param([Parameter(Mandatory=$true)][string]$Candidate)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($PSScriptRoot)
$candidatePath=[IO.Path]::GetFullPath($Candidate)
if(-not $candidatePath.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Candidate must be inside the tool directory.'}
$exe=Join-Path $root 'PromptOptimizer.exe'
$configHash=(Get-FileHash -LiteralPath (Join-Path $root 'config.json')).Hash
Add-Type @'
using System;using System.Text;using System.Runtime.InteropServices;
public static class PromptLocalUpdate {
 public delegate bool Callback(IntPtr h,IntPtr p);
 [DllImport("user32.dll")]public static extern bool EnumWindows(Callback c,IntPtr p);
 [DllImport("user32.dll")]public static extern bool EnumChildWindows(IntPtr h,Callback c,IntPtr p);
 [DllImport("user32.dll")]public static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="SendMessageW")]public static extern IntPtr ReadText(IntPtr h,uint m,IntPtr n,StringBuilder t);
 [DllImport("user32.dll")]public static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
 public static string Text(IntPtr h){var t=new StringBuilder(256);ReadText(h,13,(IntPtr)256,t);return t.ToString();}
 public static IntPtr Main(uint pid){IntPtr found=IntPtr.Zero;EnumWindows((h,p)=>{uint actual;GetWindowThreadProcessId(h,out actual);if(actual==pid&&Text(h)=="提示词快捷优化"){found=h;return false;}return true;},IntPtr.Zero);return found;}
 public static bool Busy(IntPtr main){bool busy=false;uint owner;GetWindowThreadProcessId(main,out owner);EnumWindows((h,p)=>{uint actual;GetWindowThreadProcessId(h,out actual);if(actual==owner&&h!=main&&(Text(h)=="供应商与模型"||Text(h)=="快捷键设置"))busy=true;return true;},IntPtr.Zero);EnumChildWindows(main,(h,p)=>{string t=Text(h);if(t.StartsWith("AI 重写中 ")||t.StartsWith("翻译中 ")||t.StartsWith("AI 连接中 "))busy=true;return true;},IntPtr.Zero);return busy;}
}
'@
foreach($process in @(Get-Process -Name PromptOptimizer -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $exe})){
    $handle=[PromptLocalUpdate]::Main($process.Id)
    if($handle -eq [IntPtr]::Zero){throw 'Cannot identify the running tool window.'}
    $deadline=[DateTime]::UtcNow.AddSeconds(45)
    while([PromptLocalUpdate]::Busy($handle) -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 250}
    if([PromptLocalUpdate]::Busy($handle)){throw 'AI is still working; update deferred.'}
    [void][PromptLocalUpdate]::PostMessage($handle,0x8043,[IntPtr]::Zero,[IntPtr]::Zero)
    if(-not $process.WaitForExit(5000)){throw 'Tool has not exited; update deferred.'}
}
if(Test-Path -LiteralPath $exe){Copy-Item -LiteralPath $exe -Destination (Join-Path $root 'PromptOptimizer.previous.exe') -Force}
Copy-Item -LiteralPath $candidatePath -Destination $exe -Force
$app=Start-Process -FilePath $exe -WindowStyle Hidden -PassThru
$deadline=[DateTime]::UtcNow.AddSeconds(8)
do{Start-Sleep -Milliseconds 200;$handle=[PromptLocalUpdate]::Main($app.Id)}while($handle -eq [IntPtr]::Zero -and -not $app.HasExited -and [DateTime]::UtcNow -lt $deadline)
if($handle -eq [IntPtr]::Zero){throw 'Updated tool failed to start.'}
if((Get-FileHash -LiteralPath (Join-Path $root 'config.json')).Hash -ne $configHash){throw 'Configuration changed during update.'}
[pscustomobject]@{ok=$true;processId=$app.Id;settingsPreserved=$true}|ConvertTo-Json -Compress
