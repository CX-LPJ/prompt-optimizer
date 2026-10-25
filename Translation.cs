using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Forms;

namespace PromptQuick {
    public sealed class DoubleSpaceDetector {
        bool held, armed; uint last; IntPtr window;
        public void Reset() { armed = false; }
        public bool Feed(uint key, bool down, uint time, IntPtr foreground, bool modifiers, bool composing, int interval) {
            if (key != 0x20) { if (down) Reset(); return false; }
            if (!down) { held = false; return false; }
            if (held) return false;
            held = true;
            if (modifiers || composing || foreground == IntPtr.Zero) { Reset(); return false; }
            if (armed && foreground == window && unchecked(time - last) <= interval) { Reset(); return true; }
            armed = true; last = time; window = foreground; return false;
        }
    }
    public sealed class DoubleSpaceHook : IDisposable {
        delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);
        [StructLayout(LayoutKind.Sequential)] struct KeyboardData { public uint Key, Scan, Flags, Time; public UIntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] struct GuiInfo { public uint Size, Flags; public IntPtr Active, Focus, Capture, Menu, Move, Caret; public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookCallback callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr handle);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr handle, int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint message, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("imm32.dll")] static extern IntPtr ImmGetContext(IntPtr window);
        [DllImport("imm32.dll")] static extern bool ImmReleaseContext(IntPtr window, IntPtr context);
        [DllImport("imm32.dll", EntryPoint = "ImmGetCompositionStringW")] static extern int ImmGetCompositionString(IntPtr context, uint index, IntPtr buffer, uint length);
        readonly DoubleSpaceDetector detector = new DoubleSpaceDetector();
        readonly Thread thread; readonly IntPtr recipient; readonly HookCallback callback;
        readonly ManualResetEvent started = new ManualResetEvent(false);
        IntPtr hook; uint threadId; int generation, reset; Exception startupError;
        volatile bool enabled = true; public int Interval;
        public bool Enabled { get { return enabled; } set { enabled = value; Interlocked.Exchange(ref reset, 1); } }
        public int Generation { get { return Volatile.Read(ref generation); } }
        public DoubleSpaceHook(IntPtr recipient, int interval) {
            this.recipient = recipient; Interval = interval; callback = OnKey;
            thread = new Thread(Run) { IsBackground = true, Name = "PromptDoubleSpace" }; thread.Start();
            if (!started.WaitOne(3000)) throw new Exception("双击空格监听启动超时。");
            if (startupError != null) throw startupError;
        }
        void Run() {
            try {
                threadId = GetCurrentThreadId(); hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
                if (hook == IntPtr.Zero) throw new Exception("无法启用双击空格，请重新启动工具。");
                // Create the message queue before publishing readiness.
                using (var queueWindow = new NativeWindowHost()) { started.Set(); Application.Run(); }
            } catch (Exception e) { startupError = e; started.Set(); }
            finally { if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook); }
        }
        sealed class NativeWindowHost : NativeWindow, IDisposable {
            public NativeWindowHost() { CreateHandle(new CreateParams { Caption = "PromptDoubleSpaceQueue" }); }
            public void Dispose() { DestroyHandle(); }
        }
        static bool Modifiers() {
            foreach (int key in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }) if ((GetAsyncKeyState(key) & 0x8000) != 0) return true;
            return false;
        }
        public static bool IsComposing(IntPtr foreground) {
            uint pid; uint tid = Native.GetWindowThreadProcessId(foreground, out pid);
            var info = new GuiInfo { Size = (uint)Marshal.SizeOf(typeof(GuiInfo)) };
            if (!GetGUIThreadInfo(tid, ref info)) return true;
            IntPtr window = info.Focus != IntPtr.Zero ? info.Focus : foreground;
            IntPtr context = ImmGetContext(window); if (context == IntPtr.Zero) return false;
            try { return ImmGetCompositionString(context, 8, IntPtr.Zero, 0) > 0; }
            finally { ImmReleaseContext(window, context); }
        }
        IntPtr OnKey(int code, IntPtr message, IntPtr pointer) {
            try {
                if (code >= 0) {
                    uint msg = unchecked((uint)message.ToInt64()); bool down = msg == 0x100 || msg == 0x104, up = msg == 0x101 || msg == 0x105;
                    if (down || up) {
                        var data = (KeyboardData)Marshal.PtrToStructure(pointer, typeof(KeyboardData));
                        if (Interlocked.Exchange(ref reset, 0) != 0) detector.Reset();
                        int version = down ? Interlocked.Increment(ref generation) : Generation;
                        var foreground = Native.GetForegroundWindow();
                        if (detector.Feed(data.Key, down, data.Time, foreground, !enabled || (data.Key == 0x20 && down && Modifiers()), data.Key == 0x20 && down && IsComposing(foreground), Interval) && enabled)
                            Native.PostMessage(recipient, 0x8045, new IntPtr(version), foreground);
                    }
                }
            } catch { detector.Reset(); }
            // Observe only: ordinary spaces and IME input always reach the application.
            return CallNextHookEx(hook, code, message, pointer);
        }
        public void Dispose() { enabled = false; if (threadId != 0) PostThreadMessage(threadId, 0x12, IntPtr.Zero, IntPtr.Zero); thread.Join(1500); started.Dispose(); }
    }
    public static class TranslationText {
        static string Normalize(string value) { return value.Replace("\r\n", "\n"); }
        public static bool RemoveTrigger(string value, string fullText, string beforeCaret, out string source) {
            source = null; string prefix = Normalize(beforeCaret), normalized = Normalize(value);
            if (!prefix.EndsWith("  ", StringComparison.Ordinal) || !normalized.StartsWith(prefix, StringComparison.Ordinal)
                || Normalize(fullText).TrimEnd('\n') != normalized.TrimEnd('\n')) return false;
            int index = 0, count = 0;
            while (index < value.Length && count < prefix.Length) { if (value[index] == '\r' && index + 1 < value.Length && value[index + 1] == '\n') index++; index++; count++; }
            if (count != prefix.Length || index < 2 || value.Substring(index - 2, 2) != "  ") return false;
            source = value.Remove(index - 2, 2); return !String.IsNullOrWhiteSpace(source);
        }
        public static bool FromDraft(Draft draft, out string source) {
            source = null; object pattern;
            string prefix;
            if (ExternalEditors.TryPrefix(draft.Editor, draft.Text, out prefix) && RemoveTrigger(draft.Text, draft.Text, prefix, out source)) return true;
            if (!draft.Editor.TryGetCurrentPattern(TextPattern.Pattern, out pattern)) {
                // Some web inputs expose their value without a caret range. Preserve
                // the observed value rather than guessing which spaces to delete.
                source = draft.Text; return !String.IsNullOrWhiteSpace(source);
            }
            var text = (TextPattern)pattern; var selection = text.GetSelection();
            if (selection.Length != 1 || selection[0].GetText(1).Length != 0) return false;
            var before = text.DocumentRange.Clone(); before.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection[0], TextPatternRangeEndpoint.Start);
            return RemoveTrigger(draft.Text, text.DocumentRange.GetText(-1), before.GetText(-1), out source);
        }
        public static void SavePreferences(string dir, Settings settings, bool enabled, string target) {
            string file = Path.Combine(dir, "config.json"), temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var data = File.Exists(file) ? Json.Decode(File.ReadAllText(file, Encoding.UTF8)) : Json.Decode(Json.Encode(settings));
            data["doubleSpaceTranslation"] = enabled; data["translationTarget"] = target; data["doubleSpaceMilliseconds"] = settings.doubleSpaceMilliseconds;
            try { File.WriteAllText(temp, Json.Encode(data), new UTF8Encoding(false)); if (File.Exists(file)) File.Replace(temp, file, null); else File.Move(temp, file); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
    public static class TranslationTests {
        public static List<string> Run() {
            var checks = new List<string>(); Action<bool, string> check = (ok, name) => { if (!ok) throw new Exception("FAIL: " + name); checks.Add(name); };
            var d = new DoubleSpaceDetector(); var window = new IntPtr(1);
            Func<uint, bool, uint, bool, bool, bool> feed = (key, down, time, mods, ime) => d.Feed(key, down, time, window, mods, ime, 350);
            check(!feed(32, true, 100, false, false) && !feed(32, false, 130, false, false) && feed(32, true, 220, false, false), "快速两次空格触发翻译");
            d = new DoubleSpaceDetector(); check(!feed(32, true, 100, false, false) && !feed(32, true, 220, false, false), "长按空格不触发");
            feed(32, false, 230, false, false); check(!feed(32, true, 800, false, false), "慢速两次空格不触发");
            d = new DoubleSpaceDetector(); feed(32, true, 100, false, true); feed(32, false, 130, false, false); check(!feed(32, true, 220, false, false), "输入法组词空格不组成翻译手势");
            d = new DoubleSpaceDetector(); feed(32, true, 100, true, false); feed(32, false, 130, false, false); check(!feed(32, true, 220, false, false), "Ctrl等组合键不组成翻译手势");
            d = new DoubleSpaceDetector(); feed(32, true, 100, false, false); feed(32, false, 130, false, false); feed(65, true, 150, false, false); check(!feed(32, true, 220, false, false), "夹入其他按键取消手势");
            d = new DoubleSpaceDetector(); feed(32, true, 100, false, false); feed(32, false, 130, false, false); check(!d.Feed(32, true, 220, new IntPtr(2), false, false, 350), "切换窗口取消手势");
            string source; check(TranslationText.RemoveTrigger("Hello  world\r\nnext", "Hello  world\nnext", "Hello  ", out source) && source == "Helloworld\r\nnext", "只移除光标前的触发空格并保留换行");
            check(TranslationText.RemoveTrigger("Hello   world", "Hello   world", "Hello   ", out source) && source == "Hello world", "保留触发手势以外的原有空格");
            check(!TranslationText.RemoveTrigger("  \n", "  \n", "  ", out source), "空草稿不翻译");
            check(!TranslationText.RemoveTrigger("Hello world", "Hello world", "Hello ", out source), "单个空格不改变草稿");
            check(ExternalEditors.Protected(true, "web-input", 0), "UIA密码标记拒绝读取");
            check(ExternalEditors.Protected(false, "Edit", 0x20), "原生密码样式拒绝读取");
            check(ExternalEditors.Protected(false, "WindowsForms10.EDIT.app.0.test", 0x20), "WinForms密码样式拒绝读取");
            return checks;
        }
    }
}
