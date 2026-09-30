#requires -Version 7.2
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
function Assert-WorkspacePath([string]$Path) {
    $full=[IO.Path]::GetFullPath($Path)
    if(-not $full.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw "Path is outside the workspace: $full"}
    # Check every existing ancestor, including the workspace and its parents.
    # Get-Item also detects dangling links, which Test-Path may report as absent.
    $cursor=$full
    while($cursor){
        $item=Get-Item -LiteralPath $cursor -Force -ErrorAction SilentlyContinue
        if($null -ne $item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw "Refusing reparse point: $cursor"}
        $cursor=[IO.Path]::GetDirectoryName($cursor)
    }
    return $full
}
$null=Assert-WorkspacePath $PSScriptRoot
$zip=Assert-WorkspacePath (Join-Path $root 'dist/Relay-Windows-x64.zip')
$run=Assert-WorkspacePath (Join-Path $root ('artifacts/portable-update/'+[guid]::NewGuid().ToString('N')))
$package=Assert-WorkspacePath (Join-Path $run 'package')
New-Item -ItemType Directory -Path $package -Force | Out-Null
$archive=[IO.Compression.ZipFile]::OpenRead($zip)
try{
    foreach($entry in $archive.Entries){
        if($entry.FullName -match '(^|/)(Data|[.][.])(/|$)' -or [IO.Path]::IsPathRooted($entry.FullName)){throw 'Archive contains data or unsafe path'}
        $target=Assert-WorkspacePath (Join-Path $package $entry.FullName)
        if(-not $target.StartsWith($package+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Archive escaped test package'}
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$target,$false)
    }
}finally{$archive.Dispose()}
$data=Assert-WorkspacePath (Join-Path $package 'Data')
New-Item -ItemType Directory -Path $data | Out-Null
$sentinel=Assert-WorkspacePath (Join-Path $data 'vault.json')
$bytes=[Text.Encoding]::UTF8.GetBytes('{"testOnly":"数据不应被更新覆盖","nonce":"'+[guid]::NewGuid().ToString('N')+'"}')
[IO.File]::WriteAllBytes($sentinel,$bytes)
$before=(Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash
$archive=[IO.Compression.ZipFile]::OpenRead($zip)
try{
 foreach($entry in $archive.Entries){
  $target=Assert-WorkspacePath (Join-Path $package $entry.FullName)
  if(-not $target.StartsWith($package+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe update path'}
  [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$target,$true)
 }
}finally{$archive.Dispose()}
$after=(Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash
if($before-ne$after){throw 'Portable update changed data'}
$report=@{status='passed';generatedUtc=[datetime]::UtcNow.ToString('o');dataSha256=$after;scope='Extract clean release twice over isolated synthetic Data; no user directories touched';package=$package}
$reportPath=Assert-WorkspacePath (Join-Path $run 'report.json')
$report|ConvertTo-Json|Set-Content -LiteralPath $reportPath -Encoding utf8
Write-Host ('PASS portable archive update preserves existing Data: '+$run)
