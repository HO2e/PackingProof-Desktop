using System;
using System.Collections.Generic;
using AForge.Video.DirectShow;
using ExpressPackingMonitoring.Config;

namespace ExpressPackingMonitoring.Services;

/// <summary>可选摄像头的一项：无 / 本机某台摄像头 / 网络摄像头。</summary>
public sealed record CameraDeviceChoice(string Name, string Kind, string Moniker, int Index)
{
    public override string ToString() => Name;
}

/// <summary>
/// 可选摄像头列表的**唯一**构造入口：主摄、副摄（以及以后的第三、第四路）都从这里取，
/// 各页面/ViewModel 不再各写一遍 FilterInfoCollection 和"无/网络摄像头"的拼装。
/// </summary>
internal static class CameraDeviceCatalog
{
    /// <summary>
    /// 列出可选摄像头：先"无"，再本机设备（<paramref name="excludeMoniker"/> 那台会被剔除，
    /// 避免和已占用的那一路撞设备），最后"网络摄像头"。
    /// 枚举失败时至少还有"无"和"网络摄像头"，不影响界面打开。
    /// </summary>
    internal static IReadOnlyList<CameraDeviceChoice> BuildChoices(string? excludeMoniker)
    {
        var choices = new List<CameraDeviceChoice>
        {
            new("无", AppConfig.OverlayChannelSourceNone, "", -1)
        };

        try
        {
            var devices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
            for (int i = 0; i < devices.Count; i++)
            {
                if (!string.IsNullOrEmpty(excludeMoniker)
                    && string.Equals(devices[i].MonikerString, excludeMoniker, StringComparison.Ordinal))
                {
                    continue;
                }

                choices.Add(new CameraDeviceChoice(devices[i].Name, "usb", devices[i].MonikerString, i));
            }
        }
        catch (Exception ex)
        {
            Logging.RuntimeLog.Warn("Camera", $"枚举摄像头列表失败：{ex.Message}");
        }

        choices.Add(new CameraDeviceChoice("网络摄像头", "network", "", -1));
        return choices;
    }
}
