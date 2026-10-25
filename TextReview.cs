using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace PromptQuick {
    public sealed class TextChanges {
        public List<int[]> Removed = new List<int[]>();
        public List<int[]> Added = new List<int[]>();
    }
    public static class TextReview {
        static readonly Regex Tokens = new Regex(@"\r\n|\s+|[A-Za-z0-9_]+(?:[./:_-][A-Za-z0-9_]+)*|[\uD800-\uDBFF][\uDC00-\uDFFF]|.", RegexOptions.Compiled);
        public static TextChanges Compare(string before, string after) {
            var changes = new TextChanges();
            if (before == after) return changes;
            var a = Tokens.Matches(before).Cast<Match>().ToArray();
            var b = Tokens.Matches(after).Cast<Match>().ToArray();
            if ((long)a.Length * b.Length > 1000000) {
                int first = 0; while (first < before.Length && first < after.Length && before[first] == after[first]) first++;
                if (first > 0 && ((first < before.Length && Char.IsLowSurrogate(before[first])) || (first < after.Length && Char.IsLowSurrogate(after[first])))) first--;
                int oldEnd = before.Length, newEnd = after.Length;
                while (oldEnd > first && newEnd > first && before[oldEnd - 1] == after[newEnd - 1]) { oldEnd--; newEnd--; }
                if (oldEnd > 0 && oldEnd < before.Length && Char.IsLowSurrogate(before[oldEnd])) oldEnd++;
                if (newEnd > 0 && newEnd < after.Length && Char.IsLowSurrogate(after[newEnd])) newEnd++;
                if (oldEnd > first) changes.Removed.Add(new[] { first, oldEnd - first });
                if (newEnd > first) changes.Added.Add(new[] { first, newEnd - first });
                return changes;
            }
            var lengths = new int[a.Length + 1, b.Length + 1];
            for (int i = a.Length - 1; i >= 0; i--) for (int j = b.Length - 1; j >= 0; j--)
                lengths[i, j] = a[i].Value == b[j].Value ? lengths[i + 1, j + 1] + 1 : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            int x = 0, y = 0;
            while (x < a.Length || y < b.Length) {
                if (x < a.Length && y < b.Length && a[x].Value == b[y].Value) { x++; y++; }
                else if (x < a.Length && (y >= b.Length || lengths[x + 1, y] >= lengths[x, y + 1])) { changes.Removed.Add(new[] { a[x].Index, a[x].Length }); x++; }
                else { changes.Added.Add(new[] { b[y].Index, b[y].Length }); y++; }
            }
            return changes;
        }
        public static void Reset(RichTextBox box) {
            int start = box.SelectionStart, length = box.SelectionLength;
            box.SelectAll(); box.SelectionBackColor = Color.White; box.Select(Math.Min(start, box.TextLength), Math.Min(length, Math.Max(0, box.TextLength - start)));
        }
        public static void Highlight(RichTextBox source, RichTextBox result) {
            Reset(source); Reset(result);
            var changes = Compare(source.Text, result.Text);
            Paint(source, changes.Removed, Color.FromArgb(255, 224, 224));
            Paint(result, changes.Added, Color.FromArgb(219, 244, 226));
        }
        static void Paint(RichTextBox box, List<int[]> spans, Color color) {
            int start = box.SelectionStart, length = box.SelectionLength;
            foreach (var span in spans) { box.Select(span[0], span[1]); box.SelectionBackColor = color; }
            box.Select(Math.Min(start, box.TextLength), Math.Min(length, Math.Max(0, box.TextLength - start)));
        }
        public static List<string> Tests() {
            var passed = new List<string>();
            Action<bool, string> check = (ok, name) => { if (!ok) throw new Exception("FAIL: " + name); passed.Add(name); };
            check(Compare("原文", "原文").Added.Count == 0 && Compare("原文", "原文").Removed.Count == 0, "相同文本不标记差异");
            var result = Compare("不要加热", "允许加热");
            check(result.Removed.Count > 0 && result.Added.Count > 0, "改写前后的删除和新增均可见");
            result = Compare("保持0.5 mM，4 °C", "保持4 mM，0.5 °C");
            check(result.Added.Count > 0 && result.Removed.Count > 0, "数值互换可显示差异");
            result = Compare("", "新内容");
            check(result.Added.Count > 0 && result.Removed.Count == 0, "空原文对比不越界");
            result = Compare(new string('甲', 5000), new string('乙', 5000));
            check(result.Added.Count == 1 && result.Removed.Count == 1, "长文本差异使用有界计算");
            return passed;
        }
    }
}
