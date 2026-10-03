using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;

namespace ExpressPackingMonitoring.UI
{
    /// <summary>
    /// 设置页里"副画面 N"这张卡片的绑定数据。
    ///
    /// 每一路叠加画面一张卡，卡片自己只读配置、下拉清单与档位；设备互斥、清单投影这些
    /// 跨通道的规则都在设置窗口那边统一算（见 <see cref="SettingsWindow.SyncCameraChoices"/>）。
    /// 加第三、第四路时这里一行都不用改。
    /// </summary>
    public sealed class OverlayChannelCard : INotifyPropertyChanged
    {
        private readonly SettingsWindow _owner;
        private readonly int _index;
        private IReadOnlyList<CameraDeviceChoice> _deviceChoices = Array.Empty<CameraDeviceChoice>();
        private IReadOnlyList<CameraResolutionOption> _resolutions = Array.Empty<CameraResolutionOption>();
        private IReadOnlyList<FpsOption> _fpsOptions = Array.Empty<FpsOption>();
        private CameraResolutionOption? _selectedResolution;
        private FpsOption? _selectedFps;

        internal OverlayChannelCard(SettingsWindow owner, int index)
        {
            _owner = owner;
            _index = index;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>通道号：1 = 副画面 1。与识别来源、日志里的通道号同一套编号。</summary>
        public int Number => _index + 1;

        public string Title => $"副画面 {Number}";

        /// <summary>卡头显示的当前画面来源。</summary>
        public string DeviceSummary =>
            !IsConfigured
                ? "未添加"
                : IsNetworkSelected
                    ? "网络摄像头"
                    : SelectedDevice?.Name ?? "未添加";

        internal CameraChannelConfig? Config =>
            _owner.Config is { } config && _index >= 0 && _index < config.CameraChannels.Count
                ? config.CameraChannels[_index]
                : null;

        /// <summary>这一路接了设备才显示档位与旋转。</summary>
        public bool IsConfigured => Config is { } config && config.IsConfigured;

        /// <summary>选了"网络摄像头"才显示地址输入。</summary>
        public bool IsNetworkSelected =>
            Config is { } config
            && string.Equals(
                AppConfig.NormalizeOverlayChannelSourceKind(config.SourceKind, config.NetworkCameraUrl),
                "network",
                StringComparison.Ordinal);

        /// <summary>
        /// 这一路能选的设备：无 + 本机设备（去掉主摄和其它叠加路占用的）+ 网络摄像头。
        /// 清单由窗口统一投影，这里只负责显示与回写用户的选择。
        /// </summary>
        public IReadOnlyList<CameraDeviceChoice> DeviceChoices => _deviceChoices;

        public CameraDeviceChoice? SelectedDevice
        {
            get
            {
                if (Config is not { } config)
                    return _deviceChoices.FirstOrDefault();

                string kind = AppConfig.NormalizeOverlayChannelSourceKind(
                    config.SourceKind,
                    config.NetworkCameraUrl);
                return _deviceChoices.FirstOrDefault(choice =>
                        string.Equals(choice.Kind, kind, StringComparison.Ordinal)
                        && (choice.Kind != "usb"
                            || string.Equals(choice.Moniker, config.MonikerString ?? "", StringComparison.Ordinal)))
                    ?? _deviceChoices.FirstOrDefault();
            }
            set
            {
                if (value == null || Config is not { } config || _owner.IsSyncingCameraChoices)
                    return;

                config.SourceKind = value.Kind;
                config.Index = value.Index;
                config.MonikerString = value.Kind == "usb" ? value.Moniker : "";

                _owner.SyncCameraChoices();
                _owner.LoadOverlayChannelFormats(this);
                RaiseAll();
            }
        }

        public string NetworkUrl
        {
            get => Config?.NetworkCameraUrl ?? "";
            set
            {
                if (Config is not { } config || string.Equals(config.NetworkCameraUrl, value, StringComparison.Ordinal))
                    return;

                config.NetworkCameraUrl = value ?? "";
                Raise();
            }
        }

        /// <summary>这一路的旋转角度（0/90/180/270）。</summary>
        public int RotationDegrees
        {
            get => Config?.RotationDegrees ?? 0;
            set
            {
                if (Config is not { } config || config.RotationDegrees == value)
                    return;

                config.RotationDegrees = value;
                Raise();
                Raise(nameof(DeviceSummary));
            }
        }

        public IReadOnlyList<CameraResolutionOption> Resolutions => _resolutions;

        public CameraResolutionOption? SelectedResolution
        {
            get => _selectedResolution;
            set
            {
                if (value == null || Config is not { } config || ReferenceEquals(_selectedResolution, value))
                    return;

                _selectedResolution = value;
                config.FrameWidth = value.Width;
                config.FrameHeight = value.Height;
                config.ResolutionPreset = AppConfig.PresetForSize(value.Width, value.Height);
                Raise();
            }
        }

        public IReadOnlyList<FpsOption> FpsOptions => _fpsOptions;

        public FpsOption? SelectedFps
        {
            get => _selectedFps;
            set
            {
                if (value == null || Config is not { } config || ReferenceEquals(_selectedFps, value) || value.Fps <= 0)
                    return;

                _selectedFps = value;
                config.FrameFps = value.Fps;
                Raise();
            }
        }

        /// <summary>窗口重算这一路的设备清单后调用：只换列表，不碰用户已经选中的设备。</summary>
        internal void UpdateDeviceChoices(IReadOnlyList<CameraDeviceChoice> choices)
        {
            _deviceChoices = choices;
            Raise(nameof(DeviceChoices));
            Raise(nameof(SelectedDevice));
            Raise(nameof(DeviceSummary));
        }

        /// <summary>窗口枚举出这一路支持的档位后调用。</summary>
        internal void ApplyFormats(CameraChannelConfig config, CameraFormatOptions formats)
        {
            List<CameraResolutionOption> resolutions = formats.Resolutions.ToList();
            (int width, int height) = AppConfig.ResolveOverlayFrameSize(
                config.ResolutionPreset,
                config.FrameWidth,
                config.FrameHeight);
            CameraResolutionOption? selectedResolution =
                resolutions.FirstOrDefault(r => r.Width == width && r.Height == height);
            if (selectedResolution == null)
            {
                // 设备这次报出来的档位里没有用户存的那一档（设备被本程序占用、换了采集后端、
                // 换了设备都会这样）。把存的那一档补进列表，**绝不能**默默改成第一项 ——
                // 那样保存时会把用户选好的分辨率改掉，看起来就是"一进设置页分辨率就被清了"。
                selectedResolution = new CameraResolutionOption(
                    $"{width}x{height}{CameraFormatCatalog.ResolutionLabel(width, height)}",
                    width,
                    height);
                resolutions.Insert(0, selectedResolution);
            }

            List<FpsOption> fpsOptions = formats.FpsValues
                .Select(fps => new FpsOption { Fps = fps, Label = $"{fps} FPS" })
                .ToList();
            int currentFps = config.FrameFps > 0 ? config.FrameFps : AppConfig.DefaultOverlayFrameFps;
            FpsOption? selectedFps = fpsOptions.FirstOrDefault(option => option.Fps == currentFps);
            if (selectedFps == null)
            {
                // 同理：帧率列表对不上时保留用户存的那一档，而不是回退到列表第一项。
                selectedFps = new FpsOption { Fps = currentFps, Label = $"{currentFps} FPS" };
                fpsOptions.Insert(0, selectedFps);
            }

            _resolutions = resolutions;
            _selectedResolution = selectedResolution;
            _fpsOptions = fpsOptions;
            _selectedFps = selectedFps;

            Raise(nameof(Resolutions));
            Raise(nameof(SelectedResolution));
            Raise(nameof(FpsOptions));
            Raise(nameof(SelectedFps));
        }

        /// <summary>保存前把界面上选中的档位落回配置（下拉刚改完还没失焦时也能写进去）。</summary>
        internal void FlushFormatSelection()
        {
            if (Config is not { } config)
                return;

            if (_selectedResolution is { } resolution)
            {
                config.FrameWidth = resolution.Width;
                config.FrameHeight = resolution.Height;
                config.ResolutionPreset = AppConfig.PresetForSize(resolution.Width, resolution.Height);
            }

            if (_selectedFps is { Fps: > 0 } fps)
                config.FrameFps = fps.Fps;
        }

        internal void RaiseAll()
        {
            Raise(nameof(IsConfigured));
            Raise(nameof(IsNetworkSelected));
            Raise(nameof(DeviceSummary));
            Raise(nameof(SelectedDevice));
            Raise(nameof(NetworkUrl));
            Raise(nameof(RotationDegrees));
        }

        private void Raise(string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>面单识别来源下拉的一项：0 = 主摄像头，1..n = 第 n 路叠加画面。</summary>
    public sealed record BarcodeRecognitionChannelOption(int Number, string Name);

    /// <summary>
    /// 设置页里的摄像头通道绑定数据。
    ///
    /// 设置页的 DataContext 是窗口自己（<c>this.DataContext = this</c>），所以这些属性必须挂在窗口上；
    /// 放在独立分部文件里是为了不给冻结的 SettingsWindow.xaml.cs 增加行数。
    ///
    /// 主摄像头那张卡仍是显式布局：冻结的 SettingsWindow.xaml.cs 直接读 CameraComboBox / ResComboBox
    /// 这些具名控件。会"多路"的是叠加画面，所以频道化落在 <see cref="OverlayCameraCards"/> 上，
    /// 加第三、第四路只是列表里多一项。
    ///
    /// 选设备的规则（哪一路能选哪台）统一走 <see cref="CameraDeviceSelectionPolicy"/>：
    /// 按优先级占设备，撞车时让位的那一路退回"无"。
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

        private List<BarcodeRecognitionChannelOption> _barcodeChannelChoices = new();

        internal bool IsSyncingCameraChoices => _syncingCameraChoices;

        /// <summary>每一路叠加画面一张卡；没接设备的那一路也留着，用户才能"添加"。</summary>
        public ObservableCollection<OverlayChannelCard> OverlayCameraCards { get; } = new();

        /// <summary>面单识别来源可选项：主摄像头 + 已经接了设备的叠加画面。</summary>
        public IReadOnlyList<BarcodeRecognitionChannelOption> BarcodeRecognitionChannelChoices =>
            _barcodeChannelChoices;

        public BarcodeRecognitionChannelOption? SelectedBarcodeRecognitionChannel
        {
            get
            {
                int number = Config?.CameraBarcodeRecognitionChannel ?? 0;
                return _barcodeChannelChoices.FirstOrDefault(option => option.Number == number)
                    ?? _barcodeChannelChoices.FirstOrDefault();
            }
            set
            {
                if (value == null || Config is not { } config || config.CameraBarcodeRecognitionChannel == value.Number)
                    return;

                config.CameraBarcodeRecognitionChannel = value.Number;
                Raise(nameof(SelectedBarcodeRecognitionChannel));
            }
        }

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

        /// <summary>某一路叠加画面当前占用的设备标识。</summary>
        private static string ChannelMoniker(CameraChannelConfig channel) =>
            string.Equals(
                AppConfig.NormalizeOverlayChannelSourceKind(channel.SourceKind, channel.NetworkCameraUrl),
                "usb",
                StringComparison.Ordinal)
                ? channel.MonikerString ?? ""
                : "";

        /// <summary>
        /// 主摄当前占用的设备：优先看下拉里真正选中的那一项，下拉还没选好时才回落到配置。
        ///
        /// 不能只看 <see cref="AppConfig.CameraMonikerString"/> —— 它只在保存时才写回，
        /// 改完主摄还没保存时它还是旧设备，叠加画面就会以为新设备空着（这就是"没及时互斥"）。
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

        /// <summary>按当前配置重建卡片列表（加/删一路、重新打开设置页时调用）。</summary>
        internal void RebuildOverlayCards()
        {
            OverlayCameraCards.Clear();
            if (Config is { } config)
            {
                for (int i = 0; i < config.CameraChannels.Count; i++)
                    OverlayCameraCards.Add(new OverlayChannelCard(this, i));
            }
        }

        /// <summary>
        /// 主摄与所有叠加画面一起重算：任一路换了设备，所有下拉都用新的占用关系重新投影。
        /// 同一台设备只能归一路，主摄优先；让位的那一路退回"无"（不把用户眼前选中的主摄挪走）。
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
                string[] requested = new string[config.CameraChannels.Count + 1];
                requested[0] = LiveMainMoniker();
                for (int i = 0; i < config.CameraChannels.Count; i++)
                    requested[i + 1] = ChannelMoniker(config.CameraChannels[i]);

                IReadOnlyList<string> resolved = CameraDeviceSelectionPolicy.ResolveOwnership(requested, all);
                List<int> clearedChannels = new();
                for (int i = 1; i < requested.Length; i++)
                {
                    if (string.Equals(resolved[i], requested[i], StringComparison.Ordinal))
                        continue;

                    // 直接改配置：卡片的 setter 带同步守卫，从同步流程里调会被挡掉。
                    ClearChannel(config.CameraChannels[i - 1]);
                    requested[i] = "";
                    clearedChannels.Add(i);
                }

                ApplyMainCameraChoices(all, requested);
                ApplyCardChoices(all, requested);
                RaiseBarcodeChannelChoices();

                // 只有真的被让位（设备变成"无"）的那一路才需要重算档位；
                // 其它路重新枚举会把用户选好的分辨率/帧率冲掉。
                foreach (int number in clearedChannels)
                {
                    OverlayChannelCard? cleared = OverlayCameraCards.FirstOrDefault(card => card.Number == number);
                    if (cleared != null)
                        LoadOverlayChannelFormats(cleared);
                }
            }
            finally
            {
                _syncingCameraChoices = false;
            }

            foreach (OverlayChannelCard card in OverlayCameraCards)
                card.RaiseAll();
        }

        private static void ClearChannel(CameraChannelConfig channel)
        {
            channel.SourceKind = AppConfig.OverlayChannelSourceNone;
            channel.Index = -1;
            channel.MonikerString = "";
        }

        /// <summary>
        /// 把"排除其它路占用的设备"之后的清单套回主摄下拉。
        /// 只有内容真的变了才换 ItemsSource：每次同步都换一份新清单会把用户刚选中的项清掉、
        /// 再退到列表第一台，看到的就是"选了之后选中项乱跳"。
        /// </summary>
        private void ApplyMainCameraChoices(IReadOnlyList<CameraDeviceChoice> all, string[] requested)
        {
            List<CameraInfo> projected = CameraDeviceSelectionPolicy
                .Project(all, requested[0], requested.Skip(1))
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
                current.FirstOrDefault(camera => string.Equals(MonikerOf(camera), requested[0], StringComparison.Ordinal))
                ?? current.FirstOrDefault();
            if (!ReferenceEquals(CameraComboBox.SelectedItem, target))
                CameraComboBox.SelectedItem = target;
        }

        private void ApplyCardChoices(IReadOnlyList<CameraDeviceChoice> all, string[] requested)
        {
            for (int i = 0; i < OverlayCameraCards.Count; i++)
            {
                OverlayChannelCard card = OverlayCameraCards[i];
                int selfIndex = card.Number;
                if (selfIndex >= requested.Length)
                    continue;

                List<CameraDeviceChoice> choices = new()
                {
                    new CameraDeviceChoice("无", AppConfig.OverlayChannelSourceNone, "", -1)
                };
                choices.AddRange(
                    CameraDeviceSelectionPolicy
                        .Project(all, requested[selfIndex], requested.Where((_, index) => index != selfIndex))
                        .Where(choice => choice.Kind != "network"));
                choices.Add(new CameraDeviceChoice("网络摄像头", "network", "", -1));
                card.UpdateDeviceChoices(choices);
            }
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

        /// <summary>识别来源下拉只列"主摄 + 已接设备的叠加画面"：没接的那一路选了也认不出来。</summary>
        private void RaiseBarcodeChannelChoices()
        {
            List<BarcodeRecognitionChannelOption> choices = new()
            {
                new BarcodeRecognitionChannelOption(0, "主摄像头")
            };
            foreach (OverlayChannelCard card in OverlayCameraCards)
            {
                if (card.IsConfigured)
                    choices.Add(new BarcodeRecognitionChannelOption(card.Number, card.Title));
            }

            if (Config is { } config
                && !choices.Any(option => option.Number == config.CameraBarcodeRecognitionChannel))
            {
                // 识别来源指到了没接设备的那一路：回到主摄，别让识别静默失效。
                config.CameraBarcodeRecognitionChannel = 0;
            }

            _barcodeChannelChoices = choices;
            Raise(nameof(BarcodeRecognitionChannelChoices));
            Raise(nameof(SelectedBarcodeRecognitionChannel));
        }

        /// <summary>
        /// 下拉第一次显示时把卡片与档位填好，并盯住主摄下拉：主摄换设备 → 所有下拉都重算。
        /// （在 XAML 挂 Loaded，避免动冻结的 SettingsWindow.xaml.cs）
        /// </summary>
        internal void CameraChannelCards_Loaded(object sender, RoutedEventArgs e)
        {
            if (CameraComboBox != null)
            {
                CameraComboBox.SelectionChanged -= MainCameraSelectionChangedForChannels;
                CameraComboBox.SelectionChanged += MainCameraSelectionChangedForChannels;
            }

            WatchCameraItemsSource();
            WatchMainCameraFormats();
            RebuildOverlayCards();
            foreach (OverlayChannelCard card in OverlayCameraCards)
                LoadOverlayChannelFormats(card);
            SyncCameraChoices();
        }

        /// <summary>
        /// 主摄清单是异步填进下拉的，填好那一刻选中项可能压根没变 ——
        /// 例如配置里那个索引已经不在新清单里，赋值不会引起 SelectionChanged，
        /// 只盯选中事件会漏掉这一次，叠加画面的下拉就会一直空着。所以直接盯住 ItemsSource 的赋值。
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

        private bool _repairingMainCameraFormats;

        /// <summary>
        /// 盯住主摄的分辨率/帧率下拉。它们由冻结的设置页代码填：设备这次枚举不到档位时
        /// （采集中的摄像头被本程序占用，很常见）会回退到列表第一项，保存时就把用户选好的档位改掉 ——
        /// 现场表现就是"每次进设置页，这些摄像头的帧率/分辨率被清掉，要重新点"。
        /// 这里等它填完，再把用户存的那一档补回下拉并选中。
        /// </summary>
        private void WatchMainCameraFormats()
        {
            if (ResComboBox != null)
            {
                ResComboBox.SelectionChanged -= MainCameraFormatSelectionChanged;
                ResComboBox.SelectionChanged += MainCameraFormatSelectionChanged;
            }

            if (FpsComboBox != null)
            {
                FpsComboBox.SelectionChanged -= MainCameraFormatSelectionChanged;
                FpsComboBox.SelectionChanged += MainCameraFormatSelectionChanged;
            }
        }

        private void MainCameraFormatSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_repairingMainCameraFormats)
                return;

            // 冻结的加载是"先填列表再选一项"，要等它这一轮走完再补，否则会被它覆盖掉。
            Dispatcher.BeginInvoke(
                new Action(RepairMainCameraFormatSelections),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private void RepairMainCameraFormatSelections()
        {
            if (_repairingMainCameraFormats || Config is not { } config)
                return;

            _repairingMainCameraFormats = true;
            try
            {
                List<CameraResolutionOption> resolutions =
                    (ResComboBox?.ItemsSource as IEnumerable)?.OfType<CameraResolutionOption>().ToList()
                    ?? new List<CameraResolutionOption>();
                if (resolutions.Count > 0
                    && config.FrameWidth > 0
                    && config.FrameHeight > 0
                    && !resolutions.Any(r => r.Width == config.FrameWidth && r.Height == config.FrameHeight))
                {
                    var savedResolution = new CameraResolutionOption(
                        $"{config.FrameWidth}x{config.FrameHeight}"
                            + CameraFormatCatalog.ResolutionLabel(config.FrameWidth, config.FrameHeight),
                        config.FrameWidth,
                        config.FrameHeight);
                    resolutions.Insert(0, savedResolution);
                    ResComboBox!.ItemsSource = resolutions;
                    ResComboBox.SelectedItem = savedResolution;
                }

                List<ComboBoxItem> fpsItems =
                    (FpsComboBox?.ItemsSource as IEnumerable)?.OfType<ComboBoxItem>().ToList()
                    ?? new List<ComboBoxItem>();
                if (fpsItems.Count > 0
                    && config.Fps > 0
                    && !fpsItems.Any(item => item.Tag is int fps && fps == config.Fps))
                {
                    var savedFps = new ComboBoxItem { Content = $"{config.Fps} FPS", Tag = config.Fps };
                    fpsItems.Insert(0, savedFps);
                    FpsComboBox!.ItemsSource = fpsItems;
                    FpsComboBox.SelectedItem = savedFps;
                }
            }
            finally
            {
                _repairingMainCameraFormats = false;
            }
        }

        private void MainCameraSelectionChangedForChannels(object sender, SelectionChangedEventArgs e)
        {
            // 我们自己重排清单、恢复选中项时也会触发一次 SelectionChanged，这里不用再跟着跑一遍
            if (_syncingCameraChoices)
                return;

            // 只重算占用关系：这一路的设备没变就不重新枚举档位 ——
            // 设备每次枚举出来的档位可能不一样，重新枚举会把用户选好的分辨率/帧率冲掉。
            SyncCameraChoices();
        }

        /// <summary>
        /// 某一路的档位枚举走 <see cref="CameraFormatCatalog"/>，与主摄同一套；
        /// 网络摄像头/没选设备时用兜底档位。
        /// </summary>
        internal void LoadOverlayChannelFormats(OverlayChannelCard card)
        {
            if (card.Config is not { } config)
                return;

            string moniker = ChannelMoniker(config);
            CameraFormatOptions formats = string.IsNullOrEmpty(moniker)
                ? CameraFormatCatalog.FromCapabilities([])
                : CameraFormatCatalog.Enumerate(moniker);
            card.ApplyFormats(config, formats);
        }

        /// <summary>保存时把各张卡片上选中的分辨率/帧率写回配置；预设字段按实际尺寸回填。</summary>
        internal void ApplySecondaryCameraFormatsToConfig()
        {
            foreach (OverlayChannelCard card in OverlayCameraCards)
                card.FlushFormatSelection();
        }

        private void Raise(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
