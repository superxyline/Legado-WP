using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Parser.Html;

namespace legado_wp.Core
{
    /// <summary>
    /// 迷你 XPath 引擎（0.0.12.0）：覆盖书源常用子集，不引第三方 XPath 库。
    /// 支持：//tag / /tag / * / . ｜ 谓词 [n] [last()] [@a='v'] [@a] [contains(@a,'v')]
    ///       [contains(text(),'v')] [text()='v'] [starts-with(@a,'v')] [position()=n]
    /// 末段：/text()（直接文本）、//text()（全部文本）、/@attr（属性值）
    /// 顶层 | 并集；不支持的函数/语法静默返回空（与 legado 宽容语义一致）。
    /// </summary>
    public class AnalyzeByXPath
    {
        private readonly IElement _root;

        public AnalyzeByXPath(object doc)
        {
            if (doc is IElement el) _root = el;
            else _root = new HtmlParser().Parse(doc?.ToString() ?? "").DocumentElement;
        }

        // ---------------- 对外 API ----------------

        public string GetString(string rule)
        {
            var list = GetStringList(rule);
            if (list.Count == 0) return null;
            return list.Count == 1 ? list[0] : string.Join("\n", list);
        }

        public List<string> GetStringList(string rule)
        {
            var result = new List<string>();
            foreach (var branch in SplitUnion(rule ?? ""))
            {
                var steps = ParseBranch(branch);
                if (steps.Count == 0) continue;
                foreach (var item in Eval(steps))
                {
                    if (item is string s) { if (s.Length > 0) result.Add(s); }
                    else
                    {
                        var t = ((IElement)item).TextContent;
                        if (!string.IsNullOrWhiteSpace(t)) result.Add(t);
                    }
                }
            }
            return result;
        }

        public List<IElement> GetElements(string rule)
        {
            var result = new List<IElement>();
            foreach (var branch in SplitUnion(rule ?? ""))
            {
                var steps = ParseBranch(branch);
                if (steps.Count == 0) continue;
                foreach (var item in Eval(steps))
                    if (item is IElement el) result.Add(el);
            }
            return result;
        }

        // ---------------- 求值 ----------------

        private List<object> Eval(List<Step> steps)
        {
            var current = new List<object> { _root };
            for (var si = 0; si < steps.Count; si++)
            {
                var step = steps[si];
                // text() 段视为终点（文本后再取子节点无意义）
                if (step.IsText) return EvalText(current, step);
                if (step.IsAttr) return EvalAttr(current, step);

                var next = new List<object>();
                var seen = new HashSet<IElement>();
                foreach (var node in current.OfType<IElement>())
                {
                    List<IElement> cand;
                    if (step.Self) cand = new List<IElement> { node };
                    else if (step.Descendant)
                    {
                        cand = new List<IElement>();
                        CollectDesc(node, step.Tag, cand);
                    }
                    else
                    {
                        // 首步且为子轴时，根元素自身也算候选（支持 /html/... 绝对路径语义）
                        if (si == 0 && TagMatch(node, step.Tag)) next.Add(node);
                        cand = node.Children
                            .Where(c => TagMatch(c, step.Tag))
                            .ToList();
                    }
                    cand = ApplyPreds(cand, step.Preds);
                    foreach (var c in cand)
                        if (seen.Add(c)) next.Add(c);
                }
                current = next;
                if (current.Count == 0) return current;
            }
            return current;
        }

        private static List<object> EvalText(List<object> current, Step step)
        {
            var texts = new List<string>();
            foreach (var node in current.OfType<IElement>())
            {
                IEnumerable<IText> nodes = step.Descendant
                    ? DescTextNodes(node)
                    : node.ChildNodes.OfType<IText>();
                foreach (var t in nodes)
                {
                    var v = t.Text.Trim();
                    if (v.Length > 0 && !texts.Contains(v)) texts.Add(v);
                }
            }
            return texts.Cast<object>().ToList();
        }

        private static IEnumerable<IText> DescTextNodes(IElement root)
        {
            foreach (var e in AllDescendants(root))
                foreach (var t in e.ChildNodes.OfType<IText>())
                    yield return t;
        }

        private static List<object> EvalAttr(List<object> current, Step step)
        {
            var values = new List<object>();
            var seen = new HashSet<string>();
            foreach (var node in current.OfType<IElement>())
            {
                var v = step.Attr.Length == 0 ? null : node.GetAttribute(step.Attr);
                if (!string.IsNullOrEmpty(v) && seen.Add(v)) values.Add(v);
            }
            return values;
        }

        // ---------------- 谓词 ----------------

        private static readonly Regex PredAttrEq =
            new Regex(@"^@([\w-]+)\s*=\s*['""](.*)['""]$", RegexOptions.Compiled);
        private static readonly Regex PredAttrHas =
            new Regex(@"^@([\w-]+)$", RegexOptions.Compiled);
        private static readonly Regex PredAttrContains =
            new Regex(@"^contains\(@([\w-]+)\s*,\s*['""](.*)['""]\)$", RegexOptions.Compiled);
        private static readonly Regex PredTextEq =
            new Regex(@"^text\(\)\s*=\s*['""](.*)['""]$", RegexOptions.Compiled);
        private static readonly Regex PredTextContains =
            new Regex(@"^contains\(text\(\)\s*,\s*['""](.*)['""]\)$", RegexOptions.Compiled);
        private static readonly Regex PredStartsWith =
            new Regex(@"^starts-with\(@([\w-]+)\s*,\s*['""](.*)['""]\)$", RegexOptions.Compiled);
        private static readonly Regex PredPosition =
            new Regex(@"^position\(\)\s*(<=?|>=?|=)\s*(\d+)$", RegexOptions.Compiled);

        private static List<IElement> ApplyPreds(List<IElement> cand, List<string> preds)
        {
            foreach (var raw in preds)
            {
                var pred = raw.Trim();
                if (pred.Length == 0) continue;

                // [n] / [last()] 位置谓词（XPath 从 1 计数）
                if (int.TryParse(pred, out var idx))
                {
                    cand = idx >= 1 && idx <= cand.Count
                        ? new List<IElement> { cand[idx - 1] }
                        : new List<IElement>();
                    continue;
                }
                if (pred == "last()")
                {
                    cand = cand.Count > 0 ? new List<IElement> { cand[cand.Count - 1] } : cand;
                    continue;
                }
                var pm = PredPosition.Match(pred);
                if (pm.Success)
                {
                    var n = int.Parse(pm.Groups[2].Value);
                    cand = FilterPosition(cand, pm.Groups[1].Value, n);
                    continue;
                }

                List<IElement> filtered = null;
                var m = PredAttrEq.Match(pred);
                if (m.Success)
                {
                    var attr = m.Groups[1].Value; var val = m.Groups[2].Value;
                    filtered = cand.Where(e => (e.GetAttribute(attr) ?? "") == val).ToList();
                }
                else if ((m = PredAttrContains.Match(pred)).Success)
                {
                    var attr = m.Groups[1].Value; var val = m.Groups[2].Value;
                    filtered = cand.Where(e => (e.GetAttribute(attr) ?? "").Contains(val)).ToList();
                }
                else if ((m = PredStartsWith.Match(pred)).Success)
                {
                    var attr = m.Groups[1].Value; var val = m.Groups[2].Value;
                    filtered = cand.Where(e => (e.GetAttribute(attr) ?? "").StartsWith(val, StringComparison.Ordinal)).ToList();
                }
                else if ((m = PredTextEq.Match(pred)).Success)
                {
                    var val = m.Groups[1].Value;
                    filtered = cand.Where(e => OwnText(e) == val).ToList();
                }
                else if ((m = PredTextContains.Match(pred)).Success)
                {
                    var val = m.Groups[1].Value;
                    filtered = cand.Where(e => OwnText(e).Contains(val)).ToList();
                }
                else if ((m = PredAttrHas.Match(pred)).Success)
                {
                    var attr = m.Groups[1].Value;
                    filtered = cand.Where(e => e.HasAttribute(attr)).ToList();
                }
                // 不认识的谓词：宽容放行（保持全部），与 legado 降级语义一致
                if (filtered != null) cand = filtered;
            }
            return cand;
        }

        private static List<IElement> FilterPosition(List<IElement> cand, string op, int n)
        {
            switch (op)
            {
                case "=": return n >= 1 && n <= cand.Count ? new List<IElement> { cand[n - 1] } : new List<IElement>();
                case "<": return cand.Take(Math.Max(0, n - 1)).ToList();
                case "<=": return cand.Take(Math.Min(cand.Count, n)).ToList();
                case ">": return cand.Skip(Math.Min(cand.Count, n)).ToList();
                case ">=": return cand.Skip(Math.Max(0, n - 1)).ToList();
                default: return cand;
            }
        }

        private static string OwnText(IElement el)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var t in el.ChildNodes.OfType<IText>()) sb.Append(t.Text);
            return sb.ToString().Trim();
        }

        // ---------------- 解析 ----------------

        private class Step
        {
            public bool Self;
            public bool Descendant;
            public string Tag = "*";
            public bool IsText;
            public bool IsAttr;
            public string Attr = "";
            public readonly List<string> Preds = new List<string>();
        }

        /// <summary>顶层 | 并集切分（不在括号/引号内的 |）。</summary>
        private static IEnumerable<string> SplitUnion(string s)
        {
            var parts = new List<string>();
            var depth = 0; var inQuote = '\0';
            var start = 0;
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (inQuote != '\0') { if (c == inQuote) inQuote = '\0'; continue; }
                if (c == '\'' || c == '"') { inQuote = c; continue; }
                if (c == '[') depth++;
                else if (c == ']') depth--;
                else if (c == '|' && depth == 0)
                {
                    if (i > start) parts.Add(s.Substring(start, i - start));
                    start = i + 1;
                }
            }
            if (start < s.Length) parts.Add(s.Substring(start));
            return parts.Select(p => p.Trim()).Where(p => p.Length > 0);
        }

        private static List<Step> ParseBranch(string s)
        {
            var steps = new List<Step>();
            var i = 0;
            while (i < s.Length)
            {
                var step = new Step();
                if (s[i] == '/')
                {
                    if (i + 1 < s.Length && s[i + 1] == '/') { step.Descendant = true; i += 2; }
                    else i += 1;
                }
                if (i >= s.Length) break;
                if (s[i] == '.')
                {
                    // ./ 或 .（自身）；.. 父轴极少用于书源，不支持的仍按 . 处理
                    step.Self = true;
                    i++;
                    if (i < s.Length && s[i] == '.') { step.Self = false; step.Tag = "*"; /* .. 退化为全子节点 */ }
                    steps.Add(step);
                    continue;
                }
                var start = i;
                while (i < s.Length && s[i] != '/' && s[i] != '[') i++;
                var tok = s.Substring(start, i - start).Trim();
                if (tok.Length == 0) { if (step.Descendant) continue; break; }

                if (tok == "text()") step.IsText = true;
                else if (tok.StartsWith("@")) { step.IsAttr = true; step.Attr = tok.Substring(1); }
                else if (tok != "*") step.Tag = tok;

                while (i < s.Length && s[i] == '[')
                {
                    var depth = 0; var ps = i;
                    for (; i < s.Length; i++)
                    {
                        if (s[i] == '[') depth++;
                        else if (s[i] == ']') { depth--; if (depth == 0) { i++; break; } }
                    }
                    if (i >= s.Length && depth != 0) break; // 括号不闭合，丢弃
                    step.Preds.Add(s.Substring(ps + 1, i - ps - 2).Trim());
                }
                steps.Add(step);
            }
            return steps;
        }

        // ---------------- DOM 辅助 ----------------

        private static bool TagMatch(IElement e, string tag)
        {
            return tag == "*" || string.Equals(e.LocalName, tag, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>文档序收集后代元素。</summary>
        private static void CollectDesc(IElement el, string tag, List<IElement> output)
        {
            foreach (var c in el.Children)
            {
                if (TagMatch(c, tag)) output.Add(c);
                CollectDesc(c, tag, output);
            }
        }

        private static IEnumerable<IElement> AllDescendants(IElement root)
        {
            foreach (var c in root.Children)
            {
                yield return c;
                foreach (var d in AllDescendants(c)) yield return d;
            }
        }
    }
}
