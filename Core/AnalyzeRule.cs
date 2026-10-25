using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace legado_wp.Core
{
    public enum RuleMode { Default, Json, XPath, Js, Regex }

    /// <summary>
    /// 规则引擎入口（对照 Kotlin AnalyzeRuleCore.kt，M1 范围）。
    /// M1 边界：{{js}}/<js> 段切分但不执行（stub 返回空）；XPath 不实现（返回空）。
    /// </summary>
    public class AnalyzeRule
    {
        private static readonly Regex JsPattern =
            new Regex("<js>([\\s\\S]*?)</js>|\\{\\{([\\s\\S]*?)\\}\\}", RegexOptions.Compiled);
        private static readonly Regex EvalPattern =
            new Regex("@get:\\{([\\s\\S]*?)\\}|\\{\\{([\\s\\S]*?)\\}\\}", RegexOptions.Compiled);
        private static readonly Regex PutPattern =
            new Regex("@put:\\{([\\s\\S]*?)\\}", RegexOptions.Compiled);
        private static readonly Regex GroupRefPattern =
            new Regex("\\$(\\d{1,2})", RegexOptions.Compiled);

        public object Content { get; private set; }
        public string BaseUrl { get; set; }

        private readonly Dictionary<string, string> _variables = new Dictionary<string, string>();
        private readonly List<string> _unsupported = new List<string>();

        public AnalyzeRule(object content, string baseUrl = null)
        {
            Content = content;
            BaseUrl = baseUrl;
        }

        /// <summary>M1 期间记录遇到的未支持语法（{{js}}/XPath），供探针输出。</summary>
        public IList<string> Unsupported => _unsupported;

        private static bool IsJson(object o)
        {
            if (o is JToken) return true;
            var s = o as string;
            if (s == null) return false;
            s = s.TrimStart();
            return s.StartsWith("{") || s.StartsWith("[");
        }

        // ---------------- SourceRule ----------------

        public class SourceRule
        {
            public string Rule;
            public string ReplaceRegex = "";
            public string Replacement = "";
            public bool ReplaceFirst;
            public RuleMode Mode;
            public readonly Dictionary<string, string> PutMap = new Dictionary<string, string>();
            private readonly List<string> _ruleParam = new List<string>();
            private readonly List<int> _ruleType = new List<int>(); // -2 get / -1 js / >=1 组引用 / 0 静态
            private const int GetRuleType = -2;
            private const int JsRuleType = -1;
            private const int DefaultRuleType = 0;

            public SourceRule(string ruleStr, RuleMode mode, AnalyzeRule host, bool contentIsJson)
            {
                Mode = mode;
                if (mode == RuleMode.Js || mode == RuleMode.Regex)
                {
                    Rule = ruleStr;
                }
                else if (ruleStr.StartsWith("@CSS:", StringComparison.OrdinalIgnoreCase))
                {
                    Mode = RuleMode.Default;
                    Rule = ruleStr;
                }
                else if (ruleStr.StartsWith("@@"))
                {
                    Mode = RuleMode.Default;
                    Rule = ruleStr.Substring(2);
                }
                else if (ruleStr.StartsWith("@XPath:", StringComparison.OrdinalIgnoreCase))
                {
                    Mode = RuleMode.XPath;
                    Rule = ruleStr.Substring(7);
                }
                else if (ruleStr.StartsWith("@Json:", StringComparison.OrdinalIgnoreCase))
                {
                    Mode = RuleMode.Json;
                    Rule = ruleStr.Substring(6);
                }
                else if (ruleStr.StartsWith(":"))
                {
                    // 字符串正则提取语法（文档规则：:pattern 取捕获组/匹配）
                    Mode = RuleMode.Regex;
                    Rule = ruleStr.Substring(1);
                }
                else if (contentIsJson || ruleStr.StartsWith("$.") || ruleStr.StartsWith("$["))
                {
                    Mode = RuleMode.Json;
                    Rule = ruleStr;
                }
                else if (ruleStr.StartsWith("/"))
                {
                    Mode = RuleMode.XPath;
                    Rule = ruleStr;
                }
                else
                {
                    Rule = ruleStr;
                }

                Rule = SplitPutRule(Rule, PutMap);

                // @get:{...} / {{...}} 切分
                var start = 0;
                var matches = EvalPattern.Matches(Rule).Cast<Match>().ToList();
                if (matches.Count > 0)
                {
                    var first = matches[0];
                    var tmp = Rule.Substring(start, first.Index - start);
                    if (Mode != RuleMode.Js && Mode != RuleMode.Regex &&
                        (first.Index == 0 || !tmp.Contains("##")))
                    {
                        Mode = RuleMode.Regex;
                    }
                    foreach (var match in matches)
                    {
                        if (match.Index > start)
                        {
                            SplitStatic(Rule.Substring(start, match.Index - start));
                        }
                        var tmp2 = match.Value;
                        if (tmp2.StartsWith("@get:", StringComparison.OrdinalIgnoreCase))
                        {
                            _ruleType.Add(GetRuleType);
                            _ruleParam.Add(tmp2.Substring(6, tmp2.Length - 7));
                        }
                        else
                        {
                            _ruleType.Add(JsRuleType);
                            _ruleParam.Add(tmp2.Substring(2, tmp2.Length - 4));
                        }
                        start = match.Index + match.Length;
                    }
                }
                if (Rule.Length > start)
                {
                    SplitStatic(Rule.Substring(start));
                }
            }

            private void SplitStatic(string ruleStr)
            {
                // $n 组引用切分（Regex 模式的捕获组引用）
                var matches = GroupRefPattern.Matches(ruleStr).Cast<Match>().ToList();
                if (matches.Count > 0)
                {
                    if (Mode != RuleMode.Js && Mode != RuleMode.Regex) Mode = RuleMode.Regex;
                    var start = 0;
                    foreach (var match in matches)
                    {
                        if (match.Index > start)
                        {
                            _ruleType.Add(DefaultRuleType);
                            _ruleParam.Add(ruleStr.Substring(start, match.Index - start));
                        }
                        _ruleType.Add(int.Parse(match.Groups[1].Value));
                        _ruleParam.Add(match.Value);
                        start = match.Index + match.Length;
                    }
                    if (ruleStr.Length > start)
                    {
                        _ruleType.Add(DefaultRuleType);
                        _ruleParam.Add(ruleStr.Substring(start));
                    }
                }
                else
                {
                    _ruleType.Add(DefaultRuleType);
                    _ruleParam.Add(ruleStr);
                }
            }

            /// <summary>用上一段结果重建规则串（$n 组引用替换，@get 取变量，{{js}} stub）。</summary>
            public void MakeUpRule(object result, AnalyzeRule host)
            {
                if (_ruleParam.Count == 0) return;
                var infoVal = new StringBuilder();
                for (var index = _ruleParam.Count - 1; index >= 0; index--)
                {
                    var regType = _ruleType[index];
                    if (regType > DefaultRuleType)
                    {
                        if (result is List<string> list && list.Count > regType && list[regType] != null)
                        {
                            infoVal.Insert(0, list[regType]);
                        }
                        else
                        {
                            infoVal.Insert(0, _ruleParam[index]);
                        }
                    }
                    else if (regType == JsRuleType)
                    {
                        // {{js}} 内嵌表达式（M3：Jint 执行，结果拼回规则串）
                        var jsVal = JsEngine.Eval(_ruleParam[index], result, host.BaseUrl);
                        infoVal.Insert(0, jsVal == null ? "" : jsVal.ToString());
                    }
                    else if (regType == GetRuleType)
                    {
                        infoVal.Insert(0, host.Get(_ruleParam[index]));
                    }
                    else
                    {
                        infoVal.Insert(0, _ruleParam[index]);
                    }
                }
                Rule = infoVal.ToString();
                SplitTail();
            }

            private void SplitTail()
            {
                var parts = Rule.Split(new[] { "##" }, StringSplitOptions.None);
                Rule = parts[0].Trim();
                if (parts.Length > 1) ReplaceRegex = parts[1];
                if (parts.Length > 2) Replacement = parts[2];
                if (parts.Length > 3) ReplaceFirst = true;
                if (ReplaceRegex.StartsWith("!"))
                {
                    ReplaceFirst = true;
                    ReplaceRegex = ReplaceRegex.Substring(1);
                }
            }

            private static string SplitPutRule(string ruleStr, Dictionary<string, string> putMap)
            {
                foreach (Match match in PutPattern.Matches(ruleStr))
                {
                    ruleStr = ruleStr.Replace(match.Value, "");
                    try
                    {
                        var obj = JObject.Parse(match.Groups[1].Value);
                        foreach (var prop in obj.Properties())
                            putMap[prop.Name] = prop.Value?.ToString() ?? "";
                    }
                    catch { /* put JSON 解析失败忽略 */ }
                }
                return ruleStr;
            }
        }

        // ---------------- 规则切分 ----------------

        public List<SourceRule> SplitSourceRule(string ruleStr)
        {
            var ruleList = new List<SourceRule>();
            if (string.IsNullOrEmpty(ruleStr)) return ruleList;
            var mode = RuleMode.Default;
            var contentIsJson = IsJson(Content);
            var start = 0;
            var tmp = "";
            foreach (Match match in JsPattern.Matches(ruleStr))
            {
                if (match.Index > start)
                {
                    tmp = ruleStr.Substring(start, match.Index - start).Trim();
                    if (tmp.Length > 0) ruleList.Add(new SourceRule(tmp, mode, this, contentIsJson));
                }
                var js = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[1].Value;
                ruleList.Add(new SourceRule(js, RuleMode.Js, this, contentIsJson));
                start = match.Index + match.Length;
            }
            if (ruleStr.Length > start)
            {
                tmp = ruleStr.Substring(start).Trim();
                if (tmp.Length > 0) ruleList.Add(new SourceRule(tmp, mode, this, contentIsJson));
            }
            return ruleList;
        }

        // ---------------- 对外执行 ----------------

        public string GetString(string ruleStr)
        {
            object result = null;
            var content = Content;
            var ruleList = SplitSourceRule(ruleStr);
            if (content != null && ruleList.Count > 0)
            {
                result = content;
                foreach (var sourceRule in ruleList)
                {
                    foreach (var kv in sourceRule.PutMap) Put(kv.Key, GetString(kv.Value));
                    sourceRule.MakeUpRule(result, this);
                    if (result == null) continue;
                    var rule = sourceRule.Rule;
                    if (!string.IsNullOrWhiteSpace(rule) || sourceRule.ReplaceRegex.Length == 0)
                    {
                        result = Apply(sourceRule, rule, result);
                    }
                    if (result != null && sourceRule.ReplaceRegex.Length > 0)
                    {
                        result = ReplaceRegex(result.ToString(), sourceRule);
                    }
                }
            }
            var resultStr = result?.ToString() ?? "";
            if (resultStr.IndexOf('&') > -1) resultStr = UnescapeHtml(resultStr);
            return resultStr;
        }

        private object Apply(SourceRule sr, string rule, object result)
        {
            switch (sr.Mode)
            {
                case RuleMode.Js:
                    {
                        var v = JsEngine.Eval(rule, result, BaseUrl);
                        return v == null ? "" : v.ToString();
                    }
                case RuleMode.Json:
                    return new AnalyzeByJsonPath(result).GetString(rule);
                case RuleMode.XPath:
                    _unsupported.Add("XPath");
                    return "";
                case RuleMode.Regex:
                    return ApplyRegex(result.ToString(), rule);
                default:
                    return new AnalyzeByJSoup(result).GetString(rule);
            }
        }

        /// <summary>Regex 模式：rule 为 pattern，返回整个匹配（无组）或组拼接。</summary>
        private static string ApplyRegex(string input, string pattern)
        {
            Match m;
            try { m = Regex.Match(input, pattern); }
            catch { return ""; }
            if (!m.Success) return "";
            if (m.Groups.Count > 1)
                return string.Join("", m.Groups.Cast<Group>().Skip(1).Where(g => g.Success).Select(g => g.Value));
            return m.Value;
        }

        public List<string> GetStringList(string ruleStr)
        {
            var result = new List<string>();
            var content = Content;
            var ruleList = SplitSourceRule(ruleStr);
            if (content == null || ruleList.Count == 0) return result;
            foreach (var sr in ruleList)
            {
                sr.MakeUpRule(content, this);
                switch (sr.Mode)
                {
                    case RuleMode.Json:
                        result.AddRange(new AnalyzeByJsonPath(content).GetList(sr.Rule)
                            .Select(t => t?.ToString()));
                        break;
                    case RuleMode.XPath:
                        _unsupported.Add("XPath");
                        break;
                    case RuleMode.Js:
                        _unsupported.Add("<js>");
                        break;
                    default:
                        result.AddRange(new AnalyzeByJSoup(content).GetStringList(sr.Rule));
                        break;
                }
            }
            return result.Where(s => s != null).ToList();
        }

        /// <summary>元素列表（搜索 bookList / 目录 chapterList 用）。</summary>
        public List<object> GetElements(string ruleStr)
        {
            var result = new List<object>();
            var content = Content;
            if (content == null) return result;
            foreach (var sr in SplitSourceRule(ruleStr))
            {
                sr.MakeUpRule(content, this);
                switch (sr.Mode)
                {
                    case RuleMode.Json:
                        result.AddRange(new AnalyzeByJsonPath(content).GetList(sr.Rule));
                        break;
                    case RuleMode.XPath:
                        _unsupported.Add("XPath");
                        break;
                    case RuleMode.Js:
                        _unsupported.Add("<js>");
                        break;
                    default:
                        result.AddRange(new AnalyzeByJSoup(content).GetElements(sr.Rule)
                            .Cast<object>());
                        break;
                }
            }
            return result;
        }

        /// <summary>对单个元素/JSON 节点继续取字段（列表项 → 书名/作者）。</summary>
        public string GetStringFrom(object item, string ruleStr)
        {
            var saved = Content;
            Content = item;
            try { return GetString(ruleStr); }
            finally { Content = saved; }
        }

        // ---------------- 正则替换 & HTML 反转义 & 变量 ----------------

        internal static string ReplaceRegex(string result, SourceRule rule)
        {
            if (rule.ReplaceRegex.Length == 0) return result;
            try
            {
                if (rule.ReplaceFirst)
                {
                    var m = Regex.Match(result, rule.ReplaceRegex);
                    if (!m.Success) return "";
                    return Regex.Replace(m.Value, rule.ReplaceRegex, rule.Replacement);
                }
                return Regex.Replace(result, rule.ReplaceRegex, rule.Replacement);
            }
            catch
            {
                return rule.ReplaceFirst ? rule.Replacement : result;
            }
        }

        internal static string UnescapeHtml(string s)
        {
            if (s.IndexOf('&') == -1) return s;
            var sb = new StringBuilder(s);
            sb.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
              .Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&#39;", "'")
              .Replace("&nbsp;", " ");
            // 数字实体 &#123; / &#x1A;
            var out2 = new StringBuilder();
            for (var i = 0; i < sb.Length; i++)
            {
                if (sb[i] == '&' && i + 2 < sb.Length && sb[i + 1] == '#')
                {
                    var end = sb.ToString().IndexOf(';', i);
                    if (end > 0 && end - i <= 10)
                    {
                        var num = sb.ToString(i + 2, end - i - 2);
                        int code;
                        if (num.StartsWith("x") || num.StartsWith("X"))
                            code = Convert.ToInt32(num.Substring(1), 16);
                        else if (!int.TryParse(num, out code)) code = -1;
                        if (code >= 0)
                        {
                            out2.Append((char)code);
                            i = end;
                            continue;
                        }
                    }
                }
                out2.Append(sb[i]);
            }
            return out2.ToString();
        }

        public void Put(string key, string value) { _variables[key] = value ?? ""; }

        public string Get(string key)
        {
            string v;
            return _variables.TryGetValue(key, out v) ? v : "";
        }
    }
}
