using System.Text;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 悬浮小窗增强：支持手动常驻、宽度与不透明度可记忆。
/// 这里钉住配置夹紧、发布宽度取值，以及主界面不新增小窗入口按钮。
/// </summary>
public sealed class FloatingPreviewEnhancementTests
{
    [Fact]
    public void FloatingPreviewDefaults_KeepTheHistoricalWindow()
    {
        var config = new AppConfig();

        Assert.Equal(AppConfig.DefaultFloatingPreviewWidth, config.FloatingPreviewWidth);
        Assert.Equal(AppConfig.DefaultFloatingPreviewOpacity, config.FloatingPreviewOpacity);
    }

    /// <summary>
    /// 配置可能被手改成任意值，发布前必须夹回可用区间：
    /// 宽度太小会比 FloatingPreviewWindow.xaml 的 MinWidth 还窄，不透明度太低就看不清画面。
    /// </summary>
    [Theory]
    [InlineData(50, AppConfig.MinimumFloatingPreviewWidth)]
    [InlineData(0, AppConfig.DefaultFloatingPreviewWidth)]
    [InlineData(-120, AppConfig.DefaultFloatingPreviewWidth)]
    [InlineData(double.NaN, AppConfig.DefaultFloatingPreviewWidth)]
    [InlineData(5000, AppConfig.MaximumFloatingPreviewWidth)]
    [InlineData(400, 400)]
    public void NormalizeAfterLoad_ClampsFloatingPreviewWidth(double raw, double expected)
    {
        var config = new AppConfig { FloatingPreviewWidth = raw };

        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal(expected, config.FloatingPreviewWidth);
    }

    [Theory]
    [InlineData(0.0, AppConfig.DefaultFloatingPreviewOpacity)]
    [InlineData(-1.0, AppConfig.DefaultFloatingPreviewOpacity)]
    [InlineData(double.NaN, AppConfig.DefaultFloatingPreviewOpacity)]
    [InlineData(0.05, AppConfig.MinimumFloatingPreviewOpacity)]
    [InlineData(4.0, AppConfig.MaximumFloatingPreviewOpacity)]
    [InlineData(0.8, 0.8)]
    public void NormalizeAfterLoad_ClampsFloatingPreviewOpacity(double raw, double expected)
    {
        var config = new AppConfig { FloatingPreviewOpacity = raw };

        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal(expected, config.FloatingPreviewOpacity);
    }

    /// <summary>
    /// 主界面预览与悬浮小窗可能同时在消费同一帧，只按最大的显示宽度发布：
    /// 发布得比控件小会被 WPF 插值放大，画面立刻发糊。
    /// </summary>
    [Theory]
    [InlineData(960, 340, 960)]
    [InlineData(0, 340, 340)]
    [InlineData(1200, 0, 1200)]
    [InlineData(0, 0, 0)]
    public void ResolvePublishWidth_UsesLargestVisibleConsumer(
        int main,
        int floating,
        int expected)
    {
        Assert.Equal(expected, PreviewDisplayWidthPolicy.ResolvePublishWidth(main, floating));
    }

    /// <summary>窗口还没布局时控件宽度是 0 或 NaN，必须折算成"没有消费方"，不能被当成很小的发布宽度。</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(340.4, 340)]
    [InlineData(339.6, 340)]
    public void NormalizeWidth_FoldsInvalidValuesToZero(double raw, int expected)
    {
        Assert.Equal(expected, PreviewDisplayWidthPolicy.NormalizeWidth(raw));
    }

    /// <summary>
    /// 手动开关要能在主窗口不最小化时打开小窗，所以在缩小时的状态之外单独记一个"手动钉住"标记；
    /// 否则主窗口一还原，小窗就被当成自动弹出的那一种收掉了。
    /// </summary>
    [Fact]
    public void FloatingPreviewController_SupportsManualPin()
    {
        string controller = ReadProjectFile(Path.Combine("UI", "FloatingPreviewController.cs"));

        Assert.Contains("internal void ToggleFromUser()", controller, StringComparison.Ordinal);
        Assert.Contains("_manuallyPinned", controller, StringComparison.Ordinal);
        Assert.Contains("ApplyConfiguredOpacity", controller, StringComparison.Ordinal);
    }

    /// <summary>小窗关闭时记住宽度，打开时按宽度贴回；高度由画面比例算，不记。</summary>
    [Fact]
    public void FloatingWindow_PersistsWidthAndOpacity()
    {
        string window = ReadProjectFile(Path.Combine("UI", "FloatingPreviewWindow.xaml.cs"));

        Assert.Contains("SaveFloatingPreviewWidth(Width)", window, StringComparison.Ordinal);
        Assert.Contains("public void ApplyConfiguredOpacity()", window, StringComparison.Ordinal);
        Assert.Contains("FloatingPreviewOpacity", window, StringComparison.Ordinal);

        string viewModel = ReadProjectFile(Path.Combine("ViewModels", "MainViewModel.FloatingPreview.cs"));
        Assert.Contains("PreviewDisplayWidthPolicy.ResolvePublishWidth", viewModel, StringComparison.Ordinal);
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
