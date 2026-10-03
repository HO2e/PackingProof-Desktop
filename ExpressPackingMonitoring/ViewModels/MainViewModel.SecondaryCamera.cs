using AForge.Video;
using AForge.Video.DirectShow;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;
using OpenCvSharp;

namespace ExpressPackingMonitoring.ViewModels
{
    /// <summary>
    /// 第二路摄像头：独立采集一路画面，把它叠到主画面右下角。
    ///
    /// 只做"采集 + 合成进同一帧"，不新建发布管线：副画面画进录像与预览共用的那一帧之后，
    /// 预览、录像、缩略图天然一致，也不会出现"预览里有、录像里没有"的分叉。
    /// 第二路不参与条码识别、不参与运动检测、不写预录缓冲区，掉线也不重启主路。
    /// </summary>
    public partial class MainViewModel
    {
        private VideoCaptureDevice? _secondaryVideoSource;
        private NetworkCameraSource? _secondaryNetworkCameraSource;
        private readonly LatestFrameHandoffSlot<Mat> _latestSecondaryCameraFrame = new();
        private readonly object _secondaryCameraLock = new();

        /// <summary>
        /// 副帧取用锁：实时帧由 VideoProcessLoop 合成、预录帧由录像写线程合成，
        /// 两个线程都会从槽里取走副帧，必须串行，否则会出现"帧已被释放还在读"。
        /// </summary>
        private readonly object _secondaryOverlayLock = new();

        /// <summary>合成用的副帧（只有 VideoProcessLoop 线程访问，谁持有谁释放）。</summary>
        private Mat? _secondaryOverlayFrame;

        /// <summary>最新副帧的原始尺寸，供主界面摆放拖动框（合成与拖动框必须同一套尺寸）。</summary>
        private (int Width, int Height) _secondaryOverlaySourceSize;

        /// <summary>
        /// 上一帧**实际合成**用的落位（主帧坐标系）与那一帧的尺寸。
        /// 界面拖动框按它等比换算：预览帧可能是降采样过的，
        /// 直接用这个"已经画上去的结果"换算，框和画面才会必然重合。
        /// </summary>
        private SecondaryCameraOverlayRect? _lastComposedOverlayRect;
        private (int Width, int Height) _lastComposedFrameSize;

        private bool _hasSecondaryCameraFrame;

        /// <summary>
        /// 副路是否已经出过帧。主界面靠它决定什么时候摆拖动框：
        /// 没有副帧就不知道副画面的宽高比，拖动框的尺寸也就无从算起。
        /// </summary>
        internal bool HasSecondaryCameraFrame
        {
            get => _hasSecondaryCameraFrame;
            private set
            {
                if (SetProperty(ref _hasSecondaryCameraFrame, value))
                    OnPropertyChanged(nameof(IsBarcodeGuideVisible));
            }
        }

        /// <summary>副画面是否参与合成。关闭时整条链路直接跳过，主路行为与从前完全一致。</summary>
        internal bool IsSecondaryCameraComposeEnabled => Config is { EnableSecondaryCamera: true };

        /// <summary>副摄像头当前是否已启动。</summary>
        internal bool IsSecondaryCameraRunning =>
            _secondaryVideoSource != null || _secondaryNetworkCameraSource != null;

        /// <summary>
        /// 主画面取景框是否该显示。识别改用副画面时它是按副画面整帧解码的，
        /// 取景框既不起作用、又会叠在副画面上让人以为"框不对位"，所以这时收起。
        /// </summary>
        public bool IsBarcodeGuideVisible => !ShouldUseSecondaryCameraForBarcode;

        /// <summary>
        /// 「摄像头自动识别面单」是否改用副画面：配置选了副画面、副画面开着、而且副路真的出过帧。
        ///
        /// 最后一条很重要：选了副画面但副路连不上/还没出帧时必须回退主画面，
        /// 否则"换了识别摄像头但没连上"会直接变成完全识别不了。
        /// </summary>
        internal bool ShouldUseSecondaryCameraForBarcode =>
            Config is { EnableSecondaryCamera: true }
            && string.Equals(
                Config.CameraBarcodeRecognitionSource,
                AppConfig.CameraBarcodeSourceSecondary,
                StringComparison.Ordinal)
            && HasSecondaryCameraFrame;

        /// <summary>
        /// 启动第二路。任何失败都只记日志/提示，绝不影响主路——副画面是增强项，
        /// 不能因为它连不上就把主录像拖下水。
        /// </summary>
        internal void StartSecondaryCamera()
        {
            if (Config is not { EnableSecondaryCamera: true } config)
            {
                StopSecondaryCamera();
                return;
            }

            lock (_secondaryCameraLock)
            {
                if (_secondaryVideoSource != null || _secondaryNetworkCameraSource != null)
                    return;

                try
                {
                    if (IsSecondaryNetworkCameraConfigured())
                        StartSecondaryNetworkCamera(config);
                    else
                        StartSecondaryUsbCamera(config);
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error("SecondaryCamera", $"启动副摄像头失败：{ex.Message}");
                    StopSecondaryCameraCore();
                }
            }
        }

        /// <summary>停止第二路并释放副帧。可重复调用。</summary>
        internal void StopSecondaryCamera()
        {
            lock (_secondaryCameraLock)
            {
                StopSecondaryCameraCore();
            }
        }

        /// <summary>按当前配置重启第二路：设置里换设备、换开关或换地址后调用。</summary>
        internal void RestartSecondaryCamera()
        {
            StopSecondaryCamera();
            StartSecondaryCamera();
        }

        private void StopSecondaryCameraCore()
        {
            VideoCaptureDevice? usb = _secondaryVideoSource;
            NetworkCameraSource? network = _secondaryNetworkCameraSource;
            _secondaryVideoSource = null;
            _secondaryNetworkCameraSource = null;

            if (usb != null)
            {
                try
                {
                    usb.NewFrame -= SecondaryVideoSource_NewFrame;
                    if (usb.IsRunning)
                    {
                        usb.SignalToStop();
                        usb.WaitForStop();
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn("SecondaryCamera", $"停止副 USB 摄像头失败：{ex.Message}");
                }
            }

            if (network != null)
            {
                try
                {
                    network.FrameReady -= SecondaryNetworkCameraSource_FrameReady;
                    network.Stop();
                    network.Dispose();
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn("SecondaryCamera", $"停止副网络摄像头失败：{ex.Message}");
                }
            }

            _latestSecondaryCameraFrame.Clear();
            lock (_secondaryOverlayLock)
            {
                DisposeSecondaryOverlayFrame();
                _secondaryOverlaySourceSize = default;
            }

            // 副路停了，主界面的拖动框要跟着消失，等重新出帧再出现。
            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
                HasSecondaryCameraFrame = false;
            else
                dispatcher.BeginInvoke(new Action(() => HasSecondaryCameraFrame = false));
        }

        private bool IsSecondaryNetworkCameraConfigured() =>
            Config is { SecondaryCameraSourceKind: "network" }
            && !string.IsNullOrWhiteSpace(Config.SecondaryNetworkCameraUrl);

        private void StartSecondaryNetworkCamera(AppConfig config)
        {
            if (!NetworkCameraUrlPolicy.TryNormalize(
                    config.SecondaryNetworkCameraUrl,
                    out string url,
                    out string error))
            {
                RuntimeLog.Warn("SecondaryCamera", $"副网络摄像头地址无效：{error}");
                ShowToast($"副网络摄像头地址无效：{error}", ToastSeverity.Error);
                return;
            }

            // 与主路同一个地址等于两个 ffmpeg 进程拉同一路流，纯浪费带宽和解码。
            if (NetworkCameraUrlPolicy.TryNormalize(config.NetworkCameraUrl, out string primaryUrl, out _)
                && string.Equals(primaryUrl, url, StringComparison.OrdinalIgnoreCase))
            {
                RuntimeLog.Warn("SecondaryCamera", "副网络摄像头与主摄像头地址相同，已跳过");
                ShowToast("副画面不能和主画面用同一个网络摄像头地址", ToastSeverity.Warning);
                return;
            }

            var source = new NetworkCameraSource(
                url,
                config.SecondaryNetworkCameraRtspTransport,
                config.Fps > 0 ? config.Fps : 15);
            source.FrameReady += SecondaryNetworkCameraSource_FrameReady;
            source.SourceError += (_, e) =>
                RuntimeLog.Warn("SecondaryCamera", $"副网络摄像头错误：{e.Description}");

            if (!source.Start())
            {
                RuntimeLog.Warn("SecondaryCamera", $"副网络摄像头连接失败：{source.LastError}");
                ShowToast($"副网络摄像头连接失败：{source.LastError}", ToastSeverity.Warning);
                source.Dispose();
                return;
            }

            _secondaryNetworkCameraSource = source;
            RuntimeLog.Info(
                "SecondaryCamera",
                $"副网络摄像头已启动 url={NetworkCameraUrlPolicy.SanitizeForLog(url)}, "
                + $"transport={config.SecondaryNetworkCameraRtspTransport}");
        }

        private void StartSecondaryUsbCamera(AppConfig config)
        {
            var devices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
            if (devices.Count == 0)
            {
                RuntimeLog.Warn("SecondaryCamera", "副摄像头启动失败：没有检测到视频设备");
                return;
            }

            string selectedMoniker = ResolveSecondaryCameraMoniker(config, devices);
            if (selectedMoniker.Length == 0)
                return;

            // 同一台 USB 摄像头是独占设备：两路同时打开必然有一路拿不到画面。
            // 主路走网络流时不占用本地设备，这时不该因为残留的 Moniker 把副路挡掉。
            if (!IsNetworkCameraConfigured()
                && string.Equals(selectedMoniker, config.CameraMonikerString, StringComparison.Ordinal))
            {
                RuntimeLog.Warn("SecondaryCamera", "副摄像头与主摄像头是同一台设备，已跳过");
                ShowToast("副画面不能和主画面用同一台摄像头", ToastSeverity.Warning);
                return;
            }

            var source = new VideoCaptureDevice(selectedMoniker);
            source.NewFrame += SecondaryVideoSource_NewFrame;
            source.VideoSourceError += (_, e) =>
                RuntimeLog.Warn("SecondaryCamera", $"副摄像头发送错误：{e.Description}");
            source.Start();
            _secondaryVideoSource = source;

            if (!string.Equals(config.SecondaryCameraMonikerString, selectedMoniker, StringComparison.Ordinal))
                config.SecondaryCameraMonikerString = selectedMoniker;

            RuntimeLog.Info("SecondaryCamera", "副摄像头已启动");
        }

        /// <summary>
        /// 优先按记住的 Moniker 精确匹配；没配过或设备已拔掉时按索引回落。
        /// 副摄像头不写回主路的摄像头配置，两者互不影响。
        /// </summary>
        private static string ResolveSecondaryCameraMoniker(
            AppConfig config,
            FilterInfoCollection devices)
        {
            string configured = config.SecondaryCameraMonikerString ?? "";
            if (configured.Length > 0)
            {
                for (int i = 0; i < devices.Count; i++)
                {
                    if (string.Equals(devices[i].MonikerString, configured, StringComparison.Ordinal))
                        return configured;
                }

                RuntimeLog.Warn("SecondaryCamera", "配置的副摄像头未连接，回落到索引选择");
            }

            int index = config.SecondaryCameraIndex;
            if (index < 0 || index >= devices.Count)
                index = devices.Count > 1 ? 1 : 0;
            return devices[index].MonikerString;
        }

        private void SecondaryVideoSource_NewFrame(object sender, NewFrameEventArgs eventArgs)
        {
            Mat? frame = null;
            try
            {
                frame = CameraFrameConverter.ConvertToBgrMat(eventArgs.Frame);
                if (Config is { SecondaryCameraRotate180: true })
                    CameraFrameOrientation.Apply(frame, true);
                // 识别来源选了副画面时，这一帧就是面单识别输入（识别服务内部会自己克隆）。
                TrySubmitCameraBarcodeFrame(frame, fromSecondaryCamera: true);
                PublishSecondaryCameraFrame(frame);
            }
            catch (Exception ex)
            {
                frame?.Dispose();
                RuntimeLog.Error("SecondaryCamera", "副摄像头帧转换失败", ex);
            }
        }

        private void SecondaryNetworkCameraSource_FrameReady(object sender, NetworkCameraFrameEventArgs e)
        {
            Mat? frame = null;
            try
            {
                frame = e.Frame;
                if (frame == null || frame.Empty())
                {
                    frame?.Dispose();
                    return;
                }

                if (Config is { SecondaryCameraRotate180: true })
                    CameraFrameOrientation.Apply(frame, true);
                TrySubmitCameraBarcodeFrame(frame, fromSecondaryCamera: true);
                PublishSecondaryCameraFrame(frame);
            }
            catch (Exception ex)
            {
                frame?.Dispose();
                RuntimeLog.Error("SecondaryCamera", "副摄像头帧处理失败", ex);
            }
        }

        /// <summary>整帧所有权交给槽：被顶掉的旧帧由槽自己释放。</summary>
        private void PublishSecondaryCameraFrame(Mat frame) => _latestSecondaryCameraFrame.Publish(frame);

        /// <summary>
        /// 处理循环里调用：把副画面叠进这一帧。
        /// 只有"要录像"或"这一帧真的要发布预览"时才动手，空闲降档时不白做缩放与拷贝。
        /// </summary>
        internal void ComposeSecondaryCameraOverlayIfNeeded(Mat frame, bool previewPublishDue)
        {
            if (!IsSecondaryCameraComposeEnabled)
            {
                lock (_secondaryOverlayLock)
                {
                    DisposeSecondaryOverlayFrame();
                }
                return;
            }

            if (!IsRecording && !previewPublishDue)
                return;

            lock (_secondaryOverlayLock)
            {
                RefreshSecondaryOverlayFrame();

                Mat? secondary = _secondaryOverlayFrame;
                if (secondary == null || secondary.IsDisposed || secondary.Empty())
                    return;

                try
                {
                    if (Config is not { } config)
                        return;

                    // 与下面的合成用同一套输入算一次，用来记录"这一帧把副画面画在哪"；
                    // 界面拖动框据此换算，不再自己另算一份。
                    SecondaryCameraOverlayRect? composedRect = SecondaryCameraOverlayPolicy.Resolve(
                        frame.Width,
                        frame.Height,
                        secondary.Width,
                        secondary.Height,
                        config.SecondaryCameraOverlayWidthRatio,
                        config.SecondaryCameraOverlayMargin,
                        config.SecondaryCameraOverlayLeftRatio,
                        config.SecondaryCameraOverlayTopRatio);

                    if (SecondaryCameraFrameComposer.TryCompose(
                            frame,
                            secondary,
                            config.SecondaryCameraOverlayWidthRatio,
                            config.SecondaryCameraOverlayMargin,
                            config.SecondaryCameraOverlayLeftRatio,
                            config.SecondaryCameraOverlayTopRatio))
                    {
                        _lastComposedOverlayRect = composedRect;
                        _lastComposedFrameSize = (frame.Width, frame.Height);
                    }
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn("SecondaryCamera", $"副画面合成失败：{ex.Message}");
                }
            }
        }

        /// <summary>
        /// 取走最新的副帧并保留：主帧处理比副帧快/慢时都不会闪成黑块，
        /// 没有新副帧就继续用上一帧，直到副路停止才释放。
        /// </summary>
        private void RefreshSecondaryOverlayFrame()
        {
            Mat? latest = _latestSecondaryCameraFrame.Take();
            if (latest == null)
                return;

            if (latest.Empty() || latest.IsDisposed)
            {
                latest.Dispose();
                return;
            }

            Mat? previous = _secondaryOverlayFrame;
            _secondaryOverlayFrame = latest;
            _secondaryOverlaySourceSize = (latest.Width, latest.Height);
            previous?.Dispose();

            if (!HasSecondaryCameraFrame)
                NotifySecondaryCameraFrameAvailable();
        }

        /// <summary>
        /// 这里跑在视频处理线程上，属性通知必须回 UI 线程，
        /// 否则绑定到它的界面元素会在非 UI 线程更新而抛异常。
        /// </summary>
        private void NotifySecondaryCameraFrameAvailable()
        {
            System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
                HasSecondaryCameraFrame = true;
            else
                dispatcher.BeginInvoke(new Action(() => HasSecondaryCameraFrame = true));
        }

        private void DisposeSecondaryOverlayFrame()
        {
            Mat? current = _secondaryOverlayFrame;
            _secondaryOverlayFrame = null;
            if (current != null && !current.IsDisposed)
                current.Dispose();
        }

        /// <summary>副画面是否正在显示，供主界面的拖动框显隐使用。</summary>
        internal bool IsSecondaryCameraOverlayVisible => Config is { EnableSecondaryCamera: true };

        /// <summary>
        /// 按当前配置与最新副帧尺寸算出副画面在主帧里的落位。
        /// 主界面用它摆放拖动框，合成用它摆放画面，两边保证同源。
        /// </summary>
        internal bool TryResolveSecondaryOverlayRect(
            int frameWidth,
            int frameHeight,
            out SecondaryCameraOverlayRect rect)
        {
            rect = default;
            if (Config is not { } config)
                return false;

            // 优先用"上一帧实际合成时画在哪"等比换算：预览帧可能被降采样过，
            // 自己另算一份会因为取整/尺寸来源不同而与画面错开几个像素甚至几十像素。
            if (_lastComposedOverlayRect is { } composed
                && _lastComposedFrameSize.Width > 0
                && _lastComposedFrameSize.Height > 0
                && frameWidth > 0
                && frameHeight > 0)
            {
                double scaleX = (double)frameWidth / _lastComposedFrameSize.Width;
                double scaleY = (double)frameHeight / _lastComposedFrameSize.Height;
                rect = new SecondaryCameraOverlayRect(
                    (int)Math.Round(composed.X * scaleX),
                    (int)Math.Round(composed.Y * scaleY),
                    (int)Math.Round(composed.Width * scaleX),
                    (int)Math.Round(composed.Height * scaleY));
                return true;
            }

            (int sourceWidth, int sourceHeight) = _secondaryOverlaySourceSize;
            if (sourceWidth <= 0 || sourceHeight <= 0)
                return false;

            SecondaryCameraOverlayRect? resolved = SecondaryCameraOverlayPolicy.Resolve(
                frameWidth,
                frameHeight,
                sourceWidth,
                sourceHeight,
                config.SecondaryCameraOverlayWidthRatio,
                config.SecondaryCameraOverlayMargin,
                config.SecondaryCameraOverlayLeftRatio,
                config.SecondaryCameraOverlayTopRatio);

            if (resolved is not { } value)
                return false;

            rect = value;
            return true;
        }

        /// <summary>
        /// 拖动中调用：只改内存配置，下一帧合成立刻按新位置画，预览实时跟随；
        /// 松手时再落盘（见 <see cref="SaveSecondaryCameraOverlayPosition"/>）。
        /// </summary>
        internal void SetSecondaryCameraOverlayPosition(
            double frameX,
            double frameY,
            int frameWidth,
            int frameHeight)
        {
            if (Config is not { } config || frameWidth <= 0 || frameHeight <= 0)
                return;
            if (!TryResolveSecondaryOverlayRect(frameWidth, frameHeight, out SecondaryCameraOverlayRect rect))
                return;

            // 存左上角的归一化比例，换分辨率/换摄像头都不会跑偏；夹紧保证整块小窗留在画面内。
            double left = Math.Clamp(frameX / frameWidth, 0.0, 1.0);
            double top = Math.Clamp(frameY / frameHeight, 0.0, 1.0);
            left = Math.Min(left, Math.Max(0.0, 1.0 - ((double)rect.Width / frameWidth)));
            top = Math.Min(top, Math.Max(0.0, 1.0 - ((double)rect.Height / frameHeight)));

            config.SecondaryCameraOverlayLeftRatio = left;
            config.SecondaryCameraOverlayTopRatio = top;
        }

        /// <summary>把拖动结果落盘，下次启动还在同一位置。</summary>
        internal void SaveSecondaryCameraOverlayPosition()
        {
            if (Config is not { } config)
                return;

            if (config.SecondaryCameraOverlayLeftRatio < 0 || config.SecondaryCameraOverlayTopRatio < 0)
                return;

            double left = config.SecondaryCameraOverlayLeftRatio;
            double top = config.SecondaryCameraOverlayTopRatio;
            if (!WorkstationConfigStore.TryUpdate(
                    saved =>
                    {
                        saved.SecondaryCameraOverlayLeftRatio = left;
                        saved.SecondaryCameraOverlayTopRatio = top;
                    },
                    out AppConfig savedConfig,
                    out string error))
            {
                RuntimeLog.Warn("SecondaryCamera", $"副画面位置保存失败：{error}");
                return;
            }

            Config.SecondaryCameraOverlayLeftRatio = savedConfig.SecondaryCameraOverlayLeftRatio;
            Config.SecondaryCameraOverlayTopRatio = savedConfig.SecondaryCameraOverlayTopRatio;
        }
    }
}
