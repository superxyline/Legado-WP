using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace legado_wp.Core
{
    /// <summary>
    /// M2 回归用例：AnalyzeUrl 断言 + mock 全链路（搜索→详情→目录翻页→正文翻页）+ 真实 HTTP 探测。
    /// </summary>
    public static class M2Cases
    {
        private class MockFetcher : IPageFetcher
        {
            public readonly Dictionary<string, string> Pages = new Dictionary<string, string>();
            public Task<StrResponse> GetAsync(AnalyzeUrl au)
            {
                string body;
                if (Pages.TryGetValue(au.Url, out body)) return Task.FromResult(new StrResponse(au.Url, body));
                if (au.IsPost() && Pages.TryGetValue(au.UrlNoQuery, out body))
                    return Task.FromResult(new StrResponse(au.UrlNoQuery, body));
                return Task.FromResult(new StrResponse(au.Url, "<html>404</html>", 404));
            }
        }

        private static readonly string SearchPage = @"
<html><body><div class='result-list'>
<div class='item'><h3><a href='https://demo.example/book/1.html'>斗破苍穹</a></h3>
  <span class='author'>天蚕土豆</span><p class='intro'>少年热血。</p></div>
<div class='item'><h3><a href='https://demo.example/book/2.html'>斗罗大陆</a></h3>
  <span class='author'>唐家三少</span><p class='intro'>武魂世界。</p></div>
</div></body></html>";

        private static readonly string InfoPage = @"
<html><body><div id='maininfo'>
<h1>斗破苍穹</h1><p class='info'>作者：天蚕土豆</p>
<p class='intro'>这里是属于斗气的世界。</p>
<a id='list' href='/book/1/list.html'>查看目录</a>
<span class='last'>第一千章 大结局</span>
</div></body></html>";

        private static readonly string TocPage1 = @"
<html><body><div class='listmain'>
<dl><dd><a href='/book/1/101.html'>第1章 陨落的天才</a></dd>
<dd><a href='/book/1/102.html'>第2章 斗气大陆</a></dd></dl>
<a class='next' href='/book/1/list_2.html'>下一页</a>
</div></body></html>";

        private static readonly string TocPage2 = @"
<html><body><div class='listmain'>
<dl><dd><a href='/book/1/103.html'>第3章 异界火</a></dd></dl>
</div></body></html>";

        private static readonly string ContentPage1 = @"
<html><body><div id='content'>“斗之力，三段！”<br/><br/>少年低声呢喃。（本章完）
<a id='next' href='/book/1/101_2.html'>下一页</a></div></body></html>";

        private static readonly string ContentPage2 = @"
<html><body><div id='content'>片刻之后，少年的目光缓缓抬起。<br/><br/>（本章完）</div></body></html>";

        private static BookSource DemoSource()
        {
            return new BookSource
            {
                BookSourceUrl = "https://demo.example",
                BookSourceName = "mock 源",
                SearchUrl = "https://demo.example/search?kw={{key}}",
                SearchRule = new SearchRule
                {
                    BookList = "class.result-list@div.item",
                    Name = "tag.h3@tag.a@text",
                    BookUrl = "tag.h3@tag.a@href",
                    Author = "class.author@text",
                    Intro = "class.intro@text"
                },
                BookInfoRule = new BookInfoRule
                {
                    Name = "id.maininfo@tag.h1@text",
                    Author = "class.info@text##作者：",
                    Intro = "class.intro@text",
                    TocUrl = "id.list@href",
                    LastChapter = "class.last@text"
                },
                TocRule = new TocRule
                {
                    ChapterList = "class.listmain@dd",
                    ChapterName = "tag.a@text",
                    ChapterUrl = "tag.a@href",
                    NextTocUrl = "class.next@href"
                },
                ContentRule = new ContentRule
                {
                    Content = "id.content@textNodes##（本章完）##",
                    NextContentUrl = "id.next@href",
                    ReplaceRegex = "“|”"
                }
            };
        }

        public static async Task<List<string>> Run()
        {
            var log = new List<string>();
            var pass = 0; var fail = 0;
            Action<string, string, string> check = (label, expect, actual) =>
            {
                var ok = expect == actual;
                if (ok) pass++; else fail++;
                log.Add((ok ? "[PASS] " : "[FAIL] ") + label +
                        (ok ? "" : "  期望=[" + expect + "] 实际=[" + actual + "]"));
            };

            // ---------- AnalyzeUrl 静态断言 ----------

            var au1 = new AnalyzeUrl("https://a.example/s?wd=x", "https://a.example");
            check("url: 纯 GET method", "GET", au1.Method);
            check("url: query 重编码", "wd=x", au1.EncodedParams ?? "");
            check("url: urlNoQuery", "https://a.example/s", au1.UrlNoQuery);

            var au2 = new AnalyzeUrl("/search.php,{\"method\":\"POST\",\"body\":\"kw={{key}}\",\"charset\":\"gbk\"}",
                "https://b.example", null, new Dictionary<string, string> { { "key", "斗破" } });
            check("url: POST method", "POST", au2.Method);
            check("url: 相对合并", "https://b.example/search.php", au2.Url);
            check("url: charset", "gbk", au2.Charset);
            check("url: body 变量替换+gbk编码", "kw=%B6%B7%C6%C6", au2.EncodedParams);

            var au3 = new AnalyzeUrl("https://c.example/api,{\"headers\":{\"Token\":\"abc\"},\"method\":\"POST\",\"body\":\"{\\\"q\\\":1}\"}", "");
            check("url: option headers", "abc", au3.HeaderMap.TryGetValue("Token", out var t) ? t : "");
            check("url: json body 不做 form 编码", "{\"q\":1}", au3.Body ?? "");
            check("url: json body EncodedParams 空", "", au3.EncodedParams ?? "");

            // ---------- mock 全链路 ----------

            var fetcher = new MockFetcher();
            fetcher.Pages["https://demo.example/search?kw=%E6%96%97%E7%A0%B4"] = SearchPage;
            fetcher.Pages["https://demo.example/book/1.html"] = InfoPage;
            fetcher.Pages["https://demo.example/book/1/list.html"] = TocPage1;
            fetcher.Pages["https://demo.example/book/1/list_2.html"] = TocPage2;
            fetcher.Pages["https://demo.example/book/1/101.html"] = ContentPage1;
            fetcher.Pages["https://demo.example/book/1/101_2.html"] = ContentPage2;

            var src = DemoSource();
            var wb = new WebBook(fetcher);

            // 搜索
            var books = await wb.SearchBookAsync(src, "斗破");
            check("web: 搜索结果数", "2", books.Count.ToString());
            check("web: 书名", "斗破苍穹", books.Count > 0 ? books[0].Name : "");
            check("web: 作者", "天蚕土豆", books.Count > 0 ? books[0].Author : "");
            check("web: bookUrl 绝对化", "https://demo.example/book/1.html",
                books.Count > 0 ? books[0].BookUrl : "");

            // 详情
            var book = await wb.GetBookInfoAsync(src, books[0]);
            check("web: 详情作者(##替换)", "天蚕土豆", book.Author);
            check("web: tocUrl 取链接", "https://demo.example/book/1/list.html", book.TocUrl);
            check("web: lastChapter", "第一千章 大结局", book.LastChapter ?? "");

            // 目录（2 页拼接）
            var chapters = await wb.GetChapterListAsync(src, book);
            check("web: 目录翻页总数", "3", chapters.Count.ToString());
            check("web: 章节名", "第3章 异界火", chapters.Count > 2 ? chapters[2].Title : "");
            check("web: 章节 URL", "https://demo.example/book/1/103.html",
                chapters.Count > 2 ? chapters[2].Url : "");

            // 正文（翻页 + ## 替换 + 净化 replaceRegex 去引号）
            var content = await wb.GetContentAsync(src, chapters[0]);
            check("web: 正文含两页", "True",
                (content.Contains("斗之力") && content.Contains("片刻之后")).ToString());
            check("web: ## 净化（本章完）", "False", content.Contains("本章完").ToString());
            check("web: 引号净化 replaceRegex", "False", content.Contains("“").ToString());

            // ---------- 真实 HTTP 探测（记录不判分） ----------
            try
            {
                var realAu = new AnalyzeUrl("https://www.baidu.com/s?wd=test", "https://www.baidu.com");
                var realRes = await HttpPageFetcher.Default.GetAsync(realAu);
                log.Add("[probe] baidu 搜索页 -> code=" + realRes.Code + " len=" + (realRes.Body?.Length ?? 0));
            }
            catch (Exception ex)
            {
                log.Add("[probe] baidu EX " + ex.GetType().Name + ": " + ex.Message);
            }

            log.Insert(0, "[M2] pass=" + pass + " fail=" + fail +
                (fail == 0 ? "  全部通过" : "  存在失败"));
            return log;
        }
    }
}
