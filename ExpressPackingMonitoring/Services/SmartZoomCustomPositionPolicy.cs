using ExpressPackingMonitoring.Config;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 「面单智能特写」手动定位：用户在主界面拖动十字标记指定特写中心，
/// 特写就固定放大到该位置，不再跟随识别到的面单坐标。
///
/// 用副画面识别时坐标来自另一台摄像头，直接套在主画面上本来就容易偏，
/// 手动定位让用户按自己的画面直接定，不依赖两边机位是否对得上。
/// 位置以 0~1 归一化保存，换分辨率或换摄像头都不必重设。
/// </summary>
internal static class SmartZoomCustomPositionPolicy
{
    /// <summary>用户没指定过位置时使用的中心，等价于画面正中</summary>
    internal const double DefaultCenterRatio = 0.5;

    /// <summary>归一化手动特写中心；未设置或非法值统一回到哨兵，与"设成 0.5"区分开</summary>
    internal static double NormalizeCenter(double value) => AppConfig.NormalizeOverlayPosition(value);

    /// <summary>解析实际生效的中心：用户设过就用设置值，没设过回落画面中心</summary>
    internal static (double RatioX, double RatioY) ResolveCenter(double configuredX, double configuredY)
    {
        double ratioX = NormalizeCenter(configuredX);
        double ratioY = NormalizeCenter(configuredY);
        return (
            ratioX < 0 ? DefaultCenterRatio : ratioX,
            ratioY < 0 ? DefaultCenterRatio : ratioY);
    }

    /// <summary>
    /// 把归一化中心合成为特写目标几何。外接框取极小尺寸是有意的：
    /// <see cref="SmartZoomPolicy.GetBoundedScale"/> 会按条码外接框反推倍率上限，
    /// 手动定位时没有条码尺寸，若给大框会把倍率压到 1 倍、特写直接失效。
    /// </summary>
    internal static CameraBarcodeGeometry CreateCenterGeometry(
        int frameWidth,
        int frameHeight,
        double ratioX,
        double ratioY)
    {
        double centerX = Math.Clamp(ratioX, 0.0, 1.0) * frameWidth;
        double centerY = Math.Clamp(ratioY, 0.0, 1.0) * frameHeight;
        return new CameraBarcodeGeometry(centerX - 1.0, centerY - 1.0, 2.0, 2.0);
    }
}
