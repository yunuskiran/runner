const $ = id => document.getElementById(id);
let page = 0, refreshing = false, scriptingEnabled = false, functionPage = 0, selectedFunction = null;
const esc = v => String(v ?? '').replace(/[&<>"']/g, x => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[x]));
async function api(path, method='GET', body) {
  const r = await fetch('/api'+path, {method, headers:{'Content-Type':'application/json','X-Runner-Request':'1'}, body:body === undefined ? undefined : JSON.stringify(body)});
  if (r.status === 401) {location.href='/login'; throw Error('Session expired');}
  if (!r.ok) {
    let detail; try { detail = await r.json(); } catch { detail = {}; }
    const diagnostics = detail.diagnostics?.map(d => `${d.id} (${d.line}:${d.column}): ${d.message}`).join('\n');
    throw Error(diagnostics || detail.error || `Request failed (${r.status}). Check input or server logs.`);
  }
  return r.status === 204 || r.status === 202 ? null : r.json();
}
function status(message, error=false) { $('status').textContent=message; $('status').className='notice'+(error?' error':''); }
async function refresh() {
  if(refreshing) return; refreshing=true;
  try {
    const [stats,jobs,schedules] = await Promise.all([api('/stats'),api(`/jobs?page=${page}&${$('filter').value ? 'state='+$('filter').value : ''}`),api('/schedules')]);
    $('overview').innerHTML=['Pending','Running','Succeeded','DeadLetter','Cancelled'].map(s=>`<article><span>${s==='DeadLetter'?'Dead letter':s}</span><strong>${stats.find(x=>x.state===s)?.count??0}</strong><small>${s==='Running'?'Leased to a worker':s==='DeadLetter'?'Needs attention':'In retained history'}</small></article>`).join('');
    $('rows').innerHTML=jobs.length?jobs.map(j=>`<tr><td><strong>${esc(j.type)}</strong><small>${esc(j.id)}</small></td><td><span class="badge ${esc(j.state.toLowerCase())}">${esc(j.state)}</span>${j.cancellationRequested?'<small>Cancellation requested</small>':''}${j.errorCode?`<small>${esc(j.errorCode)}</small>`:''}</td><td>${j.attempts} <span class="muted">/ ${j.maxAttempts}</span></td><td>${esc(new Date(j.createdUtc).toLocaleString())}</td><td>${['Pending','Running'].includes(j.state)?`<button class="text" data-action="cancel" data-id="${j.id}">Cancel</button>`:['DeadLetter','Cancelled'].includes(j.state)?`<button class="text" data-action="retry" data-id="${j.id}">Retry</button>`:'—'}<button class="text" data-action="details" data-id="${j.id}">Details</button></td></tr>`).join(''):'<tr><td colspan="5" class="empty">No jobs here yet. Dispatch one to get started.</td></tr>';
    $('page').textContent=`Page ${page+1}`; $('prev').disabled=page===0; $('next').disabled=jobs.length<50;
    $('schedule-list').innerHTML=schedules.length?schedules.map(s=>`<div class="schedule-item"><strong>${esc(s.type)}</strong><span>Every ${s.intervalSeconds}s · next ${esc(new Date(s.nextUtc).toLocaleString())}</span><button class="text" data-schedule="${s.id}">Delete</button></div>`).join(''):'<p>No recurring schedules.</p>';
    if(scriptingEnabled) await refreshFunctions();
    $('updated').textContent='Updated '+new Date().toLocaleTimeString();
    if($('status').textContent.startsWith('Connecting'))status('Control plane connected · persistent job storage');
  } catch(e) {status(e.message,true);} finally {refreshing=false;}
}
async function act(fn, message) {try{await fn();status(message);await refresh();}catch(e){status(e.message,true);}}
$('dispatch').onsubmit=e=>{e.preventDefault();act(()=>api('/jobs','POST',{type:$('handler').value,payload:JSON.parse($('payload').value),maxAttempts:Number($('attempts').value),delaySeconds:Number($('delay').value),idempotencyKey:$('key').value||null}),'Job accepted.');};
$('schedule').onsubmit=e=>{e.preventDefault();act(()=>api('/schedules','POST',{type:$('handler').value,payload:JSON.parse($('payload').value),intervalSeconds:Number($('interval').value)}),'Schedule created.');};
$('rows').onclick=e=>{const b=e.target.closest('[data-action]');if(!b)return;
  if(b.dataset.action==='details') act(async()=>{const job=await api(`/jobs/${b.dataset.id}`);$('job-detail-content').textContent=JSON.stringify({...job,result:job.resultJson ? JSON.parse(job.resultJson) : null},null,2);$('job-detail').showModal();},'Job details loaded.');
  else act(()=>api(`/jobs/${b.dataset.id}/${b.dataset.action}`,'POST'),'Operation accepted.');
};
$('schedule-list').onclick=e=>{const b=e.target.closest('[data-schedule]');if(b&&confirm('Delete this schedule?'))act(()=>api(`/schedules/${b.dataset.schedule}`,'DELETE'),'Schedule deleted.');};
$('filter').onchange=()=>{page=0;refresh();}; $('prev').onclick=()=>{page=Math.max(0,page-1);refresh();}; $('next').onclick=()=>{page++;refresh();};
$('logout').onclick=()=>act(async()=>{await api('/logout','POST');location.href='/login';},'Signed out.');
api('/handlers').then(h=>{$('handler').innerHTML=h.map(x=>`<option>${esc(x)}</option>`).join('');return refresh();}).catch(e=>status(e.message,true));
setInterval(refresh,5000);

async function refreshFunctions() {
  const functions = await api(`/functions?page=${functionPage}`);
  if(selectedFunction && functions.some(f=>f.id===selectedFunction.id)) {
    selectedFunction.enabled=functions.find(f=>f.id===selectedFunction.id).enabled;
    $('run-function').disabled=!selectedFunction.enabled;
  }
  $('function-rows').innerHTML=functions.length ? functions.map(f=>`<tr><td><strong>${esc(f.name)}</strong><small>${esc(f.id)}</small></td><td>v${f.version}</td><td><span class="badge ${f.enabled?'succeeded':'cancelled'}">${f.enabled?'Enabled':'Disabled'}</span><small>${esc(f.sourceHash.slice(0,16))}…</small></td><td><button class="text" data-function="${f.id}" data-function-action="select">Select / source</button><button class="text" data-function="${f.id}" data-function-action="${f.enabled?'disable':'enable'}">${f.enabled?'Disable':'Enable'}</button></td></tr>`).join('') : '<tr><td colspan="4" class="empty">No functions registered. Compile your first version above.</td></tr>';
  $('functions-page').textContent=`Page ${functionPage+1}`; $('functions-prev').disabled=functionPage===0; $('functions-next').disabled=functions.length<50;
}
function selectFunction(f) {
  selectedFunction=f; $('selected-function').value=`${f.name} · v${f.version}`; $('run-function').disabled=!f.enabled;
  $('function-name').value=f.name; $('function-version').value=f.version+1; $('function-source').value=f.source;
}
$('register-function').onsubmit=e=>{e.preventDefault();act(async()=>{
  $('register-button').disabled=true; $('compile-diagnostics').textContent='Compiling…';
  try {const f=await api('/functions','POST',{name:$('function-name').value,version:Number($('function-version').value),source:$('function-source').value});selectFunction(f);$('compile-diagnostics').textContent='Compiled successfully. Immutable version registered.';}
  catch(error){$('compile-diagnostics').textContent=error.message;throw error;}
  finally{$('register-button').disabled=false;}
},'Function version registered.');};
$('run-function').onclick=()=>act(async()=>{
  if(!selectedFunction?.enabled)throw Error('Select an enabled function version first.');
  await api('/jobs','POST',{type:'runner.script',payload:{functionId:selectedFunction.id,arguments:JSON.parse($('function-arguments').value)},maxAttempts:5});
},'Function run queued. Open job details to inspect its JSON result.');
$('function-rows').onclick=e=>{const b=e.target.closest('[data-function]');if(!b)return;act(async()=>{
  if(b.dataset.functionAction==='select')selectFunction(await api(`/functions/${b.dataset.function}`));
  else await api(`/functions/${b.dataset.function}`,'PATCH',{enabled:b.dataset.functionAction==='enable'});
},'Function registry updated.');};
$('functions-prev').onclick=()=>{functionPage=Math.max(0,functionPage-1);act(refreshFunctions,'Function registry updated.');};
$('functions-next').onclick=()=>{functionPage++;act(refreshFunctions,'Function registry updated.');};
$('close-detail').onclick=()=>$('job-detail').close();
api('/scripting').then(async config=>{
  scriptingEnabled=config.enabled; $('function-workspace').hidden=!config.enabled;
  $('scripting-status').textContent=config.enabled?'Trusted operator mode · C# runs with host privileges. Only register reviewed code.':'Scripting is disabled. Set Scripting__Enabled=true on all workers to enable the registry.';
  if(config.enabled)await refreshFunctions();
}).catch(e=>status(e.message,true));
