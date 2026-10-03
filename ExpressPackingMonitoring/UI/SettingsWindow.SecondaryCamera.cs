using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.UI
{
    /// <summary>
    /// 设置页里摄像头相关的绑定数据。
    ///
    /// 设置页的 DataContext 是窗口自己（<c>this.DataContext = this</c>），所以这些属性必须挂在窗口上；
    /// 放在独立分部文件里是为了不给冻结的 SettingsWindow.xaml.cs 增加行数。
    ///
    /// 选设备的规则（主摄/副摄互相排除）统一走 <see cref="CameraDeviceSelectionPolicy"/>，
    /// 这里只负责"抓一份完整清单 + 把投影结果套回两个下拉"。
    /// </summary>
    public partial class SettingsWindow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private bool _syncingCameraChoices;
        private List<CameraDeviceChoice>? _allCameraChoices;

        /// <summary>
        /// 摄像头完整清单（含主摄清单末尾的"网络摄像头（手动地址）"伪项）。
        ///
        /// 注意：主摄的设备清单是**异步**填进下拉的，第一次读到时很可能还是空的；
        /// 所以只缓存"非空"的结果，读到空时不能缓存，否则副摄下拉会一直是空的。
        /// </summary>
        private List<CameraDeviceChoice> AllCameraChoices
        {
            get
            {
                List<CameraDeviceChoice> current = (CameraComboBox?.ItemsSource as System.Collections.IEnumerable)?
                    .OfType<CameraInfo>()
                    .Select(ToChoice)
                    .ToList() ?? new List<CameraDeviceChoice>();
                if (current.Count > 0)
                    _allCameraChoices = current;

                return _allCameraChoices ?? current;
            }
        }

        private static CameraDeviceChoice ToChoice(CameraInfo camera) =>
            new(
                camera.Name,
                string.Equals(camera.Moniker, "network:", StringComparison.Ordinal) ? "network" : "usb",
                camera.Moniker,
                camera.Index);

        private static CameraInfo ToCameraInfo(CameraDeviceChoice choice) =>
            new() { Name = choice.Name, Moniker = choice.Moniker, Index = choice.Index };

        private string MainMoniker =>
            Config is { } config && string.Equals(config.CameraSourceKind, "usb", StringComparison.Ordinal)
                ? config.CameraMonikerString
                : "";

        private string SecondaryMoniker =>
            Config is { } config && string.Equals(config.SecondaryCameraSourceKind, "usb", StringComparison.Ordinal)
                ? config.SecondaryCameraMonikerString
                : "";

        /// <summary>
        /// 副摄下拉：无 + （完整清单按"排除主摄占用的那台"投影）+ 网络摄像头。
        /// 主摄那份清单本身没有"无"、也没有副摄的"网络摄像头"入口，这里补齐；
        /// 每次取值都重算，不缓存过期副本。
        /// </summary>
        public IReadOnlyList<CameraDeviceChoice> SecondaryCameraChoices
        {
            get
            {
                var choices = new List<CameraDeviceChoice>
                {
                    new("无", AppConfig.SecondaryCameraSourceNone, "", -1)
                };

                choices.AddRange(
                    CameraDeviceSelectionPolicy
                        .Project(AllCameraChoices, SecondaryMoniker, [MainMoniker])
                        .Where(choice => choice.Kind != "network"));

                choices.Add(new CameraDeviceChoice("网络摄像头", "network", "", -1));
                return choices;
            }
        }

        /// <summary>
        /// 主摄/副摄两个下拉互相排除：任一边换了设备，两边都用新的占用关系重新投影一次。
        /// 主摄下拉仍用 <see cref="CameraInfo"/>（冻结代码按这个类型读选中项）。
        /// </summary>
        internal void SyncCameraChoices()
        {
            if (_syncingCameraChoices || CameraComboBox == null || Config is not { } config)
                return;
            if (AllCameraChoices.Count == 0)
                return;

            _syncingCameraChoices = true;
            try
            {
                List<CameraInfo> mainList = CameraDeviceSelectionPolicy
                    .Project(AllCameraChoices, MainMoniker, [SecondaryMoniker])
                    .Select(ToCameraInfo)
                    .ToList();
                CameraComboBox.ItemsSource = mainList;
                CameraComboBox.SelectedValue = config.CameraIndex;
                if (CameraComboBox.SelectedItem == null)
                    CameraComboBox.SelectedItem = mainList.FirstOrDefault();

                EnsureSecondaryChoiceStaysValid(config);

                Raise(nameof(SecondaryCameraChoices));
                Raise(nameof(SelectedSecondaryCameraChoice));
            }
            finally
            {
                _syncingCameraChoices = false;
            }
        }

        /// <summary>副摄选的那台如果被主摄占用了（或设备已消失），副摄回到"无"。</summary>
        private void EnsureSecondaryChoiceStaysValid(AppConfig config)
        {
            if (!string.Equals(config.SecondaryCameraSourceKind, "usb", StringComparison.Ordinal)
                || string.IsNullOrEmpty(config.SecondaryCameraMonikerString))
            {
                return;
            }

            // 判断本身也在公共服务里（与主摄共用同一套），这里只负责"不能用就退回无"
            if (CameraDeviceSelectionPolicy.CanKeepSelection(
                    config.SecondaryCameraMonikerString,
                    [config.CameraMonikerString],
                    AllCameraChoices))
            {
                return;
            }

            // 注意：这里不能走 SelectedSecondaryCameraChoice 的 setter —— 它带同步守卫，
            // 而本方法正是在同步过程中调用的，会被守卫直接挡掉（这就是"还能选成同一台"的来源）。
            config.SecondaryCameraSourceKind = AppConfig.SecondaryCameraSourceNone;
            config.SecondaryCameraIndex = -1;
            config.SecondaryCameraMonikerString = "";
            Raise(nameof(SelectedSecondaryCameraChoice));
            Raise(nameof(IsSecondaryCameraConfigured));
            Raise(nameof(IsSecondaryNetworkCameraSelected));
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
                if (value == null || Config is not { } config || _syncingCameraChoices)
                    return;

                config.SecondaryCameraSourceKind = value.Kind;
                config.SecondaryCameraIndex = value.Index;
                config.SecondaryCameraMonikerString = value.Kind == "usb" ? value.Moniker : "";

                SyncCameraChoices();
                LoadSecondaryCameraFormats();
                Raise(nameof(SelectedSecondaryCameraChoice));
                Raise(nameof(IsSecondaryCameraConfigured));
                Raise(nameof(IsSecondaryNetworkCameraSelected));
            }
        }

        /// <summary>
        /// 下拉第一次显示时填一次档位，并盯住主摄下拉：主摄换设备 → 两个列表都重算。
        /// （在 XAML 挂 Loaded，避免动冻结的 SettingsWindow.xaml.cs）
        /// </summary>
        internal void SecondaryCameraFormats_Loaded(object sender, RoutedEventArgs e)
        {
            if (CameraComboBox != null)
            {
                CameraComboBox.SelectionChanged -= MainCameraSelectionChangedForSecondary;
                CameraComboBox.SelectionChanged += MainCameraSelectionChangedForSecondary;
            }

            LoadSecondaryCameraFormats();
            SyncCameraChoices();
        }

        private void MainCameraSelectionChangedForSecondary(object sender, SelectionChangedEventArgs e)
        {
            SyncCameraChoices();
            LoadSecondaryCameraFormats();
        }

        /// <summary>
        /// 副摄分辨率/帧率：档位枚举走 <see cref="CameraFormatCatalog"/>，与主摄同一套；
        /// 网络摄像头/没选设备时用兜底档位。
        /// </summary>
        internal void LoadSecondaryCameraFormats()
        {
            if (SecondaryResolutionComboBox == null || SecondaryFpsComboBox == null)
                return;

            CameraFormatOptions formats = string.IsNullOrEmpty(SecondaryMoniker)
                ? CameraFormatCatalog.FromCapabilities([])
                : CameraFormatCatalog.Enumerate(SecondaryMoniker);

            SecondaryResolutionComboBox.ItemsSource = formats.Resolutions;
            (int targetWidth, int targetHeight) = AppConfig.ResolveSecondaryFrameSize(
                Config?.SecondaryResolutionPreset,
                Config?.SecondaryFrameWidth ?? 0,
                Config?.SecondaryFrameHeight ?? 0);
            SecondaryResolutionComboBox.SelectedItem =
                formats.Resolutions.FirstOrDefault(r => r.Width == targetWidth && r.Height == targetHeight)
                ?? formats.Resolutions.FirstOrDefault();

            var fpsItems = formats.FpsValues
                .Select(f => new ComboBoxItem { Content = $"{f} FPS", Tag = f })
                .ToList();
            SecondaryFpsComboBox.ItemsSource = fpsItems;
            int currentFps = Config?.SecondaryFrameFps > 0
                ? Config!.SecondaryFrameFps
                : AppConfig.DefaultSecondaryFrameFps;
            SecondaryFpsComboBox.SelectedItem =
                fpsItems.FirstOrDefault(i => i.Tag is int fps && fps == currentFps)
                ?? fpsItems.FirstOrDefault();
        }

        /// <summary>保存时把副摄分辨率/帧率的选择写回配置；预设字段按实际尺寸回填。</summary>
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

            if (SecondaryFpsComboBox?.SelectedItem is ComboBoxItem fpsItem
                && fpsItem.Tag is int fps
                && fps > 0)
            {
                config.SecondaryFrameFps = fps;
            }
        }

        /// <summary>选了"无"之外的值就显示其余副摄选项。</summary>
        public bool IsSecondaryCameraConfigured =>
            Config is { } config
            && !string.Equals(NormalizedKind(config), AppConfig.SecondaryCameraSourceNone, StringComparison.Ordinal);

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
