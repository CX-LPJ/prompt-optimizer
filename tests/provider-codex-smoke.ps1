param([Parameter(Mandatory=$true)][string]$ToolDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Text; using System.Runtime.InteropServices;
public static class ProviderSmokeNative {
 public delegate bool Callback(IntPtr h, IntPtr p);
 [DllImport("user32.dll")] public static extern bool EnumWindows(Callback c, IntPtr p);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="SendMessageW")] public static extern IntPtr ReadText(IntPtr h,uint m,IntPtr n,StringBuilder t);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
 public static IntPtr Main(uint pid) { IntPtr found=IntPtr.Zero; EnumWindows((h,p)=>{uint actual;GetWindowThreadProcessId(h,out actual);var text=new StringBuilder(256);ReadText(h,13,(IntPtr)256,text);if(actual==pid&&text.ToString()=="提示词快捷优化"){found=h;return false;}return true;},IntPtr.Zero);return found; }
}
'@
$toolExe = Join-Path $ToolDirectory 'PromptOptimizer.exe'
$toolProcess = @(Get-Process -Name PromptOptimizer | Where-Object { $_.Path -eq $toolExe })
if ($toolProcess.Count -ne 1) { throw 'Expected exactly one running tool instance.' }
$handle = [ProviderSmokeNative]::Main($toolProcess[0].Id)
if ($handle -eq [IntPtr]::Zero) { throw 'Tool window not found.' }
[void][ProviderSmokeNative]::PostMessage($handle,0x8046,[IntPtr]::Zero,[IntPtr]::Zero)
$deadline = [DateTime]::UtcNow.AddSeconds(20)
$mainRoot = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
$condition = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,'供应商与模型')),
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Window)))
do { Start-Sleep -Milliseconds 100; $dialog = $mainRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition) } while (-not $dialog -and [DateTime]::UtcNow -lt $deadline)
if (-not $dialog) { throw 'Provider settings did not open.' }
function Named([string]$Name) { $dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants,(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$Name))) }
function ReadNative($Element) { $buffer = New-Object Text.StringBuilder 4096; [void][ProviderSmokeNative]::ReadText([IntPtr]$Element.Current.NativeWindowHandle,13,[IntPtr]4096,$buffer); $buffer.ToString() }
do {
    Start-Sleep -Milliseconds 100
    $labels = $dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants,(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Text)))
    $status = $null
    foreach ($label in $labels) { if ($label.Current.Name -match '^已加载') { $status = $label; break } }
} while (-not $status -and [DateTime]::UtcNow -lt $deadline)
if (-not $status) { throw 'Codex model list did not load.' }
$combos = $dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants,(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ComboBox)))
$model = ''; $effort = ''
foreach ($combo in $combos) { $value = ReadNative $combo; if ($value -eq 'gpt-6.1-sol') { $model = $value }; if ($value -eq 'medium') { $effort = $value } }
if ($model -cne 'gpt-6.1-sol' -or $effort -cne 'medium') { throw 'Configured Codex model or effort changed.' }
$test = Named '测试连接'
if (-not [ProviderSmokeNative]::PostMessage([IntPtr]$test.Current.NativeWindowHandle,0xF5,[IntPtr]::Zero,[IntPtr]::Zero)) { throw 'Test button invocation failed.' }
$deadline = [DateTime]::UtcNow.AddSeconds(45)
do { Start-Sleep -Milliseconds 150; $text = ReadNative $status } while ($text -notmatch '^连接成功' -and [DateTime]::UtcNow -lt $deadline -and ($text -match '正在|^已加载'))
if ($text -notmatch '^连接成功') { throw ('Codex connection test failed: ' + $text) }
[void][ProviderSmokeNative]::PostMessage([IntPtr]$dialog.Current.NativeWindowHandle,0x10,[IntPtr]::Zero,[IntPtr]::Zero)
$result = @{ok=$true;model=$model;reasoningEffort=$effort;modelListLoaded=$true;realConnectionTest=$true;processId=$toolProcess[0].Id}
$result | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $ToolDirectory 'provider-codex-smoke.json') -Encoding UTF8
$result | ConvertTo-Json -Compress
