using AForge.Video;
using AForge.Video.DirectShow;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.Services.MediaFoundation;
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
        private MfCameraSource? _secondaryMfCameraSource;
        private NetworkCameraSource? _secondaryNetworkCameraSource;
        private readonly LatestFrameHandoffSlot<Mat> _latestSecondaryCameraFrame = new();
        private readonly object _secondaryCameraLock = new();

        /// <summary>
        /// 启动后"到底有没有画面"的观察令牌。每次启动/停止都换一个，
        /// 迟到的旧观察直接失效，不会对已经换过的设备报错。
        /// </summary>
        private CancellationTokenSource? _secondaryFrameWatchCts;

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
        private int _secondaryOverlayPlacementVersion;

        /// <summary>
        /// 副画面实际落位的版本号。合成位置一变就自增并通知界面重摆拖动框 ——
        /// 副画面是直接画进帧里的，界面那个拖动框平时不跟着每帧走，
        /// 不在这里通知就会停在上一帧的位置，看起来就是"框和画面对不上"。
        /// </summary>
        public int SecondaryOverlayPlacementVersion => _secondaryOverlayPlacementVersion;

        private void NotifySecondaryOverlayPlacementChanged()
        {
            _secondaryOverlayPlacementVersion++;
            OnPropertyChanged(nameof(SecondaryOverlayPlacementVersion));
        }

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
                {
                    OnPropertyChanged(nameof(IsBarcodeGuideVisible));
                    OnPropertyChanged(nameof(IsCameraBarcodeGuideLockVisible));
                    OnPropertyChanged(nameof(IsCameraBarcodeGuideEditable));
                }
            }
        }

        /// <summary>副画面是否参与合成：来源选了"无"就整条链路跳过，主路行为与从前完全一致。</summary>
        internal bool IsSecondaryCameraComposeEnabled =>
            Config is { } config
            && !string.Equals(
                config.SecondaryCameraSourceKind,
                AppConfig.SecondaryCameraSourceNone,
                StringComparison.Ordinal);

        /// <summary>设置页据此显示/隐藏副摄的其余选项：选了"无"就整块收起。</summary>
        public bool IsSecondaryCameraConfigured => IsSecondaryCameraComposeEnabled;

        /// <summary>选了"网络摄像头"才显示地址输入。</summary>
        public bool IsSecondaryNetworkCameraSelected =>
            Config is { } config
            && string.Equals(config.SecondaryCameraSourceKind, "network", StringComparison.Ordinal);

        /// <summary>副摄下拉的一项：无 / 本机某台摄像头 / 网络摄像头。</summary>
        public sealed record SecondaryCameraChoice(string Name, string Kind, string Moniker, int Index);

        private List<SecondaryCameraChoice>? _secondaryCameraChoices;

        /// <summary>
        /// 副摄下拉列表：与主摄一样列出本机真实存在的摄像头，只是把主摄已经选走的那一台剔掉
        /// （同一台设备无法被两路同时打开）。"网络摄像头"只在需要时选，选它才出现地址输入。
        /// 列表放在 ViewModel 里而不是设置页代码里，是为了不往被冻结的 SettingsWindow.xaml.cs 里加逻辑。
        /// </summary>
        public IReadOnlyList<SecondaryCameraChoice> SecondaryCameraChoices =>
            _secondaryCameraChoices ??= BuildSecondaryCameraChoices();

        private List<SecondaryCameraChoice> BuildSecondaryCameraChoices()
        {
            var choices = new List<SecondaryCameraChoice>
            {
                new("无", AppConfig.SecondaryCameraSourceNone, "", -1)
            };

            try
            {
                var devices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                string mainMoniker = Config?.CameraMonikerString ?? "";
                for (int i = 0; i < devices.Count; i++)
                {
                    // 主摄已经占用的那一台不再出现在副摄列表里。
                    if (!string.IsNullOrEmpty(mainMoniker)
                        && string.Equals(devices[i].MonikerString, mainMoniker, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    choices.Add(new SecondaryCameraChoice(devices[i].Name, "usb", devices[i].MonikerString, i));
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("SecondaryCamera", $"枚举副摄候选设备失败：{ex.Message}");
            }

            choices.Add(new SecondaryCameraChoice("网络摄像头", "network", "", -1));
            return choices;
        }

        /// <summary>当前选中的副摄；写入时把选择落到配置并保存，主摄那套字段完全不动。</summary>
        public SecondaryCameraChoice? SelectedSecondaryCameraChoice
        {
            get
            {
                if (Config is not { } config)
                    return null;

                return SecondaryCameraChoices.FirstOrDefault(choice =>
                    string.Equals(choice.Kind, config.SecondaryCameraSourceKind, StringComparison.Ordinal)
                    && (choice.Kind != "usb"
                        || string.Equals(choice.Moniker, config.SecondaryCameraMonikerString, StringComparison.Ordinal)));
            }
            set
            {
                if (value == null || Config is not { } config)
                    return;
                if (ReferenceEquals(value, SelectedSecondaryCameraChoice))
                    return;

                config.SecondaryCameraSourceKind = value.Kind;
                config.SecondaryCameraIndex = value.Index;
                if (value.Kind == "usb")
                    config.SecondaryCameraMonikerString = value.Moniker;
                else
                    config.SecondaryCameraMonikerString = "";

                OnPropertyChanged(nameof(IsSecondaryCameraConfigured));
                OnPropertyChanged(nameof(IsSecondaryCameraOverlayVisible));
                OnPropertyChanged(nameof(IsBarcodeGuideVisible));
                OnPropertyChanged(nameof(IsCameraBarcodeGuideLockVisible));
                OnPropertyChanged(nameof(IsCameraBarcodeGuideEditable));
                SaveConfig();
                RestartSecondaryCamera();
            }
        }

        /// <summary>副摄像头当前是否已启动。</summary>
        internal bool IsSecondaryCameraRunning =>
            _secondaryVideoSource != null
            || _secondaryMfCameraSource != null
            || _secondaryNetworkCameraSource != null;

        /// <summary>
        /// 识别框是否显示。框只出现在识别来源那一路：
        /// 来源是主摄时画在主画面上；来源是副摄时贴到画中画上（画中画就是框内那块裁剪结果），
        /// 这样绿/黄识别状态与提示文字才有地方显示 —— 不能再像之前那样整条反馈都收掉。
        /// </summary>
        public bool IsBarcodeGuideVisible => true;

        /// <summary>
        /// 框上的小锁只在"这个框能编辑"的时候出现：主摄取景、或副摄取景编辑屏。
        /// 识别来源是副画面时，框只是画中画上的状态反馈（取景在"点画中画"的编辑屏里改），不显示锁。
        /// </summary>
        public bool IsCameraBarcodeGuideLockVisible =>
            !ShouldUseSecondaryCameraForBarcode || IsEditingSecondaryCameraPreview;


        /// <summary>
        /// 「摄像头自动识别面单」是否改用副画面：配置选了副画面、副画面开着、而且副路真的出过帧。
        ///
        /// 最后一条很重要：选了副画面但副路连不上/还没出帧时必须回退主画面，
        /// 否则"换了识别摄像头但没连上"会直接变成完全识别不了。
        /// </summary>
        internal bool ShouldUseSecondaryCameraForBarcode =>
            IsSecondaryCameraComposeEnabled
            && string.Equals(
                Config?.CameraBarcodeRecognitionSource,
                AppConfig.CameraBarcodeSourceSecondary,
                StringComparison.Ordinal)
            && HasSecondaryCameraFrame;

        /// <summary>
        /// 启动第二路。任何失败都只记日志/提示，绝不影响主路——副画面是增强项，
        /// 不能因为它连不上就把主录像拖下水。
        /// </summary>
        internal void StartSecondaryCamera()
        {
            if (Config is not { } config
                || string.Equals(
                    config.SecondaryCameraSourceKind,
                    AppConfig.SecondaryCameraSourceNone,
                    StringComparison.Ordinal))
            {
                StopSecondaryCamera();
                return;
            }

            lock (_secondaryCameraLock)
            {
                if (IsSecondaryCameraRunning)
                    return;

                try
                {
                    if (IsSecondaryNetworkCameraConfigured())
                        StartSecondaryNetworkCamera(config);
                    else
                        StartSecondaryUsbCamera(config);
                    // 打开设备不代表有画面：USB 摄像头被别的程序占用、两路选了同一台设备、
                    // 网络地址挂着一台不存在的相机，都会"启动成功但一帧都不给"。
                    // 最终以出帧为准，所以启动后观察一段时间。
                    ScheduleSecondaryCameraFrameWatch();
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
            MfCameraSource? mf = _secondaryMfCameraSource;
            NetworkCameraSource? network = _secondaryNetworkCameraSource;
            _secondaryVideoSource = null;
            _secondaryMfCameraSource = null;
            _secondaryNetworkCameraSource = null;

            CancellationTokenSource? watch = _secondaryFrameWatchCts;
            _secondaryFrameWatchCts = null;
            if (watch != null)
            {
                try { watch.Cancel(); } catch { }
                try { watch.Dispose(); } catch { }
            }

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

            if (mf != null)
            {
                try
                {
                    mf.FrameReady -= SecondaryMfCameraSource_FrameReady;
                    mf.SourceError -= SecondaryMfCameraSource_SourceError;
                    mf.Stop();
                    mf.Dispose();
                }
                catch (Exception ex)
                {
                    RuntimeLog.Warn("SecondaryCamera", $"停止副 Media Foundation 摄像头失败：{ex.Message}");
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
                config.SecondaryFrameFps > 0 ? config.SecondaryFrameFps : AppConfig.DefaultSecondaryFrameFps);
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

            // 先走新后端：原生格式协商 + GPU 解码与色彩校正，与主摄同一条路径。
            // 软件虚拟摄像头（MF 枚举不到）、驱动异常或探测不通过时再回退 DirectShow。
            if (TryStartMediaFoundationSecondaryCamera(selectedMoniker))
            {
                if (!string.Equals(config.SecondaryCameraMonikerString, selectedMoniker, StringComparison.Ordinal))
                    config.SecondaryCameraMonikerString = selectedMoniker;
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

            RuntimeLog.Info("SecondaryCamera", "副摄像头已启动（DirectShow）");
        }

        /// <summary>
        /// 解析副摄要打开的设备。
        ///
        /// **不做"随便回落一台"**：以前在没配过或设备拔掉时会退到索引 1/0，结果经常正好是主摄那一台，
        /// 两路抢同一台设备 —— 表现就是"主副冲突"提示和副摄永远没有画面。
        /// 现在解析不到就返回空，由调用方直接不启动副路。
        /// </summary>
        private static string ResolveSecondaryCameraMoniker(
            AppConfig config,
            FilterInfoCollection devices)
        {
            string mainMoniker = config.CameraMonikerString ?? "";
            string configured = config.SecondaryCameraMonikerString ?? "";
            if (configured.Length > 0)
            {
                for (int i = 0; i < devices.Count; i++)
                {
                    if (!string.Equals(devices[i].MonikerString, configured, StringComparison.Ordinal))
                        continue;
                    if (string.Equals(configured, mainMoniker, StringComparison.Ordinal))
                        break;
                    return configured;
                }

                RuntimeLog.Info("SecondaryCamera", "配置的副摄像头当前不在（或就是主摄那一台），副画面先不启动");
                return "";
            }

            int index = config.SecondaryCameraIndex;
            if (index >= 0 && index < devices.Count)
            {
                string moniker = devices[index].MonikerString;
                if (!string.Equals(moniker, mainMoniker, StringComparison.Ordinal))
                    return moniker;
            }

            RuntimeLog.Info("SecondaryCamera", "副摄还没有选定设备，副画面先不启动");
            return "";
        }

        /// <summary>
        /// 副摄的 Media Foundation 启动路径，与主摄同一套：直接协商原生 YUY2/NV12，
        /// 交给 GPU 做解码、色彩校正与缩放，不走 DirectShow 的系统转换器。
        /// 任何一步不成立都返回 false，由调用方回退 DirectShow —— 副画面是增强项，
        /// 绝不能因为后端问题让两路都录不了。
        /// </summary>
        private bool TryStartMediaFoundationSecondaryCamera(string monikerString)
        {
            if (Config is not { } config)
                return false;
            if (CameraBackendPolicy.IsMediaFoundationDisabled(config.CameraBackend))
                return false;

            try
            {
                using MfPlatform? platform = MfPlatform.TryStart();
                if (platform == null)
                    return false;

                MfCaptureDevice? device = MfDeviceMatcher.FindByMoniker(
                    monikerString,
                    MfCaptureDevice.Enumerate());
                if (device == null)
                    return false;

                (int width, int height) = AppConfig.ResolveSecondaryFrameSize(config.SecondaryResolutionPreset);
                int fps = config.SecondaryFrameFps > 0
                    ? config.SecondaryFrameFps
                    : AppConfig.DefaultSecondaryFrameFps;

                MfCaptureProbe.Result probe = MfCaptureProbe.Probe(
                    device.SymbolicLink,
                    width,
                    height,
                    fps,
                    config.CameraColorMatrix);
                if (CameraBackendPolicy.Decide(config.CameraBackend, probe.Usable)
                    != CameraBackendKind.MediaFoundation)
                {
                    RuntimeLog.Info(
                        "SecondaryCamera",
                        $"副摄 Media Foundation 后端不可用（{probe.Failure}），改用 DirectShow 后端");
                    return false;
                }

                var source = new MfCameraSource(
                    device.SymbolicLink,
                    width,
                    height,
                    fps,
                    config.CameraColorMatrix,
                    config.SecondaryCameraRotationDegrees);
                source.FrameReady += SecondaryMfCameraSource_FrameReady;
                source.SourceError += SecondaryMfCameraSource_SourceError;
                if (!source.Start())
                {
                    source.FrameReady -= SecondaryMfCameraSource_FrameReady;
                    source.SourceError -= SecondaryMfCameraSource_SourceError;
                    source.Dispose();
                    RuntimeLog.Warn(
                        "SecondaryCamera",
                        $"副摄 Media Foundation 探测通过但启动失败（{source.LastStartFailure}），改用 DirectShow 后端");
                    return false;
                }

                _secondaryMfCameraSource = source;
                RuntimeLog.Info(
                    "SecondaryCamera",
                    $"副摄已启动（Media Foundation）{source.ActualWidth}x{source.ActualHeight}@{source.ActualFps:F0}"
                        + $"，格式={source.ActualFormat}，bt709={source.UsesBt709}"
                        + $"，configured={width}x{height}@{fps}");
                return true;
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("SecondaryCamera", $"副摄 Media Foundation 启动异常，改用 DirectShow：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 新后端的副帧到达。帧已经是 BGR24，所有权交给订阅方，
        /// 不需要再做一次格式转换与克隆；后续处理与 DirectShow 路径完全一致。
        /// </summary>
        private void SecondaryMfCameraSource_FrameReady(object? sender, MfFrameEventArgs e)
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

                // 副摄的旋转同样在采集层完成（MF 用 GPU 着色器，AForge 回退在帧回调里转），
                // 这里不能再转一次。
                TrySubmitCameraBarcodeFrame(frame, fromSecondaryCamera: true);
                PublishSecondaryCameraFrame(frame);
            }
            catch (Exception ex)
            {
                frame?.Dispose();
                RuntimeLog.Error("SecondaryCamera", "副摄 Media Foundation 帧处理失败", ex);
            }
        }

        private void SecondaryMfCameraSource_SourceError(object? sender, MfSourceErrorEventArgs e) =>
            RuntimeLog.Warn(
                "SecondaryCamera",
                $"副摄 Media Foundation 采集错误：{e.Description}（deviceLost={e.DeviceLost}）");

        /// <summary>
        /// 启动后观察一段时间：打开设备成功不等于有画面。副摄最常见的失败形态是
        /// "与主摄是同一台设备被独占""被其它程序占用""网络地址挂着不存在的相机"，
        /// 这些都不会抛异常。这里只提示用户、不动主路、也不自动换设备 ——
        /// 换了设备反而可能把用户装好的面单机位换成一台拍不到面单的相机。
        /// </summary>
        private void ScheduleSecondaryCameraFrameWatch()
        {
            CancellationTokenSource? previous = _secondaryFrameWatchCts;
            if (previous != null)
            {
                try { previous.Cancel(); } catch { }
                try { previous.Dispose(); } catch { }
            }

            var cts = new CancellationTokenSource();
            _secondaryFrameWatchCts = cts;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(6), cts.Token).ConfigureAwait(false);
                    if (cts.IsCancellationRequested || HasSecondaryCameraFrame || !IsSecondaryCameraRunning)
                        return;

                    RuntimeLog.Warn(
                        "SecondaryCamera",
                        "副摄像头启动后 6 秒没有画面：可能与主摄是同一台设备被独占、被其它程序占用，或地址不可用");
                    System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
                    dispatcher?.BeginInvoke(new Action(() =>
                        ShowToast("副摄像头没有画面，请检查它是否与主摄冲突、被其它程序占用或地址不可用", ToastSeverity.Warning)));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    RuntimeLog.Warn("SecondaryCamera", $"副摄出帧观察失败：{ex.Message}");
                }
            });
        }

        private void SecondaryVideoSource_NewFrame(object? sender, NewFrameEventArgs eventArgs)
        {
            Mat? frame = null;
            try
            {
                frame = CameraFrameConverter.ConvertToBgrMat(eventArgs.Frame);
                if (Config is { } config)
                    frame = CameraFrameOrientation.Apply(frame, config.SecondaryCameraRotationDegrees);
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

        private void SecondaryNetworkCameraSource_FrameReady(object? sender, NetworkCameraFrameEventArgs e)
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

                if (Config is { } config)
                    frame = CameraFrameOrientation.Apply(frame, config.SecondaryCameraRotationDegrees);
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
        private void PublishSecondaryCameraFrame(Mat frame)
        {
            // 编辑态下顺便出一张预览位图；三路采集回调都经过这里，不必各自处理。
            PublishSecondaryPreviewFrameIfDue(frame);
            _latestSecondaryCameraFrame.Publish(frame);
        }

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

                    // 识别框就是裁剪范围：画中画只显示框内那块，识别也只解这块。
                    // 与主摄画框用的是同一个换算：几何 -> 画面上的矩形。
                    System.Windows.Rect guide = CameraBarcodeGuideLayout.ToDisplayRect(
                        GetSecondaryCameraGuideGeometry(),
                        new System.Windows.Rect(0, 0, secondary.Width, secondary.Height));
                    Rect cropRect = new Rect(
                            (int)Math.Round(guide.X),
                            (int)Math.Round(guide.Y),
                            Math.Max(1, (int)Math.Round(guide.Width)),
                            Math.Max(1, (int)Math.Round(guide.Height)))
                        .Intersect(new Rect(0, 0, secondary.Width, secondary.Height));
                    if (cropRect.Width <= 0 || cropRect.Height <= 0)
                        return;

                    using var cropped = new Mat(secondary, cropRect);

                    // 与下面的合成用同一套输入算一次，用来记录"这一帧把副画面画在哪"；
                    // 界面拖动框据此换算，不再自己另算一份。
                    SecondaryCameraOverlayRect? composedRect = SecondaryCameraOverlayPolicy.Resolve(
                        frame.Width,
                        frame.Height,
                        cropped.Width,
                        cropped.Height,
                        config.SecondaryCameraOverlayWidthRatio,
                        config.SecondaryCameraOverlayMargin,
                        config.SecondaryCameraOverlayLeftRatio,
                        config.SecondaryCameraOverlayTopRatio,
                        allowUpscale: false);

                    if (SecondaryCameraFrameComposer.TryCompose(
                            frame,
                            cropped,
                            config.SecondaryCameraOverlayWidthRatio,
                            config.SecondaryCameraOverlayMargin,
                            config.SecondaryCameraOverlayLeftRatio,
                            config.SecondaryCameraOverlayTopRatio,
                            allowUpscale: false))
                    {
                        bool placementChanged = _lastComposedOverlayRect != composedRect
                            || _lastComposedFrameSize != (frame.Width, frame.Height);
                        _lastComposedOverlayRect = composedRect;
                        _lastComposedFrameSize = (frame.Width, frame.Height);
                        // 落位变了要立刻叫界面重摆拖动框。副画面是画进帧里的，界面那个框
                        // 平时不跟着每帧走，只在这里通知才不会停在上一帧的位置、和画面错开。
                        if (placementChanged)
                            NotifySecondaryOverlayPlacementChanged();
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
        /// 副摄识别框：与主摄同一套定义（宽高占比 + 居中偏移），在预览的画中画上直接拖。
        /// 它只决定识别哪一块，不裁剪画面内容。
        /// </summary>
        internal CameraBarcodeGuideGeometry GetSecondaryCameraGuideGeometry() =>
            Config is { } config
                ? new CameraBarcodeGuideGeometry(
                    config.SecondaryBarcodeGuideWidthRatio,
                    config.SecondaryBarcodeGuideHeightRatio,
                    config.SecondaryBarcodeGuideOffsetX,
                    config.SecondaryBarcodeGuideOffsetY)
                : new CameraBarcodeGuideGeometry(1.0, 1.0, 0, 0);

        /// <summary>识别来源选了副摄且副摄已经出帧时，识别框画在画中画上。</summary>
        internal bool IsSecondaryCameraGuideVisible => ShouldUseSecondaryCameraForBarcode;

        /// <summary>副摄识别框当前几何（与主摄同一套语义）。</summary>
        internal CameraBarcodeGuideGeometry CurrentSecondaryCameraBarcodeGuideGeometry =>
            GetSecondaryCameraGuideGeometry();

        private bool _isEditingSecondaryCameraPreview;
        private System.Windows.Media.Imaging.BitmapSource? _secondaryPreviewFrame;
        private DateTime _lastSecondaryPreviewPublishedAt = DateTime.MinValue;

        /// <summary>
        /// 是否正在编辑副摄取景：主预览区切成副摄整幅画面，识别框（含小锁）复用主摄那一套，
        /// 框内就是将来画中画显示与识别的内容。点画中画进入，点"完成"退出。
        /// </summary>
        public bool IsEditingSecondaryCameraPreview
        {
            get => _isEditingSecondaryCameraPreview;
            private set
            {
                if (!SetProperty(ref _isEditingSecondaryCameraPreview, value))
                    return;

                OnPropertyChanged(nameof(IsSecondaryPreviewEditing));
                OnPropertyChanged(nameof(PreviewImageSource));
                // 框只在识别来源那一路出现：进出编辑态会改变它的显隐。
                OnPropertyChanged(nameof(IsBarcodeGuideVisible));
                OnPropertyChanged(nameof(IsCameraBarcodeGuideLockVisible));
                OnPropertyChanged(nameof(IsCameraBarcodeGuideEditable));
                // 编辑态的提示文案与识别态不同，进出编辑态要重新取一次。
                OnPropertyChanged(nameof(CameraBarcodeStatusText));
                // 画面尺寸在编辑态下由副摄帧决定，界面要据此重摆识别框。
                OnPropertyChanged(nameof(CameraFrameSize));
                // 编辑态由副摄画面接管预览，别让主画面的帧把它冲掉。
                SuppressVideoPreviewUpdates = value;
            }
        }

        /// <summary>供界面按钮显隐使用。</summary>
        public bool IsSecondaryPreviewEditing => IsEditingSecondaryCameraPreview;

        /// <summary>副摄整幅画面（编辑态下由主预览区显示）。</summary>
        public System.Windows.Media.Imaging.BitmapSource? SecondaryPreviewFrame
        {
            get => _secondaryPreviewFrame;
            private set
            {
                if (SetProperty(ref _secondaryPreviewFrame, value))
                {
                    OnPropertyChanged(nameof(PreviewImageSource));
                    OnPropertyChanged(nameof(CameraFrameSize));
                }
            }
        }

        /// <summary>
        /// 主预览区当前该显示的帧：平常是主画面，进入副摄取景编辑后是副摄整幅画面。
        /// 界面只绑这一个属性，不必在代码里抢 Image.Source（抢了会被帧刷新冲掉）。
        /// </summary>
        public System.Windows.Media.Imaging.BitmapSource? PreviewImageSource =>
            IsEditingSecondaryCameraPreview ? SecondaryPreviewFrame : VideoFrame;

        /// <summary>点画中画进入取景编辑；副摄没在跑时不进（进去也没画面）。</summary>
        internal void EnterSecondaryCameraPreviewEdit()
        {
            if (!IsSecondaryCameraComposeEnabled)
                return;

            RuntimeLog.Info("SecondaryCamera", "进入副摄取景编辑");
            IsEditingSecondaryCameraPreview = true;
        }

        internal void ExitSecondaryCameraPreviewEdit()
        {
            RuntimeLog.Info("SecondaryCamera", "退出副摄取景编辑");
            IsEditingSecondaryCameraPreview = false;
            SecondaryPreviewFrame = null;
        }

        /// <summary>
        /// 编辑态下把副摄帧转成预览位图，节流到 30fps：既跟得上采集帧率、拖动取景时不卡，
        /// 又不会每一帧都做一次整帧转换。原来限在 10fps，现场反馈"副摄画面太卡"。
        /// </summary>
        private void PublishSecondaryPreviewFrameIfDue(Mat frame)
        {
            if (!IsEditingSecondaryCameraPreview || frame == null || frame.Empty())
                return;

            DateTime now = DateTime.Now;
            if (now - _lastSecondaryPreviewPublishedAt < TimeSpan.FromMilliseconds(33))
                return;
            _lastSecondaryPreviewPublishedAt = now;

            int width = frame.Width;
            int height = frame.Height;
            byte[] pixels = new byte[width * height * 3];
            System.Runtime.InteropServices.Marshal.Copy(frame.Data, pixels, 0, pixels.Length);
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
                width,
                height,
                96,
                96,
                System.Windows.Media.PixelFormats.Bgr24,
                null,
                pixels,
                width * 3);
            bitmap.Freeze();
            SecondaryPreviewFrame = bitmap;
        }

        /// <summary>
        /// 在预览里拖动副摄识别框时写回配置：拖动过程只改内存，松手才落盘。
        /// 与主摄识别框走同一套换算，只是参考矩形换成画中画。
        /// </summary>
        internal void ApplySecondaryCameraBarcodeGuideGeometry(
            CameraBarcodeGuideGeometry geometry,
            bool persist)
        {
            if (Config is not { } config)
                return;

            double widthRatio = AppConfig.NormalizeSecondaryGuideRatio(geometry.WidthRatio);
            double heightRatio = AppConfig.NormalizeSecondaryGuideRatio(geometry.HeightRatio);
            double offsetX = AppConfig.NormalizeGuideOffset(geometry.OffsetX);
            double offsetY = AppConfig.NormalizeGuideOffset(geometry.OffsetY);
            config.SecondaryBarcodeGuideWidthRatio = widthRatio;
            config.SecondaryBarcodeGuideHeightRatio = heightRatio;
            config.SecondaryBarcodeGuideOffsetX = offsetX;
            config.SecondaryBarcodeGuideOffsetY = offsetY;
            if (!persist)
                return;

            if (!WorkstationConfigStore.TryUpdate(
                    saved =>
                    {
                        saved.SecondaryBarcodeGuideWidthRatio = widthRatio;
                        saved.SecondaryBarcodeGuideHeightRatio = heightRatio;
                        saved.SecondaryBarcodeGuideOffsetX = offsetX;
                        saved.SecondaryBarcodeGuideOffsetY = offsetY;
                    },
                    out AppConfig savedConfig,
                    out string error))
            {
                RuntimeLog.Warn("SecondaryCamera", $"副摄识别框保存失败：{error}");
                return;
            }

            config.SecondaryBarcodeGuideWidthRatio = savedConfig.SecondaryBarcodeGuideWidthRatio;
            config.SecondaryBarcodeGuideHeightRatio = savedConfig.SecondaryBarcodeGuideHeightRatio;
            config.SecondaryBarcodeGuideOffsetX = savedConfig.SecondaryBarcodeGuideOffsetX;
            config.SecondaryBarcodeGuideOffsetY = savedConfig.SecondaryBarcodeGuideOffsetY;
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

            // 画中画显示的是识别框内那块，落位必须按裁剪后的尺寸与比例算，
            // 否则拖动框会跟画面错位（这正是之前"框和画面对不上"的来源）。
            System.Windows.Rect guide = CameraBarcodeGuideLayout.ToDisplayRect(
                GetSecondaryCameraGuideGeometry(),
                new System.Windows.Rect(0, 0, sourceWidth, sourceHeight));
            int croppedWidth = Math.Max(1, (int)Math.Round(guide.Width));
            int croppedHeight = Math.Max(1, (int)Math.Round(guide.Height));

            SecondaryCameraOverlayRect? resolved = SecondaryCameraOverlayPolicy.Resolve(
                frameWidth,
                frameHeight,
                croppedWidth,
                croppedHeight,
                config.SecondaryCameraOverlayWidthRatio,
                config.SecondaryCameraOverlayMargin,
                config.SecondaryCameraOverlayLeftRatio,
                config.SecondaryCameraOverlayTopRatio,
                allowUpscale: false);

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

        /// <summary>
        /// 拖动画中画右下角把手改大小：只改内存配置，下一帧合成立刻跟着变，松手才落盘。
        /// 与设置页原先是同一个字段（SecondaryCameraOverlayWidthRatio），现在只保留这一个入口。
        /// </summary>
        internal void SetSecondaryCameraOverlayWidth(double widthRatio)
        {
            if (Config is not { } config)
                return;

            config.SecondaryCameraOverlayWidthRatio =
                SecondaryCameraOverlayPolicy.NormalizeWidthRatio(widthRatio);
        }

        /// <summary>把拖动改出来的副画面大小落盘。</summary>
        internal void SaveSecondaryCameraOverlayWidth()
        {
            if (Config is not { } config)
                return;

            double widthRatio = config.SecondaryCameraOverlayWidthRatio;
            if (!WorkstationConfigStore.TryUpdate(
                    saved => saved.SecondaryCameraOverlayWidthRatio = widthRatio,
                    out AppConfig savedConfig,
                    out string error))
            {
                RuntimeLog.Warn("SecondaryCamera", $"副画面大小保存失败：{error}");
                return;
            }

            Config.SecondaryCameraOverlayWidthRatio = savedConfig.SecondaryCameraOverlayWidthRatio;
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
