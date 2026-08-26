using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

/// <summary>
///     Source contracts for the R "reset view" keybind shared by the 3D world viewer and the 2D map.
///     Both handlers live in the App TFM (not built by the test target), so these pins read the
///     source text directly. They exist to keep the two viewers on ONE framing entry point apiece —
///     a reset that re-derives its own camera/pan math would silently drift from the framing the
///     load path and the toolbar's Zoom-to-Fit apply.
/// </summary>
public sealed class ResetViewKeybindSourceContractTests
{
    private static string Read3DInput()
    {
        return SourceContract.ReadAppSource("WorldView3DControl.Input.cs");
    }

    private static string ReadMapInput()
    {
        return SourceContract.ReadAppSource("WorldMapControl.Input.cs");
    }

    [Fact]
    public void ThreeDViewerHandlesRAndCallsTheSharedFramingEntryPoint()
    {
        var source = Read3DInput();

        // R is dispatched, text-entry guarded, first-press guarded, and lands on the reset method.
        SourceContract.AssertOrder(source,
            "if (e.Key == VirtualKey.R && !TextEntryFocusGuard.IsTextEntryFocused(XamlRoot))",
            "if (_toggleKeysDown.Add(e.Key)) ResetViewToSceneFraming();",
            "private void ResetViewToSceneFraming()",
            "ResetCameraToDataCentroid();");

        // Ortho/projection modes frame from the ortho focus, not the camera — the reset must re-seed
        // it, again through the existing seeding method.
        Assert.Contains("if (ProjectionActive) InitProjectionFocusFromCamera();",
            source, StringComparison.Ordinal);
    }

    [Fact]
    public void ThreeDResetDoesNotReimplementCameraFraming()
    {
        var source = Read3DInput();
        var body = SourceContract.Extract(source,
            "private void ResetViewToSceneFraming()",
            "private void OnRenderPanelKeyUp");

        // The whole point: delegate to the load-path framing. No hand-rolled pose assignment here.
        Assert.DoesNotContain("_camera.Position =", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_camera.Yaw =", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_camera.Pitch =", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MapHandlesRAndCallsTheSharedFramingEntryPoint()
    {
        var source = ReadMapInput();

        SourceContract.AssertOrder(source,
            "if (e.Key == Windows.System.VirtualKey.R && !TextEntryFocusGuard.IsTextEntryFocused(XamlRoot))",
            "ResetViewToActiveExtent();",
            "internal void ResetViewToActiveExtent()");
    }

    [Fact]
    public void MapResetReusesTheZoomToFitEntryPoints()
    {
        var source = ReadMapInput();
        var body = SourceContract.Extract(source,
            "internal void ResetViewToActiveExtent()",
            "private void MapCanvas_KeyUp");

        // Cell-detail frames the open cell; overview frames the active worldspace. Both go through
        // the existing helpers so ITEM-1-style centring changes apply to the keybind for free.
        SourceContract.AssertOrder(body,
            "WorldMapViewportHelper.ZoomToFitCell(",
            "ApplyZoomToFitWorldspace();");
        Assert.Contains("MapCanvas.Invalidate();", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MapToolbarZoomFitDelegatesToTheSameEntryPoint()
    {
        // One framing path for the button and the key — a second copy would be free to drift.
        var navigation = SourceContract.ReadAppSource("WorldMapControl.Navigation.cs");
        Assert.Contains(
            "private void ZoomFit_Click(object sender, RoutedEventArgs e) => ResetViewToActiveExtent();",
            navigation,
            StringComparison.Ordinal);
    }

    // "Both resets appear in the F1 list" is now asserted by value in
    // KeyboardShortcutRegistryTests.ResetViewChord_IsDocumentedForBothViewers, which also checks
    // that the 3D handler actually handles every letter chord the dialog advertises.
}