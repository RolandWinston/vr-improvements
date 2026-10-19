[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][string]$ManagedPath,
 [Parameter(Mandatory=$true)][string]$OwmlPath
)
$ErrorActionPreference='Stop'
if (@(Get-Process -Name 'OuterWilds','OWML.Launcher' -ErrorAction SilentlyContinue).Count -gt 0) { throw '请先保存并退出游戏。' }
$compiler='C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$managed=$ManagedPath
$owml=$OwmlPath
$output=Join-Path $PSScriptRoot 'OuterVRLandingAssist.dll'
$oldHash=if(Test-Path -LiteralPath $output){(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash}else{$null}
$temp=Join-Path $PSScriptRoot ('build-'+[Guid]::NewGuid().ToString('N')+'.dll')
$refs=@('Assembly-CSharp.dll','UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.PhysicsModule.dll','netstandard.dll') | ForEach-Object { Join-Path $managed $_ }
$refs+=@('OWML.Common.dll','OWML.ModHelper.dll','0Harmony.dll') | ForEach-Object { Join-Path $owml $_ }
foreach($ref in $refs){if(-not(Test-Path -LiteralPath $ref -PathType Leaf)){throw "缺少依赖：$ref"}}
$argsList=@('/nologo','/utf8output','/target:library','/optimize+',('/out:'+$temp))
$argsList+=@($refs | ForEach-Object {'/r:'+$_})
$argsList+=@(Join-Path $PSScriptRoot 'LandingPolicy.cs')
$argsList+=@(Join-Path $PSScriptRoot 'LandingAssist.cs')
& $compiler @argsList
if($LASTEXITCODE -ne 0){throw "编译失败，退出码 $LASTEXITCODE"}
if($oldHash){
 if((Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash -ne $oldHash){throw "编译期间产物发生变化，未覆盖；候选：$temp"}
 [IO.File]::Replace($temp,$output,[NullString]::Value)
}else{
 if(Test-Path -LiteralPath $output){throw "编译期间出现其他产物，未覆盖；候选：$temp"}
 [IO.File]::Move($temp,$output)
}
Write-Output "编译完成：$output。未部署至 OWML；新构建须另行核对和验证。"
