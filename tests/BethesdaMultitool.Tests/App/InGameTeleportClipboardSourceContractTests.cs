using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class InGameTeleportClipboardSourceContractTests
{
    [Fact]
    public void PKeyClipboardPreservesProfilerPoseAndAppendsTeleportStatus()
    {
        var hud = SourceContract.ReadAppSource("WorldView3DControl.Hud.cs");

        SourceContract.AssertOrder(
            hud,
            "var pose = string.Format(",
            "InGameTeleportCommandFormatter.Format(",
            "InGameTeleportCommandFormatter.AppendToProfilerPose(pose, teleport)",
            "ClipboardHelper.CopyText(clipboardText)");
        Assert.DoesNotContain("ClipboardHelper.CopyText(pose)", hud, StringComparison.Ordinal);
    }
}