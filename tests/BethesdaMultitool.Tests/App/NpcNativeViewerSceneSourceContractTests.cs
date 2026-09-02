using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class NpcNativeViewerSceneSourceContractTests
{
    private static string ServiceSource() => SourceContract.ReadSource(
        "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Npc",
        "NpcBrowserService.cs");

    [Fact]
    public void NpcAndCreatureCompositionEnterNativeSceneBeforeGlbSerialization()
    {
        var source = ServiceSource();
        var npcScene = SourceContract.Extract(
            source,
            "public BethesdaViewerScene? BuildViewerScene(",
            "public BethesdaViewerScene? BuildCreatureViewerScene(");
        var creatureScene = SourceContract.Extract(
            source,
            "public BethesdaViewerScene? BuildCreatureViewerScene(",
            "public byte[] ExportViewerSceneToGlb(");
        var npcGlb = SourceContract.Extract(
            source,
            "public byte[]? BuildGlb(",
            "public byte[]? BuildCreatureGlb(");
        var creatureGlb = SourceContract.Extract(
            source,
            "public byte[]? BuildCreatureGlb(",
            "public byte[]? RenderPng(");

        SourceContract.AssertOrder(
            npcScene,
            "ResolveAppearance(npcFormId)",
            "NpcCompositionPlanner.CreatePlan(",
            "NpcCompositionExportAdapter.BuildNpc(",
            "BethesdaViewerSceneGlbAdapter.FromGlbScene(",
            "BethesdaViewerScenePurpose.NpcAppearance",
            "game: _game",
            "CaptureReferencedGeneratedTextures(viewerScene, appearance)",
            "NpcBoundaryVertexStitcher.PopulateViewerSceneBoundaryGroups(viewerScene)");
        SourceContract.AssertOrder(
            creatureScene,
            "CreatureCompositionPlanner.CreatePlan(",
            "NpcCompositionExportAdapter.BuildCreature(",
            "BethesdaViewerSceneGlbAdapter.FromGlbScene(",
            "BethesdaViewerScenePurpose.CreatureAppearance",
            "game: _game",
            "textureSourcePaths: _textureSourcePaths",
            "NpcBoundaryVertexStitcher.PopulateViewerSceneBoundaryGroups(viewerScene)");
        SourceContract.AssertOrder(
            npcGlb,
            "BuildViewerScene(",
            "ExportViewerSceneToGlb(scene)");
        SourceContract.AssertOrder(
            creatureGlb,
            "BuildCreatureViewerScene(",
            "ExportViewerSceneToGlb(scene)");
    }

    [Fact]
    public void ActorSceneOwnsOnlyReferencedGeneratedEgtPayloads()
    {
        var source = ServiceSource();
        var capture = SourceContract.Extract(
            source,
            "private void CaptureReferencedGeneratedTextures(",
            "private static string BuildNpcSourceLabel(");
        var export = SourceContract.Extract(
            source,
            "public byte[] ExportViewerSceneToGlb(",
            "public byte[]? BuildGlb(");

        Assert.Contains("meshPart.Submesh.DiffuseTexturePath", capture, StringComparison.Ordinal);
        Assert.Contains("BuildNpcFaceEgtTextureKey(appearance)", capture, StringComparison.Ordinal);
        Assert.Equal(3, SourceContract.CountOccurrences(capture, "BuildNpcBodyEgtTextureKey("));
        SourceContract.AssertOrder(
            capture,
            "referencedDiffusePaths.Contains",
            "_textureResolver.GetTexture(textureKey)",
            "scene.AddGeneratedTexture(textureKey, texture)");

        SourceContract.AssertOrder(
            export,
            "scene.GeneratedTextures",
            "_textureResolver.InjectTexture(texturePath, texture)",
            "BethesdaViewerSceneGlbAdapter.ToGlbScene(scene)",
            "GlbWriter.WriteToBytes(exportScene, _textureResolver)");
    }

    [Fact]
    public void WorkflowExposesNativeSceneWithoutBreakingGlbCallers()
    {
        var workflow = SourceContract.ReadAppSource("NpcBrowserWorkflowService.cs");
        var native = SourceContract.Extract(
            workflow,
            "internal static Task<BethesdaViewerScene?> BuildViewerSceneAsync(",
            "internal static async Task<byte[]?> BuildGlbAsync(");
        var compatibility = SourceContract.Extract(
            workflow,
            "internal static async Task<byte[]?> BuildGlbAsync(",
            "internal static async Task ExportGlbAsync(");

        Assert.Contains("service.BuildCreatureViewerScene(", native, StringComparison.Ordinal);
        Assert.Contains("service.BuildViewerScene(", native, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            compatibility,
            "BuildViewerSceneAsync(service, npc, options)",
            "service.ExportViewerSceneToGlb(scene)");

        Assert.Contains(
            "GameProfiles.ResolveByNames([pluginName]) ?? BethesdaGame.Unknown",
            ServiceSource(),
            StringComparison.Ordinal);
    }
}
