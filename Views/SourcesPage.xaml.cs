using System;
using System.Collections.Generic;
using System.Linq;
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
            try
            {
                _sources = await Storage.LoadSourcesAsync();
                SourceList.ItemsSource = _sources;
                StatusHint.Visibility = _sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                SourceList.Visibility = _sources.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            }
            catch (Exception ex)
            {
                StatusHint.Text = "书源加载失败：" + ex.Message;
                StatusHint.Visibility = Visibility.Visible;
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
                var added = await Storage.ImportSourcesFromUrlAsync(url).TimeoutAfter(20000);
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
                SourceList.SelectionMode = ListViewSelectionMode.Single;
                SourceList.SelectedItems.Clear();
                ManageBtn.Content = "管理";
                StatusHint.Visibility = _sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
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
