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

        var shortcuts = SourceContract.ReadAppSource("KeyboardShortcutsDialog.xaml.cs");
        Assert.Contains("\"Shift+Mouse drag\", \"Rotate a projection view\"", shortcuts,
            StringComparison.Ordinal);
    }
}
