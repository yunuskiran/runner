using Microsoft.EntityFrameworkCore;
namespace Runner.Engine;

public sealed class WorkflowService(IDbContextFactory<RunnerDb> factory)
{
    public static void Validate(IReadOnlyDictionary<string, string[]> dependencies)
    {
        if (dependencies.Count is < 1 or > 100 || dependencies.Keys.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 64))
            throw new ArgumentException("Workflows need 1–100 uniquely named steps (1–64 characters)");
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string key)
        {
            if (visited.Contains(key)) return;
            if (!dependencies.ContainsKey(key)) throw new ArgumentException("Unknown workflow dependency: " + key);
            if (!visiting.Add(key)) throw new ArgumentException("Workflow contains a dependency cycle");
            if (dependencies[key].Length > 100 || dependencies[key].Distinct(StringComparer.Ordinal).Count() != dependencies[key].Length)
                throw new ArgumentException("Invalid or duplicate workflow dependencies");
            foreach (var parent in dependencies[key]) Visit(parent);
            visiting.Remove(key); visited.Add(key);
        }
        foreach (var key in dependencies.Keys) Visit(key);
    }

    public async Task<int> AdvanceAsync(DateTime now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var candidates = await db.Jobs.Where(x => x.State == JobState.Blocked && !x.CancellationRequested)
            .Select(x => new { x.Id, Failed = db.JobDependencies.Any(d => d.JobId == x.Id && db.Jobs.Any(p => p.Id == d.DependsOnJobId &&
                (p.State == JobState.DeadLetter || p.State == JobState.Cancelled))),
                Ready = !db.JobDependencies.Any(d => d.JobId == x.Id && db.Jobs.Any(p => p.Id == d.DependsOnJobId && p.State != JobState.Succeeded)) })
            .Where(x => x.Failed || x.Ready).OrderBy(x => x.Id).Take(500).ToListAsync(ct);
        var failed = candidates.Where(x => x.Failed).Select(x => x.Id).ToArray();
        var ready = candidates.Where(x => x.Ready && !x.Failed).Select(x => x.Id).ToArray();
        var count = failed.Length == 0 ? 0 : await db.Jobs.Where(x => failed.Contains(x.Id) && x.State == JobState.Blocked)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, JobState.Cancelled).SetProperty(x => x.ErrorCode, "dependency_failed")
                .SetProperty(x => x.FinishedUtc, now), ct);
        return count + (ready.Length == 0 ? 0 : await db.Jobs.Where(x => ready.Contains(x.Id) && x.State == JobState.Blocked && !x.CancellationRequested)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, JobState.Pending).SetProperty(x => x.AvailableUtc, x => x.AvailableUtc < now ? now : x.AvailableUtc), ct));
    }
}
