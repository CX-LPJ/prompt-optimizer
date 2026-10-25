$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class ShortcutUiTest {
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr h);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
 [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a,uint b,bool attach);
 [DllImport("user32.dll")] public static extern void keybd_event(byte key,byte scan,uint flags,UIntPtr extra);
 [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h,uint msg,IntPtr w,IntPtr l);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h,StringBuilder text,int max);
 [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="SendMessageW")] public static extern IntPtr ReadText(IntPtr h,uint msg,IntPtr max,StringBuilder text);
 [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h,int id,uint mods,uint key);
 [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h,int id);
}
'@
$dir = Split-Path $PSScriptRoot -Parent
$file = Join-Path $dir 'config.json'
$original = [IO.File]::ReadAllText($file)
$settings = $original | ConvertFrom-Json
$complete = $false
function Open-Settings {
    Start-Process -FilePath (Join-Path $dir 'PromptOptimizer.exe') -ArgumentList '--shortcuts' -WindowStyle Hidden -Wait
    Start-Sleep -Milliseconds 180
    $owner=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,'提示词快捷优化')))
    if(-not $owner){throw 'Optimizer window not ready.'}
    $dialog=$owner.FindFirst([System.Windows.Automation.TreeScope]::Descendants,(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,'快捷键设置')))
    if(-not $dialog){throw 'Settings dialog not found.'}
    return $dialog
}
function Read-Control($element){
    $text=[Text.StringBuilder]::new(200)
    [void][ShortcutUiTest]::ReadText([IntPtr]$element.Current.NativeWindowHandle,0xD,[IntPtr]200,$text)
    return $text.ToString()
}
function Send-Combination([string]$value){
    $parts=$value.Split('+'); $modifiers=@()
    if($parts -contains 'Ctrl'){$modifiers+=0x11};if($parts -contains 'Alt'){$modifiers+=0x12};if($parts -contains 'Shift'){$modifiers+=0x10}
    $last=$parts[-1]; $key=if($last -match '^F(\d+)$'){0x70+[int]$Matches[1]-1}else{[int][char]$last}
    try{foreach($m in $modifiers){[ShortcutUiTest]::keybd_event($m,0,0,[UIntPtr]::Zero)};[ShortcutUiTest]::keybd_event($key,0,0,[UIntPtr]::Zero)}
    finally{[ShortcutUiTest]::keybd_event($key,0,2,[UIntPtr]::Zero);[array]::Reverse($modifiers);foreach($m in $modifiers){[ShortcutUiTest]::keybd_event($m,0,2,[UIntPtr]::Zero)}}
}
function Record-Key($dialog,$element,[string]$value){
    $handle=[IntPtr]$element.Current.NativeWindowHandle; [uint32]$targetPid=0
    $targetThread=[ShortcutUiTest]::GetWindowThreadProcessId($handle,[ref]$targetPid);$ourThread=[ShortcutUiTest]::GetCurrentThreadId()
    [void][ShortcutUiTest]::AttachThreadInput($ourThread,$targetThread,$true)
    try{[void][ShortcutUiTest]::SetForegroundWindow([IntPtr]$dialog.Current.NativeWindowHandle);[void][ShortcutUiTest]::SetFocus($handle);[void][ShortcutUiTest]::SendMessage($handle,0x201,[IntPtr]1,[IntPtr]655370);[void][ShortcutUiTest]::SendMessage($handle,0x202,[IntPtr]::Zero,[IntPtr]655370)}
    finally{[void][ShortcutUiTest]::AttachThreadInput($ourThread,$targetThread,$false)}
    $focused=[System.Windows.Automation.AutomationElement]::FocusedElement.Current.NativeWindowHandle
    if($focused -ne $handle.ToInt32()){throw ('Focus failed: expected '+$handle+' actual '+$focused+' dialog '+$dialog.Current.NativeWindowHandle)}
    Send-Combination $value; Start-Sleep -Milliseconds 250
    if((Read-Control $element) -cne $value){throw ('Recording failed: '+$value+' got '+(Read-Control $element)+' focus '+[System.Windows.Automation.AutomationElement]::FocusedElement.Current.NativeWindowHandle)}
}
function Editors($dialog){ return @($dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.ClassName -match '\.EDIT\.' }) }
function Save-Dialog($dialog){
    $button=$dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants,(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,'保存并生效')))
    [void][ShortcutUiTest]::SendMessage([IntPtr]$button.Current.NativeWindowHandle,0xF5,[IntPtr]::Zero,[IntPtr]::Zero)
    Start-Sleep -Milliseconds 150
}
try{
    $dialog=Open-Settings; $inputs=Editors $dialog
    if($inputs.Count -ne 3){throw 'Expected three shortcut boxes.'}
    Record-Key $dialog $inputs[0] $settings.aiHotkey # Already registered combinations must remain recordable.
    $new=@('Ctrl+Shift+F8','Ctrl+Shift+F9','Ctrl+Shift+F10')
    for($i=0;$i -lt 3;$i++){Record-Key $dialog $inputs[$i] $new[$i]}
    if(-not [ShortcutUiTest]::RegisterHotKey([IntPtr]::Zero,17777,0x4006,0x78)){throw 'Cannot reserve collision test key.'}
    try{
        Save-Dialog $dialog
        if([IO.File]::ReadAllText($file) -cne $original){throw 'Conflict changed configuration.'}
        $errorLabel=$dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -match '已被系统或其他程序占用' }
        if(-not $errorLabel){throw 'Conflict feedback missing.'}
    }finally{[void][ShortcutUiTest]::UnregisterHotKey([IntPtr]::Zero,17777)}
    Save-Dialog $dialog
    $saved=Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    if($saved.localHotkey -cne $new[0] -or $saved.aiHotkey -cne $new[1] -or $saved.restoreHotkey -cne $new[2]){throw 'New shortcuts were not saved.'}
    if(-not [ShortcutUiTest]::RegisterHotKey([IntPtr]::Zero,17778,0x4003,0x50)){throw 'Old local shortcut not released.'}
    [void][ShortcutUiTest]::UnregisterHotKey([IntPtr]::Zero,17778)
    # Verify the new local/restore combinations against the actual empty Codex draft.
    $windows=[System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition)
    $codex=@($windows | Where-Object { (Get-Process -Id $_.Current.ProcessId -ErrorAction SilentlyContinue).Path -match '\\WindowsApps\\OpenAI\.Codex_[^\\]+\\app\\ChatGPT\.exe$' })[0]
    [void][ShortcutUiTest]::SetForegroundWindow([IntPtr]$codex.Current.NativeWindowHandle)
    $editor=@($codex.FindAll([System.Windows.Automation.TreeScope]::Descendants,(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Edit))) | Where-Object { $_.Current.ClassName.Split(' ') -contains 'ProseMirror' })[0]
    $v=$editor.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    if($v.Current.Value.Trim() -cne $editor.Current.Name -and $v.Current.Value.Trim().Length -ne 0){throw 'Draft not empty; test stopped.'}
    $sample='帮我分析错误原因，不要修改代码。';$known=@($sample)
    try{
        $editor.SetFocus();$v.SetValue($sample);Start-Sleep -Milliseconds 180;Send-Combination $new[0];Start-Sleep -Milliseconds 400
        $optimized=$v.Current.Value.TrimEnd("`r","`n")
        if($optimized -cne '请分析错误原因，不要修改代码。'){throw 'New local hotkey did not apply immediately.'}
        $known+=$optimized;Send-Combination $new[2];Start-Sleep -Milliseconds 180
        if($v.Current.Value.TrimEnd("`r","`n") -cne $sample){throw 'New restore hotkey failed.'}
    }finally{if($known -ccontains $v.Current.Value.TrimEnd("`r","`n")){$v.SetValue('')}}
    $dialog=Open-Settings;$inputs=Editors $dialog
    $old=@($settings.localHotkey,$settings.aiHotkey,$settings.restoreHotkey)
    for($i=0;$i -lt 3;$i++){Record-Key $dialog $inputs[$i] $old[$i]};Save-Dialog $dialog
    $restored=Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    if($restored.localHotkey -cne $old[0] -or $restored.aiHotkey -cne $old[1] -or $restored.restoreHotkey -cne $old[2]){throw 'Original shortcuts were not restored.'}
    $report=[ordered]@{ok=$true;recordKeysVerified=$true;recordExistingHotkeyVerified=$true;conflictRollbackVerified=$true;savedWithoutRestart=$true;newLocalAndRestoreVerified=$true;originalShortcutsRestored=$true}
    $report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dir 'shortcuts-smoke.json') -Encoding utf8
    $report | ConvertTo-Json -Compress; $complete=$true
}finally{
    if(-not $complete){
        Start-Process -FilePath (Join-Path $dir 'PromptOptimizer.exe') -ArgumentList '--quit' -WindowStyle Hidden -Wait
        Start-Sleep -Milliseconds 250
        [IO.File]::WriteAllText($file,$original,[Text.UTF8Encoding]::new($false))
        Start-Process -FilePath (Join-Path $dir 'PromptOptimizer.exe') -WindowStyle Hidden
    }
}
