using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using AForge.Video.DirectShow;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.UI
{
    /// <summary>
    /// 设置页里副摄像头那几项要绑定的数据。
    ///
    /// 设置页的 DataContext 是窗口自己（<c>this.DataContext = this</c>），所以这些属性必须挂在窗口上，
    /// 而不是 MainViewModel 上 —— 挂在 ViewModel 上只会静默绑不上（下拉空白、显隐失效）。
    /// 放在独立分部文件里是为了不给冻结的 SettingsWindow.xaml.cs 增加行数。
    /// </summary>
    public partial class SettingsWindow : INotifyPropertyChanged
    {
        /// <summary>副摄下拉的一项：无 / 本机某台摄像头 / 网络摄像头。</summary>
        public sealed record SecondaryCameraChoice(string Name, string Kind, string Moniker, int Index);

        public event PropertyChangedEventHandler? PropertyChanged;

        private List<SecondaryCameraChoice>? _secondaryCameraChoices;

        /// <summary>
        /// 副摄下拉列表：无 + 本机真实存在的摄像头（剔除主摄已经选走的那台）+ 网络摄像头。
        /// </summary>
        public IReadOnlyList<SecondaryCameraChoice> SecondaryCameraChoices =>
            _secondaryCameraChoices ??= BuildSecondaryCameraChoices();

        private List<SecondaryCameraChoice> BuildSecondaryCameraChoices()
        {
            var choices = new List<SecondaryCameraChoice>
            {
                new("无", AppConfig.SecondaryCameraSourceNone, "", -1)
            };

            try
            {
                var devices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
                string mainMoniker = Config?.CameraMonikerString ?? "";
                for (int i = 0; i < devices.Count; i++)
                {
                    if (!string.IsNullOrEmpty(mainMoniker)
                        && string.Equals(devices[i].MonikerString, mainMoniker, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    choices.Add(new SecondaryCameraChoice(devices[i].Name, "usb", devices[i].MonikerString, i));
                }
            }
            catch
            {
                // 枚举失败时至少还有"无"和"网络摄像头"，不影响窗口打开。
            }

            choices.Add(new SecondaryCameraChoice("网络摄像头", "network", "", -1));
            return choices;
        }

        public SecondaryCameraChoice? SelectedSecondaryCameraChoice
        {
            get
            {
                if (Config is not { } config)
                    return SecondaryCameraChoices.FirstOrDefault();

                return SecondaryCameraChoices.FirstOrDefault(choice =>
                    string.Equals(choice.Kind, NormalizedKind(config), StringComparison.Ordinal)
                    && (choice.Kind != "usb"
                        || string.Equals(choice.Moniker, config.SecondaryCameraMonikerString, StringComparison.Ordinal)))
                    ?? SecondaryCameraChoices.FirstOrDefault();
            }
            set
            {
                if (value == null || Config is not { } config)
                    return;

                config.SecondaryCameraSourceKind = value.Kind;
                config.SecondaryCameraIndex = value.Index;
                config.SecondaryCameraMonikerString = value.Kind == "usb" ? value.Moniker : "";

                Raise(nameof(SelectedSecondaryCameraChoice));
                Raise(nameof(IsSecondaryCameraConfigured));
                Raise(nameof(IsSecondaryNetworkCameraSelected));
                // 换了设备就重新枚举它支持的采集档位（枚举走和主摄同一个 CameraFormatCatalog）
                LoadSecondaryCameraFormats();
            }
        }

        /// <summary>下拉第一次显示时按当前设备档位填一次（在 XAML 挂 Loaded，避免动冻结文件）。</summary>
        internal void SecondaryCameraFormats_Loaded(object sender, System.Windows.RoutedEventArgs e) =>
            LoadSecondaryCameraFormats();

        /// <summary>选了"无"之外的值就显示其余副摄选项。</summary>
        public bool IsSecondaryCameraConfigured =>
            Config is { } config
            && !string.Equals(NormalizedKind(config), AppConfig.SecondaryCameraSourceNone, StringComparison.Ordinal);

        /// <summary>
        /// 副摄分辨率/帧率下拉：档位枚举走 <see cref="CameraFormatCatalog"/>，与主摄（以及以后的第三、第四路）
        /// 完全同一套代码；这里只负责把结果套到自己的下拉和配置上。
        /// 网络摄像头没有本地设备档位，直接用兜底档位。
        /// </summary>
        internal void LoadSecondaryCameraFormats()
        {
            if (SecondaryResolutionComboBox == null || SecondaryFpsComboBox == null)
                return;

            bool isLocalDevice = Config is { } current
                && string.Equals(NormalizedKind(current), "usb", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(current.SecondaryCameraMonikerString);
            CameraFormatOptions formats = isLocalDevice
                ? CameraFormatCatalog.Enumerate(Config!.SecondaryCameraMonikerString)
                : CameraFormatCatalog.FromCapabilities([]);
            IReadOnlyList<CameraResolutionOption> resolutions = formats.Resolutions;
            IReadOnlyList<int> fpsList = formats.FpsValues;

            SecondaryResolutionComboBox.ItemsSource = resolutions;
            (int targetWidth, int targetHeight) = AppConfig.ResolveSecondaryFrameSize(
                Config?.SecondaryResolutionPreset,
                Config?.SecondaryFrameWidth ?? 0,
                Config?.SecondaryFrameHeight ?? 0);
            SecondaryResolutionComboBox.SelectedItem =
                resolutions.FirstOrDefault(r => r.Width == targetWidth && r.Height == targetHeight)
                ?? resolutions.FirstOrDefault();

            var fpsItems = fpsList
                .Select(f => new System.Windows.Controls.ComboBoxItem { Content = $"{f} FPS", Tag = f })
                .ToList();
            SecondaryFpsComboBox.ItemsSource = fpsItems;
            int currentFps = Config?.SecondaryFrameFps > 0
                ? Config!.SecondaryFrameFps
                : AppConfig.DefaultSecondaryFrameFps;
            SecondaryFpsComboBox.SelectedItem =
                fpsItems.FirstOrDefault(i => i.Tag is int fps && fps == currentFps)
                ?? fpsItems.FirstOrDefault();
        }

        /// <summary>保存时把副摄分辨率/帧率下拉的选择写回配置；预设字段按实际尺寸回填，兼容旧口径。</summary>
        internal void ApplySecondaryCameraFormatsToConfig()
        {
            if (Config is not { } config)
                return;

            if (SecondaryResolutionComboBox?.SelectedItem is CameraResolutionOption resolution)
            {
                config.SecondaryFrameWidth = resolution.Width;
                config.SecondaryFrameHeight = resolution.Height;
                config.SecondaryResolutionPreset = AppConfig.PresetForSize(resolution.Width, resolution.Height);
            }

            if (SecondaryFpsComboBox?.SelectedItem is System.Windows.Controls.ComboBoxItem fpsItem
                && fpsItem.Tag is int fps
                && fps > 0)
            {
                config.SecondaryFrameFps = fps;
            }
        }

        /// <summary>选了"网络摄像头"才显示地址输入。</summary>
        public bool IsSecondaryNetworkCameraSelected =>
            Config is { } config
            && string.Equals(NormalizedKind(config), "network", StringComparison.Ordinal);

        private static string NormalizedKind(AppConfig config) =>
            AppConfig.NormalizeSecondaryCameraSourceKind(
                config.SecondaryCameraSourceKind,
                config.SecondaryNetworkCameraUrl);

        private void Raise(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
