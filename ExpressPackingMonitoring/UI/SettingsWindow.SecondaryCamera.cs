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
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// 副摄下拉列表：**直接复用主摄下拉那份设备清单**（无 + 主摄没占用的那些设备 + 网络摄像头），
        /// 不再自己枚举一遍设备。主摄一换设备，主摄下拉的选中项变了 → 这里重新算一次即可。
        /// </summary>
        public IReadOnlyList<CameraDeviceChoice> SecondaryCameraChoices =>
            BuildChoicesFromMainCameraList();

        private List<CameraDeviceChoice> BuildChoicesFromMainCameraList()
        {
            var choices = new List<CameraDeviceChoice>
            {
                new("无", AppConfig.SecondaryCameraSourceNone, "", -1)
            };

            string mainMoniker = Config?.CameraMonikerString ?? "";
            if (CameraComboBox?.ItemsSource is System.Collections.IEnumerable items)
            {
                foreach (object? item in items)
                {
                    if (item is not CameraInfo camera)
                        continue;

                    // 主摄清单末尾的"网络摄像头（手动地址）"只是主摄的入口，副摄自己带一项，不重复列。
                    if (string.Equals(camera.Moniker, "network:", StringComparison.Ordinal))
                        continue;

                    // 主摄已经占用的那台不能再被副摄选（同一台设备不能被两路同时打开）。
                    if (!string.IsNullOrEmpty(mainMoniker)
                        && string.Equals(camera.Moniker, mainMoniker, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    choices.Add(new CameraDeviceChoice(camera.Name, "usb", camera.Moniker, camera.Index));
                }
            }

            choices.Add(new CameraDeviceChoice("网络摄像头", "network", "", -1));
            return choices;
        }

        public CameraDeviceChoice? SelectedSecondaryCameraChoice
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

        /// <summary>
        /// 下拉第一次显示时按当前设备档位填一次（在 XAML 挂 Loaded，避免动冻结文件）；
        /// 同时盯住主摄下拉——主摄换了设备，副摄的候选列表要立刻重算。
        /// </summary>
        internal void SecondaryCameraFormats_Loaded(object sender, System.Windows.RoutedEventArgs e)
        {
            if (CameraComboBox != null)
            {
                CameraComboBox.SelectionChanged -= MainCameraSelectionChangedForSecondary;
                CameraComboBox.SelectionChanged += MainCameraSelectionChangedForSecondary;
            }

            LoadSecondaryCameraFormats();
        }

        /// <summary>
        /// 主摄换了设备：副摄候选列表重新枚举（同一台设备不能被两路同时打开，
        /// 主摄刚占用的那台要从副摄列表里去掉、腾出来的那台要重新出现）。
        /// </summary>
        private void MainCameraSelectionChangedForSecondary(
            object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // 主摄占用了副摄当前选的那台设备：副摄自动回到"无"，不允许两路选同一台
            if (Config is { } config
                && string.Equals(config.SecondaryCameraSourceKind, "usb", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(config.SecondaryCameraMonikerString)
                && string.Equals(
                    config.SecondaryCameraMonikerString,
                    config.CameraMonikerString,
                    StringComparison.Ordinal))
            {
                SelectedSecondaryCameraChoice = SecondaryCameraChoices
                    .FirstOrDefault(choice => choice.Kind == AppConfig.SecondaryCameraSourceNone);
            }

            Raise(nameof(SecondaryCameraChoices));
            Raise(nameof(SelectedSecondaryCameraChoice));
            LoadSecondaryCameraFormats();
        }

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
