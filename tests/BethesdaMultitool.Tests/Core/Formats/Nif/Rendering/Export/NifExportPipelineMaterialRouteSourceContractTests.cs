using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

public sealed class NifExportPipelineMaterialRouteSourceContractTests
{
    [Fact]
    public void CliModernExportResolvesMaterialsBeforeChoosingHierarchyOrRigidFallback()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "CLI", "Rendering", "Nif", "NifExportPipeline.cs");
        var run = SourceContract.Extract(
            source,
            "internal static void Run(NifExportSettings settings)",
            "private static GlbScene? BuildScene(");
        var route = source[source.IndexOf("private static GlbScene? BuildScene(", StringComparison.Ordinal)..];

        SourceContract.AssertOrder(
            run,
            "using var textureResolver",
            "BuildScene(nifData, nif, settings.InputPath, textureResolver)",
            "GlbWriter.Write(scene, textureResolver, settings.OutputPath)");
        SourceContract.AssertOrder(
            route,
            "NifGeometryExtractor.Extract(data, nif, textureResolver)",
            "Fo76BsSkinBindingExtractor.IsCandidate(data, nif, shapeIndex)",
            "NifExportSceneBuilder.Build(data, nif, sourceLabel)",
            "NifExportSceneBuilder.ApplyModernMaterialState(hierarchyScene, modernModel)",
            "return hierarchyScene;",
            "NifExportSceneBuilder.BuildRenderableModel(modernModel, sourceLabel)");
    }
}
