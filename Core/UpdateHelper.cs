using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace legado_wp.Core
{
    /// <summary>
    /// 章节更新检测（对照 Kotlin UpdateBookShared 语义的同步精简版）：
    /// 打开书架时触发（30 分钟节流），逐书拉目录比章节数，更新 LastChapter 并记 NewCount 角标。
    /// WP10 后台任务受 15 分钟最小间隔限制，不做后台定时版。
    /// </summary>
    public static class UpdateHelper
    {
        public const int MinIntervalMinutes = 30;

        /// <summary>书架批量检查：并发 2，跳过本地书/禁用源的书/未到节流间隔的。回调带回进度 (done, total)。</summary>
        public static async Task<int> CheckShelfAsync(List<Book> shelf, Action<int, int> onProgress,
            Action onBookUpdated, bool force = false)
        {
            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            var pending = new List<Book>();
            var sources = await Storage.LoadSourcesAsync();
            var sourceMap = sources.Where(s => s.Enabled).ToDictionary(s => s.BookSourceUrl, s => s);

            foreach (var b in shelf)
            {
                if (LocalBookStore.IsLocal(b)) continue;
                if (!sourceMap.ContainsKey(b.SourceUrl ?? "")) continue;
                if (!force && now - b.LastCheck < MinIntervalMinutes * 60L * 1000L) continue;
                pending.Add(b);
            }

            var updated = 0;
            var done = 0;
            var tasks = new List<Task>();
            using (var sem = new System.Threading.SemaphoreSlim(2))
            {
                foreach (var book in pending)
                {
                    await sem.WaitAsync();
                    var b = book;
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            if (await CheckBookAsync(b, sourceMap[b.SourceUrl])) updated++;
                        }
                        catch { /* 单书失败忽略 */ }
                        finally
                        {
                            b.LastCheck = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                            sem.Release();
                            done++;
                            onProgress?.Invoke(done, pending.Count);
                        }
                    }));
                }
                await Task.WhenAll(tasks);
            }
            await Storage.SaveShelfAsync(shelf);
            onBookUpdated?.Invoke();
            return updated;
        }

        /// <summary>单书检查：章节数变多 → 更新 LastChapter/NewCount。返回是否检测到更新。</summary>
        public static async Task<bool> CheckBookAsync(Book book, BookSource source)
        {
            var wb = new WebBook();
            var chapters = await wb.GetChapterListAsync(source, book).TimeoutAfter(25000);
            if (chapters.Count == 0) return false;

            var oldCount = book.ChapterCount;
            book.ChapterCount = chapters.Count;
            book.LastChapter = chapters[chapters.Count - 1].Title;
            if (oldCount > 0 && chapters.Count > oldCount)
            {
                book.NewCount = Math.Max(book.NewCount, chapters.Count - oldCount);
                return true;
            }
            if (oldCount == 0)
            {
                // 首次记录基准，不算更新
                book.NewCount = 0;
            }
            return false;
        }
    }
}
