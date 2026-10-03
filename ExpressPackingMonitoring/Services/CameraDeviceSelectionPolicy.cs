using System;
using System.Collections.Generic;
using System.Linq;

namespace ExpressPackingMonitoring.Services;

/// <summary>
/// 摄像头设备下拉的**唯一**选设备规则：拿一份完整设备清单，投影成某一"路"能选的项 ——
/// 排除其它路已占用的设备，但保留自己当前选中的那台（否则重算后选中项会被清空）。
///
/// 主摄、副摄、以后的第三/第四路都调这里，页面与 ViewModel 不再各写一套过滤逻辑；
/// 任一路换了设备，就用新的占用关系把各路各投影一次，几个下拉同时正确。
/// </summary>
internal static class CameraDeviceSelectionPolicy
{
    /// <summary>
    /// 投影某一路的可选项。<paramref name="otherMonikers"/> 是其它路占用的设备标识；
    /// 自己当前那台（<paramref name="selfMoniker"/>）即使被算进占用也保留在列表首位。
    /// </summary>
    internal static IReadOnlyList<CameraDeviceChoice> Project(
        IReadOnlyList<CameraDeviceChoice> allDevices,
        string? selfMoniker,
        IEnumerable<string?> otherMonikers)
    {
        var occupied = otherMonikers
            .Where(m => !string.IsNullOrEmpty(m))
            .Select(m => m!)
            .ToHashSet(StringComparer.Ordinal);

        var list = new List<CameraDeviceChoice>(allDevices.Count);
        foreach (CameraDeviceChoice device in allDevices)
        {
            if (device.Kind == "usb" && occupied.Contains(device.Moniker))
                continue;

            list.Add(device);
        }

        if (!string.IsNullOrEmpty(selfMoniker)
            && list.All(d => !string.Equals(d.Moniker, selfMoniker, StringComparison.Ordinal)))
        {
            CameraDeviceChoice? self = allDevices.FirstOrDefault(
                d => string.Equals(d.Moniker, selfMoniker, StringComparison.Ordinal));
            if (self != null)
                list.Insert(0, self);
        }

        return list;
    }
}
