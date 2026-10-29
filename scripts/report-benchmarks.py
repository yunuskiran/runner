#!/usr/bin/env python3
"""Render native benchmark JSON as CSV/Markdown; optional SVG uses matplotlib."""
import argparse,csv,json,pathlib,statistics
parser=argparse.ArgumentParser();parser.add_argument('reports',nargs='+',type=pathlib.Path);parser.add_argument('--output',type=pathlib.Path,default=pathlib.Path('artifacts/benchmarks/comparison'));parser.add_argument('--plot',action='store_true');args=parser.parse_args()
reports=[json.loads(p.read_text()) for p in args.reports]
if any(r['config']['Smoke'] for r in reports):raise SystemExit('Smoke reports validate adapters only; do not render them as performance evidence.')
for report in reports:
    expected={(p,e,n) for p in report['config']['Profiles'].split(',') for e in report['config']['Engines'].split(',') for n in range(1,report['config']['Repetitions']+1)}
    actual={(r['Profile'],r['Engine'],r['Repetition']) for r in report['results']}
    if expected!=actual or len(report['results'])!=len(expected):raise SystemExit('Incomplete or duplicate benchmark cases; rerun before comparison.')
for key in ['packages','runtime','os','logicalProcessors','runnerRevision']:
    if any(r.get(key)!=reports[0].get(key) for r in reports):raise SystemExit('Reports have different runtime/version metadata; compare separately.')
# Refuse to silently combine different workloads into one comparison.
keys=['Jobs','Nodes','Workers','DelayMs','WarmupJobs','SettleSeconds','IdleSeconds']
if any(any(r['config'][k]!=reports[0]['config'][k] for k in keys) for r in reports):raise SystemExit('Reports have different workloads/timing; compare separately.')
rows=[row for report in reports for row in report['results']];groups={}
for row in rows:groups.setdefault((row['Profile'],row['Engine']),[]).append(row)
columns=['profile','engine','samples','jobsPerSecondMedian','p95BusinessLatencyMsMedian','idleCompletedSqlRequestsPerSecondMedian','idleSqlStatementEventsPerSecondMedian','idleSpStatementEventsPerSecondMedian','idleActiveRequestsMedian','loadSqlRequestsPerJobMedian','workerCpuMsPerJobMedian','executingNodesMedian']
summary=[]
for (profile,engine),samples in sorted(groups.items()):
    median=lambda f:statistics.median(f(r) for r in samples)
    summary.append(dict(zip(columns,[profile,engine,len(samples),median(lambda r:r['JobsPerSecond']),median(lambda r:r['BusinessLatencyP95Ms']),median(lambda r:r['Idle']['CompletedRequestsPerSecond']),median(lambda r:r['Idle']['SqlStatementEvents']/r['Idle']['Seconds']),median(lambda r:r['Idle']['SpStatementEvents']/r['Idle']['Seconds']),median(lambda r:r['Idle']['EndActiveRequests']),median(lambda r:r['Load']['CompletedRequests']/r['Jobs']),median(lambda r:r['Load']['WorkerCpuMs']/r['Jobs']),median(lambda r:r['DistinctExecutingNodes'])])))
args.output.parent.mkdir(parents=True,exist_ok=True)
with args.output.with_suffix('.csv').open('w',newline='') as target:
    writer=csv.DictWriter(target,fieldnames=columns,lineterminator="\n");writer.writeheader();writer.writerows(summary)
lines=['# Local engine comparison','','Exploratory measurements of in-process jobs on one shared development machine. No production sizing or universal winner claim. Business latency excludes framework finalization; throughput waits for durable drain. Completed SQL requests are server RPC + batch events, not EF command counts. Statement event classes overlap framework/request boundaries and are reported separately. Long-poll requests still running at the end are shown as active requests.','', '| Profile | Engine | Samples | Jobs/s median | P95 business latency ms | Idle completed SQL requests/s | Idle active requests | Load SQL requests/job | Executing nodes |','| --- | --- | --- | --- | --- | --- | --- | --- | --- |']
for r in summary:lines.append(f"| {r['profile']} | {r['engine']} | {r['samples']} | {r['jobsPerSecondMedian']:.2f} | {r['p95BusinessLatencyMsMedian']:.1f} | {r['idleCompletedSqlRequestsPerSecondMedian']:.3f} | {r['idleActiveRequestsMedian']:.1f} | {r['loadSqlRequestsPerJobMedian']:.2f} | {r['executingNodesMedian']:.1f} |")
lines+=['','Defaults and responsive profiles have different discovery latencies: do not treat cross-profile throughput as pure execution speed. Runner includes its ordinary attempt/lifecycle journal; Hangfire includes its ordinary state history; Quartz uses default history settings and removes completed one-shot triggers. None includes HTTP, auth/audit middleware, or per-job process startup. Common result recording goes to a separate database excluded from engine DB counters; its cost remains in wall time. All schemas are fresh and warm-up/settling run before measurement. Read the raw effective settings and methodology before drawing conclusions.','']
args.output.with_suffix('.md').write_text('\n'.join(lines))
if args.plot:
    import matplotlib;matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    profiles=[p for p in ['defaults','responsive'] if any(r['profile']==p for r in summary)];engines=['runner','hangfire','quartz']
    figure,axes=plt.subplots(1,2,figsize=(11,4.6));colors=['#2674b5','#d87928'];width=.32
    for index,profile in enumerate(profiles):
        for axis,metric,title in [(axes[0],'jobsPerSecondMedian','Observed jobs / second'),(axes[1],'idleCompletedSqlRequestsPerSecondMedian','Idle completed SQL requests / second')]:
            values=[next((r[metric] for r in summary if r['profile']==profile and r['engine']==e),float('nan')) for e in engines]
            axis.bar([n+(index-(len(profiles)-1)/2)*width for n in range(3)],values,width,label=profile,color=colors[index]);axis.set_title(title);axis.set_xticks(range(3),engines);axis.grid(axis='y',alpha=.2);axis.set_axisbelow(True)
    axes[0].legend();figure.suptitle('Local in-process engine comparison · exploratory samples');figure.text(.5,.015,'Same workload, SQL Server and node/slot counts. Polling profiles differ. See raw JSON and methodology; no production sizing claim.',ha='center',fontsize=8);figure.tight_layout(rect=(0,.05,1,.94));figure.savefig(args.output.with_suffix('.svg'));plt.close(figure)
    svg=args.output.with_suffix('.svg');svg.write_text('\n'.join(line.rstrip() for line in svg.read_text().splitlines())+'\n')
print(args.output.with_suffix('.md'))
