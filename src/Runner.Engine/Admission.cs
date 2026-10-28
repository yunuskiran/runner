using Microsoft.EntityFrameworkCore;
namespace Runner.Engine;

public static class Admission
{
    // Call inside the enqueue transaction, before channel/job writes. Covers all API replicas.
    public static Task<int> LockAsync(RunnerDb db, CancellationToken ct) => db.ClusterLeases.Where(x => x.Name == "admission")
        .ExecuteUpdateAsync(s => s.SetProperty(x => x.UntilUtc, x => x.UntilUtc), ct);

    public static async Task<bool> HasCapacityAsync(RunnerDb db, int incoming, int limit, CancellationToken ct) =>
        await db.Jobs.Where(x => x.State == JobState.Pending || x.State == JobState.Running || x.State == JobState.Blocked)
            .CountAsync(ct) <= limit - incoming;

    public static bool ValidQueue(string? queue) => queue is not null && queue.Length is > 0 and <= 64 && queue.All(x => char.IsAsciiLetterOrDigit(x) || x is '.' or '-' or '_');
    public static void SetOrder(Job job) => job.PriorityUtc = job.CreatedUtc.AddMinutes(-job.Priority);
}
