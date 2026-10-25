$ErrorActionPreference = 'Stop'
$toolDir = Split-Path $PSScriptRoot -Parent
$config = Get-Content -LiteralPath (Join-Path $toolDir 'config.json') -Raw | ConvertFrom-Json
Add-Type @'
using System; using System.Text; using System.Runtime.InteropServices;
public static class LocalRewriteSmokeNative {
 public delegate bool Callback(IntPtr h, IntPtr p);
 [DllImport("user32.dll")] public static extern bool EnumWindows(Callback c, IntPtr p);
 [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, Callback c, IntPtr p);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
 [DllImport("user32.dll", EntryPoint="GetWindowLongW")] public static extern int Style(IntPtr h, int n);
 [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="SendMessageW")] public static extern IntPtr Read(IntPtr h, uint m, IntPtr n, StringBuilder s);
 [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="SendMessageW")] public static extern IntPtr Write(IntPtr h, uint m, IntPtr n, string s);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr h);
 [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool on);
 [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
 [DllImport("user32.dll")] public static extern void keybd_event(byte k, byte s, uint f, UIntPtr e);
 [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int k);
 public static string Text(IntPtr h) { var b=new StringBuilder(21000);Read(h,13,(IntPtr)b.Capacity,b);return b.ToString(); }
 public static IntPtr Window(uint pid) { IntPtr found=IntPtr.Zero;EnumWindows((h,p)=>{uint actual;GetWindowThreadProcessId(h,out actual);if(actual==pid && Text(h)=="Prompt Optimizer External Test"){found=h;return false;}return true;},IntPtr.Zero);return found; }
 public static IntPtr Plain(IntPtr root) { IntPtr found=IntPtr.Zero;EnumChildWindows(root,(h,p)=>{var cls=new StringBuilder(256);GetClassName(h,cls,256);if(cls.ToString().StartsWith("WindowsForms10.EDIT") && (Style(h,-16)&0x820)==0){found=h;return false;}return true;},IntPtr.Zero);return found; }
}
'@
function Combination([string]$Value) {
    if([LocalRewriteSmokeNative]::GetForegroundWindow() -ne $window){throw 'Fixture lost foreground; test stopped before sending keys'}
    $parts=$Value.Split('+'); $mods=@()
    if($parts -contains 'Ctrl'){$mods+=0x11}; if($parts -contains 'Alt'){$mods+=0x12}; if($parts -contains 'Shift'){$mods+=0x10}
    $last=$parts[-1]
    $code=if($last -eq 'Space'){0x20}elseif($last -match '^F(\d+)$'){0x70+[int]$Matches[1]-1}elseif($last.Length -eq 1){[int][char]$last.ToUpperInvariant()}else{throw 'Unsupported smoke-test shortcut'}
    foreach($modifier in @(0x11,0x12,0x10)){if(([LocalRewriteSmokeNative]::GetAsyncKeyState($modifier) -band 0x8000) -ne 0){throw 'Modifier is held; test skipped'}}
    try {
        foreach($modifier in $mods){[LocalRewriteSmokeNative]::keybd_event($modifier,0,0,[UIntPtr]::Zero)}
        [LocalRewriteSmokeNative]::keybd_event($code,0,0,[UIntPtr]::Zero)
    } finally {
        [LocalRewriteSmokeNative]::keybd_event($code,0,2,[UIntPtr]::Zero)
        [array]::Reverse($mods); foreach($modifier in $mods){[LocalRewriteSmokeNative]::keybd_event($modifier,0,2,[UIntPtr]::Zero)}
    }
}
$fixture = Start-Process -FilePath (Join-Path $PSScriptRoot 'EditorFixture.exe') -WindowStyle Hidden -PassThru
try {
    $deadline=[DateTime]::UtcNow.AddSeconds(8)
    do { Start-Sleep -Milliseconds 100; $window=[LocalRewriteSmokeNative]::Window($fixture.Id) } while($window -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)
    if($window -eq [IntPtr]::Zero){throw 'Independent editor fixture did not open'}
    $editor=[LocalRewriteSmokeNative]::Plain($window); if($editor -eq [IntPtr]::Zero){throw 'Plain edit fixture missing'}
    [void][LocalRewriteSmokeNative]::SetForegroundWindow($window)
    [uint32]$targetPid=0
    $thread=[LocalRewriteSmokeNative]::GetWindowThreadProcessId($editor,[ref]$targetPid); $own=[LocalRewriteSmokeNative]::GetCurrentThreadId()
    [void][LocalRewriteSmokeNative]::AttachThreadInput($own,$thread,$true)
    try{[void][LocalRewriteSmokeNative]::SetFocus($editor)}finally{[void][LocalRewriteSmokeNative]::AttachThreadInput($own,$thread,$false)}
    Start-Sleep -Milliseconds 150
    $inline=[string][char]96
    $cases=@(
        @{source='帮我找5篇MOF文献，要DOI，不要综述，先给列表确认后再下载。';expected='请查找5篇MOF文献，附DOI，不要综述。先提供列表，确认后再下载。'},
        @{source=('帮我检查 '+$inline+'帮我找5篇，要DOI'+$inline+'，不要加热，0.5 mM，4 °C。');expected=('请检查 '+$inline+'帮我找5篇，要DOI'+$inline+'，不要加热，0.5 mM，4 °C。')}
    )
    $report=@{ok=$true;cases=@();localShortcut=$config.localHotkey;restoreShortcut=$config.restoreHotkey}
    foreach($case in $cases) {
        [void][LocalRewriteSmokeNative]::Write($editor,0xC,[IntPtr]::Zero,$case.source)
        $watch=[Diagnostics.Stopwatch]::StartNew(); Combination $config.localHotkey
        $deadline=[DateTime]::UtcNow.AddSeconds(4)
        do{Start-Sleep -Milliseconds 20; $value=[LocalRewriteSmokeNative]::Text($editor)}while($value -cne $case.expected -and [DateTime]::UtcNow -lt $deadline)
        if($value -cne $case.expected){throw 'Local rewrite did not fill the expected result'}
        $elapsed=$watch.Elapsed.TotalMilliseconds
        Combination $config.restoreHotkey
        $deadline=[DateTime]::UtcNow.AddSeconds(3)
        do{Start-Sleep -Milliseconds 20; $value=[LocalRewriteSmokeNative]::Text($editor)}while($value -cne $case.source -and [DateTime]::UtcNow -lt $deadline)
        if($value -cne $case.source){throw 'Restore did not preserve the exact original'}
        $report.cases+=@{source=$case.source;result=$case.expected;fillMs=[Math]::Round($elapsed,1);restoreVerified=$true}
    }
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $toolDir 'local-hotkey-smoke.json') -Encoding UTF8
    $report | ConvertTo-Json -Depth 5 -Compress
} finally {
    if(-not $fixture.HasExited) {
        $window=[LocalRewriteSmokeNative]::Window($fixture.Id)
        if($window -ne [IntPtr]::Zero){[void][LocalRewriteSmokeNative]::PostMessage($window,0x10,[IntPtr]::Zero,[IntPtr]::Zero);[void]$fixture.WaitForExit(3000)}
    }
}
