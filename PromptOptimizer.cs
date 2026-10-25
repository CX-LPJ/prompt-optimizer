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
        public string model = "gpt-6-luna";
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
            if (settings.doubleSpaceMilliseconds < 150 || settings.doubleSpaceMilliseconds > 1000 || !new[] { "auto", "english", "chinese" }.Contains(settings.translationTarget)) throw new Exception("翻译方向须为 auto、english 或 chinese，双击间隔须在 150–1000 ms 之间。");
            return settings;
        }
    }
    public static class LocalOptimizer {
        const string Prefix = "请完成以下任务。\n\n任务：\n";
        public static string Optimize(string source) {
            if (String.IsNullOrWhiteSpace(source)) throw new Exception("请先输入需要优化的内容。");
            if (source.Length > 20000) throw new Exception("草稿超过 20000 字符，请缩短后重试。");
            if (source.Replace("\r\n", "\n").StartsWith(Prefix, StringComparison.Ordinal)) return source;
            string rule = "遵守任务中给出的范围和限制；缺少关键条件时简短指出，不自行编造。";
            if (Regex.IsMatch(source, "文献|论文检索|DOI|doi|literature|references", RegexOptions.IgnoreCase))
                rule += " 文献信息须能追溯到真实来源，并标明无法核验之处。";
            else if (Regex.IsMatch(source, "绘图|画图|作图|Origin|plot", RegexOptions.IgnoreCase))
                rule += " 保留原始数据，核对坐标、单位、图例与文字，清楚说明实际改动。";
            else if (Regex.IsMatch(source, "润色|翻译|改写|translate|polish", RegexOptions.IgnoreCase))
                rule += " 保留原意、数字、术语和事实；优先给出可直接使用的文本。";
            else if (Regex.IsMatch(source, "代码|程序|修复|报错|bug|code", RegexOptions.IgnoreCase))
                rule += " 按任务范围定位相关原因，清楚区分建议、实际改动和已验证结果。";
            return Prefix + source + "\n\n执行要求：\n" + rule + "\n\n结果：\n先给可直接使用的结果，再简要说明必要的依据、假设和未解决事项。";
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
        readonly Dictionary<int, TaskCompletionSource<Dictionary<string, object>>> pending = new Dictionary<int, TaskCompletionSource<Dictionary<string, object>>>();
        int counter; readonly object gate = new object();
        public AiClient(string dir, Settings settings) { this.dir = dir; this.settings = settings; }
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
            worker.Exited += (s, e) => FailAll("AI 连接已结束，请重试。");
            worker.Start(); worker.StandardInput.AutoFlush = true;
            worker.BeginOutputReadLine(); worker.BeginErrorReadLine();
        }
        void Handle(string line) {
            try {
                var value = Json.Decode(line); int id = Convert.ToInt32(value["id"]);
                TaskCompletionSource<Dictionary<string, object>> task;
                lock (gate) { if (!pending.TryGetValue(id, out task)) return; pending.Remove(id); }
                if (Convert.ToBoolean(value["ok"])) task.TrySetResult(value); else task.TrySetException(new Exception(Json.Str(value, "error")));
            } catch { }
        }
        void FailAll(string message) { lock (gate) { foreach (var task in pending.Values) task.TrySetException(new Exception(message)); pending.Clear(); } }
        public async Task<Dictionary<string, object>> Request(string action, string text = "", string targetLanguage = "auto") {
            EnsureWorker();
            int id = Interlocked.Increment(ref counter);
            var task = new TaskCompletionSource<Dictionary<string, object>>();
            lock (gate) pending.Add(id, task);
            var q = new { id = id, action = action, text = text, targetLanguage = targetLanguage, model = settings.model, timeoutMs = settings.aiTimeoutSeconds * 1000,
                cwd = Path.Combine(dir, "ai-work"), codexPath = Locate(settings.codexPath, "codex.exe", @"AppData\Local\OpenAI\Codex\bin") };
            Directory.CreateDirectory(q.cwd);
            try {
                byte[] bytes = new UTF8Encoding(false).GetBytes(Json.Encode(q) + "\n");
                worker.StandardInput.BaseStream.Write(bytes, 0, bytes.Length); worker.StandardInput.BaseStream.Flush();
                if (await Task.WhenAny(task.Task, Task.Delay((settings.aiTimeoutSeconds + 45) * 1000)) != task.Task) { Dispose(); throw new Exception("AI 连接超时，原文已保留。"); }
                return await task.Task;
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
        readonly Settings settings; readonly AiClient ai; readonly NotifyIcon tray; readonly TextBox source, result;
        readonly Label status; readonly Button localButton, aiButton, translateButton; readonly string dir;
        readonly Label shortcutHint;
        HotkeyRegistry hotkeys; ShortcutDialog shortcutDialog; string startupHotkeyError;
        Draft lastDraft; string original, applied; bool busy, exiting, paused;
        readonly System.Windows.Forms.Timer uiTimer = new System.Windows.Forms.Timer { Interval = 250 };
        Stopwatch activeWatch;
        string activeOperation = "AI 重写中";
        DoubleSpaceHook spaceHook;
        readonly System.Windows.Forms.Timer gestureTimer = new System.Windows.Forms.Timer { Interval = 100 };
        int gestureVersion; IntPtr gestureWindow;
        public OptimizerForm(string dir, Settings settings) {
            this.dir = dir; this.settings = settings; ai = new AiClient(dir, settings);
            Text = "提示词快捷优化"; Font = new Font("Microsoft YaHei UI", 10); Size = new Size(940, 690); MinimumSize = new Size(750, 580);
            Icon = new Icon(Path.Combine(dir, "prompt.ico"), 32, 32);
            StartPosition = FormStartPosition.CenterScreen; ShowInTaskbar = true; BackColor = Color.FromArgb(248, 250, 252);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 7 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 56));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.Controls.Add(new Label { Text = "把想法变成清楚、可执行的提示词", Font = new Font(Font.FontFamily, 16, FontStyle.Bold), AutoSize = true }, 0, 0);
            shortcutHint = new Label { AutoSize = true, ForeColor = Color.FromArgb(70, 80, 95) }; UpdateShortcutHint(); layout.Controls.Add(shortcutHint, 0, 1);
            layout.Controls.Add(new Label { Text = "原文（也可在这里粘贴）", AutoSize = true }, 0, 2);
            source = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, AcceptsReturn = true, MaxLength = 20000 };
            result = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, AcceptsReturn = true, ReadOnly = false, BackColor = Color.White };
            layout.Controls.Add(source, 0, 3); layout.Controls.Add(new Label { Text = "优化结果（可继续编辑）", AutoSize = true }, 0, 4); layout.Controls.Add(result, 0, 5);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0), WrapContents = true };
            localButton = new Button { Text = "本地整理", AutoSize = true }; aiButton = new Button { Text = "AI 重写", AutoSize = true };
            translateButton = new Button { Text = "中英互译", AutoSize = true }; translateButton.Click += async (s, e) => await Translate(false);
            var copy = new Button { Text = "复制结果", AutoSize = true }; var hide = new Button { Text = "收起到托盘", AutoSize = true };
            var configure = new Button { Text = "快捷键设置", AutoSize = true, Name = "configureShortcuts" }; configure.Click += (s, e) => OpenShortcutSettings();
            status = new Label { Text = "就绪", AutoSize = true, Padding = new Padding(8, 5, 0, 0) };
            actions.Controls.AddRange(new Control[] { localButton, aiButton, translateButton, copy, configure, hide, status }); layout.Controls.Add(actions, 0, 6); Controls.Add(layout);
            localButton.Click += async (s, e) => await Optimize(false, false); aiButton.Click += async (s, e) => await Optimize(true, false);
            copy.Click += (s, e) => { try { if (result.Text.Length > 0) { Clipboard.SetText(result.Text); status.Text = "已复制"; } } catch { status.Text = "剪贴板忙，请重试"; } };
            hide.Click += (s, e) => Hide();
            var menu = new ContextMenuStrip(); menu.Items.Add("打开优化面板", null, (s, e) => Open());
            menu.Items.Add("快捷键设置…", null, (s, e) => OpenShortcutSettings());
            menu.Items.Add("预热 AI 连接", null, async (s, e) => await Warm());
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
            uiTimer.Tick += (s, e) => { if (busy && activeWatch != null) status.Text = activeOperation + " " + activeWatch.Elapsed.TotalSeconds.ToString("0.0") + "s"; };
            gestureTimer.Tick += async (s, e) => { gestureTimer.Stop(); if (spaceHook != null && spaceHook.Enabled && spaceHook.Generation == gestureVersion && Native.GetForegroundWindow() == gestureWindow) await Translate(true); };
            FormClosing += (s, e) => { if (!exiting) { e.Cancel = true; Hide(); } };
            FormClosed += (s, e) => { UnregisterKeys(); gestureTimer.Stop(); if (spaceHook != null) spaceHook.Dispose(); uiTimer.Stop(); tray.Dispose(); ai.Dispose(); };
            Shown += (s, e) => { Hide(); try { spaceHook = new DoubleSpaceHook(Handle, settings.doubleSpaceMilliseconds); UpdateSpaceHook(); } catch (Exception ex) { Notice(ex.Message); } if (startupHotkeyError != null) { Open(); OpenShortcutSettings(startupHotkeyError); } else Notice("已启动。在支持的文本框内双击空格即可中英互译。右键托盘可设置。"); };
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
        void UpdateShortcutHint() { shortcutHint.Text = settings.localHotkey + " 本地整理    |    " + settings.aiHotkey + " AI 重写\n" + settings.restoreHotkey + " 恢复原文    ·    支持普通文本框、记事本及网页输入框\n双击空格翻译：" + (settings.doubleSpaceTranslation ? "开启" : "关闭") + " · " + (settings.translationTarget == "auto" ? "自动中英互译" : settings.translationTarget == "english" ? "译成英文" : "译成中文"); }
        void UpdateSpaceHook() { if (spaceHook != null) spaceHook.Enabled = settings.doubleSpaceTranslation && !paused && shortcutDialog == null && !busy; }
        void SetTranslationPreferences(bool enabled, string target) { TranslationText.SavePreferences(dir, settings, enabled, target); settings.doubleSpaceTranslation = enabled; settings.translationTarget = target; UpdateShortcutHint(); UpdateSpaceHook(); }
        void ApplyShortcuts(string[] values) {
            var canonical = hotkeys.Apply(values, keys => ShortcutStore.Save(dir, settings, keys));
            settings.localHotkey = canonical[0]; settings.aiHotkey = canonical[1]; settings.restoreHotkey = canonical[2];
            if (paused) hotkeys.Clear();
            startupHotkeyError = null; UpdateShortcutHint(); status.Text = paused ? "已保存 · 暂停中" : "快捷键已生效";
        }
        void OpenShortcutSettings(string error = null) {
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
            if (m.Msg == 0x0312) {
                int action; string shortcut;
                if (hotkeys == null || !hotkeys.TryAction(m.WParam.ToInt32(), out action, out shortcut)) return;
                if (shortcutDialog != null) { shortcutDialog.RecordRegisteredKey(shortcut); return; }
                if (action == 3) Restore(); else HandleOptimizeHotkey(action == 2);
            }
            base.WndProc(ref m);
        }
        async void HandleOptimizeHotkey(bool useAi) { await Optimize(useAi, true); }
        async Task Warm() {
            try { var response = await ai.Request("warmup"); status.Text = "AI 连接已就绪"; Notice("AI 连接已就绪，模型 " + settings.model + "。" ); }
            catch (Exception e) { Notice(e.Message); }
        }
        async Task Optimize(bool useAi, bool fromHotkey) {
            if (busy) { Notice("AI 正在改写，请稍候。"); return; }
            Draft draft = null;
            try {
                string input;
                if (fromHotkey) { draft = Draft.Capture(); input = draft.Text; } else input = source.Text;
                if (String.IsNullOrWhiteSpace(input)) throw new Exception("请先输入需要优化的内容。");
                if (input.Length > 20000) throw new Exception("草稿超过 20000 字符，请缩短后重试。");
                // Keep the source visible/recoverable in memory before any editor write.
                source.Text = input;
                string output;
                var watch = Stopwatch.StartNew();
                if (useAi) {
                    busy = true; activeOperation = "AI 重写中"; aiButton.Enabled = localButton.Enabled = translateButton.Enabled = false; UpdateSpaceHook(); activeWatch = watch; uiTimer.Start();
                    tray.Text = "提示词快捷优化 · AI 重写中";
                    var response = await ai.Request("optimize", input); output = Json.Str(response, "result");
                } else output = LocalOptimizer.Optimize(input);
                result.Text = output;
                bool filled = draft != null && draft.Replace(input, output);
                if (filled) { lastDraft = draft; original = input; applied = Draft.Read(draft.Editor); }
                status.Text = (useAi ? "AI " : "本地 ") + watch.Elapsed.TotalMilliseconds.ToString("0") + " ms";
                tray.Text = "提示词快捷优化 · " + status.Text;
                if (draft != null && !filled) { Open(); Notice(draft.PreviewOnly ? "此控件保留格式，结果已放入面板，请手动使用。" : "草稿或焦点已变化。结果已留在面板，请自行复制使用。"); }
                else if (useAi && filled) Notice("AI 改写已填回，耗时 " + watch.Elapsed.TotalSeconds.ToString("0.0") + " 秒。");
            } catch (Exception e) { status.Text = "未完成"; Notice(e.Message); }
            finally { busy = false; activeWatch = null; uiTimer.Stop(); aiButton.Enabled = localButton.Enabled = translateButton.Enabled = true; UpdateSpaceHook(); }
        }
        async Task Translate(bool fromGesture) {
            if (busy) return;
            Draft draft = null; string input, expected = null;
            try {
                if (fromGesture) {
                    if (DoubleSpaceHook.IsComposing(Native.GetForegroundWindow())) return;
                    try { draft = Draft.Capture(); } catch { return; }
                    expected = draft.Text;
                    if (!TranslationText.FromDraft(draft, out input)) return;
                } else input = source.Text;
                if (String.IsNullOrWhiteSpace(input)) { if (!fromGesture) Notice("请先输入需要翻译的内容。"); return; }
                if (input.Length > 20000) throw new Exception("草稿超过 20000 字符，请缩短后重试。");
                source.Text = input; busy = true; activeOperation = "翻译中";
                aiButton.Enabled = localButton.Enabled = translateButton.Enabled = false; UpdateSpaceHook();
                var watch = Stopwatch.StartNew(); activeWatch = watch; uiTimer.Start(); tray.Text = "提示词快捷优化 · 翻译中";
                var response = await ai.Request("translate", input, settings.translationTarget); string output = Json.Str(response, "result"); result.Text = output;
                bool filled = draft != null && draft.Replace(expected, output);
                if (filled) { lastDraft = draft; original = input; applied = Draft.Read(draft.Editor); }
                status.Text = "翻译 " + watch.Elapsed.TotalSeconds.ToString("0.0") + "s"; tray.Text = "提示词快捷优化 · " + status.Text;
                if (draft != null && !filled) { Open(); Notice(draft.PreviewOnly ? "此控件保留格式，译文已放入面板，请手动使用。" : "草稿或焦点已变化。译文已留在面板，请自行复制使用。"); }
                else if (filled) Notice("译文已填回，可按 " + settings.restoreHotkey + " 恢复原文。");
            } catch (Exception ex) { status.Text = "翻译未完成"; Notice(ex.Message); }
            finally { busy = false; activeWatch = null; uiTimer.Stop(); aiButton.Enabled = localButton.Enabled = translateButton.Enabled = true; UpdateSpaceHook(); }
        }
        void Restore() {
            if (busy) { Notice("AI 正在改写，请返回后恢复。"); return; }
            try {
                if (lastDraft == null) throw new Exception("当前没有可恢复的优化记录。");
                if (!lastDraft.Replace(applied, original)) throw new Exception("当前草稿已变化。原文仍在优化面板中，可手动复制。");
                lastDraft = null; status.Text = "已恢复原文"; tray.Text = "提示词快捷优化 · 已恢复原文";
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
            check(optimized.Contains(raw), "保留原文、数字、路径、单位及否定条件");
            check(LocalOptimizer.Optimize(optimized) == optimized, "重复优化不叠加模板");
            bool rejected = false; try { LocalOptimizer.Optimize(" \r\n"); } catch { rejected = true; } check(rejected, "空草稿拒绝");
            rejected = false; try { LocalOptimizer.Optimize(new string('a', 20001)); } catch { rejected = true; } check(rejected, "过长草稿拒绝");
            check(LocalOptimizer.Optimize("Translate ‘Do not remove 5.2 mg.’").Contains("5.2 mg"), "英文与标点保留");
            check(Draft.SameText("第一行\r\n第二行\n", "第一行\n第二行"), "适配输入框换行");
            check(!Draft.SameText("不要删除", "删除"), "内容变化不可视为相同");
            check(Hotkey.Parse("Ctrl+Alt+Shift+P").Modifiers == 0x4007, "快捷键组合解析");
            rejected = false; try { Hotkey.Parse("P"); } catch { rejected = true; } check(rejected, "拒绝占用普通输入键");
            checks.AddRange(HotkeyTests.Run());
            checks.AddRange(TranslationTests.Run());
            var watch = Stopwatch.StartNew(); for (int i = 0; i < 1000; i++) LocalOptimizer.Optimize(raw); watch.Stop();
            File.WriteAllText(report, Json.Encode(new { ok = true, passed = checks, localAverageMs = watch.Elapsed.TotalMilliseconds / 1000 }), new UTF8Encoding(false));
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
