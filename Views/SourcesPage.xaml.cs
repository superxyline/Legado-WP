using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using legado_wp.Core;

namespace legado_wp.Views
{
    /// <summary>书源管理：URL 导入（单对象/数组，按 bookSourceUrl 去重）、启停、删除。</summary>
    public sealed partial class SourcesPage : Page
    {
        private List<BookSource> _sources = new List<BookSource>();
        private bool _suppressToggle = true; // 列表绑定初始化会批量触发 Checked/Unchecked，期间不落盘

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
    }
}
