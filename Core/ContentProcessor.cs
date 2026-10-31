using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace legado_wp.Core
{
    /// <summary>
    /// 正文/标题替换净化（对照 Kotlin ContentProcessor + ReplaceAnalyzer 语义精简版）：
    /// 读取启用的 ReplaceRule，按作用域过滤（书名/书源 URL 包含匹配），SortOrder 升序依次套用。
    /// 非 useRegex 规则做字面替换（pattern 先正则转义），替换串支持 $1 组引用。
    /// </summary>
    public static class ContentProcessor
    {
        public static async Task<string> ApplyContentAsync(string content, string bookName, string sourceUrl)
        {
            if (string.IsNullOrEmpty(content)) return content;
            var rules = await GetRulesAsync();
            if (rules.Count == 0) return content;
            foreach (var r in rules)
                content = Apply(content, r, sourceUrl, bookName);
            return content;
        }

        public static async Task<string> ApplyTitleAsync(string title, string bookName, string sourceUrl)
        {
            if (string.IsNullOrEmpty(title)) return title;
            var rules = await GetRulesAsync();
            if (rules.Count == 0) return title;
            foreach (var r in rules)
                title = Apply(title, r, sourceUrl, bookName);
            return title;
        }

        private static string Apply(string text, ReplaceRule rule, string sourceUrl, string bookName)
        {
            if (!rule.Enabled || string.IsNullOrEmpty(rule.Pattern)) return text;
            if (!ScopeMatch(rule.Scope, bookName, sourceUrl)) return text;
            try
            {
                var pattern = rule.UseRegex ? rule.Pattern : Regex.Escape(rule.Pattern);
                // 字面替换时把替换串里的 $ 序列转义，避免被当组引用
                var replacement = rule.UseRegex ? rule.Replacement : Regex.Replace(rule.Replacement ?? "", "\\$", "$$$$");
                return Regex.Replace(text, pattern, replacement);
            }
            catch
            {
                return text; // 坏正则不吞内容
            }
        }

        /// <summary>作用域匹配：空=全部；各项为书名或书源 URL 的"包含"匹配，分号/逗号分隔。</summary>
        public static bool ScopeMatch(string scope, string bookName, string sourceUrl)
        {
            if (string.IsNullOrWhiteSpace(scope)) return true;
            var items = scope.Split(new[] { ';', '；', ',', '，' }, StringSplitOptions.RemoveEmptyEntries);
            if (items.Length == 0) return true;
            return items.Any(item =>
            {
                var k = item.Trim();
                if (k.Length == 0) return false;
                return (!string.IsNullOrEmpty(bookName) && bookName.Contains(k))
                    || (!string.IsNullOrEmpty(sourceUrl) && sourceUrl.Contains(k));
            });
        }

        private static async Task<List<ReplaceRule>> GetRulesAsync()
        {
            var all = await Storage.LoadReplaceRulesAsync();
            return all.Where(r => r.Enabled && !string.IsNullOrEmpty(r.Pattern))
                .OrderBy(r => r.SortOrder)
                .ToList();
        }
    }
}
