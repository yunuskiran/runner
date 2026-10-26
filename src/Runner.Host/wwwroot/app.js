const $ = id => document.getElementById(id);
let page = 0, refreshing = false;
const esc = v => String(v ?? '').replace(/[&<>"']/g, x => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[x]));
async function api(path, method='GET', body) {
  const r = await fetch('/api'+path, {method, headers:{'Content-Type':'application/json','X-Runner-Request':'1'}, body:body === undefined ? undefined : JSON.stringify(body)});
  if (r.status === 401) {location.href='/login'; throw Error('Session expired');}
  if (!r.ok) throw Error(`Request failed (${r.status}). Check input or server logs.`);
  return r.status === 204 || r.status === 202 ? null : r.json();
}
function status(message, error=false) { $('status').textContent=message; $('status').className='notice'+(error?' error':''); }
async function refresh() {
  if(refreshing) return; refreshing=true;
  try {
    const [stats,jobs,schedules] = await Promise.all([api('/stats'),api(`/jobs?page=${page}&${$('filter').value ? 'state='+$('filter').value : ''}`),api('/schedules')]);
    $('overview').innerHTML=['Pending','Running','Succeeded','DeadLetter','Cancelled'].map(s=>`<article><span>${s==='DeadLetter'?'Dead letter':s}</span><strong>${stats.find(x=>x.state===s)?.count??0}</strong><small>${s==='Running'?'Leased to a worker':s==='DeadLetter'?'Needs attention':'In retained history'}</small></article>`).join('');
    $('rows').innerHTML=jobs.length?jobs.map(j=>`<tr><td><strong>${esc(j.type)}</strong><small>${esc(j.id)}</small></td><td><span class="badge ${esc(j.state.toLowerCase())}">${esc(j.state)}</span>${j.cancellationRequested?'<small>Cancellation requested</small>':''}${j.errorCode?`<small>${esc(j.errorCode)}</small>`:''}</td><td>${j.attempts} <span class="muted">/ ${j.maxAttempts}</span></td><td>${esc(new Date(j.createdUtc).toLocaleString())}</td><td>${['Pending','Running'].includes(j.state)?`<button class="text" data-action="cancel" data-id="${j.id}">Cancel</button>`:['DeadLetter','Cancelled'].includes(j.state)?`<button class="text" data-action="retry" data-id="${j.id}">Retry</button>`:'—'}</td></tr>`).join(''):'<tr><td colspan="5" class="empty">No jobs here yet. Dispatch one to get started.</td></tr>';
    $('page').textContent=`Page ${page+1}`; $('prev').disabled=page===0; $('next').disabled=jobs.length<50;
    $('schedule-list').innerHTML=schedules.length?schedules.map(s=>`<div class="schedule-item"><strong>${esc(s.type)}</strong><span>Every ${s.intervalSeconds}s · next ${esc(new Date(s.nextUtc).toLocaleString())}</span><button class="text" data-schedule="${s.id}">Delete</button></div>`).join(''):'<p>No recurring schedules.</p>';
    $('updated').textContent='Updated '+new Date().toLocaleTimeString();
    if($('status').textContent.startsWith('Connecting'))status('Control plane connected · persistent job storage');
  } catch(e) {status(e.message,true);} finally {refreshing=false;}
}
async function act(fn, message) {try{await fn();status(message);await refresh();}catch(e){status(e.message,true);}}
$('dispatch').onsubmit=e=>{e.preventDefault();act(()=>api('/jobs','POST',{type:$('handler').value,payload:JSON.parse($('payload').value),maxAttempts:Number($('attempts').value),delaySeconds:Number($('delay').value),idempotencyKey:$('key').value||null}),'Job accepted.');};
$('schedule').onsubmit=e=>{e.preventDefault();act(()=>api('/schedules','POST',{type:$('handler').value,payload:JSON.parse($('payload').value),intervalSeconds:Number($('interval').value)}),'Schedule created.');};
$('rows').onclick=e=>{const b=e.target.closest('[data-action]');if(b)act(()=>api(`/jobs/${b.dataset.id}/${b.dataset.action}`,'POST'),'Operation accepted.');};
$('schedule-list').onclick=e=>{const b=e.target.closest('[data-schedule]');if(b&&confirm('Delete this schedule?'))act(()=>api(`/schedules/${b.dataset.schedule}`,'DELETE'),'Schedule deleted.');};
$('filter').onchange=()=>{page=0;refresh();}; $('prev').onclick=()=>{page=Math.max(0,page-1);refresh();}; $('next').onclick=()=>{page++;refresh();};
$('logout').onclick=()=>act(async()=>{await api('/logout','POST');location.href='/login';},'Signed out.');
api('/handlers').then(h=>{$('handler').innerHTML=h.map(x=>`<option>${esc(x)}</option>`).join('');return refresh();}).catch(e=>status(e.message,true));
setInterval(refresh,5000);
