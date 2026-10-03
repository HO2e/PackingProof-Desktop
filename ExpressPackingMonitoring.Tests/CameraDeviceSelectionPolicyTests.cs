using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

/// <summary>
/// 摄像头设备下拉的共用规则：主摄/副摄互相排除对方占用的设备、
/// 自己当前那台保留在列表里、被占用或消失时要退回"无"。
/// 主摄、副摄（以及以后的第三第四路）都调这一份，这里把规则钉死。
/// </summary>
public sealed class CameraDeviceSelectionPolicyTests
{
    private static readonly CameraDeviceChoice None = new("无", "none", "", -1);
    private static readonly CameraDeviceChoice CamA = new("A", "usb", "moniker-a", 0);
    private static readonly CameraDeviceChoice CamB = new("B", "usb", "moniker-b", 1);
    private static readonly CameraDeviceChoice Network = new("网络摄像头", "network", "", -1);
    private static readonly CameraDeviceChoice[] All = [None, CamA, CamB, Network];

    [Fact]
    public void Project_ExcludesDevicesTakenByOtherChannels()
    {
        IReadOnlyList<CameraDeviceChoice> forSecondary =
            CameraDeviceSelectionPolicy.Project(All, selfMoniker: "", otherMonikers: ["moniker-a"]);

        Assert.DoesNotContain(forSecondary, c => c.Moniker == "moniker-a");
        Assert.Contains(forSecondary, c => c.Moniker == "moniker-b");
        Assert.Contains(forSecondary, c => c.Kind == "network");
        Assert.Contains(forSecondary, c => c.Kind == "none");
    }

    [Fact]
    public void Project_KeepsOwnCurrentDeviceEvenIfListedAsTaken()
    {
        IReadOnlyList<CameraDeviceChoice> forSecondary =
            CameraDeviceSelectionPolicy.Project(All, selfMoniker: "moniker-a", otherMonikers: ["moniker-a"]);

        Assert.Contains(forSecondary, c => c.Moniker == "moniker-a");
    }

    [Theory]
    [InlineData("", "moniker-a", true)]          // 没选设备：无需回退
    [InlineData("moniker-a", "moniker-a", false)] // 被另一路占用：必须回退
    [InlineData("moniker-c", "moniker-a", false)] // 设备已不存在：必须回退
    [InlineData("moniker-b", "moniker-a", true)]  // 仍然可用：保留
    public void CanKeepSelection_DecidesWhetherToFallBackToNone(
        string selfMoniker,
        string otherMoniker,
        bool expected)
    {
        Assert.Equal(
            expected,
            CameraDeviceSelectionPolicy.CanKeepSelection(selfMoniker, [otherMoniker], All));
    }
}
