using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace legado_wp.Core
{
    /// <summary>
    /// 书源校验（对照 Kotlin CheckSourceShared 语义精简版）：
    /// 逐源跑 搜索→详情→目录→正文，记录卡在哪一步、耗时与错误。
    /// </summary>
    public class CheckSource
    {
        public class Result
        {
            public BookSource Source;
            public bool Ok;
            public string Step = "";     // 通过的最后一步；失败时为失败步骤名
            public string Error = "";
            public long Ms;
            public string Display => (Source.BookSourceName ?? "") + "（" + (Source.BookSourceGroup ?? "") + "）";
        }

        /// <summary>单源体检。key=搜索关键字，bookName=用于精确匹配（可空）。</summary>
        public static async Task<Result> CheckAsync(BookSource src, WebBook wb, string key, CancellationToken ct)
        {
            var r = new Result { Source = src };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // 1. 搜索
                ct.ThrowIfCancellationRequested();
                r.Step = "搜索";
                var found = (await wb.SearchBookAsync(src, key)).FirstOrDefault();
                if (found == null) throw new Exception("无搜索结果");

                // 2. 详情
                ct.ThrowIfCancellationRequested();
                r.Step = "详情";
                var book = await wb.GetBookInfoAsync(src, found);
                if (book == null || string.IsNullOrEmpty(book.BookUrl)) throw new Exception("详情解析失败");

                // 3. 目录
                ct.ThrowIfCancellationRequested();
                r.Step = "目录";
                var chapters = await wb.GetChapterListAsync(src, book);
                if (chapters.Count == 0) throw new Exception("目录为空");

                // 4. 正文（首章）
                ct.ThrowIfCancellationRequested();
                r.Step = "正文";
                var text = await wb.GetContentAsync(src, chapters[0], 1);
                if (string.IsNullOrWhiteSpace(text)) throw new Exception("正文为空");

                r.Ok = true;
            }
            catch (OperationCanceledException)
            {
                r.Error = "已取消";
                r.Step = "取消";
            }
            catch (Exception ex)
            {
                r.Ok = false;
                r.Error = r.Step + "失败：" + ex.Message;
            }
            finally
            {
                r.Ms = sw.ElapsedMilliseconds;
            }
            return r;
        }

        /// <summary>并发批量体检（回调在调用方线程上下文）。</summary>
        public static async Task<List<Result>> CheckAllAsync(
            List<BookSource> sources, string key, int concurrency, Action<Result> onEach, CancellationToken ct)
        {
            var results = new List<Result>();
            var queue = new Queue<BookSource>(sources);
            var tasks = new List<Task>();
            using (var sem = new SemaphoreSlim(concurrency))
            {
                while (queue.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    await sem.WaitAsync(ct);
                    var src = queue.Dequeue();
                    var wb = new WebBook();
                    var task = Task.Run(async () =>
                    {
                        try
                        {
                            var r = await CheckAsync(src, wb, key, ct);
                            results.Add(r);
                            onEach?.Invoke(r);
                        }
                        finally { sem.Release(); }
                    }, ct);
                    tasks.Add(task);
                }
                await Task.WhenAll(tasks);
            }
            return results;
        }
    }
}
