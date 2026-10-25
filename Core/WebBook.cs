using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace legado_wp.Core
{
    // ---------------- 实体（XAML 绑定需要属性而非字段）----------------

    public class SearchBook
    {
        public string Name { get; set; }
        public string Author { get; set; }
        public string BookUrl { get; set; }
        public string CoverUrl { get; set; }
        public string Intro { get; set; }
        public string LastChapter { get; set; }
        public string SourceUrl { get; set; }
        public override string ToString() { return Name + "/" + Author + " " + BookUrl; }
    }

    public class Book : SearchBook
    {
        public string TocUrl { get; set; }
        public long WordCount { get; set; }
    }

    public class BookChapter
    {
        public string Title { get; set; }
        public string Url { get; set; }
        public string UpdateTime { get; set; }
        public bool IsVolume { get; set; }
        public override string ToString() { return Title; }
    }

    /// <summary>
    /// WebBook 链路（对照 Kotlin webBook/ 四文件，M2 范围）。
    /// 搜索 → 详情 → 目录（含翻页）→ 正文（含翻页）。
    /// </summary>
    public class WebBook
    {
        private readonly IPageFetcher _fetcher;
        public readonly List<string> DebugLog = new List<string>();

        public WebBook(IPageFetcher fetcher = null)
        {
            _fetcher = fetcher ?? HttpPageFetcher.Default;
        }

        // ---------------- 搜索 ----------------

        public async Task<List<SearchBook>> SearchBookAsync(BookSource source, string key, int page = 1)
        {
            var variables = new Dictionary<string, string> { { "key", key }, { "page", page.ToString() } };
            var au = new AnalyzeUrl(source.SearchUrl, source.BookSourceUrl, source.Header, variables);
            var res = await _fetcher.GetAsync(au);
            DebugLog.Add("search code=" + res.Code + " len=" + (res.Body?.Length ?? 0) + " url=" + au.Url);

            var analyzer = new AnalyzeRule(res.Body) { BaseUrl = au.BaseUrl };
            var items = analyzer.GetElements(source.SearchRule?.BookList);
            var result = new List<SearchBook>();
            foreach (var item in items)
            {
                var sb = new SearchBook { SourceUrl = source.BookSourceUrl };
                var ia = new AnalyzeRule(item) { BaseUrl = au.BaseUrl };
                sb.Name = ia.GetString(source.SearchRule?.Name);
                sb.Author = ia.GetString(source.SearchRule?.Author);
                sb.BookUrl = ResolveUrl(ia, source.SearchRule?.BookUrl);
                sb.CoverUrl = ResolveUrl(ia, source.SearchRule?.CoverUrl);
                sb.Intro = ia.GetString(source.SearchRule?.Intro);
                sb.LastChapter = ia.GetString(source.SearchRule?.LastChapter);
                if (!string.IsNullOrEmpty(sb.Name) && !string.IsNullOrEmpty(sb.BookUrl)) result.Add(sb);
            }
            return result;
        }

        // ---------------- 详情 ----------------

        public async Task<Book> GetBookInfoAsync(BookSource source, SearchBook found)
        {
            var book = new Book
            {
                Name = found.Name, Author = found.Author, BookUrl = found.BookUrl,
                CoverUrl = found.CoverUrl, Intro = found.Intro, SourceUrl = source.BookSourceUrl
            };
            var rule = source.BookInfoRule;
            if (rule == null) { book.TocUrl = found.BookUrl; return book; }

            var au = new AnalyzeUrl(found.BookUrl, source.BookSourceUrl, source.Header);
            var res = await _fetcher.GetAsync(au);
            DebugLog.Add("info " + au.Url + " -> len=" + (res.Body?.Length ?? 0));

            var ia = new AnalyzeRule(res.Body) { BaseUrl = au.BaseUrl };
            var newName = ia.GetString(rule.Name);
            if (!string.IsNullOrEmpty(newName)) book.Name = newName;
            var newAuthor = ia.GetString(rule.Author);
            if (!string.IsNullOrEmpty(newAuthor)) book.Author = newAuthor;
            book.Intro = ia.GetString(rule.Intro);
            book.CoverUrl = ResolveUrl(ia, rule.CoverUrl) ?? book.CoverUrl;
            book.LastChapter = ia.GetString(rule.LastChapter);
            var tocUrl = ResolveUrl(ia, rule.TocUrl);
            book.TocUrl = string.IsNullOrEmpty(tocUrl) ? found.BookUrl : tocUrl;
            return book;
        }

        // ---------------- 目录（含翻页）----------------

        public async Task<List<BookChapter>> GetChapterListAsync(BookSource source, Book book, int maxPages = 20)
        {
            var chapters = new List<BookChapter>();
            var nextUrl = book.TocUrl;
            var visited = new HashSet<string>();
            var pages = 0;
            while (!string.IsNullOrEmpty(nextUrl) && pages < maxPages && visited.Add(nextUrl))
            {
                pages++;
                var au = new AnalyzeUrl(nextUrl, source.BookSourceUrl, source.Header);
                var res = await _fetcher.GetAsync(au);
                DebugLog.Add("toc[" + pages + "] " + au.Url + " -> len=" + (res.Body?.Length ?? 0));

                var ia = new AnalyzeRule(res.Body) { BaseUrl = au.BaseUrl };
                var items = ia.GetElements(source.TocRule?.ChapterList);
                foreach (var item in items)
                {
                    var ca = new AnalyzeRule(item) { BaseUrl = au.BaseUrl };
                    var ch = new BookChapter
                    {
                        Title = ca.GetString(source.TocRule?.ChapterName),
                        Url = ResolveUrl(ca, source.TocRule?.ChapterUrl),
                        UpdateTime = ca.GetString(source.TocRule?.UpdateTime),
                        IsVolume = !string.IsNullOrEmpty(ca.GetString(source.TocRule?.IsVolume))
                    };
                    if (!string.IsNullOrEmpty(ch.Title) && !string.IsNullOrEmpty(ch.Url)) chapters.Add(ch);
                }
                nextUrl = ResolveUrl(ia, source.TocRule?.NextTocUrl);
            }
            return chapters;
        }

        // ---------------- 正文（含翻页 + 替换）----------------

        public async Task<string> GetContentAsync(BookSource source, BookChapter chapter, int maxPages = 10)
        {
            var sb = new System.Text.StringBuilder();
            var nextUrl = chapter.Url;
            var visited = new HashSet<string>();
            var pages = 0;
            while (!string.IsNullOrEmpty(nextUrl) && pages < maxPages && visited.Add(nextUrl))
            {
                pages++;
                var au = new AnalyzeUrl(nextUrl, source.BookSourceUrl, source.Header);
                var res = await _fetcher.GetAsync(au);
                DebugLog.Add("content[" + pages + "] " + au.Url + " -> len=" + (res.Body?.Length ?? 0));

                var ia = new AnalyzeRule(res.Body) { BaseUrl = au.BaseUrl };
                var content = ia.GetString(source.ContentRule?.Content);
                if (content.Length > 0)
                {
                    if (sb.Length > 0) sb.Append("\n");
                    sb.Append(content);
                }
                nextUrl = ResolveUrl(ia, source.ContentRule?.NextContentUrl);
            }
            var text = sb.ToString().Trim();

            var replaceRegex = source.ContentRule?.ReplaceRegex;
            if (!string.IsNullOrEmpty(replaceRegex) && text.Length > 0)
            {
                // 全局净化替换：pattern replacement
                try
                {
                    var parts = replaceRegex.Split(new[] { "##" }, StringSplitOptions.None);
                    if (parts.Length >= 2)
                        text = System.Text.RegularExpressions.Regex.Replace(text, parts[0], parts[1]);
                    else
                        text = System.Text.RegularExpressions.Regex.Replace(text, parts[0], "");
                }
                catch { /* 净化正则非法忽略 */ }
            }

            // 中文排版惯例：净化后每段首缩进两个全角空格，去空行
            if (text.Length > 0)
            {
                text = string.Join("\n", text
                    .Replace("\r\n", "\n")
                    .Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0)
                    .Select(l => l.StartsWith("　　") ? l : "　　" + l));
            }
            return text;
        }

        private static string ResolveUrl(AnalyzeRule ia, string rule)
        {
            if (string.IsNullOrEmpty(rule)) return "";
            var raw = ia.GetString(rule);
            if (string.IsNullOrEmpty(raw)) return "";
            return AnalyzeUrl.GetAbsoluteUrl(ia.BaseUrl, raw);
        }
    }
}
