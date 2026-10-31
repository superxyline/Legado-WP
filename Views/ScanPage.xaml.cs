using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using legado_wp.Core;
using ZXing;
using ZXing.Common;

namespace legado_wp.Views
{
    /// <summary>
    /// 扫码导入书源：相机预览 + ZXing 解码二维码。
    /// 码内容支持两种：书源 JSON 的 URL（http/https）→ 下载导入；或书源 JSON 本体 → 直接导入。
    /// </summary>
    public sealed partial class ScanPage : Page
    {
        private MediaCapture _media;
        private DispatcherTimer _timer;
        private BarcodeReaderGeneric _reader;
        private bool _busy;
        private bool _done;

        public ScanPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            await StartCameraAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            StopCamera();
        }

        private async Task StartCameraAsync()
        {
            try
            {
                Status.Text = "正在启动相机…";
                _media = new MediaCapture();
                await _media.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                    PhotoCaptureSource = PhotoCaptureSource.VideoPreview
                });
                Preview.Source = _media;
                await _media.StartPreviewAsync();
                Status.Text = "";

                _reader = new BarcodeReaderGeneric
                {
                    AutoRotate = true,
                    Options = new DecodingOptions
                    {
                        TryHarder = true,
                        TryInverted = true,
                        PossibleFormats = new[] { BarcodeFormat.QR_CODE }
                    }
                };

                _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                _timer.Tick += async (s, ev) => await ScanFrameAsync();
                _timer.Start();
            }
            catch (Exception ex)
            {
                Status.Text = "相机不可用：" + ex.Message
                    + "\n请回书源页粘贴链接导入";
                Hint.Text = "";
            }
        }

        private async Task ScanFrameAsync()
        {
            if (_busy || _done || _media == null) return;
            _busy = true;
            try
            {
                using (var preview = await _media.GetPreviewFrameAsync())
                {
                    var bmp = preview.SoftwareBitmap; // 原生格式多为 NV12
                    if (bmp == null) return;
                    var bgra = bmp.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                        ? bmp
                        : SoftwareBitmap.Convert(bmp, BitmapPixelFormat.Bgra8);

                    var w = bgra.PixelWidth;
                    var h = bgra.PixelHeight;
                    var bytes = new byte[4 * w * h];
                    bgra.CopyToBuffer(bytes.AsBuffer());

                    var source = new RGBLuminanceSource(bytes, w, h, RGBLuminanceSource.BitmapFormat.BGRA32);
                    var result = _reader.Decode(source);
                    if (result != null && !string.IsNullOrEmpty(result.Text))
                    {
                        _done = true;
                        _timer.Stop();
                        await HandleScanResultAsync(result.Text.Trim());
                    }
                }
            }
            catch
            {
                // 单帧失败（预览未就绪等）忽略，下一帧再试
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>码内容分发：URL → 下载导入；JSON → 直接导入。</summary>
        private async Task HandleScanResultAsync(string text)
        {
            try
            {
                Status.Text = "识别成功，正在导入…";
                StopCamera();

                var added = text.StartsWith("{") || text.StartsWith("[")
                    ? await Storage.ImportSourcesAsync(text)
                    : await Storage.ImportSourcesFromUrlAsync(text).TimeoutAfter(30000);

                var dlg = new ContentDialog
                {
                    Title = "扫码导入完成",
                    Content = "本次新增 " + added + " 个书源",
                    CloseButtonText = "好"
                };
                await dlg.ShowAsync();
            }
            catch (Exception ex)
            {
                var dlg = new ContentDialog
                {
                    Title = "导入失败",
                    Content = ex.Message + "\n\n码内容：" + (text.Length > 80 ? text.Substring(0, 80) + "…" : text),
                    CloseButtonText = "好"
                };
                await dlg.ShowAsync();
            }
            finally
            {
                if (Frame.CanGoBack) Frame.GoBack();
            }
        }

        private void StopCamera()
        {
            try { _timer?.Stop(); } catch { }
            var media = _media;
            _media = null;
            Preview.Source = null;
            if (media == null) return;
            // UI 线程不能 Wait WinRT 异步（会死锁），fire-and-forget 清理
            _ = StopPreviewSafeAsync(media);
        }

        private static async Task StopPreviewSafeAsync(MediaCapture media)
        {
            try { await media.StopPreviewAsync(); } catch { }
            try { media.Dispose(); } catch { }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }
    }
}
