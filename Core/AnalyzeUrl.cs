using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace legado_wp.Core
{
    /// <summary>
    /// 搜索/详情/目录/正文 URL 规则解析（对照 Kotlin AnalyzeUrlCore.kt，M2 范围）。
    /// 形如: /search.php,{"method":"POST","body":"kw={{key}}","charset":"gbk"}
    /// M2 边界：@js/&lt;js&gt;/{{js}} 段不执行；webView 不支持。
    /// </summary>
    public class AnalyzeUrl
    {
        private static readonly Regex ParamPattern =
            new Regex(",\\{[\\s\\S]*\\}$", RegexOptions.Compiled);

        public string RawUrl;
        public string Url = "";
        public string UrlNoQuery = "";
        public string EncodedParams;
        public string Body;
        public string Method = "GET";
        public string Charset;
        public string WebViewJs;
        public int Retry;
        public readonly Dictionary<string, string> HeaderMap = new Dictionary<string, string>();
        public string BaseUrl;

        private JObject _option;

        public AnalyzeUrl(string rawUrl, string baseUrl, string sourceHeader = null,
            Dictionary<string, string> variables = null)
        {
            RawUrl = rawUrl;
            BaseUrl = baseUrl ?? "";

            // source 级 headers
            if (!string.IsNullOrEmpty(sourceHeader))
            {
                try
                {
                    var h = JObject.Parse(sourceHeader);
                    foreach (var p in h.Properties()) HeaderMap[p.Name] = p.Value?.ToString() ?? "";
                }
                catch { /* header 非法忽略 */ }
            }

            var tmpUrl = rawUrl ?? "";

            // @js:/<js></js> URL 段（M3：Jint 执行，结果作为 URL）
            if (tmpUrl.Contains("<js>"))
            {
                var mJs = Regex.Match(tmpUrl, "<js>([\\s\\S]*?)</js>");
                if (mJs.Success)
                {
                    var prefix = tmpUrl.Substring(0, mJs.Index);
                    var evaluated = JsEngine.Eval(mJs.Groups[1].Value, prefix, BaseUrl);
                    tmpUrl = (evaluated ?? prefix).ToString();
                }
            }
            if (tmpUrl.StartsWith("@js:"))
            {
                var evaluated = JsEngine.Eval(tmpUrl.Substring(4), tmpUrl, BaseUrl);
                tmpUrl = (evaluated ?? "").ToString();
            }

            // {{key}}/{{page}} 等变量替换（已知变量文本替换，其余 {{js}} 表达式求值）
            if (variables != null)
            {
                foreach (var kv in variables)
                    tmpUrl = tmpUrl.Replace("{{" + kv.Key + "}}", Uri.EscapeDataString(kv.Value ?? ""));
            }
            tmpUrl = ReplaceJsTemplates(tmpUrl);

            var urlNoOption = tmpUrl;
            string urlOptionStr = null;
            var m = ParamPattern.Match(tmpUrl);
            if (m.Success)
            {
                urlNoOption = tmpUrl.Substring(0, m.Index);
                urlOptionStr = tmpUrl.Substring(m.Index + 1);
            }

            Url = GetAbsoluteUrl(BaseUrl, urlNoOption.Trim());
            BaseUrl = GetBaseUrl(Url);

            if (urlOptionStr != null)
            {
                try { _option = JObject.Parse(urlOptionStr); } catch { /* 宽松失败忽略 */ }
                if (_option != null)
                {
                    Method = (_option["method"]?.ToString() ?? "GET").ToUpperInvariant();
                    Charset = _option["charset"]?.ToString();
                    Body = _option["body"]?.ToString();
                    WebViewJs = _option["webJs"]?.ToString();
                    Retry = (int?)_option["retry"] ?? 0;
                    var headers = _option["headers"] as JObject;
                    if (headers != null)
                        foreach (var p in headers.Properties()) HeaderMap[p.Name] = p.Value?.ToString() ?? "";
                }
            }

            UrlNoQuery = Url;
            if (IsPost())
            {
                if (Body != null && !IsJsonBody(Body) && !HeaderMap.ContainsKey("Content-Type"))
                {
                    EncodedParams = EncodeParams(Body, Charset, false);
                }
            }
            else
            {
                var pos = Url.IndexOf('?');
                if (pos != -1)
                {
                    EncodedParams = EncodeParams(Url.Substring(pos + 1), Charset, true);
                    UrlNoQuery = Url.Substring(0, pos);
                }
            }
        }

        public bool IsPost() => Method == "POST";

        /// <summary>剩余 {{expr}} 模板逐个求值替换（M3）。JS 上下文内跳过（java.ajax 同步取页时防锁死锁）。</summary>
        private static string ReplaceJsTemplates(string url)
        {
            if (!url.Contains("{{") || JsEngine.EvalActive) return url;
            var result = url;
            var m = Regex.Match(result, "\\{\\{([^{}]+)\\}\\}");
            var guard = 0;
            while (m.Success && guard++ < 20)
            {
                var v = JsEngine.Eval(m.Groups[1].Value, result, "");
                result = result.Substring(0, m.Index) + (v ?? "") + result.Substring(m.Index + m.Length);
                m = Regex.Match(result, "\\{\\{([^{}]+)\\}\\}");
            }
            return result;
        }

        /// <summary>按 charset 重新 percent-encode 参数串（对照 encodeUrlParams）。</summary>
        internal static string EncodeParams(string text, string charset, bool isQuery)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var enc = GetCharset(charset);
            var sb = new StringBuilder();
            foreach (var pair in text.Split('&'))
            {
                if (sb.Length > 0) sb.Append('&');
                var eq = pair.IndexOf('=');
                if (eq == -1) { sb.Append(PercentEncode(pair, enc)); continue; }
                sb.Append(PercentEncode(pair.Substring(0, eq), enc));
                sb.Append('=');
                sb.Append(PercentEncode(pair.Substring(eq + 1), enc));
            }
            return sb.ToString();
        }

        private static bool _codePagesRegistered;

        internal static Encoding GetCharset(string name)
        {
            if (string.IsNullOrEmpty(name)) return Encoding.UTF8;
            if (!_codePagesRegistered)
            {
                _codePagesRegistered = true;
                try { Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); }
                catch { /* 已注册 */ }
            }
            try { return Encoding.GetEncoding(name); }
            catch { return Encoding.UTF8; }
        }

        internal static string PercentEncode(string s, Encoding enc)
        {
            // 先解一层已有转义再按目标字符集编码（对照 Kotlin URLDecoder.decode + 自编码）
            string decoded;
            try { decoded = Uri.UnescapeDataString(s); }
            catch { decoded = s; }
            var bytes = enc.GetBytes(decoded);
            var sb = new StringBuilder();
            var unreserved = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.~";
            foreach (var b in bytes)
            {
                var c = (char)b;
                if (b < 128 && unreserved.IndexOf(c) >= 0) sb.Append(c);
                else sb.Append('%').Append(b.ToString("X2"));
            }
            return sb.ToString();
        }

        private static bool IsJsonBody(string body)
        {
            var t = body.TrimStart();
            return t.StartsWith("{") || t.StartsWith("[");
        }

        public static string GetAbsoluteUrl(string baseUrl, string rel)
        {
            if (string.IsNullOrEmpty(rel)) return baseUrl ?? "";
            if (rel.StartsWith("http://") || rel.StartsWith("https://") ||
                rel.StartsWith("//", StringComparison.Ordinal))
            {
                return rel.StartsWith("//") ? "https:" + rel : rel;
            }
            try
            {
                if (!string.IsNullOrEmpty(baseUrl))
                    return new Uri(new Uri(baseUrl), rel).ToString();
                return new Uri(rel, UriKind.RelativeOrAbsolute).ToString();
            }
            catch { return rel; }
        }

        public static string GetBaseUrl(string url)
        {
            try
            {
                var u = new Uri(url);
                return u.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
            }
            catch { return ""; }
        }
    }
}
