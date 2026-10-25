using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace PromptQuick {
    public static class ExternalEditors {
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")] static extern IntPtr ReadMessage(IntPtr window, uint message, IntPtr count, StringBuilder text, uint flags, uint timeout, out UIntPtr result);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")] static extern IntPtr WriteMessage(IntPtr window, uint message, IntPtr count, string text, uint flags, uint timeout, out UIntPtr result);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")] static extern IntPtr QueryMessage(IntPtr window, uint message, IntPtr w, IntPtr l, uint flags, uint timeout, out UIntPtr result);
        public static bool PlainClass(string cls) { return Regex.IsMatch(cls ?? "", @"^(Edit|WindowsForms10\.EDIT\..*)$", RegexOptions.IgnoreCase); }
        public static bool RichClass(string cls) { return (cls ?? "").IndexOf("RichEdit", StringComparison.OrdinalIgnoreCase) >= 0; }
        static bool NativeEdit(AutomationElement editor) { return PlainClass(editor.Current.ClassName) || RichClass(editor.Current.ClassName); }
        static int Style(AutomationElement editor) { return editor.Current.NativeWindowHandle == 0 ? 0 : GetWindowLong(new IntPtr(editor.Current.NativeWindowHandle), -16); }
        public static bool Protected(bool uiaPassword, string cls, int style) { return uiaPassword || ((PlainClass(cls) || RichClass(cls)) && (style & 0x20) != 0); }
        public static void EnsureSafe(AutomationElement editor) {
            if (Protected(editor.Current.IsPassword, editor.Current.ClassName, Style(editor))) throw new Exception("密码输入框不处理。");
            if (!editor.Current.IsEnabled || (NativeEdit(editor) && (Style(editor) & 0x800) != 0)) throw new Exception("当前输入框不可编辑。");
        }
        public static bool Supported(AutomationElement editor) {
            EnsureSafe(editor);
            if (NativeEdit(editor)) return editor.Current.NativeWindowHandle != 0;
            object pattern;
            return (editor.Current.ControlType == ControlType.Edit || editor.Current.ControlType == ControlType.Document || RichClass(editor.Current.ClassName))
                && editor.TryGetCurrentPattern(ValuePattern.Pattern, out pattern) && !((ValuePattern)pattern).Current.IsReadOnly;
        }
        static bool IsNotepad(AutomationElement editor) {
            try {
                string path = Process.GetProcessById(editor.Current.ProcessId).MainModule.FileName;
                return Regex.IsMatch(path, @"\\(Windows\\(?:System32|SysWOW64)|WindowsApps\\Microsoft\.WindowsNotepad_[^\\]+\\Notepad)\\notepad\.exe$", RegexOptions.IgnoreCase);
            } catch { return false; }
        }
        public static bool RequiresPreview(AutomationElement editor) {
            if (PlainClass(editor.Current.ClassName)) return false;
            if (RichClass(editor.Current.ClassName)) return !IsNotepad(editor);
            if (editor.Current.ControlType == ControlType.Document) return true;
            return false;
        }
        public static string ReadPlain(AutomationElement editor) {
            EnsureSafe(editor); var window = new IntPtr(editor.Current.NativeWindowHandle); UIntPtr result;
            if (QueryMessage(window, 0xE, IntPtr.Zero, IntPtr.Zero, 2, 300, out result) == IntPtr.Zero) throw new Exception("当前输入框无法读取。");
            int length = checked((int)result.ToUInt64()); if (length > 20000) throw new Exception("输入框超过 20000 字符，请选用较短文本。");
            var text = new StringBuilder(length + 1);
            if (ReadMessage(window, 0xD, new IntPtr(text.Capacity), text, 2, 300, out result) == IntPtr.Zero) throw new Exception("当前输入框无法读取。");
            return text.ToString();
        }
        public static bool WritePlain(AutomationElement editor, string value) {
            EnsureSafe(editor); UIntPtr result;
            return WriteMessage(new IntPtr(editor.Current.NativeWindowHandle), 0xC, IntPtr.Zero, value, 2, 300, out result) != IntPtr.Zero && result.ToUInt64() != 0;
        }
        public static bool TryPrefix(AutomationElement editor, string value, out string beforeCaret) {
            beforeCaret = null; if (!NativeEdit(editor) || editor.Current.NativeWindowHandle == 0 || value.Length > 20000) return false;
            UIntPtr result;
            if (QueryMessage(new IntPtr(editor.Current.NativeWindowHandle), 0xB0, IntPtr.Zero, IntPtr.Zero, 2, 300, out result) == IntPtr.Zero) return false;
            ulong selection = result.ToUInt64(); int start = (int)(selection & 65535), end = (int)((selection >> 16) & 65535);
            if (start != end || start > value.Length) return false;
            beforeCaret = value.Substring(0, start); return true;
        }
    }
}
