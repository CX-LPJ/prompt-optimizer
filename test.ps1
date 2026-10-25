$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    & (Join-Path $PSScriptRoot 'build.ps1')
    $output = Join-Path $PSScriptRoot 'artifacts'
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $report = Join-Path $output 'self-test.json'
    $process = Start-Process -FilePath (Join-Path $PSScriptRoot 'PromptOptimizer.exe') -ArgumentList @('--self-test',('"' + $report + '"')) -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw 'C# checks failed. See artifacts/self-test.json.' }
    $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if (-not $result.ok) { throw 'C# check report indicates failure.' }
    Write-Output ('C# checks passed: ' + $result.passed.Count)
    $node = Get-Command node.exe -ErrorAction SilentlyContinue
    if (-not $node) { throw 'Node.js is required to run worker tests.' }
    & $node.Source --test (Join-Path $PSScriptRoot 'tests\worker.test.cjs')
    if ($LASTEXITCODE -ne 0) { throw 'Node.js checks failed.' }
} finally { Pop-Location }
