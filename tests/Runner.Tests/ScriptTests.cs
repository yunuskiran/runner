using System.Text.Json;
using Microsoft.Extensions.Options;
using Runner.Contracts;
using Runner.Engine;
using Xunit;
namespace Runner.Tests;

public sealed class ScriptTests
{
    [Fact] public void Compiler_returns_line_diagnostics()
    {
        using var compiler = new ScriptCompiler();
        var error = Assert.Throws<ScriptValidationException>(() => compiler.Compile("var x = 1;\nreturn missing;", default));
        Assert.Contains(error.Diagnostics, d => d.Line == 2 && d.Id == "CS0103");
    }
    [Theory]
    [InlineData("#r \"System.IO\"\nreturn 1;")]
    [InlineData("#load \"anything.csx\"\nreturn 1;")]
    public void Compiler_rejects_external_directives(string source)
    {
        using var compiler = new ScriptCompiler();
        var error = Assert.Throws<ScriptValidationException>(() => compiler.Compile(source, default));
        Assert.Contains(error.Diagnostics, d => d.Id == "DIRECTIVE_NOT_ALLOWED");
    }
    [Fact] public void Compiler_bounds_source_size()
    {
        using var compiler = new ScriptCompiler();
        Assert.Throws<ScriptValidationException>(() => compiler.Compile(new string('x', 32769), default));
    }
    [Fact] public async Task Script_cooperatively_observes_cancellation()
    {
        using var compiler = new ScriptCompiler();
        var runner = compiler.Compile("await Task.Delay(30000, CancellationToken); return 1;", default);
        using var ct = new CancellationTokenSource(100);
        using var args = JsonDocument.Parse("{}");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner(new ScriptGlobals(Guid.NewGuid(), 1, args.RootElement, ct.Token), ct.Token));
    }
    [Fact] public async Task Disabled_worker_rejects_execution_before_database_access()
    {
        using var compiler = new ScriptCompiler();
        var handler = new ScriptFunctionHandler(null!, compiler, Options.Create(new ScriptingOptions()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.ExecuteAsync(new JobContext(Guid.NewGuid(), "{}", 1), default));
    }    [Fact] public async Task Apps_helper_uses_shared_process_failure_and_logging_policy()
    {
        using var compiler = new ScriptCompiler();
        var applications = new ApplicationCommands(Options.Create(new OperationOptions
        { Applications = new() { ["test"] = new() { FileName = "/usr/bin/python3", Arguments = ["-c", "import sys;print('script app output');sys.exit(9)"] } } }));
        var messages = new List<string>();
        var context = new JobContext(Guid.NewGuid(), "{}", 1)
        { WriteLogAsync = (_, message, _) => { messages.Add(message); return Task.CompletedTask; } };
        using var arguments = JsonDocument.Parse("{}");
        var runner = compiler.Compile("return await Apps.RunAsync(\"test\", cancellationToken: CancellationToken);", default);
        var globals = new ScriptGlobals(context.JobId, 1, arguments.RootElement, default) { Apps = new FunctionApplications(applications, context) };
        var error = await Assert.ThrowsAsync<JobExecutionException>(() => runner(globals));
        Assert.Equal("process_exit_9", error.Code); Assert.Contains(messages, x => x.Contains("script app output", StringComparison.Ordinal));
    }

}
