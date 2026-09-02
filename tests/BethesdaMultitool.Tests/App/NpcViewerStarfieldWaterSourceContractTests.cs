using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class NpcViewerStarfieldWaterSourceContractTests
{
    private static string ViewerSource() => SourceContract.ReadAppSource("npc-viewer.html");

    [Fact]
    public void MarkedWaterMaterialIsValidatedBeforeAnimationStarts()
    {
        var source = ViewerSource();
        var inspect = SourceContract.Extract(
            source,
            "function inspectGlbVertexLerpContract(bytes)",
            "function findInternalModelScene()");
        var patch = SourceContract.Extract(
            source,
            "function patchMarkedStarfieldWaterScene(scene, contract, loadGeneration)",
            "function loadModel(base64Glb)");

        Assert.Contains("'bethesdaStarfieldWaterApproxV1'", source, StringComparison.Ordinal);
        Assert.Contains("materials[index]?.extras?.[starfieldWaterMarker] === true", inspect,
            StringComparison.Ordinal);
        Assert.Contains(
            "expectedStarfieldWaterMaterialCount: starfieldWaterMaterialIndexes.length",
            inspect,
            StringComparison.Ordinal);
        Assert.Contains("material?.userData?.[starfieldWaterMarker] !== true", patch,
            StringComparison.Ordinal);
        Assert.Contains("material.userData?.associations?.materials", patch,
            StringComparison.Ordinal);
        Assert.Contains("missingMaterialIndexes.length > 0", patch, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            patch,
            "if (markedMaterials.size === 0)",
            "const animatedNormals = []",
            "stopStarfieldWaterAnimation();",
            "requestAnimationFrame(renderWaterFrame)");
    }

    [Fact]
    public void WaterNormalDriftIsBoundedDeduplicatedAndHiddenViewerDoesNotRender()
    {
        var source = ViewerSource();
        var patch = SourceContract.Extract(
            source,
            "function patchMarkedStarfieldWaterScene(scene, contract, loadGeneration)",
            "function loadModel(base64Glb)");
        var load = SourceContract.Extract(
            source,
            "function loadModel(base64Glb)",
            "function clearModel()");
        var clear = SourceContract.Extract(source, "function clearModel()", "function setStatus(text)");

        Assert.Contains("const normalMap = material.normalMap;", patch, StringComparison.Ordinal);
        Assert.Contains("animatedNormalTextures.has(normalMap)", patch, StringComparison.Ordinal);
        Assert.Contains("animatedNormalTextures.add(normalMap);", patch, StringComparison.Ordinal);
        Assert.Contains("normalMap.matrixAutoUpdate = true;", patch, StringComparison.Ordinal);
        Assert.Contains("loadGeneration !== modelLoadGeneration", patch, StringComparison.Ordinal);
        Assert.Contains("document.visibilityState === 'visible'", patch, StringComparison.Ordinal);
        Assert.Contains("viewer.modelIsVisible === true", patch, StringComparison.Ordinal);
        Assert.Contains("entry.texture.offset.x = entry.initialX + (seconds * 0.018) % 1", patch,
            StringComparison.Ordinal);
        Assert.Contains("entry.texture.offset.y = entry.initialY - (seconds * 0.045) % 1", patch,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            patch,
            "document.visibilityState === 'visible'",
            "viewer.modelIsVisible === true",
            "scene.queueRender();",
            "starfieldWaterAnimationFrame = requestAnimationFrame(renderWaterFrame);");
        Assert.Contains("stopStarfieldWaterAnimation();", load, StringComparison.Ordinal);
        Assert.Contains("stopStarfieldWaterAnimation();", clear, StringComparison.Ordinal);
        Assert.Contains(
            "vertexLerpContract.expectedStarfieldWaterMaterialCount > 0",
            load,
            StringComparison.Ordinal);
        Assert.Contains("patchMarkedStarfieldWaterScene(", load, StringComparison.Ordinal);
    }

    [Fact]
    public void BundledModelViewerSupportsThePhysicalWaterChannelsAndRuntimeTextureOffset()
    {
        var bundle = SourceContract.ReadAppSource("model-viewer.min.js");

        Assert.Contains("setTransmissionFactor", bundle, StringComparison.Ordinal);
        Assert.Contains("setClearcoatFactor", bundle, StringComparison.Ordinal);
        Assert.Contains("setClearcoatRoughnessFactor", bundle, StringComparison.Ordinal);
        Assert.Contains("setIor", bundle, StringComparison.Ordinal);
        Assert.Contains("matrixAutoUpdate", bundle, StringComparison.Ordinal);
        Assert.Contains("Object.assign(t.userData,e.extras)", bundle, StringComparison.Ordinal);
        Assert.Contains("t.userData.associations=r", bundle, StringComparison.Ordinal);
    }
}
