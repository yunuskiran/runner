using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Runner.Benchmarks;

if (args.Contains("--node")) { await NodeProcess.Run(); return; }
var connection = Environment.GetEnvironmentVariable("BENCH_MSSQL") ?? throw new InvalidOperationException("Set BENCH_MSSQL securely to a disposable SQL Server instance with database creation and XE permissions");
var config = new Settings();
for (int i=0;i<args.Length;i++) config = args[i] switch
{
    "--smoke" => config with { Smoke=true, Jobs=12, WarmupJobs=4, SettleSeconds=2, IdleSeconds=3, Repetitions=1, Profiles="responsive" },
    "--jobs" => config with { Jobs=int.Parse(args[++i]) }, "--repetitions" => config with { Repetitions=int.Parse(args[++i]) },
    "--idle-seconds" => config with { IdleSeconds=int.Parse(args[++i]) }, "--settle-seconds" => config with { SettleSeconds=int.Parse(args[++i]) },
    "--profiles" => config with { Profiles=args[++i] }, "--engines" => config with { Engines=args[++i] },
    "--nodes" => config with { Nodes=int.Parse(args[++i]) }, "--workers" => config with { Workers=int.Parse(args[++i]) },
    "--delay-ms" => config with { DelayMs=int.Parse(args[++i]) }, _ => throw new ArgumentException("Unknown argument: "+args[i])
};
if(config.Jobs is <1 or >10000 || config.Nodes is <1 or >4 || config.Workers is <1 or >32 || config.Repetitions is <1 or >10 ||
    config.DelayMs is <0 or >1000 || config.IdleSeconds is <1 or >600 || config.SettleSeconds is <0 or >600 ||
    config.Profiles.Split(',').Any(x=>x is not ("defaults" or "responsive" or "longpoll")) || config.Engines.Split(',').Any(x=>x is not ("runner" or "hangfire" or "quartz"))) throw new ArgumentException("Invalid benchmark limits/profile/engine");
var master = Config.WithDatabase(connection,"master","RunnerBenchmark.Observer");
Directory.CreateDirectory("artifacts/benchmarks");
var reportPath = "artifacts/benchmarks/"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+(config.Smoke?"-smoke":"")+".json";
var results = new List<CaseResult>();
var schema = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"Sql/quartz-4.6.2-sqlserver.sql"));
var created = new List<string>();
try
{
    for(int repetition=1;repetition<=config.Repetitions;repetition++)
    foreach(var profile in config.Profiles.Split(','))
    {
        // Rotate order across repetitions instead of always granting one framework the cold/warm server.
        var engines=config.Engines.Split(','); engines=engines.Skip((repetition-1)%engines.Length).Concat(engines.Take((repetition-1)%engines.Length)).ToArray();
        foreach(var engineName in engines)
        {
            Console.WriteLine($"START {engineName} {profile} repetition {repetition}");
            var suffix=Guid.NewGuid().ToString("N"); var database="runner_bench_"+suffix; var ledger="runner_bench_result_"+suffix;
            var prefix="RBench."+suffix+"."; var nodes=new List<NodeProcess>();
            try
            {
                foreach(var name in new[]{database,ledger})
                {
                    await Config.Execute(master,$"CREATE DATABASE [{name}];");
                    created.Add(name); // Ownership is recorded only after creation succeeds.
                    await Config.Execute(master,$"ALTER DATABASE [{name}] SET RECOVERY SIMPLE; ALTER DATABASE [{name}] SET READ_COMMITTED_SNAPSHOT ON;");
                }
                var observerConnection=Config.WithDatabase(connection,database,"RunnerBenchmark.Observer");
                var resultConnection=Config.WithDatabase(connection,ledger,"RunnerBenchmark.Results");
                await Config.Execute(resultConnection,"CREATE TABLE dbo.Bodies(Id varchar(64) NOT NULL PRIMARY KEY,EnqueuedUtc datetime2(7) NOT NULL,StartedUtc datetime2(7) NOT NULL,FinishedUtc datetime2(7) NOT NULL,Node nvarchar(100) NOT NULL,Executions int NOT NULL)");
                if(engineName=="quartz")
                {
                    var ddl=schema.Replace("USE [enter_db_name_here];",$"USE [{database}];").Replace("DECLARE @DropDb BIT = 1;","DECLARE @DropDb BIT = 0;");
                    foreach(var batch in Regex.Split(ddl,@"^\s*GO\s*\r?$",RegexOptions.Multiline)) if(!string.IsNullOrWhiteSpace(batch)) await Config.Execute(observerConnection,batch);
                }
                var producerConfig=new NodeConfig(engineName,Config.WithDatabase(connection,database,prefix+"producer"),resultConnection,"producer",config.Workers,profile);
                await using var producer=new Engine(producerConfig); await producer.Initialize(false);
                for(int node=0;node<config.Nodes;node++) nodes.Add(await NodeProcess.Start(producerConfig with {Name="node-"+node,Connection=Config.WithDatabase(connection,database,prefix+"node-"+node)}));
                await SubmitAndDrain("warm",config.WarmupJobs,producer,engineName,observerConnection,resultConnection,nodes);
                await Task.Delay(TimeSpan.FromSeconds(config.SettleSeconds));
                await using var observer=new SqlObserver(master,database,prefix); await observer.Start();
                var before=await observer.Read();var cpu=nodes.Sum(n=>n.CpuMs);var timer=Stopwatch.StartNew();
                await Task.Delay(TimeSpan.FromSeconds(config.IdleSeconds)); nodes.ForEach(n=>n.Verify());
                var after=await observer.Read();var idle=Window.Between(before,after,timer.Elapsed.TotalSeconds,nodes.Sum(n=>n.CpuMs)-cpu,nodes.Sum(n=>n.WorkingSet));
                before=await observer.Read();cpu=nodes.Sum(n=>n.CpuMs);timer.Restart();
                var enqueue=await SubmitAndDrain("load",config.Jobs,producer,engineName,observerConnection,resultConnection,nodes);
                var drained=timer.Elapsed.TotalSeconds; after=await observer.Read();
                var load=Window.Between(before,after,timer.Elapsed.TotalSeconds,nodes.Sum(n=>n.CpuMs)-cpu,nodes.Sum(n=>n.WorkingSet));
                if (load.CompletedRequests <= 0 || load.SqlStatementEvents + load.SpStatementEvents <= 0 || idle.CompletedRequests < 0)
                    throw new InvalidOperationException("SQL counter calibration failed; refusing a zero/missing/reset measurement");
                var latencies=new List<double>();var workers=new HashSet<string>();int duplicates=0;
                await using(var db=new SqlConnection(resultConnection))
                {
                    await db.OpenAsync();await using var cmd=new SqlCommand("SELECT DATEDIFF_BIG(microsecond,EnqueuedUtc,FinishedUtc)/1000.0,Node,Executions FROM dbo.Bodies WHERE Id LIKE 'load-%'",db);
                    await using var data=await cmd.ExecuteReaderAsync();while(await data.ReadAsync()){latencies.Add(Convert.ToDouble(data.GetValue(0)));workers.Add(data.GetString(1));duplicates+=data.GetInt32(2)-1;}
                }
                if(latencies.Count!=config.Jobs || duplicates!=0 || workers.Any(n => !Enumerable.Range(0,config.Nodes).Select(i=>"node-"+i).Contains(n)))throw new InvalidOperationException("Missing or duplicate job bodies; benchmark rejected");
                latencies.Sort();double Percentile(double p)=>latencies[Math.Max(0,(int)Math.Ceiling(latencies.Count*p)-1)];
                var row=new CaseResult(engineName,profile,repetition,producer.Effective,idle,load,enqueue,config.Jobs/drained,Percentile(.5),Percentile(.95),Percentile(.99),config.Jobs,workers.Count,duplicates);
                results.Add(row);await Save();Console.WriteLine($"PASS {engineName} {profile}: {row.JobsPerSecond:F2} jobs/s; p95 {row.BusinessLatencyP95Ms:F1} ms; idle {idle.CompletedRequestsPerSecond:F3} completed SQL requests/s");
            }
            finally
            {
                foreach(var node in nodes)await node.DisposeAsync();
                foreach(var name in new[]{database,ledger})await Drop(name);
            }
        }
    }
    Console.WriteLine("REPORT "+reportPath);
}
finally { foreach(var name in created.ToArray())await Drop(name); }

async Task Drop(string name)
{
    if (!created.Contains(name)) return;
    await Config.Execute(master,$"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END");
    created.Remove(name);
}
async Task Save()
{
    var report=new { config, purpose=config.Smoke?"functional smoke only; not comparative performance evidence":"exploratory local benchmark; not a production sizing result",
        runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,os=System.Runtime.InteropServices.RuntimeInformation.OSDescription,logicalProcessors=Environment.ProcessorCount,
        runnerRevision=typeof(Runner.Engine.Worker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        packages=new{HangfireSqlServer="1.8.25",Quartz="4.6.2",MicrosoftDataSqlClient="7.1.1",NewtonsoftJson="13.0.4"},
        methodology="Separate processes, same SQL Server, fresh database per case, common in-process body + separate result database. Extended Events event_counter counts completed RPC/batches (observer/result traffic excluded). Statement event classes reported separately; do not add to request counts. Physical IO includes database background writes. Business latency ends before result insert/framework finalization; throughput waits for durable framework drain. No HTTP/auth/audit/plugin-process overhead in any adapter.",results };
    await File.WriteAllTextAsync(reportPath+".tmp",JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true})+"\n");
    File.Move(reportPath+".tmp",reportPath,true);
}
async Task<double> SubmitAndDrain(string phase,int count,Engine producer,string engine,string engineDb,string resultDb,List<NodeProcess> nodes)
{
    var enqueue=Stopwatch.StartNew();for(int n=0;n<count;n++)await producer.Submit(new(phase+"-"+n,DateTime.UtcNow.Ticks,config.DelayMs));
    var enqueueSeconds=enqueue.Elapsed.TotalSeconds;var deadline=DateTime.UtcNow.AddMinutes(3);
    while(DateTime.UtcNow<deadline)
    {
        nodes.ForEach(n=>n.Verify());
        await using var db=new SqlConnection(resultDb);await db.OpenAsync();await using var body=new SqlCommand("SELECT COUNT(*) FROM dbo.Bodies WHERE Id LIKE @phase",db);body.Parameters.AddWithValue("@phase",phase+"-%");
        if(Convert.ToInt32(await body.ExecuteScalarAsync())==count)
        {
            await using var state=new SqlConnection(engineDb);await state.OpenAsync();
            var sql=engine switch{"runner"=>"SELECT COUNT(*) FROM dbo.Jobs WHERE State <> 2","hangfire"=>"SELECT COUNT(*) FROM HangFire.Job WHERE StateName <> 'Succeeded' OR StateName IS NULL",_=>"SELECT (SELECT COUNT(*) FROM dbo.QRTZ_FIRED_TRIGGERS)+(SELECT COUNT(*) FROM dbo.QRTZ_TRIGGERS)"};
            await using var done=new SqlCommand(sql,state);if(Convert.ToInt32(await done.ExecuteScalarAsync())==0)return enqueueSeconds;
        }
        await Task.Delay(20);
    }
    throw new TimeoutException("Jobs did not durably drain; no performance result will be published for this case");
}
