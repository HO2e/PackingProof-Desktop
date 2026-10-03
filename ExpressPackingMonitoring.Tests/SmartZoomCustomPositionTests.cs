using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class SmartZoomCustomPositionTests
{
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.25, 0.25)]
    [InlineData(0.5, 0.5)]
    [InlineData(1.0, 1.0)]
    [InlineData(1.5, 1.0)]
    public void NormalizeCenter_ClampsFiniteRatiosIntoUnitRange(double input, double expected) =>
        Assert.Equal(expected, SmartZoomCustomPositionPolicy.NormalizeCenter(input), 3);

    [Theory]
    [InlineData(-1.0)]
    [InlineData(-0.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NormalizeCenter_TreatsNegativeOrNonFiniteAsUnset(double input) =>
        Assert.Equal(AppConfig.UnsetOverlayPosition, SmartZoomCustomPositionPolicy.NormalizeCenter(input), 3);

    [Fact]
    public void ResolveCenter_UsesFrameCenterWhenNeverSet()
    {
        (double x, double y) = SmartZoomCustomPositionPolicy.ResolveCenter(
            AppConfig.UnsetOverlayPosition,
            AppConfig.UnsetOverlayPosition);

        Assert.Equal(0.5, x, 3);
        Assert.Equal(0.5, y, 3);
    }

    [Fact]
    public void ResolveCenter_UsesConfiguredPositionWhenSet()
    {
        (double x, double y) = SmartZoomCustomPositionPolicy.ResolveCenter(0.2, 0.8);

        Assert.Equal(0.2, x, 3);
        Assert.Equal(0.8, y, 3);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.25, 0.75)]
    [InlineData(1.0, 1.0)]
    public void CreateCenterGeometry_CentersOnRequestedRatio(double ratioX, double ratioY)
    {
        const int width = 1920;
        const int height = 1080;

        CameraBarcodeGeometry geometry =
            SmartZoomCustomPositionPolicy.CreateCenterGeometry(width, height, ratioX, ratioY);

        Assert.Equal(width * ratioX, geometry.CenterX, 3);
        Assert.Equal(height * ratioY, geometry.CenterY, 3);
    }

    /// <summary>
    /// 手动定位没有条码尺寸可参考，合成框必须小到不触发 GetBoundedScale 的倍率限制；
    /// 否则用户设的放大倍数会被压成 1 倍，特写形同失效。
    /// </summary>
    [Fact]
    public void CreateCenterGeometry_DoesNotCapRequestedZoomScale()
    {
        CameraBarcodeGeometry geometry =
            SmartZoomCustomPositionPolicy.CreateCenterGeometry(1920, 1080, 0.5, 0.5);

        double bounded = SmartZoomPolicy.GetBoundedScale(1920, 1080, 2.5, geometry);

        Assert.Equal(2.5, bounded, 3);
    }

    /// <summary>
    /// 没有手动定位时仍应回落识别到的面单位置，保持原有行为。
    /// 面单取画面中部，避免裁剪框被画面边界夹紧后中心偏移，干扰这条断言。
    /// </summary>
    [Fact]
    public void CreateCropRect_WithoutCustomCenter_StillCentersOnBarcode()
    {
        var barcode = new CameraBarcodeGeometry(900, 480, 200, 120);

        OpenCvSharp.Rect rect = SmartZoomPolicy.CreateCropRect(1920, 1080, 2.0, barcode);

        Assert.Equal(barcode.CenterX, rect.X + (rect.Width / 2.0), 0);
        Assert.Equal(barcode.CenterY, rect.Y + (rect.Height / 2.0), 0);
    }
}
