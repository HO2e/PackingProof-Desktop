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
/// 设置页两个摄像头设备下拉的互斥与选中项稳定性。
///
/// "没及时互斥"取决于主摄下拉里**当前**选中的那一台（而不是保存过的配置），
/// "选中项乱跳"取决于同步时有没有把主摄的 ItemsSource 换成一份新表；
/// 这两件事纯逻辑守卫看不出来，所以这里真的建出设置窗口、直接动两个 ComboBox。
/// </summary>
[Collection("WPF render tests")]
public sealed class CameraChannelChoiceUiTests
{
    [Fact]
    public void ChangingMainCamera_ImmediatelyDropsItFromSecondaryList_WithoutJumpingItsOwnSelection()
    {
        RunOnStaThread(() =>
        {
            AppConfig config = CreateConfig(mainMoniker: "moniker-a", secondaryMoniker: "moniker-b");
            SettingsWindow window = CreateWindow(config);
            (ComboBox main, ComboBox secondary) = PrepareCombos(window);

            Assert.Equal("moniker-a", SelectedMoniker(main));
            Assert.Equal(0, config.CameraIndex);
            // 副摄自己那台还在，主摄占着的那台不在
            Assert.Contains(SecondaryItems(secondary), c => c.Moniker == "moniker-b");
            Assert.DoesNotContain(SecondaryItems(secondary), c => c.Moniker == "moniker-a");
            Assert.Equal("moniker-b", SelectedSecondaryChoice(secondary).Moniker);

            // 主摄换成 C：不等保存、不等重开窗口，副摄列表必须立刻把 C 去掉
            main.SelectedItem = ItemOf(main, "moniker-c");

            Assert.Equal("moniker-c", SelectedMoniker(main));
            Assert.Equal(2, config.CameraIndex);
            Assert.DoesNotContain(SecondaryItems(secondary), c => c.Moniker == "moniker-c");
            Assert.Contains(SecondaryItems(secondary), c => c.Moniker == "moniker-b");

            // 来回换几次，副摄列表不能越同步越少，主摄选中项也不能被挤走
            main.SelectedItem = ItemOf(main, "moniker-a");
            main.SelectedItem = ItemOf(main, "moniker-c");
            Assert.Equal(2, SecondaryItems(secondary).Count(c => c.Kind == "usb"));
            Assert.Equal("moniker-c", SelectedMoniker(main));
            Assert.Equal("moniker-b", SelectedSecondaryChoice(secondary).Moniker);
        });
    }

    [Fact]
    public void PickingSecondary_ImmediatelyDropsItFromTheMainList()
    {
        RunOnStaThread(() =>
        {
            AppConfig config = CreateConfig(mainMoniker: "moniker-a", secondaryMoniker: "");
            SettingsWindow window = CreateWindow(config);
            (ComboBox main, ComboBox secondary) = PrepareCombos(window);

            Assert.Equal("无", SelectedSecondaryChoice(secondary).Name);
            Assert.Contains(MainItems(main), c => c.Moniker == "moniker-b");

            secondary.SelectedItem = SecondaryItems(secondary).First(c => c.Moniker == "moniker-b");

            Assert.Equal("moniker-b", config.CameraChannels[0].MonikerString);
            Assert.Equal("usb", config.CameraChannels[0].SourceKind);
            // 主摄列表立刻排除副摄刚占用的那台
            Assert.DoesNotContain(MainItems(main), c => c.Moniker == "moniker-b");
            Assert.Equal("moniker-a", SelectedMoniker(main));

            // 换回"无"以后，那台设备要能被主摄重新选到
            secondary.SelectedItem = SecondaryItems(secondary).First(c => c.Kind == "none");
            Assert.Equal(AppConfig.OverlayChannelSourceNone, config.CameraChannels[0].SourceKind);
            Assert.Contains(MainItems(main), c => c.Moniker == "moniker-b");
        });
    }

    [Fact]
    public void ConflictingSelections_KeepTheMainChoiceAndSendTheSecondaryBackToNone()
    {
        RunOnStaThread(() =>
        {
            // 历史配置里主副摄存了同一台：只能有一个结果，而且不能把主摄的选中项挪走
            AppConfig config = CreateConfig(mainMoniker: "moniker-b", secondaryMoniker: "moniker-b");
            SettingsWindow window = CreateWindow(config);
            (ComboBox main, ComboBox secondary) = PrepareCombos(window);

            Assert.Equal("moniker-b", SelectedMoniker(main));
            // 主摄那台在完整清单里排第二：没有被挤到第一台才算没跳
            Assert.Equal(1, main.SelectedIndex);

            Assert.Equal(AppConfig.OverlayChannelSourceNone, config.CameraChannels[0].SourceKind);
            Assert.Equal("", config.CameraChannels[0].MonikerString);
            Assert.Equal("无", SelectedSecondaryChoice(secondary).Name);
            Assert.DoesNotContain(SecondaryItems(secondary), c => c.Moniker == "moniker-b");
        });
    }

    /// <summary>
    /// 主摄清单是异步填进来的，填好时选中项可能压根没变（旧索引已经不在新清单里），
    /// 只靠 SelectionChanged 会漏掉这次同步，副摄下拉就会空着。
    /// </summary>
    [Fact]
    public void WhenDeviceListArrivesWithoutASelectionChange_SecondaryListStillFills()
    {
        RunOnStaThread(() =>
        {
            AppConfig config = CreateConfig(mainMoniker: "moniker-b", secondaryMoniker: "moniker-c");
            config.CameraIndex = 99; // 旧索引已经不在这次枚举出来的清单里
            SettingsWindow window = CreateWindow(config);
            (ComboBox main, ComboBox secondary) = PrepareCombos(window, selectConfiguredCamera: false);

            Assert.Equal("moniker-b", SelectedMoniker(main));
            // 主摄占着 B，副摄能选的只有 A 和 C
            Assert.Equal(2, SecondaryItems(secondary).Count(c => c.Kind == "usb"));
            Assert.Contains(SecondaryItems(secondary), c => c.Moniker == "moniker-c");
            Assert.DoesNotContain(SecondaryItems(secondary), c => c.Moniker == "moniker-b");
        });
    }

    /// <summary>
    /// 没 Show 过的窗口里，XAML 挂的绑定不会自己 attach（绑定创建时 DataContext 还没到位），
    /// 所以这里按 XAML 里同样的路径和模式重挂一遍，让控件真的走生产里的那套绑定；
    /// 下方 XAML 守卫负责保证这里重挂的路径跟 XAML 没走偏。
    /// </summary>
    private static (ComboBox Main, ComboBox Secondary) PrepareCombos(
        SettingsWindow window,
        bool selectConfiguredCamera = true)
    {
        var main = Assert.IsType<ComboBox>(window.FindName("CameraComboBox"));
        var secondary = Assert.IsType<ComboBox>(window.FindName("SecondaryCameraDeviceComboBox"));

        main.SetBinding(
            Selector.SelectedValueProperty,
            new Binding("Config.CameraIndex") { Source = window, Mode = BindingMode.TwoWay });
        secondary.SetBinding(
            ItemsControl.ItemsSourceProperty,
            new Binding(nameof(SettingsWindow.SecondaryCameraChoices)) { Source = window });
        secondary.SetBinding(
            Selector.SelectedItemProperty,
            new Binding(nameof(SettingsWindow.SelectedSecondaryCameraChoice))
            {
                Source = window,
                Mode = BindingMode.TwoWay
            });

        // 生产里这一步由 CameraComboBox 的 Loaded 触发（见 XAML 守卫），此时清单还没填好
        window.SecondaryCameraFormats_Loaded(main, new RoutedEventArgs());

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
        return (main, secondary);
    }

    private static CameraInfo ItemOf(ComboBox combo, string moniker) =>
        MainItems(combo).First(camera => string.Equals(camera.Moniker, moniker, StringComparison.Ordinal));

    private static List<CameraInfo> MainItems(ComboBox combo) =>
        combo.Items.OfType<CameraInfo>().ToList();

    private static string SelectedMoniker(ComboBox combo) =>
        Assert.IsType<CameraInfo>(combo.SelectedItem).Moniker;

    private static CameraDeviceChoice SelectedSecondaryChoice(ComboBox combo) =>
        Assert.IsType<CameraDeviceChoice>(combo.SelectedItem);

    private static List<CameraDeviceChoice> SecondaryItems(ComboBox combo) =>
        combo.Items.OfType<CameraDeviceChoice>().ToList();

    /// <summary>这里重挂的绑定路径必须跟 XAML 一致，否则测试通过也说明不了界面。</summary>
    [Fact]
    public void SettingsXaml_BindsBothCameraCombosToTheSharedChannelProperties()
    {
        string xaml = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "ExpressPackingMonitoring", "UI", "SettingsWindow.xaml"),
            Encoding.UTF8);

        Assert.Contains("x:Name=\"CameraComboBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedValue=\"{Binding Config.CameraIndex}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Loaded=\"SecondaryCameraFormats_Loaded\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SecondaryCameraDeviceComboBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding SecondaryCameraChoices}\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "SelectedItem=\"{Binding SelectedSecondaryCameraChoice, Mode=TwoWay}\"",
            xaml,
            StringComparison.Ordinal);
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

    private static AppConfig CreateConfig(string mainMoniker, string secondaryMoniker)
    {
        var config = new AppConfig
        {
            DeploymentPreset = DeploymentPresets.RecordingWorkstation,
            CameraSourceKind = "usb",
            CameraMonikerString = mainMoniker,
            // 索引与设备标识必须一致（生产里同一台设备就是这么存的）
            CameraIndex = DeviceIndexOf(mainMoniker)
        };
        config.CameraChannels[0].SourceKind = string.IsNullOrEmpty(secondaryMoniker)
            ? AppConfig.OverlayChannelSourceNone
            : "usb";
        config.CameraChannels[0].MonikerString = secondaryMoniker;
        config.CameraChannels[0].Index = DeviceIndexOf(secondaryMoniker);
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
            throw new Xunit.Sdk.XunitException($"摄像头下拉互斥验证失败：{detail}");
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
