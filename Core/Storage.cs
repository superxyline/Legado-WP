using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            catch
            {
                throw new Exception("书源 JSON 解析失败");
            }
            if (incoming == null || incoming.Count == 0) throw new Exception("书源内容为空");
            incoming = incoming.Where(s => !string.IsNullOrEmpty(s.BookSourceUrl)).ToList();
            if (incoming.Count == 0) throw new Exception("无有效书源（缺 bookSourceUrl）");

            var existing = await LoadSourcesAsync();
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

        /// <summary>从 URL 导入书源。</summary>
        public static async Task<int> ImportSourcesFromUrlAsync(string url)
        {
            string body;
            try
            {
                var au = new AnalyzeUrl(url, "");
                var res = await HttpPageFetcher.Default.GetAsync(au);
                if (!res.IsSuccess || string.IsNullOrEmpty(res.Body)) throw new Exception("HTTP " + res.Code);
                body = res.Body;
            }
            catch (Exception ex)
            {
                throw new Exception("下载失败: " + ex.Message);
            }
            return await ImportSourcesAsync(body);
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
    }
}
