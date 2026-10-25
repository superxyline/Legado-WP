using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using legado_wp.Core;

namespace legado_wp.Views
{
    /// <summary>
    /// 搜索页：全部启用书源并发搜索。
    /// 流式体验（对照安卓版）：每个源一出结果就刷新列表（400ms 节流，保持滚动位置）；
    /// 点击结果或点「停止」立即取消剩余搜索。
    /// </summary>
    public sealed partial class SearchPage : Page
    {
        private class ResultItem : SearchBook
        {
            // XAML 绑定只认属性，不能是公有字段
            public string SourceName { get; set; }
        }

        private CancellationTokenSource _cts;
        private readonly List<ResultItem> _results = new List<ResultItem>();
        private string _key;
        private readonly DispatcherTimer _flushTimer;
        private bool _flushPending;

        public SearchPage()
        {
            InitializeComponent();
            _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _flushTimer.Tick += (s, e) => { _flushTimer.Stop(); _flushPending = false; Flush(); };
        }

        private void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter) DoSearch();
        }

        private void OnSearchClick(object sender, RoutedEventArgs e) => DoSearch();

        private void OnStopClick(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            StopBtn.IsEnabled = false;
            StatusHint.Text = "已停止，共找到 " + _results.Count + " 条";
        }

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
            _key = key;

            _results.Clear();
            ResultList.ItemsSource = null;
            StatusHint.Text = "搜索中…（首轮）";
            Progress.Visibility = Visibility.Visible;
            StopBtn.IsEnabled = true;

            var errors = new ConcurrentBag<string>();
            var okSet = new ConcurrentDictionary<string, byte>();

            // 每源完成即入列 + 节流刷新（流式出结果）
            Action<BookSource, SearchOutcome> onDone = (src, o) =>
            {
                if (o.Ok)
                {
                    okSet[src.BookSourceUrl] = 1;
                    foreach (var sb in o.Books)
                        _results.Add(new ResultItem
                        {
                            Name = sb.Name, Author = sb.Author, BookUrl = sb.BookUrl,
                            CoverUrl = sb.CoverUrl, Intro = sb.Intro,
                            LastChapter = sb.LastChapter, SourceUrl = sb.SourceUrl,
                            SourceName = src.BookSourceName
                        });
                    ScheduleFlush();
                }
                else if (o.Error != null)
                {
                    errors.Add(src.BookSourceName + ": " + o.Error);
                }
            };
            Action<int, int> onProgress = (d, total) =>
            {
                StatusHint.Text = "搜索中… " + d + "/" + total + "，已找到 " + _results.Count + " 条";
            };

            try
            {
                // 第一轮：全源 4 并发 10s（老芯片并发越保守越稳）
                await SearchService.RunRoundAsync(sources, key, new HttpPageFetcher(),
                    4, 10000, onDone, onProgress, ct);

                // 第二轮：瞬态补救——首轮无结果/失败的源再试一次
                var retrySources = sources.Where(s => !okSet.ContainsKey(s.BookSourceUrl)).ToList();
                if (retrySources.Count > 0)
                {
                    errors = new ConcurrentBag<string>();

                    if (okSet.Count == 0)
                    {
                        // 全灭场景：全新 fetcher 探活 6 个源（8s 放宽），避免网络已断时空跑全部
                        StatusHint.Text = "网络探活中…";
                        var probe = retrySources.Take(6).ToList();
                        await SearchService.RunRoundAsync(probe, key, new HttpPageFetcher(),
                            3, 8000, onDone, onProgress, ct);
                        if (okSet.Count == 0)
                        {
                            Flush();
                            var probeErr = errors.Count > 0
                                ? "\n错误详情: " + string.Join("；", errors.Take(3))
                                : "";
                            StatusHint.Text = "所有源无响应" + probeErr + "\n请确认手机浏览器能否打开网站后重试";
                            return;
                        }
                        // 探活有结果：跑剩余源
                        var probed = new HashSet<string>(probe.Select(p => p.BookSourceUrl));
                        await SearchService.RunRoundAsync(
                            retrySources.Where(s => !probed.Contains(s.BookSourceUrl)).ToList(),
                            key, new HttpPageFetcher(), 4, 8000, onDone, onProgress, ct);
                    }
                    else
                    {
                        // 部分失败：直接重试失败源
                        await SearchService.RunRoundAsync(retrySources, key, new HttpPageFetcher(),
                            4, 8000, onDone, onProgress, ct);
                    }

                    foreach (var s in retrySources)
                        if (!okSet.ContainsKey(s.BookSourceUrl))
                            errors.Add(s.BookSourceName + ": 重试仍无结果");
                }

                // 匹配度排序：精确等于 > 书名以关键词开头 > 包含（同级按源名+书名）
                Flush();
                Progress.Visibility = Visibility.Collapsed;
                StopBtn.IsEnabled = false;

                var okSources = _results.Select(r => r.SourceName).Distinct().Count();
                var failMsg = errors.Count > 0
                    ? "\n失败示例: " + string.Join("；", errors.Take(3))
                    : "";
                StatusHint.Text = _results.Count > 0
                    ? ""
                    : "没有找到「" + key + "」\n" + sources.Count + " 个源："
                      + okSources + " 个有结果，" + errors.Count + " 个无结果/失败" + failMsg;
            }
            catch (OperationCanceledException)
            {
                // 用户点了结果/停止：保留已找到的结果
                Flush();
                StatusHint.Text = "已停止，共找到 " + _results.Count + " 条";
            }
            finally
            {
                Progress.Visibility = Visibility.Collapsed;
                StopBtn.IsEnabled = false;
                _flushTimer.Stop();
                _flushPending = false;
            }
        }

        /// <summary>400ms 节流：攒一批结果再刷，避免高频重绑卡 UI。</summary>
        private void ScheduleFlush()
        {
            if (_flushPending) return;
            _flushPending = true;
            _flushTimer.Start();
        }

        /// <summary>按匹配度排序重绑列表，并保持用户当前滚动位置。</summary>
        private void Flush()
        {
            var sv = FindDescendant<ScrollViewer>(ResultList);
            var offset = sv?.VerticalOffset ?? 0;
            var list = _results
                .OrderBy(r => SearchService.MatchLevel(r.Name, _key))
                .ThenBy(r => r.SourceName)
                .ThenBy(r => r.Name)
                .ToList();
            ResultList.ItemsSource = list;
            if (sv != null && offset > 0) sv.ChangeView(null, offset, null, true);
        }

        private static T FindDescendant<T>(DependencyObject root) where T : class
        {
            if (root == null) return null;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T t) return t;
                var found = FindDescendant<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        private void OnBackClick(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }

        private async void OnResultClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as ResultItem;
            if (item == null) return;
            // 点了结果就停掉剩余搜索（省流量省电，结果已在列表中）
            _cts?.Cancel();
            StopBtn.IsEnabled = false;

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
