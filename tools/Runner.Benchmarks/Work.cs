using System.Globalization;
using Microsoft.Data.SqlClient;
using Runner.Contracts;
using Quartz;
namespace Runner.Benchmarks;

public static class Work
{
    public static string Connection { get; set; } = "";
    public static string Node { get; set; } = "";
    public static async Task Execute(WorkItem item, CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        await Task.Delay(item.DelayMs, ct);
        var finished = DateTime.UtcNow;
        await using var db = new SqlConnection(Connection); await db.OpenAsync(ct);
        await using var command = new SqlCommand("""
            UPDATE dbo.Bodies WITH (UPDLOCK, SERIALIZABLE) SET Executions=Executions+1 WHERE Id=@id;
            IF @@ROWCOUNT=0 INSERT dbo.Bodies(Id,EnqueuedUtc,StartedUtc,FinishedUtc,Node,Executions)
              VALUES(@id,@enqueued,@started,@finished,@node,1);
            """, db);
        command.Parameters.AddWithValue("@id", item.Id); command.Parameters.AddWithValue("@enqueued", new DateTime(item.EnqueuedTicks, DateTimeKind.Utc));
        command.Parameters.AddWithValue("@started", started); command.Parameters.AddWithValue("@finished", finished); command.Parameters.AddWithValue("@node", Node);
        await command.ExecuteNonQueryAsync(ct);
    }
}
public sealed class RunnerJob : IJobHandler
{
    public string Type => "benchmark";
    public Task ExecuteAsync(JobContext context, CancellationToken ct) => Work.Execute(System.Text.Json.JsonSerializer.Deserialize<WorkItem>(context.Payload)!, ct);
}
public sealed class HangfireJob
{
    [Hangfire.AutomaticRetry(Attempts = 0)]
    public Task Execute(string id, long ticks, int delay) => Work.Execute(new(id, ticks, delay));
}
public sealed class QuartzJob : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken ct)
    {
        var map = context.MergedJobDataMap;
        await Work.Execute(new(map.GetString("id")!, long.Parse(map.GetString("ticks")!, CultureInfo.InvariantCulture),
            int.Parse(map.GetString("delay")!, CultureInfo.InvariantCulture)), ct);
    }
}
