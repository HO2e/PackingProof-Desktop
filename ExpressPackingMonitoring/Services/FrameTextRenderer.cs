using System.Drawing;
using System.Drawing.Drawing2D;
using OpenCvSharp;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 在 OpenCV 帧上画水印文字。
///
/// OpenCV 自带的 Hershey 字体只认 ASCII，中文一律被画成问号
/// （现场反馈：水印里的“快递单”显示成“??????”），所以这里改用 GDI+
/// 把文字直接画进帧里，中英文都能正常显示；字体优先用界面同款微软雅黑。
///
/// 直接包住 Mat 的内存来画，省掉每帧一次整帧拷贝——水印在录制时是逐帧绘制的。
/// </summary>
internal static class FrameTextRenderer
{
    /// <summary>描边线宽相对字号的比例，保证白字在亮画面上也看得清</summary>
    private const float OutlineWidthRatio = 0.14f;

    private static readonly Lazy<FontFamily> WatermarkFontFamily = new(ResolveFontFamily);

    private static FontFamily ResolveFontFamily()
    {
        foreach (string name in new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Segoe UI" })
        {
            try
            {
                return new FontFamily(name);
            }
            catch
            {
                // 这台机器上没有该字体，接着试下一个
            }
        }

        return FontFamily.GenericSansSerif;
    }

    /// <summary>
    /// 画一行右对齐的水印文字（黑色描边 + 白色填充），返回这一行占用的高度。
    /// <paramref name="topY"/> 是这一行文字的上边缘，方便调用方逐行向下排。
    /// </summary>
    internal static float DrawRightAlignedLine(
        Mat frame,
        string text,
        float emSize,
        float rightMargin,
        float topY)
    {
        if (string.IsNullOrWhiteSpace(text) || frame == null || frame.IsDisposed || frame.Empty())
            return 0;

        try
        {
            return DrawWithGdiPlus(frame, text, emSize, rightMargin, topY);
        }
        catch
        {
            // GDI+ 不可用时退回 OpenCV 自带字体：中文会变成问号，但时间戳等信息还在
            return DrawWithOpenCv(frame, text, emSize, rightMargin, topY);
        }
    }

    private static float DrawWithGdiPlus(Mat frame, string text, float emSize, float rightMargin, float topY)
    {
        // 24bppRgb 的字节序与 OpenCV 的 BGR 一致，画上去颜色不会串。
        using var bitmap = new Bitmap(
            frame.Width,
            frame.Height,
            (int)frame.Step(),
            System.Drawing.Imaging.PixelFormat.Format24bppRgb,
            frame.Data);

        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using var path = new GraphicsPath();
        path.AddString(
            text,
            WatermarkFontFamily.Value,
            (int)FontStyle.Bold,
            emSize,
            PointF.Empty,
            StringFormat.GenericTypographic);

        RectangleF bounds = path.GetBounds();
        float outline = Math.Max(1.2f, emSize * OutlineWidthRatio);
        float x = frame.Width - rightMargin - bounds.Width - bounds.X;
        float y = topY - bounds.Y;

        using (var translate = new Matrix())
        {
            translate.Translate(x, y);
            path.Transform(translate);
        }

        using var outlinePen = new Pen(Color.Black, outline) { LineJoin = LineJoin.Round };
        graphics.DrawPath(outlinePen, path);
        graphics.FillPath(Brushes.White, path);
        graphics.Flush();

        return bounds.Height + outline;
    }

    private static float DrawWithOpenCv(Mat frame, string text, float emSize, float rightMargin, float topY)
    {
        double fontScale = Math.Max(0.4, emSize / 30.0);
        int thickness = fontScale >= 0.8 ? 2 : 1;
        var size = Cv2.GetTextSize(text, HersheyFonts.HersheySimplex, fontScale, thickness, out _);
        int x = Math.Max(4, frame.Width - size.Width - (int)rightMargin);
        int y = (int)(topY + size.Height);
        var point = new OpenCvSharp.Point(x, y);

        Cv2.PutText(
            frame,
            text,
            point,
            HersheyFonts.HersheySimplex,
            fontScale,
            new Scalar(0, 0, 0),
            thickness + 2,
            LineTypes.AntiAlias);
        Cv2.PutText(
            frame,
            text,
            point,
            HersheyFonts.HersheySimplex,
            fontScale,
            new Scalar(255, 255, 255),
            thickness,
            LineTypes.AntiAlias);

        return size.Height;
    }
}
