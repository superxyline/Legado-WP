using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.System.Profile;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace legado_wp
{
    public sealed partial class MainPage : Page
    {
        private readonly StringBuilder _log = new StringBuilder();

        public MainPage()
        {
            InitializeComponent();
            Loaded += async (s, e) => await RunProbesAsync();
        }

        private void OnBackClick(object sender, RoutedEventArgs e)
        {
            Frame.GoBack();
        }

        private void Log(string line)
        {
            _log.AppendLine(line);
            Output.Text = _log.ToString();
        }

        private async System.Threading.Tasks.Task FlushLogAsync()
        {
            try
            {
                var dir = Windows.Storage.ApplicationData.Current.LocalFolder;
                var file = await dir.CreateFileAsync("m0-probe.log",
                    Windows.Storage.CreationCollisionOption.ReplaceExisting);
                await Windows.Storage.FileIO.WriteTextAsync(file, _log.ToString());
            }
            catch { /* 探针日志失败不影响 UI */ }
        }

        private async System.Threading.Tasks.Task RunProbesAsync()
        {
            try
            {
                Log("[env] arch=" + (IntPtr.Size == 8 ? "x64" : "32bit(x86/ARM)"));
                Log("[env] os=" + AnalyticsInfo.VersionInfo.DeviceFamily);

                Log("[step] js begin");
                ProbeJs();
                Log("[step] js end");

                await ProbeHttpAsync("http://example.com", "HTTP 明文");
                Log("[step] http1 end");
                await ProbeHttpAsync("https://www.baidu.com", "HTTPS TLS1.2");
                Log("[step] http2 end");

                Log("[done] M0 探针执行完毕");

                // M1：规则引擎回归用例
                foreach (var line in Core.M1Cases.Run()) Log(line);

                // M2：URL 解析 + WebBook 全链路 + 真实探测
                foreach (var line in await Core.M2Cases.Run()) Log(line);

                // M3：JS 引擎 + java.* 桥
                foreach (var line in await Core.M3Cases.Run()) Log(line);

                // 0.0.12.0：XPath 迷你引擎
                foreach (var line in Core.XPathCases.Run()) Log(line);

                // 0.0.13.0：净化规则 / java.* 扩展 / jsLib / 书源变量
                foreach (var line in await Core.Round1Cases.Run()) Log(line);

                await FlushLogAsync();
            }
            catch (Exception ex)
            {
                Log("[fatal] " + ex.ToString());
                await FlushLogAsync();
            }
        }

        /// <summary>
        /// JS 引擎探针：Jint 2.11 执行书源风格 JS（正则提取 + java 桥）。
        /// java.md5Encode 桥即 M3 java.* shim 的注入方式验证。
        /// </summary>
        private void ProbeJs()
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var engine = new Jint.Engine();
                engine.SetValue("java", new JavaBridge());
                var result = engine.Execute(@"
                    var html = '<div class=""title""><a href=""/book/123.html"">测试小说</a></div>';
                    var m = html.match(/href=""([^""]+)""/);
                    var name = html.replace(/<[^>]+>/g, '').trim();
                    var sign = java.md5Encode(name + m[1]);
                    JSON.stringify({
                        name: name,
                        url: m[1],
                        sign: sign,
                        es5: [1,2,3].map(function(x){return x*2;}).join(','),
                        json: JSON.parse('{""a"":1}').a
                    });
                ").GetCompletionValue();
                sw.Stop();
                Log("[js] " + result.AsString() + " (" + sw.ElapsedMilliseconds + "ms)");
            }
            catch (Exception ex)
            {
                Log("[js] EX " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private async System.Threading.Tasks.Task ProbeHttpAsync(string url, string label)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(15);
                    var sw = Stopwatch.StartNew();
                    var resp = await client.GetAsync(url);
                    var body = await resp.Content.ReadAsStringAsync();
                    sw.Stop();
                    Log("[http] " + label + " -> " + (int)resp.StatusCode
                        + " len=" + body.Length + " " + sw.ElapsedMilliseconds + "ms");
                }
            }
            catch (Exception ex)
            {
                Log("[http] " + label + " -> EX " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    /// <summary>
    /// java.* 桥最小集（M3 shim 原型）：对象注入 Jint，反射映射成员。
    /// </summary>
    public class JavaBridge
    {
        public string md5Encode(string s)
        {
            var provider = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Md5);
            var buf = CryptographicBuffer.ConvertStringToBinary(s, BinaryStringEncoding.Utf8);
            var hash = provider.HashData(buf);
            return CryptographicBuffer.EncodeToHexString(hash);
        }

        public string base64Encode(string s)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
        }
    }
}
