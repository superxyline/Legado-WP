using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jint;

namespace legado_wp.Core
{
    /// <summary>
    /// Jint 封装：引擎复用 + java/cookie/result/baseUrl/source 绑定 + fetcher 注入口（测试）。
    /// 0.0.13.0：书源 jsLib 共享作用域（按源缓存的独立 Engine）+ CurrentSource（java.getVariable）。
    /// </summary>
    public static class JsEngine
    {
        private static Engine _engine;
        private static readonly object Lock = new object();

        /// <summary>书源专用引擎缓存：key = bookSourceUrl + '#' + jsLib 内容哈希。</summary>
        private static readonly Dictionary<string, Engine> SourceEngines = new Dictionary<string, Engine>();

        /// <summary>当前正在执行的书源（java.getVariable/setVariable 用；全局锁下安全）。</summary>
        public static BookSource CurrentSource { get; private set; }

        /// <summary>测试注入的取页器；null 时用真实 HttpClient。</summary>
        public static IPageFetcher FetcherOverride;

        internal static Task<StrResponse> FetchAsync(AnalyzeUrl au)
        {
            return (FetcherOverride ?? HttpPageFetcher.Default).GetAsync(au);
        }

        private static Engine GetEngine()
        {
            if (_engine == null)
            {
                _engine = new Engine(cfg => cfg.TimeoutInterval(TimeSpan.FromSeconds(20)));
                _engine.SetValue("java", new JsExtensions());
                _engine.SetValue("cookie", new CookieBridge());
            }
            return _engine;
        }

        /// <summary>
        /// 执行 JS。变量按 bindings 注入（key/result/baseUrl/book/source…），返回 CLR 化结果。
        /// </summary>
        public static object Eval(string js, object result = null,
            string baseUrl = null, Dictionary<string, object> vars = null)
        {
            if (string.IsNullOrEmpty(js)) return null;
            lock (Lock)
            {
                Engine engine;
                try
                {
                    engine = GetEngine();
                }
                catch
                {
                    return null;
                }
                engine.SetValue("result", result);
                engine.SetValue("baseUrl", baseUrl ?? "");
                if (vars != null)
                    foreach (var kv in vars) engine.SetValue(kv.Key, kv.Value);

                try
                {
                    var v = engine.Execute(js).GetCompletionValue();
                    return Unwrap(v, engine);
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// 带书源上下文执行：jsLib 预载进共享作用域（按源缓存），绑定 source 变量。
        /// 供规则里的 {{js}}/&lt;js&gt;/@js: 与书源 JS 函数库联动。
        /// </summary>
        public static object EvalWithSource(string js, object result, string baseUrl, BookSource source)
        {
            if (string.IsNullOrEmpty(js)) return null;
            lock (Lock)
            {
                try
                {
                    var engine = GetSourceEngine(source);
                    CurrentSource = source;
                    engine.SetValue("result", result);
                    engine.SetValue("baseUrl", baseUrl ?? "");
                    engine.SetValue("source", source);
                    try
                    {
                        var v = engine.Execute(js).GetCompletionValue();
                        return Unwrap(v, engine);
                    }
                    finally
                    {
                        CurrentSource = null;
                    }
                }
                catch
                {
                    CurrentSource = null;
                    return null;
                }
            }
        }

        /// <summary>书源引擎：jsLib 变化（lastUpdateTime/hash）时重建。</summary>
        private static Engine GetSourceEngine(BookSource source)
        {
            var jsLib = source?.JsLib ?? "";
            var key = (source?.BookSourceUrl ?? "") + "#" + Hash(jsLib);
            Engine engine;
            if (!SourceEngines.TryGetValue(key, out engine))
            {
                engine = new Engine(cfg => cfg.TimeoutInterval(TimeSpan.FromSeconds(20)));
                engine.SetValue("java", new JsExtensions());
                engine.SetValue("cookie", new CookieBridge());
                if (jsLib.Length > 0)
                {
                    try { engine.Execute(jsLib); }
                    catch { /* jsLib 本身报错：后续调用退化为无库执行 */ }
                }
                // 缓存上限保护（老机器内存）：超过 32 个源重置
                if (SourceEngines.Count >= 32) SourceEngines.Clear();
                SourceEngines[key] = engine;
            }
            return engine;
        }

        private static string Hash(string s)
        {
            if (s == null || s.Length == 0) return "0";
            unchecked
            {
                uint h = 2166136261;
                foreach (var c in s) { h ^= c; h *= 16777619; }
                return h.ToString("x8");
            }
        }

        /// <summary>JsValue → CLR（String/Double/Bool/对象 ObjectWrapper 原样）。</summary>
        private static object Unwrap(Jint.Native.JsValue v, Engine engine)
        {
            if (v == null || v.IsNull() || v.IsUndefined()) return null;
            if (v.IsString()) return v.AsString();
            if (v.IsBoolean()) return v.AsBoolean();
            if (v.IsNumber())
            {
                var d = v.AsNumber();
                return Math.Abs(d % 1.0) < 0.0000001 ? (object)(long)d : d;
            }
            return v.ToObject();
        }

        /// <summary>cookie 桥（java.getCookie 之外的 cookie.get 形式）。</summary>
        public class CookieBridge
        {
            public string get(string url) { string v; return CookieStore.TryGet(url, out v) ? v : ""; }
            public string get(string url, string key) { return get(url); }
        }
    }
}
