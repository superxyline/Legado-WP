using System;
using System.Collections.Generic;
using System.Linq;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using legado_wp.Core;

namespace legado_wp.Views
{
    public sealed partial class BookshelfPage : Page
    {
        private List<Book> _shelf = new List<Book>();

        public BookshelfPage()
        {
            InitializeComponent();
            Loaded += async (s, e) => await ReloadAsync();
        }

        private async System.Threading.Tasks.Task ReloadAsync()
        {
            _shelf = await Storage.LoadShelfAsync();
            ShelfList.ItemsSource = _shelf
                .OrderByDescending(b => string.IsNullOrEmpty(b.LastChapter) ? 0 : 1)
                .ThenBy(b => b.Name)
                .ToList();
            EmptyHint.Visibility = _shelf.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ShelfList.Visibility = _shelf.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

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

        private void OnBookClick(object sender, ItemClickEventArgs e)
        {
            // 书架点击：直达上次阅读位置
            TocPage.AutoReadOnce = true;
            Frame.Navigate(typeof(TocPage), e.ClickedItem as Book);
        }

        private void OnSearchClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(SearchPage));
        }

        private void OnSourcesClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(SourcesPage));
        }

        private void OnProbeClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(MainPage));
        }

        private async void OnBookHolding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != Windows.UI.Input.HoldingState.Started) return;
            await ConfirmRemove(sender);
        }

        private async void OnBookRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            await ConfirmRemove(sender);
        }

        /// <summary>ContextFlyout 菜单项（长按弹菜单，比裸 Holding 可靠）。</summary>
        private async void OnRemoveMenu(object sender, RoutedEventArgs e)
        {
            var item = sender as MenuFlyoutItem;
            var book = item?.DataContext as Book;
            if (book == null) return;
            await RemoveBookAsync(book);
        }

        private async System.Threading.Tasks.Task ConfirmRemove(object sender)
        {
            var panel = sender as StackPanel;
            var book = panel?.DataContext as Book;
            if (book == null) return;
            await RemoveBookAsync(book);
        }

        private async System.Threading.Tasks.Task RemoveBookAsync(Book book)
        {
            var dlg = new ContentDialog
            {
                Title = "移出书架",
                Content = "把《" + book.Name + "》移出书架？（已离线的内容会保留）",
                PrimaryButtonText = "移出",
                CloseButtonText = "取消"
            };
            if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            {
                await Storage.RemoveFromShelfAsync(book.BookUrl);
                await ReloadAsync();
            }
        }
    }
}
