#!/usr/bin/env python3
"""Bounded local load and idle SQL command measurement. Not a production capacity certification."""
import json,os,pathlib,statistics,time
from host_fixture import Hosts,ROOT
count=int(os.environ.get('BENCH_JOBS','100'));idle=int(os.environ.get('BENCH_IDLE_SECONDS','30'));warmup=int(os.environ.get('BENCH_WARMUP_SECONDS','35'))
assert 1<=count<=100 and 10<=idle<=120 and 5<=warmup<=120
with Hosts() as hosts:
    settings=dict(Runner__Concurrency='8',Runner__NodeHeartbeatSeconds='10',Runner__SchedulerSeconds='5',Runner__PollMilliseconds='1000',Runner__MaxIdlePollSeconds='30',Scripting__Enabled='false')
    hosts.start('load-a',**settings);hosts.start('load-b',**settings)
    def sql():return {name:hosts.api('/api/diagnostics/database',base=node['url'])[1] for name,node in hosts.nodes.items()}
    def delta(before,after):return {name:{key:after[name][key]-before[name][key] for key in before[name]} for name in before}
    before=sql();started=time.monotonic();ids=[hosts.submit({'delayMs':10},maxAttempts=1) for _ in range(count)]
    def completed():
        rows=hosts.api('/api/jobs?page=0')[1]
        if count>50:rows+=hosts.api('/api/jobs?page=1')[1]
        selected=[row for row in rows if row['id'] in ids]
        assert not any(row['state']=='DeadLetter' for row in selected),selected
        return selected if len(selected)==count and all(row['state']=='Succeeded' for row in selected) else None
    rows=hosts.wait(completed,120);elapsed=time.monotonic()-started;after=sql()
    from datetime import datetime
    latencies=sorted((datetime.fromisoformat(row['finishedUtc'])-datetime.fromisoformat(row['createdUtc'])).total_seconds()*1000 for row in rows)
    # Timers in this script are test measurement windows, not production polling loops.
    time.sleep(warmup);idle_before=sql();started_idle=time.monotonic();time.sleep(idle);idle_elapsed=time.monotonic()-started_idle;idle_after=sql()
    idle_delta=delta(idle_before,idle_after);idle_rate=sum(sum(values.values()) for values in idle_delta.values())/idle_elapsed
    report=dict(provider=hosts.provider,jobs=count,seconds=elapsed,jobsPerSecond=count/elapsed,p50EndToEndMs=statistics.median(latencies),p95EndToEndMs=latencies[max(0,int(len(latencies)*.95)-1)],loadCommands=delta(before,after),idleSeconds=idle_elapsed,idleCommands=idle_delta,idleClusterCommandsPerSecond=idle_rate,notes='Two local processes, 8 slots each. Includes API/audit/validation traffic; shared development hardware. EF command counts exclude transaction protocol round trips. Not a production sizing result.')
    assert idle_rate<3.5,report
    target=ROOT/'artifacts'/('load-'+hosts.provider.lower()+'.json');target.write_text(json.dumps(report,indent=2)+'\n')
    print('PASS: '+str(target)+'; idle cluster SQL commands/s='+str(round(idle_rate,3)))
