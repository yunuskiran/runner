using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Runner.Engine;

namespace Runner.Host;

public sealed class RunnerSecurity
{
    private readonly List<(string Name, string Role, byte[] Hash)> keys = [];
    public string SessionVersion { get; }
    public bool OidcEnabled { get; }
    public RunnerSecurity(IConfiguration configuration)
    {
        SessionVersion = configuration["Security:SessionVersion"] ?? "1";
        OidcEnabled = !string.IsNullOrWhiteSpace(configuration["Security:Oidc:Authority"]);
        if (configuration["Security:ApiKey"] is { Length: > 0 } bootstrap) Add("bootstrap-admin", "Admin", bootstrap);
        foreach (var key in configuration.GetSection("Security:ApiKeys").GetChildren())
            Add(key.Key, key["Role"] ?? "Viewer", key["Key"] ?? "");
        if (keys.Count == 0 && !OidcEnabled) throw new InvalidOperationException("Configure OIDC or an API key with at least 32 characters");
    }
    private void Add(string name, string role, string key)
    {
        if (key.Length < 32 || role is not ("Admin" or "Operator" or "Viewer") || name.Length > 120)
            throw new InvalidOperationException("API keys require a name, 32+ characters and Admin/Operator/Viewer role");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        if (keys.Any(x => x.Name == name || CryptographicOperations.FixedTimeEquals(x.Hash, hash))) throw new InvalidOperationException("API key values must be unique");
        keys.Add((name, role, hash));
    }
    public ClaimsPrincipal? Authenticate(string supplied)
    {
        if (supplied.Length > 1024) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        foreach (var key in keys)
            if (CryptographicOperations.FixedTimeEquals(key.Hash, hash)) return CreatePrincipal(key.Name, key.Role, "ApiKey");
        return null;
    }
    public ClaimsPrincipal CreatePrincipal(string actor, string role, string source)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, actor), new(ClaimTypes.NameIdentifier, actor),
            new("runner:session-version", SessionVersion), new("runner:source", source) };
        if (role == "Admin") claims.Add(new(ClaimTypes.Role, "Admin"));
        if (role is "Admin" or "Operator") claims.Add(new(ClaimTypes.Role, "Operator"));
        if (role is "Admin" or "Operator" or "Viewer") claims.Add(new(ClaimTypes.Role, "Viewer"));
        return new(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    public static void Configure(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<RunnerSecurity>();
        var auth = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
        {
            o.Cookie.Name = "runner.session"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            o.ExpireTimeSpan = TimeSpan.FromHours(8); o.SlidingExpiration = false;
            o.Events.OnRedirectToLogin = c => { if (c.Request.Path.StartsWithSegments("/api")) c.Response.StatusCode = 401; else c.Response.Redirect("/login"); return Task.CompletedTask; };
            o.Events.OnValidatePrincipal = async c =>
            {
                if (c.Principal?.FindFirst("runner:session-version")?.Value != c.HttpContext.RequestServices.GetRequiredService<RunnerSecurity>().SessionVersion)
                { c.RejectPrincipal(); await c.HttpContext.SignOutAsync(); }
            };
        });
        var config = builder.Configuration.GetSection("Security:Oidc");
        if (!string.IsNullOrWhiteSpace(config["Authority"])) auth.AddOpenIdConnect(o =>
        {
            o.Authority = config["Authority"]; o.ClientId = config["ClientId"] ?? throw new InvalidOperationException("OIDC ClientId required");
            o.ClientSecret = config["ClientSecret"]; o.ResponseType = "code"; o.ResponseMode = "query"; o.UsePkce = true;
            if (builder.Environment.IsDevelopment())
            {
                o.CorrelationCookie.SameSite = SameSiteMode.Lax; o.NonceCookie.SameSite = SameSiteMode.Lax;
                o.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            }
            o.RequireHttpsMetadata = !builder.Environment.IsDevelopment(); o.SaveTokens = false; o.MapInboundClaims = false;
            o.TokenValidationParameters.NameClaimType = "name";
            o.Events.OnTokenValidated = c =>
            {
                var roleClaim = config["RoleClaimType"] ?? "roles";
                var roles = c.Principal!.FindAll(roleClaim).Select(x => x.Value).ToHashSet(StringComparer.Ordinal);
                var role = roles.Contains(config["AdminRole"] ?? "runner-admin") ? "Admin" :
                    roles.Contains(config["OperatorRole"] ?? "runner-operator") ? "Operator" :
                    roles.Contains(config["ViewerRole"] ?? "runner-viewer") ? "Viewer" : "Denied";
                var subject = c.Principal.FindFirst("sub")?.Value ?? throw new InvalidOperationException("OIDC subject required");
                var issuer = c.SecurityToken.Issuer;
                c.Principal = c.HttpContext.RequestServices.GetRequiredService<RunnerSecurity>().CreatePrincipal(issuer + "|" + subject, role, "OIDC");
                return Task.CompletedTask;
            };
        });
        builder.Services.AddAuthorization();
    }

    public static async Task EnforceAndAuditAsync(HttpContext context, RequestDelegate next)
    {
        if (!context.Request.Path.StartsWithSegments("/api")) { await next(context); return; }
        if (context.User.Identity?.IsAuthenticated != true) { context.Response.StatusCode = 401; return; }
        var path = context.Request.Path.Value ?? "";
        var write = context.Request.Method is not ("GET" or "HEAD");
        var role = path.StartsWith("/api/audit", StringComparison.Ordinal) || (write && new[] { "/api/packages", "/api/functions", "/api/nodes" }
            .Any(x => path.StartsWith(x, StringComparison.Ordinal))) ? "Admin" : write && path != "/api/logout" ? "Operator" : "Viewer";
        var allowed = path == "/api/logout" || context.User.IsInRole(role);
        if (!write && allowed) { await next(context); return; }
        var factory = context.RequestServices.GetRequiredService<IDbContextFactory<RunnerDb>>();
        await using var db = await factory.CreateDbContextAsync(context.RequestAborted);
        var clock = context.RequestServices.GetRequiredService<ClusterClock>();
        var record = new AuditRecord { StartedUtc = clock.UtcNow, Actor = context.User.Identity.Name?[..Math.Min(240, context.User.Identity.Name.Length)] ?? "unknown",
            AuthenticationType = context.User.FindFirst("runner:source")?.Value ?? "unknown", Method = context.Request.Method,
            Path = path[..Math.Min(path.Length, 512)], TraceId = context.TraceIdentifier };
        db.AuditRecords.Add(record);
        // Persist intent before changing state. On a crash, an incomplete record remains for investigation.
        await db.SaveChangesAsync(context.RequestAborted);
        int status = 500;
        try
        {
            if (!allowed) { context.Response.StatusCode = 403; status = 403; return; }
            await next(context); status = context.Response.StatusCode;
        }
        finally
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var details = System.Text.Json.JsonSerializer.Serialize(new {
                resource = context.Response.Headers.Location.ToString().StartsWith("/api/", StringComparison.Ordinal) ? context.Response.Headers.Location.ToString().Split('?')[0] : null,
                change = context.Items.TryGetValue("RunnerAudit", out var safeDetails) ? safeDetails : null });
            if (details.Length > 4096) details = "{}";
            await db.AuditRecords.Where(x => x.Id == record.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CompletedUtc, clock.UtcNow)
                .SetProperty(x => x.DetailsJson, details).SetProperty(x => x.StatusCode, status), deadline.Token);
            if (allowed && status < 400) context.RequestServices.GetRequiredService<QueueSignals>().Pulse();
        }
    }
}
