using Microsoft.EntityFrameworkCore;
namespace Runner.Engine;

public enum JobState { Pending, Running, Succeeded, DeadLetter, Cancelled }

public sealed class Job
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public string? IdempotencyKey { get; set; }
    public JobState State { get; set; }
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime AvailableUtc { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedUtc { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }
    public Guid? LeaseToken { get; set; }
    public string? Worker { get; set; }
    public bool CancellationRequested { get; set; }
    public string? ErrorCode { get; set; }
}

public sealed class Schedule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public int IntervalSeconds { get; set; }
    public DateTime NextUtc { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class RunnerDb(DbContextOptions<RunnerDb> options) : DbContext(options)
{
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<Schedule> Schedules => Set<Schedule>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Job>().HasIndex(x => new { x.State, x.AvailableUtc });
        b.Entity<Job>().HasIndex(x => x.LeaseUntilUtc);
        b.Entity<Job>().HasIndex(x => x.CreatedUtc);
        b.Entity<Job>().HasIndex(x => x.IdempotencyKey).IsUnique();
        b.Entity<Job>().Property(x => x.Type).HasMaxLength(120);
        b.Entity<Job>().Property(x => x.IdempotencyKey).HasMaxLength(160);
        b.Entity<Schedule>().HasIndex(x => new { x.Enabled, x.NextUtc });
        foreach (var entity in b.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties())
                if (property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?))
                    property.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
                        value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc)));
    }
}

public sealed class RunnerOptions
{
    public int Concurrency { get; set; } = 4;
    public int PollMilliseconds { get; set; } = 1000;
    public int LeaseSeconds { get; set; } = 60;
    public int HeartbeatSeconds { get; set; } = 15;
    public int JobTimeoutSeconds { get; set; } = 300;
    public int RetentionDays { get; set; } = 14;
    public bool WorkerEnabled { get; set; } = true;
}
