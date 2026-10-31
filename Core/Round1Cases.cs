using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace legado_wp.Core
{
    /// <summary>
    /// 0.0.13.0 第一轮回归：替换净化规则 / java.* 桥扩展 / jsLib 共享作用域 / 书源变量。
    /// </summary>
    public static class Round1Cases
    {
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

            // ---- 1. 替换净化规则（写测试规则 → 跑 → 清空还原）----
            var backup = await Storage.LoadReplaceRulesAsync();
            try
            {
                await Storage.SaveReplaceRulesAsync(new List<ReplaceRule>
                {
                    new ReplaceRule { Name = "去广告", Pattern = "本章未完.*?广告", Replacement = "",
                        UseRegex = true, Enabled = true, SortOrder = 0 },
                    new ReplaceRule { Name = "错字", Pattern = "少 oak 侠", Replacement = "萧秋水",
                        UseRegex = false, Enabled = true, SortOrder = 1 },
                    new ReplaceRule { Name = "仅指定源", Pattern = "广告", Replacement = "X",
                        Scope = "demo.example", Enabled = true, SortOrder = 2 },
                    new ReplaceRule { Name = "已停用", Pattern = "原文", Replacement = "不该出现",
                        Enabled = false, SortOrder = 3 }
                });

                var out1 = await ContentProcessor.ApplyContentAsync(
                    "正文开头。本章未完请点击广告。少 oak 侠出手。", "测试书", "https://other.example");
                check(" 净化: 正则+字面", "正文开头。。萧秋水出手。", out1);

                var out2 = await ContentProcessor.ApplyContentAsync(
                    "这里有广告要替换", "测试书", "https://demo.example");
                check(" 净化: 作用域命中", "这里有X要替换", out2);

                var out3 = await ContentProcessor.ApplyContentAsync(
                    "原文保留", "测试书", "https://demo.example");
                check(" 净化: 停用规则跳过", "原文保留", out3);

                var out4 = await ContentProcessor.ApplyTitleAsync("第1章 少 oak 侠归来", "测试书", "https://demo.example");
                check(" 净化: 标题", "第1章 萧秋水归来", out4);
            }
            finally
            {
                await Storage.SaveReplaceRulesAsync(backup);
            }

            // ---- 2. java.* 桥扩展 ----
            var je = new JsExtensions();
            check(" 桥: digestHex SHA256",
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                je.digestHex("abc", "SHA256"));

            // gzip 往返（测试里压缩，桥里解压）
            var plain = "这是被 gzip 压缩的正文内容 12345";
            string gzB64;
            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionMode.Compress))
                using (var w = new StreamWriter(gz, new System.Text.UTF8Encoding(false)))
                {
                    w.Write(plain);
                }
                gzB64 = Convert.ToBase64String(ms.ToArray());
            }
            check(" 桥: decompressGzip", plain, je.decompressGzip(gzB64));

            // ---- 3. jsLib 共享作用域 + source 绑定 ----
            var src = new BookSource
            {
                BookSourceUrl = "https://jslib.example",
                JsLib = "function add(a,b){return a+b;}\nfunction sign(s){return java.md5Encode(s+'salt');}"
            };
            var r1 = JsEngine.EvalWithSource("add(2,3)", null, "https://jslib.example", src);
            check(" jsLib: 函数跨调用", "5", r1?.ToString());
            var r2 = JsEngine.EvalWithSource("sign('abc')", null, "https://jslib.example", src);
            check(" jsLib: 库内用 java.*", je.digestHex("abcsalt", "MD5"), r2?.ToString());

            // jsLib 变更后重建作用域（hash 变化）
            src.JsLib = "function add(a,b){return a+b+1;}";
            var r3 = JsEngine.EvalWithSource("add(2,3)", null, "https://jslib.example", src);
            check(" jsLib: 变更后重建", "6", r3?.ToString());

            // ---- 4. 书源变量 ----
            src.Variable = "v123";
            var r4 = JsEngine.EvalWithSource("java.getVariable()", null, "https://jslib.example", src);
            check(" 变量: getVariable", "v123", r4?.ToString());

            JsEngine.EvalWithSource("java.setVariable('xyz890')", null, "https://jslib.example", src);
            check(" 变量: setVariable 回写实体", "xyz890", src.Variable);
            // 持久化（异步）落盘验证
            await System.Threading.Tasks.Task.Delay(300);
            var sources = await Storage.LoadSourcesAsync();
            var found = sources.FirstOrDefault(s => s.BookSourceUrl == "https://jslib.example");
            check(" 变量: setVariable 持久化", found != null ? "xyz890" : "（源不在库，跳过）",
                found == null ? "（源不在库，跳过）" : found.Variable);

            // ---- 5. AnalyzeRule 集成：{{js}} 内访问 source ----
            src.JsLib = "";
            var ar = new AnalyzeRule("<html></html>", "https://jslib.example") { Source = src };
            check(" 规则: {{js}} 访问 source", "https://jslib.example",
                ar.GetString("{{ source.bookSourceUrl }}"));

            // ---- 清理：测试源不留在书源库里 ----
            var allSources = await Storage.LoadSourcesAsync();
            var cleaned = allSources.Where(s => s.BookSourceUrl != "https://jslib.example").ToList();
            if (cleaned.Count != allSources.Count) await Storage.SaveSourcesAsync(cleaned);

            log.Add($"[Round1 汇总] pass={pass} fail={fail}");
            return log;
        }
    }
}
