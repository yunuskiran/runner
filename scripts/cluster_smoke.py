#!/usr/bin/env python3
"""Two independent worker hosts share a DB: sequential/parallel plugins and crash takeover."""
import os,pathlib,socket,tempfile,subprocess,secrets,time,json,urllib.request,urllib.error,datetime
root=pathlib.Path(__file__).resolve().parents[1];dotnet=os.environ.get('DOTNET_COMMAND','dotnet')
host=pathlib.Path(os.environ.get('SMOKE_HOST_DLL',root/'src/Runner.Host/bin/Release/net10.0/Runner.Host.dll')).resolve()
cwd=host.parent if 'SMOKE_HOST_DLL' in os.environ else root/'src/Runner.Host'
key=secrets.token_hex(32)
def free_port():
    with socket.socket() as s:s.bind(('127.0.0.1',0));return s.getsockname()[1]
with tempfile.TemporaryDirectory() as tmp:
    connection='Data Source='+tmp+'/cluster.db;Default Timeout=30';provider='Sqlite'
    if os.environ.get('SMOKE_POSTGRES'):provider='Postgres';connection=os.environ['SMOKE_POSTGRES']
    if os.environ.get('SMOKE_MSSQL'):provider='Mssql';connection=os.environ['SMOKE_MSSQL']
    if os.environ.get('SMOKE_MSSQL') and os.environ.get('SMOKE_POSTGRES'):raise ValueError('Choose one store')
    nodes={};logs=[]
    def start(name):
        url='http://127.0.0.1:'+str(free_port())
        env=dict(os.environ,ASPNETCORE_ENVIRONMENT='Development',ASPNETCORE_URLS=url,Security__ApiKey=key,Security__KeyDirectory=tmp+'/'+name+'/keys',
            Storage__Provider=provider,Storage__ConnectionString=connection,Storage__Initialize='true',Runner__InstanceName=name,
            Runner__Concurrency='2',Runner__PollMilliseconds='100',Runner__MaxIdlePollSeconds='1',Runner__NodeHeartbeatSeconds='1',Runner__LeaseSeconds='10',Runner__HeartbeatSeconds='2',
            Packages__CacheDirectory=tmp+'/'+name+'/packages')
        log=open(tmp+'/'+name+'.log','w+');logs.append(log)
        process=subprocess.Popen([dotnet,str(host)],cwd=cwd,env=env,stdout=log,stderr=log)
        nodes[name]=(process,url)
        for _ in range(150):
            if process.poll() is not None:raise RuntimeError(name+' exited')
            try:
                with urllib.request.urlopen(url+'/health/ready',timeout=1) as r:
                    if r.status==200:return
            except OSError:pass
            time.sleep(.2)
        raise RuntimeError(name+' not ready')
    def api(path,method='GET',body=None,base=None,content='application/json'):
        base=base or next(url for process,url in nodes.values() if process.poll() is None)
        headers={'X-Runner-Key':key,'X-Runner-Request':'1','Content-Type':content}
        if body is not None and not isinstance(body,bytes):body=json.dumps(body).encode()
        try:
            with urllib.request.urlopen(urllib.request.Request(base+path,data=body,method=method,headers=headers),timeout=20) as r:
                data=r.read();return r.status,json.loads(data) if data else None
        except urllib.error.HTTPError as e:
            data=e.read();return e.code,json.loads(data) if data else None
    package='cluster.test.'+secrets.token_hex(4)
    def install():
        boundary='runner'+secrets.token_hex(12);parts=[]
        for name,value in {'packageId':package,'version':'1.0.0','entryAssembly':'Plugin.FromNuGet.dll'}.items():
            parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n'.encode())
        for file in (root/'artifacts/sdk-consumer').iterdir():
            if file.is_file():parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="files"; filename="{file.name}"\r\n\r\n'.encode()+file.read_bytes()+b'\r\n')
        parts.append(f'--{boundary}--\r\n'.encode())
        return api('/api/packages/install','POST',b''.join(parts),content='multipart/form-data; boundary='+boundary)
    def mode(value):
        channel=api('/api/packages/'+package+'/channel')[1]
        return api('/api/packages/'+package+'/activation','POST',{'version':'1.0.0','enabled':True,'revision':channel['revision'],'executionMode':value})
    def submit(delay=500):
        code,job=api('/api/jobs','POST',{'type':'runner.plugin','payload':{'packageId':package,'version':'1.0.0','payload':{'delayMs':delay}},'maxAttempts':3})
        assert code==201,job;return job['id']
    def done(ids,seconds=30):
        deadline=time.time()+seconds
        while time.time()<deadline:
            rows=[api('/api/jobs/'+id)[1] for id in ids]
            if all(r['state']=='Succeeded' for r in rows):return rows
            assert not any(r['state']=='DeadLetter' for r in rows),rows
            time.sleep(.1)
        raise AssertionError(rows)
    def intervals(rows):
        return sorted((datetime.datetime.fromisoformat(result['startedUtc']),datetime.datetime.fromisoformat(result['finishedUtc'])) for result in (json.loads(r['resultJson']) for r in rows))
    try:
        start('node-a');start('node-b')
        code,info=install();assert code==201,info;assert info['defaultExecutionMode']=='Sequential',info
        channel=api('/api/packages/'+package+'/channel')[1];assert channel['executionMode']=='Sequential'
        assert mode('Sequential')[0]==204
        sequential=done([submit(350) for _ in range(6)])
        points=intervals(sequential);assert all(points[n][0]>=points[n-1][1] for n in range(1,len(points))),points
        assert mode('Parallel')[0]==204
        parallel=done([submit(1200) for _ in range(12)])
        points=intervals(parallel);assert any(points[n][0]<points[n-1][1] for n in range(1,len(points))),points
        assert len({r['worker'].split(':')[0] for r in parallel})==2
        assert mode('Sequential')[0]==204
        crashed=submit(2000)
        deadline=time.time()+10
        while time.time()<deadline:
            row=api('/api/jobs/'+crashed)[1]
            if row['state']=='Running':break
            time.sleep(.05)
        else:raise AssertionError('No worker claimed crash test')
        assert mode('Parallel')[0]==409 # Active execution must drain before policy changes.
        owner=row['worker'].split(':')[0];nodes[owner][0].kill();nodes[owner][0].wait()
        recovered=done([crashed],30)[0]
        assert recovered['attempts']==2,recovered
        assert recovered['worker'].split(':')[0]!=owner
        print('PASS: two host processes, plugin attribute default, no sequential overlap, parallel overlap on both nodes, active-mode guard, and lease takeover after worker death.')
    except:
        for log in logs:log.flush();log.seek(0);print(log.read())
        raise
    finally:
        for process,url in nodes.values():
            if process.poll() is None:
                process.terminate()
                try:process.wait(timeout=20)
                except subprocess.TimeoutExpired:process.kill();process.wait()
        for log in logs:log.close()
