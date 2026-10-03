using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Services;
using ExpressPackingMonitoring.UI;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 设置页摄像头通道的互斥、选中项稳定性与增删。
///
/// "没及时互斥"取决于主摄下拉里**当前**选中的那一台（而不是保存过的配置），
/// "选中项乱跳"取决于同步时有没有把主摄的 ItemsSource 换成一份新表；
/// 这两件事纯逻辑守卫看不出来，所以这里真的建出设置窗口，直接动它的下拉与卡片。
/// </summary>
[Collection("WPF render tests")]
public sealed class CameraChannelChoiceUiTests
{
    [Fact]
    public void ChangingMainCamera_ImmediatelyDropsItFromTheCardChoices()
    {
        RunOnStaThread(() =>
        {
            AppConfig config = CreateConfig(mainMoniker: "moniker-a", channelMoniker: "moniker-b");
            SettingsWindow window = CreateWindow(config);
            ComboBox main = PrepareWindow(window);
            OverlayChannelCard card = Assert.Single(window.OverlayCameraCards);

            Assert.Equal("moniker-a", SelectedMoniker(main));
            Assert.Equal(0, config.CameraIndex);
            // 卡片自己那台还在，主摄占着的那台不在
            Assert.Contains(card.DeviceChoices, choice => choice.Moniker == "moniker-b");
            Assert.DoesNotContain(card.DeviceChoices, choice => choice.Moniker == "moniker-a");
            Assert.Equal("moniker-b", card.SelectedDevice?.Moniker);

            // 主摄换成 C：不等保存、不等重开窗口，卡片立刻不能选 C
            main.SelectedItem = ItemOf(main, "moniker-c");

            Assert.Equal("moniker-c", SelectedMoniker(main));
            Assert.Equal(2, config.CameraIndex);
            Assert.DoesNotContain(card.DeviceChoices, choice => choice.Moniker == "moniker-c");
            Assert.Contains(card.DeviceChoices, choice => choice.Moniker == "moniker-b");
            // 主摄来回换也不会把卡片的选中项挤走
            Assert.Equal("moniker-b", card.SelectedDevice?.Moniker);
        });
    }

    [Fact]
    public void PickingCardDevice_ImmediatelyDropsItFromTheMainList()
    {
        RunOnStaThread(() =>
        {
            AppConfig config = CreateConfig(mainMoniker: "moniker-a", channelMoniker: "");
            SettingsWindow window = CreateWindow(config);
            ComboBox main = PrepareWindow(window);
            OverlayChannelCard card = Assert.Single(window.OverlayCameraCards);

            Assert.Equal("无", card.SelectedDevice?.Name);
            Assert.Contains(MainItems(main), camera => camera.Moniker == "moniker-b");

            card.SelectedDevice = card.DeviceChoices.First(choice => choice.Moniker == "moniker-b");

            Assert.Equal("moniker-b", config.CameraChannels[0].MonikerString);
            Assert.Equal("usb", config.CameraChannels[0].SourceKind);
            // 主摄列表立刻排除这一路刚占用的设备
            Assert.DoesNotContain(MainItems(main), camera => camera.Moniker == "moniker-b");
            Assert.Equal("moniker-a", SelectedMoniker(main));

            // 换回"无"以后，那台设备要能被主摄重新选到
            card.SelectedDevice = card.DeviceChoices.First(choice => choice.Kind == "none");
            Assert.Equal(AppConfig.OverlayChannelSourceNone, config.CameraChannels[0].SourceKind);
            Assert.Contains(MainItems(main), camera => camera.Moniker == "moniker-b");
        });
    }

    [Fact]
    public void ConflictingSelections_KeepTheMainChoiceAndClearTheCard()
    {
        RunOnStaThread(() =>
        {
            // 历史配置里主摄和这一路存了同一台：只能有一个结果，而且不能把主摄的选中项挪走
            AppConfig config = CreateConfig(mainMoniker: "moniker-b", channelMoniker: "moniker-b");
            SettingsWindow window = CreateWindow(config);
            ComboBox main = PrepareWindow(window);
            OverlayChannelCard card = Assert.Single(window.OverlayCameraCards);

            Assert.Equal("moniker-b", SelectedMoniker(main));
            // 主摄那台在完整清单里排第二：没有被挤到第一台才算没跳
            Assert.Equal(1, main.SelectedIndex);

            Assert.Equal(AppConfig.OverlayChannelSourceNone, config.CameraChannels[0].SourceKind);
            Assert.Equal("", config.CameraChannels[0].MonikerString);
            Assert.Equal("无", card.SelectedDevice?.Name);
            Assert.DoesNotContain(card.DeviceChoices, choice => choice.Moniker == "moniker-b");
        });
    }

    /// <summary>
    /// 主摄清单是异步填进来的，填好时选中项可能压根没变（旧索引已经不在新清单里），
    /// 只靠 SelectionChanged 会漏掉这次同步，卡片的设备下拉就会空着。
    /// </summary>
    [Fact]
    public void WhenDeviceListArrivesWithoutASelectionChange_CardsStillFill()
    {
        RunOnStaThread(() =>
        {
            AppConfig config = CreateConfig(mainMoniker: "moniker-b", channelMoniker: "moniker-c");
            config.CameraIndex = 99; // 旧索引已经不在这次枚举出来的清单里
            SettingsWindow window = CreateWindow(config);
            ComboBox main = PrepareWindow(window, selectConfiguredCamera: false);
            OverlayChannelCard card = Assert.Single(window.OverlayCameraCards);

            Assert.Equal("moniker-b", SelectedMoniker(main));
            // 主摄占着 B，这一路能选的只有 A 和 C
            Assert.Equal(2, card.DeviceChoices.Count(choice => choice.Kind == "usb"));
            Assert.Contains(card.DeviceChoices, choice => choice.Moniker == "moniker-c");
            Assert.DoesNotContain(card.DeviceChoices, choice => choice.Moniker == "moniker-b");
        });
    }

    /// <summary>加一路就是往配置里多放一个通道，卡片、设备清单与识别来源都要跟着长出来。</summary>
    [Fact]
    public void AddingAndRemovingOverlayChannel_RebuildsCardsAndBarcodeChoices()
    {
        RunOnStaThread(() =>
        {
            AppConfig config = CreateConfig(mainMoniker: "moniker-a", channelMoniker: "moniker-b");
            SettingsWindow window = CreateWindow(config);
            PrepareWindow(window);

            Assert.Single(window.OverlayCameraCards);
            Assert.True(window.CanAddOverlayChannel);
            Assert.Equal(["主摄像头", "副画面 1"], window.BarcodeRecognitionChannelChoices.Select(o => o.Name));

            window.AddOverlayChannelCommand.Execute(null);

            Assert.Equal(2, config.CameraChannels.Count);
            Assert.Equal(2, window.OverlayCameraCards.Count);
            // 到达上限后不能再加
            Assert.False(window.CanAddOverlayChannel);
            Assert.Equal(2, window.OverlayCameraCards[1].Number);

            // 识别来源指向第二路，删掉第一路以后要跟着前移
            config.CameraBarcodeRecognitionChannel = 2;
            config.CameraChannels[1].SourceKind = "usb";
            config.CameraChannels[1].MonikerString = "moniker-c";
            window.OverlayCameraCards[1].SelectedDevice =
                window.OverlayCameraCards[1].DeviceChoices.First(choice => choice.Moniker == "moniker-c");
            Assert.Equal(2, config.CameraBarcodeRecognitionChannel);

            window.RemoveOverlayChannel(window.OverlayCameraCards[0]);

            Assert.Single(window.OverlayCameraCards);
            Assert.Equal(1, config.CameraBarcodeRecognitionChannel);
            Assert.Equal("moniker-c", config.CameraChannels[0].MonikerString);
        });
    }

    /// <summary>配置里的通道数决定卡片数：两路就是两张卡，界面不写死"副摄"。</summary>
    [Fact]
    public void CardsFollowTheConfiguredChannelCount()
    {
        RunOnStaThread(() =>
        {
            AppConfig config = CreateConfig(mainMoniker: "moniker-a", channelMoniker: "moniker-b");
            config.CameraChannels.Add(new CameraChannelConfig
            {
                SourceKind = "usb",
                MonikerString = "moniker-c",
                FrameFps = 15
            });

            SettingsWindow window = CreateWindow(config);
            PrepareWindow(window);

            Assert.Equal(2, window.OverlayCameraCards.Count);
            Assert.Equal("副画面 1", window.OverlayCameraCards[0].Title);
            Assert.Equal("副画面 2", window.OverlayCameraCards[1].Title);
            Assert.Equal("moniker-b", window.OverlayCameraCards[0].SelectedDevice?.Moniker);
            Assert.Equal("moniker-c", window.OverlayCameraCards[1].SelectedDevice?.Moniker);
            // 两路各自排除别人占用的设备，但自己那台一定还在自己的清单里
            Assert.Contains(window.OverlayCameraCards[0].DeviceChoices, choice => choice.Moniker == "moniker-b");
            Assert.DoesNotContain(window.OverlayCameraCards[0].DeviceChoices, choice => choice.Moniker == "moniker-c");
            Assert.Contains(window.OverlayCameraCards[1].DeviceChoices, choice => choice.Moniker == "moniker-c");
        });
    }

    /// <summary>
    /// 没 Show 过的窗口里，XAML 挂的绑定不会自己 attach（绑定创建时 DataContext 还没到位），
    /// 所以主摄下拉按 XAML 里同样的路径重挂一遍；下方 XAML 守卫负责保证路径没走偏。
    /// 卡片本身是普通 VM 属性，测试直接读它们即可。
    /// </summary>
    private static ComboBox PrepareWindow(SettingsWindow window, bool selectConfiguredCamera = true)
    {
        var main = Assert.IsType<ComboBox>(window.FindName("CameraComboBox"));
        main.SetBinding(
            Selector.SelectedValueProperty,
            new Binding("Config.CameraIndex") { Source = window, Mode = BindingMode.TwoWay });

        // 生产里这一步由 CameraComboBox 的 Loaded 触发（见 XAML 守卫），此时清单还没填好
        window.CameraChannelCards_Loaded(main, new RoutedEventArgs());

        // 模拟冻结的加载代码：先把完整设备清单填进主摄下拉，再选中配置里那一台
        main.ItemsSource = new List<CameraInfo>
        {
            new() { Index = 0, Name = "[0] A", Moniker = "moniker-a" },
            new() { Index = 1, Name = "[1] B", Moniker = "moniker-b" },
            new() { Index = 2, Name = "[2] C", Moniker = "moniker-c" },
            new() { Index = -1, Name = "网络摄像头（手动地址）", Moniker = "network:" }
        };
        if (selectConfiguredCamera)
            main.SelectedItem = ItemOf(main, window.Config.CameraMonikerString);
        return main;
    }

    private static CameraInfo ItemOf(ComboBox combo, string moniker) =>
        MainItems(combo).First(camera => string.Equals(camera.Moniker, moniker, StringComparison.Ordinal));

    private static List<CameraInfo> MainItems(ComboBox combo) =>
        combo.Items.OfType<CameraInfo>().ToList();

    private static string SelectedMoniker(ComboBox combo) =>
        Assert.IsType<CameraInfo>(combo.SelectedItem).Moniker;

    /// <summary>这里重挂的绑定路径必须跟 XAML 一致，否则测试通过也说明不了界面。</summary>
    [Fact]
    public void SettingsXaml_DrivesChannelsFromOneTemplateAndBindingOnly()
    {
        string xaml = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "ExpressPackingMonitoring", "UI", "SettingsWindow.xaml"),
            Encoding.UTF8);

        // 主摄卡保留具名控件（冻结的设置页代码直接读它们），但不再有自己的 ItemsSource 投影；
        Assert.Contains("x:Name=\"CameraComboBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedValue=\"{Binding Config.CameraIndex}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Loaded=\"CameraChannelCards_Loaded\"", xaml, StringComparison.Ordinal);

        // 叠加画面是一套模板 + 一个列表：加第三、第四路不用改 XAML。
        Assert.Contains("ItemsControl ItemsSource=\"{Binding OverlayCameraCards}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding DeviceChoices}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedDevice, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding AddOverlayChannelCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding RemoveCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding Resolutions}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding FpsOptions}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedValue=\"{Binding RotationDegrees, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);

        // 识别来源按通道列，不再是写死的"主摄像头/副摄像头"两项。
        Assert.Contains("ItemsSource=\"{Binding BarcodeRecognitionChannelChoices}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedBarcodeRecognitionChannel, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Tag=\"secondary\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"SecondaryCameraDeviceComboBox\"", xaml, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        foreach (string startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? directory = new(Path.GetFullPath(startPath));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("ExpressPackingMonitoring repository root was not found.");
    }

    private static AppConfig CreateConfig(string mainMoniker, string channelMoniker)
    {
        var config = new AppConfig
        {
            DeploymentPreset = DeploymentPresets.RecordingWorkstation,
            CameraSourceKind = "usb",
            CameraMonikerString = mainMoniker,
            // 索引与设备标识必须一致（生产里同一台设备就是这么存的）
            CameraIndex = DeviceIndexOf(mainMoniker)
        };
        config.CameraChannels[0].SourceKind = string.IsNullOrEmpty(channelMoniker)
            ? AppConfig.OverlayChannelSourceNone
            : "usb";
        config.CameraChannels[0].MonikerString = channelMoniker;
        config.CameraChannels[0].Index = DeviceIndexOf(channelMoniker);
        AppConfig.NormalizeAfterLoad(config);
        return config;
    }

    private static int DeviceIndexOf(string moniker) => moniker switch
    {
        "moniker-a" => 0,
        "moniker-b" => 1,
        "moniker-c" => 2,
        _ => -1
    };

    private static SettingsWindow CreateWindow(AppConfig config)
    {
        var window = new SettingsWindow(
            new SettingsContext
            {
                Capabilities = SettingsCapabilities.ForPreset(config.DeploymentPreset),
                ApplyAsync = _ => Task.FromResult(true)
            },
            config,
            12d,
            "12%");
        window.Measure(new Size(1200, 900));
        window.Arrange(new Rect(0, 0, 1200, 900));
        window.UpdateLayout();
        return window;
    }

    /// <summary>
    /// WPF 控件必须在 STA 线程上创建，测试宿主默认 MTA；
    /// 设置页里选摄像头会走 async void 事件，续体得回到本线程，所以还要装上 Dispatcher 上下文。
    /// </summary>
    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current == null)
                    _ = new Application();
                LoadAppResources();
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                action();
                PumpDispatcher(TimeSpan.FromMilliseconds(600));
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "STA 线程执行超时");
        if (failure != null)
        {
            var detail = new StringBuilder();
            for (Exception? current = failure; current != null; current = current.InnerException)
                detail.AppendLine($"{current.GetType().Name}: {current.Message}");
            detail.AppendLine(failure.StackTrace);
            throw new Xunit.Sdk.XunitException($"摄像头通道验证失败：{detail}");
        }
    }

    /// <summary>把异步续体跑完（选摄像头会触发 async void 的档位加载）。</summary>
    private static void PumpDispatcher(TimeSpan duration)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < duration)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(20);
        }
    }

    /// <summary>与 App.xaml 相同的合并顺序，否则窗口里的资源键解析不到。</summary>
    private static void LoadAppResources()
    {
        string[] files =
        [
            "ColorTokens.xaml", "LightTheme.xaml", "ComboBoxTheme.xaml", "DatePickerTheme.xaml",
            "SpinBoxTheme.xaml", "TextBoxTheme.xaml", "ButtonTheme.xaml", "ScrollBarTheme.xaml",
            "FluentIcons.xaml", "SliderTheme.xaml", "MenuTheme.xaml"
        ];

        var merged = new ResourceDictionary();
        foreach (string file in files)
        {
            merged.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    $"pack://application:,,,/ExpressPackingMonitoring;component/themes/{file.ToLowerInvariant()}",
                    UriKind.Absolute)
            });
        }

        if (Application.Current != null)
            Application.Current.Resources = merged;
    }
}
