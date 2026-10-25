using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace PromptQuick {
    public sealed class Hotkey {
        public uint Modifiers, Key;
        public string Signature { get { return Modifiers + ":" + Key; } }
        static readonly Dictionary<string, Keys> Special = new Dictionary<string, Keys>(StringComparer.OrdinalIgnoreCase) {
            { "Space", Keys.Space }, { "Enter", Keys.Enter }, { "Tab", Keys.Tab }, { "Esc", Keys.Escape },
            { "Home", Keys.Home }, { "End", Keys.End }, { "Insert", Keys.Insert }, { "Delete", Keys.Delete },
            { "PageUp", Keys.PageUp }, { "PageDown", Keys.PageDown }, { "Left", Keys.Left }, { "Right", Keys.Right },
            { "Up", Keys.Up }, { "Down", Keys.Down }, { "Backspace", Keys.Back }
        };
        public static Hotkey Parse(string value) {
            uint mods = 0x4000, key = 0;
            foreach (string raw in (value ?? "").Split('+')) {
                string part = raw.Trim().ToUpperInvariant();
                if (part == "CTRL" || part == "CONTROL") mods |= 2;
                else if (part == "ALT") mods |= 1;
                else if (part == "SHIFT") mods |= 4;
                else {
                    uint next = 0; Keys special;
                    if (part.Length == 1 && ((part[0] >= 'A' && part[0] <= 'Z') || (part[0] >= '0' && part[0] <= '9'))) next = part[0];
                    else if (Regex.IsMatch(part, "^F([1-9]|1[0-9]|2[0-4])$")) next = (uint)Keys.F1 + UInt32.Parse(part.Substring(1)) - 1;
                    else if (Special.TryGetValue(part, out special)) next = (uint)special;
                    if (next == 0 || key != 0) throw new Exception("无效快捷键：" + value + "。请点击输入框后重新按键。");
                    key = next;
                }
            }
            bool function = key >= (uint)Keys.F1 && key <= (uint)Keys.F24;
            if (key == 0 || (!function && (mods & 3) == 0)) throw new Exception("字母、数字和普通按键需要搭配 Ctrl 或 Alt；F1–F24 可以单独使用。");
            return new Hotkey { Modifiers = mods, Key = key };
        }
        public override string ToString() {
            string text = ((Modifiers & 2) != 0 ? "Ctrl+" : "") + ((Modifiers & 1) != 0 ? "Alt+" : "") + ((Modifiers & 4) != 0 ? "Shift+" : "");
            if (Key >= (uint)Keys.F1 && Key <= (uint)Keys.F24) return text + "F" + (Key - (uint)Keys.F1 + 1);
            foreach (var pair in Special) if (Key == (uint)pair.Value) return text + pair.Key;
            return text + (char)Key;
        }
        public static string FromKeys(Keys data) {
            string mods = ((data & Keys.Control) != 0 ? "Ctrl+" : "") + ((data & Keys.Alt) != 0 ? "Alt+" : "") + ((data & Keys.Shift) != 0 ? "Shift+" : "");
            uint code = (uint)(data & Keys.KeyCode);
            return Parse(mods + new Hotkey { Key = code }.ToString()).ToString();
        }
    }
    // Stage only new combinations. Existing combinations remain registered until
    // all new registrations and the configuration write succeed.
    public sealed class HotkeyRegistry {
        sealed class Binding { public int Id, Action; public Hotkey Key; }
        Dictionary<string, Binding> current = new Dictionary<string, Binding>();
        readonly Func<int, Hotkey, bool> register; readonly Action<int> unregister;
        int nextId = 100;
        public HotkeyRegistry(IntPtr handle) : this((id, key) => Native.RegisterHotKey(handle, id, key.Modifiers, key.Key), id => Native.UnregisterHotKey(handle, id)) { }
        public HotkeyRegistry(Func<int, Hotkey, bool> register, Action<int> unregister) { this.register = register; this.unregister = unregister; }
        public string[] Apply(string[] values, Action<string[]> persist = null) {
            var keys = values.Select(Hotkey.Parse).ToArray();
            if (keys.Select(k => k.Signature).Distinct().Count() != keys.Length) throw new Exception("三个功能不能使用相同的快捷键，请修改重复项。");
            var added = new List<Binding>();
            var replacement = new Dictionary<string, Binding>();
            try {
                for (int i = 0; i < keys.Length; i++) {
                    Binding binding;
                    if (!current.TryGetValue(keys[i].Signature, out binding)) {
                        binding = new Binding { Id = ++nextId, Key = keys[i] };
                        if (!register(binding.Id, binding.Key)) throw new Exception("快捷键 " + keys[i] + " 已被系统或其他程序占用。原设置仍然有效。");
                        added.Add(binding);
                    }
                    replacement.Add(keys[i].Signature, binding);
                }
                var canonical = keys.Select(k => k.ToString()).ToArray();
                if (persist != null) persist(canonical);
                for (int i = 0; i < keys.Length; i++) replacement[keys[i].Signature].Action = i + 1;
                foreach (var pair in current) if (!replacement.ContainsKey(pair.Key)) unregister(pair.Value.Id);
                current = replacement;
                return canonical;
            } catch { foreach (var binding in added) unregister(binding.Id); throw; }
        }
        public bool TryAction(int id, out int action, out string shortcut) {
            foreach (var binding in current.Values) if (binding.Id == id) { action = binding.Action; shortcut = binding.Key.ToString(); return true; }
            action = 0; shortcut = null; return false;
        }
        public void Clear() { foreach (var binding in current.Values) unregister(binding.Id); current.Clear(); }
    }
    public static class ShortcutStore {
        public static void Save(string dir, Settings settings, string[] values) {
            string file = Path.Combine(dir, "config.json"), temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var data = File.Exists(file) ? Json.Decode(File.ReadAllText(file, Encoding.UTF8)) : Json.Decode(Json.Encode(settings));
            data["localHotkey"] = values[0]; data["aiHotkey"] = values[1]; data["restoreHotkey"] = values[2];
            try {
                File.WriteAllText(temp, Json.Encode(data), new UTF8Encoding(false));
                if (File.Exists(file)) File.Replace(temp, file, null); else File.Move(temp, file);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
    public sealed class HotkeyBox : TextBox {
        public event Action<string> InvalidShortcut;
        public HotkeyBox() { ReadOnly = true; BackColor = Color.White; ShortcutsEnabled = false; }
        public void Record(string shortcut) { Text = shortcut; SelectionStart = Text.Length; }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData) {
            Keys code = keyData & Keys.KeyCode;
            if (code == Keys.Tab && (keyData & (Keys.Control | Keys.Alt)) == 0) return base.ProcessCmdKey(ref msg, keyData);
            if (keyData == Keys.Escape) return base.ProcessCmdKey(ref msg, keyData);
            if (code == Keys.ControlKey || code == Keys.ShiftKey || code == Keys.Menu || code == Keys.LWin || code == Keys.RWin) return true;
            if (keyData == Keys.Back || keyData == Keys.Delete) { Text = ""; return true; }
            try { Record(Hotkey.FromKeys(keyData)); }
            catch (Exception e) { if (InvalidShortcut != null) InvalidShortcut(e.Message); }
            return true;
        }
    }
    public sealed class ShortcutDialog : Form {
        readonly HotkeyBox[] inputs = new HotkeyBox[3]; readonly Label error;
        public ShortcutDialog(Settings settings, Action<string[]> apply, string initialError = null) {
            Text = "快捷键设置"; Name = "ShortcutDialog"; Font = new Font("Microsoft YaHei UI", 10);
            ClientSize = new Size(590, 355); FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 2, RowCount = 6 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            for (int i = 0; i < 3; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            var hint = new Label { Text = "点击输入框，直接按下组合键；设置期间暂停全局触发。\n支持 Ctrl、Alt、Shift、字母、数字和 F1–F24；保存后立即生效。", Dock = DockStyle.Fill, AutoSize = true };
            layout.Controls.Add(hint, 0, 0); layout.SetColumnSpan(hint, 2);
            string[] labels = { "本地整理", "AI 重写", "恢复原文" };
            string[] values = { settings.localHotkey, settings.aiHotkey, settings.restoreHotkey };
            for (int i = 0; i < 3; i++) {
                inputs[i] = new HotkeyBox { Text = values[i], Dock = DockStyle.Top, Name = new[] { "localHotkey", "aiHotkey", "restoreHotkey" }[i], AccessibleName = labels[i] + "快捷键", TabIndex = i };
                inputs[i].InvalidShortcut += message => { error.Text = message; };
                layout.Controls.Add(new Label { Text = labels[i], AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, 0, i + 1); layout.Controls.Add(inputs[i], 1, i + 1);
            }
            error = new Label { Text = initialError ?? "修改后点击保存；取消会保留原来的设置。", ForeColor = Color.FromArgb(170, 45, 45), Dock = DockStyle.Fill, AutoSize = true };
            layout.Controls.Add(error, 0, 4); layout.SetColumnSpan(error, 2);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            var save = new Button { Text = "保存并生效", AutoSize = true, Name = "saveShortcuts" };
            var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
            var defaults = new Button { Text = "恢复默认", AutoSize = true };
            save.Click += (s, e) => { try { apply(inputs.Select(box => box.Text).ToArray()); DialogResult = DialogResult.OK; Close(); } catch (Exception ex) { error.Text = ex.Message; } };
            defaults.Click += (s, e) => { var d = new Settings(); string[] keys = { d.localHotkey, d.aiHotkey, d.restoreHotkey }; for (int i = 0; i < 3; i++) inputs[i].Record(keys[i]); error.Text = "默认键位已填入，点击保存后生效。"; };
            buttons.Controls.AddRange(new Control[] { save, cancel, defaults }); layout.Controls.Add(buttons, 0, 5); layout.SetColumnSpan(buttons, 2);
            CancelButton = cancel; Controls.Add(layout);
        }
        public void RecordRegisteredKey(string shortcut) { foreach (var box in inputs) if (box.Focused) { box.Record(shortcut); break; } }
    }
    public static class HotkeyTests {
        public static List<string> Run() {
            var passed = new List<string>();
            Action<bool, string> check = (ok, name) => { if (!ok) throw new Exception("FAIL: " + name); passed.Add(name); };
            check(Hotkey.Parse("shift+ctrl+p").ToString() == "Ctrl+Shift+P", "支持 Ctrl+Shift 并规范化键位");
            check(Hotkey.Parse("F8").Key == (uint)Keys.F8 && Hotkey.Parse("Alt+F12").ToString() == "Alt+F12", "支持功能键");
            check(Hotkey.FromKeys(Keys.Control | Keys.Shift | Keys.D7) == "Ctrl+Shift+7", "录入数字键");
            var registered = new Dictionary<int, string>(); string blocked = null;
            var registry = new HotkeyRegistry((id, key) => { if (key.ToString() == blocked || registered.Values.Contains(key.Signature)) return false; registered.Add(id, key.Signature); return true; }, id => registered.Remove(id));
            string[] original = { "Ctrl+Alt+P", "Ctrl+Alt+Shift+P", "Ctrl+Alt+O" };
            registry.Apply(original); int[] oldIds = registered.Keys.OrderBy(i => i).ToArray();
            bool failed = false; blocked = "Ctrl+Shift+R";
            try { registry.Apply(new[] { "Ctrl+Shift+Q", "Ctrl+Shift+R", "Ctrl+Shift+S" }); } catch { failed = true; }
            check(failed && registered.Keys.OrderBy(i => i).SequenceEqual(oldIds), "冲突时撤销新注册且旧键位保持有效");
            failed = false;
            try { registry.Apply(new[] { "Ctrl+Shift+Q", "Ctrl+Shift+W", "Ctrl+Shift+E" }, keys => { throw new IOException("save failed"); }); } catch { failed = true; }
            check(failed && registered.Keys.OrderBy(i => i).SequenceEqual(oldIds), "保存失败时保留旧键位");
            registry.Apply(new[] { original[1], original[0], original[2] }); int action; string shortcut;
            check(registry.TryAction(oldIds[0], out action, out shortcut) && action == 2, "交换已有键位无需释放且动作正确");
            failed = false; try { registry.Apply(new[] { "Ctrl+Shift+P", "shift+ctrl+p", "F9" }); } catch { failed = true; }
            check(failed && registered.Count == 3, "重复键位不能保存");
            registry.Apply(new[] { "F8", "Ctrl+Shift+P", "Alt+O" });
            check(registered.Count == 3 && !registered.ContainsKey(oldIds[0]), "新键位生效后释放旧键位");
            registry.Clear(); check(registered.Count == 0, "暂停和退出释放全部快捷键");
            string dir = Path.Combine(Path.GetTempPath(), "prompt-shortcuts-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "config.json");
            try {
                File.WriteAllText(file, "{\"model\":\"keep-model\",\"aiTimeoutSeconds\":35,\"customField\":\"keep-value\"}");
                ShortcutStore.Save(dir, new Settings(), new[] { "F8", "Ctrl+Shift+P", "Alt+O" });
                var data = Json.Decode(File.ReadAllText(file));
                check(Json.Str(data, "customField") == "keep-value" && Json.Str(data, "model") == "keep-model" && Settings.Load(dir).localHotkey == "F8", "设置落盘并保留模型和其他配置");
            } finally { File.Delete(file); Directory.Delete(dir); }
            return passed;
        }
    }
}
