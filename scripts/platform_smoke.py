#!/usr/bin/env python3
"""Real hosts: RBAC/audit, DAGs, calendar, placement, cluster limits, drain and process isolation."""
import datetime,json,secrets,time
from zoneinfo import ZoneInfo
from host_fixture import Hosts,maximum_overlap
with Hosts() as hosts:
    a=hosts.start('platform-a',Runner__Tags__0='primary',Runner__JobTimeoutSeconds='6',Runner__Concurrency='1')
    hosts.start('platform-b',Runner__Tags__0='secondary',Runner__JobTimeoutSeconds='6',Runner__Concurrency='1')
    assert hosts.api('/api/jobs',role=None)[0]==401
    assert hosts.api('/api/jobs',role='Viewer')[0]==200
    assert hosts.api('/api/jobs','POST',{'type':'sample.echo','payload':{}},role='Viewer')[0]==403
    assert hosts.api('/api/functions','POST',{'name':'denied','version':1,'source':'return 1;'},role='Operator')[0]==403
    code,row=hosts.api('/api/jobs','POST',{'type':'sample.echo','payload':{}},role='Operator');assert code==201,row
    hosts.job(row['id'])
    audit=hosts.api('/api/audit')[1];assert any(x['actor']=='viewer' and x['statusCode']==403 for x in audit)
    assert any(x['actor']=='operator' and x['statusCode']==201 for x in audit)
    assert hosts.api('/api/audit',role='Operator')[0]==403
    steps=[{'key':'a','job':{'type':'sample.echo','payload':{'delayMs':300}}},{'key':'b','dependsOn':['a'],'job':{'type':'sample.echo','payload':{}}},{'key':'c','dependsOn':['a','b'],'job':{'type':'sample.echo','payload':{}}}]
    code,workflow=hosts.api('/api/workflows','POST',{'name':'ordered','steps':steps});assert code==201,workflow
    jobs={key:hosts.job(value) for key,value in workflow['jobs'].items()}
    for child,parent in [('b','a'),('c','b')]:
        attempt=hosts.api('/api/jobs/'+jobs[child]['id']+'/attempts')[1][0]
        assert datetime.datetime.fromisoformat(attempt['startedUtc'])>=datetime.datetime.fromisoformat(jobs[parent]['finishedUtc'])
    failing=[{'key':'bad','job':{'type':'sample.echo','payload':{'fail':True},'maxAttempts':1}},{'key':'blocked','dependsOn':['bad'],'job':{'type':'sample.echo','payload':{}}},{'key':'independent','job':{'type':'sample.echo','payload':{}}}]
    code,workflow=hosts.api('/api/workflows','POST',{'name':'failure','steps':failing});assert code==201,workflow
    hosts.job(workflow['jobs']['bad'],'DeadLetter');child=hosts.job(workflow['jobs']['blocked'],'Cancelled');assert child['errorCode']=='dependency_failed';hosts.job(workflow['jobs']['independent'])
    assert hosts.api('/api/workflows','POST',{'name':'cycle','steps':[dict(steps[0],dependsOn=['c']),*steps[1:]]})[0]==400
    code,schedule=hosts.api('/api/schedules','POST',{'type':'sample.echo','payload':{},'cron':'0 9 * * 1-5','timeZoneId':'Australia/Sydney','misfirePolicy':'CatchUp'});assert code==201,schedule
    assert datetime.datetime.fromisoformat(schedule['nextUtc']).astimezone(ZoneInfo('Australia/Sydney')).hour==9
    assert hosts.api('/api/schedules/'+schedule['id'],'DELETE')[0]==204
    assert hosts.api('/api/schedules','POST',{'type':'sample.echo','payload':{},'cron':'* * * * *','timeZoneId':'Missing/Zone'})[0]==400
    package='platform.limit.'+secrets.token_hex(4);hosts.install(package);hosts.activate(package,limit=2)
    rows=[hosts.job(job) for job in [hosts.plugin(package,1200) for _ in range(8)]]
    assert maximum_overlap(rows)==2,maximum_overlap(rows)
    assert len({row['worker'].split(':')[0] for row in rows})==2
    specific='platform.primary.'+secrets.token_hex(4);hosts.install(specific,{'requiredTags':['primary']})
    running=hosts.plugin(specific,1800);row=hosts.job(running,'Running');assert row['worker'].startswith('platform-a:')
    node=next(n for n in hosts.api('/api/nodes')[1] if n['name']=='platform-a')
    assert hosts.api('/api/nodes/'+node['id']+'/drain','POST',{'enabled':True,'deadlineSeconds':30})[0]==202
    hosts.job(running)
    hosts.wait(lambda:any(n['id']==node['id'] and n['state']=='Drained' for n in hosts.api('/api/nodes')[1]))
    paused=hosts.plugin(specific);time.sleep(1.5)
    row=hosts.api('/api/jobs/'+paused)[1];assert row['state']=='Pending' and row['attempts']==0,row
    peer=hosts.job(hosts.submit());assert peer['worker'].startswith('platform-b:'),peer
    assert hosts.api('/api/nodes/'+node['id']+'/drain','POST',{'enabled':False})[0]==202
    assert hosts.job(paused)['worker'].startswith('platform-a:')
    incompatible='platform.missing.'+secrets.token_hex(4);hosts.install(incompatible,{'requiredTags':['no-node-has-this']})
    pending=hosts.plugin(incompatible)
    report=hosts.api('/api/packages/'+incompatible+'/versions/1.0.0/placement')[1]
    assert all(n['reasons'] for n in report['nodes'])
    hosts.job(hosts.submit());row=hosts.api('/api/jobs/'+pending)[1];assert row['attempts']==0 and row['state']=='Pending'
    assert hosts.api('/api/jobs/'+pending+'/cancel','POST')[0]==202
    # The child can crash or ignore cancellation without taking down the web/worker host.
    hosts.job(pending,'Cancelled')
    crashed=hosts.script('Environment.Exit(23); return null;');row=hosts.job(crashed,'DeadLetter');assert row['errorCode']=='executor_process_failed',row
    hung=hosts.script('while (true) { Thread.Sleep(100); }');row=hosts.job(hung,'DeadLetter');assert row['errorCode']=='cancelled_or_timed_out',row
    probe=hosts.script('return new { runnerKey = Environment.GetEnvironmentVariable("Security__ApiKey"), runnerStore = Environment.GetEnvironmentVariable("Storage__ConnectionString") };')
    result=json.loads(hosts.job(probe)['resultJson']);assert result=={'runnerKey':None,'runnerStore':None},result
    hosts.job(hosts.submit())
    assert all(hosts.api('/health/live',base=n['url'])[0]==200 for n in hosts.nodes.values())
    admission=hosts.start('admission-api',Runner__WorkerEnabled='false',Runner__MaxPendingJobs='100')
    for _ in range(100):
        code,row=hosts.api('/api/jobs','POST',{'type':'sample.echo','payload':{},'delaySeconds':86400},base=admission);assert code==201,(code,row)
    for _ in range(100):
        code,row=hosts.api('/api/jobs','POST',{'type':'sample.echo','payload':{},'delaySeconds':86400},base=admission)
        if code==429:break
        assert code==409,(code,row)
    else:raise AssertionError('Submission rate limit never rejected the burst')
    print('PASS: two-host RBAC/audit, DAG success/failure, calendar/timezone validation, global limit=2, compatible placement, drain/resume, child crash/hard timeout and credential isolation, shared outstanding capacity and HTTP 429 backpressure.')
