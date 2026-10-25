using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage.Streams;

namespace legado_wp.Core
{
    /// <summary>
    /// java.* 桥（对照 Kotlin JsExtensionsCommon.kt 核心集）。
    /// 哈希/编码/WinRT 加密/网络（ajax）/杂项；平台强相关方法 stub。
    /// </summary>
    public class JsExtensions
    {
        // ---------------- 编码 ----------------

        public string base64Encode(string s)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? ""));
        }

        public string base64Decode(string s)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String((s ?? "").Trim())); }
            catch { return s; }
        }

        public byte[] strToBytes(string s) { return Encoding.UTF8.GetBytes(s ?? ""); }
        public byte[] strToBytes(string s, string charset) { return AnalyzeUrl.GetCharset(charset).GetBytes(s ?? ""); }
        public string bytesToStr(byte[] b) { return b == null ? "" : Encoding.UTF8.GetString(b); }
        public string bytesToStr(byte[] b, string charset) { return b == null ? "" : AnalyzeUrl.GetCharset(charset).GetString(b); }

        public string hexDecodeToString(string hex)
        {
            try
            {
                var bytes = new byte[hex.Length / 2];
                for (var i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return null; }
        }

        public string hexEncodeToString(string s)
        {
            return string.Concat(Encoding.UTF8.GetBytes(s ?? "").Select(b => b.ToString("x2")));
        }

        public string encodeURI(string s) { return Uri.EscapeDataString(s ?? ""); }

        public string htmlFormat(string s)
        {
            return AnalyzeRule.UnescapeHtml(s ?? "");
        }

        public string randomUUID() { return Guid.NewGuid().ToString(); }

        // ---------------- 哈希 ----------------

        public string md5Encode(string s) { return DigestHex(s, "MD5"); }

        public string md5Encode16(string s)
        {
            var full = DigestHex(s, "MD5");
            return full == null ? null : full.Substring(8, 16);
        }

        public string digestHex(string data, string algorithm)
        {
            return DigestHex(data, algorithm);
        }

        public string digestBase64Str(string data, string algorithm)
        {
            try
            {
                var alg = HashAlgorithmProvider.OpenAlgorithm(NormalizeHash(algorithm));
                var buf = CryptographicBuffer.CreateFromByteArray(Encoding.UTF8.GetBytes(data ?? ""));
                var hash = alg.HashData(buf);
                return CryptographicBuffer.EncodeToBase64String(hash);
            }
            catch { return null; }
        }

        private static string DigestHex(string data, string algorithm)
        {
            try
            {
                var alg = HashAlgorithmProvider.OpenAlgorithm(NormalizeHash(algorithm));
                var buf = CryptographicBuffer.CreateFromByteArray(Encoding.UTF8.GetBytes(data ?? ""));
                var hash = alg.HashData(buf);
                return CryptographicBuffer.EncodeToHexString(hash);
            }
            catch { return null; }
        }

        private static string NormalizeHash(string algorithm)
        {
            var a = (algorithm ?? "MD5").ToUpperInvariant().Replace("-", "");
            if (a == "SHA") a = "SHA1";
            return a;
        }

        public string HMacHex(string data, string algorithm, string key)
        {
            var bytes = HMac(data, algorithm, key);
            return bytes == null ? null : string.Concat(bytes.Select(b => b.ToString("x2")));
        }

        public string HMacBase64(string data, string algorithm, string key)
        {
            var bytes = HMac(data, algorithm, key);
            return bytes == null ? null : Convert.ToBase64String(bytes);
        }

        private static byte[] HMac(string data, string algorithm, string key)
        {
            try
            {
                var a = (algorithm ?? "").ToUpperInvariant().Replace("HMAC", "").Replace("-", "");
                // 用编译期常量，避免手拼算法名大小写不匹配
                var name = a == "MD5" ? MacAlgorithmNames.HmacMd5
                    : a == "SHA" || a == "SHA1" ? MacAlgorithmNames.HmacSha1
                    : a == "SHA256" ? MacAlgorithmNames.HmacSha256
                    : a == "SHA384" ? MacAlgorithmNames.HmacSha384
                    : a == "SHA512" ? MacAlgorithmNames.HmacSha512
                    : MacAlgorithmNames.HmacSha1;
                var alg = MacAlgorithmProvider.OpenAlgorithm(name);
                var keyBuf = CryptographicBuffer.CreateFromByteArray(Encoding.UTF8.GetBytes(key ?? ""));
                var hmac = alg.CreateHash(keyBuf);
                var dataBuf = CryptographicBuffer.CreateFromByteArray(Encoding.UTF8.GetBytes(data ?? ""));
                hmac.Append(dataBuf);
                CryptographicBuffer.CopyToByteArray(hmac.GetValueAndReset(), out var result);
                return result;
            }
            catch { return null; }
        }

        // ---------------- 对称加密（hutool 风格 Crypto 对象）----------------

        public SymmetricCrypto createSymmetricCrypto(string mode, string padding, string key, string iv)
        {
            return new SymmetricCrypto(mode, padding, key, iv);
        }

        public SymmetricCrypto createSymmetricCrypto(string mode, string key, string iv)
        {
            return new SymmetricCrypto(mode, null, key, iv);
        }

        public SymmetricCrypto createSymmetricCrypto(string mode, string key)
        {
            return new SymmetricCrypto(mode, null, key, null);
        }

        // ---------------- 非对称（M3 基础版 RSA）----------------

        public AsymmetricCrypto createAsymmetricCrypto(string algorithm, string publicKeyBase64, string privateKeyBase64)
        {
            return new AsymmetricCrypto(algorithm, publicKeyBase64, privateKeyBase64);
        }

        // ---------------- 网络 ----------------

        /// <summary>同步 ajax（书源 JS 高频）：线程池跑避免 UI 死锁。</summary>
        public string ajax(object urlObj)
        {
            var url = urlObj?.ToString();
            if (string.IsNullOrEmpty(url)) return null;
            try
            {
                return System.Threading.Tasks.Task.Run(async delegate
                {
                    var au = new AnalyzeUrl(url, "");
                    var res = await JsEngine.FetchAsync(au);
                    return res.Body;
                }).GetAwaiter().GetResult();
            }
            catch { return null; }
        }

        public object[] ajaxAll(object[] urlList)
        {
            if (urlList == null) return new object[0];
            var results = new List<object>();
            foreach (var u in urlList) results.Add(ajax(u));
            return results.ToArray();
        }

        /// <summary>get/post 通用：返回 {code,url,body} 简化 StrResponse。</summary>
        public object connect(string urlStr, string header)
        {
            try
            {
                var au = new AnalyzeUrl(urlStr, "");
                var res = System.Threading.Tasks.Task.Run(() => JsEngine.FetchAsync(au)).GetAwaiter().GetResult();
                return new StrResponseLite { code = res.Code, url = res.Url, body = res.Body };
            }
            catch { return null; }
        }

        public object get(string url) { return connect(url, null); }
        public object post(string url, string body) { return connect(url + ",{\"method\":\"POST\",\"body\":\"" + body + "\"}", null); }

        // ---------------- Cookie（简版存取）----------------

        public string getCookie(string tag)
        {
            string v;
            return CookieStore.TryGet(tag, out v) ? v : "";
        }

        public string getCookie(string tag, string key) { return getCookie(tag); }

        // ---------------- 杂项 ----------------

        public string timeFormat(long time)
        {
            var dt = new DateTime(1970, 1, 1).AddMilliseconds(time);
            return dt.ToString("yyyy-MM-dd HH:mm:ss");
        }

        public string toNumChapter(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var m = System.Text.RegularExpressions.Regex.Match(s, "\\d+");
            return m.Success ? m.Value : s;
        }

        public object log(object msg) { return msg; }

        public string t2s(string text) { return text; } // 繁→简：词表太重，M3 原样返回
        public string s2t(string text) { return text; }

        // ---------------- 平台 stub ----------------

        public string androidId() { return ""; }
        public void toast(object msg) { }
        public void longToast(object msg) { }
        public void copy(object text) { }
        public string webView(string html, string url, string js) { return null; }
        public void openUrl(string url) { }
        public string getWebViewUA() { return "Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/86.0 Safari/537.36"; }
    }

    /// <summary>connect/get/post 返回的简化响应对象。</summary>
    public class StrResponseLite
    {
        public int code;
        public string url;
        public string body;
        public override string ToString() { return body ?? ""; }
    }

    /// <summary>Cookie 简版存储（HttpPageFetcher 收集 Set-Cookie）。</summary>
    public static class CookieStore
    {
        private static readonly Dictionary<string, string> Map = new Dictionary<string, string>();
        public static void Set(string url, string setCookie)
        {
            try
            {
                var host = new Uri(url).Host;
                var kv = setCookie.Split(';')[0];
                var eq = kv.IndexOf('=');
                if (eq > 0) Map[host] = kv.Substring(eq + 1).Trim();
            }
            catch { }
        }
        public static bool TryGet(string tag, out string v) { return Map.TryGetValue(tag, out v); }
    }

    /// <summary>对称加密对象（对照 hutool SymmetricCrypto 常用方法）。</summary>
    public class SymmetricCrypto
    {
        private readonly string _algName;
        private readonly IBuffer _key;
        private readonly IBuffer _iv;

        public SymmetricCrypto(string mode, string padding, string key, string iv)
        {
            var m = (mode ?? "AES").ToUpperInvariant();
            var p = (padding ?? "CBC/PKCS5").ToUpperInvariant().Replace("PADDING", "").Replace("PKCS5", "PKCS7");
            var chain = p.Contains("ECB") ? "ECB" : "CBC";
            var core = m == "AES" ? "AES" : m == "DES" ? "DES" : m == "DESEDE" || m == "3DES" ? "3DES" : "RC2";
            // WinRT 格式带下划线：AES_CBC_PKCS7 / AES_ECB_PKCS7 / DES_CBC_PKCS7 / 3DES_CBC_PKCS7
            _algName = core + "_" + chain + (chain == "ECB" ? "" : "_PKCS7");
            _key = CryptographicBuffer.CreateFromByteArray(Encoding.UTF8.GetBytes(key ?? ""));
            _iv = string.IsNullOrEmpty(iv) ? null : CryptographicBuffer.CreateFromByteArray(Encoding.UTF8.GetBytes(iv));
        }

        private CryptographicKey CreateKey()
        {
            var provider = SymmetricKeyAlgorithmProvider.OpenAlgorithm(_algName);
            return provider.CreateSymmetricKey(_key);
        }

        private IBuffer Crypt(IBuffer data, bool encrypt)
        {
            try
            {
                var key = CreateKey();
                return encrypt
                    ? CryptographicEngine.Encrypt(key, data, _iv)
                    : CryptographicEngine.Decrypt(key, data, _iv);
            }
            catch { return null; }
        }

        private static IBuffer BufOf(string s, string encoding)
        {
            var bytes = encoding == "base64"
                ? Convert.FromBase64String(s)
                : (encoding == "hex" ? HexToBytes(s) : Encoding.UTF8.GetBytes(s));
            return CryptographicBuffer.CreateFromByteArray(bytes);
        }

        private static string StrOf(IBuffer buf, string encoding)
        {
            CryptographicBuffer.CopyToByteArray(buf, out var bytes);
            if (encoding == "base64") return Convert.ToBase64String(bytes);
            if (encoding == "hex") return string.Concat(bytes.Select(b => b.ToString("X2")));
            return Encoding.UTF8.GetString(bytes);
        }

        private static byte[] HexToBytes(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        public string encryptHex(string plain) { var r = Crypt(BufOf(plain, "utf8"), true); return r == null ? null : StrOf(r, "hex"); }
        public string encryptBase64(string plain) { var r = Crypt(BufOf(plain, "utf8"), true); return r == null ? null : StrOf(r, "base64"); }
        public string decryptStr(string cipherHex) { var r = Crypt(BufOf(cipherHex, "hex"), false); return r == null ? null : StrOf(r, "utf8"); }
        public string decryptStr(string cipher, string encoding)
        {
            var r = Crypt(BufOf(cipher, encoding ?? "hex"), false); return r == null ? null : StrOf(r, "utf8");
        }
        public string decrypt(string cipherBase64) { var r = Crypt(BufOf(cipherBase64, "base64"), false); return r == null ? null : StrOf(r, "utf8"); }
    }

    /// <summary>RSA 基础版（PKCS1）。</summary>
    public class AsymmetricCrypto
    {
        private CryptographicKey _publicKey;
        private CryptographicKey _privateKey;

        public AsymmetricCrypto(string algorithm, string publicKeyBase64, string privateKeyBase64)
        {
            try
            {
                var provider = AsymmetricKeyAlgorithmProvider.OpenAlgorithm(AsymmetricAlgorithmNames.RsaPkcs1);
                if (!string.IsNullOrEmpty(publicKeyBase64))
                    _publicKey = provider.ImportPublicKey(CryptographicBuffer.DecodeFromBase64String(publicKeyBase64),
                        CryptographicPublicKeyBlobType.Pkcs1RsaPublicKey);
                if (!string.IsNullOrEmpty(privateKeyBase64))
                    _privateKey = provider.ImportKeyPair(CryptographicBuffer.DecodeFromBase64String(privateKeyBase64),
                        CryptographicPrivateKeyBlobType.Pkcs1RsaPrivateKey);
            }
            catch { }
        }

        public string encryptHex(string plain)
        {
            if (_publicKey == null) return null;
            try
            {
                var buf = CryptographicBuffer.CreateFromByteArray(Encoding.UTF8.GetBytes(plain));
                var enc = CryptographicEngine.Encrypt(_publicKey, buf, null);
                CryptographicBuffer.CopyToByteArray(enc, out var bytes);
                return string.Concat(bytes.Select(b => b.ToString("x2")));
            }
            catch { return null; }
        }

        public string decryptStr(string cipherHex)
        {
            if (_privateKey == null) return null;
            try
            {
                var bytes = new byte[cipherHex.Length / 2];
                for (var i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(cipherHex.Substring(i * 2, 2), 16);
                var buf = CryptographicBuffer.CreateFromByteArray(bytes);
                var dec = CryptographicEngine.Decrypt(_privateKey, buf, null);
                CryptographicBuffer.CopyToByteArray(dec, out var outBytes);
                return Encoding.UTF8.GetString(outBytes);
            }
            catch { return null; }
        }
    }
}
