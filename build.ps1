param([string]$OutputExe = (Join-Path $PSScriptRoot 'PromptOptimizer.exe'))
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$wpf = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'
Push-Location $PSScriptRoot
try {
    if(-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'prompt.ico'))){
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'assets\icon-v2\prompt-optimizer-v2.ico') -Destination (Join-Path $PSScriptRoot 'prompt.ico')
    }
    & $compiler /nologo /target:winexe /platform:x64 /optimize+ /utf8output "/win32icon:$PSScriptRoot\prompt.ico" "/out:$OutputExe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.Security.dll "/r:$wpf\UIAutomationClient.dll" "/r:$wpf\UIAutomationTypes.dll" "/r:$wpf\WindowsBase.dll" PromptOptimizer.cs ShortcutSettings.cs Translation.cs ExternalEditors.cs ProviderSettings.cs LocalOptimizer.cs TextReview.cs
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    Write-Output 'BUILD_OK'
} finally { Pop-Location }
