using System.Text;
using ExpressPackingMonitoring.Config;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 第二路摄像头（副画面）的配置与接线约束：
/// 默认关闭、比例与留白被夹紧、改副路配置要求重启采集；
/// 合成必须接在处理循环里（预览与录像共用的那一帧上），且旧的"同一路内嵌小窗"不得再出现。
/// </summary>
public sealed class SecondaryCameraConfigurationTests
{
    [Fact]
    public void SecondaryCameraIsOffByDefault()
    {
        var config = new AppConfig();

        // 默认必须关：老用户升级后画面不能凭空多出一路。
        Assert.Equal(AppConfig.SecondaryCameraSourceNone, config.SecondaryCameraSourceKind);
        Assert.False(config.EnableSecondaryCamera);
        Assert.Equal(AppConfig.DefaultSecondaryOverlayWidthRatio, config.SecondaryCameraOverlayWidthRatio);
        Assert.Equal(AppConfig.DefaultSecondaryOverlayMargin, config.SecondaryCameraOverlayMargin);
    }

    [Theory]
    [InlineData(0.0, AppConfig.DefaultSecondaryOverlayWidthRatio)]
    [InlineData(-1.0, AppConfig.DefaultSecondaryOverlayWidthRatio)]
    [InlineData(double.NaN, AppConfig.DefaultSecondaryOverlayWidthRatio)]
    [InlineData(0.01, AppConfig.MinimumSecondaryOverlayWidthRatio)]
    [InlineData(0.9, AppConfig.MaximumSecondaryOverlayWidthRatio)]
    [InlineData(0.3, 0.3)]
    public void NormalizeAfterLoad_ClampsSecondaryOverlayWidthRatio(double raw, double expected)
    {
        var config = new AppConfig { SecondaryCameraOverlayWidthRatio = raw };

        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal(expected, config.SecondaryCameraOverlayWidthRatio);
    }

    [Theory]
    [InlineData(-5, AppConfig.DefaultSecondaryOverlayMargin)]
    [InlineData(0, 0)]
    [InlineData(5000, AppConfig.MaximumSecondaryOverlayMargin)]
    [InlineData(24, 24)]
    public void NormalizeAfterLoad_ClampsSecondaryOverlayMargin(int raw, int expected)
    {
        var config = new AppConfig { SecondaryCameraOverlayMargin = raw };

        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal(expected, config.SecondaryCameraOverlayMargin);
    }

    /// <summary>副路地址和来源与主路同口径：去空白、判来源、传输方式归一。</summary>
    [Fact]
    public void NormalizeAfterLoad_NormalizesSecondarySource()
    {
        var config = new AppConfig
        {
            // 已经启用过副摄的老配置：来源要按主路同口径归一，不能被迁移成"无"。
            EnableSecondaryCamera = true,
            SecondaryCameraSourceKind = "什么都不是",
            SecondaryNetworkCameraUrl = "  rtsp://192.168.1.9/stream  ",
            SecondaryNetworkCameraRtspTransport = "UDP"
        };

        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal("network", config.SecondaryCameraSourceKind);
        Assert.Equal("rtsp://192.168.1.9/stream", config.SecondaryNetworkCameraUrl);
        Assert.Equal("udp", config.SecondaryNetworkCameraRtspTransport);
    }

    /// <summary>
    /// 副画面规格预设只认白名单，写坏或旧配置一律回到 720p：
    /// 静默换成默认规格，比卡在一个非法尺寸上更容易解释。
    /// </summary>
    [Theory]
    [InlineData(null, "720p")]
    [InlineData("", "720p")]
    [InlineData("480p", "480p")]
    [InlineData("720p", "720p")]
    [InlineData("1080P", "1080p")]
    [InlineData("2160p", "720p")]
    public void NormalizeSecondaryResolutionPreset_FallsBackTo720p(string? raw, string expected) =>
        Assert.Equal(expected, AppConfig.NormalizeSecondaryResolutionPreset(raw));

    [Fact]
    public void ResolveSecondaryFrameSize_MapsPresets()
    {
        Assert.Equal((640, 480), AppConfig.ResolveSecondaryFrameSize("480p"));
        Assert.Equal((1280, 720), AppConfig.ResolveSecondaryFrameSize("720p"));
        Assert.Equal((1920, 1080), AppConfig.ResolveSecondaryFrameSize("1080p"));
        Assert.Equal((1280, 720), AppConfig.ResolveSecondaryFrameSize("不属于任何预设"));
    }

    /// <summary>副画面帧率是独立设置项，必须被夹到合法区间，0 回落到默认值。</summary>
    [Fact]
    public void NormalizeAfterLoad_ClampsSecondaryFrameRate()
    {
        var config = new AppConfig { SecondaryFrameFps = 0 };
        AppConfig.NormalizeAfterLoad(config);
        Assert.Equal(AppConfig.DefaultSecondaryFrameFps, config.SecondaryFrameFps);

        config = new AppConfig { SecondaryFrameFps = 999 };
        AppConfig.NormalizeAfterLoad(config);
        Assert.Equal(AppConfig.MaximumSecondaryFrameFps, config.SecondaryFrameFps);
    }

    /// <summary>换副画面规格必须重建采集会话，否则设置改了不生效。</summary>
    [Fact]
    public void RequiresCameraRestart_ReactsToSecondaryCaptureFormat()
    {
        var current = new AppConfig();
        Assert.True(AppConfig.RequiresCameraRestart(
            current,
            new AppConfig { SecondaryResolutionPreset = "1080p" }));
        Assert.True(AppConfig.RequiresCameraRestart(
            current,
            new AppConfig { SecondaryFrameFps = 30 }));
    }

    /// <summary>改了副路就必须重启采集，否则设置里换设备不会生效。</summary>
    [Fact]
    public void RequiresCameraRestart_ReactsToSecondaryCameraChanges()
    {
        var current = new AppConfig();

        Assert.True(AppConfig.RequiresCameraRestart(
            current,
            new AppConfig { SecondaryCameraSourceKind = "usb" }));
        Assert.True(AppConfig.RequiresCameraRestart(
            current,
            new AppConfig { SecondaryCameraMonikerString = "别的一台" }));
        Assert.True(AppConfig.RequiresCameraRestart(
            current,
            new AppConfig { SecondaryCameraSourceKind = "network", SecondaryNetworkCameraUrl = "rtsp://x/y" }));
        Assert.True(AppConfig.RequiresCameraRestart(
            current,
            new AppConfig { SecondaryCameraRotate180 = true }));

        // 与采集无关的字段不能引起重启。
        Assert.False(AppConfig.RequiresCameraRestart(
            current,
            new AppConfig { SecondaryCameraOverlayWidthRatio = 0.4 }));
        Assert.False(AppConfig.RequiresCameraRestart(
            current,
            new AppConfig { CameraBarcodeRecognitionSource = AppConfig.CameraBarcodeSourceSecondary }));
    }

    /// <summary>
    /// 合成必须挂在处理循环里、预览发布与录像入队之前：预览和录像共用那一帧，
    /// 合成一次两边都有，不能各自去叠一遍。
    /// </summary>
    [Fact]
    public void OverlayIsComposedInsideTheSingleFramePipeline()
    {
        string camera = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.Camera.cs"));

        int composeIndex = camera.IndexOf(
            "ComposeSecondaryCameraOverlayIfNeeded(processedFrame, previewPublishDue)",
            StringComparison.Ordinal);
        Assert.True(composeIndex >= 0, "处理循环里没有调用副画面合成");

        int previewIndex = camera.IndexOf(
            "PublishPreviewFrameIfDue(processedFrame, previewResizer, currentFrameCapturedTicks)",
            StringComparison.Ordinal);
        int recorderIndex = camera.IndexOf(
            "TryEnqueueFrameForRecording(processedFrame, currentFrameCapturedTicks)",
            StringComparison.Ordinal);

        Assert.True(previewIndex > composeIndex, "合成必须在预览发布之前");
        Assert.True(recorderIndex > composeIndex, "合成必须在录像入队之前");
    }

    /// <summary>
    /// 水印必须后于副画面绘制：水印承载时间戳与单号，是取证核心，必须永远压在最上层。
    /// 顺序反过来时，用户把副画面拖到右上角就会把水印盖掉。
    /// </summary>
    [Fact]
    public void WatermarkIsDrawnAfterTheSecondaryOverlay()
    {
        string camera = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.Camera.cs"));
        int composeIndex = camera.IndexOf(
            "ComposeSecondaryCameraOverlayIfNeeded(processedFrame, previewPublishDue)",
            StringComparison.Ordinal);
        int watermarkIndex = camera.IndexOf(
            "ApplyWatermarkToFrame(processedFrame",
            StringComparison.Ordinal);
        Assert.True(composeIndex >= 0, "处理循环里没有调用副画面合成");
        Assert.True(watermarkIndex >= 0, "处理循环里没有绘制水印");
        Assert.True(watermarkIndex > composeIndex, "水印必须在副画面之后绘制");

        string recording = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.Recording.cs"));
        int preComposeIndex = recording.IndexOf(
            "ComposeSecondaryCameraOverlayIfNeeded(preFrame",
            StringComparison.Ordinal);
        int preWatermarkIndex = recording.IndexOf(
            "ApplyWatermarkToFrame(preFrame",
            StringComparison.Ordinal);
        Assert.True(preComposeIndex >= 0 && preWatermarkIndex >= 0, "预录帧缺少合成或水印");
        Assert.True(preWatermarkIndex > preComposeIndex, "预录帧的水印必须在副画面之后绘制");
    }

    /// <summary>预录帧也必须贴副画面，否则录像开头几秒只有主画面、与后面接不上。</summary>
    [Fact]
    public void PreRecordFramesAlsoGetTheOverlay()
    {
        string recording = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.Recording.cs"));

        int composeIndex = recording.IndexOf(
            "ComposeSecondaryCameraOverlayIfNeeded(preFrame",
            StringComparison.Ordinal);
        Assert.True(composeIndex >= 0, "预录帧没有贴副画面");

        int enqueueIndex = recording.IndexOf(
            "TryEnqueueFrameForRecording(preFrame",
            StringComparison.Ordinal);
        Assert.True(enqueueIndex > composeIndex, "副画面合成必须在预录入队之前");
    }

    /// <summary>副画面是"第二路采集"，不能再出现旧的"同一路内嵌小窗"叠加层。</summary>
    [Fact]
    public void LegacyInlineOverlayIsGone()
    {
        string mainWindow = ReadProjectFile(Path.Combine("UI", "MainWindow.xaml"));
        Assert.DoesNotContain("InlinePipHost", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("InlinePipImage", mainWindow, StringComparison.Ordinal);

        string mainWindowCode = ReadProjectFile(Path.Combine("UI", "MainWindow.xaml.cs"));
        Assert.DoesNotContain("InlinePip", mainWindowCode, StringComparison.Ordinal);

        // 主界面依旧不允许新增小窗入口按钮。
        Assert.DoesNotContain("FloatingPreviewButton", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("BtnFloatingPreview_Click", mainWindow, StringComparison.Ordinal);
    }

    /// <summary>设置页用纯绑定暴露副画面，避免让被冻结的 SettingsWindow.xaml.cs 继续增长。</summary>
    [Fact]
    public void SettingsWindow_ExposesSecondaryCameraByBindingOnly()
    {
        string settings = ReadProjectFile(Path.Combine("UI", "SettingsWindow.xaml"));

        // 来源就是开关：选"无"时其余选项整块收起。
        Assert.Contains("SecondaryCameraDeviceComboBox", settings, StringComparison.Ordinal);
        Assert.Contains("{Binding SecondaryCameraChoices}", settings, StringComparison.Ordinal);
        Assert.Contains("{Binding SelectedSecondaryCameraChoice", settings, StringComparison.Ordinal);
        Assert.Contains(
            "Visibility=\"{Binding IsSecondaryCameraConfigured, Converter={StaticResource BoolToVisibility}}\"",
            settings,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SecondaryCameraCheckBox", settings, StringComparison.Ordinal);
        Assert.Contains("{Binding Config.SecondaryNetworkCameraUrl", settings, StringComparison.Ordinal);
        Assert.Contains("{Binding Config.SecondaryCameraOverlayWidthRatio", settings, StringComparison.Ordinal);
        Assert.Contains("{Binding Config.SecondaryResolutionPreset", settings, StringComparison.Ordinal);
        Assert.Contains("{Binding Config.SecondaryFrameFps", settings, StringComparison.Ordinal);
    }

    /// <summary>第二路独立采集：不能占用主路的帧槽与会话闸门。</summary>
    [Fact]
    public void SecondaryCameraOwnsSeparateCaptureState()
    {
        string source = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.SecondaryCamera.cs"));

        Assert.Contains("_latestSecondaryCameraFrame", source, StringComparison.Ordinal);
        Assert.Contains("SecondaryVideoSource_NewFrame", source, StringComparison.Ordinal);
        Assert.Contains("SecondaryNetworkCameraSource_FrameReady", source, StringComparison.Ordinal);
        Assert.Contains("TryCompose", source, StringComparison.Ordinal);

        // 副路不得写主路的帧槽，也不得复用主路的会话闸门。
        Assert.DoesNotContain("_latestCameraFrame", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_previewSessionGate", source, StringComparison.Ordinal);
    }

    /// <summary>位置只在拖动过之后才是 0~1 的比例，非法值一律回到"自动右下角"。</summary>
    [Theory]
    [InlineData(-5.0, AppConfig.UnsetOverlayPosition)]
    [InlineData(double.NaN, AppConfig.UnsetOverlayPosition)]
    [InlineData(-1.0, AppConfig.UnsetOverlayPosition)]
    [InlineData(2.0, 1.0)]
    [InlineData(0.35, 0.35)]
    public void NormalizeAfterLoad_ClampsSecondaryOverlayPosition(double raw, double expected)
    {
        var config = new AppConfig
        {
            SecondaryCameraOverlayLeftRatio = raw,
            SecondaryCameraOverlayTopRatio = raw
        };

        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal(expected, config.SecondaryCameraOverlayLeftRatio);
        Assert.Equal(expected, config.SecondaryCameraOverlayTopRatio);
    }

    /// <summary>识别来源只认 secondary，其余一律回主画面：写错不能变成两边都不识别。</summary>
    [Theory]
    [InlineData(null, AppConfig.CameraBarcodeSourcePrimary)]
    [InlineData("", AppConfig.CameraBarcodeSourcePrimary)]
    [InlineData("primary", AppConfig.CameraBarcodeSourcePrimary)]
    [InlineData("SECONDARY", AppConfig.CameraBarcodeSourceSecondary)]
    [InlineData("乱写", AppConfig.CameraBarcodeSourcePrimary)]
    public void NormalizeCameraBarcodeSource_FallsBackToPrimary(string? raw, string expected)
    {
        Assert.Equal(expected, AppConfig.NormalizeCameraBarcodeSource(raw));
    }

    /// <summary>默认必须是主画面识别，否则升级后所有老用户的面单识别来源会静默改变。</summary>
    [Fact]
    public void BarcodeRecognitionSourceDefaultsToPrimary()
    {
        var config = new AppConfig();

        Assert.Equal(AppConfig.CameraBarcodeSourcePrimary, config.CameraBarcodeRecognitionSource);
    }

    /// <summary>
    /// 识别来源是二选一：两路都提交会让稳定性追踪器在两种画面之间反复归零，
    /// 结果谁都认不出来。所以提交入口必须按来源分流，副路只在被选中时参与。
    /// </summary>
    [Fact]
    public void BarcodeRecognitionPicksExactlyOneSource()
    {
        string scanner = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.Scanner.cs"));
        Assert.Contains("ShouldUseSecondaryCameraForBarcode", scanner, StringComparison.Ordinal);
        Assert.Contains("fromSecondaryCamera != shouldUseSecondary", scanner, StringComparison.Ordinal);

        string secondary = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.SecondaryCamera.cs"));
        Assert.Contains("ShouldUseSecondaryCameraForBarcode", secondary, StringComparison.Ordinal);
        // 副路的两条采集路径都要把帧交给识别入口。
        Assert.Contains(
            "TrySubmitCameraBarcodeFrame(frame, fromSecondaryCamera: true);",
            secondary,
            StringComparison.Ordinal);
    }

    /// <summary>副路没出帧时必须回退主画面，否则"选了副路但没连上"= 完全识别不了。</summary>
    [Fact]
    public void BarcodeRecognitionFallsBackWhenSecondaryHasNoFrame()
    {
        string secondary = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.SecondaryCamera.cs"));

        Assert.Contains("HasSecondaryCameraFrame", secondary, StringComparison.Ordinal);
    }

    /// <summary>
    /// 副摄识别框有自己的比例（不能套主画面那套构图），并且要跳过运动门控：
    /// 面单放好后副画面是静止的，不跳过门控就永远解不出静止条码。
    /// </summary>
    [Fact]
    public void SecondaryBarcodeRecognitionUsesCropGeometryAndSkipsMotionGate()
    {
        string scanner = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.Scanner.cs"));

        Assert.Contains("GetSecondaryCameraGuideGeometry()", scanner, StringComparison.Ordinal);
        Assert.Contains("forceDecode: fromSecondaryCamera", scanner, StringComparison.Ordinal);

        string secondary = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.SecondaryCamera.cs"));
        Assert.Contains("SecondaryBarcodeGuideWidthRatio", secondary, StringComparison.Ordinal);

        string service = ReadProjectFile(Path.Combine("Services", "CameraBarcodeRecognitionService.cs"));
        Assert.Contains("public bool TrySubmitFrame(Mat frame, bool forceDecode = false)", service, StringComparison.Ordinal);
    }

    /// <summary>默认必须是"还没拖动过"，否则首次启动副画面就会跑到左上角。</summary>
    [Fact]
    public void SecondaryOverlayPositionStartsUnset()
    {
        var config = new AppConfig();

        Assert.Equal(AppConfig.UnsetOverlayPosition, config.SecondaryCameraOverlayLeftRatio);
        Assert.Equal(AppConfig.UnsetOverlayPosition, config.SecondaryCameraOverlayTopRatio);
    }

    /// <summary>副摄识别框默认居中、占画面八成半，用户不设置也能直接用。</summary>
    [Fact]
    public void SecondaryBarcodeGuideDefaultsToCenteredBox()
    {
        var config = new AppConfig();

        Assert.Equal(AppConfig.DefaultSecondaryGuideRatio, config.SecondaryBarcodeGuideWidthRatio);
        Assert.Equal(AppConfig.DefaultSecondaryGuideRatio, config.SecondaryBarcodeGuideHeightRatio);
        Assert.Equal(0.0, config.SecondaryBarcodeGuideOffsetX);
        Assert.Equal(0.0, config.SecondaryBarcodeGuideOffsetY);
    }

    /// <summary>
    /// 来源就是开关：选"无"整条链路关闭，兼容字段跟着同步，降级回旧版本仍能识别。
    /// 没有这个字段的老配置反序列化后就是默认值"无"，不会凭空多出一路副摄。
    /// </summary>
    [Fact]
    public void SecondaryCameraSourceReplacesTheLegacySwitch()
    {
        var picked = new AppConfig { SecondaryCameraSourceKind = "network", SecondaryNetworkCameraUrl = "rtsp://x/y" };
        AppConfig.NormalizeAfterLoad(picked);
        Assert.Equal("network", picked.SecondaryCameraSourceKind);
        Assert.True(picked.EnableSecondaryCamera);

        var none = new AppConfig { SecondaryCameraSourceKind = AppConfig.SecondaryCameraSourceNone };
        AppConfig.NormalizeAfterLoad(none);
        Assert.False(none.EnableSecondaryCamera);

        // 反序列化后没有这个字段的老配置：默认就是"无"。
        Assert.Equal(AppConfig.SecondaryCameraSourceNone, new AppConfig().SecondaryCameraSourceKind);
    }

    /// <summary>
    /// 主界面必须给出可拖动的副画面框：副画面是画进帧里的，没有它就没法用鼠标调位置。
    /// </summary>
    [Fact]
    public void MainWindow_ExposesDraggableOverlayThumb()
    {
        string mainWindow = ReadProjectFile(Path.Combine("UI", "MainWindow.xaml"));
        Assert.Contains("x:Name=\"SecondaryOverlayDragThumb\"", mainWindow, StringComparison.Ordinal);
        Assert.Contains("SecondaryOverlayDragThumb_DragDelta", mainWindow, StringComparison.Ordinal);
        Assert.Contains("SecondaryOverlayDragCompleted", mainWindow, StringComparison.Ordinal);

        string codeBehind = ReadProjectFile(Path.Combine("UI", "MainWindow.xaml.cs"));
        Assert.Contains("SetSecondaryCameraOverlayPosition", codeBehind, StringComparison.Ordinal);
        Assert.Contains("SaveSecondaryCameraOverlayPosition", codeBehind, StringComparison.Ordinal);
        // 拖动框与合成必须用同一套落位算法，不能各算一份。
        Assert.Contains("TryResolveSecondaryOverlayRect", codeBehind, StringComparison.Ordinal);
    }

    private static string ReadProjectFile(string relativePath) =>
        File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "ExpressPackingMonitoring", relativePath),
            Encoding.UTF8);

    private static string FindRepositoryRoot()
    {
        foreach (string startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? directory = new(Path.GetFullPath(startPath));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("ExpressPackingMonitoring repository root was not found.");
    }
}
