using Newtonsoft.Json;

namespace legado_wp.Core
{
    /// <summary>
    /// 替换净化规则（对照 Kotlin ReplaceRule.kt 精简版）。
    /// 注意：XAML 绑定只认属性，全部用 get;set;。
    /// </summary>
    public class ReplaceRule
    {
        [JsonProperty("name")] public string Name { get; set; } = "";
        [JsonProperty("group")] public string Group { get; set; }
        [JsonProperty("pattern")] public string Pattern { get; set; } = "";
        [JsonProperty("replacement")] public string Replacement { get; set; } = "";
        /// <summary>作用域：分号/逗号分隔的书名或书源 URL 片段；空 = 全部生效。</summary>
        [JsonProperty("scope")] public string Scope { get; set; } = "";
        [JsonProperty("useRegex")] public bool UseRegex { get; set; } = false;
        [JsonProperty("enabled")] public bool Enabled { get; set; } = true;
        [JsonProperty("sortOrder")] public int SortOrder { get; set; } = 0;
    }
}
