using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.ViewModels;
using OpenCvSharp;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 水印文字渲染的回归。OpenCV 自带的 Hershey 字体只认 ASCII，
/// 直接把中文丢给 Cv2.PutText 会画成一串问号（现场反馈：水印里的“快递单”变成“??????”）。
/// </summary>
public sealed class WatermarkRenderingTests
{
    /// <summary>量一下某段文字实际画出来占的像素宽度，用来判断是不是真字形。</summary>
    private static int DrawnWidth(string text)
    {
        using var frame = new Mat(160, 1200, MatType.CV_8UC3, Scalar.Black);
        FrameTextRenderer.DrawRightAlignedLine(frame, text, 48f, 20f, 20f);

        using var gray = new Mat();
        Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        using var points = new Mat();
        Cv2.FindNonZero(gray, points);
        if (points.Empty())
            return 0;

        return Cv2.BoundingRect(points).Width;
    }

    /// <summary>
    /// 中文必须按全角字形画出来。若退回 Hershey 字体，中文会变成“?”，
    /// 宽度就和同样数量的问号一样窄；真正的中文字形明显更宽。
    /// </summary>
    [Fact]
    public void ChineseLabelRendersAsFullWidthGlyphs()
    {
        int chinese = DrawnWidth("快递单");
        int questionMarks = DrawnWidth("???");

        Assert.True(chinese > 0, "中文没有画出任何像素");
        Assert.True(
            chinese > questionMarks * 1.2,
            $"中文疑似被画成了问号：中文={chinese}px，???={questionMarks}px");
    }

    /// <summary>带中文标签的水印必须真的改动画面，避免渲染异常被吞掉后静默不出水印。</summary>
    [Fact]
    public void ApplyWatermarkToFrame_WithChineseLabel_DrawsPixels()
    {
        using var frame = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        using var before = frame.Clone();

        MainViewModel.ApplyWatermarkToFrame(
            frame,
            new DateTimeOffset(2026, 10, 3, 16, 53, 20, TimeSpan.FromHours(8)),
            "YT0713241449592",
            new[] { "扩展行：买家留言 尽快发货" });

        using var difference = new Mat();
        Cv2.Absdiff(before, frame, difference);
        Assert.True(Cv2.CountNonZero(difference.Reshape(1)) > 0, "水印没有改动画面");
    }

    /// <summary>水印整体往右下排布：第一行时间戳、第二行更大的快递单号，不能互相压住。</summary>
    [Fact]
    public void ApplyWatermarkToFrame_KeepsTimestampAboveWaybillLine()
    {
        using var frame = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);

        MainViewModel.ApplyWatermarkToFrame(
            frame,
            new DateTimeOffset(2026, 10, 3, 16, 53, 20, TimeSpan.FromHours(8)),
            "YT0713241449592",
            Array.Empty<string>());

        using var gray = new Mat();
        Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);

        // 逐行统计非空像素，找出两段文字的水平区间（时间戳那行更窄、单号那行更宽）
        int firstRow = -1;
        int lastRow = -1;
        for (int y = 0; y < gray.Height; y++)
        {
            using var row = gray.Row(y);
            if (Cv2.CountNonZero(row) <= 0)
                continue;

            if (firstRow < 0)
                firstRow = y;
            lastRow = y;
        }

        Assert.True(firstRow >= 0, "水印没有画出任何像素");
        Assert.True(lastRow > firstRow, "水印只占了一行，快递单号那行可能没画出来");
    }
}
