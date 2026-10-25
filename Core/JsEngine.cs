using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jint;

namespace legado_wp.Core
{
    /// <summary>
    /// Jint 封装：引擎复用 + java/cookie/result/baseUrl 绑定 + fetcher 注入口（测试）。
    /// </summary>
    public static class JsEngine
    {
        private static Engine _engine;
        private static readonly object Lock = new object();

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
