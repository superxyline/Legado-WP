using System.Collections.Generic;
using Newtonsoft.Json;

namespace legado_wp.Core
{
    /// <summary>
    /// 书源实体（对照 Kotlin BookSource.kt，仅保留 M1/M2 链路所需字段）。
    /// 注意：XAML 绑定只认属性，全部用 get;set;（不能用公有字段）。
    /// </summary>
    public class BookSource
    {
        [JsonProperty("bookSourceUrl")] public string BookSourceUrl { get; set; } = "";
        [JsonProperty("bookSourceName")] public string BookSourceName { get; set; } = "";
        [JsonProperty("bookSourceGroup")] public string BookSourceGroup { get; set; }
        [JsonProperty("bookSourceType")] public int BookSourceType { get; set; }
        [JsonProperty("enabled")] public bool Enabled { get; set; } = true;
        [JsonProperty("enabledExplore")] public bool EnabledExplore { get; set; } = true;
        [JsonProperty("header")] public string Header { get; set; }
        [JsonProperty("loginUrl")] public string LoginUrl { get; set; }
        [JsonProperty("jsLib")] public string JsLib { get; set; }
        [JsonProperty("variable")] public string Variable { get; set; }
        [JsonProperty("bookUrlPattern")] public string BookUrlPattern { get; set; }
        [JsonProperty("customOrder")] public int CustomOrder { get; set; }
        [JsonProperty("weight")] public int Weight { get; set; }
        [JsonProperty("lastUpdateTime")] public long LastUpdateTime { get; set; }
        [JsonProperty("respondTime")] public long RespondTime { get; set; } = 180000;
        [JsonProperty("searchUrl")] public string SearchUrl { get; set; }
        [JsonProperty("exploreUrl")] public string ExploreUrl { get; set; }
        [JsonProperty("ruleSearch")] public SearchRule SearchRule { get; set; }
        [JsonProperty("ruleExplore")] public ExploreRule ExploreRule { get; set; }
        [JsonProperty("ruleBookInfo")] public BookInfoRule BookInfoRule { get; set; }
        [JsonProperty("ruleToc")] public TocRule TocRule { get; set; }
        [JsonProperty("ruleContent")] public ContentRule ContentRule { get; set; }

        public static BookSource FromJson(string json)
        {
            var src = JsonConvert.DeserializeObject<BookSource>(json);
            return src;
        }

        public static List<BookSource> FromJsonArray(string json)
        {
            return JsonConvert.DeserializeObject<List<BookSource>>(json) ?? new List<BookSource>();
        }
    }

    public class SearchRule
    {
        [JsonProperty("bookList")] public string BookList { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("author")] public string Author { get; set; }
        [JsonProperty("intro")] public string Intro { get; set; }
        [JsonProperty("coverUrl")] public string CoverUrl { get; set; }
        [JsonProperty("bookUrl")] public string BookUrl { get; set; }
        [JsonProperty("lastChapter")] public string LastChapter { get; set; }
        [JsonProperty("checkKeyWord")] public string CheckKeyWord { get; set; }
    }

    public class ExploreRule
    {
        [JsonProperty("bookList")] public string BookList { get; set; }
    }

    public class BookInfoRule
    {
        [JsonProperty("init")] public string Init { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("author")] public string Author { get; set; }
        [JsonProperty("intro")] public string Intro { get; set; }
        [JsonProperty("coverUrl")] public string CoverUrl { get; set; }
        [JsonProperty("tocUrl")] public string TocUrl { get; set; }
        [JsonProperty("wordCount")] public string WordCount { get; set; }
        [JsonProperty("lastChapter")] public string LastChapter { get; set; }
        [JsonProperty("canReName")] public string CanReName { get; set; }
    }

    public class TocRule
    {
        [JsonProperty("chapterList")] public string ChapterList { get; set; }
        [JsonProperty("chapterName")] public string ChapterName { get; set; }
        [JsonProperty("chapterUrl")] public string ChapterUrl { get; set; }
        [JsonProperty("isVolume")] public string IsVolume { get; set; }
        [JsonProperty("isVip")] public string IsVip { get; set; }
        [JsonProperty("updateTime")] public string UpdateTime { get; set; }
        [JsonProperty("nextTocUrl")] public string NextTocUrl { get; set; }
    }

    public class ContentRule
    {
        [JsonProperty("content")] public string Content { get; set; }
        [JsonProperty("nextContentUrl")] public string NextContentUrl { get; set; }
        [JsonProperty("replaceRegex")] public string ReplaceRegex { get; set; }
        [JsonProperty("webJs")] public string WebJs { get; set; }
        [JsonProperty("sourceRegex")] public string SourceRegex { get; set; }
    }
}
