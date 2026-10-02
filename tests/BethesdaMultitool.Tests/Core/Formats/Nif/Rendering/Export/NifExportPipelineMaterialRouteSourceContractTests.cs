using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

public sealed class NifExportPipelineMaterialRouteSourceContractTests
{
    [Fact]
    public void CliModernExportResolvesMaterialsBeforeChoosingHierarchyOrRigidFallback()
    {
        var pipeline = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "CLI", "Rendering", "Nif", "NifExportPipeline.cs");
        var run = pipeline[pipeline.IndexOf("internal static void Run(NifExportSettings settings)", StringComparison.Ordinal)..];

        // The scene assembly moved to Core unchanged; the CLI pipeline still owns the resolver
        // lifetime and the writer call, in this order.
        SourceContract.AssertOrder(
            run,
            "NifExportSceneAssembly.TryParseForExport(rawData, out var nifData, out var nif, out var error)",
            "using var textureResolver",
            "NifExportSceneAssembly.BuildForExport(nifData, nif, settings.InputPath, textureResolver)",
            "GlbWriter.Write(scene, textureResolver, settings.OutputPath)");

        var assembly = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Export", "NifExportSceneAssembly.cs");
        var route = assembly[assembly.IndexOf("internal static GlbScene? BuildForExport(", StringComparison.Ordinal)..];

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
