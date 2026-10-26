// Optional documentation tool. Requires Playwright and Chromium; no app runtime dependency.
const {chromium}=require('playwright');
const fs=require('node:fs');
const path=require('node:path');
(async()=>{
  const root=path.resolve(__dirname,'..');
  const base=process.env.RUNNER_SCREENSHOT_URL || 'http://127.0.0.1:5090';
  const key=fs.readFileSync(process.env.RUNNER_KEY_FILE || path.join(root,'src/Runner.Host/data/operator.key'),'utf8').trim();
  const output=path.join(root,'docs/images');fs.mkdirSync(output,{recursive:true});
  const browser=await chromium.launch({executablePath:process.env.CHROMIUM_PATH || '/usr/bin/chromium',headless:true,args:['--no-sandbox']});
  try {
    const page=await browser.newPage({viewport:{width:1440,height:1080},deviceScaleFactor:1});
    const errors=[];page.on('pageerror',e=>errors.push(e.message));
    await page.goto(base);
    await page.locator('input[name=key]').fill(key);
    await page.getByRole('button',{name:'Sign in'}).click();
    await page.waitForSelector('#function-workspace',{state:'visible'});
    await page.waitForSelector('#handler option',{state:'attached'});
    // Exercise compiler diagnostics in the actual editor before capturing its successful state.
    const source=await page.locator('#function-source').inputValue();
    await page.locator('#function-source').fill('return missing;');
    await page.locator('#register-button').click();
    await page.waitForFunction(()=>document.getElementById('compile-diagnostics').textContent.includes('CS0103'));
    await page.locator('#function-source').fill(source);
    const existing=await page.evaluate(async()=>await (await fetch('/api/functions')).json());
    const found=existing.find(f=>f.name==='orders.total'&&f.version===1);
    if(found)await page.locator(`[data-function="${found.id}"][data-function-action="select"]`).click();
    else {
      await page.locator('#register-button').click();
      await page.waitForFunction(()=>document.getElementById('compile-diagnostics').textContent.includes('Compiled successfully'));
    }
    await page.waitForFunction(()=>!document.getElementById('run-function').disabled);
    await page.locator('#run-function').click();
    let job;
    for(let i=0;i<60;i++){
      const jobs=await page.evaluate(async()=>await (await fetch('/api/jobs')).json());
      job=jobs.find(j=>j.type==='runner.script'&&j.state==='Succeeded');if(job)break;
      await page.waitForTimeout(300);
    }
    if(!job)throw Error('Script did not complete');
    const detail=await page.evaluate(async id=>await (await fetch('/api/jobs/'+id)).json(),job.id);
    if(JSON.parse(detail.resultJson).total!==59.7)throw Error('Incorrect script result');
    await page.evaluate(async()=>{
      for(const payload of [{delayMs:100},{fail:true}]) await fetch('/api/jobs',{method:'POST',headers:{'Content-Type':'application/json','X-Runner-Request':'1'},body:JSON.stringify({type:'sample.echo',payload,maxAttempts:1})});
      await fetch('/api/jobs',{method:'POST',headers:{'Content-Type':'application/json','X-Runner-Request':'1'},body:JSON.stringify({type:'sample.echo',payload:{},delaySeconds:3600})});
    });
    await page.waitForTimeout(6000);
    // No credentials are rendered in the dashboard or included in these screenshots.
    await page.evaluate(()=>window.scrollTo(0,0));
    await page.screenshot({path:path.join(output,'dashboard.png')});
    await page.locator('#functions').screenshot({path:path.join(output,'function-registry.png')});
    await page.locator(`[data-action="details"][data-id="${job.id}"]`).click();
    await page.waitForSelector('#job-detail[open]');
    if(!(await page.locator('#job-detail-content').textContent()).includes('59.7'))throw Error('Result not shown in dashboard');
    await page.locator('#job-detail').screenshot({path:path.join(output,'function-result.png')});
    await page.locator('#close-detail').click();
    await page.setViewportSize({width:390,height:844});
    await page.evaluate(()=>window.scrollTo(0,0));
    await page.screenshot({path:path.join(output,'dashboard-mobile.png'),fullPage:true});
    if(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth))throw Error('Mobile viewport overflow');
    if(errors.length)throw Error(errors.join('\n'));
    console.log('PASS: editor diagnostics, registration, execution, persisted result dialog, desktop/mobile layout; captured docs/images screenshots.');
  } finally {await browser.close();}
})().catch(e=>{console.error(e);process.exit(1);});
