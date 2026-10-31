using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using legado_wp.Core;

namespace legado_wp.Views
{
    /// <summary>书源管理：URL 导入（单对象/数组，按 bookSourceUrl 去重）、单项启停/删除、多选批量管理。</summary>
    public sealed partial class SourcesPage : Page
    {
        private List<BookSource> _sources = new List<BookSource>();
        private bool _suppressToggle = true; // 列表绑定初始化会批量触发 Checked/Unchecked，期间不落盘
        private bool _manageMode;            // 多选管理模式

        public SourcesPage()
        {
            InitializeComponent();
            Loaded += async (s, e) => await ReloadAsync();
        }

        private void OnBackClick(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }

        private async Task ReloadAsync()
        {
            _suppressToggle = true;
            var step = "存储读取";
            try
            {
                _sources = await Storage.LoadSourcesAsync();

                step = "清空选中";
                // UWP 15063 坑：单选模式下 SelectedItems.Clear() 抛 0x8000FFFF，只在多选模式清
                if (SourceList.SelectionMode == ListViewSelectionMode.Multiple)
                    SourceList.SelectedItems.Clear();

                step = "列表渲染";
                SourceList.ItemsSource = _sources;

                step = "界面状态";
                StatusHint.Visibility = _sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                SourceList.Visibility = _sources.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            }
            catch (Exception ex)
            {
                StatusHint.Text = "书源加载失败[" + step + "]：" + ex.Message;
                StatusHint.Visibility = Visibility.Visible;
                _ = BookshelfPage.LogLaunchCrashAsync("sources-reload@" + step, ex);
            }
            finally
            {
                _suppressToggle = false;
            }
        }

        private async void OnImportClick(object sender, RoutedEventArgs e)
        {
            var url = UrlBox.Text?.Trim();
            if (string.IsNullOrEmpty(url)) return;
            StatusHint.Visibility = Visibility.Visible;
            StatusHint.Text = "正在下载书源…";
            try
            {
                var added = await Storage.ImportSourcesFromUrlAsync(url, n =>
                {
                    var msg = n < 0 ? "下载中断，自动重试…" : "下载书源中 " + Storage.FormatBytes(n);
                    var ignore = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low,
                        () => StatusHint.Text = msg);
                });
                await ReloadAsync();
                StatusHint.Text = "";
                UrlBox.Text = "";
                StatusHint.Visibility = _sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (_sources.Count != 0) StatusHint.Visibility = Visibility.Collapsed;
                var dlg = new ContentDialog
                {
                    Title = "导入完成",
                    Content = "共 " + _sources.Count + " 个书源（本次新增 " + added + " 个）",
                    CloseButtonText = "好"
                };
                await dlg.ShowAsync();
            }
            catch (Exception ex)
            {
                StatusHint.Text = "导入失败：" + ex.Message;
            }
        }

        private void OnScanClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(ScanPage));
        }

        /// <summary>编辑书源 variable（JS 里 java.getVariable/setVariable 读写的就是它）。</summary>
        private async void OnVariableClick(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            var src = btn?.DataContext as BookSource;
            if (src == null) return;
            var box = new TextBox
            {
                Text = src.Variable ?? "",
                PlaceholderText = "书源变量（JSON 或任意文本，书源 JS 通过 java.getVariable() 读取）",
                AcceptsReturn = true,
                Height = 160
            };
            var dlg = new ContentDialog
            {
                Title = "书源变量：" + src.BookSourceName,
                Content = box,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消"
            };
            if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            {
                src.Variable = box.Text;
                await Storage.SaveSourceVariableAsync(src);
            }
        }

        private async void OnToggle(object sender, RoutedEventArgs e)
        {
            if (_suppressToggle) return;
            try { await Storage.SaveSourcesAsync(_sources); }
            catch { /* 保存失败不崩，下次进入重载 */ }
        }

        private async void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            var src = btn?.DataContext as BookSource;
            if (src == null) return;
            var dlg = new ContentDialog
            {
                Title = "删除书源",
                Content = "删除「" + src.BookSourceName + "」？",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消"
            };
            if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            {
                _sources.RemoveAll(s => s.BookSourceUrl == src.BookSourceUrl);
                await Storage.SaveSourcesAsync(_sources);
                await ReloadAsync();
            }
        }

        // ---------------- 多选管理 ----------------

        /// <summary>「管理/完成」：切换多选模式。</summary>
        private void OnManageClick(object sender, RoutedEventArgs e)
        {
            _manageMode = !_manageMode;
            try
            {
                if (_manageMode)
                {
                    ManageBar.Visibility = Visibility.Visible;
                    SourceList.SelectionMode = ListViewSelectionMode.Multiple;
                    ManageBtn.Content = "完成";
                    StatusHint.Text = "点按书源进行勾选";
                    StatusHint.Visibility = Visibility.Visible;
                }
                else
                {
                    ManageBar.Visibility = Visibility.Collapsed;
                    // UWP 15063 坑：SelectionMode Multiple→Single 时若仍有选中项会直接崩溃，
                    // 必须先清空选中再切模式
                    SourceList.SelectedItems.Clear();
                    SourceList.SelectionMode = ListViewSelectionMode.Single;
                    ManageBtn.Content = "管理";
                    StatusHint.Visibility = _sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch { /* 模式切换异常不崩，退回单选 */ _manageMode = false; }
        }

        private void OnSelectAllClick(object sender, RoutedEventArgs e)
        {
            foreach (var item in SourceList.Items)
                if (!SourceList.SelectedItems.Contains(item))
                    SourceList.SelectedItems.Add(item);
        }

        private void OnInvertClick(object sender, RoutedEventArgs e)
        {
            foreach (var item in SourceList.Items)
            {
                if (SourceList.SelectedItems.Contains(item))
                    SourceList.SelectedItems.Remove(item);
                else
                    SourceList.SelectedItems.Add(item);
            }
        }

        private async void OnBatchEnableClick(object sender, RoutedEventArgs e)
            => await BatchSetEnabledAsync(true);

        private async void OnBatchDisableClick(object sender, RoutedEventArgs e)
            => await BatchSetEnabledAsync(false);

        private async Task BatchSetEnabledAsync(bool enabled)
        {
            var picked = SourceList.SelectedItems.Cast<BookSource>().ToList();
            if (picked.Count == 0)
            {
                StatusHint.Text = "先勾选书源";
                StatusHint.Visibility = Visibility.Visible;
                return;
            }
            foreach (var s in picked) s.Enabled = enabled;
            await Storage.SaveSourcesAsync(_sources);
            SourceList.SelectedItems.Clear();
            await ReloadAsync();
            StatusHint.Text = (enabled ? "已启用 " : "已禁用 ") + picked.Count + " 个书源";
            StatusHint.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// 校验所选书源：逐源跑 搜索→详情→目录→正文，弹窗显示进度，
        /// 完成后列出失败源（可一键禁用失败源）。
        /// </summary>
        private async void OnCheckClick(object sender, RoutedEventArgs e)
        {
            var picked = SourceList.SelectedItems.Cast<BookSource>().ToList();
            if (picked.Count == 0)
            {
                StatusHint.Text = "先勾选书源";
                StatusHint.Visibility = Visibility.Visible;
                return;
            }

            var progress = new ProgressBar { Maximum = picked.Count, Value = 0, Margin = new Thickness(0, 4, 0, 0) };
            var status = new TextBlock { Text = "准备校验…", TextWrapping = TextWrapping.Wrap, FontSize = 13 };
            var panel = new StackPanel();
            panel.Children.Add(progress);
            panel.Children.Add(status);

            var dlg = new ContentDialog
            {
                Title = "书源校验（" + picked.Count + " 个）",
                Content = panel,
                CloseButtonText = "后台继续",
                SecondaryButtonText = "停止"
            };
            var cancelled = false;
            dlg.SecondaryButtonClick += (s, args) => cancelled = true;
            // 不 await 弹窗（校验在后台跑，弹窗可先关）
            var showTask = dlg.ShowAsync();

            var okCount = 0;
            var lastFail = "";
            var done = 0;
            List<CheckSource.Result> all;
            try
            {
                // 进度只在回调里更新 UI（节流），结果统一从返回值聚合（避免跨线程 List.Add）
                var lastFailLocal = "";
                all = await CheckSource.CheckAllAsync(picked, "我的", 3,
                    r =>
                    {
                        var d = Interlocked.Increment(ref done);
                        if (r.Ok) Interlocked.Increment(ref okCount); else lastFailLocal = r.Source.BookSourceName;
                        var failName = lastFailLocal;
                        _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                        {
                            progress.Value = d;
                            status.Text = d + "/" + picked.Count + " 通过 " + okCount
                                + (failName.Length > 0 ? " · 最近失败：" + failName : "");
                        });
                    },
                    new System.Threading.CancellationTokenSource(180000).Token);
            }
            catch (OperationCanceledException) { all = null; cancelled = true; }
            catch (Exception ex)
            {
                status.Text = "校验异常：" + ex.Message;
                all = null;
            }

            var failed = all != null ? all.Where(r => !r.Ok).ToList() : new List<CheckSource.Result>();
            var ok = all != null ? all.Count(r => r.Ok) : okCount;

            // 确保进度弹窗已关闭再弹总结（UWP 不允许两个 ContentDialog）
            try { await showTask; } catch { }

            // 总结
            var summary = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = (cancelled ? "已停止。" : "校验完成。")
                    + "通过 " + ok + "/" + picked.Count
                    + (failed.Count > 0 ? "\n\n失败：" + string.Join("\n", failed.Select(f =>
                        "· " + f.Display + " — " + f.Error)) : "")
            };
            var doneDlg = new ContentDialog
            {
                Title = "校验结果",
                Content = new ScrollViewer { Content = summary, MaxHeight = 400 },
                PrimaryButtonText = failed.Count > 0 ? "禁用失败源" : "好",
                CloseButtonText = "关闭"
            };
            var result = await doneDlg.ShowAsync();
            if (result == ContentDialogResult.Primary && failed.Count > 0)
            {
                var badUrls = new HashSet<string>(failed.Select(f => f.Source.BookSourceUrl));
                foreach (var s in _sources.Where(s => badUrls.Contains(s.BookSourceUrl))) s.Enabled = false;
                await Storage.SaveSourcesAsync(_sources);
                await ReloadAsync();
            }
        }

        /// <summary>清空全部书源（二次确认；已离线的书仍可读）。</summary>
        private async void OnClearAllClick(object sender, RoutedEventArgs e)
        {
            if (_sources.Count == 0)
            {
                StatusHint.Text = "书源已经是空的";
                StatusHint.Visibility = Visibility.Visible;
                return;
            }
            var dlg = new ContentDialog
            {
                Title = "清空全部书源",
                Content = "将删除全部 " + _sources.Count + " 个书源，不可恢复！\n\n已离线全文的书籍仍可阅读；书架与阅读进度不受影响。",
                PrimaryButtonText = "确认清空",
                CloseButtonText = "取消"
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            await Storage.SaveSourcesAsync(new List<BookSource>());
            SourceList.SelectedItems.Clear();
            await ReloadAsync();
            StatusHint.Text = "已清空全部书源";
            StatusHint.Visibility = Visibility.Visible;
        }

        private async void OnBatchDeleteClick(object sender, RoutedEventArgs e)
        {
            var picked = SourceList.SelectedItems.Cast<BookSource>().ToList();
            if (picked.Count == 0)
            {
                StatusHint.Text = "先勾选书源";
                StatusHint.Visibility = Visibility.Visible;
                return;
            }
            var dlg = new ContentDialog
            {
                Title = "批量删除书源",
                Content = "删除所选 " + picked.Count + " 个书源？（已离线的书籍仍可阅读）",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消"
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

            var urls = new HashSet<string>(picked.Select(s => s.BookSourceUrl));
            _sources.RemoveAll(s => urls.Contains(s.BookSourceUrl));
            await Storage.SaveSourcesAsync(_sources);
            SourceList.SelectedItems.Clear();
            await ReloadAsync();
            StatusHint.Text = "已删除 " + picked.Count + " 个书源";
            StatusHint.Visibility = Visibility.Visible;
        }
    }
}
