using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class NpcNativeViewerSceneSourceContractTests
{
    private static string ServiceSource()
    {
        return SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Npc",
            "NpcBrowserService.cs");
    }

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
            "ResolveAppearance(npcFormId, previewPlayerLevel, generation)",
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
        var npcScene = SourceContract.Extract(
            source,
            "public BethesdaViewerScene? BuildViewerScene(",
            "public BethesdaViewerScene? BuildCreatureViewerScene(");
        var capture = SourceContract.Extract(
            source,
            "private void CaptureReferencedGeneratedTextures(",
            "private void EvictNpcGeneratedTextures(");
        var export = SourceContract.Extract(
            source,
            "public byte[] ExportViewerSceneToGlb(",
            "public byte[]? BuildGlb(");

        Assert.Contains("meshPart.Submesh.DiffuseTexturePath", capture, StringComparison.Ordinal);
        Assert.Contains("NpcTextureHelpers.BuildNpcGeneratedTextureKeys(appearance)", capture,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            capture,
            "referencedDiffusePaths.Contains",
            "_textureResolver.GetTexture(textureKey)",
            "scene.AddGeneratedTexture(textureKey, texture)");
        SourceContract.AssertOrder(
            npcScene,
            "NpcCompositionExportAdapter.BuildNpc(",
            "CaptureReferencedGeneratedTextures(viewerScene, appearance)",
            "return viewerScene",
            "finally",
            "EvictNpcGeneratedTextures(appearance)");

        SourceContract.AssertOrder(
            export,
            "scene.GeneratedTextures",
            "_textureResolver.InjectTexture(texturePath, texture)",
            "BethesdaViewerSceneGlbAdapter.ToGlbScene(scene)",
            "GlbWriter.WriteToBytes(exportScene, _textureResolver)",
            "finally",
            "scene.GeneratedTextures.Keys",
            "_textureResolver.EvictTexture(texturePath)");
    }

    [Fact]
    public void GeneratedNpcTexturesAreEvictedAcrossBrowserAndCliOutputPaths()
    {
        var service = ServiceSource();
        var renderPng = SourceContract.Extract(
            service,
            "public byte[]? RenderPng(",
            "public async Task BatchExportGlbAsync(");
        var batchGlb = SourceContract.Extract(
            service,
            "public async Task BatchExportGlbAsync(",
            "public async Task BatchRenderPngAsync(");
        var batchPng = SourceContract.Extract(
            service,
            "public async Task BatchRenderPngAsync(",
            "private NpcAppearance? ResolveAppearance(");
        var cliExportSource = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "CLI", "Rendering", "Npc", "NpcExportPipeline.cs");
        var cliNpcExport = SourceContract.Extract(cliExportSource, "// Export NPCs", "// Export creatures");
        var cliRender = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "CLI", "Rendering", "Npc", "NpcRenderPipeline.cs");
        var cpuRender = SourceContract.Extract(
            cliRender,
            "private static void RenderNpcsCpu(",
            "private static void RenderNpcsPipelinedGpu(");
        var gpuRender = SourceContract.Extract(
            cliRender,
            "private static void RenderNpcsPipelinedGpu(",
            "private static NifRenderableModel? BuildNpcModel(");
        var cliEviction = SourceContract.Extract(
            cliRender,
            "private static void EvictNpcTextures(",
            "private sealed class NpcGpuRenderResources");

        SourceContract.AssertOrder(
            renderPng,
            "NpcCompositionRenderAdapter.BuildNpc(",
            "PngWriter.EncodeRgba(",
            "finally",
            "EvictNpcGeneratedTextures(appearance)");
        SourceContract.AssertOrder(
            batchGlb,
            "NpcCompositionExportAdapter.BuildNpc(",
            "GlbWriter.Write(",
            "finally",
            "EvictNpcGeneratedTextures(npc)");
        SourceContract.AssertOrder(
            batchPng,
            "NpcCompositionRenderAdapter.BuildNpc(",
            "PngWriter.SaveRgba(",
            "finally",
            "EvictNpcGeneratedTextures(npc)");
        SourceContract.AssertOrder(
            cliNpcExport,
            "NpcCompositionExportAdapter.BuildNpc(",
            "GlbWriter.Write(",
            "finally",
            "NpcTextureHelpers.BuildNpcGeneratedTextureKeys(npc)");
        SourceContract.AssertOrder(cpuRender, "finally", "EvictNpcTextures(textureResolver, npc)");
        SourceContract.AssertOrder(
            gpuRender,
            "try",
            "if (!currentModelPrepared)",
            "BuildNpcModel(npc, meshArchives, textureResolver, caches, settings)",
            "finally",
            "EvictNpcTextures(textureResolver, npc)",
            "NpcTextureHelpers.BuildNpcGeneratedTextureKeys(npc)",
            "gpuRenderer.EvictTexture(textureKey)");
        Assert.Contains("NpcTextureHelpers.BuildNpcGeneratedTextureKeys(npc)", cliEviction,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OblivionEarEgtAndFacePartNormalsFlowIntoBothNpcAdapters()
    {
        var planner = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Npc",
            "Composition", "NpcCompositionPlanner.cs");
        var headPlan = SourceContract.Extract(
            planner,
            "private static NpcHeadCompositionPlan BuildHeadPlan(",
            "internal static IReadOnlyList<NpcBodyMeshPlan> BuildBodyParts(");
        var exportHead = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Npc",
            "Assembly", "NpcExportHeadAssembler.cs");
        var renderHead = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "CLI", "Rendering", "Npc", "NpcHeadPartAttacher.cs");

        SourceContract.AssertOrder(
            headPlan,
            "var effectiveEarTexturePath = npc.EarTexturePath",
            "npc.Game == BethesdaGame.Oblivion",
            "NpcEarTextureComposer.Resolve(",
            "map1Source={4} map1EffectivePath={5}",
            "EffectiveEarTexturePath = effectiveEarTexturePath");
        Assert.Contains("headPlan.EffectiveEarTexturePath", exportHead, StringComparison.Ordinal);
        Assert.Contains("OblivionNpcFacePartMaterialResolver.ApplyClassicSkin2000(", exportHead,
            StringComparison.Ordinal);
        Assert.Contains("OblivionNpcFacePartMaterialResolver.ApplyClassicSkin2000(", renderHead,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AuthoredFaceTextureSourceFlowsThroughSharedCliAndActorsCompositionPlan()
    {
        var planner = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Npc",
            "Composition", "NpcCompositionPlanner.cs");
        var cli = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "CLI", "Rendering", "Npc", "NpcRenderPipeline.cs");
        var renderAdapter = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Npc",
            "Composition", "NpcCompositionRenderAdapter.cs");
        var headBuilder = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "CLI", "Rendering", "Npc", "NpcHeadBuilder.cs");
        var exportHead = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Npc",
            "Assembly", "NpcExportHeadAssembler.cs");

        SourceContract.AssertOrder(
            cli,
            "NpcCompositionPlanner.CreatePlan(",
            "NpcCompositionRenderAdapter.BuildNpc(plan");
        SourceContract.AssertOrder(
            ServiceSource(),
            "NpcCompositionPlanner.CreatePlan(",
            "NpcCompositionExportAdapter.BuildNpc(");
        Assert.Contains("EffectiveHeadTextureSource = headTexture.Source", planner, StringComparison.Ordinal);
        Assert.Contains("map1Source={4} map1EffectivePath={5}", planner, StringComparison.Ordinal);
        Assert.Contains("var classicSkin2000 = npc.Game == BethesdaGame.Oblivion", headBuilder,
            StringComparison.Ordinal);
        Assert.Contains("submesh.IsFaceGen = classicSkin2000", headBuilder,
            StringComparison.Ordinal);
        Assert.Contains("part.Submesh.IsFaceGen = classicSkin2000", exportHead,
            StringComparison.Ordinal);
        Assert.Contains("FaceGenHeadShaderFamilyResolver.ApplyClassicSkin2000Material(", headBuilder,
            StringComparison.Ordinal);
        Assert.Contains("FaceGenHeadShaderFamilyResolver.ApplyClassicSkin2000Material(", exportHead,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "EffectiveHeadTextureSource != NpcHeadTextureSource.BaseDiffuse",
            headBuilder + exportHead,
            StringComparison.Ordinal);
        Assert.Contains("NpcHeadBuilder.BuildFromPlan(plan", renderAdapter, StringComparison.Ordinal);
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
        Assert.Contains("options.PreviewPlayerLevel", native, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            compatibility,
            "BuildViewerSceneAsync(service, npc, options)",
            "service.ExportViewerSceneToGlb(scene)");

        Assert.Contains(
            "GameProfiles.ResolveByNames([pluginName]) ?? BethesdaGame.Unknown",
            ServiceSource(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ActorPreviewLevelIsVisibleAndFlowsThroughEveryNpcOutputPath()
    {
        var host = SourceContract.ReadAppSource("SingleFileTab.NpcBrowser.cs");
        var workflow = SourceContract.ReadAppSource("NpcBrowserWorkflowService.cs");
        var xaml = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Tabs", "SingleFile", "SingleFileTab.xaml");

        Assert.Contains("x:Name=\"NpcPreviewPlayerLevelNumberBox\"", xaml, StringComparison.Ordinal);
        var previewLevel = SourceContract.Extract(
            xaml,
            "<NumberBox x:Name=\"NpcPreviewPlayerLevelNumberBox\"",
            "/>");
        Assert.DoesNotContain(" Value=\"", previewLevel, StringComparison.Ordinal);
        Assert.Contains("Minimum=\"1\"", previewLevel, StringComparison.Ordinal);
        // The hint moved into Resources.resw behind RuntimeLocalization so it follows a live
        // language change; pin the binding and its resource rather than the English text.
        Assert.Contains("live:RuntimeLocalization.Uid=\"ActorInventory_PreviewLevelInput\"", previewLevel,
            StringComparison.Ordinal);
        // The label moved into Resources.resw as ActorInventory_PreviewLevel.Text.
        Assert.Contains("ActorInventory_PreviewLevel", xaml, StringComparison.Ordinal);
        Assert.Contains("NpcPreviewPlayerLevelNumberBox.Value", host, StringComparison.Ordinal);
        Assert.Equal(2, SourceContract.CountOccurrences(workflow, "options.PreviewPlayerLevel"));
    }
}