using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using legado_wp.Core;

namespace legado_wp.Views
{
    /// <summary>
    /// 书籍详情页：简介 + 加入书架/离线全文 + 目录。
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

        /// <summary>书架入口标记：加载完成后直接进阅读器（上次位置）。</summary>
        public static bool AutoReadOnce;

        private TocPayload _payload;
        private bool _inShelf;

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
