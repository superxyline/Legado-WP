using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Windows.Storage;

namespace legado_wp.Core
{
    /// <summary>
    /// 本地持久化：书源 / 书架 / 阅读进度（LocalFolder JSON）。
    /// </summary>
    public static class Storage
    {
        private static async Task<T> ReadJsonAsync<T>(string name) where T : class
        {
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var file = await folder.GetFileAsync(name);
                var text = await FileIO.ReadTextAsync(file);
                return JsonConvert.DeserializeObject<T>(text);
            }
            catch { return null; }
        }

        private static async Task WriteJsonAsync(string name, object value)
        {
            var folder = ApplicationData.Current.LocalFolder;
            var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, JsonConvert.SerializeObject(value, Formatting.Indented));
        }

        // ---------------- 书源 ----------------

        public static async Task<List<BookSource>> LoadSourcesAsync()
        {
            return await ReadJsonAsync<List<BookSource>>("sources.json") ?? new List<BookSource>();
        }

        public static Task SaveSourcesAsync(List<BookSource> sources) => WriteJsonAsync("sources.json", sources);

        /// <summary>本机书源数量上限：929 老机型内存有限，数千源会 OOM 压垮书源页。</summary>
        public const int MaxSources = 1000;

        /// <summary>导入书源 JSON（单对象或数组），按 bookSourceUrl 去重合并。返回新增数。</summary>
        public static async Task<int> ImportSourcesAsync(string json)
        {
            json = json?.Trim() ?? "";
            List<BookSource> incoming;
            try
            {
                incoming = json.StartsWith("[")
                    ? JsonConvert.DeserializeObject<List<BookSource>>(json)
                    : new List<BookSource> { JsonConvert.DeserializeObject<BookSource>(json) };
            }
            catch (Exception ex)
            {
                // 数千源大文件在老机型上解析可能直接 OOM，给出可行动的提示
                var hint = ex is OutOfMemoryException
                    ? "（文件过大，老机型内存不足，请导入精选小合集）"
                    : "";
                throw new Exception("书源 JSON 解析失败" + hint);
            }
            if (incoming == null || incoming.Count == 0) throw new Exception("书源内容为空");
            incoming = incoming.Where(s => !string.IsNullOrEmpty(s.BookSourceUrl)).ToList();
            if (incoming.Count == 0) throw new Exception("无有效书源（缺 bookSourceUrl）");
            if (incoming.Count > MaxSources)
                throw new Exception("书源过多（" + incoming.Count + " 个）。本机最多支持约 "
                    + MaxSources + " 个，请导入精选合集或分批导入");

            var existing = await LoadSourcesAsync();
            if (existing.Count + incoming.Count > MaxSources)
            {
                var room = Math.Max(0, MaxSources - existing.Count);
                if (room == 0) throw new Exception("书源已满（" + existing.Count + " 个），请先删除部分再导入");
                incoming = incoming.Take(room).ToList();
            }

            var keys = new HashSet<string>(existing.Select(s => s.BookSourceUrl));
            var added = 0;
            foreach (var src in incoming)
            {
                if (keys.Add(src.BookSourceUrl)) added++;
                else
                {
                    existing.RemoveAll(s => s.BookSourceUrl == src.BookSourceUrl);
                }
                existing.Add(src);
            }
            await SaveSourcesAsync(existing);
            return added;
        }

        /// <summary>
        /// 从 URL 导入书源（流式下载 + 进度回调 + 自动重试）。
        /// 大合集（几 MB / 数千源）走慢速站点也要几分钟，所以不设硬超时，靠进度反馈。
        /// </summary>
        public static async Task<int> ImportSourcesFromUrlAsync(string url, Action<long> onProgress = null)
        {
            string body = null;
            Exception last = null;
            for (var attempt = 1; attempt <= 2 && body == null; attempt++)
            {
                try
                {
                    body = await DownloadTextAsync(url, onProgress);
                    if (string.IsNullOrEmpty(body)) throw new Exception("响应为空");
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt == 1)
                    {
                        onProgress?.Invoke(-1); // -1 = 重试中
                        await Task.Delay(1000);
                    }
                }
            }
            if (body == null) throw new Exception("下载失败: " + (last?.Message ?? "未知错误"));
            return await ImportSourcesAsync(body);
        }

        /// <summary>流式下载文本（ResponseHeadersRead + 循环 Read，支持进度回调，无硬超时）。</summary>
        private static async Task<string> DownloadTextAsync(string url, Action<long> onProgress)
        {
            var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromMinutes(15);
            try
            {
                using (var resp = await client.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!resp.IsSuccessStatusCode) throw new Exception("HTTP " + (int)resp.StatusCode);
                    var total = resp.Content.Headers.ContentLength ?? -1;
                    var ms = new MemoryStream();
                    var buffer = new byte[65536];
                    using (var stream = await resp.Content.ReadAsStreamAsync())
                    {
                        while (true)
                        {
                            var read = await stream.ReadAsync(buffer, 0, buffer.Length);
                            if (read <= 0) break;
                            ms.Write(buffer, 0, read);
                            onProgress?.Invoke(ms.Length);
                        }
                    }
                    var bytes = ms.ToArray();
                    var enc = Encoding.UTF8;
                    // BOM 探测
                    if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                        return enc.GetString(bytes, 3, bytes.Length - 3);
                    return enc.GetString(bytes, 0, bytes.Length);
                }
            }
            finally
            {
                client.Dispose();
            }
        }

        // ---------------- 书架 ----------------

        public static async Task<List<Book>> LoadShelfAsync()
        {
            return await ReadJsonAsync<List<Book>>("shelf.json") ?? new List<Book>();
        }

        public static Task SaveShelfAsync(List<Book> shelf) => WriteJsonAsync("shelf.json", shelf);

        public static async Task<Book> AddToShelfAsync(Book book)
        {
            var shelf = await LoadShelfAsync();
            shelf.RemoveAll(b => b.BookUrl == book.BookUrl);
            shelf.Add(book);
            await SaveShelfAsync(shelf);
            return book;
        }

        public static async Task RemoveFromShelfAsync(string bookUrl)
        {
            var shelf = await LoadShelfAsync();
            shelf.RemoveAll(b => b.BookUrl == bookUrl);
            await SaveShelfAsync(shelf);
            var progress = await LoadProgressAsync();
            progress.Remove(bookUrl);
            await SaveProgressAsync(progress);
        }

        // ---------------- 阅读进度 ----------------

        public static Task<Dictionary<string, int>> LoadProgressAsync()
        {
            return ReadJsonAsync<Dictionary<string, int>>("progress.json")
                .ContinueWith(t => t.Result ?? new Dictionary<string, int>());
        }

        public static Task SaveProgressAsync(Dictionary<string, int> progress) =>
            WriteJsonAsync("progress.json", progress);

        public static async Task SaveProgressAsync(string bookUrl, int chapterIndex)
        {
            var progress = await LoadProgressAsync();
            progress[bookUrl] = chapterIndex;
            await SaveProgressAsync(progress);
        }

        // ---------------- 书签 ----------------

        public class Bookmark
        {
            public string BookUrl { get; set; }
            public int ChapterIndex { get; set; }
            public string ChapterTitle { get; set; }
            public int PageIndex { get; set; }
            public long CreatedAt { get; set; }
        }

        public static Task<List<Bookmark>> LoadBookmarksAsync()
        {
            return ReadJsonAsync<List<Bookmark>>("bookmarks.json")
                .ContinueWith(t => t.Result ?? new List<Bookmark>());
        }

        public static Task SaveBookmarksAsync(List<Bookmark> bookmarks) =>
            WriteJsonAsync("bookmarks.json", bookmarks);

        public static async Task AddBookmarkAsync(Bookmark mark)
        {
            var list = await LoadBookmarksAsync();
            // 同书同章同页去重
            if (!list.Any(b => b.BookUrl == mark.BookUrl
                && b.ChapterIndex == mark.ChapterIndex && b.PageIndex == mark.PageIndex))
            {
                list.Add(mark);
                await SaveBookmarksAsync(list);
            }
        }

        public static async Task RemoveBookmarkAsync(Bookmark mark)
        {
            var list = await LoadBookmarksAsync();
            list.RemoveAll(b => b.BookUrl == mark.BookUrl
                && b.ChapterIndex == mark.ChapterIndex && b.PageIndex == mark.PageIndex);
            await SaveBookmarksAsync(list);
        }

        /// <summary>书源 variable 变更后持久化（java.setVariable 用）。</summary>
        public static async Task SaveSourceVariableAsync(BookSource source)
        {
            try
            {
                var sources = await LoadSourcesAsync();
                var idx = sources.FindIndex(s => s.BookSourceUrl == source.BookSourceUrl);
                if (idx >= 0) sources[idx] = source;
                else sources.Add(source);
                await SaveSourcesAsync(sources);
            }
            catch { }
        }

        // ---------------- 替换净化规则 ----------------

        public static Task<List<ReplaceRule>> LoadReplaceRulesAsync()
        {
            return ReadJsonAsync<List<ReplaceRule>>("replace_rules.json")
                .ContinueWith(t => t.Result ?? new List<ReplaceRule>());
        }

        public static Task SaveReplaceRulesAsync(List<ReplaceRule> rules) =>
            WriteJsonAsync("replace_rules.json", rules);

        /// <summary>字节数格式化（下载进度显示用）。</summary>
        public static string FormatBytes(long n)
        {
            if (n < 0) return "重试中…";
            if (n < 1024) return n + "B";
            if (n < 1024 * 1024) return (n / 1024.0).ToString("F0") + "KB";
            return (n / 1024.0 / 1024.0).ToString("F1") + "MB";
        }

        // ---------------- 备份 / 恢复 ----------------

        /// <summary>备份载荷：书源 + 书架 + 进度 + 书签 + 阅读器设置。</summary>
        public class BackupPayload
        {
            public int Version { get; set; } = 1;
            public string ExportedAt { get; set; }
            public List<BookSource> Sources { get; set; }
            public List<Book> Shelf { get; set; }
            public Dictionary<string, int> Progress { get; set; }
            public List<Bookmark> Bookmarks { get; set; }
            public List<ReplaceRule> ReplaceRules { get; set; }
            public double? ReaderFontSize { get; set; }
            public string ReaderMode { get; set; }
        }

        /// <summary>导出全部数据为备份 JSON 文本。</summary>
        public static async Task<string> ExportBackupAsync()
        {
            var payload = new BackupPayload
            {
                ExportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Sources = await LoadSourcesAsync(),
                Shelf = await LoadShelfAsync(),
                Progress = await LoadProgressAsync(),
                Bookmarks = await LoadBookmarksAsync(),
                ReplaceRules = await LoadReplaceRulesAsync()
            };
            try
            {
                var s = ApplicationData.Current.LocalSettings;
                if (s.Values.ContainsKey("reader.fontSize"))
                    payload.ReaderFontSize = (double)s.Values["reader.fontSize"];
                payload.ReaderMode = s.Values["reader.mode"] as string;
            }
            catch { }
            return JsonConvert.SerializeObject(payload, Formatting.Indented);
        }

        /// <summary>
        /// 恢复备份。replace=true 整体替换；false 合并（书源/书架按主键去重，进度/书签并集，已有值不覆盖）。
        /// 返回恢复摘要。
        /// </summary>
        public static async Task<string> ImportBackupAsync(string json, bool replace)
        {
            BackupPayload p;
            try { p = JsonConvert.DeserializeObject<BackupPayload>(json); }
            catch { throw new Exception("备份 JSON 解析失败"); }
            if (p == null) throw new Exception("备份内容为空");

            if (replace)
            {
                await SaveSourcesAsync(p.Sources ?? new List<BookSource>());
                await SaveShelfAsync(p.Shelf ?? new List<Book>());
                await SaveProgressAsync(p.Progress ?? new Dictionary<string, int>());
                await SaveBookmarksAsync(p.Bookmarks ?? new List<Bookmark>());
                if (p.ReplaceRules != null) await SaveReplaceRulesAsync(p.ReplaceRules);
            }
            else
            {
                var sources = await LoadSourcesAsync();
                var srcKeys = new HashSet<string>(sources.Select(s => s.BookSourceUrl));
                foreach (var src in p.Sources ?? new List<BookSource>())
                    if (srcKeys.Add(src.BookSourceUrl)) sources.Add(src);
                await SaveSourcesAsync(sources);

                var shelf = await LoadShelfAsync();
                var bookKeys = new HashSet<string>(shelf.Select(b => b.BookUrl));
                foreach (var b in p.Shelf ?? new List<Book>())
                    if (bookKeys.Add(b.BookUrl)) shelf.Add(b);
                await SaveShelfAsync(shelf);

                var progress = await LoadProgressAsync();
                foreach (var kv in p.Progress ?? new Dictionary<string, int>())
                    if (!progress.ContainsKey(kv.Key)) progress[kv.Key] = kv.Value;
                await SaveProgressAsync(progress);

                var marks = await LoadBookmarksAsync();
                foreach (var m in p.Bookmarks ?? new List<Bookmark>())
                    if (!marks.Any(x => x.BookUrl == m.BookUrl
                        && x.ChapterIndex == m.ChapterIndex && x.PageIndex == m.PageIndex))
                        marks.Add(m);
                await SaveBookmarksAsync(marks);

                if (p.ReplaceRules != null && p.ReplaceRules.Count > 0)
                {
                    var rules = await LoadReplaceRulesAsync();
                    var names = new HashSet<string>(rules.Select(r => (r.Name ?? "") + "|" + (r.Pattern ?? "")));
                    foreach (var r in p.ReplaceRules)
                        if (names.Add((r.Name ?? "") + "|" + (r.Pattern ?? ""))) rules.Add(r);
                    await SaveReplaceRulesAsync(rules);
                }
            }

            try
            {
                var s = ApplicationData.Current.LocalSettings;
                if (p.ReaderFontSize.HasValue) s.Values["reader.fontSize"] = p.ReaderFontSize.Value;
                if (!string.IsNullOrEmpty(p.ReaderMode)) s.Values["reader.mode"] = p.ReaderMode;
            }
            catch { }

            return "书源 " + (p.Sources?.Count ?? 0)
                + " · 书架 " + (p.Shelf?.Count ?? 0)
                + " · 进度 " + (p.Progress?.Count ?? 0)
                + " · 书签 " + (p.Bookmarks?.Count ?? 0)
                + " · 净化规则 " + (p.ReplaceRules?.Count ?? 0);
        }
    }
}
