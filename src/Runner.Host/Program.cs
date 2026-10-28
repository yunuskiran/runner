using OpenTelemetry.Logs;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Runner.Engine;
using Runner.Engine.Packaging;
using Runner.Host;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o => { o.IncludeScopes = true; o.TimestampFormat = "O"; });
builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeScopes = true; o.IncludeFormattedMessage = true;
    if (builder.Configuration.GetValue<bool>("Telemetry:OtlpEnabled")) o.AddOtlpExporter();
});
builder.Services.AddOptions<OperationOptions>().BindConfiguration("Operations").Validate(o =>
    o.Connections.All(c => c.Value.Provider is "Mssql" or "Postgres" && !string.IsNullOrWhiteSpace(c.Value.ConnectionString)),
    "Configured connections must specify Mssql or Postgres and a connection string").ValidateOnStart();
builder.Services.AddSingleton<DatabaseCommands>(); builder.Services.AddSingleton<ApplicationCommands>();
builder.Services.AddScoped<DatabaseJobHandler>(); builder.Services.AddScoped<ProcessJobHandler>();
builder.Services.AddScoped<Runner.Contracts.IJobHandler>(s => s.GetRequiredService<DatabaseJobHandler>());
builder.Services.AddScoped<Runner.Contracts.IJobHandler>(s => s.GetRequiredService<ProcessJobHandler>());
builder.Services.AddOptions<AlertOptions>().BindConfiguration("Alerts").Validate(o => string.IsNullOrEmpty(o.WebhookUrl) ||
    (Uri.TryCreate(o.WebhookUrl, UriKind.Absolute, out var url) && (url.Scheme == "https" ||
        (builder.Environment.IsDevelopment() && url.Scheme == "http" && url.IsLoopback))), "Webhook must be HTTPS (loopback HTTP allowed only in Development)").ValidateOnStart();
builder.Services.AddSingleton<AlertStore>();
builder.Services.AddHttpClient("runner-alerts").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).RemoveAllLoggers();
builder.Services.AddHostedService<AlertDispatcher>();
builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("runner"))
    .WithTracing(t => { t.AddSource("Runner").AddAspNetCoreInstrumentation();
        if (builder.Configuration.GetValue<bool>("Telemetry:OtlpEnabled")) t.AddOtlpExporter(); })
    .WithMetrics(m => { m.AddMeter("Runner").AddAspNetCoreInstrumentation().AddRuntimeInstrumentation();
        if (builder.Configuration.GetValue<bool>("Telemetry:OtlpEnabled")) m.AddOtlpExporter(); });
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 128 * 1024);
builder.Services.AddOptions<RunnerOptions>().BindConfiguration("Runner").Validate(o =>
    o.Concurrency is >= 1 and <= 128 && o.PollMilliseconds is >= 100 and <= 60000 &&
    o.LeaseSeconds is >= 10 and <= 3600 && o.HeartbeatSeconds >= 1 && o.HeartbeatSeconds * 3 <= o.LeaseSeconds &&
    o.JobTimeoutSeconds is >= 1 and <= 86400 && o.RetentionDays is >= 1 and <= 3650,
    "Invalid worker bounds: heartbeat must be at most one third of lease.").ValidateOnStart();
var provider = builder.Configuration["Storage:Provider"] ?? "Sqlite";
var connection = builder.Configuration["Storage:ConnectionString"] ?? throw new InvalidOperationException("Storage connection required");
if (provider is not ("Sqlite" or "Postgres")) throw new InvalidOperationException("Unsupported storage provider");
if (provider == "Sqlite") Directory.CreateDirectory("data");
builder.Services.AddDbContextFactory<RunnerDb>(o => { if (provider == "Postgres") o.UseNpgsql(connection); else o.UseSqlite(connection); });
builder.Services.Configure<ScriptingOptions>(builder.Configuration.GetSection("Scripting"));
builder.Services.AddSingleton<ScriptCompiler>();
if (builder.Configuration.GetValue<bool>("Scripting:Enabled"))
{
    builder.Services.AddScoped<ScriptFunctionHandler>();
    builder.Services.AddScoped<Runner.Contracts.IJobHandler>(s => s.GetRequiredService<ScriptFunctionHandler>());
}
builder.Services.Configure<PackageOptions>(builder.Configuration.GetSection("Packages"));
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 64 * 1024 * 1024);
builder.Services.AddSingleton<PackageManager>();
builder.Services.AddScoped<PackageJobHandler>();
builder.Services.AddScoped<Runner.Contracts.IJobHandler>(s => s.GetRequiredService<PackageJobHandler>());
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<IJobStore>(s => s.GetRequiredService<JobStore>());
PluginLoader.AddPlugins(builder.Services, builder.Configuration.GetSection("Plugins").Get<string[]>()!
    .Select(p => Path.Combine(AppContext.BaseDirectory, p)));
builder.Services.AddSingleton(sp => { using var scope = sp.CreateScope(); return new HandlerCatalog(scope.ServiceProvider.GetServices<Runner.Contracts.IJobHandler>()); });
builder.Services.AddHostedService<Worker>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "runner.session"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    o.ExpireTimeSpan = TimeSpan.FromHours(8); o.SlidingExpiration = false;
    o.Events.OnRedirectToLogin = c => { if (c.Request.Path.StartsWithSegments("/api")) c.Response.StatusCode = 401; else c.Response.Redirect("/login"); return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(o => o.Cookie.SameSite = SameSiteMode.Strict);
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("Runner");
if (builder.Configuration["Security:KeyDirectory"] is { Length: > 0 } keyDir)
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keyDir));
builder.Services.AddRateLimiter(o =>
{
    o.AddFixedWindowLimiter("login", l => { l.PermitLimit = 10; l.Window = TimeSpan.FromMinutes(1); l.QueueLimit = 0; });
    o.AddFixedWindowLimiter("package-install", l => { l.PermitLimit = 6; l.Window = TimeSpan.FromMinutes(1); l.QueueLimit = 0; });
    o.AddFixedWindowLimiter("script-registration", l => { l.PermitLimit = 10; l.Window = TimeSpan.FromMinutes(1); l.QueueLimit = 0; });
});
var app = builder.Build();
if (builder.Configuration["Security:TrustedProxy"] is { Length: > 0 } proxy)
{
    var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, ForwardLimit = 1 };
    forwarded.KnownProxies.Clear(); forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Add(IPAddress.Parse(proxy));
    app.UseForwardedHeaders(forwarded);
}
app.UseExceptionHandler();
var factory = app.Services.GetRequiredService<IDbContextFactory<RunnerDb>>();
if (args.Contains("--schema"))
{
    await using var db = await factory.CreateDbContextAsync();
    Console.WriteLine(db.Database.GenerateCreateScript()); return;
}
if (args.Contains("--upgrade-packages"))
{
    await using var db = await factory.CreateDbContextAsync();
    await SchemaUpgrade.ApplyPackagesAsync(db);
    Console.WriteLine("Schema version 4 verified: plugin package management is available."); return;
}
if (args.Contains("--upgrade-operations"))
{
    await using var db = await factory.CreateDbContextAsync();
    await SchemaUpgrade.ApplyOperationsAsync(db);
    Console.WriteLine("Schema version 3 verified: centralized operations records are available."); return;
}
if (args.Contains("--upgrade-scripting"))
{
    await using var db = await factory.CreateDbContextAsync();
    await SchemaUpgrade.ApplyFunctionsAsync(db);
    Console.WriteLine("Schema version 2 verified: scripting tables and job results are available."); return;
}
if (args.Contains("--initialize") || builder.Configuration.GetValue<bool>("Storage:Initialize"))
{
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
    if (args.Contains("--initialize")) return;
}
var apiKey = builder.Configuration["Security:ApiKey"];
if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length < 32)
    throw new InvalidOperationException("Set Security__ApiKey to a secret of at least 32 characters. No default credentials exist.");
var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
bool ValidKey(string supplied) => supplied.Length <= 1024 && CryptographicOperations.FixedTimeEquals(expectedHash, SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));
ClaimsPrincipal Principal() => new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "operator")], CookieAuthenticationDefaults.AuthenticationScheme));
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.Request.Headers.TryGetValue("X-Runner-Key", out var value))
    {
        if (!ValidKey(value.ToString())) { context.Response.StatusCode = 401; return; }
        context.User = Principal();
    }
    if (context.Request.Path.StartsWithSegments("/api") && context.Request.Method is not ("GET" or "HEAD") &&
        context.Request.Headers["X-Runner-Request"] != "1")
    { context.Response.StatusCode = 400; return; }
    await next();
});
app.UseAuthorization();
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
app.MapGet("/health/ready", async (CancellationToken ct) =>
{
    try { await using var db = await factory.CreateDbContextAsync(ct); _ = await db.Jobs.AsNoTracking().Select(x => x.ResultJson).FirstOrDefaultAsync(ct);
        _ = await db.Jobs.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.PackageId, x.PackageVersion }).FirstOrDefaultAsync(ct);
        _ = await db.PluginChannels.AsNoTracking().AnyAsync(ct); _ = await db.PluginPackages.AsNoTracking().AnyAsync(ct); _ = await db.PackageChanges.AsNoTracking().AnyAsync(ct);
        _ = await db.Functions.AsNoTracking().AnyAsync(ct);
        _ = await db.Attempts.AsNoTracking().AnyAsync(ct); _ = await db.JobLogs.AsNoTracking().AnyAsync(ct); _ = await db.Alerts.AsNoTracking().AnyAsync(ct); return Results.Ok(new { status = "ready" }); }
    catch { return Results.StatusCode(503); }
});
app.MapGet("/login", (HttpContext c, IAntiforgery af) =>
{
    var token = af.GetAndStoreTokens(c);
    return Results.Content($"""
    <!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>Runner · Sign in</title><link rel="stylesheet" href="/style.css">
    <main class="login"><div class="brand">◈ RUNNER</div><h1>Operations, under control.</h1><p>Enter the operator key configured for this deployment.</p>
    <form method="post" action="/login"><input type="hidden" name="{token.FormFieldName}" value="{System.Net.WebUtility.HtmlEncode(token.RequestToken)}"><label>Operator key<input type="password" name="key" required autocomplete="current-password" maxlength="1024"></label><button>Sign in →</button></form></main></html>
    """, "text/html");
});
app.MapPost("/login", async (HttpContext c, IAntiforgery af) =>
{
    try { await af.ValidateRequestAsync(c); } catch (AntiforgeryValidationException) { return Results.BadRequest(); }
    if (!c.Request.HasFormContentType) return Results.BadRequest();
    var form = await c.Request.ReadFormAsync();
    if (!ValidKey(form["key"].ToString())) return Results.Unauthorized();
    await c.SignInAsync(Principal()); return Results.Redirect("/");
}).RequireRateLimiting("login");
app.MapGet("/style.css", () => Results.File(Path.Combine(app.Environment.WebRootPath, "style.css"), "text/css"));
app.MapGet("/app.js", () => Results.File(Path.Combine(app.Environment.WebRootPath, "app.js"), "text/javascript")).RequireAuthorization();
app.MapGet("/", () => Results.File(Path.Combine(app.Environment.WebRootPath, "index.html"), "text/html")).RequireAuthorization();
var api = app.MapGroup("/api").RequireAuthorization();
api.MapFunctions();
api.MapOperations();
api.MapPackages();
api.MapPost("/logout", async (HttpContext c) => { await c.SignOutAsync(); return Results.NoContent(); });
api.MapGet("/handlers", (HandlerCatalog c) => c.Types.Keys.Order());
api.MapGet("/stats", async (CancellationToken ct) =>
{
    await using var db = await factory.CreateDbContextAsync(ct);
    return await db.Jobs.GroupBy(x => x.State).Select(g => new { state = g.Key, count = g.Count() }).ToListAsync(ct);
});
api.MapGet("/jobs", async (int? page, JobState? state, CancellationToken ct) =>
{
    await using var db = await factory.CreateDbContextAsync(ct);
    var query = db.Jobs.AsNoTracking(); if (state is not null) query = query.Where(x => x.State == state);
    return await query.OrderByDescending(x => x.CreatedUtc).Skip(Math.Clamp(page ?? 0, 0, 10000) * 50).Take(50)
        .Select(x => new { x.Id, x.Type, x.State, x.Attempts, x.MaxAttempts, x.CreatedUtc, x.AvailableUtc, x.FinishedUtc, x.ErrorCode, x.CancellationRequested }).ToListAsync(ct);
});
api.MapGet("/jobs/{id:guid}", async (Guid id, CancellationToken ct) =>
{
    await using var db = await factory.CreateDbContextAsync(ct);
    var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    return job is null ? Results.NotFound() : Results.Ok(job);
});
api.MapPost("/jobs", async (SubmitJob input, HandlerCatalog catalog, DatabaseCommands database, ApplicationCommands apps, PackageManager packages, CancellationToken ct) =>
{
    if (!ValidInput(input.Type, input.Payload, catalog) || !OperationEndpoints.ValidPayload(input.Type, input.Payload, database, apps) || input.MaxAttempts is < 1 or > 20 || input.IdempotencyKey?.Length > 160 ||
        input.DelaySeconds is < 0 or > 31536000 || input.IdempotencyKey?.StartsWith("schedule:", StringComparison.Ordinal) == true)
        return Results.BadRequest(new { error = "Invalid handler, payload, attempt limit, delay or idempotency key" });
    await using var db = await factory.CreateDbContextAsync(ct);
    if (!await FunctionEndpoints.ValidPayloadAsync(input.Type, input.Payload, db, ct)) return Results.BadRequest(new { error = "Invalid or disabled function version / arguments" });
    PluginRequest? plugin = null;
    try { if (input.Type == PackageJobHandler.JobType) plugin = await packages.PinAsync(input.Payload, ct); }
    catch (PackageException e) { return Results.BadRequest(new { error = e.Message }); }
    var normalizedPayload = plugin is null ? input.Payload.GetRawText() : JsonSerializer.Serialize(plugin, JsonSerializerOptions.Web);
    var key = string.IsNullOrWhiteSpace(input.IdempotencyKey) ? null : input.IdempotencyKey;
    if (key is not null && await db.Jobs.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct) is { } existing)
        return existing.Type == input.Type && existing.Payload == normalizedPayload ? Results.Ok(existing) : Results.Conflict(new { error = "Key already used with a different job" });
    var job = new Job { Type = input.Type, Payload = normalizedPayload, PackageId = plugin?.PackageId, PackageVersion = plugin?.Version, MaxAttempts = input.MaxAttempts,
        AvailableUtc = DateTime.UtcNow.AddSeconds(input.DelaySeconds), IdempotencyKey = key };
    db.Jobs.Add(job);
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateException) when (key is not null)
    {
        db.ChangeTracker.Clear();
        var winner = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);
        if (winner is null) throw;
        return winner.Type == job.Type && winner.Payload == job.Payload ? Results.Ok(winner) : Results.Conflict();
    }
    return Results.Created($"/api/jobs/{job.Id}", job);
});
api.MapPost("/jobs/{id:guid}/cancel", async (Guid id, CancellationToken ct) =>
{
    await using var db = await factory.CreateDbContextAsync(ct);
    var n = await db.Jobs.Where(x => x.Id == id && (x.State == JobState.Pending || x.State == JobState.Running))
        .ExecuteUpdateAsync(s => s.SetProperty(x => x.CancellationRequested, true), ct);
    return n == 1 ? Results.Accepted() : Results.Conflict();
});
api.MapPost("/jobs/{id:guid}/retry", async (Guid id, CancellationToken ct) =>
{
    await using var db = await factory.CreateDbContextAsync(ct);
    var n = await db.Jobs.Where(x => x.Id == id && (x.State == JobState.DeadLetter || x.State == JobState.Cancelled))
        .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, JobState.Pending).SetProperty(x => x.Attempts, 0)
            .SetProperty(x => x.AvailableUtc, DateTime.UtcNow).SetProperty(x => x.FinishedUtc, (DateTime?)null)
            .SetProperty(x => x.CancellationRequested, false).SetProperty(x => x.ResultJson, (string?)null).SetProperty(x => x.ErrorCode, (string?)null), ct);
    return n == 1 ? Results.Accepted() : Results.Conflict();
});
api.MapGet("/schedules", async (CancellationToken ct) =>
{ await using var db = await factory.CreateDbContextAsync(ct); return await db.Schedules.AsNoTracking().OrderBy(x => x.NextUtc).Take(100).ToListAsync(ct); });
api.MapPost("/schedules", async (SubmitSchedule input, HandlerCatalog catalog, DatabaseCommands database, ApplicationCommands apps, PackageManager packages, CancellationToken ct) =>
{
    if (!ValidInput(input.Type, input.Payload, catalog) || !OperationEndpoints.ValidPayload(input.Type, input.Payload, database, apps) || input.IntervalSeconds is < 10 or > 31536000) return Results.BadRequest();
    await using var db = await factory.CreateDbContextAsync(ct);
    if (!await FunctionEndpoints.ValidPayloadAsync(input.Type, input.Payload, db, ct)) return Results.BadRequest();
    PluginRequest? plugin = null;
    try { if (input.Type == PackageJobHandler.JobType) plugin = await packages.PinAsync(input.Payload, ct); }
    catch (PackageException e) { return Results.BadRequest(new { error = e.Message }); }
    var schedule = new Schedule { PackageId = plugin?.PackageId, PackageVersion = plugin?.Version, Type = input.Type, Payload = plugin is null ? input.Payload.GetRawText() : JsonSerializer.Serialize(plugin, JsonSerializerOptions.Web), IntervalSeconds = input.IntervalSeconds, NextUtc = DateTime.UtcNow.AddSeconds(input.IntervalSeconds) };
    db.Schedules.Add(schedule); await db.SaveChangesAsync(ct); return Results.Created($"/api/schedules/{schedule.Id}", schedule);
});
api.MapDelete("/schedules/{id:guid}", async (Guid id, CancellationToken ct) =>
{ await using var db = await factory.CreateDbContextAsync(ct); return await db.Schedules.Where(x => x.Id == id).ExecuteDeleteAsync(ct) == 1 ? Results.NoContent() : Results.NotFound(); });
// Resolve at startup to fail early on missing handlers or duplicate job type names.
_ = app.Services.GetRequiredService<HandlerCatalog>();
await app.RunAsync();

static bool ValidInput(string? type, JsonElement payload, HandlerCatalog catalog) => type is not null && catalog.Types.ContainsKey(type) &&
    payload.ValueKind == JsonValueKind.Object && Encoding.UTF8.GetByteCount(payload.GetRawText()) <= 65536;
public sealed record SubmitJob(string Type, JsonElement Payload, string? IdempotencyKey = null, int MaxAttempts = 5, int DelaySeconds = 0);
public sealed record SubmitSchedule(string Type, JsonElement Payload, int IntervalSeconds);
