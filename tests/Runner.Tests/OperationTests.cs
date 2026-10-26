using System.Data;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Runner.Contracts;
using Runner.Engine;
using Xunit;
namespace Runner.Tests;

public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute(string name) { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))) Skip = $"Set {name} to test a live database"; }
}
public sealed class OperationTests
{
    private static readonly OperationOptions Config = new();
    [Theory]
    [InlineData("Postgres", typeof(Npgsql.NpgsqlConnection))]
    [InlineData("Mssql", typeof(Microsoft.Data.SqlClient.SqlConnection))]
    public void Named_connection_selects_provider(string provider, Type expected)
    {
        using var connection = DatabaseCommands.CreateConnection(new DatabaseConnection { Provider = provider });
        Assert.IsType(expected, connection);
    }
    [DatabaseFact("TEST_DATABASE_POSTGRES")]
    public Task PostgreSQL_parameterized_commands() => DatabaseChecks("Postgres", "TEST_DATABASE_POSTGRES");
    [DatabaseFact("TEST_DATABASE_MSSQL")]
    public Task MSSQL_parameterized_commands() => DatabaseChecks("Mssql", "TEST_DATABASE_MSSQL");
    private static async Task DatabaseChecks(string provider, string variable)
    {
        var options = new OperationOptions { Connections = new() { ["test"] = new() { Provider = provider, ConnectionString = Environment.GetEnvironmentVariable(variable)! } } };
        var db = new DatabaseCommands(Options.Create(options));
        var arguments = JsonSerializer.SerializeToElement(new { value = 42, name = "x'; DROP TABLE imaginary;--" });
        var result = await db.RunAsync(new DatabaseRequest("test", "SELECT CAST(@value AS integer) AS value, CAST(@name AS varchar(100)) AS name", arguments, "query", ParameterTypes: new() { ["value"] = "Int32" }), default);
        Assert.Equal(42, Convert.ToInt32(Assert.Single(result.Rows!)["value"]));
        Assert.Equal("x'; DROP TABLE imaginary;--", result.Rows![0]["name"]);
        var scalar = await db.RunAsync(new DatabaseRequest("test", "SELECT CAST(NULL AS integer)", Mode: "scalar"), default);
        Assert.Null(scalar.Scalar);
        var rows = provider == "Postgres" ? "SELECT generate_series(1,3) AS value" : "SELECT value FROM (VALUES (1),(2),(3)) v(value)";
        result = await db.RunAsync(new DatabaseRequest("test", rows, Mode: "query", MaxRows: 2), default);
        Assert.True(result.Truncated); Assert.Equal(2, result.Rows!.Count);
        var execute = provider == "Postgres" ? "CREATE TEMP TABLE runner_execute (value int); INSERT INTO runner_execute VALUES (1)" : "CREATE TABLE #runner_execute (value int); INSERT INTO #runner_execute VALUES (1)";
        result = await db.RunAsync(new DatabaseRequest("test", execute), default); Assert.Equal(1, result.AffectedRows);
        var wait = provider == "Postgres" ? "SELECT pg_sleep(5)" : "WAITFOR DELAY '00:00:05'";
        await Assert.ThrowsAsync<JobExecutionException>(() => db.RunAsync(new DatabaseRequest("test", wait, TimeoutSeconds: 1), default));
    }
    [Fact] public async Task Unknown_connection_is_rejected_without_exposing_secrets()
    {
        var db = new DatabaseCommands(Options.Create(Config));
        var e = await Assert.ThrowsAsync<JobExecutionException>(() => db.RunAsync(new("missing", "SELECT 1"), default));
        Assert.Equal("invalid_database_operation", e.Code);
    }
    private static ApplicationCommands Apps(string executable, string[]? arguments = null, bool extra = false) => new(Options.Create(new OperationOptions
    { Applications = new() { ["test"] = new() { FileName = executable, Arguments = arguments ?? [], AllowAdditionalArguments = extra } } }));
    [Fact] public async Task Process_passes_arguments_without_shell_expansion()
    {
        var result = await Apps("/usr/bin/printf", ["%s"], true).RunAsync(new ProcessRequest("test", ["$(not-a-command); & | > literal"]), default);
        Assert.Equal(0, result.ExitCode); Assert.Equal("$(not-a-command); & | > literal", result.StandardOutput);
    }
    [Fact] public async Task Process_bounds_and_drains_both_output_streams()
    {
        var app = Apps("/usr/bin/python3", ["-c", "import sys;print('x'*20000);sys.stderr.write('e'*20000)"]);
        var result = await app.RunAsync(new ProcessRequest("test"), default);
        Assert.True(result.OutputTruncated); Assert.Equal(8192, result.StandardOutput.Length); Assert.Equal(8192, result.StandardError.Length);
    }
    [Fact] public async Task Process_does_not_inherit_worker_credentials()
    {
        var result = await Apps("/usr/bin/env").RunAsync(new ProcessRequest("test"), default);
        Assert.Equal("", result.StandardOutput);
    }
    [Fact] public async Task Process_timeout_terminates_execution()
    {
        var e = await Assert.ThrowsAsync<JobExecutionException>(() => Apps("/usr/bin/sleep", ["30"]).RunAsync(new ProcessRequest("test", TimeoutSeconds: 1), default));
        Assert.Equal("process_timeout", e.Code);
    }
    [Fact] public async Task Process_cancellation_is_observed()
    {
        using var ct = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Apps("/usr/bin/sleep", ["30"]).RunAsync(new ProcessRequest("test"), ct.Token));
    }
    [Fact] public async Task Process_nonzero_exit_is_centralized_failure()
    {
        var log = new List<string>();
        var handler = new ProcessJobHandler(Apps("/usr/bin/python3", ["-c", "import sys;print('business output');sys.exit(7)"]));
        var context = new JobContext(Guid.NewGuid(), "{\"application\":\"test\"}", 1)
        { WriteLogAsync = (_, message, _) => { log.Add(message); return Task.CompletedTask; } };
        var e = await Assert.ThrowsAsync<JobExecutionException>(() => handler.ExecuteAsync(context, default));
        Assert.Equal("process_exit_7", e.Code); Assert.Contains(log, x => x.Contains("business output", StringComparison.Ordinal));
    }
    [Fact] public void Unconfigured_paths_and_unapproved_arguments_are_rejected()
    {
        var apps = Apps("/usr/bin/printf"); Assert.False(apps.IsValid(new("/bin/sh"))); Assert.False(apps.IsValid(new("test", ["unapproved"])));
    }
    [Fact] public async Task Alert_signature_covers_the_exact_payload()
    {
        var alert = new JobAlert { Id = Guid.NewGuid(), JobId = Guid.NewGuid(), JobType = "test", ErrorCode = "failed", CreatedUtc = DateTime.UtcNow };
        using var request = AlertDispatcher.BuildRequest(alert, new AlertOptions { WebhookUrl = "https://alerts.example.test", SigningKey = "test-only" });
        var bytes = await request.Content!.ReadAsByteArrayAsync();
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes("test-only"), bytes));
        Assert.Equal(expected, Assert.Single(request.Headers.GetValues("X-Runner-Signature")));
        Assert.DoesNotContain("DeliveryToken", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }
}
