using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using PromptQuick;

public static class MainUiReviewSmoke {
    static int failed;
    static string reportPath;
    public delegate bool Callback(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumWindows(Callback callback, IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, Callback callback, IntPtr p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder text, int length);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int Style(IntPtr h, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] static extern IntPtr Read(IntPtr h, uint m, IntPtr n, StringBuilder text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] static extern IntPtr Write(IntPtr h, uint m, IntPtr n, string text);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int command);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr h);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    static T Field<T>(Control root, string name) where T : Control { return (T)root.Controls.Find(name, true).Single(); }
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static object Call(object target, string method, params object[] args) { return target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args); }
    static string Text(IntPtr handle) { var text = new StringBuilder(21000); Read(handle, 13, (IntPtr)text.Capacity, text); return text.ToString(); }
    static IntPtr Window(int pid) {
        IntPtr result = IntPtr.Zero;
        EnumWindows((h, p) => { uint actual; GetWindowThreadProcessId(h, out actual); if (actual == pid && Text(h) == "Prompt Optimizer External Test") { result = h; return false; } return true; }, IntPtr.Zero);
        return result;
    }
    static IntPtr Plain(IntPtr window) {
        IntPtr result = IntPtr.Zero;
        EnumChildWindows(window, (h, p) => {
            var name = new StringBuilder(256); GetClassName(h, name, name.Capacity);
            if (name.ToString().StartsWith("WindowsForms10.EDIT") && (Style(h, -16) & 0x820) == 0) { result = h; return false; }
            return true;
        }, IntPtr.Zero);
        return result;
    }
    static async Task Until(Func<bool> condition, string message, int milliseconds = 10000) {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < milliseconds) await Task.Delay(30);
        Check(condition(), message);
    }
    static async Task Focus(IntPtr window, IntPtr editor) {
        uint pid;
        uint target = GetWindowThreadProcessId(editor, out pid), current = GetCurrentThreadId();
        uint foreground = GetWindowThreadProcessId(GetForegroundWindow(), out pid);
        if (foreground != current && foreground != target) AttachThreadInput(current, foreground, true);
        AttachThreadInput(current, target, true);
        try { ShowWindow(window, 9); SetForegroundWindow(window); SetFocus(editor); }
        finally { AttachThreadInput(current, target, false); if (foreground != current && foreground != target) AttachThreadInput(current, foreground, false); }
        await Task.Delay(120);
        Check(GetForegroundWindow() == window, "Independent editor did not obtain foreground");
    }
    static async Task Optimize(OptimizerForm form, IntPtr window, IntPtr editor, string source, bool stop, bool escape) {
        Write(editor, 0xC, IntPtr.Zero, source); await Focus(window, editor);
        var task = (Task)Call(form, "Optimize", true, true);
        var result = Field<RichTextBox>(form, "resultText");
        var button = Field<Button>(form, "stopRequest");
        await Until(() => result.TextLength > 0 || task.IsCompleted, "No streamed preview reached the main panel");
        Check(!task.IsCompleted && button.Enabled, "Preview was only displayed after completion");
        Check(Text(editor) == source, "Partial response was written into the external editor");
        if (stop) {
            if (escape) {
                var message = Message.Create(form.Handle, 0, IntPtr.Zero, IntPtr.Zero);
                var arguments = new object[] { message, Keys.Escape };
                Check((bool)Call(form, "ProcessCmdKey", arguments), "Escape did not stop the active request");
            } else button.PerformClick();
        }
        await task;
        Check(Field<Button>(form, "aiOptimize").Enabled, "Controls were not restored after request completion");
        if (stop) {
            Check(Text(editor) == source, "Stopped response changed the external editor");
            Check(Field<Label>(form, "reviewHint").Text.Contains("已停止"), "Stopped state was not visible");
            Check(!Field<Button>(form, "applyReviewed").Enabled, "Incomplete response remained eligible for fill");
        }
    }
    static bool HasHighlight(RichTextBox box) {
        for (int i = 0; i < Math.Min(box.TextLength, 200); i++) { box.Select(i, 1); if (box.SelectionBackColor.ToArgb() != Color.White.ToArgb()) { box.Select(0, 0); return true; } }
        box.Select(0, 0); return false;
    }
    static void Configure(string dir, string endpoint, Settings settings) {
        var store = new ProviderStore(dir, settings); var document = store.Copy();
        var api = document.providers.First(p => p.id == "compatible");
        api.name = "本机 UI 测试"; api.baseUrl = endpoint + "/v1"; api.model = "ui-test"; api.reasoningEffort = "auto"; api.SetApiKey("synthetic-ui-key");
        document.selectedId = api.id; store.Save(document);
    }
    static void ProviderCancellation(OptimizerForm owner, string dir, string endpoint, Settings settings) {
        string testDir = Path.Combine(dir, "provider-cancel"); Directory.CreateDirectory(testDir);
        foreach (string name in new[] { "ai-worker.cjs", "api-provider.cjs", "提示词优化专用指令.txt" }) File.Copy(Path.Combine(dir, name), Path.Combine(testDir, name), true);
        var store = new ProviderStore(testDir, settings); var data = store.Copy(); var api = data.providers.First(p => p.id == "compatible");
        api.baseUrl = endpoint + "/slow/v1"; api.model = "ui-test"; api.SetApiKey("synthetic-ui-key"); data.selectedId = api.id; store.Save(data);
        string before = File.ReadAllText(Path.Combine(testDir, "providers.json"));
        Exception error = null;
        using (var client = new AiClient(testDir, settings, store)) using (var dialog = new ProviderDialog(store, client, () => { throw new Exception("Canceled provider draft was applied"); })) {
            dialog.Shown += async (s, e) => {
                try {
                    var cancel = Field<Button>(dialog, "cancelProvider"); var save = Field<Button>(dialog, "saveProvider");
                    await Until(() => cancel.Text == "停止", "Provider model loading did not enter cancellable state");
                    Check(cancel.Enabled, "Provider stop button is disabled");
                    cancel.PerformClick(); await Until(() => save.Enabled, "Provider model loading cancellation did not restore controls");
                    Check(dialog.Visible, "Stopping model loading unexpectedly closed the modal dialog");
                    Check(Object.ReferenceEquals(dialog.CancelButton, cancel), "Escape is not bound to provider cancellation");
                    Field<TextBox>(dialog, "providerName").Text = "不应保存的名称";
                    Field<Button>(dialog, "testProvider").PerformClick();
                    await Until(() => cancel.Text == "停止", "Provider connection test did not become cancellable");
                    cancel.PerformClick(); await Until(() => save.Enabled, "Provider connection-test cancellation did not restore controls");
                    Check(dialog.Visible, "Stopping provider connection unexpectedly closed dialog");
                    Field<Button>(dialog, "testProvider").PerformClick();
                    await Until(() => cancel.Text == "停止", "Provider closing case did not start request");
                    dialog.Close();
                } catch (Exception ex) { error = ex; dialog.Close(); }
            };
            dialog.ShowDialog(owner);
        }
        if (error != null) throw error;
        Check(File.ReadAllText(Path.Combine(testDir, "providers.json")) == before, "Provider cancel/close wrote unsaved settings");
        Check(store.Current.name != "不应保存的名称", "Provider cancel/close applied unsaved draft");
    }
    [STAThread] public static int Main(string[] args) {
        string dir = args[0], endpoint = args[1], editorExe = args[3]; reportPath = args[2];
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        var settings = new Settings { localHotkey = "Ctrl+Alt+Shift+F17", aiHotkey = "Ctrl+Alt+Shift+F18", restoreHotkey = "Ctrl+Alt+Shift+F19", doubleSpaceTranslation = false, aiTimeoutSeconds = 15 };
        Process editorProcess = null;
        try {
            Configure(dir, endpoint, settings);
            editorProcess = Process.Start(new ProcessStartInfo(editorExe) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            using (var form = new OptimizerForm(dir, settings)) {
                form.Shown += async (sender, eventArgs) => {
                    try {
                        form.Open(); await Task.Delay(150);
                        IntPtr window = IntPtr.Zero;
                        await Until(() => { window = Window(editorProcess.Id); return window != IntPtr.Zero; }, "Independent editor fixture did not open");
                        IntPtr editor = Plain(window); Check(editor != IntPtr.Zero, "Plain editor fixture missing");
                        await Optimize(form, window, editor, "请保留0.5 mM，不要加热。慢请求。", true, false);
                        await Optimize(form, window, editor, "请保留0.5 mM，不要加热。新请求。", false, false);
                        Check(Text(editor) == "请保留0.5 mM，不要加热。", "Immediate retry did not fill its complete result");
                        await Task.Delay(1500);
                        Check(!Field<RichTextBox>(form, "resultText").Text.Contains("旧片段"), "Old response leaked into the replacement request");
                        await Focus(window, editor); Call(form, "Restore");
                        Check(Text(editor) == "请保留0.5 mM，不要加热。新请求。", "Normal result restore failed");
                        await Optimize(form, window, editor, "请保留0.5 mM，不要加热。慢请求 Esc。", true, true);

                        const string risk = "浓度0.5 mM，温度4 °C，不要加热。风险样例。";
                        await Optimize(form, window, editor, risk, false, false);
                        var result = Field<RichTextBox>(form, "resultText"); var source = Field<RichTextBox>(form, "sourceText"); var apply = Field<Button>(form, "applyReviewed");
                        Check(Text(editor) == risk, "Risky changed numbers/negation automatically overwrote the external editor");
                        Check(Field<Label>(form, "reviewHint").Text.Contains("需要核对") && apply.Enabled, "Risk preview warning/explicit apply missing");
                        Check(HasHighlight(source) && HasHighlight(result), "Original and result differences were not highlighted");
                        string edited = "用户已改动原文。"; Write(editor, 0xC, IntPtr.Zero, edited);
                        await (Task)Call(form, "ApplyReviewed");
                        Check(Text(editor) == edited, "Explicit apply overwrote a changed target");
                        Write(editor, 0xC, IntPtr.Zero, risk);
                        await (Task)Call(form, "ApplyReviewed");
                        Check(Text(editor) == "浓度4 mM，温度0.5 °C，加热。", "Confirmed review result was not filled");
                        await Focus(window, editor); Call(form, "Restore");
                        Check(Text(editor) == risk, "Confirmed result could not restore original");

                        form.Open(); await Focus(form.Handle, form.Handle);
                        using (var screenshot = new Bitmap(form.Width, form.Height)) {
                            using (var graphics = Graphics.FromImage(screenshot)) graphics.CopyFromScreen(form.Location, Point.Empty, screenshot.Size);
                            screenshot.Save(Path.Combine(dir, "main-review.png"));
                        }
                        ProviderCancellation(form, dir, endpoint, settings);
                        File.WriteAllText(reportPath, Json.Encode(new { ok = true, streamingPreview = true, stopAndEscape = true, cancelThenRetry = true, staleDeltaIsolated = true, riskyResultNotAutoFilled = true, changedTargetNotOverwritten = true, explicitApply = true, restore = true, differenceHighlight = true, providerCancellation = true }));
                    } catch (Exception error) {
                        failed = 1; File.WriteAllText(reportPath, Json.Encode(new { ok = false, error = error.ToString(), hint = Field<Label>(form, "reviewHint").Text, result = Field<RichTextBox>(form, "resultText").Text }));
                    } finally { PostMessage(form.Handle, 0x8043, IntPtr.Zero, IntPtr.Zero); }
                };
                Application.Run(form);
            }
        } catch (Exception error) { failed = 1; File.WriteAllText(reportPath, Json.Encode(new { ok = false, error = error.ToString() })); }
        finally {
            if (editorProcess != null && !editorProcess.HasExited) { IntPtr window = Window(editorProcess.Id); if (window != IntPtr.Zero) { PostMessage(window, 0x10, IntPtr.Zero, IntPtr.Zero); editorProcess.WaitForExit(3000); } }
        }
        return failed;
    }
}
