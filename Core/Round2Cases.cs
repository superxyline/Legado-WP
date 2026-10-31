using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;

namespace legado_wp.Core
{
    /// <summary>
    /// 0.0.14.0 第二轮回归：本地书（TXT 切章 / EPUB 解包）、更新检测纯逻辑、书架过滤语义。
    /// CheckSource 走真实网络，不在探针里测。
    /// </summary>
    public static class Round2Cases
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

            // ---- 1. TXT 切章 ----
            var txt = "书名页\r\n作者：测试者\r\n\r\n第一章 初入江湖\r\n张三下山了。\r\n\r\n第二章 再遇强敌\r\n李四出手。\r\n\r\n番外 日常\r\n王五喝茶。";
            var chapters = LocalBookStore.SplitChapters(txt.Replace("\r\n", "\n"));
            check(" TXT: 章节数", "3", chapters.Count.ToString());
            check(" TXT: 首章标题", "第一章 初入江湖", chapters[0].Title);
            check(" TXT: 末章标题", "番外 日常", chapters[2].Title);

            // 切片正确性：第二章内容
            var parts = chapters[1].Url.Split(':');
            var sliced = txt.Replace("\r\n", "\n").Substring(int.Parse(parts[0]),
                int.Parse(parts[1]) - int.Parse(parts[0]));
            check(" TXT: 第二章正文", "李四出手。", sliced.Trim());

            // 无章节标记 → 全文单章
            var flat = LocalBookStore.SplitChapters("没有任何章节标记的一段话");
            check(" TXT: 无标记单章", "1", flat.Count.ToString());

            // 编码探测：GBK 字节
            try { Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); } catch { }
            var gbkBytes = Encoding.GetEncoding("GBK").GetBytes("天蚕土豆");
            check(" TXT: GBK 探测", "天蚕土豆", LocalBookStore.DecodeText(gbkBytes));
            var utf8Bytes = new UTF8Encoding(false).GetBytes("天蚕土豆");
            check(" TXT: UTF8 探测", "天蚕土豆", LocalBookStore.DecodeText(utf8Bytes));

            // ---- 2. EPUB（内存构造 → 导入 → 读正文）----
            var epubBook = await ImportTestEpubAsync();
            if (epubBook != null)
            {
                check(" EPUB: 章节数", "2", epubBook.ChapterCount.ToString());
                check(" EPUB: 书名", "测试EPUB", epubBook.Name);
                var toc = await LocalBookStore.GetTocAsync(epubBook);
                check(" EPUB: 目录标题", "第一章 开端", toc[0].Title);
                var text = await LocalBookStore.GetChapterTextAsync(epubBook, 1);
                check(" EPUB: 第二章正文", "这是第二章的内容。", text);

                // 清理测试书数据
                await RemoveLocalBookAsync(epubBook);
            }
            else
            {
                check(" EPUB: 导入", "成功", "返回 null");
            }

            // ---- 3. 书架语义（UpdateBadge / ScopeMatch 复用）----
            var b = new Book { Name = "X", NewCount = 3 };
            check(" 书架: 更新角标", "🆕 +3章", b.UpdateBadge);
            var b2 = new Book { Name = "Y", LocalType = 1 };
            check(" 书架: 本地角标", "[本地]", b2.UpdateBadge);

            log.Add($"[Round2 汇总] pass={pass} fail={fail}");
            return log;
        }

        /// <summary>内存构造最小 EPUB → 写临时文件 → ImportEpubAsync。</summary>
        private static async Task<Book> ImportTestEpubAsync()
        {
            var chapter1 = "<?xml version=\"1.0\"?><html><head><title>c1</title></head><body>" +
                "<h1>第一章 开端</h1><p>这是第一章的内容。</p></body></html>";
            var chapter2 = "<?xml version=\"1.0\"?><html><head><title>c2</title></head><body>" +
                "<p>这是第二章的内容。</p></body></html>";
            var container = "<?xml version=\"1.0\"?>" +
                "<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">" +
                "<rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>";
            var opf = "<?xml version=\"1.0\"?>" +
                "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"2.0\" unique-identifier=\"id\">" +
                "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>测试EPUB</dc:title></metadata>" +
                "<manifest><item id=\"c1\" href=\"ch1.xhtml\" media-type=\"application/xhtml+xml\"/>" +
                "<item id=\"c2\" href=\"ch2.xhtml\" media-type=\"application/xhtml+xml\"/>" +
                "<item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/></manifest>" +
                "<spine toc=\"ncx\"><itemref idref=\"c1\"/><itemref idref=\"c2\"/></spine></package>";
            var ncx = "<?xml version=\"1.0\"?>" +
                "<ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\" version=\"2005-1\">" +
                "<head/><docTitle><text>测试EPUB</text></docTitle><navMap>" +
                "<navPoint id=\"n1\"><navLabel><text>第一章 开端</text></navLabel><content src=\"ch1.xhtml\"/></navPoint>" +
                "<navPoint id=\"n2\"><navLabel><text>第二章 转折</text></navLabel><content src=\"ch2.xhtml\"/></navPoint>" +
                "</navMap></ncx>";

            var tempFolder = ApplicationData.Current.TemporaryFolder;
            var file = await tempFolder.CreateFileAsync("test-probe.epub", CreationCollisionOption.ReplaceExisting);
            using (var fs = await file.OpenStreamForWriteAsync())
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                void Add(string name, string content)
                {
                    var e = zip.CreateEntry(name);
                    using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false)))
                        w.Write(content);
                }
                Add("mimetype", "application/epub+zip");
                Add("META-INF/container.xml", container);
                Add("OEBPS/content.opf", opf);
                Add("OEBPS/toc.ncx", ncx);
                Add("OEBPS/ch1.xhtml", chapter1);
                Add("OEBPS/ch2.xhtml", chapter2);
            }

            try
            {
                return await LocalBookStore.ImportEpubAsync(file);
            }
            finally
            {
                try { await file.DeleteAsync(); } catch { }
            }
        }

        private static async Task RemoveLocalBookAsync(Book book)
        {
            try
            {
                var root = await ApplicationData.Current.LocalFolder
                    .GetFolderAsync("LocalBooks");
                var folder = await root.GetFolderAsync(book.LocalPath);
                await folder.DeleteAsync();
            }
            catch { }
        }
    }
}
