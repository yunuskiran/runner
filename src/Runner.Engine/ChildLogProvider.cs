using Microsoft.Extensions.Logging;
using Runner.Contracts;
namespace Runner.Engine;

/// <summary>Forward explicit plugin ILogger messages through the same bounded parent journal protocol.</summary>
public sealed class ChildLogProvider(JobContext context) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ChildLogger(context, categoryName);
    public void Dispose() { }
    private sealed class ChildLogger(JobContext context, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level is >= LogLevel.Information and < LogLevel.None;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level) || context.WriteLogAsync is null) return;
            var severity = level >= LogLevel.Error ? "Error" : level >= LogLevel.Warning ? "Warning" : "Information";
            context.WriteLogAsync(severity, category + ": " + formatter(state, exception), default).GetAwaiter().GetResult();
        }
    }
}
