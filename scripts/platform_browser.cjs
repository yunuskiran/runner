const {chromium}=require('playwright'),fs=require('fs'),path=require('path');
(async()=>{
 const base=process.env.RUNNER_SCREENSHOT_URL,key=fs.readFileSync(process.env.RUNNER_KEY_FILE,'utf8').trim();
 const output=path.resolve(__dirname,'../docs/images');fs.mkdirSync(output,{recursive:true});
 const browser=await chromium.launch({executablePath:process.env.CHROMIUM_PATH||'/usr/bin/chromium',headless:true,args:['--no-sandbox']});
 try{
  const context=await browser.newContext({viewport:{width:1440,height:1080},timezoneId:process.env.RUNNER_BROWSER_TIMEZONE||'Australia/Sydney'}),page=await context.newPage(),errors=[];page.on('pageerror',error=>errors.push(error.message));
  await page.goto(base);await page.locator('input[name=key]').fill(key);await page.getByRole('button',{name:'Sign in'}).click();
  await page.waitForFunction(()=>document.querySelectorAll('#node-list [data-node-drain]').length===2);
  const node=(await (await page.request.get(base+'/api/nodes')).json()).find(n=>n.name==='dashboard-worker-a');
  await page.locator(`[data-node-drain="${node.id}"]`).click();
  await page.waitForFunction(async id=>(await (await fetch('/api/nodes')).json()).some(n=>n.id===id&&n.state==='Drained'),node.id);
  await page.locator('#nodes-refresh').click();await page.waitForFunction(()=>document.getElementById('node-list').textContent.includes('Drained'));
  await page.locator('#nodes').scrollIntoViewIfNeeded();await page.screenshot({path:path.join(output,'worker-servers.png')});
  await page.locator(`[data-node-drain="${node.id}"]`).click();
  await page.locator('#workflow-name').fill('order-fulfilment');
  await page.locator('#workflow-steps').fill(JSON.stringify([{key:'validate',job:{type:'sample.echo',payload:{delayMs:100}}},{key:'invoice',dependsOn:['validate'],job:{type:'sample.echo',payload:{delayMs:100}}},{key:'notify',dependsOn:['invoice'],job:{type:'sample.echo',payload:{}}}],null,2));
  await page.locator('#workflow-create button').click();await page.waitForSelector('[data-workflow]');
  const id=await page.locator('[data-workflow]').first().getAttribute('data-workflow');
  await page.waitForFunction(async id=>{const run=await (await fetch('/api/workflows/'+id)).json();return run.jobs.length===3&&run.jobs.every(j=>j.state==='Succeeded');},id);
  await page.locator('#workflows').scrollIntoViewIfNeeded();await page.screenshot({path:path.join(output,'dependency-workflows.png')});
  await page.locator('[data-workflow]').first().click();await page.waitForSelector('#job-detail[open]');
  await page.screenshot({path:path.join(output,'workflow-run.png')});await page.locator('#close-detail').click();
  await page.locator('#cron-expression').fill('0 9 * * 1-5');await page.locator('#cron-timezone').fill('Australia/Sydney');await page.locator('#cron-misfire').selectOption('CatchUp');
  const created=page.waitForResponse(response=>response.url().endsWith('/api/schedules')&&response.request().method()==='POST');
  await page.locator('#schedule button').click();if((await created).status()!==201)throw Error('Calendar UI submission failed');
  await page.waitForFunction(()=>document.getElementById('schedule-list').textContent.includes('Australia/Sydney'));
  await page.locator('#schedules').scrollIntoViewIfNeeded();await page.screenshot({path:path.join(output,'calendar-schedules.png')});
  await page.locator('#audit-refresh').click();await page.waitForFunction(()=>document.getElementById('audit-list').textContent.includes('/drain'));
  await page.locator('#audit').scrollIntoViewIfNeeded();await page.screenshot({path:path.join(output,'audit-trail.png')});
  await page.setViewportSize({width:390,height:844});await page.locator('#nodes').scrollIntoViewIfNeeded();await page.screenshot({path:path.join(output,'worker-servers-mobile.png')});
  if(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth+1))throw Error('Mobile overflow');
  if(errors.length)throw Error(errors.join('\n'));
  console.log('PASS: browser server drain/resume, dependency workflow creation/results, cron/timezone submission, audit UI and mobile layout; screenshots captured.');
 }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
