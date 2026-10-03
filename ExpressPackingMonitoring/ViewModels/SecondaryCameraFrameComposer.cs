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
            double topRatio = Config.AppConfig.UnsetOverlayPosition,
            bool allowUpscale = true)
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
                topRatio,
                allowUpscale);
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
            // 圆角裁剪：四个角保留主画面自己的内容，不能把小窗的方角贴上去。
            int cornerRadius = ResolveCornerRadius(rect.Width, rect.Height);
            using Mat roundedMask = BuildRoundedMask(rect.Width, rect.Height, cornerRadius);
            bgr.CopyTo(region, roundedMask);

            // 边框画在画面内侧，不会越出主帧边界；圆角与识别框对应，不要生硬的方角。
            var borderRect = new Rect(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2);
            DrawRoundedBorder(
                frame,
                borderRect,
                new Scalar(255, 255, 255),
                BorderThickness,
                cornerRadius);
            return true;
        }

        /// <summary>圆角半径：按小窗短边取比例，保证和识别框的圆角观感一致，不随分辨率跑偏。</summary>
        private static int ResolveCornerRadius(int width, int height) =>
            Math.Clamp((int)Math.Round(Math.Min(width, height) * 0.02), 4, 48);

        /// <summary>
        /// 圆角矩形蒙版：中间两个十字交叠的矩形加四个实心圆，合成一块圆角形状。
        /// 用来把小窗内容按圆角贴进目标区域，四角留出主画面原本的内容。
        /// </summary>
        private static Mat BuildRoundedMask(int width, int height, int radius)
        {
            var mask = new Mat(height, width, MatType.CV_8UC1, Scalar.Black);
            int r = Math.Min(radius, Math.Min(width, height) / 2);
            if (r <= 0)
            {
                mask.SetTo(Scalar.White);
                return mask;
            }

            Cv2.Rectangle(mask, new Rect(r, 0, width - (2 * r), height), Scalar.White, -1, LineTypes.AntiAlias);
            Cv2.Rectangle(mask, new Rect(0, r, width, height - (2 * r)), Scalar.White, -1, LineTypes.AntiAlias);
            Cv2.Circle(mask, new Point(r, r), r, Scalar.White, -1, LineTypes.AntiAlias);
            Cv2.Circle(mask, new Point(width - r, r), r, Scalar.White, -1, LineTypes.AntiAlias);
            Cv2.Circle(mask, new Point(r, height - r), r, Scalar.White, -1, LineTypes.AntiAlias);
            Cv2.Circle(mask, new Point(width - r, height - r), r, Scalar.White, -1, LineTypes.AntiAlias);
            return mask;
        }

        /// <summary>
        /// 画一圈圆角边框：OpenCV 没有现成的圆角矩形，用四段直边加四个 90° 圆弧拼出来。
        /// 识别框是圆角矩形，小窗边框跟着圆角，两者才对得上。
        /// </summary>
        private static void DrawRoundedBorder(Mat frame, Rect rect, Scalar color, int thickness, int radius)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
                return;

            int r = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2);
            if (r <= 0)
            {
                Cv2.Rectangle(frame, rect, color, thickness);
                return;
            }

            int left = rect.Left;
            int top = rect.Top;
            int right = rect.Right;
            int bottom = rect.Bottom;

            Cv2.Line(frame, new Point(left + r, top), new Point(right - r, top), color, thickness, LineTypes.AntiAlias);
            Cv2.Line(frame, new Point(left + r, bottom), new Point(right - r, bottom), color, thickness, LineTypes.AntiAlias);
            Cv2.Line(frame, new Point(left, top + r), new Point(left, bottom - r), color, thickness, LineTypes.AntiAlias);
            Cv2.Line(frame, new Point(right, top + r), new Point(right, bottom - r), color, thickness, LineTypes.AntiAlias);

            Cv2.Ellipse(frame, new Point(left + r, top + r), new Size(r, r), 0, 180, 270, color, thickness, LineTypes.AntiAlias);
            Cv2.Ellipse(frame, new Point(right - r, top + r), new Size(r, r), 0, 270, 360, color, thickness, LineTypes.AntiAlias);
            Cv2.Ellipse(frame, new Point(right - r, bottom - r), new Size(r, r), 0, 0, 90, color, thickness, LineTypes.AntiAlias);
            Cv2.Ellipse(frame, new Point(left + r, bottom - r), new Size(r, r), 0, 90, 180, color, thickness, LineTypes.AntiAlias);
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
