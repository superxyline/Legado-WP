using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using legado_wp.Core;

namespace legado_wp.Views
{
    /// <summary>
    /// 书架：搜索/分组过滤、批量管理（分组/移出）、本地 TXT/EPUB 导入、
    /// 章节更新检测（进 App 自动触发 + 手动「更新」按钮）。
    /// </summary>
    public sealed partial class BookshelfPage : Page
    {
        private List<Book> _shelf = new List<Book>();
        private string _searchText = "";
        private string _group = "";
        private bool _manageMode;

        public BookshelfPage()
        {
            try
            {
                InitializeComponent();
            }
            catch (Exception ex)
            {
                // 启动期 XAML 异常兜底：记录后不构造失败（老机型闪退诊断）
                _ = LogLaunchCrashAsync("bookshelf-ctor", ex);
            }
            Loaded += async (s, e) =>
            {
                try
                {
                    await ReloadAsync();
                    await AutoCheckAsync(false);
                }
                catch { /* 启动期加载失败不崩，下次进入重试 */ }
            };
        }

        internal static async Task LogLaunchCrashAsync(string tag, Exception ex)
        {
            try
            {
                var file = await Windows.Storage.ApplicationData.Current.LocalFolder.CreateFileAsync("crash.log",
                    Windows.Storage.CreationCollisionOption.OpenIfExists);
                await Windows.Storage.FileIO.AppendTextAsync(file,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + tag + "] "
                    + ex.GetType().Name + ": " + ex.Message + "\r\n---\r\n");
            }
            catch { }
        }

        // ---------------- 列表与过滤 ----------------

        private async Task ReloadAsync()
        {
            _shelf = await Storage.LoadShelfAsync();
            RebuildGroupFilter();
            ApplyFilter();

            try
            {
                var dir = Windows.Storage.ApplicationData.Current.LocalFolder;
                var file = await dir.CreateFileAsync("app.log",
                    Windows.Storage.CreationCollisionOption.OpenIfExists);
                await Windows.Storage.FileIO.AppendTextAsync(file,
                    DateTime.Now.ToString("HH:mm:ss") + " 书架加载 " + _shelf.Count + " 本\r\n");
            }
            catch { }
        }

        private void RebuildGroupFilter()
        {
            var groups = _shelf.Select(b => b.Group ?? "").Where(g => g.Length > 0)
                .Distinct().OrderBy(g => g).ToList();
            GroupFilter.Items.Clear();
            GroupFilter.Items.Add("全部分组");
            foreach (var g in groups) GroupFilter.Items.Add(g);
            var sel = _group.Length == 0 ? "全部分组" : _group;
            var idx = groups.IndexOf(_group);
            GroupFilter.SelectedIndex = _group.Length == 0 ? 0 : idx + 1;
        }

        private void ApplyFilter()
        {
            IEnumerable<Book> q = _shelf;
            if (!string.IsNullOrEmpty(_searchText))
                q = q.Where(b => ((b.Name ?? "") + (b.Author ?? "")).IndexOf(_searchText, StringComparison.OrdinalIgnoreCase) >= 0);
            if (_group.Length > 0)
                q = q.Where(b => (b.Group ?? "") == _group);
            var list = q.OrderByDescending(b => string.IsNullOrEmpty(b.LastChapter) ? 0 : 1)
                .ThenBy(b => b.Name).ToList();
            // 先清选中再换 ItemsSource，避免多选模式下替换列表触发崩溃
            // （UWP 15063：单选模式下 SelectedItems.Clear() 会抛 0x8000FFFF，只在多选时清）
            if (ShelfList.SelectionMode == ListViewSelectionMode.Multiple)
                ShelfList.SelectedItems.Clear();
            ShelfList.ItemsSource = list;
            var empty = _shelf.Count == 0;
            EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            ShelfList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            _searchText = SearchBox.Text?.Trim() ?? "";
            ApplyFilter();
        }

        private void OnGroupFilterChanged(object sender, SelectionChangedEventArgs e)
        {
            var sel = GroupFilter.SelectedItem as string;
            if (sel == null) return;
            _group = sel == "全部分组" ? "" : sel;
            ApplyFilter();
        }

        // ---------------- 点击 / 长按菜单 ----------------

        private void OnBookClick(object sender, ItemClickEventArgs e)
        {
            if (_manageMode) return; // 管理模式下点按=勾选，由 ListView 处理
            var book = e.ClickedItem as Book;
            if (book == null) return;
            // 进入即清更新角标
            if (book.NewCount > 0)
            {
                book.NewCount = 0;
                _ = Storage.SaveShelfAsync(_shelf);
            }
            TocPage.AutoReadOnce = true;
            Frame.Navigate(typeof(TocPage), book);
        }

        // 长按/右键直接交给 ContextFlyout（触摸长按自动弹出菜单）。
        // ⚠️ 不能再挂 Holding/RightTapped 弹 ContentDialog：与 ContextFlyout 弹出竞态，
        // 弹窗叠弹窗在 15063 上直接闪退（0.0.15.3 实锤）。

        private async void OnRemoveMenu(object sender, RoutedEventArgs e)
        {
            var book = (sender as MenuFlyoutItem)?.DataContext as Book;
            if (book == null) return;
            await RemoveBookAsync(book);
        }

        private async void OnGroupMenu(object sender, RoutedEventArgs e)
        {
            var book = (sender as MenuFlyoutItem)?.DataContext as Book;
            if (book == null) return;
            var group = await PromptGroupAsync(book.Group);
            if (group == null) return;
            book.Group = group.Length == 0 ? null : group;
            await Storage.SaveShelfAsync(_shelf);
            await ReloadAsync();
        }

        private async Task ConfirmRemove(object sender)
        {
            var panel = sender as StackPanel;
            var book = panel?.DataContext as Book;
            if (book == null) return;
            await RemoveBookAsync(book);
        }

        private async Task RemoveBookAsync(Book book)
        {
            var dlg = new ContentDialog
            {
                Title = "移出书架",
                Content = "把《" + book.Name + "》移出书架？" + (LocalBookStore.IsLocal(book) ? "（本地书文件保留）" : "（已离线的内容会保留）"),
                PrimaryButtonText = "移出",
                CloseButtonText = "取消"
            };
            if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            {
                await Storage.RemoveFromShelfAsync(book.BookUrl);
                await ReloadAsync();
            }
        }

        // ---------------- 批量管理 ----------------

        private void OnManageClick(object sender, RoutedEventArgs e)
        {
            _manageMode = !_manageMode;
            try
            {
                ManageBar.Visibility = _manageMode ? Visibility.Visible : Visibility.Collapsed;
                // UWP 15063 坑：SelectionMode Multiple→Single 时若仍有选中项会直接崩溃，
                // 必须先清空选中再切模式
                if (!_manageMode) ShelfList.SelectedItems.Clear();
                ShelfList.SelectionMode = _manageMode
                    ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Single;
                ApplyFilter();
            }
            catch { _manageMode = false; }
        }

        private void OnSelectAllClick(object sender, RoutedEventArgs e)
        {
            foreach (var item in ShelfList.Items)
                if (!ShelfList.SelectedItems.Contains(item))
                    ShelfList.SelectedItems.Add(item);
        }

        private async void OnBatchGroupClick(object sender, RoutedEventArgs e)
        {
            var picked = ShelfList.SelectedItems.Cast<Book>().ToList();
            if (picked.Count == 0) { UpdateStatus.Text = "先勾选书籍"; return; }
            var group = await PromptGroupAsync(picked[0].Group);
            if (group == null) return;
            foreach (var b in picked) b.Group = group.Length == 0 ? null : group;
            await Storage.SaveShelfAsync(_shelf);
            ShelfList.SelectedItems.Clear();
            await ReloadAsync();
            UpdateStatus.Text = "已设置 " + picked.Count + " 本的分组" + (group.Length > 0 ? "：" + group : "（未分组）");
        }

        private async void OnBatchRemoveClick(object sender, RoutedEventArgs e)
        {
            var picked = ShelfList.SelectedItems.Cast<Book>().ToList();
            if (picked.Count == 0) { UpdateStatus.Text = "先勾选书籍"; return; }
            var dlg = new ContentDialog
            {
                Title = "批量移出书架",
                Content = "移出所选 " + picked.Count + " 本？（已离线的内容会保留）",
                PrimaryButtonText = "移出",
                CloseButtonText = "取消"
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            foreach (var b in picked) await Storage.RemoveFromShelfAsync(b.BookUrl);
            ShelfList.SelectedItems.Clear();
            await ReloadAsync();
            UpdateStatus.Text = "已移出 " + picked.Count + " 本";
        }

        /// <summary>分组输入对话框。返回 null=取消；空串=清除分组。</summary>
        private async Task<string> PromptGroupAsync(string current)
        {
            var existing = _shelf.Select(b => b.Group ?? "").Where(g => g.Length > 0)
                .Distinct().OrderBy(g => g).ToList();
            var hint = existing.Count > 0 ? "（已有分组：" + string.Join(" / ", existing) + "）" : "";
            var box = new TextBox { Text = current ?? "", PlaceholderText = "分组名，留空 = 未分组" };
            var dlg = new ContentDialog
            {
                Title = "设置分组 " + hint,
                Content = box,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消"
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return null;
            return box.Text?.Trim() ?? "";
        }

        // ---------------- 更新检测 ----------------

        private async Task AutoCheckAsync(bool force)
        {
            var books = _shelf;
            if (books.Count == 0) return;
            if (!force && books.All(b => LocalBookStore.IsLocal(b)
                || DateTimeOffset.Now.ToUnixTimeMilliseconds() - b.LastCheck < UpdateHelper.MinIntervalMinutes * 60L * 1000L))
                return;
            await RunCheckAsync(force);
        }

        private async void OnUpdateClick(object sender, RoutedEventArgs e)
        {
            await RunCheckAsync(true);
        }

        private async Task RunCheckAsync(bool force)
        {
            try
            {
                UpdateStatus.Text = "更新检测中…";
                var dispatcher = Windows.UI.Core.CoreWindow.GetForCurrentThread().Dispatcher;
                var updated = await UpdateHelper.CheckShelfAsync(
                    _shelf,
                    (done, total) => _ = dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal,
                        () => UpdateStatus.Text = "更新检测中 " + done + "/" + total + "…"),
                    () => _ = dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal,
                        () => ApplyFilter()),
                    force);
                UpdateStatus.Text = updated > 0 ? "检测完成：" + updated + " 本有新章节 🆕" : "检测完成：暂无更新";
            }
            catch (Exception ex)
            {
                UpdateStatus.Text = "更新检测失败：" + ex.Message;
            }
        }

        // ---------------- 本地导入 ----------------

        private void OnImportClick(object sender, RoutedEventArgs e) { /* Flyout 弹出 */ }

        private async void OnImportTxt(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker
                {
                    SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary
                };
                picker.FileTypeFilter.Add(".txt");
                var file = await picker.PickSingleFileAsync();
                if (file == null) return;
                UpdateStatus.Text = "正在导入 TXT…";
                var book = await LocalBookStore.ImportTxtAsync(file);
                await Storage.AddToShelfAsync(book);
                UpdateStatus.Text = "已导入《" + book.Name + "》" + book.ChapterCount + " 章";
                Frame.Navigate(typeof(TocPage), book);
            }
            catch (Exception ex)
            {
                UpdateStatus.Text = "TXT 导入失败：" + ex.Message;
            }
        }

        private async void OnImportEpub(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker
                {
                    SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary
                };
                picker.FileTypeFilter.Add(".epub");
                var file = await picker.PickSingleFileAsync();
                if (file == null) return;
                UpdateStatus.Text = "正在导入 EPUB…";
                var book = await LocalBookStore.ImportEpubAsync(file);
                await Storage.AddToShelfAsync(book);
                UpdateStatus.Text = "已导入《" + book.Name + "》" + book.ChapterCount + " 章";
                Frame.Navigate(typeof(TocPage), book);
            }
            catch (Exception ex)
            {
                UpdateStatus.Text = "EPUB 导入失败：" + ex.Message;
            }
        }

        // ---------------- 导航 ----------------

        private void OnSearchClick(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(SearchPage));
        private void OnSourcesClick(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(SourcesPage));
        private void OnReplaceClick(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(ReplacePage));
        private void OnProbeClick(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(MainPage));

        // ---------------- 备份 / 恢复 ----------------

        private async void OnBackupClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var json = await Storage.ExportBackupAsync();
                var picker = new Windows.Storage.Pickers.FileSavePicker
                {
                    SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary
                };
                picker.FileTypeChoices.Add("备份文件", new List<string> { ".json" });
                picker.SuggestedFileName = "legado-wp-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmm");
                var file = await picker.PickSaveFileAsync();
                if (file == null) return;
                await Windows.Storage.FileIO.WriteTextAsync(file, json);
                var dlg = new ContentDialog
                {
                    Title = "备份完成",
                    Content = "已保存到 " + file.Name,
                    CloseButtonText = "好"
                };
                await dlg.ShowAsync();
            }
            catch (Exception ex)
            {
                var dlg = new ContentDialog
                {
                    Title = "备份失败",
                    Content = ex.Message,
                    CloseButtonText = "好"
                };
                await dlg.ShowAsync();
            }
        }

        private async void OnRestoreClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker
                {
                    SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary
                };
                picker.FileTypeFilter.Add(".json");
                var file = await picker.PickSingleFileAsync();
                if (file == null) return;
                var json = await Windows.Storage.FileIO.ReadTextAsync(file);

                var dlg = new ContentDialog
                {
                    Title = "恢复备份",
                    Content = "合并 = 与现有数据并存（推荐）；替换 = 清空后导入备份内容。",
                    PrimaryButtonText = "合并",
                    SecondaryButtonText = "替换",
                    CloseButtonText = "取消"
                };
                var result = await dlg.ShowAsync();
                if (result == ContentDialogResult.None) return;
                var summary = await Storage.ImportBackupAsync(json, result == ContentDialogResult.Secondary);
                await ReloadAsync();
                var done = new ContentDialog
                {
                    Title = "恢复完成",
                    Content = summary,
                    CloseButtonText = "好"
                };
                await done.ShowAsync();
            }
            catch (Exception ex)
            {
                var dlg = new ContentDialog
                {
                    Title = "恢复失败",
                    Content = ex.Message,
                    CloseButtonText = "好"
                };
                await dlg.ShowAsync();
            }
        }
    }
}
