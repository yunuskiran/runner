#!/usr/bin/env python3
"""Black-box smoke test: launch a real host with isolated state, assert behavior, clean up."""
import http.cookiejar, json, os, pathlib, re, secrets, socket, subprocess, tempfile, time, urllib.request, urllib.error
root = pathlib.Path(__file__).resolve().parents[1]
dotnet = os.environ.get('DOTNET_COMMAND', 'dotnet')
with socket.socket() as sock:
    sock.bind(('127.0.0.1', 0)); port = sock.getsockname()[1]
base = f'http://127.0.0.1:{port}'
key = secrets.token_hex(32)
with tempfile.TemporaryDirectory() as tmp:
    env = dict(os.environ, ASPNETCORE_ENVIRONMENT='Development', ASPNETCORE_URLS=base,
               Security__ApiKey=key, Security__KeyDirectory=tmp+'/keys', Storage__Initialize='true',
               Storage__ConnectionString='Data Source='+tmp+'/jobs.db;Default Timeout=30', Runner__PollMilliseconds='100',
               Scripting__Enabled=os.environ.get('SMOKE_SCRIPTING', 'false'), Runner__LeaseSeconds='12', Runner__HeartbeatSeconds='2', Runner__Concurrency='2')
    if os.environ.get('SMOKE_POSTGRES'):
        env.update(Storage__Provider='Postgres', Storage__ConnectionString=os.environ['SMOKE_POSTGRES'])
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
        assert set(request('/api/handlers')[1]) == ({'sample.echo', 'runner.script'} if scripting else {'sample.echo'})
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
                if candidates:
                    assert json.loads(request('/api/jobs/'+candidates[0]['id'])[1]['resultJson'])['total']==42
                    break
                time.sleep(.3)
            else: raise AssertionError('Script schedule did not complete')
            assert request('/api/schedules/'+scheduled['id'],'DELETE')[0]==204
            print('PASS: script registration, compile diagnostics, immutable version pinning, JSON results, disable/enable, scheduled scripts')
        assert request('/api/jobs','POST',{'type':'missing','payload':{}})[0]==400
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
            if any(j['type']=='sample.echo' and j['id'] not in [job['id'],failed['id'],future['id'],running['id']] and j['state']=='Succeeded' for j in jobs): break
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
