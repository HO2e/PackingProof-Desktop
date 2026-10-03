using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.ViewModels;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 第二路摄像头画面贴右下角的落位规则：按副画面自身比例缩放、贴右下角、
/// 永远不越出主画面；尺寸非法时不叠加而不是画出一个坏 ROI。
/// </summary>
public sealed class SecondaryCameraOverlayPolicyTests
{
    [Fact]
    public void DefaultRatio_SitsInBottomRightCorner()
    {
        SecondaryCameraOverlayRect? rect = SecondaryCameraOverlayPolicy.Resolve(
            frameWidth: 1920,
            frameHeight: 1080,
            overlaySourceWidth: 1920,
            overlaySourceHeight: 1080,
            widthRatio: SecondaryCameraOverlayPolicy.DefaultWidthRatio,
            margin: SecondaryCameraOverlayPolicy.DefaultMargin);

        Assert.NotNull(rect);
        // 1920 * 0.25 = 480，16:9 → 270
        Assert.Equal(480, rect!.Value.Width);
        Assert.Equal(270, rect.Value.Height);
        Assert.Equal(1920 - 16 - 480, rect.Value.X);
        Assert.Equal(1080 - 16 - 270, rect.Value.Y);
    }

    /// <summary>副画面比例必须跟着**副**画面走，不能被主画面拉变形。</summary>
    [Theory]
    [InlineData(1920, 1080, 16.0 / 9.0)]
    [InlineData(1024, 768, 4.0 / 3.0)]
    [InlineData(1080, 1920, 9.0 / 16.0)]
    public void KeepsOverlaySourceAspect(int overlayWidth, int overlayHeight, double expectedAspect)
    {
        SecondaryCameraOverlayRect? rect = SecondaryCameraOverlayPolicy.Resolve(
            1920, 1080, overlayWidth, overlayHeight, 0.25, 16);

        Assert.NotNull(rect);
        Assert.Equal(expectedAspect, (double)rect!.Value.Width / rect.Value.Height, precision: 2);
    }

    /// <summary>竖屏副画面在主画面里放不下时按可用高度反推宽度，而不是纵向顶出画面。</summary>
    [Fact]
    public void TallOverlay_ShrinksByHeightInsteadOfOverflowing()
    {
        SecondaryCameraOverlayRect? rect = SecondaryCameraOverlayPolicy.Resolve(
            1920, 1080, overlaySourceWidth: 720, overlaySourceHeight: 1280, widthRatio: 0.5, margin: 16);

        Assert.NotNull(rect);
        Assert.True(rect!.Value.Y >= 0, "副画面顶部越出了主画面");
        Assert.True(rect.Value.Y + rect.Value.Height <= 1080, "副画面底部越出了主画面");
        Assert.True(rect.Value.Height <= 1080 - 32);
    }

    [Theory]
    [InlineData(1920, 1080, 16)]
    [InlineData(1280, 720, 8)]
    [InlineData(640, 360, 0)]
    [InlineData(3840, 2160, 48)]
    public void AlwaysStaysInsideFrame(int frameWidth, int frameHeight, int margin)
    {
        SecondaryCameraOverlayRect? rect = SecondaryCameraOverlayPolicy.Resolve(
            frameWidth, frameHeight, 1920, 1080, 0.25, margin);

        Assert.NotNull(rect);
        Assert.True(rect!.Value.X >= 0 && rect.Value.Y >= 0);
        Assert.True(rect.Value.X + rect.Value.Width <= frameWidth);
        Assert.True(rect.Value.Y + rect.Value.Height <= frameHeight);
    }

    /// <summary>宽度比例异常时退回默认值，不能被配成 0 或比主画面还大。</summary>
    [Theory]
    [InlineData(0.0, SecondaryCameraOverlayPolicy.DefaultWidthRatio)]
    [InlineData(-1.0, SecondaryCameraOverlayPolicy.DefaultWidthRatio)]
    [InlineData(double.NaN, SecondaryCameraOverlayPolicy.DefaultWidthRatio)]
    [InlineData(0.01, SecondaryCameraOverlayPolicy.MinimumWidthRatio)]
    [InlineData(0.95, SecondaryCameraOverlayPolicy.MaximumWidthRatio)]
    [InlineData(0.3, 0.3)]
    public void NormalizesWidthRatio(double raw, double expected)
    {
        Assert.Equal(expected, SecondaryCameraOverlayPolicy.NormalizeWidthRatio(raw));
    }

    [Theory]
    [InlineData(0, 0, 1920, 1080)]
    [InlineData(1920, 1080, 0, 0)]
    [InlineData(-100, 1080, 1920, 1080)]
    [InlineData(1920, 1080, -1, 1080)]
    public void InvalidSizesProduceNoOverlay(int frameWidth, int frameHeight, int overlayWidth, int overlayHeight)
    {
        Assert.Null(SecondaryCameraOverlayPolicy.Resolve(
            frameWidth, frameHeight, overlayWidth, overlayHeight, 0.25, 16));
    }

    /// <summary>主画面小到放不下副画面时宁可不画，也不返回一个 0 宽或越界的矩形。</summary>
    [Fact]
    public void TinyFrame_ProducesNoOverlay()
    {
        Assert.Null(SecondaryCameraOverlayPolicy.Resolve(120, 80, 1920, 1080, 0.25, 16));
    }

    /// <summary>用户拖动过之后按记录的比例落位，不再贴右下角。</summary>
    [Fact]
    public void CustomPosition_OverridesBottomRight()
    {
        SecondaryCameraOverlayRect? rect = SecondaryCameraOverlayPolicy.Resolve(
            1920, 1080, 1920, 1080, 0.25, 16, leftRatio: 0.1, topRatio: 0.2);

        Assert.NotNull(rect);
        Assert.Equal(192, rect!.Value.X);   // 0.1 * 1920
        Assert.Equal(216, rect.Value.Y);    // 0.2 * 1080
        Assert.Equal(480, rect.Value.Width);
        Assert.Equal(270, rect.Value.Height);
    }

    /// <summary>拖到画面外时必须夹回来，保证整块小窗都留在画面里。</summary>
    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(5.0, 5.0)]
    [InlineData(0.95, 0.95)]
    public void CustomPosition_IsClampedInsideFrame(double leftRatio, double topRatio)
    {
        SecondaryCameraOverlayRect? rect = SecondaryCameraOverlayPolicy.Resolve(
            1920, 1080, 1920, 1080, 0.25, 16, leftRatio: leftRatio, topRatio: topRatio);

        Assert.NotNull(rect);
        Assert.True(rect!.Value.X >= 0 && rect.Value.Y >= 0);
        Assert.True(rect.Value.X + rect.Value.Width <= 1920);
        Assert.True(rect.Value.Y + rect.Value.Height <= 1080);
    }

    /// <summary>还没拖动过（哨兵值）时仍然贴右下角，保持默认观感。</summary>
    [Fact]
    public void UnsetPosition_FallsBackToBottomRight()
    {
        SecondaryCameraOverlayRect? withSentinel = SecondaryCameraOverlayPolicy.Resolve(
            1920, 1080, 1920, 1080, 0.25, 16,
            leftRatio: AppConfig.UnsetOverlayPosition,
            topRatio: AppConfig.UnsetOverlayPosition);
        SecondaryCameraOverlayRect? withoutArgument = SecondaryCameraOverlayPolicy.Resolve(
            1920, 1080, 1920, 1080, 0.25, 16);

        Assert.NotNull(withSentinel);
        Assert.Equal(withoutArgument, withSentinel);
        Assert.Equal(1920 - 16 - 480, withSentinel!.Value.X);
        Assert.Equal(1080 - 16 - 270, withSentinel.Value.Y);
    }
}
