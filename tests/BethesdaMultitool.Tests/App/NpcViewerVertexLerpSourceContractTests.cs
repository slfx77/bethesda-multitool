using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class NpcViewerVertexLerpSourceContractTests
{
    private static string ViewerSource() => SourceContract.ReadAppSource("npc-viewer.html");

    [Fact]
    public void MarkedGlbIsDetectedBeforePrivateModelViewerInternalsAreUsed()
    {
        var source = ViewerSource();
        var inspect = SourceContract.Extract(
            source,
            "function inspectGlbVertexLerpContract(bytes)",
            "function findInternalModelScene()");
        var load = SourceContract.Extract(
            source,
            "function loadModel(base64Glb)",
            "function clearModel()");
        var clear = SourceContract.Extract(source, "function clearModel()", "function setStatus(text)");
        var fatalStatus = SourceContract.Extract(
            source,
            "function showFatalStatus(message)",
            "function inspectGlbVertexLerpContract(bytes)");

        Assert.Contains("'bethesdaCe2VertexLerpV1'", source, StringComparison.Ordinal);
        Assert.Contains("materials[index]?.extras?.[ce2VertexLerpMarker] === true", inspect,
            StringComparison.Ordinal);
        Assert.Contains("expectedMaterialCount: markedMaterialIndexes.length", inspect,
            StringComparison.Ordinal);
        Assert.Contains("if (vertexLerpContract.expectedMaterialCount > 0)", load,
            StringComparison.Ordinal);
        Assert.Contains("const loadGeneration = ++modelLoadGeneration;", load,
            StringComparison.Ordinal);
        Assert.Contains("loadGeneration !== modelLoadGeneration", load, StringComparison.Ordinal);
        Assert.Contains("showFatalStatus(error);", load, StringComparison.Ordinal);
        Assert.Contains("modelLoadGeneration++;", clear, StringComparison.Ordinal);
        Assert.Contains("status.textContent = 'Fatal mesh viewer error: ' + detail;", fatalStatus,
            StringComparison.Ordinal);
        Assert.Contains("status.classList.remove('hidden');", fatalStatus, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            load,
            "const vertexLerpContract = inspectGlbVertexLerpContract(bytes);",
            "const onLoad = () => {",
            "const scene = findInternalModelScene();",
            "viewer.addEventListener('load', onLoad",
            "viewer.src = currentBlobUrl;");
    }

    [Fact]
    public void MarkedRuntimeMaterialsRequireColorOneAndQueueARecompileRender()
    {
        var source = ViewerSource();
        var findScene = SourceContract.Extract(
            source,
            "function findInternalModelScene()",
            "function replaceRequiredShaderAnchor(");
        var patchScene = SourceContract.Extract(
            source,
            "function patchMarkedCe2VertexLerpScene(scene, contract)",
            "function loadModel(base64Glb)");

        Assert.Contains("Object.getOwnPropertySymbols(viewer)", findScene, StringComparison.Ordinal);
        Assert.Contains("typeof candidate.model.traverse === 'function'", findScene,
            StringComparison.Ordinal);
        Assert.Contains("typeof candidate.queueRender === 'function'", findScene,
            StringComparison.Ordinal);
        Assert.Contains("sceneCandidates.length !== 1", findScene, StringComparison.Ordinal);
        Assert.Contains("material?.userData?.[ce2VertexLerpMarker] === true", patchScene,
            StringComparison.Ordinal);
        Assert.Contains("geometry.getAttribute('color_1')", patchScene, StringComparison.Ordinal);
        Assert.Contains("color1.itemSize !== 4", patchScene, StringComparison.Ordinal);
        Assert.Contains("color1.count !== position.count", patchScene, StringComparison.Ordinal);
        Assert.Contains("material.userData?.associations?.materials", patchScene,
            StringComparison.Ordinal);
        Assert.Contains("missingMaterialIndexes.length > 0", patchScene, StringComparison.Ordinal);
        Assert.Contains("patchedMaterialCount === 0", patchScene, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            patchScene,
            "installCe2VertexLerpShader(material);",
            "patchedMaterialCount++;",
            "scene.queueRender();");
    }

    [Fact]
    public void ShaderHookInterpolatesRawColorOneIntoRgbWithoutChangingAlpha()
    {
        var source = ViewerSource();
        var hook = SourceContract.Extract(
            source,
            "function installCe2VertexLerpShader(material)",
            "function patchMarkedCe2VertexLerpScene(");

        Assert.Contains("new WeakSet()", source, StringComparison.Ordinal);
        Assert.Contains("previousOnBeforeCompile.call(this, shader, renderer);", hook,
            StringComparison.Ordinal);
        Assert.Contains("previousCustomProgramCacheKey.call(this)", hook, StringComparison.Ordinal);
        Assert.Contains("previousKey + '|' + ce2VertexLerpMarker", hook, StringComparison.Ordinal);
        Assert.Contains("material.needsUpdate = true;", hook, StringComparison.Ordinal);
        Assert.Contains("#include <color_pars_vertex>", hook, StringComparison.Ordinal);
        Assert.Contains("#include <color_vertex>", hook, StringComparison.Ordinal);
        Assert.Contains("#include <color_pars_fragment>", hook, StringComparison.Ordinal);
        Assert.Contains("#include <color_fragment>", hook, StringComparison.Ordinal);
        Assert.Contains("attribute vec4 color_1;", hook, StringComparison.Ordinal);
        Assert.Contains("varying vec4 vBethesdaCe2VertexLerp;", hook, StringComparison.Ordinal);
        Assert.Contains("vBethesdaCe2VertexLerp = color_1;", hook, StringComparison.Ordinal);
        Assert.Contains("diffuseColor.rgb = mix(", hook, StringComparison.Ordinal);
        Assert.Contains(
            "diffuseColor.rgb, vBethesdaCe2VertexLerp.rgb, vBethesdaCe2VertexLerp.a);",
            hook,
            StringComparison.Ordinal);
        Assert.Equal(4, SourceContract.CountOccurrences(hook, "replaceRequiredShaderAnchor("));
        Assert.DoesNotContain("diffuseColor.a =", hook, StringComparison.Ordinal);
        Assert.DoesNotContain("diffuseColor.a *=", hook, StringComparison.Ordinal);

        var requireAnchor = SourceContract.Extract(
            source,
            "function replaceRequiredShaderAnchor(",
            "function installCe2VertexLerpShader(");
        SourceContract.AssertOrder(
            requireAnchor,
            "if (!source.includes(anchor))",
            "showFatalStatus(error);",
            "throw error;");
    }

    [Fact]
    public void BundledModelViewerStillSupportsThePinnedPrivateSceneAndGltfLoaderContracts()
    {
        var bundle = SourceContract.ReadAppSource("model-viewer.min.js");

        Assert.Contains("const n=\"163\"", bundle, StringComparison.Ordinal);
        Assert.Contains("Symbol(\"scene\")", bundle, StringComparison.Ordinal);
        Assert.Contains("get model(){return this._model}", bundle, StringComparison.Ordinal);
        Assert.Contains("queueRender(){this.isDirty=!0}", bundle, StringComparison.Ordinal);
        Assert.Contains(
            "const i=Hu[e]||e.toLowerCase();i in t.attributes||r.push(s(n[e],i))",
            bundle,
            StringComparison.Ordinal);
        Assert.Contains("Object.assign(t.userData,e.extras)", bundle, StringComparison.Ordinal);
        Assert.Contains("t.userData.associations=r", bundle, StringComparison.Ordinal);
        Assert.Contains(
            "onBeforeCompile(){}customProgramCacheKey(){return this.onBeforeCompile.toString()}",
            bundle,
            StringComparison.Ordinal);
    }
}
