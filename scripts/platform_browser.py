#!/usr/bin/env python3
"""Start disposable dashboard nodes and exercise the real UI; writes documentation screenshots."""
import os,pathlib,subprocess
from host_fixture import Hosts,ROOT
with Hosts() as hosts:
    base=hosts.start('dashboard-worker-a');hosts.start('dashboard-worker-b')
    key=pathlib.Path(hosts.tmp.name)/'browser.key';key.write_text(hosts.keys['Admin']);key.chmod(0o600)
    env=dict(os.environ,RUNNER_SCREENSHOT_URL=base,RUNNER_KEY_FILE=str(key))
    subprocess.run(['node',str(ROOT/'scripts/platform_browser.cjs')],env=env,cwd=ROOT,check=True)
