using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace legado_wp.Core
{
    /// <summary>
    /// 单源搜索结果（含诊断），供 UI 层做流式刷新与失败统计。
    /// </summary>
    public class SearchOutcome
    {
        public List<SearchBook> Books = new List<SearchBook>();
        public bool Ok;                 // 该源是否搜到结果
        public string Error;            // 失败/无结果的诊断文案（无则为 null）
    }

    /// <summary>
    /// 全源并发搜索服务（对照安卓版「陆续出结果」体验）。
    /// 与 UI 无关：每完成一个源即回调 onDone，调用方自行节流刷新；
    /// 全程支持 CancellationToken（点击结果/停止即取消）。
    /// </summary>
    public static class SearchService
    {
        /// <summary>单源搜索（含超时与诊断日志）。</summary>
        public static async Task<SearchOutcome> SearchOneAsync(
            BookSource src, string key, IPageFetcher fetcher, int timeoutMs)
        {
            var outcome = new SearchOutcome();
            try
            {
                var wb = new WebBook(fetcher);
                var found = await wb.SearchBookAsync(src, key).TimeoutAfter(timeoutMs);
                outcome.Books = found;
                if (found.Count > 0)
                {
                    outcome.Ok = true;
                    return outcome;
                }
                var detail = wb.DebugLog.Count > 0
                    ? wb.DebugLog[wb.DebugLog.Count - 1].Replace("search ", "[")
                        .Replace(" url=", "] ") + "bookList=0"
                    : "bookList=0";
                outcome.Error = detail;
            }
            catch (Exception ex)
            {
                var msg = ex is TimeoutException ? "超时" : ex.Message;
                if (msg.Length > 50) msg = msg.Substring(0, 50);
                outcome.Error = msg;
            }
            return outcome;
        }

        /// <summary>
        /// 并发跑一批源。每完成一个源回调 onDone（在调用方 UI 线程）；
        /// onProgress(done, total) 汇报进度。取消时抛 TaskCanceledException，
        /// 已排队的源不再启动，进行中的 HTTP 尽量忽略其结果。
        /// </summary>
        public static async Task RunRoundAsync(
            List<BookSource> sources, string key, IPageFetcher fetcher,
            int concurrency, int timeoutMs,
            Action<BookSource, SearchOutcome> onDone,
            Action<int, int> onProgress,
            CancellationToken ct)
        {
            var list = sources.ToList();
            var done = 0;
            var sem = new SemaphoreSlim(concurrency);
            var tasks = list.Select(async src =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    var outcome = await SearchOneAsync(src, key, fetcher, timeoutMs);
                    if (!ct.IsCancellationRequested)
                        onDone?.Invoke(src, outcome);
                    var d = Interlocked.Increment(ref done);
                    if (!ct.IsCancellationRequested)
                        onProgress?.Invoke(d, list.Count);
                }
                finally
                {
                    sem.Release();
                }
            }).ToList();
            await Task.WhenAll(tasks);
        }

        /// <summary>按匹配度排序：0=精确 1=前缀 2=包含。</summary>
        public static int MatchLevel(string name, string key)
        {
            if (string.IsNullOrEmpty(name)) return 3;
            var n = name.Trim();
            var k = (key ?? "").Trim();
            if (n == k) return 0;
            if (n.StartsWith(k, StringComparison.Ordinal)) return 1;
            return 2;
        }
    }

    internal static class SearchTaskExt
    {
        internal static async Task<T> TimeoutAfter<T>(this Task<T> task, int ms)
        {
            var done = await Task.WhenAny(task, Task.Delay(ms));
            if (done != task) throw new TimeoutException();
            return await task;
        }
    }
}
