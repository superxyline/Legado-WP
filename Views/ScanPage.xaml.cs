using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
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
        private int _frameCount;
        private int _decodeCount;
        private string _lastError = "";
        private VideoEncodingProperties _previewProps;

        public ScanPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            ApplyPreviewTransform();
            await StartCameraAsync();
        }

        /// <summary>
        /// 竖屏预览方向修正：UWP 16299 无 SetPreviewRotationAsync（WP8.1 专有 API），
        /// 相机传感器横向输出，CaptureElement 直接显示会横竖颠倒。
        /// 把元素宽高对调横放 + 顺时针转 90°（RenderTransform），旋转后足迹正好铺满竖屏。
        /// 只影响预览显示；解码帧仍取原始横向画面（ZXing AutoRotate 自动兼容旋转）。
        /// </summary>
        private void ApplyPreviewTransform()
        {
            try
            {
                var w = Window.Current.Bounds.Width;
                var h = Window.Current.Bounds.Height;
                Preview.Width = h;
                Preview.Height = w;
                Preview.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
                Preview.RenderTransform = new RotateTransform { Angle = 90 };
                Preview.HorizontalAlignment = HorizontalAlignment.Center;
                Preview.VerticalAlignment = VerticalAlignment.Center;
            }
            catch { /* 桌面或异常尺寸忽略 */ }
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

                // 记录预览流实际尺寸：取帧必须按这个尺寸构造 VideoFrame（官方 GetPreviewFrame 示例模式）
                try
                {
                    _previewProps = _media.VideoDeviceController
                        .GetMediaStreamProperties(MediaStreamType.VideoPreview) as VideoEncodingProperties;
                }
                catch { _previewProps = null; }

                // 连续对焦（微距档）：二维码近距离扫码的关键，默认对焦在远处时码是糊的
                try
                {
                    var fc = _media.VideoDeviceController.FocusControl;
                    if (fc.Supported)
                    {
                        fc.Configure(new FocusSettings
                        {
                            Mode = FocusMode.Continuous,
                            AutoFocusRange = AutoFocusRange.Macro
                        });
                        await fc.FocusAsync();
                    }
                    else
                    {
                        Status.Text = "此相机不支持自动对焦，请把手机拿远些";
                    }
                }
                catch { /* 对焦失败继续扫 */ }

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
                // 无参 GetPreviewFrameAsync 返回 GPU 帧（SoftwareBitmap 恒为 null），
                // 必须按预览流尺寸构造 Bgra8 的 VideoFrame 传入才能拿到 CPU 可读帧
                var w = (int)(_previewProps?.Width ?? 640);
                var h = (int)(_previewProps?.Height ?? 480);
                if (w <= 0 || h <= 0) { w = 640; h = 480; }

                using (var request = new VideoFrame(BitmapPixelFormat.Bgra8, w, h))
                {
                    await _media.GetPreviewFrameAsync(request);
                    var bmp = request.SoftwareBitmap;
                    if (bmp == null)
                    {
                        _lastError = "预览帧为空(带参)";
                        UpdateDebugStatus(0, 0);
                        return;
                    }
                    var bgra = bmp.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                        ? bmp
                        : SoftwareBitmap.Convert(bmp, BitmapPixelFormat.Bgra8);

                    var bw = bgra.PixelWidth;
                    var bh = bgra.PixelHeight;
                    var bytes = new byte[4 * bw * bh];
                    bgra.CopyToBuffer(bytes.AsBuffer());

                    var source = new RGBLuminanceSource(bytes, bw, bh, RGBLuminanceSource.BitmapFormat.BGRA32);
                    var result = _reader.Decode(source);
                    _frameCount++;
                    _lastError = "";
                    if (result != null && !string.IsNullOrEmpty(result.Text))
                    {
                        _done = true;
                        _timer.Stop();
                        await HandleScanResultAsync(result.Text.Trim());
                        return;
                    }
                    UpdateDebugStatus(bw, bh);
                }
            }
            catch (Exception ex)
            {
                // 单帧失败（预览未就绪等）不中断，但把错误显示出来便于诊断
                _lastError = ex.Message;
                UpdateDebugStatus(0, 0);
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>屏幕诊断：每 4 帧刷新一次状态行（帧数/尺寸/最近错误），便于真机排障。</summary>
        private void UpdateDebugStatus(int w, int h)
        {
            if (_frameCount % 4 != 0) return;
            var msg = _lastError.Length > 0
                ? "帧错误：" + _lastError
                : "取流中 第" + _frameCount + "帧 " + w + "x" + h
                  + (h > 0 && _frameCount > 12 ? "（画面正常但未识别：请靠近/拉远些，让二维码清晰充满取景框）" : "");
            var ignore = Dispatcher.RunAsync(CoreDispatcherPriority.Low,
                () => { if (!_done) Status.Text = msg; });
        }

        /// <summary>码内容分发：URL → 下载导入（带进度）；JSON → 直接导入。</summary>
        private async Task HandleScanResultAsync(string text)
        {
            try
            {
                Status.Text = "识别成功，正在导入…";
                StopCamera();

                var added = text.StartsWith("{") || text.StartsWith("[")
                    ? await Storage.ImportSourcesAsync(text)
                    : await Storage.ImportSourcesFromUrlAsync(text, n =>
                    {
                        var msg = n < 0
                            ? "下载中断，自动重试…"
                            : "下载书源中 " + Storage.FormatBytes(n) + "（大合集需几分钟）";
                        var ignore = Dispatcher.RunAsync(CoreDispatcherPriority.Low,
                            () => Status.Text = msg);
                    });

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
