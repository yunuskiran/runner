using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Runner.Engine;
namespace Runner.Host;

public static class OperationEndpoints
{
    public static void MapOperations(this RouteGroupBuilder api)
    {
        api.MapGet("/resources", (IOptions<OperationOptions> operations, IOptions<AlertOptions> alerts) => new
        {
            connections = operations.Value.Connections.Select(x => new { name = x.Key, provider = x.Value.Provider }),
            applications = operations.Value.Applications.Select(x => new { name = x.Key, acceptsArguments = x.Value.AllowAdditionalArguments }),
            webhookConfigured = !string.IsNullOrWhiteSpace(alerts.Value.WebhookUrl)
        });
        api.MapGet("/jobs/{id:guid}/logs", async (Guid id, int? page, IDbContextFactory<RunnerDb> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return await db.JobLogs.AsNoTracking().Where(x => x.JobId == id).OrderBy(x => x.TimestampUtc).ThenBy(x => x.Id)
                .Skip(Math.Clamp(page ?? 0, 0, 10000) * 100).Take(100).ToListAsync(ct);
        });
        api.MapGet("/jobs/{id:guid}/attempts", async (Guid id, IDbContextFactory<RunnerDb> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return await db.Attempts.AsNoTracking().Where(x => x.JobId == id).OrderByDescending(x => x.StartedUtc).Take(100).ToListAsync(ct);
        });
        api.MapGet("/reports/summary", async (int? hours, IDbContextFactory<RunnerDb> factory, CancellationToken ct) => await Summary(factory, hours, ct));
        api.MapGet("/reports/summary.csv", async (int? hours, IDbContextFactory<RunnerDb> factory, CancellationToken ct) =>
        {
            var rows = await Summary(factory, hours, ct);
            var text = new StringBuilder("JobType,Outcome,Count,AverageDurationMs\r\n");
            foreach (var row in rows) text.AppendLine($"{Csv(row.JobType)},{Csv(row.Outcome)},{row.Count},{row.AverageDurationMs.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            return Results.File(Encoding.UTF8.GetBytes(text.ToString()), "text/csv", "runner-report.csv");
        });
        api.MapGet("/alerts", async (int? page, IDbContextFactory<RunnerDb> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return await db.Alerts.AsNoTracking().OrderByDescending(x => x.CreatedUtc).Skip(Math.Clamp(page ?? 0, 0, 10000) * 50).Take(50)
                .Select(x => new { x.Id, x.JobId, x.JobType, x.ErrorCode, x.CreatedUtc, x.Acknowledged, x.DeliveryState, x.DeliveryAttempts }).ToListAsync(ct);
        });
        api.MapPost("/alerts/{id:guid}/acknowledge", async (Guid id, IDbContextFactory<RunnerDb> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return await db.Alerts.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Acknowledged, true), ct) == 1 ? Results.NoContent() : Results.NotFound();
        });
        api.MapPost("/alerts/{id:guid}/redeliver", async (Guid id, IDbContextFactory<RunnerDb> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return await db.Alerts.Where(x => x.Id == id && x.DeliveryState == "Parked")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.DeliveryState, "Pending").SetProperty(x => x.DeliveryAttempts, 0)
                    .SetProperty(x => x.NextDeliveryUtc, DateTime.UtcNow), ct) == 1 ? Results.Accepted() : Results.Conflict();
        });
    }
    private static string Csv(string value)
    {
        if (value.Length > 0 && "=+-@\t\r\n".Contains(value[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
    private static async Task<List<ExecutionSummary>> Summary(IDbContextFactory<RunnerDb> factory, int? hours, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddHours(-Math.Clamp(hours ?? 24, 1, 720));
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Attempts.AsNoTracking().Where(x => x.StartedUtc >= since).GroupBy(x => new { x.JobType, x.Outcome })
            .OrderBy(g => g.Key.JobType).ThenBy(g => g.Key.Outcome)
            .Select(g => new ExecutionSummary(g.Key.JobType, g.Key.Outcome, g.Count(), g.Average(x => x.DurationMilliseconds) ?? 0)).ToListAsync(ct);
    }
    public static bool ValidPayload(string type, JsonElement payload, DatabaseCommands database, ApplicationCommands apps)
    {
        try
        {
            return type switch
            {
                DatabaseJobHandler.JobType => database.IsValid(payload.Deserialize<DatabaseRequest>(JsonSerializerOptions.Web)),
                ProcessJobHandler.JobType => apps.IsValid(payload.Deserialize<ProcessRequest>(JsonSerializerOptions.Web)),
                _ => true
            };
        }
        catch (JsonException) { return false; }
    }
}
public sealed record ExecutionSummary(string JobType, string Outcome, int Count, double AverageDurationMs);
