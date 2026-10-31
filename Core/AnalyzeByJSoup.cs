using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Parser.Html;

namespace legado_wp.Core
{
    /// <summary>
    /// Default 语法 + @CSS: 解析（对照 Kotlin AnalyzeByJSoup.kt，jsoup → AngleSharp 0.9）。
    /// </summary>
    public class AnalyzeByJSoup
    {
        private readonly IElement _element;

        public AnalyzeByJSoup(object doc)
        {
            _element = Parse(doc);
        }

        /// <summary>解析后的根元素（AnalyzeRule 缓存复用，避免同一段 HTML 每字段重解析）。</summary>
        internal IElement Root => _element;

        private static IElement Parse(object doc)
        {
            if (doc is IElement el) return el;
            var parser = new HtmlParser();
            return parser.Parse(doc.ToString()).DocumentElement;
        }

        public List<IElement> GetElements(string rule) => GetElements(_element, rule);

        public string GetString(string ruleStr)
        {
            if (string.IsNullOrEmpty(ruleStr)) return null;
            var list = GetStringList(ruleStr);
            if (list.Count == 0) return null;
            if (list.Count == 1) return list[0];
            return string.Join("\n", list);
        }

        /// <summary>isUrl 场景取第一个（对照 getString0）。</summary>
        public string GetString0(string ruleStr)
        {
            var list = GetStringList(ruleStr);
            return list.Count == 0 ? "" : list[0];
        }

        public List<string> GetStringList(string ruleStr)
        {
            var textS = new List<string>();
            if (string.IsNullOrEmpty(ruleStr)) return textS;

            var isCss = ruleStr.StartsWith("@CSS:", StringComparison.OrdinalIgnoreCase);
            var elementsRule = isCss ? ruleStr.Substring(5).Trim() : ruleStr;

            if (elementsRule.Length == 0)
            {
                textS.Add(_element.TextContent);
                return textS;
            }

            var ruleAnalyzes = new RuleAnalyzer(elementsRule);
            var ruleStrS = ruleAnalyzes.SplitRule("&&", "||", "%%");

            var results = new List<List<string>>();
            foreach (var ruleStrX in ruleStrS)
            {
                List<string> temp;
                if (isCss)
                {
                    var lastIndex = ruleStrX.LastIndexOf('@');
                    temp = GetResultLast(
                        new List<IElement>(_element.QuerySelectorAll(ruleStrX.Substring(0, lastIndex))),
                        ruleStrX.Substring(lastIndex + 1));
                }
                else
                {
                    temp = GetResultList(ruleStrX);
                }
                if (temp != null && temp.Count > 0)
                {
                    results.Add(temp);
                    if (ruleAnalyzes.ElementsType == "||") break;
                }
            }
            if (results.Count > 0) RuleCombiner.CombineResults(results, ruleAnalyzes.ElementsType, textS);
            return textS;
        }

        private List<IElement> GetElements(IElement temp, string rule)
        {
            if (temp == null || string.IsNullOrEmpty(rule)) return new List<IElement>();

            var isCss = rule.StartsWith("@CSS:", StringComparison.OrdinalIgnoreCase);
            var elementsRule = isCss ? rule.Substring(5).Trim() : rule;

            var ruleAnalyzes = new RuleAnalyzer(elementsRule);
            var ruleStrS = ruleAnalyzes.SplitRule("&&", "||", "%%");

            var elementsList = new List<List<IElement>>();
            if (isCss)
            {
                foreach (var ruleStr in ruleStrS)
                {
                    var tempS = new List<IElement>(temp.QuerySelectorAll(ruleStr));
                    elementsList.Add(tempS);
                    if (tempS.Count > 0 && ruleAnalyzes.ElementsType == "||") break;
                }
            }
            else
            {
                foreach (var ruleStr in ruleStrS)
                {
                    var rsRule = new RuleAnalyzer(ruleStr);
                    rsRule.Trim();
                    var rs = rsRule.SplitRule("@");

                    List<IElement> el;
                    if (rs.Count > 1)
                    {
                        el = new List<IElement> { temp };
                        foreach (var rl in rs)
                        {
                            var es = new List<IElement>();
                            foreach (var et in el) es.AddRange(GetElements(et, rl));
                            el = es;
                        }
                    }
                    else
                    {
                        el = ElementsSingle.GetElementsSingle(temp, ruleStr);
                    }
                    elementsList.Add(el);
                    if (el.Count > 0 && ruleAnalyzes.ElementsType == "||") break;
                }
            }
            var elements = new List<IElement>();
            if (elementsList.Count > 0)
                RuleCombiner.CombineResults(elementsList, ruleAnalyzes.ElementsType, elements);
            return elements;
        }

        private List<string> GetResultList(string ruleStr)
        {
            if (string.IsNullOrEmpty(ruleStr)) return null;

            var elements = new List<IElement> { _element };
            var rule = new RuleAnalyzer(ruleStr);
            rule.Trim();
            var rules = rule.SplitRule("@");

            var last = rules.Count - 1;
            for (var i = 0; i < last; i++)
            {
                var es = new List<IElement>();
                foreach (var elt in elements) es.AddRange(ElementsSingle.GetElementsSingle(elt, rules[i]));
                elements = es;
            }
            return elements.Count == 0 ? null : GetResultLast(elements, rules[last]);
        }

        /// <summary>末段取值：text/textNodes/ownText/html/all/attr（对照 getResultLast）。</summary>
        internal static List<string> GetResultLast(List<IElement> elements, string lastRule)
        {
            var textS = new List<string>();
            switch (lastRule)
            {
                case "text":
                    foreach (var element in elements)
                    {
                        var text = element.TextContent;
                        if (!string.IsNullOrEmpty(text)) textS.Add(text);
                    }
                    break;
                case "textNodes":
                    foreach (var element in elements)
                    {
                        var tn = element.ChildNodes
                            .OfType<IText>()
                            .Select(t => t.Text.Trim())
                            .Where(t => t.Length > 0)
                            .ToList();
                        if (tn.Count > 0) textS.Add(string.Join("\n", tn));
                    }
                    break;
                case "ownText":
                    foreach (var element in elements)
                    {
                        var text = OwnText(element);
                        if (!string.IsNullOrEmpty(text)) textS.Add(text);
                    }
                    break;
                case "html":
                    foreach (var element in elements)
                    {
                        foreach (var bad in element.QuerySelectorAll("script").ToList()) bad.Parent.RemoveChild(bad);
                        foreach (var bad in element.QuerySelectorAll("style").ToList()) bad.Parent.RemoveChild(bad);
                        var html = element.OuterHtml;
                        if (!string.IsNullOrEmpty(html)) textS.Add(html);
                    }
                    break;
                case "all":
                    foreach (var element in elements) textS.Add(element.OuterHtml);
                    break;
                default:
                    foreach (var element in elements)
                    {
                        var url = element.GetAttribute(lastRule);
                        if (string.IsNullOrWhiteSpace(url) || textS.Contains(url)) continue;
                        textS.Add(url);
                    }
                    break;
            }
            return textS;
        }

        private static string OwnText(IElement el)
        {
            var sb = new StringBuilder();
            foreach (var node in el.ChildNodes.OfType<IText>()) sb.Append(node.Text);
            return sb.ToString().Trim();
        }

        /// <summary>
        /// 单段元素定位 + 索引筛选（对照 Kotlin ElementsSingle）。
        /// 支持 .N :N !N 老式索引与 [a,b] [!s:e:st] 新式区间索引。
        /// </summary>
        internal static class ElementsSingle
        {
            internal static List<IElement> GetElementsSingle(IElement temp, string rule)
            {
                var r = FindIndexSet(rule, out var split, out var beforeRule,
                    out var indexDefault, out var indexes);

                List<IElement> elements;
                if (beforeRule.Length == 0)
                {
                    elements = temp.Children.Select(c => c).ToList();
                }
                else
                {
                    var parts = beforeRule.Split('.');
                    switch (parts[0])
                    {
                        case "children":
                            elements = temp.Children.Select(c => c).ToList();
                            break;
                        case "class":
                            elements = temp.GetElementsByClassName(parts[1]).ToList();
                            break;
                        case "tag":
                            elements = temp.GetElementsByTagName(parts[1]).ToList();
                            break;
                        case "id":
                            var el = temp.QuerySelector("#" + parts[1]);
                            elements = el != null ? new List<IElement> { el } : new List<IElement>();
                            break;
                        case "text":
                            var kw = parts[1];
                            elements = AllElements(temp).Where(e => OwnTextContains(e, kw)).ToList();
                            break;
                        default:
                            try { elements = temp.QuerySelectorAll(beforeRule).ToList(); }
                            catch { elements = new List<IElement>(); }
                            break;
                    }
                }

                var len = elements.Count;
                var indexSet = new HashSet<int>();

                if (indexes.Count == 0)
                {
                    var lastIndex = indexDefault.Count - 1;
                    if (lastIndex >= 0)
                        for (var ix = lastIndex; ix >= 0; ix--)
                        {
                            var it = indexDefault[ix];
                            if (it >= 0 && it < len) indexSet.Add(it);
                            else if (it < 0 && len >= -it) indexSet.Add(it + len);
                        }
                }
                else
                {
                    var lastIndex = indexes.Count - 1;
                    for (var ix = lastIndex; ix >= 0; ix--)
                    {
                        if (indexes[ix] is int[])
                        {
                            var t = (int[])indexes[ix]; // [start, end, step]，null 用 int.MinValue 表示
                            int start = t[0] == int.MinValue ? 0 : t[0];
                            if (start < 0) start += len;
                            int end = t[1] == int.MinValue ? len - 1 : t[1];
                            if (end < 0) end += len;

                            if ((start < 0 && end < 0) || (start >= len && end >= len)) continue;
                            if (start >= len) start = len - 1; else if (start < 0) start = 0;
                            if (end >= len) end = len - 1; else if (end < 0) end = 0;

                            var stepX = t[2];
                            if (start == end || stepX >= len) { indexSet.Add(start); continue; }
                            var step = stepX > 0 ? stepX : (stepX + len > 0 ? stepX + len : 1);
                            if (end > start)
                                for (var i = start; i <= end; i += step) indexSet.Add(i);
                            else
                                for (var i = start; i >= end; i += step) indexSet.Add(i);
                        }
                        else
                        {
                            var it = (int)indexes[ix];
                            if (it >= 0 && it < len) indexSet.Add(it);
                            else if (it < 0 && len >= -it) indexSet.Add(it + len);
                        }
                    }
                }

                var result = new List<IElement>();
                if (split == '!')
                {
                    for (var i = 0; i < len; i++) if (!indexSet.Contains(i)) result.Add(elements[i]);
                }
                else if (split == '.')
                {
                    foreach (var pc in indexSet) if (pc < len) result.Add(elements[pc]);
                }
                else
                {
                    result = elements;
                }
                return result;
            }

            private static IEnumerable<IElement> AllElements(IElement root)
            {
                foreach (var e in root.QuerySelectorAll("*")) yield return e;
            }

            private static bool OwnTextContains(IElement e, string kw)
            {
                var sb = new StringBuilder();
                foreach (var node in e.ChildNodes.OfType<IText>()) sb.Append(node.Text);
                return sb.ToString().Contains(kw);
            }

            /// <summary>
            /// 索引解析（对照 findIndexSet 逆向扫描）。返回值无用，out 参数带出解析结果。
            /// </summary>
            private static bool FindIndexSet(string rule, out char split, out string beforeRule,
                out List<int> indexDefault, out List<object> indexes)
            {
                var rus = rule.Trim();
                split = '.';
                beforeRule = "";
                indexDefault = new List<int>();
                indexes = new List<object>();

                var len = rus.Length;
                var curMinus = false;
                var l = "";
                var curList = new List<int>();

                var head = rus[len - 1] == ']';
                var i = head ? len - 2 : len - 1;

                while (i >= 0)
                {
                    var rl = rus[i];
                    if (rl == ' ') { i--; continue; }
                    if (rl >= '0' && rl <= '9') { l = rl + l; i--; continue; }
                    if (rl == '-') { curMinus = true; i--; continue; }

                    var curInt = l.Length == 0 ? int.MinValue
                        : (curMinus ? -int.Parse(l) : int.Parse(l));

                    if (head)
                    {
                        if (rl == ':') { curList.Add(curInt); l = ""; curMinus = false; curInt = int.MinValue; i--; continue; }
                        if (curList.Count == 0)
                        {
                            if (curInt == int.MinValue) break;
                            indexes.Add(curInt);
                        }
                        else
                        {
                            indexes.Add(new[] { curInt, curList[curList.Count - 1], curList.Count == 2 ? curList[0] : 1 });
                            curList.Clear();
                        }
                        if (rl == '!')
                        {
                            split = '!';
                            do { i--; } while (i > 0 && rus[i] == ' ');
                            rl = rus[i];
                        }
                        if (rl == '[')
                        {
                            beforeRule = rus.Substring(0, i);
                            return true;
                        }
                        if (rl != ',') break;
                    }
                    else
                    {
                        if (rl == '!' || rl == '.' || rl == ':')
                        {
                            indexDefault.Add(curInt == int.MinValue ? 0 : curInt);
                            if (rl != ':')
                            {
                                split = rl;
                                beforeRule = rus.Substring(0, i);
                                return true;
                            }
                        }
                        else break;
                    }
                    l = "";
                    curMinus = false;
                    i--;
                }

                split = ' ';
                beforeRule = rus;
                return false;
            }
        }
    }
}
