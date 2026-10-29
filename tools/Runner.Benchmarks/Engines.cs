using System.Text.Json;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;
using Runner.Engine;
using Runner.Engine.Packaging;
namespace Runner.Benchmarks;

public sealed class Engine : IAsyncDisposable
{
    private readonly NodeConfig config;
    private IHost? host;
    private BackgroundJobServer? hangfire;
    private SqlServerStorage? storage;
    private IScheduler? scheduler;
    private StandaloneSchedulerFactory? quartzFactory;
    public Dictionary<string, object> Effective { get; } = [];
    public Engine(NodeConfig config) => this.config = config;
    public async Task Initialize(bool worker)
    {
        Work.Connection = config.ResultsConnection; Work.Node = config.Name;
        if (config.Engine == "runner")
        {
            var builder = Host.CreateApplicationBuilder(); builder.Logging.ClearProviders();
            builder.Services.AddDbContextFactory<RunnerDb>(o => RunnerStorage.Configure(o, "Mssql", config.Connection));
            var options = new RunnerOptions { InstanceName = config.Name, Concurrency = config.Workers,
                MaxIdlePollSeconds = config.Profile == "defaults" ? 30 : 1 };
            builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));
            builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new OperationOptions()));
            builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new PackageOptions()));
            builder.Services.AddScoped<RunnerJob>(); builder.Services.AddScoped<Runner.Contracts.IJobHandler>(s => s.GetRequiredService<RunnerJob>());
            builder.Services.AddSingleton<HandlerCatalog>(s => { using var scope = s.CreateScope(); return new(scope.ServiceProvider.GetServices<Runner.Contracts.IJobHandler>()); });
            builder.Services.AddSingleton<JobStore>(); builder.Services.AddSingleton<ClusterClock>(); builder.Services.AddSingleton<QueueSignals>();
            builder.Services.AddSingleton<NodeRegistry>(); builder.Services.AddSingleton<WorkflowService>();
            if (worker) builder.Services.AddHostedService<Worker>();
            host = builder.Build();
            await using var db = await host.Services.GetRequiredService<IDbContextFactory<RunnerDb>>().CreateDbContextAsync();
            if (!worker) await db.Database.EnsureCreatedAsync();
            if (worker) await host.StartAsync();
            Effective["pollMinimumMs"] = options.PollMilliseconds; Effective["idlePollCapSeconds"] = options.MaxIdlePollSeconds;
            Effective["jobHeartbeatSeconds"] = options.HeartbeatSeconds; Effective["nodeHeartbeatSeconds"] = options.NodeHeartbeatSeconds;
            Effective["schedulerSeconds"] = options.SchedulerSeconds; Effective["retentionSeconds"] = options.MaintenanceSeconds;
            Effective["execution"] = "in-process IJobHandler, regular Runner engine journal/leases; no HTTP/auth/audit or plugin-process fork";
        }
        else if (config.Engine == "hangfire")
        {
            Hangfire.Logging.LogProvider.SetCurrentLogProvider(new QuietHangfire());
            var sql = new SqlServerStorageOptions { SqlClientFactory = SqlClientFactory.Instance, PrepareSchemaIfNecessary = !worker };
            if (config.Profile == "responsive") sql.QueuePollInterval = TimeSpan.FromSeconds(1);
            if (config.Profile == "longpoll") { sql.QueuePollInterval = TimeSpan.Zero; sql.SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5); }
            storage = new SqlServerStorage(config.Connection, sql);
            if (worker) hangfire = new BackgroundJobServer(new BackgroundJobServerOptions { ServerName = config.Name, WorkerCount = config.Workers }, storage);
            Effective["queuePollSeconds"] = sql.QueuePollInterval.TotalSeconds;
            Effective["slidingInvisibilitySeconds"] = sql.SlidingInvisibilityTimeout?.TotalSeconds ?? -1;
            Effective["tryAutoDetectSchemaDependentOptions"] = sql.TryAutoDetectSchemaDependentOptions;
            Effective["disableGlobalLocks"] = sql.DisableGlobalLocks;
        }
        else
        {
            var options = new QuartzSchedulerOptions();
            quartzFactory = QuartzSchedulerBuilder.Create(q =>
            {
                q.Services.AddLogging(l => l.ClearProviders());
                q.ConfigureScheduler(o => { o.InstanceName = "benchmark"; o.InstanceId = config.Name; if (config.Profile != "defaults") o.IdleWaitTime = TimeSpan.FromSeconds(1); });
                q.UseDefaultThreadPool(config.Workers);
                q.UsePersistentStore(s =>
                {
                    s.UseSqlServer(SqlClientFactory.Instance, config.Connection);
                    s.ConfigureStore(o => o.StoreJobDataAsStrings = true);
                    s.UseClustering(); s.UseSystemTextJsonSerializer();
                });
            }).Build();
            scheduler = await quartzFactory.GetScheduler();
            if (worker) await scheduler.Start();
            Effective["idleWaitSeconds"] = config.Profile == "defaults" ? options.IdleWaitTime.TotalSeconds : 1d;
            Effective["clustered"] = true; Effective["acquisitionBatch"] = options.MaxBatchSize;
            Effective["executionHistory"] = "Quartz default; optional execution history not enabled";
        }
    }
    public async Task Submit(WorkItem item)
    {
        if (config.Engine == "runner")
        {
            await using var db = await host!.Services.GetRequiredService<IDbContextFactory<RunnerDb>>().CreateDbContextAsync();
            await using var tx = await db.Database.BeginTransactionAsync();
            await Admission.LockAsync(db, default);
            if (!await Admission.HasCapacityAsync(db, 1, 100000, default)) throw new InvalidOperationException("Benchmark queue capacity reached");
            var job = new Job { Type = "benchmark", Payload = JsonSerializer.Serialize(item), CreatedUtc = new(item.EnqueuedTicks, DateTimeKind.Utc), AvailableUtc = DateTime.UtcNow };
            Admission.SetOrder(job); db.Jobs.Add(job); await db.SaveChangesAsync(); await tx.CommitAsync();
            // Producer is a separate process from workers; no framework receives a cross-process local wake hint.
        }
        else if (config.Engine == "hangfire") new BackgroundJobClient(storage!).Enqueue<HangfireJob>(j => j.Execute(item.Id, item.EnqueuedTicks, item.DelayMs));
        else
        {
            var job = JobBuilder.Create<QuartzJob>().WithIdentity(item.Id).UsingJobData("id", item.Id)
                .UsingJobData("ticks", item.EnqueuedTicks.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .UsingJobData("delay", item.DelayMs.ToString(System.Globalization.CultureInfo.InvariantCulture)).Build();
            await scheduler!.ScheduleJob(job, TriggerBuilder.Create().WithIdentity(item.Id).StartNow().Build());
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (hangfire is not null) { hangfire.SendStop(); await hangfire.WaitForShutdownAsync(default); hangfire.Dispose(); }
        if (quartzFactory is not null) await quartzFactory.DisposeAsync();
        if (host is not null) { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15)); await host.StopAsync(stop.Token); host.Dispose(); }
    }
    private sealed class QuietHangfire : Hangfire.Logging.ILogProvider
    { public Hangfire.Logging.ILog GetLogger(string name) => new QuietLog(); }
    private sealed class QuietLog : Hangfire.Logging.ILog
    { public bool Log(Hangfire.Logging.LogLevel level, Func<string>? message, Exception? exception = null) => false; }
}
