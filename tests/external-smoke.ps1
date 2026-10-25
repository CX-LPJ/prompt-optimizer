param([switch]$WebOnly)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;using System.Text;using System.Runtime.InteropServices;
public static class ExternalSmokeNative {
 public delegate bool Callback(IntPtr h,IntPtr p);
 [DllImport("user32.dll")]public static extern bool EnumWindows(Callback c,IntPtr p);
 [DllImport("user32.dll")]public static extern bool EnumChildWindows(IntPtr h,Callback c,IntPtr p);
 [DllImport("user32.dll")]public static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [DllImport("user32.dll",EntryPoint="GetWindowLongW")]public static extern int Style(IntPtr h,int i);
 [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="SendMessageW")]public static extern IntPtr ReadText(IntPtr h,uint m,IntPtr n,StringBuilder t);
 [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="SendMessageW")]public static extern IntPtr WriteText(IntPtr h,uint m,IntPtr n,string t);
 [DllImport("user32.dll")]public static extern IntPtr SendMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
 [DllImport("user32.dll")]public static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
 [DllImport("user32.dll")]public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")]public static extern IntPtr SetFocus(IntPtr h);
 [DllImport("user32.dll")]public static extern bool AttachThreadInput(uint a,uint b,bool x);
 [DllImport("kernel32.dll")]public static extern uint GetCurrentThreadId();
 [DllImport("user32.dll")]public static extern void keybd_event(byte k,byte s,uint f,UIntPtr e);
 [DllImport("user32.dll")]public static extern bool IsWindowVisible(IntPtr h);
 public static string Text(IntPtr h){var t=new StringBuilder(21000);ReadText(h,13,(IntPtr)t.Capacity,t);return t.ToString();}
 public static IntPtr Main(uint pid){IntPtr found=IntPtr.Zero;EnumWindows((h,p)=>{uint actual;GetWindowThreadProcessId(h,out actual);if(actual==pid&&Text(h)=="提示词快捷优化"){found=h;return false;}return true;},IntPtr.Zero);return found;}
 public static string Status(IntPtr main){string state="";EnumChildWindows(main,(h,p)=>{string t=Text(h);if(t.StartsWith("翻译中 "))state="busy";else if(t.StartsWith("翻译未完成"))state="failed";else if(t.StartsWith("翻译 "))state="done";return true;},IntPtr.Zero);return state;}
}
'@
$dir=Split-Path $PSScriptRoot -Parent
$config=Get-Content -LiteralPath (Join-Path $dir 'config.json') -Raw|ConvertFrom-Json
$app=Get-Process -Name PromptOptimizer|Where-Object{$_.Path -eq (Join-Path $dir 'PromptOptimizer.exe')}
$main=[ExternalSmokeNative]::Main($app.Id)
function Key([byte]$code){[ExternalSmokeNative]::keybd_event($code,0,0,[UIntPtr]::Zero);[ExternalSmokeNative]::keybd_event($code,0,2,[UIntPtr]::Zero)}
function Combo([string]$value){
 $parts=$value.Split('+');$mods=@();if($parts -contains 'Ctrl'){$mods+=0x11};if($parts -contains 'Alt'){$mods+=0x12};if($parts -contains 'Shift'){$mods+=0x10};$last=$parts[-1]
 $key=if($last -eq 'End'){0x23}elseif($last -match '^F(\d+)$'){0x70+[int]$Matches[1]-1}else{[int][char]$last}
 try{foreach($m in $mods){[ExternalSmokeNative]::keybd_event($m,0,0,[UIntPtr]::Zero)};Key $key}finally{[array]::Reverse($mods);foreach($m in $mods){[ExternalSmokeNative]::keybd_event($m,0,2,[UIntPtr]::Zero)}}
}
function Find-Window([string]$title){
 $deadline=[DateTime]::UtcNow.AddSeconds(8)
 do{$r=[System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition)|Where-Object{$_.Current.Name -like $title}|Select-Object -First 1;if(-not $r){Start-Sleep -Milliseconds 150}}while(-not $r -and [DateTime]::UtcNow -lt $deadline)
 if(-not $r){throw ('Window not found: '+$title)};return $r
}
function Focus-Editor($root,$editor){
 [void][ExternalSmokeNative]::SetForegroundWindow([IntPtr]$root.Current.NativeWindowHandle)
 $h=[IntPtr]$editor.Current.NativeWindowHandle
 if($h -ne [IntPtr]::Zero){[uint32]$targetPid=0;$tid=[ExternalSmokeNative]::GetWindowThreadProcessId($h,[ref]$targetPid);$own=[ExternalSmokeNative]::GetCurrentThreadId();[void][ExternalSmokeNative]::AttachThreadInput($own,$tid,$true);try{[void][ExternalSmokeNative]::SetFocus($h)}finally{[void][ExternalSmokeNative]::AttachThreadInput($own,$tid,$false)}}else{$editor.SetFocus()}
 Start-Sleep -Milliseconds 80
}
function Read-Editor($editor){if($editor.Current.NativeWindowHandle -ne 0 -and $editor.Current.ClassName -match 'EDIT|RichEdit'){return [ExternalSmokeNative]::Text([IntPtr]$editor.Current.NativeWindowHandle)};return $editor.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value}
function Set-Editor($editor,[string]$text){if($editor.Current.NativeWindowHandle -ne 0 -and $editor.Current.ClassName -match 'EDIT|RichEdit'){[void][ExternalSmokeNative]::WriteText([IntPtr]$editor.Current.NativeWindowHandle,0xC,[IntPtr]::Zero,$text)}else{$editor.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text)}}
function Gesture{Combo 'Ctrl+End';Start-Sleep -Milliseconds 120;Key 0x20;Start-Sleep -Milliseconds 90;Key 0x20}
function Test-Translation($root,$editor,[string]$label,[string]$sample){
 Focus-Editor $root $editor;Set-Editor $editor $sample;Gesture;$watch=[Diagnostics.Stopwatch]::StartNew();$deadline=[DateTime]::UtcNow.AddSeconds(45)
 do{Start-Sleep -Milliseconds 75;$text=(Read-Editor $editor).TrimEnd("`r","`n")}while(($text -ceq $sample -or $text -ceq $sample+'  ') -and [DateTime]::UtcNow -lt $deadline)
 if($text -ceq $sample -or $text -ceq $sample+'  '){throw ('Translation did not fill: '+$label+'; status='+[ExternalSmokeNative]::Status($main))}
 if(-not $text.Contains('0.5') -or -not $text.Contains('mM')){throw ('Value or unit changed: '+$label)}
 if($sample -match '[\u3400-\u9fff]'){if($text -match '[\u3400-\u9fff]' -or $text -notmatch 'heat'){throw 'Expected English translation'}}elseif($text -notmatch '[\u3400-\u9fff]'){throw 'Expected Chinese translation'}
 $entry=@{app=$label;translation=$text;elapsedMs=$watch.Elapsed.TotalMilliseconds}
 Combo $config.restoreHotkey;Start-Sleep -Milliseconds 160
 if((Read-Editor $editor).TrimEnd("`r","`n") -cne $sample){throw ('Restore failed: '+$label)}
 return $entry
}
$report=[ordered]@{cases=@()}
$fixture=$null
if(-not $WebOnly){$fixture=Start-Process -FilePath (Join-Path $PSScriptRoot 'EditorFixture.exe') -WindowStyle Normal -PassThru}
try{
 if(-not $WebOnly){
 $root=Find-Window 'Prompt Optimizer External Test'
 $all=@($root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition))
 $plain=@($all|Where-Object{$_.Current.ClassName -match '^WindowsForms10\.EDIT' -and ([ExternalSmokeNative]::Style([IntPtr]$_.Current.NativeWindowHandle,-16) -band 0x820) -eq 0})[0]
 $password=@($all|Where-Object{$_.Current.ClassName -match '^WindowsForms10\.EDIT' -and ([ExternalSmokeNative]::Style([IntPtr]$_.Current.NativeWindowHandle,-16) -band 0x20) -ne 0})[0]
 $readonly=@($all|Where-Object{$_.Current.ClassName -match '^WindowsForms10\.EDIT' -and ([ExternalSmokeNative]::Style([IntPtr]$_.Current.NativeWindowHandle,-16) -band 0x800) -ne 0})[0]
 foreach($editor in @($password,$readonly)){
  if(-not $editor){throw 'Safety fixture missing'};Focus-Editor $root $editor;Gesture;$deadline=[DateTime]::UtcNow.AddMilliseconds(700)
  do{Start-Sleep -Milliseconds 40;if([ExternalSmokeNative]::Status($main) -eq 'busy'){throw 'Protected input started translation'}}while([DateTime]::UtcNow -lt $deadline)
 }
 $report.passwordSkipped=$true;$report.readonlySkipped=$true
 $report.cases+=Test-Translation $root $plain 'Windows text box' 'Keep 0.5 mM; do not heat.'
 $rich=@($all|Where-Object{$_.Current.ClassName -match 'RichEdit'})[0]
 if(-not $rich){throw 'Rich text fixture missing'}
 Focus-Editor $root $rich;Gesture;$deadline=[DateTime]::UtcNow.AddSeconds(5)
 do{Start-Sleep -Milliseconds 50;$state=[ExternalSmokeNative]::Status($main)}while($state -ne 'busy' -and [DateTime]::UtcNow -lt $deadline)
 if($state -ne 'busy'){throw 'Rich text preview did not start'}
 $deadline=[DateTime]::UtcNow.AddSeconds(45)
 do{Start-Sleep -Milliseconds 75;$state=[ExternalSmokeNative]::Status($main)}while($state -eq 'busy' -and [DateTime]::UtcNow -lt $deadline)
 if($state -ne 'done' -or (Read-Editor $rich).TrimEnd("`r","`n") -cne 'Keep all 5 samples unchanged.  ' -or -not [ExternalSmokeNative]::IsWindowVisible($main)){throw 'Rich text was not preserved with a preview'}
 $report.richTextPreviewVerified=$true
 [void][ExternalSmokeNative]::PostMessage($main,0x10,[IntPtr]::Zero,[IntPtr]::Zero)
 $report|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $dir 'external-smoke.json') -Encoding utf8
 Start-Process -FilePath 'notepad.exe' -ArgumentList ('"'+(Join-Path $PSScriptRoot 'external-translation-test.txt')+'"') -WindowStyle Normal
 $notepad=Find-Window '*external-translation-test.txt*'
 $edit=$notepad.FindFirst([System.Windows.Automation.TreeScope]::Descendants,(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty,'RichEditD2DPT')))
 if(-not $edit){throw 'Notepad input missing'}
 $report.cases+=Test-Translation $notepad $edit 'Windows Notepad' 'Keep 0.5 mM; do not heat.'
 $report|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $dir 'external-smoke.json') -Encoding utf8
 # Save only the workspace-owned test file, then close its current tab.
 Combo 'Ctrl+S';Start-Sleep -Milliseconds 160;Combo 'Ctrl+W'
 }
 $page=(New-Object Uri((Join-Path $PSScriptRoot 'web-editor.html'))).AbsoluteUri
 $web=[System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition)|Where-Object{$_.Current.Name -eq 'Prompt Optimizer Web Test'}|Select-Object -First 1
 if(-not $web){Start-Process -FilePath 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe' -ArgumentList @('--no-first-run','--no-default-browser-check',('--app='+$page)) -WindowStyle Normal;$web=Find-Window '*Prompt Optimizer Web Test*'}
 [void][ExternalSmokeNative]::SendMessage([IntPtr]$web.Current.NativeWindowHandle,0x3D,[IntPtr]::Zero,[IntPtr](-25));Start-Sleep -Milliseconds 250
 $deadline=[DateTime]::UtcNow.AddSeconds(8)
 do{$edit=$web.FindFirst([System.Windows.Automation.TreeScope]::Descendants,(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'draft')));if(-not $edit){Start-Sleep -Milliseconds 150}}while(-not $edit -and [DateTime]::UtcNow -lt $deadline)
 if(-not $edit){throw 'Web textarea missing'}
 $report.cases+=Test-Translation $web $edit 'Edge web textarea' '请保留0.5 mM，不要加热。'
 $password=$web.FindFirst([System.Windows.Automation.TreeScope]::Descendants,(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'password')))
 if(-not $password -or -not $password.Current.IsPassword){throw 'Web password fixture missing'}
 Focus-Editor $web $password;Gesture;$deadline=[DateTime]::UtcNow.AddMilliseconds(700)
 do{Start-Sleep -Milliseconds 40;if([ExternalSmokeNative]::Status($main) -eq 'busy'){throw 'Web password started translation'}}while([DateTime]::UtcNow -lt $deadline)
 $report.webPasswordSkipped=$true
 [void][ExternalSmokeNative]::PostMessage([IntPtr]$web.Current.NativeWindowHandle,0x10,[IntPtr]::Zero,[IntPtr]::Zero)
 $report.restoreVerified=$true;$report.ok=$true
 $report|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $dir 'external-smoke.json') -Encoding utf8
 $report|ConvertTo-Json -Depth 5 -Compress
}finally{
 if($fixture -and -not $fixture.HasExited){$fixture.CloseMainWindow()|Out-Null}
}
