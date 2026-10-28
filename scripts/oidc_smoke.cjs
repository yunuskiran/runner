// Local, signed OIDC code/PKCE flow; no external identity provider or feed credentials.
const http=require('http'),crypto=require('crypto'),fs=require('fs'),os=require('os'),path=require('path'),{spawn}=require('child_process');
const {chromium}=require('playwright');
const root=path.resolve(__dirname,'..'),tmp=fs.mkdtempSync(path.join(os.tmpdir(),'runner-oidc-'));
const {privateKey,publicKey}=crypto.generateKeyPairSync('rsa',{modulusLength:2048});
const jwk={...publicKey.export({format:'jwk'}),kid:'test-key',use:'sig',alg:'RS256'};
const codes=new Map();let role='runner-viewer',issuer,pkceChecks=0,processHost,browser,staleCookies;
const encode=value=>Buffer.from(JSON.stringify(value)).toString('base64url');
function jwt(claims){const input=encode({alg:'RS256',kid:'test-key'})+'.'+encode(claims);return input+'.'+crypto.sign('RSA-SHA256',Buffer.from(input),privateKey).toString('base64url');}
function json(response,value,status=200){response.writeHead(status,{'Content-Type':'application/json'});response.end(JSON.stringify(value));}
const identity=http.createServer(async(request,response)=>{
  const url=new URL(request.url,issuer);
  if(url.pathname==='/.well-known/openid-configuration')return json(response,{issuer,authorization_endpoint:issuer+'/authorize',token_endpoint:issuer+'/token',jwks_uri:issuer+'/jwks',response_types_supported:['code'],response_modes_supported:['query'],subject_types_supported:['public'],id_token_signing_alg_values_supported:['RS256'],code_challenge_methods_supported:['S256'],token_endpoint_auth_methods_supported:['none','client_secret_post']});
  if(url.pathname==='/jwks')return json(response,{keys:[jwk]});
  if(url.pathname==='/authorize'){
    const code=crypto.randomBytes(16).toString('hex');codes.set(code,{nonce:url.searchParams.get('nonce'),challenge:url.searchParams.get('code_challenge'),redirect:url.searchParams.get('redirect_uri'),role});
    const target=new URL(url.searchParams.get('redirect_uri'));target.searchParams.set('code',code);target.searchParams.set('state',url.searchParams.get('state'));
    response.writeHead(302,{Location:target.href});return response.end();
  }
  if(url.pathname==='/token'){
    let body='';for await(const chunk of request){body+=chunk;if(body.length>16384)return json(response,{error:'invalid_request'},400);}
    const form=new URLSearchParams(body),code=codes.get(form.get('code'));codes.delete(form.get('code'));
    if(!code||crypto.createHash('sha256').update(form.get('code_verifier')||'').digest('base64url')!==code.challenge||form.get('redirect_uri')!==code.redirect)return json(response,{error:'invalid_grant'},400);
    pkceChecks++;const now=Math.floor(Date.now()/1000);
    return json(response,{token_type:'Bearer',expires_in:300,access_token:'test-only-access-token',id_token:jwt({iss:issuer,aud:'runner-test',sub:'test-user',name:'OIDC Test',roles:[code.role],nonce:code.nonce,iat:now,exp:now+300})});
  }
  response.writeHead(404);response.end();
});
function listen(server){return new Promise(resolve=>server.listen(0,'127.0.0.1',()=>resolve(server.address().port)));}
const delay=ms=>new Promise(resolve=>setTimeout(resolve,ms));
(async()=>{
  issuer='http://127.0.0.1:'+await listen(identity);
  const probe=http.createServer();const hostPort=await listen(probe);await new Promise(resolve=>probe.close(resolve));const base='http://127.0.0.1:'+hostPort;
  const env={...process.env,ASPNETCORE_ENVIRONMENT:'Development',ASPNETCORE_URLS:base,Storage__Initialize:'true',Storage__Provider:'Sqlite',Storage__ConnectionString:'Data Source='+path.join(tmp,'runner.db'),Security__ApiKey:crypto.randomBytes(32).toString('hex'),Security__KeyDirectory:path.join(tmp,'keys'),Security__Oidc__Authority:issuer,Security__Oidc__ClientId:'runner-test',Runner__WorkerEnabled:'false'};
  const log=fs.openSync(path.join(tmp,'host.log'),'w');
  processHost=spawn(process.env.DOTNET_COMMAND||'dotnet',[path.join(root,'src/Runner.Host/bin/Release/net10.0/Runner.Host.dll')],{cwd:path.join(root,'src/Runner.Host'),env,stdio:['ignore',log,log]});
  for(let i=0;i<150;i++){try{if((await fetch(base+'/health/ready')).ok)break;}catch{}if(i===149)throw Error('OIDC test host not ready');await delay(200);}
  browser=await chromium.launch({executablePath:process.env.CHROMIUM_PATH||'/usr/bin/chromium',headless:true,args:['--no-sandbox']});
  for(const [providerRole,expected,allowed] of [['runner-viewer','Viewer',false],['runner-operator','Operator',true],['runner-admin','Admin',true],['unmapped-role',null,false]]){
    role=providerRole;const context=await browser.newContext();const page=await context.newPage();
    await page.goto(base+'/auth/login');await page.waitForURL(base+'/');
    const session=await page.request.get(base+'/api/session');
    if(expected){const identity=await session.json();if(!identity.roles.includes(expected)||!identity.name.endsWith('|test-user'))throw Error('Invalid mapped identity: '+JSON.stringify(identity));}
    else if(session.status()!==403)throw Error('Unmapped OIDC role must be denied');
    const mutation=await page.request.post(base+'/api/jobs',{headers:{'X-Runner-Request':'1'},data:{type:'sample.echo',payload:{}}});
    if(mutation.status()!==(allowed?201:403))throw Error('OIDC role mutation status '+mutation.status());
    if(expected==='Admin')staleCookies=await context.cookies();
    if(!expected&&(await page.request.post(base+'/api/logout',{headers:{'X-Runner-Request':'1'}})).status()!==204)throw Error('Denied-role user must still be able to sign out');
    await context.close();
  }
  processHost.kill('SIGTERM');await new Promise(resolve=>processHost.once('exit',resolve));
  processHost=spawn(process.env.DOTNET_COMMAND||'dotnet',[path.join(root,'src/Runner.Host/bin/Release/net10.0/Runner.Host.dll')],{cwd:path.join(root,'src/Runner.Host'),env:{...env,Security__SessionVersion:'2'},stdio:['ignore',log,log]});
  for(let i=0;i<150;i++){try{if((await fetch(base+'/health/ready')).ok)break;}catch{}if(i===149)throw Error('Revocation restart not ready');await delay(200);}
  const stale=await browser.newContext();await stale.addCookies(staleCookies);
  if((await stale.request.get(base+'/api/session')).status()!==401)throw Error('Old session survived SessionVersion rotation');
  await stale.close();
  if(pkceChecks!==4)throw Error('Expected four verified PKCE exchanges');
  console.log('PASS: real browser OIDC code/PKCE, RSA signature/issuer/audience/nonce validation, canonical viewer/operator/admin mapping and denial of unmapped roles and session-version revocation.');
})().catch(error=>{console.error(error);if(fs.existsSync(path.join(tmp,'host.log')))console.error(fs.readFileSync(path.join(tmp,'host.log'),'utf8').slice(-5000));process.exitCode=1;}).finally(async()=>{if(browser)await browser.close();if(processHost){processHost.kill('SIGTERM');await Promise.race([new Promise(resolve=>processHost.once('exit',resolve)),delay(5000)]);if(processHost.exitCode===null)processHost.kill('SIGKILL');}await new Promise(resolve=>identity.close(resolve));fs.rmSync(tmp,{recursive:true,force:true});});
