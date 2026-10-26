using Microsoft.EntityFrameworkCore;
using Runner.Engine;
using Xunit;
namespace Runner.Tests;

public sealed class StoreTests : IAsyncLifetime
{
    private TestFactory factory = null!;
    private JobStore store = null!;
    private readonly DateTime now = new(DateTime.UtcNow.Ticks / 10 * 10, DateTimeKind.Utc);
    public async Task InitializeAsync()
    {
        var pg = Environment.GetEnvironmentVariable("TEST_POSTGRES");
        var options = new DbContextOptionsBuilder<RunnerDb>();
        if (pg is null) options.UseSqlite($"Data Source={Path.GetTempPath()}runner-{Guid.NewGuid():N}.db;Default Timeout=30");
        else { var cs = new Npgsql.NpgsqlConnectionStringBuilder(pg) { Database = $"runner_test_{Guid.NewGuid():N}" }; options.UseNpgsql(cs.ConnectionString); }
        factory = new TestFactory(options.Options); store = new JobStore(factory);
        await using var db = factory.CreateDbContext(); await db.Database.EnsureCreatedAsync();
    }
    public async Task DisposeAsync() { await using var db = factory.CreateDbContext(); await db.Database.EnsureDeletedAsync(); }
    private async Task<Job> Add(int attempts = 3)
    {
        await using var db = factory.CreateDbContext();
        var job = new Job { Type = "test", MaxAttempts = attempts, AvailableUtc = now.AddSeconds(-1) };
        db.Add(job); await db.SaveChangesAsync(); return job;
    }
    private Task<Job?> Claim(DateTime? at = null) => store.ClaimAsync("test-worker", at ?? now, TimeSpan.FromSeconds(30), default);
    [Fact] public async Task Concurrent_claim_has_one_owner()
    {
        await Add(); var claims = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Claim()));
        Assert.Single(claims, x => x is not null); Assert.Equal(1, claims.Single(x => x is not null)!.Attempts);
    }
    [Fact] public async Task Expired_owner_cannot_complete_or_renew()
    {
        await Add(); var first = (await Claim())!; var second = (await Claim(now.AddSeconds(31)))!;
        Assert.NotEqual(first.LeaseToken, second.LeaseToken); Assert.Equal(2, second.Attempts);
        Assert.False(await store.CompleteAsync(first, now.AddSeconds(32), null, default));
        Assert.False(await store.RenewAsync(first.Id, first.LeaseToken!.Value, now.AddSeconds(32), TimeSpan.FromSeconds(30), default));
        Assert.True(await store.CompleteAsync(second, now.AddSeconds(32), null, default));
    }
    [Fact] public async Task Expired_lease_cannot_complete_even_before_reclaim()
    {
        await Add(); var job = (await Claim())!;
        Assert.False(await store.CompleteAsync(job, now.AddSeconds(31), null, default));
        Assert.False(await store.RenewAsync(job.Id, job.LeaseToken!.Value, now.AddSeconds(31), TimeSpan.FromSeconds(30), default));
    }
    [Fact] public async Task Failure_retries_then_deadletters()
    {
        await Add(2); var first = (await Claim())!;
        Assert.True(await store.CompleteAsync(first, now, "failed", default));
        Assert.Null(await Claim());
        var second = (await Claim(now.AddSeconds(10)))!; Assert.Equal(2, second.Attempts);
        await store.CompleteAsync(second, now.AddSeconds(10), "failed", default);
        await using var db = factory.CreateDbContext(); Assert.Equal(JobState.DeadLetter, (await db.Jobs.SingleAsync()).State);
    }
    [Fact] public async Task Final_crashed_attempt_deadletters()
    {
        await Add(1); await Claim(); Assert.Null(await Claim(now.AddSeconds(31)));
        await using var db = factory.CreateDbContext(); var job = await db.Jobs.SingleAsync();
        Assert.Equal(JobState.DeadLetter, job.State); Assert.Equal("lease_expired", job.ErrorCode);
    }
    [Fact] public async Task Cancellation_prevents_completion_and_eventually_finalizes()
    {
        await Add(); var job = (await Claim())!;
        await using (var db = factory.CreateDbContext()) await db.Jobs.ExecuteUpdateAsync(s => s.SetProperty(x => x.CancellationRequested, true));
        Assert.False(await store.RenewAsync(job.Id, job.LeaseToken!.Value, now, TimeSpan.FromSeconds(30), default));
        Assert.False(await store.CompleteAsync(job, now, null, default));
        await store.MaintainAsync(now.AddSeconds(31), 14, default);
        await using var verify = factory.CreateDbContext(); Assert.Equal(JobState.Cancelled, (await verify.Jobs.SingleAsync()).State);
    }
    [Fact] public async Task Pending_cancellation_never_executes()
    {
        await Add(); await using (var db = factory.CreateDbContext()) await db.Jobs.ExecuteUpdateAsync(s => s.SetProperty(x => x.CancellationRequested, true));
        Assert.Null(await Claim()); await store.MaintainAsync(now, 14, default);
        await using var verify = factory.CreateDbContext(); Assert.Equal(JobState.Cancelled, (await verify.Jobs.SingleAsync()).State);
    }
    [Fact] public async Task Scheduler_is_atomic_and_coalesces_missed_ticks()
    {
        await using (var db = factory.CreateDbContext()) { db.Schedules.Add(new Schedule { Type = "test", IntervalSeconds = 60, NextUtc = now.AddHours(-1) }); await db.SaveChangesAsync(); }
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.DispatchSchedulesAsync(now, default)));
        await using var verify = factory.CreateDbContext(); Assert.Equal(1, await verify.Jobs.CountAsync());
        Assert.Equal(now.AddSeconds(60), (await verify.Schedules.SingleAsync()).NextUtc);
    }
    [Fact] public async Task Retention_keeps_deadletters()
    {
        await using (var db = factory.CreateDbContext()) { db.Jobs.AddRange(new Job { State = JobState.Succeeded, FinishedUtc = now.AddDays(-20) }, new Job { State = JobState.DeadLetter, FinishedUtc = now.AddDays(-20) }); await db.SaveChangesAsync(); }
        await store.MaintainAsync(now, 14, default);
        await using var verify = factory.CreateDbContext(); Assert.Equal(JobState.DeadLetter, (await verify.Jobs.SingleAsync()).State);
    }
    [Fact] public async Task Idempotency_key_is_unique()
    {
        await using var db = factory.CreateDbContext(); db.Jobs.Add(new Job { IdempotencyKey = "same" }); await db.SaveChangesAsync();
        db.Jobs.Add(new Job { IdempotencyKey = "same" }); await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
    private sealed class TestFactory(DbContextOptions<RunnerDb> options) : IDbContextFactory<RunnerDb>
    { public RunnerDb CreateDbContext() => new(options); }
}
