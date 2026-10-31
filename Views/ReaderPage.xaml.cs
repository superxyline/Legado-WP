using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Phone.UI.Input;
using Windows.Storage;
using Windows.System.Display;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;
using legado_wp.Core;

namespace legado_wp.Views
{
    /// <summary>
    /// 阅读器：三种排版模式（上下滑动 / 平移覆盖 / 翻页动画），字号调节，进度自动保存。
    /// 平移覆盖/翻页动画按屏幕与字号估算分页，左右滑动或点按两侧翻页。
    /// 0.0.12.0：实体相机键翻页（半按=上一页/全按=下一页）+ 阅读时屏幕常亮。
    /// </summary>
    public sealed partial class ReaderPage : Page
    {
        private enum ReaderMode { Scroll, Cover, Curl }

        private TocPage.TocPayload _payload;
        private int _index;
        private double _fontSize = 21;
        private double _lineHeight = 38;
        private bool _busy;
        private string _chapterText = "";
        private bool _keepOn = true;
        private bool _volumeHintShown;
        private DisplayRequest _displayRequest;

        private ReaderMode _mode = ReaderMode.Scroll;
        private List<string> _pages = new List<string>();
        private int _pageIndex;
        private double _dragStartX;

        public ReaderPage()
        {
            InitializeComponent();
            LoadSettings();
        }

        // ---------------- 设置持久化 ----------------

        private void LoadSettings()
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                _fontSize = (double?)settings.Values["reader.fontSize"] ?? 21;
                _keepOn = (bool?)settings.Values["reader.keepOn"] ?? true;
                var m = settings.Values["reader.mode"] as string;
                switch (m)
                {
                    case "cover": _mode = ReaderMode.Cover; break;
                    case "curl": _mode = ReaderMode.Curl; break;
                    default: _mode = ReaderMode.Scroll; break;
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                settings.Values["reader.fontSize"] = _fontSize;
                settings.Values["reader.mode"] =
                    _mode == ReaderMode.Cover ? "cover" : _mode == ReaderMode.Curl ? "curl" : "scroll";
            }
            catch { }
        }

        // ---------------- 屏幕常亮 & 相机键翻页 ----------------

        private void AcquireDisplay()
        {
            try
            {
                if (_displayRequest == null) _displayRequest = new DisplayRequest();
                _displayRequest.RequestActive();
            }
            catch { /* 桌面或受限环境忽略 */ }
        }

        private void ReleaseDisplay()
        {
            try { _displayRequest?.RequestRelease(); }
            catch { }
        }

        /// <summary>
        /// Lumia 实体两段相机键翻页（UWP 不暴露音量键事件，相机键是唯一可用的硬件翻页键）：
        /// 半按=上一页，全按=下一页。无相机键的设备自动忽略。
        /// </summary>
        private void HookCameraKey(bool hook)
        {
            try
            {
                if (hook)
                {
                    HardwareButtons.CameraHalfPressed += OnCameraHalf;
                    HardwareButtons.CameraPressed += OnCameraFull;
                }
                else
                {
                    HardwareButtons.CameraHalfPressed -= OnCameraHalf;
                    HardwareButtons.CameraPressed -= OnCameraFull;
                }
            }
            catch { /* 无移动扩展的设备忽略 */ }
        }

        private void OnCameraHalf(object sender, CameraEventArgs e)
        {
            if (_payload == null) return;
            PrevPage();
            if (!_volumeHintShown)
            {
                _volumeHintShown = true;
                Footer.Text = "相机键翻页：半按=上一页，全按=下一页";
            }
        }

        private void OnCameraFull(object sender, CameraEventArgs e)
        {
            if (_payload == null) return;
            NextPage();
            if (!_volumeHintShown)
            {
                _volumeHintShown = true;
                Footer.Text = "相机键翻页：半按=上一页，全按=下一页";
            }
        }

        // ---------------- 导航 ----------------

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            _payload = e.Parameter as TocPage.TocPayload;
            if (_payload == null) { Frame.GoBack(); return; }
            _index = _payload.StartIndex;
            ApplyFont();
            ApplyMode(false);
            if (_keepOn) AcquireDisplay();
            HookCameraKey(true);
            _ = LoadChapterAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            SaveProgress();
            SaveSettings();
            HookCameraKey(false);
            ReleaseDisplay();
        }

        // ---------------- 模式 ----------------

        private void ApplyMode(bool reloadPage = true)
        {
            var paged = _mode != ReaderMode.Scroll;
            Scroller.Visibility = paged ? Visibility.Collapsed : Visibility.Visible;
            PagerGrid.Visibility = paged ? Visibility.Visible : Visibility.Collapsed;
            if (reloadPage)
            {
                if (paged) Paginate();
                else Content.Text = _chapterText;
            }
        }

        private void OnLayoutClick(object sender, RoutedEventArgs e) { }
        private void OnModeScroll(object sender, RoutedEventArgs e) { _mode = ReaderMode.Scroll; ApplyMode(); }
        private void OnModeCover(object sender, RoutedEventArgs e) { _mode = ReaderMode.Cover; ApplyMode(); }
        private void OnModeCurl(object sender, RoutedEventArgs e) { _mode = ReaderMode.Curl; ApplyMode(); }

        // ---------------- 字号 ----------------

        private void ApplyFont()
        {
            Content.FontSize = _fontSize;
            Content.LineHeight = _lineHeight;
            PagerContent.FontSize = _fontSize;
            PagerContent.LineHeight = _lineHeight;
        }

        private void OnFontUp(object sender, RoutedEventArgs e)
        {
            if (_fontSize < 40) { _fontSize += 2; _lineHeight += 3; ApplyFont(); if (_mode != ReaderMode.Scroll) { Paginate(); ShowCurrentPage(); } }
        }

        private void OnFontDown(object sender, RoutedEventArgs e)
        {
            if (_fontSize > 13) { _fontSize -= 2; _lineHeight -= 3; ApplyFont(); if (_mode != ReaderMode.Scroll) { Paginate(); ShowCurrentPage(); } }
        }

        // ---------------- 分页 ----------------

        /// <summary>
        /// 按屏幕尺寸与字号估算分页：段落允许跨页，逐行填满每页（页底无空白）。
        /// 段首的"　　"保留，跨页续行无缩进（标准电子书排版）。
        /// </summary>
        private void Paginate()
        {
            _pages = new List<string>();
            if (string.IsNullOrEmpty(_chapterText)) { _pages.Add(""); _pageIndex = 0; return; }

            var actual = PagerGrid.ActualHeight > 0 ? PagerGrid.ActualHeight : (Window.Current.Bounds.Height - 160);
            var linesPerPage = Math.Max(6, (int)(actual / _lineHeight));
            // 行宽估算：汉字符宽≈字号，页宽/字号≈每行字数（留 padding 余量）
            var width = PagerGrid.ActualWidth > 0 ? PagerGrid.ActualWidth : Window.Current.Bounds.Width;
            var charsPerLine = Math.Max(8, (int)((width - 32) / _fontSize));

            var page = new System.Text.StringBuilder();
            var lineUsed = 0;

            foreach (var para in _chapterText.Replace("\r", "").Split('\n'))
            {
                var remaining = para;
                while (remaining.Length > 0)
                {
                    if (lineUsed >= linesPerPage)
                    {
                        _pages.Add(page.ToString());
                        page.Clear();
                        lineUsed = 0;
                    }
                    var space = linesPerPage - lineUsed;              // 本页剩余行数
                    var take = space * charsPerLine;                  // 本页还能容纳的字符数
                    if (take >= remaining.Length)
                    {
                        page.AppendLine(remaining);
                        lineUsed += (remaining.Length + charsPerLine - 1) / charsPerLine;
                        remaining = "";
                    }
                    else
                    {
                        // 填满本页剩余行，段落剩余部分续到下页
                        page.AppendLine(remaining.Substring(0, take));
                        remaining = remaining.Substring(take);
                        lineUsed = linesPerPage;
                    }
                }
            }
            if (page.Length > 0) _pages.Add(page.ToString());
            if (_pages.Count == 0) _pages.Add("");
            if (_pageIndex >= _pages.Count) _pageIndex = _pages.Count - 1;
            if (_pageIndex < 0) _pageIndex = 0;
        }

        private void ShowCurrentPage()
        {
            if (_pageIndex >= 0 && _pageIndex < _pages.Count)
                PagerContent.Text = _pages[_pageIndex];
            UpdateFooter();
        }

        // ---------------- 翻页交互 ----------------

        private void OnManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            PagerShift.X = e.Cumulative.Translation.X;
        }

        private void OnManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            var dx = e.Cumulative.Translation.X;
            PagerShift.X = 0;
            if (dx < -60) NextPage();
            else if (dx > 60) PrevPage();
        }

        /// <summary>点按：左 1/4 上一页，右 1/4 下一页（阅读器惯例）。</summary>
        private void OnPagerTapped(object sender, TappedRoutedEventArgs e)
        {
            var x = e.GetPosition(PagerGrid).X;
            if (x < PagerGrid.ActualWidth * 0.25) PrevPage();
            else if (x > PagerGrid.ActualWidth * 0.75) NextPage();
        }

        private void NextPage()
        {
            if (_pageIndex < _pages.Count - 1)
            {
                _pageIndex++;
                AnimatePage(+1);
            }
            else if (_index < _payload.Chapters.Count - 1)
            {
                _index++;
                _pageIndex = 0;
                _ = LoadChapterAsync();
            }
        }

        private void PrevPage()
        {
            if (_pageIndex > 0)
            {
                _pageIndex--;
                AnimatePage(-1);
            }
            else if (_index > 0)
            {
                // 回上一章，加载完成后定位到末页
                _index--;
                _prevChapterToLast = true;
                _ = LoadChapterAsync();
            }
        }

        private bool _prevChapterToLast;

        /// <summary>翻页过渡：cover=平移，curl=带缓动的滑入（近似翻页动画）。</summary>
        private void AnimatePage(int direction)
        {
            ShowCurrentPage();
            if (_mode == ReaderMode.Cover)
            {
                var sb = new Storyboard();
                var anim = new DoubleAnimation
                {
                    From = direction * 60,
                    To = 0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(150))
                };
                Storyboard.SetTarget(anim, PagerShift);
                Storyboard.SetTargetProperty(anim, "X");
                sb.Children.Add(anim);
                sb.Begin();
            }
            else
            {
                // curl：滑入距离更大 + 轻微过冲（Elasticish）
                var sb = new Storyboard();
                var anim = new DoubleAnimation
                {
                    From = direction * PagerGrid.ActualWidth * 0.55,
                    To = 0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(320))
                };
                anim.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
                Storyboard.SetTarget(anim, PagerShift);
                Storyboard.SetTargetProperty(anim, "X");
                sb.Children.Add(anim);
                sb.Begin();
            }
        }

        // ---------------- 章节 ----------------

        private async Task LoadChapterAsync()
        {
            if (_busy) return;
            _busy = true;
            var chapter = _payload.Chapters[_index];
            ChapterTitle.Text = chapter.Title;
            Content.Text = "正在加载…";
            PagerContent.Text = "正在加载…";
            try
            {
                // 离线缓存优先（无网络可读）
                var cached = await OfflineStore.LoadChapterAsync(_payload.Book.BookUrl, _index);
                if (cached != null)
                {
                    _chapterText = cached;
                }
                else
                {
                    var wb = new WebBook();
                    var text = await wb.GetContentAsync(_payload.Source, chapter).TimeoutAfter(20000);
                    _chapterText = text.Length == 0 ? "（本章内容为空）" : text;
                }
            }
            catch (Exception ex)
            {
                _chapterText = "加载失败：" + ex.Message + "\n\n（如果未离线全文，无网络时无法阅读）";
            }
            finally
            {
                if (_mode == ReaderMode.Scroll)
                {
                    Content.Text = _chapterText;
                    Scroller.ChangeView(null, 0, null, true);
                }
                else
                {
                    Paginate();
                    if (_prevChapterToLast)
                    {
                        _pageIndex = _pages.Count - 1;
                        _prevChapterToLast = false;
                    }
                    ShowCurrentPage();
                }
                UpdateFooter();
                PrevBtn.IsEnabled = _index > 0;
                NextBtn.IsEnabled = _index < _payload.Chapters.Count - 1;
                _busy = false;
                SaveProgress();
            }
        }

        private void UpdateFooter()
        {
            var pagePart = _mode == ReaderMode.Scroll
                ? ""
                : ("  第" + (_pageIndex + 1) + "/" + _pages.Count + "页");
            Footer.Text = (_index + 1) + " / " + _payload.Chapters.Count + "章" + pagePart;
        }

        private void SaveProgress()
        {
            if (_payload?.Book == null) return;
            _ = Storage.SaveProgressAsync(_payload.Book.BookUrl, _index);
        }

        private void OnPrev(object sender, RoutedEventArgs e)
        {
            if (_index > 0) { _index--; _pageIndex = 0; _ = LoadChapterAsync(); }
        }

        private void OnNext(object sender, RoutedEventArgs e)
        {
            if (_index < _payload.Chapters.Count - 1) { _index++; _pageIndex = 0; _ = LoadChapterAsync(); }
        }

        private void OnToc(object sender, RoutedEventArgs e)
        {
            _payload.StartIndex = _index;
            Frame.GoBack();
        }

        // ---------------- 书签 ----------------

        private void OnBookmarkClick(object sender, RoutedEventArgs e)
        {
            // 占位：按钮的 MenuFlyout 自动弹出，无需处理点击本体
        }

        private async void OnAddBookmark(object sender, RoutedEventArgs e)
        {
            if (_payload == null) return;
            var mark = new Storage.Bookmark
            {
                BookUrl = _payload.Book.BookUrl,
                ChapterIndex = _index,
                ChapterTitle = _payload.Chapters[_index].Title,
                PageIndex = _mode == ReaderMode.Scroll ? 0 : _pageIndex,
                CreatedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds()
            };
            await Storage.AddBookmarkAsync(mark);
            Footer.Text = "✓ 已加书签：第" + (_index + 1) + "章 " + mark.ChapterTitle;
        }

        private sealed class BookmarkItem
        {
            public int ChapterIndex;
            public int PageIndex;
            public string Display;
        }

        private async void OnShowBookmarks(object sender, RoutedEventArgs e)
        {
            if (_payload == null) return;
            var marks = (await Storage.LoadBookmarksAsync())
                .Where(m => m.BookUrl == _payload.Book.BookUrl)
                .OrderByDescending(m => m.CreatedAt)
                .Select(m => new BookmarkItem
                {
                    ChapterIndex = m.ChapterIndex,
                    PageIndex = m.PageIndex,
                    Display = "第" + (m.ChapterIndex + 1) + "章 " + (m.ChapterTitle ?? "")
                              + (m.PageIndex > 0 ? " · P" + (m.PageIndex + 1) : "")
                })
                .ToList();
            if (marks.Count == 0)
            {
                Footer.Text = "暂无书签（书签菜单里点「添加书签」）";
                return;
            }

            var list = new ListView { MaxHeight = 360, IsItemClickEnabled = true };
            list.ItemTemplate = (DataTemplate)Windows.UI.Xaml.Markup.XamlReader.Load(
                "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
                "<TextBlock Text=\"{Binding Display}\" FontSize=\"15\" Margin=\"0,8\"/>" +
                "</DataTemplate>");
            list.ItemsSource = marks;

            var dlg = new ContentDialog
            {
                Title = "书签（" + marks.Count + "）· 点击跳转",
                Content = list,
                CloseButtonText = "关闭"
            };
            BookmarkItem picked = null;
            list.ItemClick += (s, args) => { picked = args.ClickedItem as BookmarkItem; dlg.Hide(); };

            await dlg.ShowAsync();
            if (picked != null)
            {
                _index = picked.ChapterIndex;
                if (_mode != ReaderMode.Scroll) _pageIndex = picked.PageIndex;
                await LoadChapterAsync();
            }
        }
    }
}
