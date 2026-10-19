$ErrorActionPreference='Stop'
$buildDir=Join-Path $PSScriptRoot '..\build'
New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe=Join-Path $buildDir 'VRSession.exe'
& $compiler /nologo /utf8output /target:exe /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll ("/out:"+$exe) (Join-Path $PSScriptRoot 'VRSession.cs')
if($LASTEXITCODE -ne 0){throw 'Compilation failed'}
foreach($test in @('--self-test-startup','--self-test-startup-evidence','--self-test-wired')){
 & $exe $test
 if($LASTEXITCODE -ne 0){throw "Failed: $test"}
}
