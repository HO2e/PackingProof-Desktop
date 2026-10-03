using System;
using System.Collections;
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
    /// 设置页里摄像头设备下拉的绑定数据与互斥规则。
    ///
    /// 设置页的 DataContext 是窗口自己（<c>this.DataContext = this</c>），所以这些属性必须挂在窗口上；
    /// 放在独立分部文件里是为了不给冻结的 SettingsWindow.xaml.cs 增加行数。
    ///
    /// 主摄/副摄（以及以后的第三、第四路）"同一台设备只能被一路占用"的判断统一走
    /// <see cref="CameraDeviceSelectionPolicy"/>；这里只负责取一份完整清单、把投影结果套回两个下拉，
    /// 并在两路撞车时让优先级低的副摄退回"无"。
    /// </summary>
    public partial class SettingsWindow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private bool _syncingCameraChoices;

        /// <summary>摄像头完整清单（含主摄清单末尾的"网络摄像头（手动地址）"伪项）。</summary>
        private List<CameraDeviceChoice>? _allCameraChoices;

        /// <summary>上面那份缓存对应的下拉清单引用，用来判断主摄清单有没有被重新填过。</summary>
        private object? _allCameraChoicesSource;

        /// <summary>上一次写进主摄下拉的那份投影，用来判断内容是否真的变了。</summary>
        private List<CameraInfo>? _appliedMainChoices;

        /// <summary>盯住主摄下拉 ItemsSource 的监听器（清单是异步填的，得知道它什么时候到位）。</summary>
        private EventHandler? _cameraItemsSourceWatcher;

        /// <summary>副摄下拉当前内容。只有内容变了才换成新的一份，避免无谓地清掉选中项。</summary>
        private List<CameraDeviceChoice> _secondaryChoices = CreateSecondaryBaseChoices();

        private static List<CameraDeviceChoice> CreateSecondaryBaseChoices() =>
            new()
            {
                new CameraDeviceChoice("无", AppConfig.OverlayChannelSourceNone, "", -1)
            };

        /// <summary>
        /// 取一份完整设备清单。主摄清单是**异步**填进下拉的，第一次读到很可能还是空的，
        /// 所以只认"非空"的那一次；缓存之后就不再回读主摄下拉 ——
        /// 主摄下拉里放的会是我们投影过的列表，回读会让清单每同步一次少一台。
        /// 冻结的加载/刷新代码重新填主摄清单时（引用变了），以它新填的那份完整清单为准。
        /// </summary>
        private List<CameraDeviceChoice> AllCameraChoices()
        {
            object? source = CameraComboBox?.ItemsSource;
            if (_allCameraChoices is { Count: > 0 } cached
                && ReferenceEquals(source, _allCameraChoicesSource))
            {
                return cached;
            }

            List<CameraDeviceChoice> read = (source as IEnumerable)?
                .OfType<CameraInfo>()
                .Select(ToChoice)
                .ToList() ?? new List<CameraDeviceChoice>();
            if (read.Count > 0)
            {
                _allCameraChoices = read;
                _allCameraChoicesSource = source;
            }

            return _allCameraChoices ?? read;
        }

        private static CameraDeviceChoice ToChoice(CameraInfo camera) =>
            new(
                camera.Name,
                string.Equals(camera.Moniker, "network:", StringComparison.Ordinal) ? "network" : "usb",
                camera.Moniker ?? "",
                camera.Index);

        private static CameraInfo ToCameraInfo(CameraDeviceChoice choice) =>
            new() { Name = choice.Name, Moniker = choice.Moniker, Index = choice.Index };

        /// <summary>下拉项对应的设备标识；"无"和网络摄像头都不占用本机设备，返回空串。</summary>
        private static string MonikerOf(CameraInfo? camera) =>
            camera == null
            || string.IsNullOrEmpty(camera.Moniker)
            || string.Equals(camera.Moniker, "network:", StringComparison.Ordinal)
                ? ""
                : camera.Moniker;

        /// <summary>
        /// 主摄当前占用的设备：优先看下拉里真正选中的那一项，下拉还没选好时才回落到配置。
        ///
        /// 不能只看 <see cref="AppConfig.CameraMonikerString"/> —— 它只在保存时才写回，
        /// 改完主摄还没保存时它还是旧设备，副摄就会以为新设备空着（这就是"没及时互斥"）。
        /// </summary>
        private string LiveMainMoniker() =>
            CameraComboBox?.SelectedItem is CameraInfo selected
                ? MonikerOf(selected)
                : ConfigMainMoniker();

        private string ConfigMainMoniker()
        {
            if (Config is not { } config)
                return "";

            // 网络摄像头/未检测到设备在下拉里是 Index = -1 的伪项，不占用本机设备
            if (config.CameraIndex < 0
                || !string.Equals(config.CameraSourceKind, "usb", StringComparison.Ordinal))
            {
                return "";
            }

            return config.CameraMonikerString ?? "";
        }

        /// <summary>
        /// 设置页当前操作的那一路叠加画面（通道 1，即"副画面 1"）。
        /// 配置里第一路永远存在（归一保证），下一提交再让页面按通道生成多张卡片。
        /// </summary>
        private CameraChannelConfig? OverlayChannel =>
            Config is { CameraChannels.Count: > 0 } config ? config.CameraChannels[0] : null;

        /// <summary>这一路当前占用的设备；配置在用户选中那一刻就写好了，直接读即可。</summary>
        private string LiveSecondaryMoniker()
        {
            if (OverlayChannel is not { } channel)
                return "";

            return string.Equals(NormalizedKind(channel), "usb", StringComparison.Ordinal)
                ? channel.MonikerString ?? ""
                : "";
        }

        /// <summary>
        /// 副摄下拉：无 + 本机设备（去掉主摄占用的那台）+ 网络摄像头。
        /// 主摄那份清单本身既没有"无"、也没有副摄自己的"网络摄像头"入口，这里补齐。
        /// </summary>
        public IReadOnlyList<CameraDeviceChoice> SecondaryCameraChoices => _secondaryChoices;

        /// <summary>
        /// 主摄/副摄两个下拉互相排除：任一边换了设备，两边都用新的占用关系重新投影一次。
        /// 主摄下拉仍用 <see cref="CameraInfo"/>（冻结代码按这个类型读选中项）。
        /// </summary>
        internal void SyncCameraChoices()
        {
            if (_syncingCameraChoices || CameraComboBox == null || Config is not { } config)
                return;

            List<CameraDeviceChoice> all = AllCameraChoices();
            if (all.Count == 0)
                return;

            _syncingCameraChoices = true;
            try
            {
                string mainMoniker = LiveMainMoniker();
                string secondaryMoniker = LiveSecondaryMoniker();

                // 同一台设备只能归一路，主摄优先。历史配置里两路撞车时让副摄退回"无"，
                // 而不是把用户眼前选中的主摄挪走 —— 那样下拉会自己跳到第一台设备。
                IReadOnlyList<string> resolved = CameraDeviceSelectionPolicy.ResolveOwnership(
                    new[] { mainMoniker, secondaryMoniker },
                    all);
                if (!string.Equals(resolved[1], secondaryMoniker, StringComparison.Ordinal))
                {
                    // 直接改配置：SelectedSecondaryCameraChoice 的 setter 带同步守卫，
                    // 从同步流程里调会被挡掉（这就是以前"还能选成同一台"的来源）。
                    ClearSecondaryCameraSelection(config);
                    secondaryMoniker = "";
                }

                ApplyMainCameraChoices(all, mainMoniker, secondaryMoniker);
                ApplySecondaryCameraChoices(all, mainMoniker, secondaryMoniker);
            }
            finally
            {
                _syncingCameraChoices = false;
            }
        }

        private static void ClearSecondaryCameraSelection(AppConfig config)
        {
            if (config.CameraChannels.Count == 0)
                return;

            CameraChannelConfig channel = config.CameraChannels[0];
            channel.SourceKind = AppConfig.OverlayChannelSourceNone;
            channel.Index = -1;
            channel.MonikerString = "";
        }

        /// <summary>
        /// 把"排除副摄占用的那台"之后的清单套回主摄下拉。
        /// 只有内容真的变了才换 ItemsSource：每次同步都换一份新清单会把用户刚选中的项清掉、
        /// 再退到列表第一台，看到的就是"选了之后选中项乱跳"。
        /// </summary>
        private void ApplyMainCameraChoices(
            IReadOnlyList<CameraDeviceChoice> all,
            string mainMoniker,
            string secondaryMoniker)
        {
            List<CameraInfo> projected = CameraDeviceSelectionPolicy
                .Project(all, mainMoniker, new[] { secondaryMoniker })
                .Select(ToCameraInfo)
                .ToList();

            if (_appliedMainChoices == null || !SameMainChoices(_appliedMainChoices, projected))
            {
                CameraComboBox.ItemsSource = projected;
                _appliedMainChoices = projected;
                _allCameraChoicesSource = projected;
            }

            List<CameraInfo> current = CameraComboBox.ItemsSource as List<CameraInfo> ?? projected;
            CameraInfo? target =
                current.FirstOrDefault(camera => string.Equals(MonikerOf(camera), mainMoniker, StringComparison.Ordinal))
                ?? current.FirstOrDefault();
            if (!ReferenceEquals(CameraComboBox.SelectedItem, target))
                CameraComboBox.SelectedItem = target;
        }

        private static bool SameMainChoices(
            IReadOnlyList<CameraInfo> left,
            IReadOnlyList<CameraInfo> right)
        {
            if (left.Count != right.Count)
                return false;

            for (int i = 0; i < left.Count; i++)
            {
                if (left[i].Index != right[i].Index
                    || !string.Equals(left[i].Moniker ?? "", right[i].Moniker ?? "", StringComparison.Ordinal)
                    || !string.Equals(left[i].Name, right[i].Name, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private void ApplySecondaryCameraChoices(
            IReadOnlyList<CameraDeviceChoice> all,
            string mainMoniker,
            string secondaryMoniker)
        {
            List<CameraDeviceChoice> choices = CreateSecondaryBaseChoices();
            choices.AddRange(
                CameraDeviceSelectionPolicy
                    .Project(all, secondaryMoniker, new[] { mainMoniker })
                    .Where(choice => choice.Kind != "network"));
            choices.Add(new CameraDeviceChoice("网络摄像头", "network", "", -1));

            // 内容没变就别换列表：换一次就会清掉选中项再重新绑定，看起来像在乱跳
            if (!_secondaryChoices.SequenceEqual(choices))
            {
                _secondaryChoices = choices;
                Raise(nameof(SecondaryCameraChoices));
            }

            Raise(nameof(SelectedSecondaryCameraChoice));
            Raise(nameof(IsSecondaryCameraConfigured));
            Raise(nameof(IsSecondaryNetworkCameraSelected));
        }

        public CameraDeviceChoice? SelectedSecondaryCameraChoice
        {
            get
            {
                if (OverlayChannel is not { } channel)
                    return _secondaryChoices.FirstOrDefault();

                string kind = NormalizedKind(channel);
                string moniker = LiveSecondaryMoniker();
                return _secondaryChoices.FirstOrDefault(choice =>
                        string.Equals(choice.Kind, kind, StringComparison.Ordinal)
                        && (choice.Kind != "usb"
                            || string.Equals(choice.Moniker, moniker, StringComparison.Ordinal)))
                    ?? _secondaryChoices.FirstOrDefault();
            }
            set
            {
                if (value == null || OverlayChannel is not { } channel || _syncingCameraChoices)
                    return;

                channel.SourceKind = value.Kind;
                channel.Index = value.Index;
                channel.MonikerString = value.Kind == "usb" ? value.Moniker : "";

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

            WatchCameraItemsSource();
            LoadSecondaryCameraFormats();
            SyncCameraChoices();
        }

        /// <summary>
        /// 主摄清单是异步填进下拉的，填好那一刻选中项可能压根没变 ——
        /// 例如配置里那个索引已经不在新清单里，赋值不会引起 SelectionChanged，
        /// 只盯选中事件会漏掉这一次，副摄下拉就会一直空着。所以直接盯住 ItemsSource 的赋值。
        /// </summary>
        private void WatchCameraItemsSource()
        {
            if (_cameraItemsSourceWatcher != null || CameraComboBox == null)
                return;

            DependencyPropertyDescriptor? descriptor = DependencyPropertyDescriptor.FromProperty(
                ItemsControl.ItemsSourceProperty,
                typeof(ComboBox));
            if (descriptor == null)
                return;

            _cameraItemsSourceWatcher = (_, _) => SyncCameraChoices();
            descriptor.AddValueChanged(CameraComboBox, _cameraItemsSourceWatcher);
        }

        private void MainCameraSelectionChangedForSecondary(object sender, SelectionChangedEventArgs e)
        {
            // 我们自己重排清单、恢复选中项时也会触发一次 SelectionChanged，这里不用再跟着跑一遍
            if (_syncingCameraChoices)
                return;

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

            string moniker = LiveSecondaryMoniker();
            CameraFormatOptions formats = string.IsNullOrEmpty(moniker)
                ? CameraFormatCatalog.FromCapabilities([])
                : CameraFormatCatalog.Enumerate(moniker);

            SecondaryResolutionComboBox.ItemsSource = formats.Resolutions;
            (int targetWidth, int targetHeight) = AppConfig.ResolveOverlayFrameSize(
                OverlayChannel?.ResolutionPreset,
                OverlayChannel?.FrameWidth ?? 0,
                OverlayChannel?.FrameHeight ?? 0);
            SecondaryResolutionComboBox.SelectedItem =
                formats.Resolutions.FirstOrDefault(r => r.Width == targetWidth && r.Height == targetHeight)
                ?? formats.Resolutions.FirstOrDefault();

            var fpsItems = formats.FpsValues
                .Select(f => new ComboBoxItem { Content = $"{f} FPS", Tag = f })
                .ToList();
            SecondaryFpsComboBox.ItemsSource = fpsItems;
            int currentFps = OverlayChannel?.FrameFps > 0
                ? OverlayChannel!.FrameFps
                : AppConfig.DefaultOverlayFrameFps;
            SecondaryFpsComboBox.SelectedItem =
                fpsItems.FirstOrDefault(i => i.Tag is int fps && fps == currentFps)
                ?? fpsItems.FirstOrDefault();
        }

        /// <summary>保存时把副摄分辨率/帧率的选择写回配置；预设字段按实际尺寸回填。</summary>
        internal void ApplySecondaryCameraFormatsToConfig()
        {
            if (OverlayChannel is not { } channel)
                return;

            if (SecondaryResolutionComboBox?.SelectedItem is CameraResolutionOption resolution)
            {
                channel.FrameWidth = resolution.Width;
                channel.FrameHeight = resolution.Height;
                channel.ResolutionPreset = AppConfig.PresetForSize(resolution.Width, resolution.Height);
            }

            if (SecondaryFpsComboBox?.SelectedItem is ComboBoxItem fpsItem
                && fpsItem.Tag is int fps
                && fps > 0)
            {
                channel.FrameFps = fps;
            }
        }

        /// <summary>选了"无"之外的值就显示其余副摄选项。</summary>
        public bool IsSecondaryCameraConfigured =>
            OverlayChannel is { } channel
            && !string.Equals(NormalizedKind(channel), AppConfig.OverlayChannelSourceNone, StringComparison.Ordinal);

        /// <summary>选了"网络摄像头"才显示地址输入。</summary>
        public bool IsSecondaryNetworkCameraSelected =>
            OverlayChannel is { } channel
            && string.Equals(NormalizedKind(channel), "network", StringComparison.Ordinal);

        private static string NormalizedKind(CameraChannelConfig channel) =>
            AppConfig.NormalizeOverlayChannelSourceKind(
                channel.SourceKind,
                channel.NetworkCameraUrl);

        private void Raise(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
