using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace legado_wp.Core
{
    /// <summary>
    /// M3 回归用例：{{js}}/<js> 段执行 + java.* 桥核心集（哈希/编码/AES//ajax 模板）。
    /// </summary>
    public static class M3Cases
    {
        private class MockFetcher : IPageFetcher
        {
            public string LastUrl;
            public string Body = "MOCK_AJAX_BODY";
            public Task<StrResponse> GetAsync(AnalyzeUrl au)
            {
                LastUrl = au.Url;
                return Task.FromResult(new StrResponse(au.Url, Body));
            }
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

            // ---------- {{js}} 内嵌表达式（规则串拼接） ----------

            var ar = new AnalyzeRule("第12章");
            check("js: {{}} 表达式拼接",
                "第12章_标题", ar.GetString("{{result + \"_标题\"}}"));

            var ar2 = new AnalyzeRule("abc");
            check("js: {{}} 纯静态计算",
                "7", ar2.GetString("{{3 + 4}}"));

            // ---------- <js></js> 独立段 ----------

            var ar3 = new AnalyzeRule("<p>hello</p>");
            check("js: <js> 段处理 result",
                "HELLO", ar3.GetString("tag.p@text<js>result.toUpperCase()</js>"));

            // 混排：静态段 + js 段链式（先取 text 再 js 加工）
            var ar4 = new AnalyzeRule("<p>2</p>");
            check("js: 取值→js 计算",
                "4", ar4.GetString("tag.p@text<js>String(Number(result) * 2)</js>"));

            // ---------- java.* 桥 ----------

            var ar5 = new AnalyzeRule("x");
            check("java: md5Encode",
                "e10adc3949ba59abbe56e057f20f883e", ar5.GetString("{{java.md5Encode(\"123456\")}}"));
            check("java: base64Encode/Decode",
                "legado", ar5.GetString("{{java.base64Decode(java.base64Encode(\"legado\"))}}"));
            check("java: hexEncodeToString",
                "6c656761646f", ar5.GetString("{{java.hexEncodeToString(\"legado\")}}"));
            check("java: digestHex(SHA-256)",
                "c848c83a14853821592f9ec571c3ee23caa985a2ebe93b8d3185be3e9d650051",
                ar5.GetString("{{java.digestHex(\"legado\", \"SHA-256\")}}"));
            // HMac 精确值断言（HMACMD5 legado/key），带 JS 侧错误捕获
            var hmacDbg = (string)JsEngine.Eval(
                "(function(){ try { return java.HMacHex('legado', 'HmacMD5', 'key'); }" +
                " catch(e) { return 'ERR:' + e.message; } })()");
            log.Add("[dbg] HMac via JS = " + hmacDbg);
            var hmacActual = hmacDbg != null && !hmacDbg.StartsWith("ERR") ? hmacDbg : "";
            check("java: HMacHex 真值",
                "8225f0aa25e3cc257ee27445fa3f1b3f", hmacActual);

            // ---------- AES roundtrip ----------

            var aesRound = (string)JsEngine.Eval(@"
                var c = java.createSymmetricCrypto('aes', 'cbc/pkcs5padding', '1234567890123456', '6543210987654321');
                var enc = c.encryptHex('阅读3.0 WP10 移植');
                c.decryptStr(enc);
            ");
            check("java: AES CBC roundtrip", "阅读3.0 WP10 移植", aesRound ?? "");

            // ---------- ajax（mock fetcher 注入） ----------

            var mock = new MockFetcher();
            JsEngine.FetcherOverride = mock;
            try
            {
                var ajaxBody = (string)JsEngine.Eval("java.ajax('https://demo.example/page')");
                check("java: ajax 返回内容", "MOCK_AJAX_BODY", ajaxBody ?? "");

                var ajaxInRule = new AnalyzeRule("x");
                check("js: 规则内 java.ajax",
                    "MOCK_AJAX_BODY",
                    ajaxInRule.GetString("{{java.ajax('https://demo.example/page')}}"));

                var conn = JsEngine.Eval("var r = java.get('https://demo.example/api'); r.code + ':' + r.body;");
                check("java: connect/get 简化响应", "200:MOCK_AJAX_BODY", conn == null ? "" : conn.ToString());
            }
            finally
            {
                JsEngine.FetcherOverride = null;
            }

            // ---------- AnalyzeUrl 的 {{js}} / @js: ----------

            var au1 = new AnalyzeUrl("https://a.example/s?sign={{java.md5Encode('k').substring(0,8)}}", "");
            check("url: {{js}} 求值",
                "https://a.example/s?sign=8ce4b16b", au1.Url);

            var au2 = new AnalyzeUrl("@js:'https://b.example/n/' + java.md5Encode('1').substring(0,4);", "");
            check("url: @js: 前缀", "https://b.example/n/c4ca", au2.Url);

            // ---------- WebBook 含 JS 规则书源全链路（mock） ----------

            var fetcher2 = new MockFetcher { Body = SearchBody };
            JsEngine.FetcherOverride = fetcher2;
            try
            {
                var src = new BookSource
                {
                    BookSourceUrl = "https://demo.example",
                    SearchUrl = "https://demo.example/search?kw={{key}}",
                    SearchRule = new SearchRule
                    {
                        BookList = "class.list@div.item",
                        Name = "tag.a@text<js>result.replace('【精】','')</js>",
                        BookUrl = "tag.a@href"
                    }
                };
                var wb = new WebBook(fetcher2);
                var books = await wb.SearchBookAsync(src, "斗");
                check("web: js 规则书源搜索", "斗破苍穹", books.Count > 0 ? books[0].Name : "无结果");
            }
            finally
            {
                JsEngine.FetcherOverride = null;
            }

            log.Insert(0, "[M3] pass=" + pass + " fail=" + fail +
                (fail == 0 ? "  全部通过" : "  存在失败"));
            return log;
        }

        private const string SearchBody = @"
<html><body><div class='list'>
<div class='item'><a href='https://demo.example/b1'>【精】斗破苍穹</a></div>
<div class='item'><a href='https://demo.example/b2'>【精】凡人修仙传</a></div>
</div></body></html>";
    }
}
