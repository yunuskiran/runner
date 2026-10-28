#!/usr/bin/env python3
"""Black-box smoke test: launch a real host with isolated state, assert behavior, clean up."""
import http.cookiejar, json, os, pathlib, re, secrets, socket, subprocess, tempfile, time, urllib.request, urllib.error
import http.server, threading, hmac, hashlib
root = pathlib.Path(__file__).resolve().parents[1]
dotnet = os.environ.get('DOTNET_COMMAND', 'dotnet')
with socket.socket() as sock:
    sock.bind(('127.0.0.1', 0)); port = sock.getsockname()[1]
base = f'http://127.0.0.1:{port}'
key = secrets.token_hex(32)
operations = os.environ.get('SMOKE_OPERATIONS', 'false').lower() == 'true'
deliveries = []
class AlertReceiver(http.server.BaseHTTPRequestHandler):
    def do_POST(self):
        body=self.rfile.read(int(self.headers['Content-Length']))
        expected=hmac.new(b'smoke-local-only',body,hashlib.sha256).hexdigest()
        if not hmac.compare_digest(expected,self.headers.get('X-Runner-Signature','')):
            self.send_response(400);self.end_headers();return
        deliveries.append(json.loads(body))
        self.send_response(500 if len(deliveries)==1 else 204);self.end_headers()
    def log_message(self,*args):pass
receiver=http.server.ThreadingHTTPServer(('127.0.0.1',0),AlertReceiver) if operations else None
if receiver: threading.Thread(target=receiver.serve_forever,daemon=True).start()
with tempfile.TemporaryDirectory() as tmp:
    env = dict(os.environ, ASPNETCORE_ENVIRONMENT='Development', ASPNETCORE_URLS=base,
               Security__ApiKey=key, Security__KeyDirectory=tmp+'/keys', Storage__Initialize='true',
               Storage__ConnectionString='Data Source='+tmp+'/jobs.db;Default Timeout=30', Runner__PollMilliseconds='100',
               Scripting__Enabled=os.environ.get('SMOKE_SCRIPTING', 'false'), Runner__LeaseSeconds='12', Runner__HeartbeatSeconds='2', Runner__Concurrency='2')
    if os.environ.get('SMOKE_POSTGRES'):
        env.update(Storage__Provider='Postgres', Storage__ConnectionString=os.environ['SMOKE_POSTGRES'])
    if operations:
        env.update(Operations__Applications__echo__FileName='/usr/bin/printf',Operations__Applications__echo__Arguments__0='%s',Operations__Applications__echo__AllowAdditionalArguments='true',
                   Operations__Applications__fail__FileName='/usr/bin/python3',Operations__Applications__fail__Arguments__0='-c',Operations__Applications__fail__Arguments__1="import sys;print('central log');sys.exit(7)",
                   Alerts__WebhookUrl=f'http://127.0.0.1:{receiver.server_port}/alerts',Alerts__SigningKey='smoke-local-only')
        for provider,variable,name in [('Postgres','TEST_DATABASE_POSTGRES','postgres'),('Mssql','TEST_DATABASE_MSSQL','mssql')]:
            if os.environ.get(variable):env.update({f'Operations__Connections__{name}__Provider':provider,f'Operations__Connections__{name}__ConnectionString':os.environ[variable]})
    log = open(tmp+'/host.log', 'w+')
    host = pathlib.Path(os.environ.get('SMOKE_HOST_DLL', root/'src/Runner.Host/bin/Release/net10.0/Runner.Host.dll')).resolve()
    working = host.parent if 'SMOKE_HOST_DLL' in os.environ else root/'src/Runner.Host'
    proc = subprocess.Popen([dotnet, str(host)], cwd=working, env=env, stdout=log, stderr=log)
    def request(path, method='GET', body=None, auth=True, headers=None, opener=None):
        h = {'X-Runner-Request':'1'}
        if auth: h['X-Runner-Key'] = key
        if body is not None: h['Content-Type']='application/json'; body=json.dumps(body).encode()
        h.update(headers or {})
        try:
            with (opener or urllib.request.build_opener()).open(urllib.request.Request(base+path, data=body, headers=h, method=method), timeout=5) as r:
                data=r.read().decode(); return r.status, json.loads(data) if data and 'application/json' in r.headers.get('Content-Type','') else data
        except urllib.error.HTTPError as e: return e.code, e.read().decode()
    def wait_job(job_id, state, seconds=20):
        deadline=time.time()+seconds
        while time.time()<deadline:
            code, job=request('/api/jobs/'+job_id)
            assert code==200, (code,job)
            if job['state']==state:return job
            time.sleep(.2)
        raise AssertionError(f'Expected {state}, got {job}')
    try:
        for _ in range(150):
            if proc.poll() is not None: raise RuntimeError('Host exited early')
            try:
                if request('/health/ready', auth=False)[0]==200: break
            except (OSError, urllib.error.URLError): pass
            time.sleep(.2)
        else: raise RuntimeError('Host did not become ready')
        assert request('/api/jobs',auth=False)[0]==401
        assert request('/api/jobs',headers={'X-Runner-Key':'bad'})[0]==401
        assert request('/api/jobs','POST',{},headers={'X-Runner-Request':''})[0]==400
        scripting = os.environ.get('SMOKE_SCRIPTING', 'false').lower() == 'true'
        assert set(request('/api/handlers')[1]) == ({'sample.echo', 'runner.database', 'runner.process', 'runner.plugin', 'runner.script'} if scripting else {'sample.echo', 'runner.database', 'runner.process', 'runner.plugin'})
        assert request('/api/functions', auth=False)[0] == 401
        assert request('/api/scripting')[1]['enabled'] == scripting
        if not scripting: assert request('/api/functions')[0] == 404
        if scripting:
            name='smoke.'+secrets.token_hex(8)
            invalid=request('/api/functions','POST',{'name':name,'version':1,'source':'return missing;'})
            assert invalid[0]==400 and 'diagnostics' in invalid[1],invalid
            code, function=request('/api/functions','POST',{'name':name,'version':1,'source':'return new { total = Arguments.GetProperty(\"quantity\").GetInt32() * 7, jobId = JobId };'})
            assert code==201,function
            assert request('/api/functions','POST',{'name':name,'version':1,'source':'return 99;'})[0]==409
            script_body={'type':'runner.script','payload':{'functionId':function['id'],'arguments':{'quantity':6}},'maxAttempts':1}
            code, execution=request('/api/jobs','POST',dict(script_body,delaySeconds=2)); assert code==201,execution
            assert request('/api/functions','POST',{'name':name,'version':2,'source':'return 999;'})[0]==201
            result=wait_job(execution['id'],'Succeeded')
            assert json.loads(result['resultJson'])['total']==42,result
            assert request('/api/functions/'+function['id'],'PATCH',{'enabled':False})[0]==204
            assert request('/api/jobs','POST',script_body)[0]==400
            assert request('/api/functions/'+function['id'],'PATCH',{'enabled':True})[0]==204
            code, scheduled=request('/api/schedules','POST',{'type':'runner.script','payload':script_body['payload'],'intervalSeconds':10})
            assert code==201,scheduled
            deadline=time.time()+20
            while time.time()<deadline:
                candidates=[j for j in request('/api/jobs')[1] if j['type']=='runner.script' and j['id']!=execution['id'] and j['state']=='Succeeded']
                selected=None
                for candidate in candidates:
                    detail=request('/api/jobs/'+candidate['id'])[1]
                    if (detail.get('idempotencyKey') or '').startswith('schedule:'+scheduled['id']+':'):
                        selected=detail;break
                if selected:
                    assert json.loads(selected['resultJson'])['total']==42
                    break
                time.sleep(.3)
            else: raise AssertionError('Script schedule did not complete')
            assert request('/api/schedules/'+scheduled['id'],'DELETE')[0]==204
            print('PASS: script registration, compile diagnostics, immutable version pinning, JSON results, disable/enable, scheduled scripts')
        assert request('/api/jobs','POST',{'type':'missing','payload':{}})[0]==400
        if operations:
            code,resources=request('/api/resources');assert code==200,resources
            assert 'connectionString' not in json.dumps(resources)
            assert request('/api/jobs','POST',{'type':'runner.process','payload':{'application':'/bin/sh'}})[0]==400
            code,run=request('/api/jobs','POST',{'type':'runner.process','payload':{'application':'echo','arguments':['literal; $(no-shell)']}})
            assert code==201,run
            result=wait_job(run['id'],'Succeeded'); assert 'literal; $(no-shell)' in result['resultJson']
            logs=request('/api/jobs/'+run['id']+'/logs')[1];assert any('literal; $(no-shell)' in row['message'] for row in logs)
            assert request('/api/jobs/'+run['id']+'/attempts')[1][0]['outcome']=='Succeeded'
            code,failed_process=request('/api/jobs','POST',{'type':'runner.process','payload':{'application':'fail'},'maxAttempts':1});assert code==201
            wait_job(failed_process['id'],'DeadLetter')
            deadline=time.time()+30
            while time.time()<deadline:
                alerts=request('/api/alerts')[1];alert=next((a for a in alerts if a['jobId']==failed_process['id']),None)
                if alert and alert['deliveryState']=='Delivered':break
                time.sleep(.3)
            else:raise AssertionError('Alert retry/delivery did not complete')
            assert alert['deliveryAttempts']==2,alert
            assert request('/api/alerts/'+alert['id']+'/acknowledge','POST')[0]==204
            assert deliveries[0]['id']==deliveries[1]['id']
            for connection in resources['connections']:
                code,query=request('/api/jobs','POST',{'type':'runner.database','payload':{'connection':connection['name'],'commandText':'SELECT CAST(@value AS integer) AS value','parameters':{'value':42},'parameterTypes':{'value':'Int32'},'mode':'query'}})
                assert code==201,query
                result=wait_job(query['id'],'Succeeded'); assert json.loads(result['resultJson'])['Rows'][0]['value']==42,result
            report=request('/api/reports/summary')[1]
            assert any(r['jobType']=='runner.process' and r['outcome']=='DeadLetter' for r in report),report
            code,csv=request('/api/reports/summary.csv'); assert code==200 and 'runner.process' in csv
            if scripting and resources['connections']:
                code,function=request('/api/functions','POST',{'name':'helper.'+secrets.token_hex(8),'version':1,'source':f'await Log.InfoAsync("business event", CancellationToken); return await Db.ExecuteAsync("{resources["connections"][0]["name"]}", "SELECT @value AS value", new {{ value = 42 }}, "query", CancellationToken);'})
                assert code==201,function
                code,job=request('/api/jobs','POST',{'type':'runner.script','payload':{'functionId':function['id'],'arguments':{}}})
                result=wait_job(job['id'],'Succeeded');assert json.loads(result['resultJson'])['Rows'][0]['value']==42
                assert any(row['message']=='business event' for row in request('/api/jobs/'+job['id']+'/logs')[1])
            print('PASS: ready database/process jobs, named resources, centralized logs, reports/CSV, durable signed webhook retry, acknowledgement, script business helpers')
        body={'type':'sample.echo','payload':{'delayMs':100},'idempotencyKey':'smoke-'+secrets.token_hex(8)}
        code, job=request('/api/jobs','POST',body); assert code==201,job
        assert request('/api/jobs','POST',body)[1]['id']==job['id']
        assert request('/api/jobs','POST',dict(body,payload={'delayMs':200}))[0]==409
        done=wait_job(job['id'],'Succeeded'); assert done['attempts']==1
        assert done['createdUtc'].endswith('Z'), done['createdUtc']
        _, failed=request('/api/jobs','POST',{'type':'sample.echo','payload':{'fail':True},'maxAttempts':2})
        dead=wait_job(failed['id'],'DeadLetter'); assert dead['attempts']==2
        assert request('/api/jobs/'+failed['id']+'/retry','POST')[0]==202
        wait_job(failed['id'],'DeadLetter')
        _, future=request('/api/jobs','POST',{'type':'sample.echo','payload':{},'delaySeconds':3600})
        assert request('/api/jobs/'+future['id']+'/cancel','POST')[0]==202
        wait_job(future['id'],'Cancelled')
        _, running=request('/api/jobs','POST',{'type':'sample.echo','payload':{'delayMs':30000}})
        wait_job(running['id'],'Running')
        assert request('/api/jobs/'+running['id']+'/cancel','POST')[0]==202
        wait_job(running['id'],'Cancelled')
        _, schedule=request('/api/schedules','POST',{'type':'sample.echo','payload':{},'intervalSeconds':10})
        deadline=time.time()+20
        while time.time()<deadline:
            jobs=request('/api/jobs')[1]
            matching=False
            for candidate in jobs:
                if candidate['type']=='sample.echo' and candidate['state']=='Succeeded':
                    detail=request('/api/jobs/'+candidate['id'])[1]
                    if (detail.get('idempotencyKey') or '').startswith('schedule:'+schedule['id']+':'):
                        matching=True;break
            if matching:break
            time.sleep(.3)
        else: raise AssertionError('Schedule did not produce a successful job')
        assert request('/api/schedules/'+schedule['id'],'DELETE')[0]==204
        # Browser cookie session and antiforgery sign-in flow.
        cookies=http.cookiejar.CookieJar(); browser=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(cookies))
        _, login=request('/login',auth=False,opener=browser)
        name, token=re.search(r'name="([^"]+)" value="([^"]+)"',login).groups()
        from urllib.parse import urlencode
        form=urlencode({name:token,'key':key}).encode()
        with browser.open(urllib.request.Request(base+'/login',data=form,headers={'Content-Type':'application/x-www-form-urlencoded'})) as r:
            assert 'Worker overview' in r.read().decode()
        assert request('/api/stats',auth=False,opener=browser)[0]==200
        assert request('/api/logout','POST',auth=False,opener=browser)[0]==204
        assert request('/api/stats',auth=False,opener=browser)[0]==401
        assert 'Operations, under control.' in request('/app.js',auth=False)[1] # protected script redirects to login
        print('PASS: auth, CSRF guard, plugin execution, idempotency, retry/dead-letter, replay, cancellation, scheduling, dashboard login/logout')
    except Exception:
        log.flush();log.seek(0); print(log.read()); raise
    finally:
        proc.terminate()
        try: proc.wait(timeout=35)
        except subprocess.TimeoutExpired: proc.kill();proc.wait()
        log.close()
        if receiver:receiver.shutdown();receiver.server_close()
