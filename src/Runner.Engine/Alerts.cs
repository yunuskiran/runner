using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
namespace Runner.Engine;

public sealed class AlertOptions
{
    public string? WebhookUrl { get; set; }
    public string? SigningKey { get; set; }
    public bool DeliveryEnabled { get; set; } = true;
}
public sealed class AlertStore(IDbContextFactory<RunnerDb> factory)
{
    public async Task<JobAlert?> ClaimAsync(DateTime now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Alerts.Where(x => x.DeliveryState == "Sending" && x.DeliveryLeaseUtc <= now && x.DeliveryAttempts >= 8)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.DeliveryState, "Parked").SetProperty(x => x.DeliveryToken, (Guid?)null).SetProperty(x => x.DeliveryLeaseUtc, (DateTime?)null), ct);
        var ids = await db.Alerts.AsNoTracking().Where(x => x.DeliveryAttempts < 8 &&
            ((x.DeliveryState == "Pending" && x.NextDeliveryUtc <= now) || (x.DeliveryState == "Sending" && x.DeliveryLeaseUtc <= now)))
            .OrderBy(x => x.NextDeliveryUtc).Select(x => x.Id).Take(16).ToListAsync(ct);
        foreach (var id in ids)
        {
            var token = Guid.NewGuid();
            var count = await db.Alerts.Where(x => x.Id == id && x.DeliveryAttempts < 8 &&
                ((x.DeliveryState == "Pending" && x.NextDeliveryUtc <= now) || (x.DeliveryState == "Sending" && x.DeliveryLeaseUtc <= now)))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.DeliveryState, "Sending").SetProperty(x => x.DeliveryToken, token)
                    .SetProperty(x => x.DeliveryLeaseUtc, now.AddSeconds(60)).SetProperty(x => x.DeliveryAttempts, x => x.DeliveryAttempts + 1), ct);
            if (count == 1) return await db.Alerts.AsNoTracking().SingleAsync(x => x.Id == id, ct);
        }
        return null;
    }
    public async Task<bool> FinishAsync(JobAlert alert, bool success, DateTime now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Alerts.Where(x => x.Id == alert.Id && x.DeliveryState == "Sending" && x.DeliveryToken == alert.DeliveryToken && x.DeliveryLeaseUtc > now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.DeliveryState, success ? "Delivered" : alert.DeliveryAttempts >= 8 ? "Parked" : "Pending")
                .SetProperty(x => x.NextDeliveryUtc, now.AddSeconds(Math.Min(3600, Math.Pow(2, alert.DeliveryAttempts) * 5)))
                .SetProperty(x => x.DeliveryToken, (Guid?)null).SetProperty(x => x.DeliveryLeaseUtc, (DateTime?)null), ct) == 1;
    }
}

public sealed class AlertDispatcher(AlertStore store, IHttpClientFactory clients, IOptions<AlertOptions> options,
    ILogger<AlertDispatcher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.DeliveryEnabled || string.IsNullOrWhiteSpace(options.Value.WebhookUrl)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var alert = await store.ClaimAsync(DateTime.UtcNow, stoppingToken);
                if (alert is not null)
                {
                    bool success = false;
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        timeout.CancelAfter(TimeSpan.FromSeconds(10));
                        using var request = BuildRequest(alert, options.Value);
                        using var response = await clients.CreateClient("runner-alerts").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                        success = response.IsSuccessStatusCode;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                    catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
                    { log.LogWarning("Alert transport failed for {AlertId}; delivery will retry", alert.Id); }
                    await store.FinishAsync(alert, success, DateTime.UtcNow, stoppingToken);
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { log.LogError("Alert dispatch cycle failed: {ErrorType}", e.GetType().Name); }
            try { await Task.Delay(1000, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
    public static HttpRequestMessage BuildRequest(JobAlert alert, AlertOptions options)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { alert.Id, alert.JobId, alert.JobType, alert.ErrorCode, alert.CreatedUtc }, JsonSerializerOptions.Web);
        var request = new HttpRequestMessage(HttpMethod.Post, options.WebhookUrl);
        request.Content = new ByteArrayContent(bytes); request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("X-Runner-Alert-Id", alert.Id.ToString());
        if (!string.IsNullOrEmpty(options.SigningKey))
            request.Headers.Add("X-Runner-Signature", Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.SigningKey), bytes)));
        return request;
    }
}
