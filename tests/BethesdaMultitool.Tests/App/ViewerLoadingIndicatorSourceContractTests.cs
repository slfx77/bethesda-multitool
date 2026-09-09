using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

/// <summary>
///     WinUI ProgressRing visibility and animation are independent. A visible ring with IsActive
///     left false reserves layout space but gives no indication that a long model build is alive.
/// </summary>
public sealed class ViewerLoadingIndicatorSourceContractTests
{
    [Fact]
    public void MeshAndNpcModelLoadsActivateAndStopTheirProgressRings()
    {
        var meshViewer = SourceContract.ReadAppSource("NifConverterTab.xaml.cs");
        var npcViewer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Tabs", "SingleFile",
            "SingleFileTab.NpcBrowser.cs");

        AssertRingLifecycle(meshViewer, "NifModelLoadingRing");
        AssertRingLifecycle(npcViewer, "NpcModelLoadingRing");
    }

    private static void AssertRingLifecycle(string source, string ringName)
    {
        SourceContract.AssertOrder(
            source,
            $"{ringName}.IsActive = true;",
            $"{ringName}.Visibility = Visibility.Visible;",
            $"{ringName}.IsActive = false;",
            $"{ringName}.Visibility = Visibility.Collapsed;");
    }
}