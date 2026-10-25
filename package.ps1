param([string]$Version = '0.5.0')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must use x.y.z format.' }
& (Join-Path $PSScriptRoot 'build.ps1')
$output = Join-Path $PSScriptRoot 'artifacts'
$stage = Join-Path $output ('package-' + $Version + '-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
foreach ($file in @('PromptOptimizer.exe','ai-worker.cjs','提示词优化专用指令.txt','prompt.ico','使用说明.md','README.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $stage $file)
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'config.example.json') -Destination (Join-Path $stage 'config.json')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'config.example.json') -Destination (Join-Path $stage 'config.example.json')
$logoDirectory = Join-Path $stage 'assets\icon-v2'
New-Item -ItemType Directory -Path $logoDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'assets\icon-v2\prompt-optimizer-v2.png') -Destination $logoDirectory
$archive = Join-Path $output ('prompt-optimizer-v' + $Version + '-windows-x64.zip')
Compress-Archive -LiteralPath (Get-ChildItem -LiteralPath $stage | Select-Object -ExpandProperty FullName) -DestinationPath $archive -Force
Get-FileHash -LiteralPath $archive -Algorithm SHA256 | ForEach-Object { $_.Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($_.Path) } | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
Write-Output $archive
