using System;
using System.Collections.Generic;
using System.Linq;

namespace legado_wp.Core
{
    /// <summary>
    /// M1 回归用例：对照 Kotlin 语义的规则引擎断言集。
    /// </summary>
    public static class M1Cases
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

        private static readonly string Json = @"
{ 'code': 0, 'data': { 'books': [
  { 'name': '诡秘之主', 'author': '爱潜水的乌贼', 'url': '/n/201/' },
  { 'name': '赘婿', 'author': '愤怒的香蕉', 'url': '/n/202/' }
]}}";

        private const string SourceJson = @"{
  'bookSourceUrl': 'https://demo.example',
  'bookSourceName': '演示源',
  'searchUrl': '/search?q={{key}}',
  'ruleSearch': {
    'bookList': 'class.bookbox',
    'name': 'tag.h3@tag.a@text',
    'bookUrl': 'tag.h3@tag.a@href',
    'author': 'class.author@text##作者：'
  },
  'ruleContent': { 'content': 'id.content@textNodes' }
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

            // 1. Default 语法：class 定位 + tag + text
            var ar = new AnalyzeRule(Html);
            check("default: class.bookbox@tag.h3@tag.a@text",
                "斗破苍穹", ar.GetString("class.bookbox.0@tag.h3@tag.a@text"));

            // 2. @CSS: （.bookbox a 匹配全部三个 a，@text 全取 join——对齐 Kotlin 语义）
            check("css: .bookbox a@text",
                "斗破苍穹\n凡人修仙传\n遮天", ar.GetString("@css:.bookbox a@text"));

            // 3. attr 取值 href
            check("default: href",
                "/book/101", ar.GetString("class.bookbox.0@tag.h3@tag.a@href"));

            // 4. 老式索引 .-1（最后一个 bookbox）
            check("index: .-1",
                "遮天", ar.GetString("class.bookbox.-1@tag.h3@tag.a@text"));

            // 5. 排除索引 !0
            var names = ar.GetStringList("class.bookbox!0@tag.h3@tag.a@text");
            check("index: !0 列表数", "2", names.Count.ToString());
            check("index: !0 首项", "凡人修仙传", names.Count > 0 ? names[0] : "");

            // 6. || 合并（首段空则取次段——空段兜底语义）
            check("merge: || 空段兜底",
                "斗破苍穹", ar.GetString("class.none@text||class.bookbox.0@tag.h3@tag.a@text"));

            // 7. ## 替换
            check("replace: ##作者：",
                "天蚕土豆", ar.GetString("class.bookbox.0@class.author@text##作者："));

            // 8. && 合并（拼接）
            var joined = ar.GetString("class.bookbox.0@class.author@text&&class.bookbox.0@class.intro@text");
            check("merge: && 拼接", "作者：天蚕土豆\n少年热血的传奇故事。", joined);

            // 9. JSON：$. 逐层
            var arj = new AnalyzeRule(Json);
            check("json: $.data.books[0].name",
                "诡秘之主", arj.GetString("$.data.books[0].name"));

            // 10. JSON 列表 + 逐项
            var books = arj.GetElements("$.data.books[*]");
            check("json: books 数量", "2", books.Count.ToString());
            check("json: 列表项 name",
                "赘婿", books.Count > 1 ? arj.GetStringFrom(books[1], "$.name") : "");

            // 11. @Json: 显式
            check("json: @Json: 显式前缀",
                "愤怒的香蕉", arj.GetString("@json:$.data.books[1].author"));

            // 12. HTML 实体反转义
            var are = new AnalyzeRule("<p>斗破&amp;苍穹</p>");
            check("unescape: &amp;",
                "斗破&苍穹", are.GetString("tag.p@text"));

            // 13. 书源 JSON 反序列化
            var src = BookSource.FromJson(SourceJson.Replace('\'', '"'));
            check("source: bookSourceName", "演示源", src.BookSourceName);
            check("source: searchUrl", "/search?q={{key}}", src.SearchUrl);
            check("source: ruleSearch.bookList", "class.bookbox", src.SearchRule.BookList);
            check("source: ruleContent.content", "id.content@textNodes", src.ContentRule.Content);

            // 14. 模拟搜索链路：bookList → 列表项 → 字段
            var items = ar.GetElements(src.SearchRule.BookList);
            check("pipeline: bookList 数量", "3", items.Count.ToString());
            var first = items.Count > 0 ? items[0] : null;
            var nameR = new AnalyzeRule(first);
            check("pipeline: name",
                "斗破苍穹", nameR.GetString(src.SearchRule.Name));
            var urlR = new AnalyzeRule(first);
            check("pipeline: bookUrl",
                "/book/101", urlR.GetString(src.SearchRule.BookUrl));
            var authorR = new AnalyzeRule(first);
            check("pipeline: author(##)",
                "天蚕土豆", authorR.GetString(src.SearchRule.Author));

            // 15. textNodes（正文常见取法：直接文本节点全部拼接）
            var artn = new AnalyzeRule("<div id='content'>第一段<br/>第二段文字</div>");
            check("textNodes", "第一段\n第二段文字", artn.GetString("id.content@textNodes"));

            // 16. 正则模式：: 前缀语法取捕获组
            var arrx = new AnalyzeRule("开篇词…… 第12章 风起云涌 正文内容");
            check("regex: :第(\\d+)章 取组",
                "12", arrx.GetString(":第(\\d+)章"));

            log.Insert(0, "[M1] pass=" + pass + " fail=" + fail +
                (fail == 0 ? "  全部通过" : "  存在失败"));
            return log;
        }
    }
}
