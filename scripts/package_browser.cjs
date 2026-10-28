// Optional Playwright validation and documentation screenshots for live package management.
const {chromium}=require('playwright');
const fs=require('node:fs');const path=require('node:path');const os=require('node:os');
(async()=>{
 const root=path.resolve(__dirname,'..');const base=process.env.RUNNER_SCREENSHOT_URL||'http://127.0.0.1:5094';
 const key=fs.readFileSync(process.env.RUNNER_KEY_FILE||path.join(root,'src/Runner.Host/data/operator.key'),'utf8').trim();
 const folder=fs.mkdtempSync(path.join(os.tmpdir(),'runner-plugin-any-folder-'));
 const fixture=path.join(root,'src/Runner.SamplePlugin/bin/Release/net10.0');
 for(const file of fs.readdirSync(fixture))if(file.startsWith('Runner.SamplePlugin.'))fs.copyFileSync(path.join(fixture,file),path.join(folder,file));
 fs.writeFileSync(path.join(folder,'large-resource.txt'),'plugin asset\n'.repeat(20000));
 const browser=await chromium.launch({executablePath:process.env.CHROMIUM_PATH||'/usr/bin/chromium',headless:true,args:['--no-sandbox']});
 try {
  const page=await browser.newPage({viewport:{width:1440,height:1080}});const errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.goto(base);await page.locator('input[name=key]').fill(key);await page.getByRole('button',{name:'Sign in'}).click();
  await page.waitForSelector('#package-workspace',{state:'visible'});
  await page.locator('#package-id').fill('company.reports');
  await page.locator('#package-folder').setInputFiles(folder);
  await page.waitForFunction(()=>!document.getElementById('package-install-button').disabled);
  await page.locator('#package-entry').selectOption('Runner.SamplePlugin.dll');
  await page.locator('#package-install-button').click();
  await page.waitForFunction(()=>document.getElementById('package-feedback').textContent.startsWith('Installed company.reports v1.0.0'));
  await page.locator('[data-version="1.0.0"][data-version-action="activate"]').click();
  await page.waitForFunction(()=>!document.getElementById('package-run').disabled);
  await page.locator('#package-run').click();
  async function waitForJob(version) {
   for(let i=0;i<70;i++) {
    const result=await page.evaluate(async version=>{
     const jobs=await (await fetch('/api/jobs')).json();
     for(const job of jobs.filter(j=>j.type==='runner.plugin'&&j.state==='Succeeded')) {
      const detail=await (await fetch('/api/jobs/'+job.id)).json();
      if(detail.packageId==='company.reports'&&detail.packageVersion===version)return detail;
     }
    },version);
    if(result){if(JSON.parse(result.resultJson).greeting!=='Hello, Runner!')throw Error('Unexpected plugin result');return;}
    await page.waitForTimeout(300);
   }
   throw Error('Live plugin execution did not complete');
  }
  await waitForJob('1.0.0');
  // Also exercise multi-file DLL selection from an arbitrary directory, independently of folder input.
  fs.writeFileSync(path.join(folder,'release-notes.txt'),'Second immutable package version');
  await page.locator('#package-version').fill('2.0.0');
  await page.locator('#package-files').setInputFiles(fs.readdirSync(folder).map(f=>path.join(folder,f)));
  await page.locator('#package-entry').selectOption('Runner.SamplePlugin.dll');await page.locator('#package-install-button').click();
  await page.waitForFunction(()=>document.getElementById('package-feedback').textContent.startsWith('Installed company.reports v2.0.0'));
  await page.locator('[data-version="2.0.0"][data-version-action="activate"]').click();
  await page.waitForFunction(()=>document.getElementById('package-selected').value.includes('v2.0.0')&&!document.getElementById('package-run').disabled);
  await page.locator('#package-run').click();await waitForJob('2.0.0');
  await page.locator('[data-package="company.reports"][data-package-action="disable"]').click();
  await page.waitForFunction(()=>document.getElementById('package-run').disabled);
  await page.locator('[data-version="1.0.0"][data-version-action="activate"]').click();
  await page.waitForFunction(()=>document.getElementById('package-selected').value.includes('v1.0.0')&&!document.getElementById('package-run').disabled);
  const output=path.join(root,'docs/images');fs.mkdirSync(output,{recursive:true});
  await page.locator('#packages').screenshot({path:path.join(output,'package-manager.png')});
  await page.locator('#package-history').click();await page.waitForSelector('#job-detail[open]');
  await page.locator('#job-detail').screenshot({path:path.join(output,'package-history.png')});
  await page.locator('#close-detail').click();
  await page.setViewportSize({width:390,height:844});await page.locator('#packages').scrollIntoViewIfNeeded();
  await page.screenshot({path:path.join(output,'package-manager-mobile.png'),fullPage:true});
  if(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth))throw Error('Mobile viewport overflow');
  if(errors.length)throw Error(errors.join('\n'));
  console.log('PASS: browser folder and DLL selection, live install/activation, two versioned executions, disable, rollback, history, responsive layout; captured package screenshots.');
 } finally {await browser.close();fs.rmSync(folder,{recursive:true,force:true});}
})().catch(e=>{console.error(e);process.exit(1);});
