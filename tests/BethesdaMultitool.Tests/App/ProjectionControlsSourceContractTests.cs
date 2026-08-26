using BethesdaMultitool.Core.Ui;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class ProjectionControlsSourceContractTests
{
    [Fact]
    public void ProjectionRotateButtonsHaveAReadableBackdrop()
    {
        var xaml = SourceContract.ReadAppSource("WorldView3DControl.xaml");
        SourceContract.AssertOrder(
            xaml,
            "<Border x:Name=\"ProjectionSnapPanel\"",
            "Background=\"#D8101014\" Visibility=\"Collapsed\"",
            "x:Name=\"RotateLeftButton\"",
            "x:Name=\"RotateRightButton\"");
    }

    [Fact]
    public void ProjectionRotationGestureIsDiscoverable()
    {
        var settings = SourceContract.ReadAppSource("WorldView3DSettingsPanel.xaml");
        Assert.Contains("Shift+drag to rotate", settings, StringComparison.Ordinal);

        var hud = SourceContract.ReadAppSource("WorldView3DControl.Hud.cs");
        Assert.Contains("? \"drag pan   Shift+drag rotate", hud, StringComparison.Ordinal);

        // The shortcut rows moved to Core as KeyboardShortcutRegistry, so this is now a value
        // assertion rather than a grep over the dialog's source.
        Assert.Contains(
            KeyboardShortcutRegistry.ForGroup(KeyboardShortcutRegistry.WorldViewer3DGroup),
            shortcut => shortcut.Keys == "Shift+Mouse drag"
                        && shortcut.Action == "Rotate a projection view");
    }
}