using System;
using System.Collections.Generic;
using System.Linq;

namespace legado_wp.Core
{
    /// <summary>
    /// 0.0.12.0 XPath 回归用例：迷你 XPath 引擎 + AnalyzeRule @XPath: 接入。
    /// </summary>
    public static class XPathCases
    {
        private static readonly string Html = @"
<html><body>
<div class='bookbox'>
  <div class='title'><h3><a href='/book/101'>斗破苍穹</a></h3></div>
  <p class='author'>作者：天蚕土豆</p>
  <p class='intro'>少年热血的传奇故事。</p>
</div>
<div class='bookbox'>
  <div class='title'><h3><a href='/book/102'>凡人修仙传</a></h3></div>
  <p class='author'>作者：忘语</p>
  <p class='intro'>凡人流开山之作。</p>
</div>
<div class='bookbox'>
  <div class='title'><h3><a href='/book/103'>遮天</a></h3></div>
  <p class='author'>作者：辰东</p>
  <p class='intro'>九龙拉棺震撼开篇。</p>
</div>
</body></html>";

        /// <summary>真实书源风格的 @XPath: 规则（搜索页）。</summary>
        private const string XSourceJson = @"{
  'bookSourceUrl': 'https://xdemo.example',
  'bookSourceName': 'XPath演示源',
  'searchUrl': '/s?q={{key}}',
  'ruleSearch': {
    'bookList': '@XPath://div[@class=""bookbox""]',
    'name': '@XPath://div[@class=""title""]/h3/a/text()',
    'bookUrl': '@XPath://div[@class=""title""]/h3/a/@href',
    'author': '@XPath://p[contains(@class,""author"")]/text()##作者：'
  },
  'ruleContent': { 'content': '@XPath://div[@id=""content""]/text()' }
}";

        public static List<string> Run()
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
            Action<string, int, int> checkN = (label, expect, actual) =>
                check(label, expect.ToString(), actual.ToString());

            // ---- 基础定位 ----
            var xp = new AnalyzeByXPath(Html);
            check(" descendant text", "斗破苍穹",
                xp.GetString("//div[@class='title'][1]/h3/a/text()"));
            check(" attr 取值", "/book/101",
                xp.GetString("//div[@class='title'][1]/h3/a/@href"));
            check(" 单斜杠子轴", "作者：天蚕土豆\n作者：忘语\n作者：辰东",
                new AnalyzeByXPath(Html).GetString("/html/body/div/p[@class='author']/text()"));

            // ---- 谓词 ----
            check(" [n] 索引", "凡人修仙传", xp.GetString("//div[@class='title'][2]/h3/a/text()"));
            check(" [last()]", "遮天", xp.GetString("//div[@class='title'][last()]/h3/a/text()"));
            check(" contains(@class) 作者", "作者：辰东",
                xp.GetString("//p[contains(@class,'auth')][3]/text()"));
            check(" text()= 精确匹配", "凡人流开山之作。",
                xp.GetString("//p[text()='凡人流开山之作。']/text()"));

            // ---- 列表与元素 ----
            checkN(" bookList 元素数", 3, xp.GetElements("//div[@class='bookbox']").Count);
            var texts = xp.GetStringList("//p[@class='intro']/text()");
            checkN(" text 列表行数", 3, texts.Count);
            check(" text 列表内容", "九龙拉棺震撼开篇。", texts.LastOrDefault() ?? "");

            // ---- 并集 ----
            var uni = xp.GetStringList("//p[@class='author']/text() | //p[@class='intro'][1]/text()");
            checkN(" | 并集行数", 4, uni.Count);

            // ---- 通过 AnalyzeRule 全链路（@XPath: 前缀 + 书源规则）----
            var source = BookSource.FromJson(XSourceJson);

            var ar = new AnalyzeRule(Html, "https://xdemo.example");
            check(" AnalyzeRule @XPath: name", "斗破苍穹",
                ar.GetString("@XPath://div[@class='title'][1]/h3/a/text()"));
            check(" AnalyzeRule @XPath: href", "/book/101",
                ar.GetString("@XPath://div[@class='title'][1]/h3/a/@href"));

            // 列表元素逐项取字段（搜索 bookList → name/author 模式）
            var arList = new AnalyzeRule(Html, "https://xdemo.example");
            var books = arList.GetElements("@XPath://div[@class='bookbox']");
            checkN(" AnalyzeRule bookList 数", 3, books.Count);
            check(" 元素内取 name", "凡人修仙传",
                arList.GetStringFrom(books[1], "@XPath://h3/a/text()"));
            check(" 元素内取 href", "/book/102",
                arList.GetStringFrom(books[1], "@XPath://h3/a/@href"));
            check(" 元素内 author+净化", "作者：辰东",
                arList.GetStringFrom(books[2], "@XPath://p[contains(@class,'author')]/text()"));

            // 书源 JSON 规则跑通（GetString 间接验证模式识别不误判 XPath 为 Json）
            // 文档含 3 个 bookbox，多结果按 legado 语义 \n 拼接
            var arSrc = new AnalyzeRule(Html, "https://xdemo.example");
            check(" 书源 name 规则", "斗破苍穹\n凡人修仙传\n遮天",
                arSrc.GetString(source.SearchRule.Name));
            check(" 书源 bookUrl 规则", "/book/101\n/book/102\n/book/103",
                arSrc.GetString(source.SearchRule.BookUrl));
            check(" 书源 author 规则+净化", "天蚕土豆\n忘语\n辰东",
                arSrc.GetString(source.SearchRule.Author));

            log.Add($"[XPath 汇总] pass={pass} fail={fail}");
            return log;
        }
    }
}
