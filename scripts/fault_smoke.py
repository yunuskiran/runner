#!/usr/bin/env python3
"""Cut one node's DB connections via a local TCP fault proxy; verify child termination and peer takeover."""
import os,pathlib,re,select,socket,threading,time,secrets
from host_fixture import Hosts,port
class FaultProxy:
    def __init__(self,target):
        self.target=target;self.enabled=threading.Event();self.enabled.set();self.stopped=threading.Event();self.lock=threading.Lock();self.connections=set()
        self.listener=socket.socket();self.listener.bind(('127.0.0.1',0));self.listener.listen();self.port=self.listener.getsockname()[1];self.listener.settimeout(.2)
        self.thread=threading.Thread(target=self.accept,daemon=True);self.thread.start()
    def accept(self):
        while not self.stopped.is_set():
            try:client,_=self.listener.accept()
            except socket.timeout:continue
            except OSError:return
            if not self.enabled.is_set():client.close();continue
            try:server=socket.create_connection(self.target,timeout=2)
            except OSError:client.close();continue
            pair=(client,server)
            with self.lock:self.connections.add(pair)
            threading.Thread(target=self.forward,args=(pair,),daemon=True).start()
    def forward(self,pair):
        try:
            while self.enabled.is_set() and not self.stopped.is_set():
                readable,_,_=select.select(pair,[],[],.2)
                for source in readable:
                    data=source.recv(65536)
                    if not data:return
                    (pair[1] if source is pair[0] else pair[0]).sendall(data)
        except (OSError,ValueError):pass
        finally:
            for connection in pair:connection.close()
            with self.lock:self.connections.discard(pair)
    def cut(self):
        self.enabled.clear()
        with self.lock:pairs=list(self.connections)
        for pair in pairs:
            for connection in pair:
                try:connection.shutdown(socket.SHUT_RDWR)
                except OSError:pass
    def repair(self):self.enabled.set()
    def close(self):self.stopped.set();self.cut();self.listener.close();self.thread.join(timeout=3)
with Hosts() as hosts:
    if hosts.provider=='Sqlite':raise ValueError('Set SMOKE_POSTGRES or SMOKE_MSSQL for a real network failure drill')
    if hosts.provider=='Postgres':
        match=re.search(r'(?i)(?:^|;)Host=([^;]+)',hosts.connection);target_host=match[1]
        match_port=re.search(r'(?i)(?:^|;)Port=(\d+)',hosts.connection);target_port=int(match_port[1]) if match_port else 5432
    else:
        match=re.search(r'(?i)(?:^|;)(?:Server|Data Source)=([^;]+)',hosts.connection);parts=match[1].replace('tcp:','').split(',');target_host=parts[0];target_port=int(parts[1]) if len(parts)>1 else 1433
    proxy=FaultProxy((target_host,target_port))
    try:
        proxied=re.sub(r'(?i)(^|;)Host=[^;]+',lambda m:m[1]+'Host=127.0.0.1',hosts.connection) if hosts.provider=='Postgres' else re.sub(r'(?i)(^|;)(?:Server|Data Source)=[^;]+',lambda m:m[1]+'Server=127.0.0.1,'+str(proxy.port),hosts.connection)
        if hosts.provider=='Postgres':proxied=re.sub(r'(?i);Port=\d+','',proxied)+';Port='+str(proxy.port)+';Timeout=2;Command Timeout=2'
        else:proxied+=';Connect Timeout=2;Command Timeout=2'
        a=hosts.start('fault-a',Storage__ConnectionString=proxied,Runner__JobTimeoutSeconds='60',Runner__Concurrency='1')
        b=hosts.start('fault-b',Runner__JobTimeoutSeconds='60',Runner__Concurrency='1')
        node_b=next(n for n in hosts.api('/api/nodes',base=b)[1] if n['name']=='fault-b')
        hosts.api('/api/nodes/'+node_b['id']+'/drain','POST',{'enabled':True},base=b)
        hosts.wait(lambda:any(n['id']==node_b['id'] and n['state']=='Drained' for n in hosts.api('/api/nodes',base=b)[1]))
        package='fault.'+secrets.token_hex(5);hosts.install(package);job=hosts.plugin(package,12000)
        row=hosts.job(job,'Running');assert row['worker'].startswith('fault-a:')
        parent=hosts.nodes['fault-a']['process'].pid
        def children():
            result=[]
            for stat in pathlib.Path('/proc').glob('[0-9]*/stat'):
                try:
                    fields=stat.read_text().rsplit(')',1)[1].split()
                    if int(fields[1])==parent:result.append(stat.parent.name)
                except (OSError,ValueError,IndexError):pass
            return result
        pids=hosts.wait(children)
        proxy.cut()
        hosts.api('/api/nodes/'+node_b['id']+'/drain','POST',{'enabled':False},base=b)
        hosts.wait(lambda:all(not pathlib.Path('/proc',pid).exists() for pid in pids),12)
        def takeover():
            code,row=hosts.api('/api/jobs/'+job,base=b)
            return row if code==200 and row['attempts']>=2 and row['worker'].startswith('fault-b:') else None
        hosts.wait(takeover,40);proxy.repair()
        row=hosts.wait(lambda: (lambda r:r if r['state']=='Succeeded' else None)(hosts.api('/api/jobs/'+job,base=b)[1]),50);assert row['attempts']==2,row
        hosts.wait(lambda:hosts.api('/health/ready',base=a)[0]==200,40)
        hosts.job(hosts.submit())
        print('PASS: '+hosts.provider+' DB partition through TCP fault proxy, owner child terminated on heartbeat failure, valid lease takeover by peer, network repair and continued work.')
    finally:proxy.close()
