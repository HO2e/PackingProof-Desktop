using OpenCvSharp;

namespace ExpressPackingMonitoring.ViewModels
{
    /// <summary>
    /// 把第二路摄像头画面缩放后贴到主画面右下角。
    ///
    /// 直接画在录像/预览共用的那一帧上（与水印同一手法），所以预览和录像天然一致：
    /// 不需要第二套发布管线，也不会出现"预览里有、录像里没有"的分叉。
    /// 条码识别在读帧阶段就已经跑完，副画面不会进入识别输入。
    /// </summary>
    internal static class SecondaryCameraFrameComposer
    {
        /// <summary>小窗边框像素宽度，让副画面从主画面里"浮"出来。</summary>
        private const int BorderThickness = 2;

        /// <summary>
        /// 叠加成功返回 true。任何一路帧缺失、尺寸非法或放不下都返回 false 且不改动主帧，
        /// 调用方按"这一帧没有副画面"处理。
        /// </summary>
        internal static bool TryCompose(
            Mat frame,
            Mat secondaryFrame,
            double widthRatio,
            int margin,
            double leftRatio = Config.AppConfig.UnsetOverlayPosition,
            double topRatio = Config.AppConfig.UnsetOverlayPosition)
        {
            if (frame == null || frame.IsDisposed || frame.Empty())
                return false;
            if (secondaryFrame == null || secondaryFrame.IsDisposed || secondaryFrame.Empty())
                return false;

            SecondaryCameraOverlayRect? target = SecondaryCameraOverlayPolicy.Resolve(
                frame.Width,
                frame.Height,
                secondaryFrame.Width,
                secondaryFrame.Height,
                widthRatio,
                margin,
                leftRatio,
                topRatio);
            if (target is not { } rect)
                return false;

            using var scaled = new Mat();
            Cv2.Resize(
                secondaryFrame,
                scaled,
                new Size(rect.Width, rect.Height),
                interpolation: InterpolationFlags.Area);

            using Mat bgr = EnsureSameChannels(scaled, frame.Channels());
            if (bgr.Empty() || bgr.Width != rect.Width || bgr.Height != rect.Height)
                return false;

            var targetRect = new Rect(rect.X, rect.Y, rect.Width, rect.Height);
            using var region = new Mat(frame, targetRect);
            bgr.CopyTo(region);

            // 边框画在画面内侧，不会越出主帧边界。
            Cv2.Rectangle(
                frame,
                new Rect(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2),
                new Scalar(255, 255, 255),
                BorderThickness);
            return true;
        }

        /// <summary>
        /// 把副画面转成与主帧一致的通道数。两路摄像头后端不同（灰度、BGRA、YUY2 转换结果不同）
        /// 时不能直接 CopyTo，否则 OpenCV 抛异常或写出错位的颜色。
        /// </summary>
        private static Mat EnsureSameChannels(Mat source, int targetChannels)
        {
            if (source.Channels() == targetChannels)
                return source.Clone();

            ColorConversionCodes? conversion = (source.Channels(), targetChannels) switch
            {
                (1, 3) => ColorConversionCodes.GRAY2BGR,
                (1, 4) => ColorConversionCodes.GRAY2BGRA,
                (3, 4) => ColorConversionCodes.BGR2BGRA,
                (4, 3) => ColorConversionCodes.BGRA2BGR,
                _ => null
            };

            if (conversion is null)
                return new Mat();

            var converted = new Mat();
            Cv2.CvtColor(source, converted, conversion.Value);
            return converted;
        }
    }
}
