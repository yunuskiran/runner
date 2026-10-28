using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
namespace Runner.Engine;

public sealed class DatabaseMetrics : DbCommandInterceptor
{
    private long readers, writes, scalars;
    public object Snapshot() => new { readers = Interlocked.Read(ref readers), writes = Interlocked.Read(ref writes), scalars = Interlocked.Read(ref scalars) };
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    { Interlocked.Increment(ref readers); return result; }
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    { Interlocked.Increment(ref readers); return ValueTask.FromResult(result); }
    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    { Interlocked.Increment(ref writes); return result; }
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { Interlocked.Increment(ref writes); return ValueTask.FromResult(result); }
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    { Interlocked.Increment(ref scalars); return ValueTask.FromResult(result); }
}
