using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.Storage;
using Windows.Storage.Streams;

namespace legado_wp.Core
{
    /// <summary>
    /// 本地书库（0.0.14.0）：TXT 正则切章 + EPUB 解包导入，断网可读。
    /// 数据目录 LocalFolder/LocalBooks/{id}/：
    ///   TXT  → book.txt + meta.json（含章节偏移表）
    ///   EPUB → 章节文件 0001.xhtml… + meta.json
    /// BookChapter.Url 承载定位信息：TXT 为 "start:end" 偏移，EPUB 为解包后的文件名。
    /// </summary>
    public static class LocalBookStore
    {
        private static readonly string TxtChapterPattern =
            @"^[ \t]*(第[0-9零一二三四五六七八九十百千两〇〇]+[章节卷回集部篇][^\n]{0,40}|序章|楔子|前言|后记|尾声|番外[^\n]{0,30})[ \t]*$";

        // ---------------- 导入 ----------------

        /// <summary>导入 TXT：复制进 LocalBooks，探测编码、正则切章、猜书名作者。</summary>
        public static async Task<Book> ImportTxtAsync(StorageFile file)
        {
            var bytes = await ReadAllBytesAsync(file);
            var text = DecodeText(bytes);

            var folder = await CreateBookFolderAsync(file.Name);
            var txtFile = await folder.CreateFileAsync("book.txt", CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(txtFile, text);

            var chapters = SplitChapters(text);
            var book = new Book
            {
                Name = GuessName(file.Name, text),
                Author = GuessAuthor(text),
                BookUrl = "local://" + folder.Name,
                LocalType = 1,
                LocalPath = folder.Name,
                Group = "本地",
                ChapterCount = chapters.Count,
                LastChapter = chapters.Count > 0 ? chapters[chapters.Count - 1].Title : ""
            };
            await SaveMetaAsync(folder, book, chapters);
            return book;
        }

        /// <summary>导入 EPUB：解包 spine 章节文件，解析目录。</summary>
        public static async Task<Book> ImportEpubAsync(StorageFile file)
        {
            var folder = await CreateBookFolderAsync(file.Name);
            var chapters = new List<BookChapter>();
            string opfTitle = null;

            using (var stream = await file.OpenStreamForReadAsync())
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                // container.xml → OPF 路径
                var opfPath = FindOpfPath(zip);
                if (opfPath == null) throw new Exception("EPUB 缺少 container.xml");

                var opfDir = "";
                var opfName = opfPath;
                var slash = opfPath.LastIndexOf('/');
                if (slash >= 0) { opfDir = opfPath.Substring(0, slash + 1); opfName = opfPath.Substring(slash + 1); }

                XDocument opf;
                var opfEntry = zip.GetEntry(opfPath) ?? zip.Entries.FirstOrDefault(e => e.FullName == opfPath);
                if (opfEntry == null) throw new Exception("EPUB 缺少 OPF");
                using (var s = opfEntry.Open())
                    opf = XDocument.Load(s);

                // manifest: id → href/类型
                var manifest = ManifestOf(opf, opfDir);

                // spine 顺序
                var spineIds = opf.Descendants().Where(d => d.Name.LocalName == "itemref")
                    .Select(d => (string)d.Attribute("idref")).ToList();

                // 目录标题：ncx 优先，EPUB3 nav 兜底
                var tocByHref = LoadNcx(zip, manifest, opfDir);
                if (tocByHref.Count == 0)
                    tocByHref = LoadNav(zip, manifest);

                var idx = 0;
                foreach (var idref in spineIds)
                {
                    if (!manifest.ContainsKey(idref)) continue;
                    var item = manifest[idref];
                    if (!item.MediaType.Contains("xhtml") && !item.MediaType.Contains("html")) continue;

                    var entry = zip.GetEntry(item.Href);
                    if (entry == null) continue;

                    idx++;
                    var fileName = idx.ToString("D4") + ".xhtml";
                    string xhtml;
                    using (var s = entry.Open())
                    using (var r = new StreamReader(s, true))
                        xhtml = r.ReadToEnd();
                    var outFile = await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
                    await FileIO.WriteTextAsync(outFile, xhtml);

                    var key = item.Href;
                    var frag = key.IndexOf('#');
                    if (frag >= 0) key = key.Substring(0, frag);
                    string title;
                    if (!tocByHref.TryGetValue(key, out title) || string.IsNullOrWhiteSpace(title))
                        title = FirstHeading(xhtml) ?? ("第" + idx + "章");
                    chapters.Add(new BookChapter { Title = title.Trim(), Url = fileName });
                }

                // 书名取 OPF dc:title，兜底文件名
                opfTitle = opf.Descendants().FirstOrDefault(d => d.Name.LocalName == "title")?.Value;
            }

            if (chapters.Count == 0) throw new Exception("EPUB 未解析到章节");

            var bookName = string.IsNullOrWhiteSpace(opfTitle) ? GuessName(file.Name, null) : opfTitle.Trim();

            var book = new Book
            {
                Name = bookName,
                Author = "",
                BookUrl = "local://" + folder.Name,
                LocalType = 2,
                LocalPath = folder.Name,
                Group = "本地",
                ChapterCount = chapters.Count,
                LastChapter = chapters[chapters.Count - 1].Title
            };
            await SaveMetaAsync(folder, book, chapters);
            return book;
        }

        // ---------------- 读取 ----------------

        public static bool IsLocal(Book book) => book != null && book.LocalType > 0;

        /// <summary>本地书目录列表（TocPage 用）。</summary>
        public static async Task<List<BookChapter>> GetTocAsync(Book book)
        {
            var folder = await GetBookFolderAsync(book);
            if (folder == null) return new List<BookChapter>();
            var meta = await ReadMetaAsync(folder);
            return meta?.Chapters ?? new List<BookChapter>();
        }

        /// <summary>本地书章节正文。</summary>
        public static async Task<string> GetChapterTextAsync(Book book, int index)
        {
            var folder = await GetBookFolderAsync(book);
            if (folder == null) return "（本地书数据丢失）";
            var meta = await ReadMetaAsync(folder);
            if (meta == null || meta.Chapters == null || index >= meta.Chapters.Count)
                return "（章节不存在）";
            var ch = meta.Chapters[index];

            if (book.LocalType == 1)
            {
                // TXT：按偏移切片（整文件缓存到会话内存，避免每章重读磁盘）
                var parts = (ch.Url ?? "").Split(':');
                if (parts.Length != 2) return "（章节定位失败）";
                int start, end;
                if (!int.TryParse(parts[0], out start) || !int.TryParse(parts[1], out end))
                    return "（章节定位失败）";
                var text = await TxtCacheAsync(folder);
                if (end > text.Length) end = text.Length;
                if (start >= text.Length) return "";
                return text.Substring(start, end - start).Trim();
            }
            else
            {
                // EPUB：读解包后的 xhtml → 纯文本
                try
                {
                    var f = await folder.GetFileAsync(ch.Url);
                    var xhtml = await FileIO.ReadTextAsync(f);
                    return XhtmlToText(xhtml);
                }
                catch { return "（章节文件缺失）"; }
            }
        }

        // ---------------- TXT 解析 ----------------

        public static List<BookChapter> SplitChapters(string text)
        {
            var chapters = new List<BookChapter>();
            var matches = Regex.Matches(text, TxtChapterPattern, RegexOptions.Multiline);
            if (matches.Count == 0)
            {
                chapters.Add(new BookChapter { Title = "全文", Url = "0:" + text.Length });
                return chapters;
            }
            for (var i = 0; i < matches.Count; i++)
            {
                var start = matches[i].Index + matches[i].Length;
                var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
                var title = matches[i].Value.Trim();
                if (title.Length == 0) title = "第" + (i + 1) + "章";
                chapters.Add(new BookChapter
                {
                    Title = title,
                    Url = start + ":" + end
                });
            }
            return chapters;
        }

        private static string GuessName(string fileName, string text)
        {
            var name = fileName;
            var dot = name.LastIndexOf('.');
            if (dot > 0) name = name.Substring(0, dot);
            // 文件名常带"作者"后缀：《书名》作者：xxx.txt / 书名-作者.txt
            name = Regex.Replace(name, "[-_ ]?作者[:：].*$", "");
            return string.IsNullOrWhiteSpace(name) ? "本地书" : name.Trim();
        }

        private static string GuessAuthor(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var head = text.Substring(0, Math.Min(text.Length, 600));
            var m = Regex.Match(head, "作\\s*者[:：]\\s*([^\\n\\r]{1,20})");
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        // ---------------- EPUB 解析 ----------------

        private static string FindOpfPath(ZipArchive zip)
        {
            var entry = zip.Entries.FirstOrDefault(e => e.FullName == "META-INF/container.xml")
                ?? zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("container.xml", StringComparison.OrdinalIgnoreCase));
            if (entry == null) return null;
            using (var s = entry.Open())
            {
                var doc = XDocument.Load(s);
                var rf = doc.Descendants().FirstOrDefault(d => d.Name.LocalName == "rootfile");
                return rf == null ? null : (string)rf.Attribute("full-path");
            }
        }

        /// <summary>ncx 目录：href(无锚点) → 标题。</summary>
        private static Dictionary<string, string> LoadNcx(ZipArchive zip, Dictionary<string, ManifestItem> manifest, string opfDir)
        {
            var result = new Dictionary<string, string>();
            var ncxId = manifest.Where(kv => kv.Value.MediaType.Contains("ncx"))
                .Select(kv => kv.Value.Href).FirstOrDefault();
            if (ncxId == null) return result;

            var entry = zip.GetEntry(opfDir + ncxId);
            if (entry == null) return result;
            using (var s = entry.Open())
            {
                var doc = XDocument.Load(s);
                foreach (var np in doc.Descendants().Where(d => d.Name.LocalName == "navPoint"))
                {
                    var label = np.Descendants().FirstOrDefault(d => d.Name.LocalName == "text")?.Value;
                    var src = np.Descendants().FirstOrDefault(d => d.Name.LocalName == "content")?
                        .Attribute("src")?.Value;
                    if (string.IsNullOrEmpty(src)) continue;
                    var hash = src.IndexOf('#');
                    if (hash >= 0) src = src.Substring(0, hash);
                    if (!string.IsNullOrEmpty(label) && !result.ContainsKey(opfDir + src))
                        result[opfDir + src] = label;
                }
            }
            return result;
        }

        /// <summary>EPUB3 nav 目录兜底。</summary>
        private static Dictionary<string, string> LoadNav(ZipArchive zip, Dictionary<string, ManifestItem> manifest)
        {
            var result = new Dictionary<string, string>();
            var navHref = manifest.Where(kv => kv.Value.Props.Contains("nav"))
                .Select(kv => kv.Value.Href).FirstOrDefault();
            if (navHref == null) return result;
            var entry = zip.GetEntry(navHref);
            if (entry == null) return result;
            string html;
            using (var s = entry.Open())
            using (var r = new StreamReader(s, true))
                html = r.ReadToEnd();
            var dir = "";
            var slash = navHref.LastIndexOf('/');
            if (slash >= 0) dir = navHref.Substring(0, slash + 1);
            foreach (Match m in Regex.Matches(html, "<a[^>]+href=[\"']([^\"'#]+)[^>]*>([\\s\\S]*?)</a>", RegexOptions.IgnoreCase))
            {
                var title = Regex.Replace(m.Groups[2].Value, "<[^>]+>", "").Trim();
                if (title.Length > 0 && !result.ContainsKey(dir + m.Groups[1].Value))
                    result[dir + m.Groups[1].Value] = title;
            }
            return result;
        }

        private static string FirstHeading(string xhtml)
        {
            var m = Regex.Match(xhtml, "(?i)<h[1-3][^>]*>([\\s\\S]*?)</h[1-3]>");
            if (m.Success)
            {
                var t = Regex.Replace(m.Groups[1].Value, "<[^>]+>", "").Trim();
                if (t.Length > 0) return t;
            }
            m = Regex.Match(xhtml, "(?i)<title>([\\s\\S]*?)</title>");
            var tt = m.Success ? m.Groups[1].Value.Trim() : "";
            return tt.Length > 0 ? tt : null;
        }

        /// <summary>XHTML → 纯文本：块级标签转换行，去标签，反转义。</summary>
        public static string XhtmlToText(string xhtml)
        {
            var t = Regex.Replace(xhtml, "(?is)<head[\\s\\S]*?</head>", "");
            t = Regex.Replace(t, "(?i)</(p|div|h[1-6]|li|tr|blockquote|section)>", "\n");
            t = Regex.Replace(t, "(?i)<br[ /]*>", "\n");
            t = Regex.Replace(t, "<[^>]+>", "");
            t = AnalyzeRule.UnescapeHtml(t);
            t = Regex.Replace(t, "[ \\t]+\\n", "\n");
            t = Regex.Replace(t, "\\n{3,}", "\n\n");
            return t.Trim();
        }

        // ---------------- 存储辅助 ----------------

        private class ManifestItem
        {
            public string Href;
            public string MediaType;
            public string Props;
        }

        /// <summary>重载：把 manifest 匿名类型换成实体（LoadNcx/LoadNav 用）。</summary>
        private static Dictionary<string, ManifestItem> ManifestOf(XDocument opf, string opfDir)
        {
            var map = new Dictionary<string, ManifestItem>();
            foreach (var d in opf.Descendants().Where(x => x.Name.LocalName == "item"))
            {
                var id = (string)d.Attribute("id");
                var href = (string)d.Attribute("href");
                if (id == null || href == null) continue;
                map[id] = new ManifestItem
                {
                    Href = opfDir + href,
                    MediaType = ((string)d.Attribute("media-type")) ?? "",
                    Props = ((string)d.Attribute("properties")) ?? ""
                };
            }
            return map;
        }

        private static async Task<StorageFolder> CreateBookFolderAsync(string originName)
        {
            var root = await ApplicationData.Current.LocalFolder
                .CreateFolderAsync("LocalBooks", CreationCollisionOption.OpenIfExists);
            var id = Guid.NewGuid().ToString("N").Substring(0, 12);
            return await root.CreateFolderAsync(id, CreationCollisionOption.OpenIfExists);
        }

        private static async Task<StorageFolder> GetBookFolderAsync(Book book)
        {
            try
            {
                var root = await ApplicationData.Current.LocalFolder
                    .CreateFolderAsync("LocalBooks", CreationCollisionOption.OpenIfExists);
                return await root.GetFolderAsync(book.LocalPath);
            }
            catch { return null; }
        }

        private class LocalMeta
        {
            public string Name;
            public string Author;
            public List<BookChapter> Chapters;
        }

        private static async Task SaveMetaAsync(StorageFolder folder, Book book, List<BookChapter> chapters)
        {
            var meta = new LocalMeta { Name = book.Name, Author = book.Author, Chapters = chapters };
            var file = await folder.CreateFileAsync("meta.json", CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, Newtonsoft.Json.JsonConvert.SerializeObject(meta));
        }

        private static async Task<LocalMeta> ReadMetaAsync(StorageFolder folder)
        {
            try
            {
                var file = await folder.GetFileAsync("meta.json");
                var text = await FileIO.ReadTextAsync(file);
                return Newtonsoft.Json.JsonConvert.DeserializeObject<LocalMeta>(text);
            }
            catch { return null; }
        }

        // TXT 全文只缓存最近一本（大 TXT 多本驻留直接 OOM），锁保护并发读
        private static readonly object TxtLock = new object();
        private static string _txtKey;
        private static string _txtText;

        private static Task<string> TxtCacheAsync(StorageFolder folder)
        {
            lock (TxtLock)
            {
                if (_txtKey == folder.Name && _txtText != null) return Task.FromResult(_txtText);
            }
            return LoadTxtAsync(folder);
        }

        private static async Task<string> LoadTxtAsync(StorageFolder folder)
        {
            var file = await folder.GetFileAsync("book.txt");
            var text = await FileIO.ReadTextAsync(file);
            lock (TxtLock)
            {
                _txtKey = folder.Name;
                _txtText = text;
            }
            return text;
        }

        /// <summary>编码探测：BOM → UTF-8 严格校验 → GBK 兜底。</summary>
        public static string DecodeText(byte[] bytes)
        {
            try { Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); }
            catch { }
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            try
            {
                var strict = new UTF8Encoding(false, true);
                return strict.GetString(bytes, 0, bytes.Length);
            }
            catch
            {
                try { return Encoding.GetEncoding("GBK").GetString(bytes, 0, bytes.Length); }
                catch { return Encoding.UTF8.GetString(bytes, 0, bytes.Length); }
            }
        }

        private static async Task<byte[]> ReadAllBytesAsync(StorageFile file)
        {
            using (var stream = await file.OpenStreamForReadAsync())
            using (var ms = new MemoryStream())
            {
                await stream.CopyToAsync(ms);
                return ms.ToArray();
            }
        }
    }
}
