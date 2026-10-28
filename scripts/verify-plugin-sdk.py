#!/usr/bin/env python3
"""Verify the local SDK package, then compile a consumer using a fresh NuGet cache."""
import os,pathlib,subprocess,tempfile,zipfile,hashlib,json
root=pathlib.Path(__file__).resolve().parents[1]
package_id=os.environ.get('RUNNER_PLUGIN_PACKAGE_ID','Runner.PluginSdk')
version=os.environ.get('RUNNER_PLUGIN_SDK_VERSION','0.1.0')
package=root/f'artifacts/nuget/{package_id}.{version}.nupkg'
symbols=package.with_name(f'{package_id}.{version}.snupkg')
assert symbols.is_file()
with zipfile.ZipFile(symbols) as archive:assert any(n.endswith('Runner.Contracts.pdb') for n in archive.namelist())
with zipfile.ZipFile(package) as archive:
    names=set(archive.namelist())
    assert 'lib/net10.0/Runner.Contracts.dll' in names
    assert 'lib/net10.0/Runner.Contracts.xml' in names
    assert f'buildTransitive/{package_id}.targets' in names
    assert 'README.md' in names
    nuspec=archive.read(package_id+'.nuspec').decode()
    assert '<dependencies>' not in nuspec or '<dependency ' not in nuspec
with tempfile.TemporaryDirectory() as tmp:
    env=dict(os.environ,NUGET_PACKAGES=str(pathlib.Path(tmp)/'packages'))
    dotnet=os.environ.get('DOTNET_COMMAND','dotnet')
    subprocess.run([dotnet,'publish',str(root/'samples/Plugin.FromNuGet'),'-c','Release','-o',str(root/'artifacts/sdk-consumer'),
        '--source',str(root/'artifacts/nuget'),f'-p:RunnerPluginPackageId={package_id}',f'-p:RunnerPluginSdkVersion={version}'],env=env,cwd=root,check=True)
    output=root/'artifacts/sdk-consumer'
    assert (output/'Plugin.FromNuGet.dll').is_file()
    assert not (output/'Runner.Contracts.dll').exists(), 'SDK contract must be supplied by the host'
    assert (output/'Plugin.FromNuGet.deps.json').exists()
checksums={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in package.parent.glob(f'{package_id}.{version}.*nupkg')}
(package.parent/'checksums.json').write_text(json.dumps(checksums,indent=2)+'\n')
print('PASS: SDK metadata/XML/targets/symbols and fresh-cache NuGet consumer; host contract excluded; no packages published.')
