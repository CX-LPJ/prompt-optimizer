param([switch]$Ai)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PromptHotkeyTestNative {
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
}
'@
function Send-OptimizerKey([byte]$Key, [bool]$Shift = $false) {
    foreach ($vk in @(0x11,0x12,0x10)) { if (([PromptHotkeyTestNative]::GetAsyncKeyState($vk) -band 0x8000) -ne 0) { throw 'A modifier is held; test skipped.' } }
    try {
        [PromptHotkeyTestNative]::keybd_event(0x11,0,0,[UIntPtr]::Zero)
        [PromptHotkeyTestNative]::keybd_event(0x12,0,0,[UIntPtr]::Zero)
        if($Shift){[PromptHotkeyTestNative]::keybd_event(0x10,0,0,[UIntPtr]::Zero)}
        [PromptHotkeyTestNative]::keybd_event($Key,0,0,[UIntPtr]::Zero)
    } finally {
        [PromptHotkeyTestNative]::keybd_event($Key,0,2,[UIntPtr]::Zero)
        if($Shift){[PromptHotkeyTestNative]::keybd_event(0x10,0,2,[UIntPtr]::Zero)}
        [PromptHotkeyTestNative]::keybd_event(0x12,0,2,[UIntPtr]::Zero)
        [PromptHotkeyTestNative]::keybd_event(0x11,0,2,[UIntPtr]::Zero)
    }
}
$windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition)
$root = $null
foreach($window in $windows){
    $process = Get-Process -Id $window.Current.ProcessId -ErrorAction SilentlyContinue
    if($process.Path -match '\\WindowsApps\\OpenAI\.Codex_[^\\]+\\app\\ChatGPT\.exe$'){ $root=$window; break }
}
if(-not $root){throw 'Codex window not found.'}
[void][PromptHotkeyTestNative]::SetForegroundWindow([IntPtr]$root.Current.NativeWindowHandle)
$editors=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Edit)))
$editor=@($editors | Where-Object { $_.Current.ClassName.Split(' ') -contains 'ProseMirror' })[0]
if(-not $editor){throw 'Composer not found.'}
$value=$editor.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
if($value.Current.Value.Trim() -cne $editor.Current.Name -and $value.Current.Value.Trim().Length -ne 0){throw 'Draft is not empty; test skipped.'}
$sample='帮我找5篇MOF荧光传感文献，附DOI，不要综述。'
$known=@($sample)
$measure=[ordered]@{}
try {
    $editor.SetFocus(); $value.SetValue($sample); Start-Sleep -Milliseconds 180
    $watch=[Diagnostics.Stopwatch]::StartNew()
    Send-OptimizerKey 0x50
    $deadline=[DateTime]::UtcNow.AddSeconds(3)
    do { Start-Sleep -Milliseconds 15; $current=$value.Current.Value.TrimEnd("`r","`n") } while(-not $current.StartsWith('请完成以下任务。') -and [DateTime]::UtcNow -lt $deadline)
    if(-not $current.StartsWith('请完成以下任务。') -or -not $current.Contains($sample)){throw 'Local hotkey failed.'}
    $known+=$current; $measure.localHotkeyMs=$watch.Elapsed.TotalMilliseconds
    Send-OptimizerKey 0x4F
    Start-Sleep -Milliseconds 120
    if($value.Current.Value.TrimEnd("`r","`n") -cne $sample){throw 'Restore hotkey failed.'}
    $measure.restoreHotkeyVerified=$true
    if($Ai){
        $watch.Restart(); Send-OptimizerKey 0x50 $true
        $deadline=[DateTime]::UtcNow.AddSeconds(45)
        do { Start-Sleep -Milliseconds 70; $current=$value.Current.Value.TrimEnd("`r","`n") } while($current -ceq $sample -and [DateTime]::UtcNow -lt $deadline)
        if($current -ceq $sample -or -not $current.Contains('5') -or -not $current.Contains('DOI') -or -not $current.Contains('综述')){throw 'AI hotkey failed.'}
        $known+=$current; $measure.aiHotkeyMs=$watch.Elapsed.TotalMilliseconds
        Send-OptimizerKey 0x4F; Start-Sleep -Milliseconds 120
        if($value.Current.Value.TrimEnd("`r","`n") -cne $sample){throw 'AI restore failed.'}
        $measure.aiRestoreVerified=$true
    }
    $measure.ok=$true
    $measure | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot '..\hotkey-smoke.json') -Encoding utf8
    $measure | ConvertTo-Json -Compress
} finally {
    if($known -ccontains $value.Current.Value.TrimEnd("`r","`n")){$value.SetValue('')}
}
