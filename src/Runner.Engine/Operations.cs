using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Npgsql;
using Runner.Contracts;
namespace Runner.Engine;

public sealed class OperationOptions
{
    public Dictionary<string, DatabaseConnection> Connections { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, ApplicationDefinition> Applications { get; set; } = new(StringComparer.Ordinal);
}
public sealed class DatabaseConnection
{
    public string Provider { get; set; } = "Postgres";
    public string ConnectionString { get; set; } = "";
}
public sealed class ApplicationDefinition
{
    public string FileName { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string[] Arguments { get; set; } = [];
    public bool AllowAdditionalArguments { get; set; }
    public Dictionary<string, string> Environment { get; set; } = [];
}
public sealed record DatabaseRequest(string Connection, string CommandText, JsonElement Parameters = default,
    string Mode = "execute", int TimeoutSeconds = 30, int MaxRows = 100,
    Dictionary<string, string>? ParameterTypes = null, string CommandType = "Text");
public sealed record DatabaseResult(int? AffectedRows, object? Scalar, List<Dictionary<string, object?>>? Rows, bool Truncated);
public sealed record ProcessRequest(string Application, string[]? Arguments = null, int TimeoutSeconds = 300);
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool OutputTruncated);
public sealed class JobExecutionException(string code) : Exception(code) { public string Code { get; } = code; }

public sealed class DatabaseCommands(IOptions<OperationOptions> options)
{
    public bool IsValid(DatabaseRequest? request) => request is not null && request.Connection is not null &&
        options.Value.Connections.TryGetValue(request.Connection, out var connection) &&
        connection.Provider is "Postgres" or "Mssql" && !string.IsNullOrWhiteSpace(connection.ConnectionString) &&
        !string.IsNullOrWhiteSpace(request.CommandText) && Encoding.UTF8.GetByteCount(request.CommandText) <= 32768 &&
        request.Mode is "execute" or "query" or "scalar" && request.CommandType is "Text" or "StoredProcedure" &&
        request.TimeoutSeconds is >= 1 and <= 300 && request.MaxRows is >= 1 and <= 1000 &&
        request.Parameters.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object &&
        (request.Parameters.ValueKind != JsonValueKind.Object || request.Parameters.EnumerateObject().Count() <= 100);

    public static DbConnection CreateConnection(DatabaseConnection definition) => definition.Provider switch
    {
        "Postgres" => new NpgsqlConnection(definition.ConnectionString),
        "Mssql" => new SqlConnection(definition.ConnectionString),
        _ => throw new JobExecutionException("unsupported_database_provider")
    };

    // Anonymous objects make parameterized database calls convenient from Roslyn scripts.
    public Task<DatabaseResult> ExecuteAsync(string connection, string commandText, object? parameters = null,
        string mode = "execute", CancellationToken cancellationToken = default) =>
        RunAsync(new DatabaseRequest(connection, commandText, JsonSerializer.SerializeToElement(parameters), mode), cancellationToken);

    public async Task<DatabaseResult> RunAsync(DatabaseRequest request, CancellationToken ct)
    {
        if (!IsValid(request)) throw new JobExecutionException("invalid_database_operation");
        await using var connection = CreateConnection(options.Value.Connections[request.Connection]);
        await using var command = connection.CreateCommand();
        command.CommandText = request.CommandText;
        command.CommandTimeout = request.TimeoutSeconds;
        command.CommandType = Enum.Parse<CommandType>(request.CommandType);
        if (request.Parameters.ValueKind == JsonValueKind.Object)
            foreach (var parameter in request.Parameters.EnumerateObject())
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(parameter.Name, "^[A-Za-z_][A-Za-z0-9_]{0,63}$"))
                    throw new JobExecutionException("invalid_parameter_name");
                var p = command.CreateParameter();
                p.ParameterName = "@" + parameter.Name;
                p.Value = ParameterValue(parameter.Value);
                if (request.ParameterTypes?.TryGetValue(parameter.Name, out var type) == true)
                {
                    if (!Enum.TryParse<DbType>(type, false, out var dbType) || !Enum.IsDefined(dbType))
                        throw new JobExecutionException("invalid_parameter_type");
                    p.DbType = dbType;
                    p.Value = ConvertTyped(p.Value, dbType);
                }
                command.Parameters.Add(p);
            }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var budget = TimeSpan.FromSeconds(request.TimeoutSeconds);
        timeout.CancelAfter(budget);
        var elapsed = Stopwatch.StartNew();
        DatabaseResult Finish(DatabaseResult result)
        {
            ct.ThrowIfCancellationRequested();
            timeout.Token.ThrowIfCancellationRequested();
            if (elapsed.Elapsed >= budget) throw new JobExecutionException("database_timeout");
            return result;
        }
        try
        {
            await connection.OpenAsync(timeout.Token);
            if (request.Mode == "execute") return Finish(new(await command.ExecuteNonQueryAsync(timeout.Token), null, null, false));
            if (request.Mode == "scalar") return Finish(new(null, CleanValue(await command.ExecuteScalarAsync(timeout.Token)), null, false));
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, timeout.Token);
            var rows = new List<Dictionary<string, object?>>();
            var bytes = 0;
            while (await reader.ReadAsync(timeout.Token))
            {
                if (rows.Count == request.MaxRows) return Finish(new(null, null, rows, true));
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var key = reader.GetName(i);
                    if (row.ContainsKey(key)) key += "_" + i;
                    row[key] = CleanValue(reader.GetValue(i));
                }
                bytes += JsonSerializer.SerializeToUtf8Bytes(row).Length;
                if (bytes > 48000) return Finish(new(null, null, rows, true));
                rows.Add(row);
            }
            return Finish(new(null, null, rows, false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new JobExecutionException("database_timeout"); }
        catch (DbException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (DbException) when (timeout.IsCancellationRequested || elapsed.Elapsed >= budget) { throw new JobExecutionException("database_timeout"); }
        catch (SqlException e) when (e.Number == -2) { throw new JobExecutionException("database_timeout"); }
        catch (NpgsqlException e) when (e.InnerException is TimeoutException) { throw new JobExecutionException("database_timeout"); }
        catch (DbException) { throw new JobExecutionException("database_command_failed"); }
    }
    private static object ParameterValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => DBNull.Value,
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.True => true, JsonValueKind.False => false,
        JsonValueKind.Number => value.TryGetInt64(out var integer) ? (object)integer : value.GetDecimal(),
        _ => throw new JobExecutionException("parameter_must_be_scalar")
    };
    private static object ConvertTyped(object value, DbType type)
    {
        if (value == DBNull.Value) return value;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return type switch
        {
            DbType.Guid => Guid.Parse(Convert.ToString(value, culture)!),
            DbType.Date or DbType.DateTime or DbType.DateTime2 => DateTime.Parse(Convert.ToString(value, culture)!, culture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
            DbType.DateTimeOffset => DateTimeOffset.Parse(Convert.ToString(value, culture)!, culture),
            DbType.Int16 => Convert.ToInt16(value, culture), DbType.Int32 => Convert.ToInt32(value, culture),
            DbType.Int64 => Convert.ToInt64(value, culture), DbType.Decimal => Convert.ToDecimal(value, culture),
            DbType.Boolean => Convert.ToBoolean(value, culture), DbType.Binary => Convert.FromBase64String(Convert.ToString(value, culture)!),
            _ => value
        };
    }
    private static object? CleanValue(object? value) => value is null or DBNull ? null : value;
}

public sealed class ApplicationCommands(IOptions<OperationOptions> options)
{
    public bool IsValid(ProcessRequest? request) => request is not null && request.Application is not null &&
        options.Value.Applications.TryGetValue(request.Application, out var application) &&
        Path.IsPathFullyQualified(application.FileName) &&
        (string.IsNullOrEmpty(application.WorkingDirectory) || Path.IsPathFullyQualified(application.WorkingDirectory)) &&
        request.TimeoutSeconds is >= 1 and <= 86400 &&
        ((request.Arguments?.Length ?? 0) == 0 || application.AllowAdditionalArguments) &&
        (request.Arguments?.Length ?? 0) <= 32 && (request.Arguments?.All(a => a is not null && a.Length <= 4096 && !a.Contains('\0')) ?? true);

    public Task<ProcessResult> RunAsync(string application, string[]? arguments = null, CancellationToken cancellationToken = default) =>
        RunAsync(new ProcessRequest(application, arguments), cancellationToken);

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct)
    {
        if (!IsValid(request)) throw new JobExecutionException("invalid_application_operation");
        var definition = options.Value.Applications[request.Application];
        if (!File.Exists(definition.FileName) || (!string.IsNullOrEmpty(definition.WorkingDirectory) && !Directory.Exists(definition.WorkingDirectory)))
            throw new JobExecutionException("application_not_installed");
        var start = new ProcessStartInfo(definition.FileName)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = string.IsNullOrEmpty(definition.WorkingDirectory) ? Path.GetDirectoryName(definition.FileName)! : definition.WorkingDirectory };
        // Do not leak the worker's credentials into child processes.
        start.Environment.Clear();
        if (OperatingSystem.IsWindows())
            foreach (var name in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" })
                if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        foreach (var entry in definition.Environment) start.Environment[entry.Key] = entry.Value;
        foreach (var argument in definition.Arguments.Concat(request.Arguments ?? [])) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { if (!process.Start()) throw new JobExecutionException("process_start_failed"); }
        catch (System.ComponentModel.Win32Exception) { throw new JobExecutionException("process_start_failed"); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
        var output = Capture(process.StandardOutput, timeout.Token);
        var error = Capture(process.StandardError, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await output; var stderr = await error;
            return new(process.ExitCode, stdout.Text, stderr.Text, stdout.Truncated || stderr.Truncated);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(cleanup.Token); } catch (OperationCanceledException) { }
            try { await Task.WhenAll(output, error); } catch (OperationCanceledException) { }
            if (ct.IsCancellationRequested) throw;
            throw new JobExecutionException("process_timeout");
        }
    }
    public async Task<ProcessResult> ExecuteAsync(ProcessRequest request, JobContext context, CancellationToken ct)
    {
        var result = await RunAsync(request, ct);
        if (context.WriteLogAsync is { } log)
        {
            if (result.StandardOutput.Length != 0) await log("Information", result.StandardOutput, ct);
            if (result.StandardError.Length != 0) await log("Warning", result.StandardError, ct);
        }
        if (result.ExitCode != 0) throw new JobExecutionException("process_exit_" + result.ExitCode);
        return result;
    }
    private static async Task<(string Text, bool Truncated)> Capture(StreamReader reader, CancellationToken ct)
    {
        var text = new StringBuilder(); var buffer = new char[2048]; bool truncated = false; int size;
        while ((size = await reader.ReadAsync(buffer.AsMemory(), ct)) != 0)
        {
            int take = Math.Min(size, 8192 - text.Length); if (take < size) truncated = true;
            text.Append(buffer, 0, take);
        }
        return (text.ToString(), truncated);
    }
}

public sealed class DatabaseJobHandler(DatabaseCommands commands) : IJobHandler
{
    public const string JobType = "runner.database";
    public string Type => JobType;
    public async Task ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var request = JsonSerializer.Deserialize<DatabaseRequest>(context.Payload, JsonSerializerOptions.Web)
            ?? throw new JobExecutionException("invalid_database_operation");
        context.ReportResult?.Invoke(JsonSerializer.Serialize(await commands.RunAsync(request, ct)));
    }
}
public sealed class ProcessJobHandler(ApplicationCommands commands) : IJobHandler
{
    public const string JobType = "runner.process";
    public string Type => JobType;
    public async Task ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var request = JsonSerializer.Deserialize<ProcessRequest>(context.Payload, JsonSerializerOptions.Web)
            ?? throw new JobExecutionException("invalid_application_operation");
        var result = await commands.ExecuteAsync(request, context, ct);
        context.ReportResult?.Invoke(JsonSerializer.Serialize(result));
    }
}

public sealed class FunctionApplications(ApplicationCommands commands, JobContext context)
{
    public Task<ProcessResult> RunAsync(string application, string[]? arguments = null, CancellationToken cancellationToken = default) =>
        commands.ExecuteAsync(new ProcessRequest(application, arguments), context, cancellationToken);
}
