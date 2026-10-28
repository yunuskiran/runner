"""Disposable real-host fixture shared by platform, failure and load validation."""
import contextlib,datetime,json,os,pathlib,secrets,socket,subprocess,tempfile,time,urllib.error,urllib.request
ROOT=pathlib.Path(__file__).resolve().parents[1]
DOTNET=os.environ.get('DOTNET_COMMAND','dotnet')
HOST=pathlib.Path(os.environ.get('SMOKE_HOST_DLL',ROOT/'src/Runner.Host/bin/Release/net10.0/Runner.Host.dll')).resolve()
CWD=HOST.parent if 'SMOKE_HOST_DLL' in os.environ else ROOT/'src/Runner.Host'
def port():
    with socket.socket() as sock:sock.bind(('127.0.0.1',0));return sock.getsockname()[1]
class Hosts:
    def __init__(self):
        self.tmp=tempfile.TemporaryDirectory();self.nodes={};self.keys={role:secrets.token_hex(32) for role in ['Admin','Operator','Viewer']}
        self.provider='Sqlite';self.connection='Data Source='+self.tmp.name+'/platform.db;Default Timeout=30'
        if os.environ.get('SMOKE_POSTGRES'):self.provider='Postgres';self.connection=os.environ['SMOKE_POSTGRES']
        if os.environ.get('SMOKE_MSSQL'):self.provider='Mssql';self.connection=os.environ['SMOKE_MSSQL']
        if os.environ.get('SMOKE_MSSQL') and os.environ.get('SMOKE_POSTGRES'):raise ValueError('Select one provider')
    def start(self,name,**settings):
        url='http://127.0.0.1:'+str(port())
        env=dict(os.environ,ASPNETCORE_ENVIRONMENT='Development',ASPNETCORE_URLS=url,Security__ApiKey=self.keys['Admin'],
            Security__ApiKeys__operator__Key=self.keys['Operator'],Security__ApiKeys__operator__Role='Operator',
            Security__ApiKeys__viewer__Key=self.keys['Viewer'],Security__ApiKeys__viewer__Role='Viewer',
            Security__KeyDirectory=self.tmp.name+'/'+name+'/keys',Storage__Provider=self.provider,Storage__ConnectionString=self.connection,
            Storage__Initialize='true',Runner__InstanceName=name,Runner__Concurrency='4',Runner__PollMilliseconds='100',Runner__MaxIdlePollSeconds='1',
            Runner__NodeHeartbeatSeconds='1',Runner__SchedulerSeconds='1',Runner__LeaseSeconds='10',Runner__HeartbeatSeconds='2',
            Scripting__Enabled='true',Packages__CacheDirectory=self.tmp.name+'/'+name+'/packages')
        env.update(settings)
        log=open(self.tmp.name+'/'+name+'.log','w+')
        process=subprocess.Popen([DOTNET,str(HOST)],cwd=CWD,env=env,stdout=log,stderr=log)
        self.nodes[name]={'process':process,'url':url,'log':log}
        deadline=time.monotonic()+45
        while time.monotonic()<deadline:
            if process.poll() is not None:raise RuntimeError(name+' exited: '+self.log_tail(name))
            try:
                if self.api('/health/ready',base=url)[0]==200:return url
            except OSError:pass
            time.sleep(.2)
        raise RuntimeError(name+' readiness failed: '+self.log_tail(name))
    def api(self,path,method='GET',body=None,role='Admin',base=None,content='application/json'):
        base=base or next(n['url'] for n in self.nodes.values() if n['process'].poll() is None)
        headers={'X-Runner-Request':'1','Content-Type':content}
        if role:headers['X-Runner-Key']=self.keys[role]
        if body is not None and not isinstance(body,bytes):body=json.dumps(body).encode()
        try:
            with urllib.request.urlopen(urllib.request.Request(base+path,data=body,method=method,headers=headers),timeout=20) as result:
                data=result.read();return result.status,json.loads(data) if data else None
        except urllib.error.HTTPError as error:
            data=error.read()
            try:payload=json.loads(data) if data else None
            except ValueError:payload={'error':data[:200].decode(errors='replace')}
            return error.code,payload
    def wait(self,predicate,seconds=40):
        deadline=time.monotonic()+seconds;value=None
        while time.monotonic()<deadline:
            value=predicate()
            if value:return value
            time.sleep(.15)
        raise AssertionError('Timed out waiting for condition; last value: '+repr(value))
    def job(self,job_id,state='Succeeded',seconds=40):
        def check():
            code,row=self.api('/api/jobs/'+job_id)
            assert code==200,row
            if state!='DeadLetter':assert row['state']!='DeadLetter',row
            return row if row['state']==state else None
        return self.wait(check,seconds)
    def submit(self,payload=None,type='sample.echo',**options):
        code,row=self.api('/api/jobs','POST',dict(type=type,payload=payload or {},**options));assert code==201,(code,row);return row['id']
    def install(self,package,manifest=None):
        boundary='runner'+secrets.token_hex(12);parts=[]
        for name,value in {'packageId':package,'version':'1.0.0','entryAssembly':'Plugin.FromNuGet.dll'}.items():
            parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n'.encode())
        files={p.name:p.read_bytes() for p in (ROOT/'artifacts/sdk-consumer').iterdir() if p.is_file()}
        if manifest is not None:files['runner.manifest.json']=json.dumps(manifest).encode()
        for name,data in files.items():parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="files"; filename="{name}"\r\n\r\n'.encode()+data+b'\r\n')
        parts.append(f'--{boundary}--\r\n'.encode())
        code,row=self.api('/api/packages/install','POST',b''.join(parts),content='multipart/form-data; boundary='+boundary);assert code==201,(code,row)
        self.activate(package)
    def activate(self,package,mode='Parallel',limit=None):
        code,channel=self.api('/api/packages/'+package+'/channel');assert code==200
        code,row=self.api('/api/packages/'+package+'/activation','POST',dict(version='1.0.0',enabled=True,revision=channel['revision'],executionMode=mode,maxConcurrency=limit,setLimit=True));assert code==204,(code,row)
    def plugin(self,package,delay=400):return self.submit({'packageId':package,'version':'1.0.0','payload':{'delayMs':delay}},type='runner.plugin')
    def script(self,source):
        code,row=self.api('/api/functions','POST',{'name':'test.'+secrets.token_hex(6),'version':1,'source':source});assert code==201,(code,row)
        return self.submit({'functionId':row['id'],'arguments':{}},type='runner.script',maxAttempts=1)
    def log_tail(self,name):
        log=self.nodes[name]['log'];log.flush();log.seek(0,2);size=log.tell();log.seek(max(0,size-5000));return log.read()
    def close(self):
        for node in self.nodes.values():
            if node['process'].poll() is None:node['process'].terminate()
        for node in self.nodes.values():
            try:node['process'].wait(timeout=12)
            except subprocess.TimeoutExpired:node['process'].kill();node['process'].wait()
            node['log'].close()
        self.tmp.cleanup()
    def __enter__(self):return self
    def __exit__(self,kind,value,traceback):
        if kind:
            for name in self.nodes:print(name,self.log_tail(name))
        self.close()
def maximum_overlap(rows):
    events=[]
    for row in rows:
        result=json.loads(row['resultJson'])
        events.extend([(datetime.datetime.fromisoformat(result['startedUtc']),1),(datetime.datetime.fromisoformat(result['finishedUtc']),-1)])
    active=maximum=0
    for _,change in sorted(events):active+=change;maximum=max(maximum,active)
    return maximum
