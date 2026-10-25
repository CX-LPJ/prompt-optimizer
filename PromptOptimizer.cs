using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace PromptQuick {
    public static class Json {
        public static string Encode(object value) { return new JavaScriptSerializer().Serialize(value); }
        public static Dictionary<string, object> Decode(string text) { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text); }
        public static string Str(Dictionary<string, object> d, string key, string fallback = "") { object v; return d.TryGetValue(key, out v) && v != null ? v.ToString() : fallback; }
    }
    public sealed class Settings {
        public string model = "gpt-6.1-sol";
        public string reasoningEffort = "medium";
        public int aiTimeoutSeconds = 35;
        public string codexPath = "", nodePath = "";
        public string localHotkey = "Ctrl+Alt+P", aiHotkey = "Ctrl+Alt+Shift+P", restoreHotkey = "Ctrl+Alt+O";
        public bool doubleSpaceTranslation = true;
        public string translationTarget = "auto";
        public int doubleSpaceMilliseconds = 350;
        public static Settings Load(string dir) {
            string file = Path.Combine(dir, "config.json");
            var settings = File.Exists(file) ? new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(file, Encoding.UTF8)) : new Settings();
            if (settings == null || settings.aiTimeoutSeconds < 5 || settings.aiTimeoutSeconds > 120) throw new Exception("config.json 中 aiTimeoutSeconds 必须在 5–120 之间。");
            if (!new[] { "auto", "none", "minimal", "low", "medium", "high", "xhigh", "max" }.Contains(settings.reasoningEffort)) throw new Exception("config.json 中 reasoningEffort 必须是 auto、none、minimal、low、medium、high、xhigh 或 max。");
            if (settings.doubleSpaceMilliseconds < 150 || settings.doubleSpaceMilliseconds > 1000 || !new[] { "auto", "english", "chinese" }.Contains(settings.translationTarget)) throw new Exception("翻译方向须为 auto、english 或 chinese，双击间隔须在 150–1000 ms 之间。");
            return settings;
        }
    }
    public static class Native {
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint key);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    }
    public sealed class Draft {
        public IntPtr Window;
        public AutomationElement Editor;
        public string Runtime, Document, Text;
        public bool PreviewOnly;
        public static string RuntimeOf(AutomationElement e) { return String.Join(".", e.GetRuntimeId()); }
        public static bool IsCodex(IntPtr window) {
            try {
                uint pid; Native.GetWindowThreadProcessId(window, out pid);
                string file = Process.GetProcessById((int)pid).MainModule.FileName.Replace('/', '\\');
                return Regex.IsMatch(file, @"\\WindowsApps\\OpenAI\.Codex_[^\\]+\\app\\ChatGPT\.exe$", RegexOptions.IgnoreCase)
                    || Regex.IsMatch(file, @"\\OpenAI\\Codex\\[^\r\n]*\\(Codex|ChatGPT)\.exe$", RegexOptions.IgnoreCase);
            } catch { return false; }
        }
        static AutomationElement FocusedEditor(AutomationElement root) {
            var cursor = AutomationElement.FocusedElement; AutomationElement candidate = null;
            for (int depth = 0; cursor != null && depth < 60; depth++, cursor = TreeWalker.RawViewWalker.GetParent(cursor)) {
                if (cursor.Current.IsPassword) throw new Exception("密码输入框不处理。");
                if (candidate == null && ExternalEditors.Supported(cursor)) candidate = cursor;
                if (Automation.Compare(cursor, root)) return candidate;
            }
            return null;
        }
        static string Doc(AutomationElement root) {
            var doc = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
            return doc == null ? "" : doc.Current.Name;
        }
        public static string Read(AutomationElement editor) {
            ExternalEditors.EnsureSafe(editor);
            if (ExternalEditors.PlainClass(editor.Current.ClassName) || ExternalEditors.RichClass(editor.Current.ClassName)) return ExternalEditors.ReadPlain(editor);
            object pattern;
            if (!editor.TryGetCurrentPattern(ValuePattern.Pattern, out pattern)) throw new Exception("当前输入框无法直接读取。");
            var value = ((ValuePattern)pattern).Current.Value ?? "";
            // Chromium exposes the placeholder as text in an empty ProseMirror editor.
            if (value.Trim() == editor.Current.Name && editor.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ClassNameProperty, "ProseMirror-trailingBreak")).Count > 0 &&
                editor.FindAll(TreeScope.Descendants, Condition.TrueCondition).Count <= 2) return "";
            return value;
        }
        public static Draft Capture() {
            var window = Native.GetForegroundWindow();
            uint pid; Native.GetWindowThreadProcessId(window, out pid);
            if (pid == Process.GetCurrentProcess().Id) throw new Exception("请使用面板按钮处理此处文字。");
            var root = AutomationElement.FromHandle(window);
            var editor = FocusedEditor(root);
            if (editor == null) throw new Exception("请将光标放在可编辑的文本输入框内。");
            bool preview = ExternalEditors.RequiresPreview(editor);
            // ValuePattern replaces plain text. Preserve rich mention/attachment nodes by declining replacement.
            foreach (AutomationElement child in editor.FindAll(TreeScope.Descendants, Condition.TrueCondition)) {
                string cls = child.Current.ClassName ?? "";
                if (Regex.IsMatch(cls, "mention|node-view|attachment|chip", RegexOptions.IgnoreCase)
                    || child.Current.ControlType == ControlType.Button || child.Current.ControlType == ControlType.Image)
                    preview = true;
            }
            return new Draft { Window = window, Editor = editor, Runtime = RuntimeOf(editor), Document = Doc(root), Text = Read(editor), PreviewOnly = preview };
        }
        public bool Matches(string expected) {
            try {
                var now = Capture();
                return now.Window == Window && now.Runtime == Runtime && now.Document == Document && now.Text == expected;
            } catch { return false; }
        }
        public string Diagnose(string expected) {
            try { var now = Capture(); return Json.Encode(new { window = now.Window == Window, runtime = now.Runtime == Runtime, document = now.Document == Document, text = now.Text == expected, nowRuntime = now.Runtime, beforeRuntime = Runtime, nowLength = now.Text.Length, expectedLength = expected.Length }); }
            catch (Exception e) { return e.Message; }
        }
        public bool Replace(string expected, string replacement) {
            if (PreviewOnly || !Matches(expected)) return false;
            if (ExternalEditors.PlainClass(Editor.Current.ClassName)) { if (!ExternalEditors.WritePlain(Editor, replacement)) return false; }
            else {
                var pattern = (ValuePattern)Editor.GetCurrentPattern(ValuePattern.Pattern);
                if (pattern.Current.IsReadOnly) return false;
                pattern.SetValue(replacement);
            }
            for (int i = 0; i < 10; i++) {
                if (SameText(Read(Editor), replacement)) return true;
                Thread.Sleep(10);
            }
            throw new Exception("未能核对填回结果，请检查输入框；原文仍在优化面板中。");
        }
        public static bool SameText(string a, string b) { return a.Replace("\r\n", "\n").TrimEnd('\n') == b.Replace("\r\n", "\n").TrimEnd('\n'); }
    }
    public sealed class AiClient : IDisposable {
        readonly string dir; readonly Settings settings; Process worker;
        readonly ProviderStore providers;
        sealed class PendingRequest {
            public TaskCompletionSource<Dictionary<string, object>> Completion = new TaskCompletionSource<Dictionary<string, object>>(TaskCreationOptions.RunContinuationsAsynchronously);
            public IProgress<string> Progress;
        }
        readonly Dictionary<int, PendingRequest> pending = new Dictionary<int, PendingRequest>();
        int counter; readonly object gate = new object();
        public AiClient(string dir, Settings settings, ProviderStore providers = null) { this.dir = dir; this.settings = settings; this.providers = providers ?? new ProviderStore(dir, settings); }
        public static string Locate(string configured, string executable, string pattern) {
            if (!String.IsNullOrWhiteSpace(configured)) { if (!File.Exists(configured)) throw new Exception("找不到 " + configured); return configured; }
            foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')) {
                try { string file = Path.Combine(folder.Trim('"'), executable); if (File.Exists(file) && !file.Contains("Microsoft\\WindowsApps")) return file; } catch { }
            }
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), pattern);
            if (Directory.Exists(root)) {
                var files = Directory.GetFiles(root, executable, SearchOption.AllDirectories);
                if (files.Length > 0) return files.OrderByDescending(File.GetLastWriteTimeUtc).First();
            }
            throw new Exception("未找到 " + executable + "，请在 config.json 中设置路径。");
        }
        void EnsureWorker() {
            if (worker != null && !worker.HasExited) return;
            string node = Locate(settings.nodePath, "node.exe", @".cache\codex-runtimes\codex-primary-runtime\dependencies\node");
            var start = new ProcessStartInfo(node, "\"" + Path.Combine(dir, "ai-worker.cjs") + "\"") {
                WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
            };
            worker = new Process { StartInfo = start, EnableRaisingEvents = true };
            worker.OutputDataReceived += (s, e) => { if (e.Data != null) Handle(e.Data); };
            worker.ErrorDataReceived += (s, e) => { };
            worker.Exited += (s, e) => { if (Object.ReferenceEquals(worker, s)) FailAll("AI 连接已结束，请重试。"); };
            worker.Start(); worker.StandardInput.AutoFlush = true;
            worker.BeginOutputReadLine(); worker.BeginErrorReadLine();
        }
        void Handle(string line) {
            try {
                var value = Json.Decode(line); int id = Convert.ToInt32(value["id"]);
                PendingRequest request;
                lock (gate) {
                    if (!pending.TryGetValue(id, out request)) return;
                    if (Json.Str(value, "event") != "delta") pending.Remove(id);
                }
                if (Json.Str(value, "event") == "delta") { if (request.Progress != null) request.Progress.Report(Json.Str(value, "text")); return; }
                if (Convert.ToBoolean(value["ok"])) request.Completion.TrySetResult(value);
                else if (Json.Str(value, "errorCode") == "cancelled") request.Completion.TrySetCanceled();
                else request.Completion.TrySetException(new Exception(Json.Str(value, "error")));
            } catch { }
        }
        void FailAll(string message) { lock (gate) { foreach (var request in pending.Values) request.Completion.TrySetException(new Exception(message)); pending.Clear(); } }
        void SendCancel(int targetId) {
            try {
                byte[] bytes = Encoding.UTF8.GetBytes(Json.Encode(new { id = Interlocked.Increment(ref counter), action = "cancel", targetId = targetId }) + "\n");
                lock (gate) { if (worker != null && !worker.HasExited) { worker.StandardInput.BaseStream.Write(bytes, 0, bytes.Length); worker.StandardInput.BaseStream.Flush(); } }
            } catch { }
        }
        public Task<Dictionary<string, object>> Request(string action, string text = "", string targetLanguage = "auto", CancellationToken cancellationToken = default(CancellationToken), IProgress<string> progress = null) { return RequestForProvider(action, providers.Current, text, targetLanguage, cancellationToken, progress); }
        public async Task<Dictionary<string, object>> RequestForProvider(string action, ProviderProfile profile, string text = "", string targetLanguage = "auto", CancellationToken cancellationToken = default(CancellationToken), IProgress<string> progress = null) {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureWorker();
            int id = Interlocked.Increment(ref counter);
            var request = new PendingRequest { Progress = progress };
            lock (gate) pending.Add(id, request);
            try {
                var q = new { id = id, action = action, text = text, targetLanguage = targetLanguage, provider = profile.Request(), timeoutMs = action == "models" ? Math.Min(15000, settings.aiTimeoutSeconds * 1000) : settings.aiTimeoutSeconds * 1000,
                    cwd = Path.Combine(dir, "ai-work"), codexPath = profile.kind == "codex" ? Locate(settings.codexPath, "codex.exe", @"AppData\Local\OpenAI\Codex\bin") : "" };
                Directory.CreateDirectory(q.cwd);
                byte[] bytes = new UTF8Encoding(false).GetBytes(Json.Encode(q) + "\n");
                try { lock (gate) { worker.StandardInput.BaseStream.Write(bytes, 0, bytes.Length); worker.StandardInput.BaseStream.Flush(); } }
                finally { Array.Clear(bytes, 0, bytes.Length); }
                using (cancellationToken.Register(() => {
                    bool removed; lock (gate) removed = pending.Remove(id);
                    if (removed) { request.Completion.TrySetCanceled(); SendCancel(id); }
                })) {
                    if (await Task.WhenAny(request.Completion.Task, Task.Delay((settings.aiTimeoutSeconds + 45) * 1000)) != request.Completion.Task) { SendCancel(id); Dispose(); throw new Exception("AI 连接超时，原文已保留。"); }
                    return await request.Completion.Task;
                }
            } finally { lock (gate) pending.Remove(id); }
        }
        public void Dispose() {
            if (worker != null) {
                try { worker.StandardInput.Close(); if (!worker.WaitForExit(1500)) worker.Kill(); } catch { }
                worker.Dispose(); worker = null;
            }
            FailAll("AI 连接已关闭。");
        }
    }
    public sealed class OptimizerForm : Form {
        readonly Settings settings; readonly AiClient ai; readonly NotifyIcon tray; readonly RichTextBox source, result;
        readonly Label status; readonly Button localButton, aiButton, translateButton; readonly string dir;
        readonly Button stopButton, applyButton, copyButton;
        readonly Label reviewHint;
        readonly ToolStripMenuItem stopMenu;
        CancellationTokenSource activeCancellation;
        int operationVersion, streamingCount;
        readonly StringBuilder preview = new StringBuilder();
        Draft pendingDraft; string pendingExpected, pendingSource, pendingOriginal;
        readonly Label shortcutHint;
        readonly ProviderStore providers;
        ProviderDialog providerDialog;
        HotkeyRegistry hotkeys; ShortcutDialog shortcutDialog; string startupHotkeyError;
        Draft lastDraft; string original, applied; bool busy, exiting, paused;
        readonly System.Windows.Forms.Timer uiTimer = new System.Windows.Forms.Timer { Interval = 250 };
        Stopwatch activeWatch;
        string activeOperation = "AI 重写中";
        DoubleSpaceHook spaceHook;
        readonly System.Windows.Forms.Timer gestureTimer = new System.Windows.Forms.Timer { Interval = 100 };
        int gestureVersion; IntPtr gestureWindow;
        public OptimizerForm(string dir, Settings settings) {
            this.dir = dir; this.settings = settings; providers = new ProviderStore(dir, settings); ai = new AiClient(dir, settings, providers);
            Text = "提示词快捷优化"; Font = new Font("Microsoft YaHei UI", 10); Size = new Size(1000, 810); MinimumSize = new Size(850, 700); KeyPreview = true;
            Icon = new Icon(Path.Combine(dir, "prompt.ico"), 32, 32);
            StartPosition = FormStartPosition.CenterScreen; ShowInTaskbar = true; BackColor = Color.FromArgb(248, 250, 252);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 8 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 56));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
            layout.Controls.Add(new Label { Text = "把想法变成清楚、可执行的提示词", Font = new Font(Font.FontFamily, 16, FontStyle.Bold), AutoSize = true }, 0, 0);
            shortcutHint = new Label { AutoSize = true, ForeColor = Color.FromArgb(70, 80, 95) }; UpdateShortcutHint(); layout.Controls.Add(shortcutHint, 0, 1);
            layout.Controls.Add(new Label { Text = "原文（也可在这里粘贴）", AutoSize = true }, 0, 2);
            source = new RichTextBox { Name = "sourceText", Multiline = true, ScrollBars = RichTextBoxScrollBars.Vertical, Dock = DockStyle.Fill, DetectUrls = false, MaxLength = 20000, BackColor = Color.White };
            result = new RichTextBox { Name = "resultText", Multiline = true, ScrollBars = RichTextBoxScrollBars.Vertical, Dock = DockStyle.Fill, DetectUrls = false, MaxLength = 60000, ReadOnly = false, BackColor = Color.White };
            layout.Controls.Add(source, 0, 3); layout.Controls.Add(new Label { Text = "优化结果（生成时预览，完成后可编辑）", AutoSize = true }, 0, 4); layout.Controls.Add(result, 0, 5);
            reviewHint = new Label { Name = "reviewHint", Text = "原文中的删除内容标红，结果中的新增内容标绿。", Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0), ForeColor = Color.FromArgb(70, 80, 95) };
            layout.Controls.Add(reviewHint, 0, 6);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0), WrapContents = true };
            localButton = new Button { Name = "localOptimize", Text = "本地整理", AutoSize = true }; aiButton = new Button { Name = "aiOptimize", Text = "AI 重写", AutoSize = true };
            translateButton = new Button { Name = "translateDraft", Text = "中英互译", AutoSize = true }; translateButton.Click += async (s, e) => await Translate(false);
            copyButton = new Button { Name = "copyResult", Text = "复制结果", AutoSize = true }; var hide = new Button { Text = "收起到托盘", AutoSize = true };
            stopButton = new Button { Name = "stopRequest", Text = "停止（Esc）", AutoSize = true, Enabled = false }; stopButton.Click += (s, e) => StopActive();
            applyButton = new Button { Name = "applyReviewed", Text = "确认填回", AutoSize = true, Enabled = false }; applyButton.Click += async (s, e) => await ApplyReviewed();
            var configure = new Button { Text = "快捷键设置", AutoSize = true, Name = "configureShortcuts" }; configure.Click += (s, e) => OpenShortcutSettings();
            var configureProvider = new Button { Text = "供应商与模型", AutoSize = true, Name = "configureProvider" }; configureProvider.Click += (s, e) => OpenProviderSettings();
            status = new Label { Text = "就绪", AutoSize = true, Padding = new Padding(8, 5, 0, 0) };
            actions.Controls.AddRange(new Control[] { localButton, aiButton, translateButton, stopButton, applyButton, copyButton, configure, configureProvider, hide, status }); layout.Controls.Add(actions, 0, 7); Controls.Add(layout);
            localButton.Click += async (s, e) => await Optimize(false, false); aiButton.Click += async (s, e) => await Optimize(true, false);
            copyButton.Click += (s, e) => { try { if (result.Text.Length > 0) { Clipboard.SetText(result.Text); status.Text = "已复制"; } } catch { status.Text = "剪贴板忙，请重试"; } };
            hide.Click += (s, e) => Hide();
            var menu = new ContextMenuStrip(); menu.Items.Add("打开优化面板", null, (s, e) => Open());
            menu.Items.Add("快捷键设置…", null, (s, e) => OpenShortcutSettings());
            menu.Items.Add("供应商与模型…", null, (s, e) => OpenProviderSettings());
            menu.Items.Add("预热 AI 连接", null, async (s, e) => await Warm());
            stopMenu = new ToolStripMenuItem("停止当前请求") { Enabled = false }; stopMenu.Click += (s, e) => StopActive(); menu.Items.Add(stopMenu);
            var pauseItem = new ToolStripMenuItem("暂停快捷键"); pauseItem.Click += (s, e) => {
                if (paused) { try { RegisterKeys(); paused = false; UpdateSpaceHook(); pauseItem.Text = "暂停快捷键"; } catch (Exception ex) { Notice(ex.Message); } }
                else { UnregisterKeys(); paused = true; UpdateSpaceHook(); pauseItem.Text = "恢复快捷键"; }
            }; menu.Items.Add(pauseItem);
            var doubleSpaceItem = new ToolStripMenuItem("双击空格翻译") { Checked = settings.doubleSpaceTranslation };
            doubleSpaceItem.Click += (s, e) => { try { SetTranslationPreferences(!settings.doubleSpaceTranslation, settings.translationTarget); doubleSpaceItem.Checked = settings.doubleSpaceTranslation; } catch (Exception ex) { Notice(ex.Message); } }; menu.Items.Add(doubleSpaceItem);
            var direction = new ToolStripMenuItem("翻译方向");
            string[] targets = { "auto", "english", "chinese" }, targetNames = { "自动中英互译", "始终翻译成英文", "始终翻译成中文" };
            for (int i = 0; i < targets.Length; i++) {
                string target = targets[i]; var item = new ToolStripMenuItem(targetNames[i]) { Checked = settings.translationTarget == target };
                item.Click += (s, e) => { try { SetTranslationPreferences(settings.doubleSpaceTranslation, target); foreach (ToolStripMenuItem option in direction.DropDownItems) option.Checked = option == item; } catch (Exception ex) { Notice(ex.Message); } }; direction.DropDownItems.Add(item);
            }
            menu.Items.Add(direction);
            menu.Items.Add("打开配置文件", null, (s, e) => Process.Start("notepad.exe", "\"" + Path.Combine(dir, "config.json") + "\""));
            menu.Items.Add("编辑优化提示词", null, (s, e) => Process.Start("notepad.exe", "\"" + Path.Combine(dir, "提示词优化专用指令.txt") + "\""));
            menu.Items.Add("退出", null, (s, e) => { exiting = true; Close(); });
            tray = new NotifyIcon { Icon = Icon, Text = "提示词快捷优化 · 就绪", Visible = true, ContextMenuStrip = menu }; tray.DoubleClick += (s, e) => Open();
            uiTimer.Tick += (s, e) => { if (busy && activeWatch != null) status.Text = activeOperation + " " + activeWatch.Elapsed.TotalSeconds.ToString("0.0") + "s" + (streamingCount > 0 ? " · " + streamingCount + "字" : ""); };
            gestureTimer.Tick += async (s, e) => { gestureTimer.Stop(); if (spaceHook != null && spaceHook.Enabled && spaceHook.Generation == gestureVersion && Native.GetForegroundWindow() == gestureWindow) await Translate(true); };
            FormClosing += (s, e) => { if (!exiting) { e.Cancel = true; Hide(); } };
            FormClosed += (s, e) => { if (activeCancellation != null) activeCancellation.Cancel(); UnregisterKeys(); gestureTimer.Stop(); if (spaceHook != null) spaceHook.Dispose(); uiTimer.Stop(); tray.Dispose(); ai.Dispose(); };
            Shown += (s, e) => { Hide(); try { spaceHook = new DoubleSpaceHook(Handle, settings.doubleSpaceMilliseconds); UpdateSpaceHook(); } catch (Exception ex) { Notice(ex.Message); } if (startupHotkeyError != null) { Open(); OpenShortcutSettings(startupHotkeyError); } else Notice(String.IsNullOrWhiteSpace(providers.LoadWarning) ? "已启动。在支持的文本框内双击空格即可中英互译。右键托盘可设置。" : providers.LoadWarning); };
        }
        public void Open() { Show(); WindowState = FormWindowState.Normal; Activate(); }
        void Notice(string text) { tray.BalloonTipTitle = "提示词快捷优化"; tray.BalloonTipText = text; tray.ShowBalloonTip(2500); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); try { RegisterKeys(); } catch (Exception ex) { startupHotkeyError = ex.Message; } }
        protected override void OnHandleDestroyed(EventArgs e) { UnregisterKeys(); hotkeys = null; base.OnHandleDestroyed(e); }
        void RegisterKeys() {
            if (hotkeys == null) hotkeys = new HotkeyRegistry(Handle);
            hotkeys.Apply(new[] { settings.localHotkey, settings.aiHotkey, settings.restoreHotkey });
        }
        void UnregisterKeys() { if (hotkeys != null) hotkeys.Clear(); }
        void UpdateShortcutHint() { shortcutHint.Text = settings.localHotkey + " 本地整理    |    " + settings.aiHotkey + " AI 重写\n" + settings.restoreHotkey + " 恢复原文    ·    支持普通文本框、记事本及网页输入框\n双击空格翻译：" + (settings.doubleSpaceTranslation ? "开启" : "关闭") + " · " + (settings.translationTarget == "auto" ? "自动中英互译" : settings.translationTarget == "english" ? "译成英文" : "译成中文") + "\n当前：" + providers.Current.name + " · " + providers.Current.model; }
        void UpdateSpaceHook() { if (spaceHook != null) spaceHook.Enabled = settings.doubleSpaceTranslation && !paused && shortcutDialog == null && providerDialog == null && !busy; }
        void OpenProviderSettings() {
            if (busy) { Notice("请等待当前 AI 请求结束后再切换供应商。"); return; }
            if (providerDialog != null) { providerDialog.Activate(); return; }
            UnregisterKeys();
            using (var dialog = new ProviderDialog(providers, ai, () => { UpdateShortcutHint(); status.Text = "供应商与模型已生效"; })) {
                providerDialog = dialog; UpdateSpaceHook();
                try { dialog.ShowDialog(this); } finally { providerDialog = null; UpdateSpaceHook(); if (!paused && !exiting) { try { RegisterKeys(); } catch (Exception ex) { Notice(ex.Message); } } }
            }
        }
        void SetTranslationPreferences(bool enabled, string target) { TranslationText.SavePreferences(dir, settings, enabled, target); settings.doubleSpaceTranslation = enabled; settings.translationTarget = target; UpdateShortcutHint(); UpdateSpaceHook(); }
        void ApplyShortcuts(string[] values) {
            var canonical = hotkeys.Apply(values, keys => ShortcutStore.Save(dir, settings, keys));
            settings.localHotkey = canonical[0]; settings.aiHotkey = canonical[1]; settings.restoreHotkey = canonical[2];
            if (paused) hotkeys.Clear();
            startupHotkeyError = null; UpdateShortcutHint(); status.Text = paused ? "已保存 · 暂停中" : "快捷键已生效";
        }
        void OpenShortcutSettings(string error = null) {
            if (providerDialog != null) return;
            if (shortcutDialog != null) { shortcutDialog.Activate(); return; }
            // Pause registrations while recording so even an existing combination
            // reaches the focused input field through the normal keyboard path.
            UnregisterKeys();
            using (var dialog = new ShortcutDialog(settings, ApplyShortcuts, error)) {
                shortcutDialog = dialog;
                UpdateSpaceHook();
                try { dialog.ShowDialog(this); } finally {
                    shortcutDialog = null;
                    UpdateSpaceHook();
                    if (!paused && !exiting) { try { RegisterKeys(); } catch (Exception ex) { Notice("恢复快捷键失败：" + ex.Message + " 请重新打开快捷键设置。"); } }
                }
            }
        }
        protected override void WndProc(ref Message m) {
            if (m.Msg == 0x8042) { Open(); return; }
            if (m.Msg == 0x8043) { exiting = true; Close(); return; }
            if (m.Msg == 0x8044) { Open(); OpenShortcutSettings(); return; }
            if (m.Msg == 0x8045) { if (!busy && !paused && shortcutDialog == null && settings.doubleSpaceTranslation) { gestureVersion = m.WParam.ToInt32(); gestureWindow = m.LParam; gestureTimer.Stop(); gestureTimer.Start(); } return; }
            if (m.Msg == 0x8046) { Open(); OpenProviderSettings(); return; }
            if (m.Msg == 0x0312) {
                int action; string shortcut;
                if (hotkeys == null || !hotkeys.TryAction(m.WParam.ToInt32(), out action, out shortcut)) return;
                if (shortcutDialog != null) { shortcutDialog.RecordRegisteredKey(shortcut); return; }
                if (action == 3) Restore(); else HandleOptimizeHotkey(action == 2);
            }
            base.WndProc(ref m);
        }
        async void HandleOptimizeHotkey(bool useAi) { if (providerDialog == null) await Optimize(useAi, true); }
        protected override bool ProcessCmdKey(ref Message message, Keys keyData) {
            if (keyData == Keys.Escape && busy) { StopActive(); return true; }
            return base.ProcessCmdKey(ref message, keyData);
        }
        int BeginAiOperation(string name, bool clearPreview = true) {
            busy = true; activeOperation = name; activeCancellation = new CancellationTokenSource(); operationVersion++;
            activeWatch = Stopwatch.StartNew(); streamingCount = 0; preview.Clear();
            aiButton.Enabled = localButton.Enabled = translateButton.Enabled = false;
            source.ReadOnly = result.ReadOnly = true; copyButton.Enabled = false;
            stopButton.Enabled = stopMenu.Enabled = true; applyButton.Enabled = false;
            if (clearPreview) {
                pendingDraft = null; pendingExpected = pendingSource = pendingOriginal = null; result.Clear(); TextReview.Reset(source);
                reviewHint.ForeColor = Color.FromArgb(70, 80, 95); reviewHint.Text = "正在生成。下方是未完成预览，完成并检查后才会填回。";
            }
            UpdateSpaceHook(); uiTimer.Start(); tray.Text = "提示词快捷优化 · " + name;
            return operationVersion;
        }
        IProgress<string> PreviewProgress(int version, CancellationToken token) {
            return new Progress<string>(delta => {
                if (IsDisposed || !busy || version != operationVersion || token.IsCancellationRequested || String.IsNullOrEmpty(delta)) return;
                int length = Math.Min(delta.Length, 60000 - preview.Length);
                if (length <= 0) return;
                preview.Append(delta, 0, length); streamingCount = preview.Length;
                result.AppendText(delta.Substring(0, length)); result.SelectionStart = result.TextLength; result.ScrollToCaret();
            });
        }
        void StopActive() {
            if (!busy || activeCancellation == null || activeCancellation.IsCancellationRequested) return;
            operationVersion++; activeCancellation.Cancel(); stopButton.Enabled = stopMenu.Enabled = false;
            status.Text = "正在停止…";
        }
        void EndAiOperation() {
            busy = false; activeWatch = null; uiTimer.Stop();
            if (activeCancellation != null) { activeCancellation.Dispose(); activeCancellation = null; }
            if (IsDisposed) return;
            source.ReadOnly = result.ReadOnly = false; copyButton.Enabled = true;
            aiButton.Enabled = localButton.Enabled = translateButton.Enabled = true;
            stopButton.Enabled = stopMenu.Enabled = false;
            applyButton.Enabled = pendingDraft != null && !pendingDraft.PreviewOnly;
            UpdateSpaceHook();
        }
        static string[] ReviewWarnings(Dictionary<string, object> response) {
            object value;
            if (response == null || !response.TryGetValue("reviewWarnings", out value) || value is string) return new string[0];
            var values = value as System.Collections.IEnumerable;
            if (values == null) return new string[0];
            return values.Cast<object>().Where(v => v != null).Select(v => v.ToString()).ToArray();
        }
        void FinishOutput(Draft draft, string expected, string output, Dictionary<string, object> response = null, string restoreOriginal = null) {
            result.Text = output; TextReview.Highlight(source, result);
            string[] warnings = ReviewWarnings(response); object flag;
            bool review = warnings.Length > 0 || (response != null && response.TryGetValue("requiresReview", out flag) && Convert.ToBoolean(flag));
            pendingDraft = null; pendingExpected = pendingSource = pendingOriginal = null; applyButton.Enabled = false;
            bool unchanged = draft != null && Draft.SameText(expected, output);
            bool filled = !review && !unchanged && draft != null && draft.Replace(expected, output);
            if (filled) { lastDraft = draft; original = restoreOriginal ?? expected; applied = Draft.Read(draft.Editor); }
            if (draft != null && !filled && (!unchanged || review)) {
                pendingDraft = draft; pendingExpected = expected; pendingSource = source.Text; pendingOriginal = restoreOriginal ?? expected;
                applyButton.Enabled = !busy && !draft.PreviewOnly;
            }
            reviewHint.ForeColor = review ? Color.FromArgb(165, 80, 20) : Color.FromArgb(70, 80, 95);
            if (review) reviewHint.Text = "需要核对，结果尚未填回：" + (warnings.Length > 0 ? String.Join("；", warnings.Take(3).ToArray()) : "关键内容可能变化。") + "\n检查高亮差异后，可选择“确认填回”或复制结果。";
            else if (pendingDraft != null) reviewHint.Text = draft.PreviewOnly ? "此控件需要保留格式，请检查差异后复制结果使用。" : "原输入框、焦点或内容发生变化，结果留在面板。确认原文后可点击“确认填回”。";
            else reviewHint.Text = unchanged ? "原文已清楚，本次未作改动。" : "原文中的删除内容标红，结果中的新增内容标绿。";
            if (pendingDraft != null) { Open(); Notice(review ? "关键内容可能变化，请先检查面板中的差异。" : "结果已留在面板，请检查后使用。"); }
        }
        async Task ApplyReviewed() {
            if (busy || pendingDraft == null || pendingDraft.PreviewOnly) return;
            try {
                if (source.Text != pendingSource) throw new Exception("面板原文已编辑，请重新优化后再填回。");
                if (String.IsNullOrWhiteSpace(result.Text) || result.Text.Length > 60000) throw new Exception("请先检查结果正文。");
                var draft = pendingDraft; string expected = pendingExpected, replacement = result.Text;
                Native.SetForegroundWindow(draft.Window); draft.Editor.SetFocus(); await Task.Delay(120);
                if (!draft.Replace(expected, replacement)) { Open(); throw new Exception("原输入框已变化，未覆盖内容。请检查后手动使用结果。"); }
                lastDraft = draft; original = pendingOriginal; applied = Draft.Read(draft.Editor);
                pendingDraft = null; pendingExpected = pendingSource = pendingOriginal = null; applyButton.Enabled = false;
                status.Text = "已确认填回"; reviewHint.ForeColor = Color.FromArgb(40, 110, 65); reviewHint.Text = "结果已由你确认填回，可使用恢复原文快捷键撤回。";
            } catch (Exception error) { Open(); Notice(error.Message); }
        }
        async Task Warm() {
            if (busy || providerDialog != null) return;
            BeginAiOperation("AI 连接中", false);
            var token = activeCancellation.Token;
            try { await ai.Request("warmup", cancellationToken: token); token.ThrowIfCancellationRequested(); status.Text = "AI 连接已就绪"; Notice("AI 连接已就绪，" + providers.Current.name + " · " + providers.Current.model + "。"); }
            catch (OperationCanceledException) { status.Text = "连接已停止"; }
            catch (Exception error) { status.Text = "连接未完成"; Notice(error.Message); }
            finally { EndAiOperation(); }
        }
        async Task Optimize(bool useAi, bool fromHotkey) {
            if (busy) { Notice("AI 正在处理，可在面板或托盘中停止。"); return; }
            Draft draft = null; bool started = false;
            try {
                string input;
                if (fromHotkey) { draft = Draft.Capture(); input = draft.Text; } else input = source.Text;
                if (String.IsNullOrWhiteSpace(input)) throw new Exception("请先输入需要优化的内容。");
                if (input.Length > 20000) throw new Exception("草稿超过 20000 字符，请缩短后重试。");
                source.Text = input;
                var watch = Stopwatch.StartNew();
                if (useAi) {
                    int version = BeginAiOperation("AI 重写中"); started = true; var token = activeCancellation.Token;
                    var response = await ai.Request("optimize", input, cancellationToken: token, progress: PreviewProgress(version, token));
                    token.ThrowIfCancellationRequested(); FinishOutput(draft, input, Json.Str(response, "result"), response);
                } else FinishOutput(draft, input, LocalOptimizer.Optimize(input));
                status.Text = (useAi ? "AI " : "本地 ") + watch.Elapsed.TotalMilliseconds.ToString("0") + " ms";
                tray.Text = "提示词快捷优化 · " + status.Text;
            } catch (OperationCanceledException) {
                pendingDraft = null; pendingExpected = pendingSource = null; status.Text = "已停止";
                reviewHint.Text = "已停止。下方仅保留未完成预览，原输入框未填回。";
            } catch (Exception error) { status.Text = "未完成"; reviewHint.Text = "请求未完成，原文已保留。"; Notice(error.Message); }
            finally { if (started) EndAiOperation(); }
        }
        async Task Translate(bool fromGesture) {
            if (busy) return;
            Draft draft = null; bool started = false; string input, expected = null;
            try {
                if (fromGesture) {
                    if (DoubleSpaceHook.IsComposing(Native.GetForegroundWindow())) return;
                    try { draft = Draft.Capture(); } catch { return; }
                    expected = draft.Text;
                    if (!TranslationText.FromDraft(draft, out input)) return;
                } else input = source.Text;
                if (String.IsNullOrWhiteSpace(input)) { if (!fromGesture) Notice("请先输入需要翻译的内容。"); return; }
                if (input.Length > 20000) throw new Exception("草稿超过 20000 字符，请缩短后重试。");
                source.Text = input;
                int version = BeginAiOperation("翻译中"); started = true; var token = activeCancellation.Token;
                var response = await ai.Request("translate", input, settings.translationTarget, token, PreviewProgress(version, token));
                token.ThrowIfCancellationRequested(); FinishOutput(draft, expected, Json.Str(response, "result"), response, input);
                status.Text = "翻译 " + activeWatch.Elapsed.TotalSeconds.ToString("0.0") + "s"; tray.Text = "提示词快捷优化 · " + status.Text;
            } catch (OperationCanceledException) {
                pendingDraft = null; pendingExpected = pendingSource = null; status.Text = "已停止";
                reviewHint.Text = "翻译已停止。下方仅为未完成预览，原输入框未填回。";
            } catch (Exception error) { status.Text = "翻译未完成"; reviewHint.Text = "请求未完成，原文已保留。"; Notice(error.Message); }
            finally { if (started) EndAiOperation(); }
        }

        void Restore() {
            if (busy) { Notice("AI 正在改写，请返回后恢复。"); return; }
            try {
                if (lastDraft == null) throw new Exception("当前没有可恢复的优化记录。");
                if (!lastDraft.Replace(applied, original)) throw new Exception("当前草稿已变化。原文仍在优化面板中，可手动复制。");
                lastDraft = null; status.Text = "已恢复原文"; reviewHint.ForeColor = Color.FromArgb(70, 80, 95); reviewHint.Text = "已恢复原输入框中的原文；当前结果保留供核对。"; tray.Text = "提示词快捷优化 · 已恢复原文";
            } catch (Exception e) { Notice(e.Message); }
        }
    }
    public static class Program {
        [STAThread] public static int Main(string[] args) {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            try {
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                if (args.Length > 0 && (args[0] == "--quit" || args[0] == "--show" || args[0] == "--shortcuts")) {
                    var existing = Native.FindWindow(null, "提示词快捷优化");
                    if (existing != IntPtr.Zero) Native.PostMessage(existing, args[0] == "--quit" ? 0x8043u : args[0] == "--shortcuts" ? 0x8044u : 0x8042u, IntPtr.Zero, IntPtr.Zero);
                    return 0;
                }
                if (args.Length >= 2 && args[0] == "--self-test") return SelfTest(args[1]);
                if (args.Length >= 2 && args[0] == "--ui-smoke") return UiSmoke(args[1]);
                if (args.Length >= 2 && args[0] == "--ai-smoke") return AiSmoke(dir, args[1]);
                bool created;
                using (var mutex = new Mutex(true, "Local\\PromptQuickOptimizer-v01", out created)) {
                    if (!created) { Native.PostMessage(Native.FindWindow(null, "提示词快捷优化"), 0x8042, IntPtr.Zero, IntPtr.Zero); return 0; }
                    Application.Run(new OptimizerForm(dir, Settings.Load(dir)));
                }
                return 0;
            } catch (Exception e) {
                if (args.Length >= 2) File.WriteAllText(args[1], Json.Encode(new { ok = false, error = e.ToString() }), Encoding.UTF8);
                else MessageBox.Show(e.Message, "提示词快捷优化", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
        }
        static int SelfTest(string report) {
            var checks = new List<string>();
            Action<bool, string> check = (condition, name) => { if (!condition) throw new Exception("FAIL: " + name); checks.Add(name); };
            string raw = "  帮我找5篇MOF文献，2023–2026年，不要综述。\r\n路径 E:\\测试 目录\\data.csv；浓度0.5 mM，DOI:10.1000/example  ";
            string optimized = LocalOptimizer.Optimize(raw);
            check(optimized.Contains("5篇") && optimized.Contains("2023–2026") && optimized.Contains(@"E:\测试 目录\data.csv") && optimized.Contains("0.5 mM") && optimized.Contains("不要综述") && optimized.Contains("10.1000/example"), "本地改写保留数字、路径、单位及否定条件");
            check(LocalOptimizer.Optimize(optimized) == optimized, "重复整理不叠加或改变已改写内容");
            bool rejected = false; try { LocalOptimizer.Optimize(" \r\n"); } catch { rejected = true; } check(rejected, "空草稿拒绝");
            rejected = false; try { LocalOptimizer.Optimize(new string('a', 20001)); } catch { rejected = true; } check(rejected, "过长草稿拒绝");
            check(LocalOptimizer.Optimize("Translate ‘Do not remove 5.2 mg.’").Contains("5.2 mg"), "英文与标点保留");
            check(Draft.SameText("第一行\r\n第二行\n", "第一行\n第二行"), "适配输入框换行");
            check(!Draft.SameText("不要删除", "删除"), "内容变化不可视为相同");
            check(Hotkey.Parse("Ctrl+Alt+Shift+P").Modifiers == 0x4007, "快捷键组合解析");
            rejected = false; try { Hotkey.Parse("P"); } catch { rejected = true; } check(rejected, "拒绝占用普通输入键");
            checks.AddRange(HotkeyTests.Run());
            checks.AddRange(TranslationTests.Run());
            checks.AddRange(ProviderTests.Run());
            checks.AddRange(LocalOptimizerTests.Run());
            checks.AddRange(TextReview.Tests());
            var watch = Stopwatch.StartNew(); for (int i = 0; i < 1000; i++) LocalOptimizer.Optimize(raw); watch.Stop();
            File.WriteAllText(report, Json.Encode(new { ok = true, passed = checks, localAverageMs = watch.Elapsed.TotalMilliseconds / 1000, localExample = LocalOptimizer.Optimize("帮我找5篇MOF文献，要DOI，不要综述，先给列表确认后再下载。") }), new UTF8Encoding(false));
            return 0;
        }
        static int UiSmoke(string report) {
            var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
            AutomationElement root = null;
            foreach (AutomationElement w in windows) if (Draft.IsCodex(new IntPtr(w.Current.NativeWindowHandle))) { root = w; break; }
            if (root == null) throw new Exception("未找到 Codex 窗口。");
            var h = new IntPtr(root.Current.NativeWindowHandle); Native.SetForegroundWindow(h);
            Native.SendMessage(h, 0x003D, IntPtr.Zero, new IntPtr(-25)); Thread.Sleep(150);
            AutomationElement editor = null;
            foreach (AutomationElement candidate in root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)))
                if (candidate.Current.ClassName.Split(' ').Contains("ProseMirror")) { editor = candidate; break; }
            if (editor == null || Draft.Read(editor).Length != 0) throw new Exception("输入框不为空或不可读，已跳过测试。");
            string sample = "帮我找5篇MOF文献，附DOI，不要综述。";
            var pattern = (ValuePattern)editor.GetCurrentPattern(ValuePattern.Pattern);
            string inserted = null;
            try {
                editor.SetFocus(); pattern.SetValue(sample); inserted = sample;
                var watch = Stopwatch.StartNew(); var draft = Draft.Capture(); string before = draft.Text;
                string output = LocalOptimizer.Optimize(before);
                if (!draft.Replace(before, output)) throw new Exception("本地填回失败：" + draft.Diagnose(before)); inserted = output;
                double localMs = watch.Elapsed.TotalMilliseconds;
                string applied = Draft.Read(editor);
                if (!draft.Replace(applied, before)) throw new Exception("原文恢复失败。"); inserted = before;
                if (!Draft.SameText(Draft.Read(editor), sample)) throw new Exception("恢复核对失败。");
                pattern.SetValue("用户后来补充了条件"); inserted = "用户后来补充了条件";
                if (draft.Replace(applied, before)) throw new Exception("错误覆盖了新草稿。");
                File.WriteAllText(report, Json.Encode(new { ok = true, localRoundTripMs = localMs, writeVerified = true, restoreVerified = true, changedDraftProtected = true }), new UTF8Encoding(false));
            } finally {
                if (inserted != null && Draft.SameText(Draft.Read(editor), inserted)) pattern.SetValue("");
            }
            return 0;
        }
        static int AiSmoke(string dir, string report) {
            using (var client = new AiClient(dir, Settings.Load(dir))) {
                var watch = Stopwatch.StartNew();
                var response = client.Request("optimize", "帮我找5篇近三年的MOF荧光传感文献，附DOI，不要综述").GetAwaiter().GetResult();
                File.WriteAllText(report, Json.Encode(new { ok = true, elapsedMs = watch.Elapsed.TotalMilliseconds, result = Json.Str(response, "result") }), new UTF8Encoding(false));
            }
            return 0;
        }
    }
}
