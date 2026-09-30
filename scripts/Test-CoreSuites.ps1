#requires -Version 7.2
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$run=Join-Path $root ('artifacts/core-regression/'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$projects=@('Relay.Tests','Relay.FocusTests','Relay.PolicyTests','Relay.SafetyTests','Relay.LauncherTests','Relay.MigrationTests')
$results=[Collections.Generic.List[object]]::new()
foreach($name in $projects){
  $project=Join-Path $root ('native/'+$name+'/'+$name+'.csproj')
  $timer=[Diagnostics.Stopwatch]::StartNew()
  & dotnet run --project $project -c Release --no-restore 2>&1 | Tee-Object -FilePath (Join-Path $run ($name+'.log')) | Out-Host
  $code=$LASTEXITCODE
  $results.Add([ordered]@{project=$name;exitCode=$code;milliseconds=$timer.ElapsedMilliseconds;status=if($code-eq0){'passed'}else{'failed'}})
  if($code-ne0){break}
}
$failed=@($results|Where-Object exitCode -ne 0).Count -gt 0
[ordered]@{generatedUtc=[datetime]::UtcNow.ToString('o');status=if($failed){'failed'}else{'passed'};results=$results.ToArray();note='Read individual logs for explicit skipped platform cases; successful process exit does not mean every platform fixture ran.'}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $run 'report.json') -Encoding utf8
Write-Host ('Core suite report: '+$run)
if($failed){exit 1}else{exit 0}
