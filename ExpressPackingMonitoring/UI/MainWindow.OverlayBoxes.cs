using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.ViewModels;

namespace ExpressPackingMonitoring.UI
{
    /// <summary>
    /// 主界面预览里的画中画拖动框。
    ///
    /// 每一路接了设备的副画面各有一组"框 + 右下角把手"：按住框体拖动改位置，按住把手改大小，
    /// 单击进这一路的取景编辑屏。框按通道生成，以后开放第三、第四路不用改这里。
    ///
    /// 位置和尺寸都来自 <see cref="MainViewModel.TryResolveOverlayRect"/>，与合成用的是同一套策略，
    /// 所以框住哪里、画面就画在哪里。
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>一路画中画的拖动控件。</summary>
        private sealed class OverlayBoxControls
        {
            internal required int ChannelNumber { get; init; }
            internal required Border Frame { get; init; }
            internal required Thumb Drag { get; init; }
            internal required Thumb Resize { get; init; }
        }

        private readonly Dictionary<int, OverlayBoxControls> _overlayBoxControls = new();

        /// <summary>把每一路画中画的拖动框摆到它当前所在的位置；没有画面的或没接的那一路收起来。</summary>
        private void UpdateOverlayBoxes(MainViewModel vm)
        {
            var alive = new HashSet<int>();
            foreach (int channelNumber in vm.VisibleOverlayChannelNumbers)
            {
                alive.Add(channelNumber);
                if (!_overlayBoxControls.TryGetValue(channelNumber, out OverlayBoxControls? controls))
                {
                    controls = CreateOverlayBoxControls(channelNumber);
                    _overlayBoxControls[channelNumber] = controls;
                }

                UpdateOverlayBox(vm, controls);
            }

            foreach (KeyValuePair<int, OverlayBoxControls> entry in _overlayBoxControls)
            {
                if (alive.Contains(entry.Key))
                    continue;

                entry.Value.Frame.Visibility = Visibility.Collapsed;
                entry.Value.Drag.Visibility = Visibility.Collapsed;
                entry.Value.Resize.Visibility = Visibility.Collapsed;
            }
        }

        private OverlayBoxControls CreateOverlayBoxControls(int channelNumber)
        {
            var frame = new Border
            {
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(6),
                BorderBrush = FindBrush("TransparentBrush"),
                Background = FindBrush("TransparentBrush"),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };

            // 命中区域用透明 Thumb：框体本身不画边框（压在画面上会被当成"画面被框住了"），
            // 鼠标移上来才由 frame 显示一圈描边，此时才看得出可以拖。
            var drag = new Thumb
            {
                Tag = channelNumber,
                Cursor = Cursors.SizeAll,
                Background = FindBrush("TransparentBrush"),
                Visibility = Visibility.Collapsed,
                ToolTip = "按住拖动可调整副画面位置"
            };
            drag.DragDelta += OverlayBox_DragDelta;
            drag.DragCompleted += OverlayBox_DragCompleted;
            drag.MouseEnter += OverlayBox_MouseEnter;
            drag.MouseLeave += OverlayBox_MouseLeave;

            var resize = new Thumb
            {
                Tag = channelNumber,
                Width = 16,
                Height = 16,
                Cursor = Cursors.SizeNWSE,
                Visibility = Visibility.Collapsed,
                ToolTip = "按住拖动可调整副画面大小",
                // 与识别框四角把手同一套外观：蓝色圆角小方块
                Style = TryFindResource("CameraGuideHandleStyle") as Style
            };
            resize.DragDelta += OverlayResize_DragDelta;
            resize.DragCompleted += OverlayResize_DragCompleted;

            SecondaryOverlayBoxLayer.Children.Add(frame);
            SecondaryOverlayBoxLayer.Children.Add(drag);
            SecondaryOverlayBoxLayer.Children.Add(resize);
            return new OverlayBoxControls
            {
                ChannelNumber = channelNumber,
                Frame = frame,
                Drag = drag,
                Resize = resize
            };
        }

        private void UpdateOverlayBox(MainViewModel vm, OverlayBoxControls controls)
        {
            controls.Frame.Visibility = Visibility.Collapsed;
            controls.Resize.Visibility = Visibility.Collapsed;

            // 取景编辑态下预览显示的是那一路的整幅画面，画中画的位置/大小框这时候没有意义。
            if (vm.IsEditingOverlayPreview)
            {
                controls.Drag.Visibility = Visibility.Collapsed;
                return;
            }

            if (vm.VideoFrame is not { PixelWidth: > 0, PixelHeight: > 0 } frame)
            {
                controls.Drag.Visibility = Visibility.Collapsed;
                return;
            }

            Rect videoRect = CameraBarcodeGuideLayout.GetVideoRect(
                frame.PixelWidth,
                frame.PixelHeight,
                VideoImage.ActualWidth,
                VideoImage.ActualHeight);
            if (videoRect.IsEmpty || videoRect.Width <= 0 || videoRect.Height <= 0)
            {
                controls.Drag.Visibility = Visibility.Collapsed;
                return;
            }

            if (!vm.TryResolveOverlayRect(
                    controls.ChannelNumber,
                    frame.PixelWidth,
                    frame.PixelHeight,
                    out CameraOverlayRect rect))
            {
                controls.Drag.Visibility = Visibility.Collapsed;
                return;
            }

            // VideoImage 在父容器里不一定从 (0,0) 开始：画面按 Uniform 居中摆放，
            // 四周留出的黑边同样占父容器的坐标。拖动框挂在同一个容器上，
            // 坐标必须换算过去，否则整块框会比画面偏出这段黑边。
            UIElement overlayHost = SecondaryOverlayBoxLayer;
            Point videoOrigin = VideoImage.TranslatePoint(
                new Point(videoRect.X, videoRect.Y),
                overlayHost);

            // 帧坐标 → 预览控件坐标（Uniform 缩放，两边黑边已由 videoRect 扣掉）。
            double scale = videoRect.Width / frame.PixelWidth;
            double left = videoOrigin.X + (rect.X * scale);
            double top = videoOrigin.Y + (rect.Y * scale);
            double width = rect.Width * scale;
            double height = rect.Height * scale;

            Place(controls.Frame, left, top, width, height);
            Place(controls.Drag, left, top, width, height);
            Place(
                controls.Resize,
                left + width - (controls.Resize.Width / 2),
                top + height - (controls.Resize.Height / 2),
                controls.Resize.Width,
                controls.Resize.Height);

            controls.Frame.Visibility = Visibility.Visible;
            controls.Drag.Visibility = Visibility.Visible;
            controls.Resize.Visibility = Visibility.Visible;
        }

        /// <summary>取主题画刷；颜色只允许定义在 ColorTokens 里，这里一律按 key 取。</summary>
        private Brush FindBrush(string key) => (Brush)FindResource(key);

        private static void Place(FrameworkElement element, double left, double top, double width, double height)
        {
            Canvas.SetLeft(element, left);
            Canvas.SetTop(element, top);
            if (width > 0)
                element.Width = width;
            if (height > 0)
                element.Height = height;
        }

        private void OverlayBox_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (sender is not Thumb { Tag: int channelNumber }
                || DataContext is not MainViewModel vm
                || vm.VideoFrame is not { PixelWidth: > 0, PixelHeight: > 0 } frame)
            {
                return;
            }

            Rect videoRect = CameraBarcodeGuideLayout.GetVideoRect(
                frame.PixelWidth,
                frame.PixelHeight,
                VideoImage.ActualWidth,
                VideoImage.ActualHeight);
            if (videoRect.IsEmpty || videoRect.Width <= 0)
                return;
            if (!vm.TryResolveOverlayRect(channelNumber, frame.PixelWidth, frame.PixelHeight, out CameraOverlayRect current))
                return;

            // 控件像素增量 → 帧像素增量，再交给 ViewModel 夹紧落位。
            double scale = videoRect.Width / frame.PixelWidth;
            double frameX = current.X + (e.HorizontalChange / scale);
            double frameY = current.Y + (e.VerticalChange / scale);

            vm.SetOverlayPosition(channelNumber, frameX, frameY, frame.PixelWidth, frame.PixelHeight);
            UpdateOverlayBoxes(vm);
        }

        private void OverlayBox_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (sender is not Thumb { Tag: int channelNumber } || DataContext is not MainViewModel vm)
                return;

            // 没有实际位移 = 单击这一路画中画：进入它的取景编辑（就像点图片进裁剪）。
            if (Math.Abs(e.HorizontalChange) < 2 && Math.Abs(e.VerticalChange) < 2)
            {
                vm.EnterOverlayPreviewEdit(channelNumber);
                UpdateOverlayBoxes(vm);
                UpdateCameraBarcodeGuide(vm);
                return;
            }

            vm.SaveOverlayPosition(channelNumber);
        }

        private void OverlayResize_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (sender is not Thumb { Tag: int channelNumber }
                || DataContext is not MainViewModel vm
                || vm.VideoFrame is not { PixelWidth: > 0, PixelHeight: > 0 } frame)
            {
                return;
            }

            Rect videoRect = CameraBarcodeGuideLayout.GetVideoRect(
                frame.PixelWidth,
                frame.PixelHeight,
                VideoImage.ActualWidth,
                VideoImage.ActualHeight);
            if (videoRect.IsEmpty || videoRect.Width <= 0)
                return;
            if (!vm.TryResolveOverlayRect(channelNumber, frame.PixelWidth, frame.PixelHeight, out CameraOverlayRect current))
                return;

            double scale = videoRect.Width / frame.PixelWidth;
            double targetWidthPixels = current.Width + (e.HorizontalChange / scale);
            vm.SetOverlayWidth(channelNumber, targetWidthPixels / frame.PixelWidth);
            UpdateOverlayBoxes(vm);
        }

        private void OverlayResize_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (sender is Thumb { Tag: int channelNumber } && DataContext is MainViewModel vm)
                vm.SaveOverlayWidth(channelNumber);
        }

        private void OverlayBox_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Thumb { Tag: int channelNumber }
                && _overlayBoxControls.TryGetValue(channelNumber, out OverlayBoxControls? controls))
            {
                controls.Frame.BorderBrush = TryFindResource("AccentBlue") as Brush;
            }
        }

        private void OverlayBox_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is Thumb { Tag: int channelNumber }
                && _overlayBoxControls.TryGetValue(channelNumber, out OverlayBoxControls? controls))
            {
                controls.Frame.BorderBrush = TryFindResource("TransparentBrush") as Brush;
            }
        }
    }
}
