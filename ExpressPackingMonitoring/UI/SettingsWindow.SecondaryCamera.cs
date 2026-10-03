using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using AForge.Video.DirectShow;
using ExpressPackingMonitoring.Config;

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
