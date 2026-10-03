#nullable disable
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Logging;
using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.ViewModels
{
    public partial class MainViewModel
    {
        /// <summary>
        /// 手动指定特写位置模式：智能特写开着、并且用户勾了"手动指定特写位置"时才生效。
        /// 生效时主界面出现可拖动的十字标记，特写固定放大到标记处。
        /// </summary>
        public bool IsSmartZoomCustomPositionMode =>
            Config is { EnableSmartZoom: true, EnableSmartZoomCustomPosition: true };

        /// <summary>拖动特写十字标记期间挂起特写：预览回到整帧，拖动坐标才和画面一一对应</summary>
        private volatile bool _isSmartZoomPositionDragActive;

        /// <summary>是否正在拖动特写十字标记。拖动期间十字必须保持可见且按整帧落位</summary>
        internal bool IsSmartZoomPositionAdjusting => _isSmartZoomPositionDragActive;

        /// <summary>
        /// 解析本次特写要用的目标几何。手动定位开启时用固定中心合成几何，
        /// 否则沿用识别到的面单位置（原有行为）。
        /// </summary>
        internal CameraBarcodeGeometry ResolveSmartZoomTargetGeometry(int frameWidth, int frameHeight)
        {
            if (!IsSmartZoomCustomPositionMode || Config == null)
                return _lastBarcodeGeometry;

            (double ratioX, double ratioY) = SmartZoomCustomPositionPolicy.ResolveCenter(
                Config.SmartZoomCustomCenterX,
                Config.SmartZoomCustomCenterY);
            return SmartZoomCustomPositionPolicy.CreateCenterGeometry(frameWidth, frameHeight, ratioX, ratioY);
        }

        /// <summary>当前手动特写中心（归一化）。未指定时给画面中心，供十字标记落位</summary>
        internal (double RatioX, double RatioY) CurrentSmartZoomCustomCenter =>
            SmartZoomCustomPositionPolicy.ResolveCenter(
                Config?.SmartZoomCustomCenterX ?? AppConfig.UnsetOverlayPosition,
                Config?.SmartZoomCustomCenterY ?? AppConfig.UnsetOverlayPosition);

        /// <summary>
        /// 主界面拖动十字标记后写回配置。拖动过程即时生效但不落盘，松手时才保存，
        /// 避免鼠标每移动一次就写一遍配置文件。
        /// </summary>
        internal void SetSmartZoomCustomCenter(double ratioX, double ratioY, bool persist)
        {
            if (Config == null)
                return;

            Config.SmartZoomCustomCenterX = Math.Clamp(ratioX, 0.0, 1.0);
            Config.SmartZoomCustomCenterY = Math.Clamp(ratioY, 0.0, 1.0);

            if (!persist)
                return;

            SaveConfig();
            RuntimeLog.Info(
                "SmartZoom",
                $"Custom close-up center saved x={Config.SmartZoomCustomCenterX:F3} y={Config.SmartZoomCustomCenterY:F3}");
        }

        internal void BeginSmartZoomPositionAdjustment() => _isSmartZoomPositionDragActive = true;

        internal void EndSmartZoomPositionAdjustment() => _isSmartZoomPositionDragActive = false;
    }
}
