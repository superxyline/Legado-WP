using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace legado_wp.Core
{
    /// <summary>响应（对照 Kotlin StrResponse）。</summary>
    public class StrResponse
    {
        public string Url;
        public string Body;
        public int Code;
        public bool IsSuccess => Code >= 200 && Code < 300;
        public StrResponse(string url, string body, int code = 200) { Url = url; Body = body; Code = code; }
    }

    /// <summary>
    /// WebBook 取页抽象：mock 用例注入本地内容，真实实现走 HttpClient。
    /// </summary>
    public interface IPageFetcher
    {
        Task<StrResponse> GetAsync(AnalyzeUrl au);
    }

    /// <summary>HttpClient 实现：GET/POST/form/json/charset 解码/headers/UA。</summary>
    public class HttpPageFetcher : IPageFetcher
    {
        public static readonly HttpPageFetcher Default = new HttpPageFetcher();
        private readonly HttpClient _client;

        static HttpPageFetcher()
        {
            try { Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); }
            catch { /* 已注册 */ }
        }

        public HttpPageFetcher()
        {
            var handler = new HttpClientHandler
            {
                UseCookies = false, // 手动管理：Set-Cookie 进 CookieStore，请求回带（java.getCookie 可见）
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            };
            _client = new HttpClient(handler);
            _client.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/86.0.4240.198 Safari/537.36");
        }

        public async Task<StrResponse> GetAsync(AnalyzeUrl au)
        {
            var req = new HttpRequestMessage(
                au.IsPost() ? HttpMethod.Post : HttpMethod.Get, au.IsPost() ? au.UrlNoQuery : au.Url);

            foreach (var kv in au.HeaderMap)
                req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);

            // 回带 Cookie（按 host）
            try
            {
                var host = new Uri(au.Url).Host;
                string cookie;
                if (CookieStore.TryGet(host, out cookie) && !string.IsNullOrEmpty(cookie))
                    req.Headers.TryAddWithoutValidation("Cookie", cookie);
            }
            catch { }

            if (au.IsPost())
            {
                var contentTypeForm = "application/x-www-form-urlencoded";
                if (au.Body != null && (au.Body.TrimStart().StartsWith("{") || au.Body.TrimStart().StartsWith("[")))
                {
                    req.Content = new StringContent(au.Body, Encoding.UTF8, "application/json");
                }
                else if (!string.IsNullOrEmpty(au.EncodedParams))
                {
                    var enc = AnalyzeUrl.GetCharset(au.Charset);
                    req.Content = new ByteArrayContent(enc.GetBytes(au.EncodedParams));
                    req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentTypeForm)
                    {
                        CharSet = string.IsNullOrEmpty(au.Charset) ? "utf-8" : au.Charset
                    };
                }
                else if (!string.IsNullOrEmpty(au.Body))
                {
                    req.Content = new StringContent(au.Body, Encoding.UTF8, "application/x-www-form-urlencoded");
                }
            }

            // 注意：不做 CancellationToken 真取消——WP10 老网络栈在取消后可能使
            // 连接池进入坏状态（后续请求挂起），超时由外层 TimeoutAfter 放弃等待即可。
            // 坏状态隔离靠"每轮搜索新建 HttpPageFetcher"实现。
            using (var resp = await _client.SendAsync(req))
            {
                // 收集 Set-Cookie（手动 Cookie 管理）
                try
                {
                    if (resp.Headers.Contains("Set-Cookie"))
                        foreach (var sc in resp.Headers.GetValues("Set-Cookie"))
                            CookieStore.Set(resp.RequestMessage.RequestUri.ToString(), sc);
                }
                catch { }

                var bytes = await resp.Content.ReadAsByteArrayAsync();
                var enc = ResolveCharset(resp.Content?.Headers?.ContentType?.CharSet, au.Charset, bytes);
                var body = enc.GetString(bytes);
                return new StrResponse(resp.RequestMessage.RequestUri.ToString(), body, (int)resp.StatusCode);
            }
        }

        /// <summary>解析顺序：URL option charset &gt; HTTP header charset &gt; meta 探测 &gt; UTF-8。</summary>
        private static Encoding ResolveCharset(string headerCharset, string urlCharset, byte[] bytes)
        {
            var name = urlCharset ?? headerCharset;
            if (!string.IsNullOrEmpty(name))
            {
                try { return Encoding.GetEncoding(name); } catch { }
            }
            // meta charset 探测（GBK 站常见 <meta charset=gbk>）
            var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 1024)).ToLowerInvariant();
            var m = System.Text.RegularExpressions.Regex.Match(head, "charset\\s*=\\s*[\"']?([a-z0-9-]+)");
            if (m.Success)
            {
                try { return Encoding.GetEncoding(m.Groups[1].Value); } catch { }
            }
            return Encoding.UTF8;
        }
    }
}
