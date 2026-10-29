#!/usr/bin/env python3
"""Compare explicit Runner revisions without silently merging unlike benchmark reports."""
import argparse,csv,json,math,pathlib,statistics

parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--before',nargs='+',type=pathlib.Path,required=True)
parser.add_argument('--after',nargs='+',type=pathlib.Path,required=True)
parser.add_argument('--output',type=pathlib.Path,required=True)
parser.add_argument('--plot',action='store_true')
args=parser.parse_args()
workload=['Jobs','Nodes','Workers','DelayMs','WarmupJobs','SettleSeconds','IdleSeconds']
environment=['packages','runtime','os','logicalProcessors']

def reject(message):raise SystemExit(message)
def read(paths):
    if len({p.resolve() for p in paths})!=len(paths):reject('Duplicate report paths cannot count as independent samples.')
    reports=[json.loads(p.read_text()) for p in paths]
    for report in reports:
        config=report['config']
        if config['Smoke']:reject('Smoke reports are not performance evidence.')
        expected={(p,e,n) for p in config['Profiles'].split(',') for e in config['Engines'].split(',') for n in range(1,config['Repetitions']+1)}
        actual={(r['Profile'],r['Engine'],r['Repetition']) for r in report['results']}
        if expected!=actual or len(actual)!=len(report['results']):reject('Incomplete or duplicate benchmark matrix.')
        if any(r['Jobs']!=config['Jobs'] or r['DuplicateBodies']!=0 for r in report['results']):reject('Invalid job verification.')
    for report in reports[1:]:
        if any(report[k]!=reports[0][k] for k in environment+['runnerRevision']):reject('A sample group contains different versions/environments.')
    return reports

before=read(args.before);after=read(args.after);all_reports=before+after
for report in all_reports[1:]:
    if any(report[k]!=before[0][k] for k in environment):reject('Versions/environments differ beyond the explicit Runner revision change.')
    if any(report['config'][k]!=before[0]['config'][k] for k in workload):reject('Workloads/timing differ.')
    if set(report['config']['Profiles'].split(','))!=set(before[0]['config']['Profiles'].split(',')):reject('Polling profiles differ.')
if before[0]['runnerRevision']==after[0]['runnerRevision']:reject('Before/after must identify different committed Runner revisions.')
if set(p.resolve() for p in args.before)&set(p.resolve() for p in args.after):reject('Before/after cannot reuse reports.')
groups={}
for stage,reports in [('before',before),('after',after)]:
    for report in reports:
        for row in report['results']:
            if stage=='before' and row['Engine']!='runner':continue
            groups.setdefault((row['Profile'],row['Engine'],stage),[]).append(row)
for profile in before[0]['config']['Profiles'].split(','):
    old=groups.get((profile,'runner','before'),[]);new=groups.get((profile,'runner','after'),[])
    if min(len(old),len(new))<3:reject('Runner before/after each need at least three independent samples per profile.')
    if any(row['EffectiveSettings']!=old[0]['EffectiveSettings'] for row in old+new):reject('Runner effective settings changed; this would conflate configuration and code improvements.')
metrics={
    'jobsPerSecond':lambda r:r['JobsPerSecond'],
    'p95BusinessLatencyMs':lambda r:r['BusinessLatencyP95Ms'],
    'loadSqlRequestsPerJob':lambda r:r['Load']['CompletedRequests']/r['Jobs'],
    'idleCompletedSqlRequestsPerSecond':lambda r:r['Idle']['CompletedRequestsPerSecond']}
summary=[]
for (profile,engine,stage),rows in sorted(groups.items(),key=lambda item:(item[0][0],{'runner':0,'hangfire':1,'quartz':2}[item[0][1]],item[0][2]!='before')):
    result=dict(profile=profile,engine=engine,stage=stage,samples=len(rows))
    for name,value in metrics.items():
        values=[value(row) for row in rows]
        if any(not math.isfinite(v) or v<0 for v in values):reject('Invalid metric.')
        for statistic,operation in [('Median',statistics.median),('Min',min),('Max',max)]:result[name+statistic]=operation(values)
    summary.append(result)
args.output.parent.mkdir(parents=True,exist_ok=True)
with args.output.with_suffix('.csv').open('w',newline='') as target:
    writer=csv.DictWriter(target,fieldnames=list(summary[0]),lineterminator='\n');writer.writeheader();writer.writerows(summary)
config=before[0]['config']
lines=['# Runner optimization comparison','',
    f"Before: `{before[0]['runnerRevision']}`. After: `{after[0]['runnerRevision']}`.",'',
    f"{config['Jobs']} jobs/run, {config['DelayMs']} ms body, {config['Nodes']} worker processes with {config['Workers']} slots each; {config['WarmupJobs']} warm-up jobs, {config['SettleSeconds']} s settling, {config['IdleSeconds']} s idle measurement. Runner effective polling/lease/history settings are identical before and after.",'',
    'Median and min–max across local samples; ranges are not confidence intervals. Shared development hardware, not production capacity or a universal ranking. Completed SQL requests are RPC + batches, including connection resets; they do not measure all database resources. Business P95 ends before framework finalization; throughput includes durable drain. See the benchmark methodology and raw reports.','',
    '| Profile | Engine | Stage | Samples | Jobs/s median (range) | P95 business ms median | Load SQL requests/job | Idle completed SQL requests/s |',
    '| --- | --- | --- | --- | --- | --- | --- | --- |']
for r in summary:
    lines.append(f"| {r['profile']} | {r['engine']} | {r['stage']} | {r['samples']} | {r['jobsPerSecondMedian']:.2f} ({r['jobsPerSecondMin']:.2f}–{r['jobsPerSecondMax']:.2f}) | {r['p95BusinessLatencyMsMedian']:.1f} | {r['loadSqlRequestsPerJobMedian']:.2f} | {r['idleCompletedSqlRequestsPerSecondMedian']:.3f} |")
lines+=['','Raw input files:','']+['- `'+str(p)+'`' for p in args.before+args.after]+['']
args.output.with_suffix('.md').write_text('\n'.join(lines))
if args.plot:
    import matplotlib;matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    profiles=sorted({r['profile'] for r in summary})
    for profile in profiles:
        order=[('runner','before'),('runner','after'),('hangfire','after'),('quartz','after')]
        rows=[next((r for r in summary if (r['profile'],r['engine'],r['stage'])==(profile,*key)),None) for key in order]
        rows=[r for r in rows if r is not None]
        figure,axes=plt.subplots(2,2,figsize=(11,7))
        for axis,(metric,title) in zip(axes.flat,[('jobsPerSecond','Jobs / second · higher is better'),('p95BusinessLatencyMs','P95 business latency (ms) · lower is better'),('loadSqlRequestsPerJob','Completed load SQL requests / job'),('idleCompletedSqlRequestsPerSecond','Completed idle SQL requests / second')]):
            medians=[r[metric+'Median'] for r in rows]
            errors=[[r[metric+'Median']-r[metric+'Min'] for r in rows],[r[metric+'Max']-r[metric+'Median'] for r in rows]]
            axis.bar(range(len(rows)),medians,yerr=errors,capsize=4,color=['#8b929a','#2674b5','#d87928','#40865a'][:len(rows)])
            axis.set_xticks(range(len(rows)),[r['engine']+'\n'+r['stage'] for r in rows]);axis.set_title(title,fontsize=10);axis.grid(axis='y',alpha=.2);axis.set_axisbelow(True)
        figure.suptitle('Runner optimization · '+profile+' · local exploratory samples')
        figure.text(.5,.015,'Bars: medians; whiskers: min–max, not confidence intervals. Same Runner settings; see raw reports and methodology.',ha='center',fontsize=8)
        figure.tight_layout(rect=(0,.04,1,.95))
        path=args.output.with_name(args.output.name+'-'+profile).with_suffix('.svg');figure.savefig(path);plt.close(figure)
        path.write_text('\n'.join(line.rstrip() for line in path.read_text().splitlines())+'\n')
print(args.output.with_suffix('.md'))
