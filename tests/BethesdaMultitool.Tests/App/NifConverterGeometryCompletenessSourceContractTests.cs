using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class NifConverterGeometryCompletenessSourceContractTests
{
    [Fact]
    public void MeshViewerDefaultOrbitIsElevatedForHorizontalRetailMeshes()
    {
        var viewer = SourceContract.ReadAppSource("npc-viewer.html");

        Assert.Contains("camera-orbit=\"180deg 75deg auto\"", viewer, StringComparison.Ordinal);
        Assert.DoesNotContain("camera-orbit=\"180deg 90deg auto\"", viewer, StringComparison.Ordinal);
    }

    [Fact]
    public void MeshViewerWorkflowCarriesExternalGeometryWarningIntoVisibleInfoBar()
    {
        var workflow = SourceContract.ReadAppSource("NifConverterWorkflowService.cs");
        var code = SourceContract.ReadAppSource("NifConverterTab.xaml.cs");
        var xaml = SourceContract.ReadAppSource("NifConverterTab.xaml");

        Assert.Contains("service.BuildViewerSceneWithDiagnostics(nifData, item.DisplayName)", workflow,
            StringComparison.Ordinal);
        Assert.Contains("build.ExternalGeometry.IncompleteWarningMessage", workflow,
            StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NifViewerGeometryWarning\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Severity=\"Warning\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SetNifViewerGeometryWarning(result.WarningMessage);", code,
            StringComparison.Ordinal);
        Assert.Contains("NifViewerGeometryWarning.IsOpen = !string.IsNullOrWhiteSpace(message);", code,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MeshViewerGlbExportPreservesBestEffortBytesButReportsIncompleteOutput()
    {
        var workflow = SourceContract.ReadAppSource("NifConverterWorkflowService.cs");
        var code = SourceContract.ReadAppSource("NifConverterTab.xaml.cs");

        Assert.Contains("service.BuildGlbWithDiagnostics(nifData, nifPath)", workflow,
            StringComparison.Ordinal);
        Assert.Contains("SetNifViewerGeometryWarning(build?.ExternalGeometry.IncompleteWarningMessage);", code,
            StringComparison.Ordinal);
        Assert.Contains("await File.WriteAllBytesAsync(file.Path, glbBytes);", code,
            StringComparison.Ordinal);
        Assert.Contains("Exported incomplete model", code, StringComparison.Ordinal);
    }
}
