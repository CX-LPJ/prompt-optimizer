$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;using System.Runtime.InteropServices;
public static class TranslationSmokeNative {
 [DllImport("user32.dll")] public static extern void keybd_event(byte key,byte scan,uint flags,UIntPtr extra);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
}
'@
function Key([byte]$code){[TranslationSmokeNative]::keybd_event($code,0,0,[UIntPtr]::Zero);[TranslationSmokeNative]::keybd_event($code,0,2,[UIntPtr]::Zero)}
function End-OfDraft {
    try{[TranslationSmokeNative]::keybd_event(0x11,0,0,[UIntPtr]::Zero);Key 0x23}
    finally{[TranslationSmokeNative]::keybd_event(0x11,0,2,[UIntPtr]::Zero)}
    Start-Sleep -Milliseconds 100
}
function Restore-Key([string]$shortcut){
    $parts=$shortcut.Split('+');$mods=@();if($parts -contains 'Ctrl'){$mods+=0x11};if($parts -contains 'Alt'){$mods+=0x12};if($parts -contains 'Shift'){$mods+=0x10}
    $last=$parts[-1];$code=if($last -match '^F(\d+)$'){0x70+[int]$Matches[1]-1}else{[int][char]$last}
    try{foreach($mod in $mods){[TranslationSmokeNative]::keybd_event($mod,0,0,[UIntPtr]::Zero)};Key $code}
    finally{[array]::Reverse($mods);foreach($mod in $mods){[TranslationSmokeNative]::keybd_event($mod,0,2,[UIntPtr]::Zero)}}
}
foreach($key in @(0x11,0x12,0x10,0x20)){if(([TranslationSmokeNative]::GetAsyncKeyState($key) -band 0x8000) -ne 0){throw 'A key is currently held; test skipped.'}}
$windows=[System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition)
$root=@($windows|Where-Object{(Get-Process -Id $_.Current.ProcessId -ErrorAction SilentlyContinue).Path -match '\\WindowsApps\\OpenAI\.Codex_[^\\]+\\app\\ChatGPT\.exe$'})[0]
if(-not $root){throw 'Codex window not found.'}
[void][TranslationSmokeNative]::SetForegroundWindow([IntPtr]$root.Current.NativeWindowHandle)
$editor=@($root.FindAll([System.Windows.Automation.TreeScope]::Descendants,(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Edit)))|Where-Object{$_.Current.ClassName.Split(' ') -contains 'ProseMirror'})[0]
if(-not $editor){throw 'Composer not found.'}
$value=$editor.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
if($value.Current.Value.Trim() -cne $editor.Current.Name -and $value.Current.Value.Trim().Length -ne 0){throw 'Draft is not empty; test skipped.'}
$config=Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\config.json') -Raw|ConvertFrom-Json
$known=[Collections.Generic.List[string]]::new();$report=[ordered]@{cases=@()}
try{
    $sample='Do not translate a held space.';$known.Add($sample);$known.Add($sample+'  ')
    $editor.SetFocus();$value.SetValue($sample);End-OfDraft
    [TranslationSmokeNative]::keybd_event(0x20,0,0,[UIntPtr]::Zero);Start-Sleep -Milliseconds 80;[TranslationSmokeNative]::keybd_event(0x20,0,0,[UIntPtr]::Zero);[TranslationSmokeNative]::keybd_event(0x20,0,2,[UIntPtr]::Zero)
    Start-Sleep -Milliseconds 600
    if($value.Current.Value.TrimEnd("`r","`n") -cne $sample+'  '){throw 'Held-space protection failed.'}
    $report.heldSpaceIgnored=$true
    # Cross-application behavior is exercised by external-smoke.ps1.
    foreach($sample in @('请保留0.5 mM，不要加热。','Keep 0.5 mM; do not heat.')){
        $known.Add($sample);$known.Add($sample+'  ');$editor.SetFocus();$value.SetValue($sample);End-OfDraft
        $watch=[Diagnostics.Stopwatch]::StartNew();Key 0x20;Start-Sleep -Milliseconds 90;Key 0x20
        $deadline=[DateTime]::UtcNow.AddSeconds(45)
        do{Start-Sleep -Milliseconds 75;$current=$value.Current.Value.TrimEnd("`r","`n")}while(($current -ceq $sample -or $current -ceq $sample+'  ') -and [DateTime]::UtcNow -lt $deadline)
        if($current -ceq $sample -or $current -ceq $sample+'  '){
            $text=$editor.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern);$selection=$text.GetSelection();$prefix=$text.DocumentRange.Clone();$prefix.MoveEndpointByRange([System.Windows.Automation.Text.TextPatternRangeEndpoint]::End,$selection[0],[System.Windows.Automation.Text.TextPatternRangeEndpoint]::Start)
            throw ('Translation did not fill. Text='+$current+'; prefix='+$prefix.GetText(-1))
        }
        $known.Add($current)
        if($current.StartsWith('"') -or $current.StartsWith('“')){throw 'Translation has added wrapper quotes.'}
        if(-not $current.Contains('0.5') -or -not $current.Contains('mM')){throw 'Scientific value or unit lost.'}
        if($sample.StartsWith('请')){if($current -match '[\u3400-\u9fff]' -or $current -notmatch 'heat' -or $current -notmatch 'not|no |without'){throw ('Chinese-to-English content check failed: '+$current)}}
        else{if($current -notmatch '[\u3400-\u9fff]' -or $current -notmatch '不要|不得|勿|禁止' -or $current -notmatch '热'){throw ('English-to-Chinese content check failed: '+$current)}}
        $report.cases+=@{source=$sample;translation=$current;elapsedMs=$watch.Elapsed.TotalMilliseconds}
        Restore-Key $config.restoreHotkey;Start-Sleep -Milliseconds 180
        if($value.Current.Value.TrimEnd("`r","`n") -cne $sample){throw 'Restoring translated draft failed.'}
    }
    $report.restoreVerified=$true;$report.ok=$true
    $report|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $PSScriptRoot '..\translation-smoke.json') -Encoding utf8
    $report|ConvertTo-Json -Depth 5 -Compress
}finally{
    if($known.Contains($value.Current.Value.TrimEnd("`r","`n"))){$value.SetValue('')}
}
