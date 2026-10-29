using System.Diagnostics;
using Microsoft.Data.SqlClient;
namespace Runner.Benchmarks;

public sealed record Settings(int Jobs = 200, int Nodes = 2, int Workers = 8, int DelayMs = 10, int WarmupJobs = 20,
    int SettleSeconds = 35, int IdleSeconds = 30, int Repetitions = 3, string Profiles = "defaults,responsive", string Engines = "runner,hangfire,quartz", bool Smoke = false);
public sealed record NodeConfig(string Engine, string Connection, string ResultsConnection, string Name, int Workers, string Profile);
public sealed record WorkItem(string Id, long EnqueuedTicks, int DelayMs);
public sealed record Observation(long Rpc, long Batches, long SqlStatements, long SpStatements, long FileReadBytes, long FileWrittenBytes,
    int Connections, int ActiveRequests);
public sealed record Window(double Seconds, long CompletedRequests, long SqlStatementEvents, long SpStatementEvents, long PhysicalReadBytes,
    long PhysicalWrittenBytes, int EndConnections, int EndActiveRequests, double WorkerCpuMs, long EndWorkerWorkingSetBytes)
{
    public double CompletedRequestsPerSecond => CompletedRequests / Seconds;
    public static Window Between(Observation a, Observation b, double seconds, double cpu, long memory) => new(seconds,
        b.Rpc + b.Batches - a.Rpc - a.Batches, b.SqlStatements - a.SqlStatements, b.SpStatements - a.SpStatements,
        b.FileReadBytes - a.FileReadBytes, b.FileWrittenBytes - a.FileWrittenBytes, b.Connections, b.ActiveRequests, cpu, memory);
}
public sealed record CaseResult(string Engine, string Profile, int Repetition, Dictionary<string, object> EffectiveSettings,
    Window Idle, Window Load, double EnqueueSeconds, double JobsPerSecond, double BusinessLatencyP50Ms, double BusinessLatencyP95Ms,
    double BusinessLatencyP99Ms, int Jobs, int DistinctExecutingNodes, int DuplicateBodies);
public static class Config
{
    public static string WithDatabase(string connection, string database, string application) => new SqlConnectionStringBuilder(connection)
        { InitialCatalog = database, ApplicationName = application }.ConnectionString;
    public static async Task Execute(string connection, string sql)
    {
        await using var db = new SqlConnection(connection); await db.OpenAsync();
        await using var command = new SqlCommand(sql, db) { CommandTimeout = 120 }; await command.ExecuteNonQueryAsync();
    }
}
