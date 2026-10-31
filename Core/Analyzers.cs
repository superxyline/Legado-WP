using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace legado_wp.Core
{
    /// <summary>JSONPath 解析（对照 AnalyzeByJSonPath.kt，Newtonsoft SelectToken 等价实现）。</summary>
    public class AnalyzeByJsonPath
    {
        private readonly JToken _token;

        public AnalyzeByJsonPath(object res)
        {
            if (res is JToken jt) _token = jt;
            else _token = JToken.Parse(res.ToString());
        }

        /// <summary>解析后的根 Token（AnalyzeRule 缓存复用）。</summary>
        internal JToken Root => _token;

        public string GetString(string rule)
        {
            var obj = GetObject(rule);
            return obj?.ToString();
        }

        public JToken GetObject(string rulePath)
        {
            if (string.IsNullOrEmpty(rulePath) || rulePath == "$") return _token;
            try
            {
                return _token.SelectToken(rulePath);
            }
            catch
            {
                return null;
            }
        }

        public List<JToken> GetList(string rulePath)
        {
            try
            {
                // 多值路径（$.a[*] / $..b）走 SelectTokens，单值退 SelectToken
                var multi = _token.SelectTokens(rulePath).ToList();
                if (multi.Count > 0) return multi;
            }
            catch { /* 语法不合法时退单值 */ }
            var obj = GetObject(rulePath);
            if (obj is JArray arr) return arr.ToList();
            return obj != null ? new List<JToken> { obj } : new List<JToken>();
        }
    }

    /// <summary>正则模式解析（对照 AnalyzeByRegex.kt：&& 多段串联、组提取）。</summary>
    public static class AnalyzeByRegex
    {
        public static List<string> GetElement(string res, string[] regs, int index = 0)
        {
            var vIndex = index;
            var match = FindFirst(res, regs[vIndex]);
            if (match == null) return null;
            if (vIndex + 1 == regs.Length)
            {
                var info = new List<string>();
                for (var g = 0; g <= match.Groups.Count - 1; g++)
                    info.Add(match.Groups[g].Success ? match.Groups[g].Value : "");
                return info;
            }
            var sb = new System.Text.StringBuilder();
            foreach (Match m in Regex.Matches(res, regs[vIndex])) sb.Append(m.Value);
            return GetElement(sb.ToString(), regs, ++vIndex);
        }

        public static List<List<string>> GetElements(string res, string[] regs, int index = 0)
        {
            var vIndex = index;
            if (FindFirst(res, regs[vIndex]) == null) return new List<List<string>>();
            if (vIndex + 1 == regs.Length)
            {
                var books = new List<List<string>>();
                foreach (Match m in Regex.Matches(res, regs[vIndex]))
                {
                    var info = new List<string>();
                    for (var g = 0; g <= m.Groups.Count - 1; g++)
                        info.Add(m.Groups[g].Success ? m.Groups[g].Value : "");
                    books.Add(info);
                }
                return books;
            }
            var sb = new System.Text.StringBuilder();
            foreach (Match m in Regex.Matches(res, regs[vIndex])) sb.Append(m.Value);
            return GetElements(sb.ToString(), regs, ++vIndex);
        }

        private static Match FindFirst(string res, string pattern)
        {
            try { return Regex.Match(res, pattern); }
            catch { return null; }
        }
    }
}
