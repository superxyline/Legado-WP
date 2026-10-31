using System;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using legado_wp.Core;

namespace legado_wp
{
    sealed partial class App : Application
    {
        public App()
        {
            InitializeComponent();
            Suspending += OnSuspending;
            UnhandledException += async (s, e) =>
            {
                try
                {
                    var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
                    var file = await folder.CreateFileAsync("crash.log",
                        Windows.Storage.CreationCollisionOption.OpenIfExists);
                    await Windows.Storage.FileIO.AppendTextAsync(file,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + e.Exception.GetType().Name
                        + ": " + e.Message + "\r\n" + (e.Exception.StackTrace ?? "") + "\r\n---\r\n");
                }
                catch { }
                // 0.0.14.9：异常不再直接杀死进程（老机型上启动期异常闪退时保住界面）
                e.Handled = true;
            };
        }

        private bool _backHooked;

        /// <summary>系统返回键：Frame 能退则退，不能退不拦截（回桌面）。</summary>
        private void HookBackButton()
        {
            if (_backHooked) return;
            _backHooked = true;
            var nav = Windows.UI.Core.SystemNavigationManager.GetForCurrentView();
            nav.BackRequested += (s, e) =>
            {
                var frame = Window.Current.Content as Frame;
                if (frame != null && frame.CanGoBack)
                {
                    frame.GoBack();
                    e.Handled = true;
                }
            };
            UpdateBackButtonVisibility();
        }

        public static void UpdateBackButtonVisibility()
        {
            var frame = Window.Current?.Content as Frame;
            Windows.UI.Core.SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                frame != null && frame.CanGoBack
                    ? Windows.UI.Core.AppViewBackButtonVisibility.Visible
                    : Windows.UI.Core.AppViewBackButtonVisibility.Collapsed;
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs e)
        {
            // 0.0.15.0 一次性迁移（第二次）：用户重新导入数千源大合集再次压垮 929（OOM），
            // 升级后首次启动自动清空书源（仅此一次；书架/进度/离线不受影响）
            try
            {
                var mig = Windows.Storage.ApplicationData.Current.LocalSettings;
                if (mig.Values["migration.15.0.sourcesCleared"] == null)
                {
                    mig.Values["migration.15.0.sourcesCleared"] = true;
                    var srcFile = await Windows.Storage.ApplicationData.Current.LocalFolder
                        .TryGetItemAsync("sources.json");
                    if (srcFile != null) await srcFile.DeleteAsync();
                }
            }
            catch { }

            var frame = Window.Current.Content as Frame;
            if (frame == null)
            {
                frame = new Frame();
                frame.CacheSize = 2; // 缓存 TocPage/ReaderPage，退出阅读器回目录不重拉
                frame.Navigated += (s, args) => UpdateBackButtonVisibility();
                Window.Current.Content = frame;
            }
            HookBackButton();
            if (frame.Content == null && e.PrelaunchActivated == false)
            {
                // 自动化测试钩子：LocalState 存在 run-probe.flag 时启动进自检页
                bool probe;
                try
                {
                    var f = await Windows.Storage.ApplicationData.Current.LocalFolder
                        .TryGetItemAsync("run-probe.flag");
                    probe = f != null;
                }
                catch { probe = false; }
                try
                {
                    frame.Navigate(probe ? typeof(MainPage) : typeof(Views.BookshelfPage), e.Arguments);
                }
                catch (Exception navEx)
                {
                    // 启动导航兜底：书架页崩溃则退到自检页，至少保证 App 能打开
                    try
                    {
                        var folder = Windows.Storage.ApplicationData.Current.LocalFolder;
                        var file = await folder.CreateFileAsync("crash.log",
                            Windows.Storage.CreationCollisionOption.OpenIfExists);
                        await Windows.Storage.FileIO.AppendTextAsync(file,
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [launch] "
                            + navEx.GetType().Name + ": " + navEx.Message + "\r\n---\r\n");
                        frame.Navigate(typeof(MainPage));
                    }
                    catch { }
                }
            }
            Window.Current.Activate();
        }

        private void OnSuspending(object sender, Windows.ApplicationModel.SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            deferral.Complete();
        }
    }
}
