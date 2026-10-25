using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using legado_wp.Core;

namespace legado_wp.Views
{
    /// <summary>
    /// 搜索页：全部启用书源并发搜索（每源 12s 超时，并发 8）。
    /// </summary>
    public sealed partial class SearchPage : Page
    {
        private class ResultItem : SearchBook
        {
            public string SourceName;
        }

        private CancellationTokenSource _cts;

        public SearchPage()
        {
            InitializeComponent();
        }

        private void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter) DoSearch();
        }

        private async void OnSearchClick(object sender, RoutedEventArgs e) => DoSearch();

        private async void DoSearch()
        {
            var key = KeyBox.Text?.Trim();
            if (string.IsNullOrEmpty(key)) return;

            var sources = (await Storage.LoadSourcesAsync()).Where(s => s.Enabled).ToList();
            if (sources.Count == 0)
            {
                StatusHint.Text = "没有可用书源，请先到「书源」页导入";
                return;
            }

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            ResultList.ItemsSource = null;
            StatusHint.Text = "搜索中…（首轮）";
            Progress.Visibility = Visibility.Visible;

            var results = new System.Collections.Concurrent.ConcurrentBag<ResultItem>();
            var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
            var okSet = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();

            // 单源搜索（含诊断记录），返回 true=本轮有结果
            // fetcher 由轮次注入：每轮全新 HttpPageFetcher，隔离可能坏掉的 HttpClient 状态
            Func<BookSource, int, IPageFetcher, Task<bool>> searchOne = async (src, timeoutMs, fetcher) =>
            {
                try
                {
                    var wb = new WebBook(fetcher);
                    var found = await wb.SearchBookAsync(src, key).TimeoutAfter(timeoutMs);
                    if (found.Count > 0)
                    {
                        okSet[src.BookSourceUrl] = 1;
                        foreach (var sb in found)
                            results.Add(new ResultItem
                            {
                                Name = sb.Name, Author = sb.Author, BookUrl = sb.BookUrl,
                                CoverUrl = sb.CoverUrl, Intro = sb.Intro,
                                LastChapter = sb.LastChapter, SourceUrl = sb.SourceUrl,
                                SourceName = src.BookSourceName
                            });
                        return true;
                    }
                    var detail = wb.DebugLog.Count > 0
                        ? wb.DebugLog[wb.DebugLog.Count - 1].Replace("search ", "[")
                            .Replace(" url=", "] ") + "bookList=0"
                        : "bookList=0";
                    errors.Add(src.BookSourceName + ": " + detail);
                    return false;
                }
                catch (Exception ex)
                {
                    var msg = ex is TimeoutException ? "超时" : ex.Message;
                    if (msg.Length > 50) msg = msg.Substring(0, 50);
                    errors.Add(src.BookSourceName + ": " + msg);
                    return false;
                }
            };

            // 并发跑一批源（带进度显示 + 轮级全新 fetcher 隔离）
            Func<IEnumerable<BookSource>, int, int, Task> runRound = async (round, timeoutMs, concurrency) =>
            {
                var list0 = round.ToList();
                var done = 0;
                var fetcher = new HttpPageFetcher(); // 每轮独立 HttpClient，坏状态不跨轮
                var sem = new SemaphoreSlim(concurrency);
                await Task.WhenAll(list0.Select(async src =>
                {
                    await sem.WaitAsync(ct);
                    try { await searchOne(src, timeoutMs, fetcher); }
                    finally
                    {
                        sem.Release();
                        var d = System.Threading.Interlocked.Increment(ref done);
                        StatusHint.Text = "搜索中… " + d + "/" + list0.Count;
                    }
                }));
            };

            // 第一轮：全源 4 并发 10s（老芯片并发越保守越稳）
            await runRound(sources, 10000, 4);
            ct.ThrowIfCancellationRequested();

            // 第二轮：瞬态补救——首轮无结果/失败的源再试一次
            var retrySources = sources.Where(s => !okSet.ContainsKey(s.BookSourceUrl)).ToList();
            if (retrySources.Count > 0)
            {
                errors = new System.Collections.Concurrent.ConcurrentBag<string>();

                if (okSet.Count == 0)
                {
                    // 全灭场景：全新 fetcher 探活 6 个源（8s 放宽），避免网络已断时空跑 40 个
                    var probe = retrySources.Take(6).ToList();
                    StatusHint.Text = "网络探活中…";
                    await runRound(probe, 8000, 3);
                    if (okSet.Count == 0)
                    {
                        // 探活全败：显示具体错误类型（超时/DNS/拒连），便于定位
                        Progress.Visibility = Visibility.Collapsed;
                        var listDead = results.OrderBy(r => MatchLevel(r.Name, key)).ThenBy(r => r.SourceName).ToList();
                        ResultList.ItemsSource = listDead;
                        var probeErr = errors.Count > 0
                            ? "\n错误详情: " + string.Join("；", errors.Take(3))
                            : "";
                        StatusHint.Text = "所有源无响应\n" + probeErr + "\n请确认手机浏览器能否打开网站后重试";
                        return;
                    }
                    // 探活有结果：跑剩余源
                    var probed = new HashSet<string>(probe.Select(p => p.BookSourceUrl));
                    await runRound(retrySources.Where(s => !probed.Contains(s.BookSourceUrl) && !okSet.ContainsKey(s.BookSourceUrl)), 8000, 4);
                }
                else
                {
                    // 部分失败：直接重试失败源
                    await runRound(retrySources, 8000, 4);
                }
                ct.ThrowIfCancellationRequested();

                foreach (var s in retrySources)
                    if (!okSet.ContainsKey(s.BookSourceUrl))
                        errors.Add(s.BookSourceName + ": 重试仍无结果");
            }

            Progress.Visibility = Visibility.Collapsed;
            // 匹配度排序：精确等于 > 书名以关键词开头 > 包含（同级按源名+书名）
            var list = results
                .OrderBy(r => MatchLevel(r.Name, key))
                .ThenBy(r => r.SourceName)
                .ThenBy(r => r.Name)
                .ToList();
            ResultList.ItemsSource = list;

            // 诊断状态行：成功源数 / 有结果数 / 失败源数 + 首条错误
            var okSources = list.Select(r => r.SourceName).Distinct().Count();
            var failMsg = errors.Count > 0
                ? "\n失败示例: " + string.Join("；", errors.Take(3))
                : "";
            StatusHint.Text = list.Count > 0
                ? ""
                : "没有找到「" + key + "」\n" + sources.Count + " 个源："
                  + okSources + " 个有结果，" + errors.Count + " 个无结果/失败" + failMsg;
        }

        /// <summary>0=精确匹配，1=书名以关键词开头，2=其余（包含）。</summary>
        private static int MatchLevel(string name, string key)
        {
            if (string.IsNullOrEmpty(name)) return 3;
            var n = name.Trim();
            var k = (key ?? "").Trim();
            if (n == k) return 0;
            if (n.StartsWith(k, StringComparison.Ordinal)) return 1;
            return 2;
        }

        private void OnBackClick(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }

        private async void OnResultClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as ResultItem;
            if (item == null) return;
            StatusHint.Text = "正在打开《" + item.Name + "》…";

            var src = (await Storage.LoadSourcesAsync())
                .FirstOrDefault(s => s.BookSourceUrl == item.SourceUrl);
            if (src == null) { StatusHint.Text = "书源不存在"; return; }

            try
            {
                var wb = new WebBook();
                var book = await wb.GetBookInfoAsync(src, item).TimeoutAfter(15000);
                var chapters = await wb.GetChapterListAsync(src, book).TimeoutAfter(20000);
                if (chapters.Count == 0) { StatusHint.Text = "目录为空"; return; }
                // 不自动入架：详情页由用户决定「加入书架」或「离线全文」
                StatusHint.Text = "";
                Frame.Navigate(typeof(TocPage), book);
            }
            catch (Exception ex)
            {
                StatusHint.Text = "打开失败：" + ex.Message;
            }
        }
    }

    internal static class TaskExt
    {
        internal static async Task<T> TimeoutAfter<T>(this Task<T> task, int ms)
        {
            var done = await Task.WhenAny(task, Task.Delay(ms));
            if (done != task) throw new TimeoutException();
            return await task;
        }
    }
}
