using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace PromptQuick {
    public static class LocalOptimizer {
        const RegexOptions Options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
        static readonly Regex RawDocument = new Regex(@"\A\s*(?:[\{\[\^\\]|<[/!A-Za-z]|(?:def|class|import|from|function|const|let|var|using|public|SELECT|INSERT|UPDATE|DELETE|CREATE|WITH)\b|#!|#include|(?:-\s+)?[A-Za-z_][\w.-]*\s*[:=])", Options);
        static readonly Regex PayloadLabel = new Regex(@"(?:原文|原句|输入文本|待译文本|待翻译内容|待翻译文本|待润色内容|待润色文本|代码片段|源代码|脚本内容|正则表达式|公式)[ \t]*[：:]", Options);
        static readonly Regex PayloadRequest = new Regex(@"(?m)^[ \t]*(?:请|帮我|麻烦|把)[^\r\n：:]{0,100}?(?:翻译|润色|改写|校对|解释|分析|总结|检查|改得更正式)[^\r\n：:]{0,40}[：:]", Options);
        static readonly Regex Fence = new Regex(@"^[ \t]{0,3}(?<mark>\x60{3,}|~{3,})", Options);
        static readonly Regex Literal = new Regex(@"\G(?:!?\[[^\]\r\n]*\]\((?:\\.|[^)\r\n])*\)|(?:https?|ftp|file)://[^\s<>""'“”‘’，。；！？]+|(?:[A-Za-z]:[\\/]|\\\\)[^\r\n""<>|?*，。；！？]*|(?:~?/)[A-Za-z0-9_.-][^\s<>""'，。；！？]*|\b10\.\d{4,9}/[^\s<>""'，。；！？]+)", Options | RegexOptions.IgnoreCase);
        static readonly Regex Polite = new Regex(@"(?m)(?<lead>^[ \t]*(?:(?:[-*+]|\d+[.)、])[ \t]*)?|[。！？，,；;][ \t]*)(?:请你帮我|请帮我|麻烦你帮我|麻烦帮我|麻烦你|麻烦|请你|帮我)(?<verb>查找|检索|整理|总结|翻译|润色|检查|分析|解释|比较|优化|修改|修复|编写|生成|绘制|绘图|找出|找到|找)", Options);
        static readonly Regex DoiRequest = new Regex(@"(?<lead>[，,；;][ \t]*)(?:要|需要|给我)(?<space>[ \t]*)(?<doi>DOI)(?=[，,。；;！？\r\n]|$)", Options | RegexOptions.IgnoreCase);
        // Match the complete delivery clause, not the start of a noun such as
        // 列表项 or a recipient phrase such as 给方案打分. Splitting punctuation
        // and changing 给 must share this same grammatical boundary.
        static readonly Regex WorkflowClause = new Regex(@"(?m)(?<lead>^|[。！？\r\n，,；;])(?<space>[ \t]*)先(?:给|提供)(?<item>列表|方案|计划|建议|预览)(?:(?<confirmation>确认后再)(?<action>(?:下载|修改|实施|执行|保存|上传|删除|发送|发布|继续)[^。！？\r\n，,；;]*))?(?=$|[。！？\r\n，,；;])", Options);

        public static string Optimize(string source) {
            if (String.IsNullOrWhiteSpace(source)) throw new Exception("请先输入需要优化的内容。");
            if (source.Length > 20000) throw new Exception("草稿超过 20000 字符，请缩短后重试。");
            if (RawDocument.IsMatch(source)) return source;
            string prefix;
            do { prefix = "\uE000" + Guid.NewGuid().ToString("N") + ":"; } while (source.Contains(prefix));
            var protectedText = new List<string>();
            Func<string, string> protect = value => { protectedText.Add(value); return prefix + (protectedText.Count - 1) + "\uE001"; };
            int payload = source.Length;
            foreach (Regex marker in new[] { PayloadLabel, PayloadRequest }) {
                var match = marker.Match(source);
                if (match.Success) payload = Math.Min(payload, match.Index + match.Length);
            }
            var masked = new StringBuilder();
            int i = 0;
            while (i < payload) {
                if (i == 0 || source[i - 1] == '\n') {
                    int lineEnd = source.IndexOf('\n', i); if (lineEnd < 0 || lineEnd >= payload) lineEnd = payload;
                    string line = source.Substring(i, lineEnd - i).TrimEnd('\r');
                    var opening = Fence.Match(line);
                    if (opening.Success) {
                        string mark = opening.Groups["mark"].Value;
                        int end = payload, next = lineEnd < payload ? lineEnd + 1 : payload;
                        while (next < payload) {
                            int closeEnd = source.IndexOf('\n', next); if (closeEnd < 0 || closeEnd >= payload) closeEnd = payload;
                            string closeLine = source.Substring(next, closeEnd - next).Trim(' ', '\t', '\r');
                            bool closes = closeLine.Length >= mark.Length;
                            for (int n = 0; closes && n < closeLine.Length; n++) if (closeLine[n] != mark[0]) closes = false;
                            if (closes) { end = closeEnd < payload ? closeEnd + 1 : closeEnd; break; }
                            next = closeEnd < payload ? closeEnd + 1 : payload;
                        }
                        masked.Append(protect(source.Substring(i, end - i))); i = end; continue;
                    }
                    if (line.TrimStart(' ', '\t').StartsWith(">", StringComparison.Ordinal) || line.StartsWith("    ", StringComparison.Ordinal) || line.StartsWith("\t", StringComparison.Ordinal)) {
                        int end = lineEnd < payload ? lineEnd + 1 : lineEnd;
                        masked.Append(protect(source.Substring(i, end - i))); i = end; continue;
                    }
                }
                char c = source[i];
                if (c == '$' || (c == '\\' && i + 1 < payload && (source[i + 1] == '(' || source[i + 1] == '['))) {
                    string start = c == '$' ? (i + 1 < payload && source[i + 1] == '$' ? "$$" : "$") : source.Substring(i, 2);
                    string closeMark = start == @"\(" ? @"\)" : start == @"\[" ? @"\]" : start;
                    int close = source.IndexOf(closeMark, i + start.Length, StringComparison.Ordinal);
                    int end = close < 0 || close >= payload ? payload : close + closeMark.Length;
                    masked.Append(protect(source.Substring(i, end - i))); i = end; continue;
                }
                if (c == (char)96) {
                    int count = 1; while (i + count < payload && source[i + count] == c) count++;
                    string mark = new string(c, count); int close = source.IndexOf(mark, i + count, StringComparison.Ordinal);
                    while (close >= 0 && close < payload && ((close > 0 && source[close - 1] == c) || (close + count < payload && source[close + count] == c))) close = source.IndexOf(mark, close + count, StringComparison.Ordinal);
                    int end = close < 0 || close >= payload ? payload : close + count;
                    masked.Append(protect(source.Substring(i, end - i))); i = end; continue;
                }
                char closeQuote = ClosingQuote(c);
                bool apostrophe = c == '\'' && i > 0 && i + 1 < payload && Char.IsLetterOrDigit(source[i - 1]) && Char.IsLetterOrDigit(source[i + 1]);
                if (closeQuote != '\0' && !apostrophe) {
                    int depth = 1, next = i + 1;
                    for (; next < payload; next++) {
                        if (source[next] == '\\') { next++; continue; }
                        if (c != closeQuote && source[next] == c) depth++;
                        if (source[next] == closeQuote) { depth--; if (depth == 0) { next++; break; } }
                    }
                    int end = Math.Min(next, payload);
                    masked.Append(protect(source.Substring(i, end - i))); i = end; continue;
                }
                var literal = Literal.Match(source, i);
                if (literal.Success && literal.Index == i) {
                    int length = Math.Min(literal.Length, payload - i);
                    masked.Append(protect(source.Substring(i, length))); i += length; continue;
                }
                masked.Append(c); i++;
            }
            if (payload < source.Length) masked.Append(protect(source.Substring(payload)));
            string text = masked.ToString();
            text = Polite.Replace(text, match => match.Groups["lead"].Value + "请" + (match.Groups["verb"].Value == "找" ? "查找" : match.Groups["verb"].Value));
            text = DoiRequest.Replace(text, match => match.Groups["lead"].Value + "附" + match.Groups["space"].Value + match.Groups["doi"].Value);
            text = WorkflowClause.Replace(text, match => {
                string lead = match.Groups["lead"].Value;
                if (lead == "，" || lead == ",") lead = "。";
                string confirmation = match.Groups["confirmation"].Success ? "，" + match.Groups["confirmation"].Value + match.Groups["action"].Value : "";
                return lead + match.Groups["space"].Value + "先提供" + match.Groups["item"].Value + confirmation;
            });
            return Regex.Replace(text, Regex.Escape(prefix) + @"(\d+)\uE001", match => protectedText[Int32.Parse(match.Groups[1].Value)]);
        }
        static char ClosingQuote(char c) {
            switch (c) {
                case '"': return '"'; case '\'': return '\'';
                case '“': return '”'; case '‘': return '’';
                case '「': return '」'; case '『': return '』';
                default: return '\0';
            }
        }
    }
    public static class LocalOptimizerTests {
        public static List<string> Run() {
            var passed = new List<string>();
            Action<bool, string> check = (ok, name) => { if (!ok) throw new Exception("FAIL: " + name); passed.Add(name); };
            var examples = new Dictionary<string, string> {
                { "帮我找5篇MOF文献，要DOI，不要综述，先给列表确认后再下载。", "请查找5篇MOF文献，附DOI，不要综述。先提供列表，确认后再下载。" },
                { "帮我优化这个工具的返回速度，保持现有功能。先给方案，确认后再修改。不要上传 GitHub。", "请优化这个工具的返回速度，保持现有功能。先提供方案，确认后再修改。不要上传 GitHub。" },
                { "请帮我找到问题，不要删除数据。", "请找到问题，不要删除数据。" },
                { "帮我整理文献，不要DOI。", "请整理文献，不要DOI。" },
                { "帮我整理文本，先给列表项加编号。", "请整理文本，先给列表项加编号。" },
                { "帮我检查方案，先给方案打分。", "请检查方案，先给方案打分。" },
                { "先给方案确认后再修改，不要上传。", "先提供方案，确认后再修改，不要上传。" },
                { "先提供列表确认后再下载。", "先提供列表，确认后再下载。" }
            };
            foreach (var example in examples) {
                string result = LocalOptimizer.Optimize(example.Key);
                check(result == example.Value, "本地改写：" + example.Key);
                check(LocalOptimizer.Optimize(result) == result, "重复整理稳定：" + example.Key);
            }
            string tick = new string((char)96, 3);
            string[] unchanged = {
                "不确定这个是否有问题。",
                "不要帮我修改任何代码。",
                "不要先给方案。",
                "先不给方案。",
                "先给方案打分。",
                "先给列表项加编号。",
                "先给计划表加编号。",
                "先给建议者发邮件。",
                "先给方案A打分。",
                "先给列表、方案打分。",
                "先给方案确认后再考虑。",
                "如果需要先给方案打分，就按原标准。",
                "“先给方案确认后再修改。”",
                "请润色：先给列表项加编号，先给方案打分。",
                "“帮我找5篇文献，要DOI，先给列表确认后再下载。”",
                "\"帮我找5篇文献，要DOI\"",
                "「帮我找5篇文献」",
                "> 帮我找5篇文献，要DOI\r\n",
                tick + "python\r\n帮我找5篇文献，要DOI\r\n" + tick + "\r\n",
                tick + "python\r\n帮我找5篇文献，要DOI",
                "~~~text\n帮我找5篇文献，要DOI\n~~~",
                new string((char)96, 1) + "帮我找5篇文献，要DOI" + new string((char)96, 1),
                "{ \"request\": \"帮我找5篇文献，要DOI\" }",
                "prompt: |\n  帮我找5篇文献，要DOI",
                "def task():\n    # 帮我找5篇文献，要DOI",
                "E:\\测试 目录\\文件,帮我找5篇.txt",
                "https://example.com/帮我找5篇,要DOI",
                "请润色：帮我找5篇文献，要DOI，不要综述。",
                "把这段话改得更正式：帮我找5篇文献，要DOI。",
                "请翻译以下原文：帮我找5篇文献，要DOI。",
                "原文：帮我找5篇文献，要DOI。",
                "    帮我找5篇文献，要DOI\n",
                "请检查 $帮我找5篇，要DOI$。",
                @"请检查 \(帮我找5篇，要DOI\)。",
                "请检查 [帮我找5篇，要DOI](https://example.com)。"
            };
            foreach (var source in unchanged) check(LocalOptimizer.Optimize(source) == source, "保留歧义或受保护内容：" + source);
            string mixed = "帮我检查 " + new string((char)96, 1) + "帮我找5篇，要DOI" + new string((char)96, 1) + "，不要加热，0.5 mM，4 °C。";
            string output = LocalOptimizer.Optimize(mixed);
            check(output == mixed.Replace("帮我检查 ", "请检查 "), "混合代码与正文分别处理，保留数字单位和否定");
            check(!output.Contains("执行要求：") && !output.Contains("任务背景") && !output.Contains("结果："), "不附加任务模板和额外要求");
            check(LocalOptimizer.Optimize("1. 帮我找文献\n2. 不要删除数据") == "1. 请查找文献\n2. 不要删除数据", "保留原有清单与要求顺序");
            check(LocalOptimizer.Optimize("“帮我找文献”，帮我整理数据。") == "“帮我找文献”，请整理数据。", "引号内部保留、引号外继续整理");
            return passed;
        }
    }
}
