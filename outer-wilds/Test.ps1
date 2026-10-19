$ErrorActionPreference='Stop'
$buildDir=Join-Path $PSScriptRoot '..\build'
New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testExe=Join-Path $buildDir 'LandingPolicyTests.exe'
& $compiler /nologo /utf8output /target:exe ("/out:"+$testExe) (Join-Path $PSScriptRoot 'LandingPolicy.cs') (Join-Path $PSScriptRoot 'PolicyTests.cs')
if($LASTEXITCODE -ne 0){throw 'Policy compilation failed'}
& $testExe
if($LASTEXITCODE -ne 0){throw 'Policy tests failed'}
