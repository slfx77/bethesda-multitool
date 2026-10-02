using System.Runtime.InteropServices;
using BethesdaMultitool.Tests.Helpers;
using Xunit;
using static BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.ReferenceRendererConstants12;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Pins regular FO4/FO76 BGSM emission as a lit additive overlay. It must remain distinct from
///     the legacy full-bright route and must not widen the persistent opaque-draw constant ABI.
/// </summary>
public sealed class BgsmEmissionRenderPathSourceContractTests
{
    /// <summary>Pins the dedicated glow binding to the same retained acquisition owner used by rollback and resident retirement.</summary>
    [Fact]
    public void CacheAcquiresTracksAndReleasesTheDedicatedGlowTexture()
    {
        var cached = D3D12Source("CachedSubmesh12.cs");
        var cache = D3D12Source("ReferenceMeshCache12.cs");
        var owner = D3D12Source("CachedNifMesh12.cs");

        Assert.Contains("BgsmEmissionTextureFlag = 1u << 16", cached, StringComparison.Ordinal);
        Assert.Contains("public GpuTextureCache12.Entry? BgsmGlowMap", cached, StringComparison.Ordinal);
        Assert.Contains("public Vector3 BgsmEmissionColor", cached, StringComparison.Ordinal);
        Assert.Contains("(HasBgsmEmission ? (float)BgsmEmissionTextureFlag : 0f)", cached,
            StringComparison.Ordinal);
        Assert.Contains("BgsmGlowMap is not { IsReady: false }", cached, StringComparison.Ordinal);

        SourceContract.AssertOrder(
            cache,
            "var resources = residencyResources ?? GpuMeshResources12.Begin(deletionQueue, geometryArena.Free, textureCache.Release);",
            "var bgsmGlowMap = hasBgsmEmission",
            "resources.AcquireTexture(() => ResolveTexture(sub.BgsmGlowMapTexturePath!))",
            "BgsmGlowMap = bgsmGlowMap",
            "BgsmEmissionColor = hasBgsmEmission ? sub.BgsmEmissionColor : Vector3.Zero",
            "var cached = new CachedNifMesh12(",
            "resources, deletionQueue,",
            "resources.Commit();");
        Assert.Contains("_resources = resources;", owner, StringComparison.Ordinal);
        SourceContract.AssertOrder(owner, "public void Dispose()", "_deletionQueue.EnqueueDispose(_resources);",
            "_retirementQueued = true;");
    }

    [Fact]
    public void GenericShaderConsumesTheUnionAndSharedDrawAbiStaysAt256Bytes()
    {
        Assert.Equal(256, Marshal.SizeOf<InstanceDrawConstants>());

        var renderer = D3D12Source("ReferenceRenderer12.cs");
        var shader = SourceContract.ReadShaderSource("reference.frag.hlsl");

        Assert.True(SourceContract.CountOccurrences(
            renderer,
            "EffectFalloff: ResolveEffectFalloffConstants(") >= 1);
        Assert.Contains("EffectFalloff = ResolveEffectFalloffConstants(", renderer,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            renderer,
            "if (submesh.HasBgsmEmission)",
            "submesh.BgsmEmissionColor",
            "glowMap.BindlessIndex + 1f");

        SourceContract.AssertOrder(
            shader,
            "bool HasBgsmEmission(float packedState)",
            "if (HasBgsmEmission(input.vTextureState.z))",
            "float3 bgsmEmission = input.vEffectFalloff.rgb;",
            "bgsmGlowSlot = (uint)round(input.vEffectFalloff.w - 1.0);",
            "lit += bgsmEmission;",
            "authoredGlow = dot(bgsmEmission, 1.0) > 0.0;",
            "if (!fullBright && !authoredGlow)");
    }

    private static string D3D12Source(string fileName)
    {
        return SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12", fileName);
    }
}
