using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Localization;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Documents;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class LocalizationTests
{
    [Theory]
    [InlineData("zh-CN", AppLanguage.Chinese)]
    [InlineData("zh-TW", AppLanguage.Chinese)]
    [InlineData("ja-JP", AppLanguage.Japanese)]
    [InlineData("en-US", AppLanguage.English)]
    [InlineData("fr-FR", AppLanguage.English)]
    public void Resolve_AutoUsesChineseFamilyAndFallsBackToEnglish(string culture, string expected)
    {
        Assert.Equal(expected, AppLanguage.Resolve(AppLanguage.Auto, CultureInfo.GetCultureInfo(culture)));
    }

    [Fact]
    public void NormalizeAfterLoad_SelectsJapaneseVoicesForJapaneseLanguage()
    {
        var config = new AppConfig { Language = AppLanguage.Japanese };

        AppConfig.NormalizeAfterLoad(config);

        Assert.Equal("ja-JP-NanamiNeural", config.EdgeTtsVoiceJaJp);
        Assert.Equal("ja-JP-KeitaNeural", config.EdgeTtsWarningVoiceJaJp);
        Assert.Equal(config.EdgeTtsVoiceJaJp, config.EdgeTtsVoice);
        Assert.Equal(config.EdgeTtsWarningVoiceJaJp, config.EdgeTtsWarningVoice);
        Assert.DoesNotContain("zh-CN", config.EdgeTtsVoice, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeAfterLoad_MigratesLegacyVoicesAndInvalidLanguage()
    {
        var config = new AppConfig
        {
            Language = "invalid",
            EdgeTtsVoice = "zh-CN-XiaoyiNeural",
            EdgeTtsWarningVoice = "zh-CN-YunxiNeural",
            EdgeTtsVoiceZhHans = "",
            EdgeTtsWarningVoiceZhHans = ""
        };

        Assert.True(AppConfig.NormalizeAfterLoad(config));
        Assert.Equal(AppLanguage.Auto, config.Language);
        Assert.Equal("zh-CN-XiaoyiNeural", config.EdgeTtsVoiceZhHans);
        Assert.Equal("zh-CN-YunxiNeural", config.EdgeTtsWarningVoiceZhHans);
        Assert.Equal("en-US-JennyNeural", config.EdgeTtsVoiceEnUs);
    }

    [Fact]
    public void Resources_ContainEnglishDefaultAndChineseSatelliteValues()
    {
        Assert.Equal("Settings", AppLanguage.Get("设置", CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("设置", AppLanguage.Get("设置", CultureInfo.GetCultureInfo("zh-Hans")));
        Assert.Equal("設定", AppLanguage.Get("设置", CultureInfo.GetCultureInfo("ja-JP")));
        Assert.Equal("Recording started", AppLanguage.Get("Speech.StartRecording", CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("开始录制", AppLanguage.Get("Speech.StartRecording", CultureInfo.GetCultureInfo("zh-Hans")));
        Assert.Equal("録画を開始しました", AppLanguage.Get("Speech.StartRecording", CultureInfo.GetCultureInfo("ja-JP")));
    }

    /// <summary>
    /// 默认资源（英文）里每一把键都必须在 zh-Hans 卫星资源里有对应条目。
    /// 只加英文不加中文时，中文界面会回退到中性资源，也就是直接显示英文。
    /// </summary>
    [Fact]
    public void Resources_EveryDefaultKeyHasChineseEntry()
    {
        string projectPath = Path.Combine(FindRepositoryRoot(), "ExpressPackingMonitoring");
        string defaultPath = Path.Combine(projectPath, "Resources", "Strings.resx");
        string chinesePath = Path.Combine(projectPath, "Resources", "Strings.zh-Hans.resx");

        string[] missing = ReadKeys(defaultPath)
            .Except(ReadKeys(chinesePath), StringComparer.Ordinal)
            .ToArray();

        Assert.True(missing.Length == 0, "Missing zh-Hans resources: " + string.Join(" | ", missing));
    }

    /// <summary>
    /// 日语同样依赖卫星资源：缺词条时会回退到中性的英文资源。
    /// </summary>
    [Fact]
    public void Resources_EveryDefaultKeyHasJapaneseEntry()
    {
        string projectPath = Path.Combine(FindRepositoryRoot(), "ExpressPackingMonitoring");
        string defaultPath = Path.Combine(projectPath, "Resources", "Strings.resx");
        string japanesePath = Path.Combine(projectPath, "Resources", "Strings.ja-JP.resx");

        string[] missing = ReadKeys(defaultPath)
            .Except(ReadKeys(japanesePath), StringComparer.Ordinal)
            .ToArray();

        Assert.True(missing.Length == 0, "Missing ja-JP resources: " + string.Join(" | ", missing));
    }

    [Fact]
    public void WpfViews_AllStaticChineseTextHasEnglishResource()
    {
        string projectPath = Path.Combine(FindRepositoryRoot(), "ExpressPackingMonitoring");
        string[] views = Directory.GetFiles(projectPath, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Themes{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith($"{Path.DirectorySeparatorChar}App.xaml", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var values = views
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), "(?:Text|Content|Header|Title|ToolTip)=\"([^\"]*[\\p{IsCJKUnifiedIdeographs}][^\"]*)\"")
                .Select(match => match.Groups[1].Value))
            .Where(value => !value.StartsWith("{Binding ", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var english = CultureInfo.GetCultureInfo("en-US");
        string[] missing = values
            .Where(value => Regex.IsMatch(AppLanguage.Get(value, english), "[\\p{IsCJKUnifiedIdeographs}]"))
            .ToArray();

        Assert.True(missing.Length == 0, "Missing English resources: " + string.Join(" | ", missing));
    }

    [Fact]
    public void WpfViews_StaticVisibleTextDoesNotEndWithChineseFullStop()
    {
        string projectPath = Path.Combine(FindRepositoryRoot(), "ExpressPackingMonitoring");
        string[] views = Directory.GetFiles(projectPath, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Themes{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith($"{Path.DirectorySeparatorChar}App.xaml", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        string[] xamlViolations = views
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), "(?:Text|Content|Header|Title|ToolTip)=\"([^\"]*)\"")
                .Select(match => match.Groups[1].Value))
            .Where(value => value.EndsWith("。", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.True(xamlViolations.Length == 0, "XAML 可见文案不得以句号结尾: " + string.Join(" | ", xamlViolations));

        string zhResxPath = Path.Combine(projectPath, "Resources", "Strings.zh-Hans.resx");
        string[] resxViolations = Regex.Matches(File.ReadAllText(zhResxPath), "<value>([^<]*)</value>")
            .Select(match => match.Groups[1].Value)
            .Where(value => value.EndsWith("。", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.True(resxViolations.Length == 0, "zh-Hans 资源值不得以句号结尾: " + string.Join(" | ", resxViolations));
    }

    [Fact]
    public void ToastLiterals_AllHaveEnglishResources()
    {
        string projectPath = Path.Combine(FindRepositoryRoot(), "ExpressPackingMonitoring");
        string[] sourceFiles = Directory.GetFiles(projectPath, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        string[] values = sourceFiles
            .SelectMany(path => Regex.Matches(
                    File.ReadAllText(path),
                    "ShowToast\\(\\s*\"([^\"\\r\\n]*[\\p{IsCJKUnifiedIdeographs}][^\"\\r\\n]*)\"\\s*\\)")
                .Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var english = CultureInfo.GetCultureInfo("en-US");
        string[] missing = values
            .Where(value => Regex.IsMatch(AppLanguage.Get(value, english), "[\\p{IsCJKUnifiedIdeographs}]"))
            .ToArray();

        Assert.True(missing.Length == 0, "Missing English toast resources: " + string.Join(" | ", missing));
    }

    private static string FindRepositoryRoot()
    {
        foreach (string startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(startPath));
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ExpressPackingMonitoring.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("ExpressPackingMonitoring repository root was not found.");
    }

    private static string[] ReadKeys(string path) =>
        Regex.Matches(File.ReadAllText(path), "<data name=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void TextBlockLocalization_DistinguishesTextPropertyFromExplicitInlines()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var ordinaryText = new TextBlock { Text = "摄像头" };
                var inlineText = new TextBlock();
                inlineText.Inlines.Add(new Run("今日:"));
                inlineText.Inlines.Add(new Run("0"));
                var businessDataContainer = new StackPanel();
                WpfLocalization.SetAutoLocalize(businessDataContainer, false);
                var businessDataText = new TextBlock { Text = "开始录制" };
                businessDataContainer.Children.Add(businessDataText);

                Assert.True(WpfLocalization.ShouldLocalizeTextProperty(ordinaryText));
                Assert.False(WpfLocalization.ShouldLocalizeTextProperty(inlineText));
                Assert.False(WpfLocalization.ShouldLocalizeTextProperty(businessDataText));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw failure;
    }

}
