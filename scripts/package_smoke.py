#!/usr/bin/env python3
"""Exercise dashboard package APIs on a real host, including activation without restart."""
import json, os, pathlib, secrets, socket, subprocess, tempfile, time, urllib.request, urllib.error
root=pathlib.Path(__file__).resolve().parents[1]
dotnet=os.environ.get('DOTNET_COMMAND','dotnet')
with socket.socket() as sock:sock.bind(('127.0.0.1',0));port=sock.getsockname()[1]
base=f'http://127.0.0.1:{port}';key=secrets.token_hex(32)
with tempfile.TemporaryDirectory() as tmp:
    env=dict(os.environ,ASPNETCORE_ENVIRONMENT='Development',ASPNETCORE_URLS=base,Security__ApiKey=key,
        Security__KeyDirectory=tmp+'/keys',Storage__Initialize='true',Storage__Provider='Sqlite',Storage__ConnectionString='Data Source='+tmp+'/packages.db',
        Packages__CacheDirectory=tmp+'/package-cache',Runner__PollMilliseconds='100')
    if os.environ.get('SMOKE_MSSQL') and os.environ.get('SMOKE_POSTGRES'):raise ValueError('Select one smoke storage provider')
    if os.environ.get('SMOKE_MSSQL'):env.update(Storage__Provider='Mssql',Storage__ConnectionString=os.environ['SMOKE_MSSQL'])
    if os.environ.get('SMOKE_POSTGRES'):env.update(Storage__Provider='Postgres',Storage__ConnectionString=os.environ['SMOKE_POSTGRES'])
    host=pathlib.Path(os.environ.get('SMOKE_HOST_DLL',root/'src/Runner.Host/bin/Release/net10.0/Runner.Host.dll')).resolve()
    cwd=host.parent if 'SMOKE_HOST_DLL' in os.environ else root/'src/Runner.Host'
    with open(tmp+'/host.log','w+') as log:
        proc=subprocess.Popen([dotnet,str(host)],cwd=cwd,env=env,stdout=log,stderr=log)
        def request(path,method='GET',body=None,content_type='application/json',auth=True):
            headers={'X-Runner-Request':'1','Content-Type':content_type}
            if auth:headers['X-Runner-Key']=key
            if body is not None and not isinstance(body,bytes):body=json.dumps(body).encode()
            try:
                with urllib.request.urlopen(urllib.request.Request(base+path,data=body,method=method,headers=headers),timeout=20) as r:
                    data=r.read();return r.status,json.loads(data) if data else None
            except urllib.error.HTTPError as e:
                data=e.read();return e.code,json.loads(data) if data else None
        fixture=root/'src/Runner.SamplePlugin/bin/Release/net10.0'
        files={f.name:f.read_bytes() for f in fixture.iterdir() if f.is_file() and f.name.startswith('Runner.SamplePlugin.')}
        files['assets/example.txt']=b'large upload asset\n'*16000
        package='smoke.echo.'+secrets.token_hex(4)
        def install(version,content):
            boundary='runner'+secrets.token_hex(12);parts=[]
            for name,value in {'packageId':package,'version':version,'entryAssembly':'Runner.SamplePlugin.dll'}.items():
                parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n'.encode())
            for name,data in content.items():
                parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="files"; filename="{name}"\r\nContent-Type: application/octet-stream\r\n\r\n'.encode()+data+b'\r\n')
            parts.append(f'--{boundary}--\r\n'.encode())
            return request('/api/packages/install','POST',b''.join(parts),'multipart/form-data; boundary='+boundary)
        def activate(version,enabled=True,revision=None):
            channel=request('/api/packages/'+package+'/channel')[1]
            return request('/api/packages/'+package+'/activation','POST',{'version':version,'enabled':enabled,'revision':channel['revision'] if revision is None else revision})
        def submit(version=None,delay=0,name='package-test'):
            return request('/api/jobs','POST',{'type':'runner.plugin','payload':{'packageId':package,'version':version,'payload':{'name':name}},'delaySeconds':delay})
        def wait_job(id):
            deadline=time.time()+20
            while time.time()<deadline:
                code,job=request('/api/jobs/'+id);assert code==200,job
                if job['state']=='Succeeded':return job
                if job['state']=='DeadLetter':raise AssertionError(job)
                time.sleep(.2)
            raise AssertionError(job)
        try:
            for _ in range(150):
                if proc.poll() is not None:raise RuntimeError('Host exited before readiness')
                try:
                    if request('/health/ready',auth=False)[0]==200:break
                except (OSError,urllib.error.URLError):pass
                time.sleep(.2)
            else:raise RuntimeError('Readiness timeout')
            assert request('/api/packages',auth=False)[0]==401
            assert request('/api/packages/settings')[1]['enabled']
            code,v1=install('1.0.0',files);assert code==201,v1
            assert v1['entryType']=='Runner.SamplePlugin.EchoPlugin'
            assert install('1.0.0',files)[1]['digest']==v1['digest']
            assert install('1.0.0',dict(files,extra=b'different content'))[0]==409
            code,v2=install('2.0.0',dict(files,extra=b'v2'));assert code==201,v2
            assert v1['digest']!=v2['digest']
            assert install('3.0.0',dict(files,**{'../escape.txt':b'bad'}))[0]==400
            bad=dict(files);bad['Runner.SamplePlugin.dll']=b'not an assembly'
            assert install('3.0.0',bad)[0]==400
            assert submit()[0]==400 # Explicit activation required.
            assert activate('1.0.0')[0]==204
            code,pinned=submit(delay=2);assert code==201,pinned
            assert pinned['packageVersion']=='1.0.0'
            assert activate('2.0.0',revision=0)[0]==409
            assert activate('2.0.0')[0]==204
            result=wait_job(pinned['id']);assert result['packageVersion']=='1.0.0'
            assert json.loads(result['resultJson'])['greeting']=='Hello, package-test!'
            logs=request('/api/jobs/'+pinned['id']+'/logs')[1]
            assert any('Echo plugin business log' in row['message'] for row in logs),logs
            code,paused=submit('1.0.0',delay=2,name='paused');assert code==201
            assert activate(None,False)[0]==204
            time.sleep(3)
            state=request('/api/jobs/'+paused['id'])[1];assert state['state']=='Pending' and state['attempts']==0,state
            assert activate('2.0.0')[0]==204;wait_job(paused['id'])
            code,schedule=request('/api/schedules','POST',{'type':'runner.plugin','payload':{'packageId':package,'version':'1.0.0','payload':{'name':'scheduled'}},'intervalSeconds':10});assert code==201,schedule
            baseline=next(x for x in request('/api/schedules')[1] if x['id']==schedule['id'])['nextUtc']
            assert activate(None,False)[0]==204;time.sleep(11)
            retained=next(x for x in request('/api/schedules')[1] if x['id']==schedule['id'])
            assert retained['nextUtc']==baseline
            assert activate('1.0.0')[0]==204 # Roll back and resume.
            deadline=time.time()+15
            while time.time()<deadline:
                for candidate in request('/api/jobs')[1]:
                    if candidate['type']=='runner.plugin' and candidate['state']=='Succeeded':
                        detail=request('/api/jobs/'+candidate['id'])[1]
                        if detail['packageId']==package and json.loads(detail['resultJson'])['greeting']=='Hello, scheduled!':break
                else:time.sleep(.3);continue
                break
            else:raise AssertionError('Resumed package schedule did not execute')
            assert request('/api/schedules/'+schedule['id'],'DELETE')[0]==204
            history=request('/api/packages/'+package+'/history')[1];assert history[0]['version']=='1.0.0'
            assert len(request('/api/packages/'+package+'/versions')[1])==2
            assert proc.poll() is None
            print('PASS: live multipart DLL/folder upload (>128 KiB), entry contract validation, immutable versions, activation CAS, version pinning, pause/resume, schedule coalescing, rollback/history; no host restart')
        except:
            log.flush();log.seek(0);print(log.read());raise
        finally:
            proc.terminate()
            try:proc.wait(timeout=35)
            except subprocess.TimeoutExpired:proc.kill();proc.wait()
