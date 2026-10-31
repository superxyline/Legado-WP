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
        private double _lineExtra = 17;
        private int _theme;
        private bool _indent = true;
        private bool _busy;
        private string _chapterText = "";
        private bool _keepOn = true;
        private bool _volumeHintShown;
        private DisplayRequest _displayRequest;
        private DispatcherTimer _autoTimer;

        private ReaderMode _mode = ReaderMode.Scroll;
        private List<string> _pages = new List<string>();
        private int _pageIndex;
        private double _dragStartX;

        /// <summary>阅读主题表：[0]=羊皮 [1]=纯白 [2]=护眼 [3]=夜间。</summary>
        private static readonly string[][] Themes =
        {
            new[] { "#FFF7F3E8", "#FF2B2B2B", "#FFEFE7D0", "#FF6D5B2F", "#FFE8DDBB", "#FF9A8B60" }, // bg fg barBg barFg bar2Bg footerFg
            new[] { "#FFFFFFFF", "#FF333333", "#FFF2F2F2", "#FF555555", "#FFE8E8E8", "#FF888888" },
            new[] { "#FFC7EDCC", "#FF2B3A2B", "#FFB5DFBC", "#FF2B3A2B", "#FFA8D6B0", "#FF3D5C42" },
            new[] { "#FF121212", "#FFAAAAAA", "#FF1E1E1E", "#FF999999", "#FF252525", "#FF666666" }
        };

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
                _lineExtra = (double?)settings.Values["reader.lineExtra"] ?? 17;
                _theme = (int?)settings.Values["reader.theme"] ?? 0;
                _indent = (bool?)settings.Values["reader.indent"] ?? true;
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
                settings.Values["reader.lineExtra"] = _lineExtra;
                settings.Values["reader.theme"] = _theme;
                settings.Values["reader.indent"] = _indent;
                settings.Values["reader.mode"] =
                    _mode == ReaderMode.Cover ? "cover" : _mode == ReaderMode.Curl ? "curl" : "scroll";
            }
            catch { }
        }

        // ---------------- 主题 / 行距 / 缩进 ----------------

        private Windows.UI.Color ParseColor(string hex)
        {
            return Windows.UI.ColorHelper.FromArgb(
                Convert.ToByte(hex.Substring(1, 2), 16),
                Convert.ToByte(hex.Substring(3, 2), 16),
                Convert.ToByte(hex.Substring(5, 2), 16),
                Convert.ToByte(hex.Substring(7, 2), 16));
        }

        private void ApplyTheme()
        {
            try
            {
                var t = Themes[Math.Max(0, Math.Min(Themes.Length - 1, _theme))];
                Background = new Windows.UI.Xaml.Media.SolidColorBrush(ParseColor(t[0]));
                var fg = new Windows.UI.Xaml.Media.SolidColorBrush(ParseColor(t[1]));
                Content.Foreground = fg;
                PagerContent.Foreground = fg;
                ChapterTitle.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(ParseColor(t[3]));
                Footer.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(ParseColor(t[5]));
                var bar = new Windows.UI.Xaml.Media.SolidColorBrush(ParseColor(t[2]));
                var bar2 = new Windows.UI.Xaml.Media.SolidColorBrush(ParseColor(t[4]));
                ((Border)VisualTreeHelperGetParent(ChapterTitle)).Background = bar;
                ((Grid)((FrameworkElement)PrevBtn.Parent)).Background = bar;
                ((Grid)((FrameworkElement)AutoBtn.Parent)).Background = bar2;
            }
            catch { }
        }

        private static Windows.UI.Xaml.DependencyObject VisualTreeHelperGetParent(Windows.UI.Xaml.DependencyObject o)
        {
            return Windows.UI.Xaml.Media.VisualTreeHelper.GetParent(o);
        }

        /// <summary>段首缩进开关：关闭时去掉净化阶段加的"　　"。</summary>
        private string FormatText(string text)
        {
            if (_indent || string.IsNullOrEmpty(text)) return text;
            return text.Replace("\n　　", "\n").TrimStart('　', ' ');
        }

        private void OnThemeParchment(object sender, RoutedEventArgs e) { _theme = 0; ApplyTheme(); SaveSettings(); }
        private void OnThemeWhite(object sender, RoutedEventArgs e) { _theme = 1; ApplyTheme(); SaveSettings(); }
        private void OnThemeGreen(object sender, RoutedEventArgs e) { _theme = 2; ApplyTheme(); SaveSettings(); }
        private void OnThemeDark(object sender, RoutedEventArgs e) { _theme = 3; ApplyTheme(); SaveSettings(); }

        private void SetLineExtra(double v)
        {
            _lineExtra = v;
            ApplyFont();
            if (_mode != ReaderMode.Scroll) { Paginate(); ShowCurrentPage(); }
            SaveSettings();
        }
        private void OnLineTight(object sender, RoutedEventArgs e) { SetLineExtra(11); }
        private void OnLineWide(object sender, RoutedEventArgs e) { SetLineExtra(17); }
        private void OnLineLoose(object sender, RoutedEventArgs e) { SetLineExtra(25); }

        private void OnToggleIndent(object sender, RoutedEventArgs e)
        {
            _indent = !_indent;
            SaveSettings();
            if (_mode == ReaderMode.Scroll)
            {
                Content.Text = FormatText(_chapterText);
                Scroller.ChangeView(null, 0, null, true);
            }
            else
            {
                Paginate();
                ShowCurrentPage();
            }
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
            ApplyTheme();
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
            StopAuto();
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
                else Content.Text = FormatText(_chapterText);
            }
        }

        private void OnLayoutClick(object sender, RoutedEventArgs e) { }
        private void OnModeScroll(object sender, RoutedEventArgs e) { _mode = ReaderMode.Scroll; ApplyMode(); }
        private void OnModeCover(object sender, RoutedEventArgs e) { _mode = ReaderMode.Cover; ApplyMode(); }
        private void OnModeCurl(object sender, RoutedEventArgs e) { _mode = ReaderMode.Curl; ApplyMode(); }

        // ---------------- 字号 ----------------

        private void ApplyFont()
        {
            _lineHeight = _fontSize + _lineExtra;
            Content.FontSize = _fontSize;
            Content.LineHeight = _lineHeight;
            PagerContent.FontSize = _fontSize;
            PagerContent.LineHeight = _lineHeight;
        }

        private void OnFontUp(object sender, RoutedEventArgs e)
        {
            if (_fontSize < 40) { _fontSize += 2; ApplyFont(); if (_mode != ReaderMode.Scroll) { Paginate(); ShowCurrentPage(); } }
        }

        private void OnFontDown(object sender, RoutedEventArgs e)
        {
            if (_fontSize > 13) { _fontSize -= 2; ApplyFont(); if (_mode != ReaderMode.Scroll) { Paginate(); ShowCurrentPage(); } }
        }

        // ---------------- 自动翻页 ----------------

        private void StopAuto()
        {
            if (_autoTimer != null) { _autoTimer.Stop(); _autoTimer = null; }
            AutoBtn.Content = "自动";
        }

        private void StartAuto(double seconds)
        {
            StopAuto();
            _autoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            _autoTimer.Tick += OnAutoTick;
            _autoTimer.Start();
            AutoBtn.Content = "自动▶";
        }

        private void OnAutoStop(object sender, RoutedEventArgs e) { StopAuto(); }
        private void OnAutoSlow(object sender, RoutedEventArgs e) { StartAuto(12); }
        private void OnAutoMid(object sender, RoutedEventArgs e) { StartAuto(8); }
        private void OnAutoFast(object sender, RoutedEventArgs e) { StartAuto(4); }

        /// <summary>自动翻页心跳：滚动模式走 ChangeView 步进，分页模式直接翻页；翻页动作会自动暂停。</summary>
        private void OnAutoTick(object sender, object e)
        {
            if (_busy || _payload == null) return;
            if (_mode == ReaderMode.Scroll)
            {
                var offset = Scroller.VerticalOffset;
                var extent = Scroller.ExtentHeight - Scroller.ViewportHeight;
                if (extent <= 0 || offset >= extent - 2)
                {
                    if (_index < _payload.Chapters.Count - 1) { _index++; _ = LoadChapterAsync(); }
                    else StopAuto();
                }
                else
                {
                    Scroller.ChangeView(null, offset + _lineHeight * 2, null);
                }
            }
            else
            {
                NextPage();
            }
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
                if (LocalBookStore.IsLocal(_payload.Book))
                {
                    // 本地书（TXT/EPUB）：直接读本地文件，不走网络与离线缓存
                    var text = await LocalBookStore.GetChapterTextAsync(_payload.Book, _index).TimeoutAfter(10000);
                    _chapterText = string.IsNullOrEmpty(text) ? "（本章内容为空）" : text;
                }
                else
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
                // 替换净化规则（书名/书源作用域，离线缓存同样生效）
                _chapterText = await ContentProcessor.ApplyContentAsync(
                    _chapterText, _payload.Book?.Name, _payload.Source?.BookSourceUrl);
                ChapterTitle.Text = await ContentProcessor.ApplyTitleAsync(
                    chapter.Title, _payload.Book?.Name, _payload.Source?.BookSourceUrl)
                    ?? chapter.Title;
            }
            catch (Exception ex)
            {
                _chapterText = "加载失败：" + ex.Message + "\n\n（如果未离线全文，无网络时无法阅读）";
            }
            finally
            {
                if (_mode == ReaderMode.Scroll)
                {
                    Content.Text = FormatText(_chapterText);
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
                LocatePendingSearch();
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

        // ---------------- 正文搜索 ----------------

        private sealed class TextHit
        {
            public int ChapterIndex;
            public string Display;
        }

        /// <summary>
        /// 正文搜索：搜当前章 + 已离线缓存的章节（逐章读缓存文本），点结果跳章。
        /// 分页模式且命中在目标章时，自动跳到含关键字的页。
        /// </summary>
        private async void OnSearchTextClick(object sender, RoutedEventArgs e)
        {
            if (_payload == null) return;
            var box = new TextBox { PlaceholderText = "关键字（在当前章与已离线章节中搜索）" };
            var dlg = new ContentDialog
            {
                Title = "搜索正文",
                Content = box,
                PrimaryButtonText = "搜索",
                CloseButtonText = "取消"
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            var kw = (box.Text ?? "").Trim();
            if (kw.Length == 0) return;

            StopAuto();
            Footer.Text = "搜索中…";
            var hits = new List<TextHit>();
            var total = _payload.Chapters.Count;
            var cachedCount = 0;

            for (var i = 0; i < total; i++)
            {
                string text;
                if (LocalBookStore.IsLocal(_payload.Book))
                    text = await LocalBookStore.GetChapterTextAsync(_payload.Book, i);
                else
                    text = await OfflineStore.LoadChapterAsync(_payload.Book.BookUrl, i);
                if (text == null) continue; // 未离线的章跳过
                cachedCount++;

                var pos = text.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
                var inChapter = 0;
                while (pos >= 0 && hits.Count < 80)
                {
                    inChapter++;
                    if (inChapter == 1)
                    {
                        var start = Math.Max(0, pos - 10);
                        var len = Math.Min(text.Length - start, 24);
                        var ctx = text.Substring(start, len).Replace("\n", " ").Replace("\r", "");
                        hits.Add(new TextHit
                        {
                            ChapterIndex = i,
                            Display = "第" + (i + 1) + "章 " + (_payload.Chapters[i].Title ?? "")
                                      + " · …" + ctx + "…"
                        });
                    }
                    pos = text.IndexOf(kw, pos + kw.Length, StringComparison.OrdinalIgnoreCase);
                }
                if (hits.Count >= 80) break;
            }

            var list = new ListView { MaxHeight = 380, IsItemClickEnabled = true };
            list.ItemTemplate = (DataTemplate)Windows.UI.Xaml.Markup.XamlReader.Load(
                "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
                "<TextBlock Text=\"{Binding Display}\" FontSize=\"14\" Margin=\"0,6\" TextWrapping=\"Wrap\"/>" +
                "</DataTemplate>");
            list.ItemsSource = hits;

            var hint = cachedCount == 0
                ? "本书没有已离线/本地的章节可搜索（先在详情页离线全文）"
                : "共搜 " + cachedCount + " 个缓存章节，命中 " + hits.Count + " 处";
            var result = new ContentDialog
            {
                Title = "正文搜索：" + kw,
                Content = hits.Count > 0 ? (object)list : new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "关闭"
            };
            TextHit pick = null;
            list.ItemClick += (s, args) => { pick = args.ClickedItem as TextHit; result.Hide(); };
            await result.ShowAsync();

            if (pick != null)
            {
                _index = pick.ChapterIndex;
                _pendingSearchKw = kw;
                if (_mode != ReaderMode.Scroll) _pageIndex = 0;
                await LoadChapterAsync();
            }
        }

        /// <summary>待跳转的搜索关键字：章节加载完用于分页定位到命中页。</summary>
        private string _pendingSearchKw;

        /// <summary>章节加载后若有待查关键字，把分页定位到第一处命中的页（滚动模式给页脚提示）。</summary>
        private void LocatePendingSearch()
        {
            if (string.IsNullOrEmpty(_pendingSearchKw) || _chapterText == null) return;
            var kw = _pendingSearchKw;
            _pendingSearchKw = null;
            var pos = _chapterText.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
            if (pos < 0) return;
            if (_mode == ReaderMode.Scroll)
            {
                Footer.Text = "本章含「" + kw + "」（未离线章节不在搜索范围）";
            }
            else
            {
                var display = FormatText(_chapterText);
                if (!ReferenceEquals(display, _chapterText)) pos = display.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
                if (pos < 0) return;
                // 按分页时每页字符数估算所在页
                var perPage = _pages.Count > 0 && display.Length > 0
                    ? display.Length / _pages.Count : int.MaxValue;
                if (perPage > 0 && perPage != int.MaxValue)
                {
                    var page = Math.Min(_pages.Count - 1, pos / perPage);
                    _pageIndex = page;
                    ShowCurrentPage();
                }
            }
        }
    }
}
