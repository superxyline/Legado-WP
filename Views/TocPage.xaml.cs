using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Markup;
using Windows.UI.Xaml.Navigation;
using legado_wp.Core;

namespace legado_wp.Views
{
    /// <summary>
    /// 书籍详情页：简介 + 加入书架/离线全文/换源 + 目录。
    /// 搜索结果点击进入（不入架）；书架点击进入（AutoReadOnce 直达阅读位置）。
    /// </summary>
    public sealed partial class TocPage : Page
    {
        public sealed class TocPayload
        {
            public Book Book;
            public BookSource Source;
            public List<BookChapter> Chapters;
            public int StartIndex;
        }

        private class ChangeSourceItem : SearchBook
        {
            // XAML 绑定只认属性，不能是公有字段
            public string SourceName { get; set; }
        }

        /// <summary>书架入口标记：加载完成后直接进阅读器（上次位置）。</summary>
        public static bool AutoReadOnce;

        private TocPayload _payload;
        private bool _inShelf;
        private CancellationTokenSource _changeCts;

        public TocPage()
        {
            InitializeComponent();
        }

        private void OnBackClick(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            var book = e.Parameter as Book;
            if (book == null) { Frame.GoBack(); return; }
            BookTitle.Text = book.Name ?? "";
            BookMeta.Text = (book.Author ?? "") + " · " + (book.LastChapter ?? "");
            BookIntro.Text = string.IsNullOrEmpty(book.Intro) ? "（暂无简介）" : book.Intro;

            Busy.IsActive = true;
            StatusHint.Text = "正在加载目录…";
            try
            {
                var source = (await Storage.LoadSourcesAsync())
                    .FirstOrDefault(s => s.BookSourceUrl == book.SourceUrl);
                if (source == null)
                {
                    source = await TrySourceFromOfflineAsync(book);
                    if (source == null) { StatusHint.Text = "书源已被删除且无离线数据"; return; }
                }

                var chapters = new List<BookChapter>();
                var offlineMeta = await OfflineStore.LoadMetaAsync(book.BookUrl);
                if (offlineMeta != null && offlineMeta.Chapters != null && offlineMeta.Chapters.Count > 0)
                {
                    // 有离线数据：目录/源全部用缓存，不依赖网络
                    chapters = offlineMeta.Chapters;
                    source = offlineMeta.Source ?? source;
                    OfflineBtn.Content = "已离线 " + offlineMeta.Chapters.Count + " 章 ✓";
                }
                else
                {
                    var wb = new WebBook();
                    chapters = await wb.GetChapterListAsync(source, book).TimeoutAfter(20000);
                }
                if (chapters.Count == 0) { StatusHint.Text = "目录为空"; return; }

                var progress = await Storage.LoadProgressAsync();
                int start;
                progress.TryGetValue(book.BookUrl, out start);
                if (start >= chapters.Count) start = 0;

                ChapterList.ItemsSource = chapters;
                ChapterList.ScrollIntoView(chapters[Math.Max(0, start - 1)]);
                BookMeta.Text = (book.Author ?? "") + " · " + (book.LastChapter ?? "")
                                + " · 源：" + (source.BookSourceName ?? "");
                StatusHint.Text = "";
                _payload = new TocPayload { Book = book, Source = source, Chapters = chapters, StartIndex = start };

                _inShelf = (await Storage.LoadShelfAsync()).Any(b => b.BookUrl == book.BookUrl);
                UpdateShelfButton();

                if (AutoReadOnce)
                {
                    AutoReadOnce = false;
                    Frame.Navigate(typeof(ReaderPage), _payload);
                }
            }
            catch (Exception ex)
            {
                StatusHint.Text = "目录加载失败：" + ex.Message;
            }
            finally
            {
                Busy.IsActive = false;
            }
        }

        private static async Task<BookSource> TrySourceFromOfflineAsync(Book book)
        {
            var meta = await OfflineStore.LoadMetaAsync(book.BookUrl);
            return meta?.Source;
        }

        private void UpdateShelfButton()
        {
            ShelfBtn.Content = _inShelf ? "已在书架 ✓" : "加入书架";
            ShelfBtn.IsEnabled = !_inShelf;
        }

        private async void OnShelfClick(object sender, RoutedEventArgs e)
        {
            if (_payload == null || _inShelf) return;
            await Storage.AddToShelfAsync(_payload.Book);
            _inShelf = true;
            UpdateShelfButton();
        }

        private async void OnOfflineClick(object sender, RoutedEventArgs e)
        {
            if (_payload == null) return;
            OfflineBusy.IsActive = true;
            var wb = new WebBook();
            var chapters = _payload.Chapters;
            for (var i = 0; i < chapters.Count; i++)
            {
                OfflineBtn.Content = "离线中 " + (i + 1) + "/" + chapters.Count;
                try
                {
                    var cached = await OfflineStore.LoadChapterAsync(_payload.Book.BookUrl, i);
                    if (cached == null)
                    {
                        var text = await wb.GetContentAsync(_payload.Source, chapters[i]).TimeoutAfter(20000);
                        await OfflineStore.SaveChapterAsync(_payload.Book.BookUrl, i, text);
                    }
                }
                catch { /* 单章失败继续 */ }
            }
            await OfflineStore.SaveMetaAsync(new OfflineStore.OfflineMeta
            {
                BookName = _payload.Book.Name,
                Author = _payload.Book.Author,
                BookUrl = _payload.Book.BookUrl,
                Intro = _payload.Book.Intro,
                LastChapter = _payload.Book.LastChapter,
                TocUrl = _payload.Book.TocUrl,
                ChapterCount = chapters.Count,
                SavedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                Source = _payload.Source,
                Chapters = chapters
            });
            OfflineBtn.Content = "已离线 " + chapters.Count + " 章 ✓";
            OfflineBusy.IsActive = false;
        }

        // ---------------- 换源 ----------------

        /// <summary>
        /// 换源：用当前书名在所有启用书源里搜同书，弹窗流式展示结果（可停止），
        /// 选中后切换到新源重新拉取详情+目录，并迁移阅读进度/书架记录。
        /// </summary>
        private async void OnChangeSourceClick(object sender, RoutedEventArgs e)
        {
            if (_payload == null) return;
            var key = (_payload.Book.Name ?? "").Trim();
            if (string.IsNullOrEmpty(key)) return;

            var sources = (await Storage.LoadSourcesAsync()).Where(s => s.Enabled).ToList();
            if (sources.Count == 0) { StatusHint.Text = "没有可用书源"; return; }

            _changeCts?.Cancel();
            _changeCts = new CancellationTokenSource();
            var ct = _changeCts.Token;

            var items = new List<ChangeSourceItem>();
            var seen = new HashSet<string>();
            ChangeSourceItem picked = null;

            // 结果列表（弹窗内）
            var list = new ListView { Height = 340, IsItemClickEnabled = true, Padding = new Thickness(0, 8, 0, 0) };
            list.ItemTemplate = (DataTemplate)XamlReader.Load(@"
<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
  <StackPanel Margin='0,6'>
    <TextBlock Text='{Binding Name}' FontSize='16'/>
    <TextBlock Text='{Binding Author}' FontSize='12' Foreground='Gray'/>
    <TextBlock Text='{Binding SourceName}' FontSize='12' Foreground='#1565C0'/>
  </StackPanel>
</DataTemplate>");
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
            var panel = new StackPanel();
            panel.Children.Add(status);
            panel.Children.Add(list);

            var dlg = new ContentDialog
            {
                Title = "换源：搜索「" + key + "」",
                Content = panel,
                PrimaryButtonText = "取消",
                SecondaryButtonText = "停止"
            };
            list.ItemClick += (s, args) =>
            {
                picked = args.ClickedItem as ChangeSourceItem;
                dlg.Hide();
            };
            dlg.SecondaryButtonClick += (s, args) => _changeCts?.Cancel();
            // 点结果即关闭弹窗执行换源
            var showTask = dlg.ShowAsync();

            // 流式搜索（回调在 UI 线程）
            try
            {
                await SearchService.RunRoundAsync(sources, key, new HttpPageFetcher(), 4, 10000,
                    (src, o) =>
                    {
                        if (!o.Ok) return;
                        foreach (var sb in o.Books)
                        {
                            if (!seen.Add(sb.BookUrl)) continue;
                            items.Add(new ChangeSourceItem
                            {
                                Name = sb.Name, Author = sb.Author, BookUrl = sb.BookUrl,
                                CoverUrl = sb.CoverUrl, Intro = sb.Intro,
                                LastChapter = sb.LastChapter, SourceUrl = sb.SourceUrl,
                                SourceName = src.BookSourceName
                            });
                        }
                        list.ItemsSource = items
                            .OrderBy(i => SearchService.MatchLevel(i.Name, key))
                            .ThenBy(i => i.SourceName).ToList();
                    },
                    (d, total) =>
                    {
                        status.Text = "搜索中… " + d + "/" + total + "，找到 " + items.Count + " 条";
                    },
                    ct);
                status.Text = items.Count > 0
                    ? "搜索完成，共 " + items.Count + " 个源，点一条换源"
                    : "没有搜到其他源";
            }
            catch (OperationCanceledException)
            {
                status.Text = "已停止，找到 " + items.Count + " 个源，点一条换源";
            }

            // 等用户选择（或取消）
            await showTask;
            if (picked == null) return;
            await ApplyChangeSourceAsync(picked);
        }

        /// <summary>执行换源：切源 + 拉详情/目录 + 进度与书架数据迁移。</summary>
        private async Task ApplyChangeSourceAsync(ChangeSourceItem picked)
        {
            var src = (await Storage.LoadSourcesAsync())
                .FirstOrDefault(s => s.BookSourceUrl == picked.SourceUrl);
            if (src == null) { StatusHint.Text = "书源不存在"; return; }

            StatusHint.Text = "正在切换书源…";
            Busy.IsActive = true;
            try
            {
                var wb = new WebBook();
                var book = await wb.GetBookInfoAsync(src, picked).TimeoutAfter(15000);
                var chapters = await wb.GetChapterListAsync(src, book).TimeoutAfter(20000);
                if (chapters.Count == 0) { StatusHint.Text = "新源目录为空，换源取消"; return; }

                var oldUrl = _payload.Book.BookUrl;
                var newUrl = book.BookUrl;
                int start = 0;
                var progress = await Storage.LoadProgressAsync();
                int oldStart;
                if (oldUrl != newUrl)
                {
                    // 换源后书记录 URL 会变：迁移阅读进度与书架记录，旧离线缓存保留
                    if (progress.TryGetValue(oldUrl, out oldStart) && oldStart < chapters.Count)
                        start = oldStart;
                    if (progress.Remove(oldUrl))
                    {
                        progress[newUrl] = start;
                        await Storage.SaveProgressAsync(progress);
                    }
                    var shelf = await Storage.LoadShelfAsync();
                    if (shelf.Any(b => b.BookUrl == oldUrl))
                    {
                        await Storage.RemoveFromShelfAsync(oldUrl);
                        await Storage.AddToShelfAsync(book);
                    }
                }
                else
                {
                    progress.TryGetValue(oldUrl, out start);
                    if (start >= chapters.Count) start = 0;
                }

                _payload = new TocPayload { Book = book, Source = src, Chapters = chapters, StartIndex = start };
                BookTitle.Text = book.Name ?? "";
                BookMeta.Text = (book.Author ?? "") + " · " + (book.LastChapter ?? "")
                                + " · 源：" + (src.BookSourceName ?? "");
                BookIntro.Text = string.IsNullOrEmpty(book.Intro) ? "（暂无简介）" : book.Intro;
                ChapterList.ItemsSource = chapters;
                ChapterList.ScrollIntoView(chapters[Math.Max(0, start - 1)]);

                _inShelf = (await Storage.LoadShelfAsync()).Any(b => b.BookUrl == book.BookUrl);
                UpdateShelfButton();

                var meta = await OfflineStore.LoadMetaAsync(book.BookUrl);
                OfflineBtn.Content = (meta != null && meta.Chapters != null && meta.Chapters.Count > 0)
                    ? "已离线 " + meta.Chapters.Count + " 章 ✓" : "离线全文";

                StatusHint.Text = "已换源：" + src.BookSourceName
                    + (start > 0 ? "（阅读进度已迁移到第 " + (start + 1) + " 章）" : "")
                    + (oldUrl != newUrl ? "，需重新离线" : "");
            }
            catch (Exception ex)
            {
                StatusHint.Text = "换源失败：" + ex.Message;
            }
            finally
            {
                Busy.IsActive = false;
            }
        }

        private void OnChapterClick(object sender, ItemClickEventArgs e)
        {
            if (_payload == null) return;
            var index = _payload.Chapters.IndexOf(e.ClickedItem as BookChapter);
            if (index < 0) return;
            _payload.StartIndex = index;
            Frame.Navigate(typeof(ReaderPage), _payload);
        }
    }
}
