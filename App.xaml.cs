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
            var frame = Window.Current.Content as Frame;
            if (frame == null)
            {
                frame = new Frame();
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
                frame.Navigate(probe ? typeof(MainPage) : typeof(Views.BookshelfPage), e.Arguments);
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
