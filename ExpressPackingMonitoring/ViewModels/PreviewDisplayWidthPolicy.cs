namespace ExpressPackingMonitoring.ViewModels
{
    /// <summary>
    /// 预览发布的显示宽度计算。主界面预览和悬浮小窗可能同时在消费同一个 VideoFrame，
    /// 只按最大的那个宽度发布：宁可发大一点让 WPF 缩小，也不能发得比控件小而被插值放大。
    ///
    /// 宽度还会决定 GPU 转换器的重建（见 GpuPreviewResizer）：把无效值统一折算成 0，
    /// 避免"窗口还没布局"这种瞬时状态被当成一个很小的发布宽度。
    /// </summary>
    internal static class PreviewDisplayWidthPolicy
    {
        /// <summary>把控件上报的宽度（设备像素）折算成发布用的整数；无效、未量到或不可见一律记 0。</summary>
        internal static int NormalizeWidth(double width) =>
            double.IsFinite(width) && width > 0 ? (int)Math.Round(width) : 0;

        /// <summary>取所有可见消费方里最大的显示宽度作为发布尺寸。</summary>
        internal static int ResolvePublishWidth(int mainWidth, int floatingWidth) =>
            Math.Max(mainWidth, floatingWidth);
    }
}
