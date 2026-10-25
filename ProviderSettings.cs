using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace PromptQuick {
    public sealed class ProviderProfile {
        public string id, name, kind = "openai", baseUrl = "", model = "", reasoningEffort = "auto", encryptedApiKey = "";
        public override string ToString() { return name; }
        public static string NormalizeUrl(string value) {
            Uri uri;
            if (!Uri.TryCreate((value ?? "").Trim(), UriKind.Absolute, out uri)) throw new Exception("请填写有效的 Base URL。");
            bool local = new[] { "localhost", "127.0.0.1", "[::1]", "::1" }.Contains(uri.Host.ToLowerInvariant());
            if ((uri.Scheme != "https" && !(uri.Scheme == "http" && local)) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                throw new Exception("Base URL 须使用 HTTPS；本机 localhost、127.0.0.1 可使用 HTTP。地址中不要包含账号、密码或查询参数。");
            return Regex.Replace(uri.AbsoluteUri.TrimEnd('/'), @"/(chat/completions|models)$", "");
        }
        byte[] Entropy() { return Encoding.UTF8.GetBytes("PromptOptimizer|" + id + "|" + NormalizeUrl(baseUrl)); }
        public void SetApiKey(string value) {
            if (String.IsNullOrWhiteSpace(value)) { encryptedApiKey = ""; return; }
            var bytes = Encoding.UTF8.GetBytes(value.Trim());
            try { encryptedApiKey = Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy(), DataProtectionScope.CurrentUser)); }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }
        public string GetApiKey() {
            if (String.IsNullOrEmpty(encryptedApiKey)) return "";
            byte[] bytes = null;
            try { bytes = ProtectedData.Unprotect(Convert.FromBase64String(encryptedApiKey), Entropy(), DataProtectionScope.CurrentUser); return Encoding.UTF8.GetString(bytes); }
            catch { throw new Exception("无法解密此供应商密钥。若更换过地址、电脑或 Windows 用户，请重新填写 API Key。"); }
            finally { if (bytes != null) Array.Clear(bytes, 0, bytes.Length); }
        }
        public object Request() {
            return new { id = id, kind = kind, baseUrl = baseUrl, model = model, reasoningEffort = reasoningEffort, apiKey = kind == "codex" ? "" : GetApiKey() };
        }
    }
    public sealed class ProviderDocument {
        public string selectedId = "codex";
        public List<ProviderProfile> providers = new List<ProviderProfile>();
    }
    public sealed class ProviderStore {
        readonly string file;
        ProviderDocument data;
        bool recoveryPending;
        public string LoadWarning { get; private set; }
        public string RecoveryBackupPath { get; private set; }
        public ProviderStore(string dir, Settings settings) {
            file = Path.Combine(dir, "providers.json");
            LoadWarning = ""; RecoveryBackupPath = "";
            data = Defaults(settings);
            if (!File.Exists(file)) return;
            try {
                if (new FileInfo(file).Length > 1024 * 1024) throw new Exception("Configuration too large.");
                var serializer = new JavaScriptSerializer();
                var root = serializer.DeserializeObject(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>;
                object rawProviders;
                if (root == null || !root.TryGetValue("providers", out rawProviders) || rawProviders is string || !(rawProviders is System.Collections.IEnumerable)) throw new Exception("Invalid configuration.");
                var recovered = new ProviderDocument();
                var ids = new HashSet<string>(StringComparer.Ordinal);
                bool repaired = false;
                foreach (var raw in (System.Collections.IEnumerable)rawProviders) {
                    try {
                        var profile = serializer.Deserialize<ProviderProfile>(serializer.Serialize(raw));
                        if (!ValidProfile(profile) || !ids.Add(profile.id) || recovered.providers.Count >= 64) { repaired = true; continue; }
                        recovered.providers.Add(profile);
                    } catch { repaired = true; }
                }
                var fallback = recovered.providers.FirstOrDefault(p => p.kind == "codex" && !String.IsNullOrWhiteSpace(p.model));
                if (fallback == null) {
                    fallback = Defaults(settings).providers[0];
                    if (ids.Contains(fallback.id)) fallback.id = "codex-recovery-" + Guid.NewGuid().ToString("N");
                    if (recovered.providers.Count >= 64) recovered.providers.RemoveAt(recovered.providers.Count - 1);
                    recovered.providers.Insert(0, fallback);
                    repaired = true;
                }
                object selected;
                recovered.selectedId = root.TryGetValue("selectedId", out selected) ? selected as string : null;
                if (!recovered.providers.Any(p => p.id == recovered.selectedId)) { recovered.selectedId = fallback.id; repaired = true; }
                Validate(recovered); data = recovered;
                if (repaired) RecoveryWarning("部分供应商配置异常，已保留可用项；保存时先备份原文件。");
            } catch {
                data = Defaults(settings);
                RecoveryWarning("供应商配置读取失败，已恢复 Codex；原文件未改动，保存时先备份。");
            }
        }
        static ProviderDocument Defaults(Settings settings) {
            return new ProviderDocument { providers = new List<ProviderProfile> {
                new ProviderProfile { id = "codex", name = "Codex（当前登录）", kind = "codex", model = settings.model, reasoningEffort = settings.reasoningEffort },
                new ProviderProfile { id = "openai", name = "OpenAI", baseUrl = "https://api.openai.com/v1" },
                new ProviderProfile { id = "compatible", name = "其他 OpenAI 兼容供应商" }
            }};
        }
        void RecoveryWarning(string message) { recoveryPending = true; LoadWarning = message; }
        static bool ValidProfile(ProviderProfile profile) {
            return profile != null && Regex.IsMatch(profile.id ?? "", "^[A-Za-z0-9_-]{1,80}$") && !String.IsNullOrWhiteSpace(profile.name) && profile.name.Length <= 80 && new[] { "codex", "openai" }.Contains(profile.kind);
        }
        public ProviderDocument Copy() { return new JavaScriptSerializer().Deserialize<ProviderDocument>(Json.Encode(data)); }
        public ProviderProfile Current { get { return data.providers.First(p => p.id == data.selectedId); } }
        public static void Validate(ProviderDocument value) {
            if (value == null || value.providers == null || value.providers.Count == 0 || value.providers.Count > 64 || value.providers.Any(p => !ValidProfile(p))) throw new Exception("供应商配置无效。");
            if (value.providers.Select(p => p.id).Distinct().Count() != value.providers.Count || !value.providers.Any(p => p.id == value.selectedId)) throw new Exception("供应商选择无效。");
        }
        public void Save(ProviderDocument value) {
            Validate(value);
            var selected = value.providers.First(p => p.id == value.selectedId);
            if (String.IsNullOrWhiteSpace(selected.model)) throw new Exception("请选择或填写当前供应商的模型名称。");
            if (selected.kind != "codex") {
                selected.baseUrl = ProviderProfile.NormalizeUrl(selected.baseUrl);
                if (!String.IsNullOrEmpty(selected.encryptedApiKey)) selected.GetApiKey();
            }
            string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                File.WriteAllText(temp, Json.Encode(value), new UTF8Encoding(false));
                if (recoveryPending && File.Exists(file)) {
                    string backup = Path.Combine(Path.GetDirectoryName(file), "providers.recovery-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json");
                    File.Copy(file, backup, false);
                    RecoveryBackupPath = backup;
                }
                if (File.Exists(file)) File.Replace(temp, file, null); else File.Move(temp, file);
                data = new JavaScriptSerializer().Deserialize<ProviderDocument>(Json.Encode(value));
                recoveryPending = false; LoadWarning = "";
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
    public sealed class ProviderDialog : Form {
        readonly ProviderStore store; readonly AiClient ai; readonly Action applied;
        readonly ProviderDocument draft;
        readonly ComboBox provider, model, effort;
        readonly TextBox name, baseUrl, key;
        readonly CheckBox showKey, clearKey;
        readonly Label keyHint, status;
        readonly Button add, load, test, save, cancel;
        ProviderProfile current;
        bool loading, busy, closeAfterStop;
        CancellationTokenSource requestCancellation;
        readonly string[] efforts = { "auto", "none", "minimal", "low", "medium", "high", "xhigh", "max" };
        public ProviderDialog(ProviderStore store, AiClient ai, Action applied) {
            this.store = store; this.ai = ai; this.applied = applied; draft = store.Copy();
            Text = "供应商与模型"; Name = "ProviderDialog"; Font = new Font("Microsoft YaHei UI", 10);
            BackColor = Color.FromArgb(248, 250, 252); ClientSize = new Size(760, 550); MinimumSize = new Size(776, 589);
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; MaximizeBox = false; MinimizeBox = false; FormBorderStyle = FormBorderStyle.FixedDialog;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 2, RowCount = 11 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 48, 38, 38, 38, 38, 48, 42, 38, 55, 72, 48 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            var title = new Label { Text = "选择供应商和模型\n各供应商独立保存地址、模型与密钥。", AutoSize = true, Dock = DockStyle.Fill }; layout.Controls.Add(title, 0, 0); layout.SetColumnSpan(title, 2);
            provider = new ComboBox { Name = "providerSelect", DropDownStyle = ComboBoxStyle.DropDownList, Width = 370 };
            add = new Button { Name = "addProvider", Text = "新增自定义", AutoSize = true };
            var providerRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false }; providerRow.Controls.AddRange(new Control[] { provider, add });
            name = new TextBox { Name = "providerName", Dock = DockStyle.Top, MaxLength = 80 };
            baseUrl = new TextBox { Name = "baseUrl", Dock = DockStyle.Top, MaxLength = 2048 };
            key = new TextBox { Name = "apiKey", Width = 335, UseSystemPasswordChar = true, MaxLength = 4096 };
            showKey = new CheckBox { Text = "显示", AutoSize = true, Name = "showApiKey" }; showKey.CheckedChanged += (s, e) => key.UseSystemPasswordChar = !showKey.Checked;
            clearKey = new CheckBox { Text = "清除已保存密钥", AutoSize = true, Name = "clearApiKey" };
            var keyRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false }; keyRow.Controls.AddRange(new Control[] { key, showKey, clearKey });
            keyHint = new Label { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(70, 80, 95) };
            model = new ComboBox { Name = "modelSelect", DropDownStyle = ComboBoxStyle.DropDown, Width = 360, MaxLength = 256 };
            load = new Button { Name = "loadModels", Text = "加载模型", AutoSize = true };
            var modelRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false }; modelRow.Controls.AddRange(new Control[] { model, load });
            effort = new ComboBox { Name = "reasoningSelect", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top }; effort.Items.AddRange(efforts);
            var hint = new Label { Text = "模型支持下拉选择或直接输入。API 的 auto 不发送推理参数。\n测试连接会发送一条简短请求，可能产生少量用量。保存后立即生效。", AutoSize = true, Dock = DockStyle.Fill };
            status = new Label { Name = "providerStatus", Text = "就绪", AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(70, 80, 95) };
            test = new Button { Name = "testProvider", Text = "测试连接", AutoSize = true };
            save = new Button { Name = "saveProvider", Text = "保存并使用", AutoSize = true };
            cancel = new Button { Name = "cancelProvider", Text = "取消", AutoSize = true, DialogResult = DialogResult.None };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft }; buttons.Controls.AddRange(new Control[] { cancel, save, test });
            string[] labels = { "供应商", "显示名称", "Base URL", "API Key", "", "模型", "推理强度" };
            Control[] fields = { providerRow, name, baseUrl, keyRow, keyHint, modelRow, effort };
            for (int i = 0; i < fields.Length; i++) { layout.Controls.Add(new Label { Text = labels[i], AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, 0, i + 1); layout.Controls.Add(fields[i], 1, i + 1); }
            layout.Controls.Add(hint, 0, 8); layout.SetColumnSpan(hint, 2); layout.Controls.Add(status, 0, 9); layout.SetColumnSpan(status, 2);
            layout.Controls.Add(buttons, 0, 10); layout.SetColumnSpan(buttons, 2); Controls.Add(layout); CancelButton = cancel;
            add.Click += (s, e) => AddProvider();
            provider.SelectedIndexChanged += async (s, e) => { if (!loading) await SelectProvider(); };
            load.Click += async (s, e) => await LoadModels();
            test.Click += async (s, e) => await TestConnection();
            cancel.Click += (s, e) => { if (busy) StopRequest(); else { DialogResult = DialogResult.Cancel; Close(); } };
            save.Click += (s, e) => { try { CaptureFields(true); draft.selectedId = current.id; store.Save(draft); ai.Dispose(); applied(); DialogResult = DialogResult.OK; Close(); } catch (Exception ex) { ShowError(ex); } };
            FormClosing += (s, e) => { if (busy) { e.Cancel = true; closeAfterStop = true; StopRequest(); } };
            loading = true; foreach (var profile in draft.providers) provider.Items.Add(profile); provider.SelectedIndex = draft.providers.FindIndex(p => p.id == draft.selectedId); loading = false;
            current = (ProviderProfile)provider.SelectedItem; LoadFields();
            Shown += async (s, e) => { if (CanAutoLoad()) await LoadModels(); };
        }
        bool CanAutoLoad() {
            Uri uri;
            return current.kind == "codex" || !String.IsNullOrEmpty(current.encryptedApiKey) || (Uri.TryCreate(current.baseUrl, UriKind.Absolute, out uri) && uri.IsLoopback);
        }
        string WithWarning(string text) { return String.IsNullOrEmpty(store.LoadWarning) ? text : text + "\n" + store.LoadWarning; }
        void ShowError(Exception error) { status.ForeColor = Color.FromArgb(170, 45, 45); status.Text = WithWarning(error.Message); }
        void SetBusy(bool value, string text) {
            busy = value;
            foreach (Control control in new Control[] { provider, add, name, model, effort, load, test, save }) control.Enabled = !value;
            cancel.Enabled = true; cancel.Text = value ? "停止" : "取消"; cancel.DialogResult = DialogResult.None;
            baseUrl.Enabled = key.Enabled = showKey.Enabled = clearKey.Enabled = !value && current.kind != "codex";
            status.ForeColor = Color.FromArgb(70, 80, 95); status.Text = WithWarning(text);
        }
        void StopRequest() {
            if (requestCancellation != null && !requestCancellation.IsCancellationRequested) {
                status.Text = WithWarning("正在停止连接…");
                requestCancellation.Cancel();
            }
        }
        void FinishRequest(CancellationTokenSource cancellation) {
            if (Object.ReferenceEquals(requestCancellation, cancellation)) requestCancellation = null;
            cancellation.Dispose();
            if (IsDisposed) return;
            if (busy) SetBusy(false, "就绪");
            if (closeAfterStop) {
                closeAfterStop = false;
                BeginInvoke(new Action(() => { if (!IsDisposed) { DialogResult = DialogResult.Cancel; Close(); } }));
            }
        }
        void CaptureFields(bool requireModel, bool validateConnection = true) {
            if (String.IsNullOrWhiteSpace(name.Text)) throw new Exception("请填写供应商显示名称。");
            if (requireModel && String.IsNullOrWhiteSpace(model.Text)) throw new Exception("请选择或填写模型名称。");
            var candidate = new JavaScriptSerializer().Deserialize<ProviderProfile>(Json.Encode(current));
            candidate.name = name.Text.Trim(); candidate.model = model.Text.Trim(); candidate.reasoningEffort = effort.SelectedItem as string ?? "auto";
            if (candidate.kind != "codex") {
                candidate.baseUrl = (baseUrl.Text ?? "").Trim();
                if (validateConnection && candidate.baseUrl.Length > 0) candidate.baseUrl = ProviderProfile.NormalizeUrl(candidate.baseUrl);
                if (clearKey.Checked && key.Text.Length > 0) throw new Exception("请取消清除密钥，或清空新输入的密钥。");
                if (clearKey.Checked) candidate.encryptedApiKey = "";
                else if (key.Text.Length > 0) candidate.SetApiKey(key.Text);
                else if (validateConnection && !String.IsNullOrEmpty(candidate.encryptedApiKey)) candidate.GetApiKey();
            }
            int index = draft.providers.IndexOf(current); draft.providers[index] = candidate; current = candidate;
            loading = true; provider.Items[index] = candidate; provider.SelectedIndex = index; loading = false;
        }
        void LoadFields() {
            name.Text = current.name; baseUrl.Text = current.baseUrl; key.Clear(); showKey.Checked = clearKey.Checked = false;
            model.Items.Clear(); model.Text = current.model; effort.SelectedItem = current.reasoningEffort; if (effort.SelectedIndex < 0) effort.SelectedIndex = 0;
            keyHint.Text = current.kind == "codex" ? "使用本机 Codex 登录，无需填写 API Key。" : String.IsNullOrEmpty(current.encryptedApiKey) ? "请输入此供应商的密钥；本地无密钥服务可留空。\nBase URL 通常填到 /v1。" : "密钥已加密保存；留空保留，输入新值替换。\n更换地址后请重新填写密钥。";
            SetBusy(false, "就绪");
        }
        async Task SelectProvider() {
            var target = provider.SelectedItem as ProviderProfile;
            if (target == null || target.id == current.id) return;
            try { CaptureFields(false, false); } catch (Exception ex) { loading = true; provider.SelectedIndex = draft.providers.FindIndex(p => p.id == current.id); loading = false; ShowError(ex); return; }
            loading = true; provider.SelectedIndex = draft.providers.FindIndex(p => p.id == target.id); loading = false;
            current = draft.providers.First(p => p.id == target.id); LoadFields();
            if (CanAutoLoad()) await LoadModels();
            else status.Text = "配置地址和密钥后点击加载模型，也可以直接填写模型名。";
        }
        void AddProvider() {
            try {
                CaptureFields(false, false); if (draft.providers.Count >= 64) throw new Exception("供应商最多保存 64 个。");
                var profile = new ProviderProfile { id = "custom-" + Guid.NewGuid().ToString("N"), name = "自定义供应商 " + (draft.providers.Count - 2) };
                draft.providers.Add(profile); loading = true; provider.Items.Add(profile); provider.SelectedIndex = provider.Items.Count - 1; loading = false; current = profile; LoadFields(); name.Focus();
            } catch (Exception ex) { ShowError(ex); }
        }
        async Task LoadModels() {
            CancellationTokenSource cancellation = null;
            try {
                if (busy) return;
                CaptureFields(false); cancellation = new CancellationTokenSource(); requestCancellation = cancellation; SetBusy(true, "正在加载模型…");
                string selected = model.Text; model.Items.Clear(); model.Text = selected;
                var response = await ai.RequestForProvider("models", current, cancellationToken: cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                foreach (var item in (System.Collections.IEnumerable)response["result"]) if (item != null) model.Items.Add(item.ToString());
                model.Text = selected.Length > 0 ? selected : model.Items.Count > 0 ? model.Items[0].ToString() : "";
                SetBusy(false, model.Items.Count > 0 ? "已加载 " + model.Items.Count + " 个模型，也可手动输入其他模型名。" : "未返回模型列表，请手动填写模型名称。");
            } catch (OperationCanceledException) { SetBusy(false, "已停止加载，未应用更改。"); }
            catch (Exception ex) { SetBusy(false, ""); ShowError(ex); }
            finally { if (cancellation != null) FinishRequest(cancellation); }
        }
        async Task TestConnection() {
            CancellationTokenSource cancellation = null;
            try {
                if (busy) return;
                CaptureFields(true); cancellation = new CancellationTokenSource(); requestCancellation = cancellation; SetBusy(true, "正在使用所选模型测试连接…");
                await ai.RequestForProvider("test", current, cancellationToken: cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                SetBusy(false, "连接成功，已收到模型回复。点击保存并使用后应用配置。");
            } catch (OperationCanceledException) { SetBusy(false, "已停止测试，未应用更改。"); }
            catch (Exception ex) { SetBusy(false, ""); ShowError(ex); }
            finally { if (cancellation != null) FinishRequest(cancellation); }
        }
    }
    public static class ProviderTests {
        public static List<string> Run() {
            var passed = new List<string>();
            Action<bool, string> check = (ok, name) => { if (!ok) throw new Exception("FAIL: " + name); passed.Add(name); };
            string dir = Path.Combine(Path.GetTempPath(), "prompt-providers-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            try {
                var store = new ProviderStore(dir, new Settings()); var draft = store.Copy();
                check(store.Current.model == "gpt-6.1-sol" && store.Current.reasoningEffort == "medium", "首次配置沿用 Codex 中档");
                var profile = draft.providers.First(p => p.id == "compatible"); profile.baseUrl = "https://example.com/v1/"; profile.model = "test-model"; profile.SetApiKey("synthetic-test-credential");
                draft.selectedId = profile.id; store.Save(draft);
                check(!File.ReadAllText(Path.Combine(dir, "providers.json")).Contains("synthetic-test-credential"), "供应商配置不保存明文密钥");
                var reopened = new ProviderStore(dir, new Settings());
                check(reopened.Current.GetApiKey() == "synthetic-test-credential" && reopened.Current.model == "test-model", "重启恢复供应商模型并解密密钥");
                var changed = reopened.Copy().providers.First(p => p.id == "compatible"); changed.baseUrl = "https://other.example/v1";
                bool rejected = false; try { changed.GetApiKey(); } catch { rejected = true; } check(rejected, "密钥不能静默转发到修改后的地址");
                var canceled = reopened.Copy(); canceled.selectedId = "codex";
                check(reopened.Current.id == "compatible", "未保存的选择不影响当前请求");
                profile = reopened.Copy().providers.First(p => p.id == "compatible"); profile.SetApiKey(""); check(profile.GetApiKey() == "", "密钥可清除");
                check(ProviderProfile.NormalizeUrl("http://127.0.0.1:11434/v1/chat/completions") == "http://127.0.0.1:11434/v1", "本机接口与完整调用地址可规范化");
                rejected = false; try { ProviderProfile.NormalizeUrl("http://example.com/v1"); } catch { rejected = true; } check(rejected, "拒绝远程明文 HTTP 密钥传输");
                var isolated = reopened.Copy(); isolated.selectedId = "codex";
                var broken = isolated.providers.First(p => p.id == "compatible"); broken.encryptedApiKey = "broken-ciphertext";
                isolated.providers.First(p => p.id == "openai").baseUrl = "not-a-url";
                reopened.Save(isolated);
                var isolatedReload = new ProviderStore(dir, new Settings());
                check(isolatedReload.Current.kind == "codex", "未选供应商的坏密钥和地址不阻止保存 Codex");
                check(isolatedReload.Copy().providers.First(p => p.id == "compatible").encryptedApiKey == "broken-ciphertext" && isolatedReload.Copy().providers.First(p => p.id == "openai").baseUrl == "not-a-url", "未选中的异常配置原样保留");
                var invalidSelection = isolatedReload.Copy(); invalidSelection.selectedId = "compatible";
                string beforeInvalidSave = File.ReadAllText(Path.Combine(dir, "providers.json"));
                rejected = false; try { isolatedReload.Save(invalidSelection); } catch { rejected = true; }
                check(rejected && File.ReadAllText(Path.Combine(dir, "providers.json")) == beforeInvalidSave, "选中坏密钥时明确失败且不改已保存选择");
                const string damaged = "{\"providers\":[";
                File.WriteAllText(Path.Combine(dir, "providers.json"), damaged);
                var recovered = new ProviderStore(dir, new Settings());
                check(recovered.Current.kind == "codex" && !String.IsNullOrEmpty(recovered.LoadWarning), "损坏 JSON 可恢复 Codex 并继续启动");
                check(File.ReadAllText(Path.Combine(dir, "providers.json")) == damaged && Directory.GetFiles(dir, "providers.recovery-*.json").Length == 0, "只读取损坏配置不改原文件或自动写备份");
                var unsavedRecovery = recovered.Copy(); unsavedRecovery.providers[0].model = "unapplied-model";
                check(recovered.Current.model != "unapplied-model" && File.ReadAllText(Path.Combine(dir, "providers.json")) == damaged, "未保存草稿与取消操作不应用恢复更改");
                recovered.Save(recovered.Copy());
                check(File.Exists(recovered.RecoveryBackupPath) && File.ReadAllText(recovered.RecoveryBackupPath) == damaged && String.IsNullOrEmpty(recovered.LoadWarning), "明确保存恢复配置前原样备份损坏文件");
                File.WriteAllText(Path.Combine(dir, "providers.json"), "{\"selectedId\":\"good\",\"providers\":[{\"id\":\"good\",\"name\":\"可用配置\",\"kind\":\"openai\",\"baseUrl\":\"https://example.com/v1\",\"model\":\"kept-model\",\"encryptedApiKey\":\"kept-cipher\"},{\"id\":\"bad\",\"name\":null,\"kind\":\"openai\"}]}");
                var partial = new ProviderStore(dir, new Settings());
                check(partial.Current.id == "good" && partial.Current.model == "kept-model" && partial.Current.encryptedApiKey == "kept-cipher" && !String.IsNullOrEmpty(partial.LoadWarning), "单条结构异常隔离后保留其他供应商的模型与加密密钥");
                partial.Save(new ProviderDocument { selectedId = "codex", providers = partial.Copy().providers });
                check(File.Exists(partial.RecoveryBackupPath) && File.ReadAllText(partial.RecoveryBackupPath).Contains("\"id\":\"bad\""), "过滤异常条目后保存仍保留完整原文件备份");
            } finally { foreach (var testFile in Directory.GetFiles(dir)) File.Delete(testFile); Directory.Delete(dir); }
            return passed;
        }
    }
}
