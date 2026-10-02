using BethesdaMultitool.Core.Formats.Nif.Rendering.Abstractions;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Vortice.Direct3D12;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

/// <summary>Checks native fixed and optional reference factory ownership, aliases and retired replacement.</summary>
[Trait("Category", GpuTestGuard.Category)]
[Collection(ProcessEnvironmentGroup.Name)]
public sealed class ReferenceFixedPipelineGpuTests
{
    /// <summary>Creates every mandatory pipeline and verifies real shadow output before releasing its retained root dependency.</summary>
    /// <param name="samples">Actual WARP scene samples; shadow maps remain single-sampled in both cases.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void MandatoryFactoryPreservesAliasesShadowOutputAndSiblingOwnership(int samples)
    {
        GpuTestGuard.SkipUnlessEnabled();
        var fixture = new ReferencePipelineGpuFixture(samples);
        var root = fixture.Root;
        try
        {
            Assert.Equal(samples, fixture.SampleCount);
            var factory = fixture.CreateFactory(BethesdaGame.Unknown);
            var pipelines = CollectMandatory(factory, samples);
            Assert.Equal(samples == 1 ? 15 : 26, pipelines.Count);
            AssertAliveAndDistinct(pipelines);
            Assert.False(factory.DirectClassicSkinAvailable);
            Assert.False(factory.ModernStandardOpaqueAvailable);
            Assert.False(factory.DirectModernStandardOpaqueAvailable);
            Assert.False(factory.StarfieldDiffuseLitOpaqueAvailable);
            Assert.False(factory.DirectStarfieldDiffuseLitOpaqueAvailable);

            var shadow = fixture.CaptureOpaqueShadow(factory);
            AssertShadow(shadow);
            Assert.Equal(shadow, fixture.CaptureOpaqueShadow(factory, reversedWinding: true));
            var occluded = fixture.CaptureOpaqueShadow(factory, clearDepth: 0.75f);
            Assert.All(occluded, depth => Assert.Equal(0.75f, depth));

            var sibling = fixture.GetPipeline(fixture.Primary, new ReferenceBlendPipelineKey(6, 7, true, false));
            var siblingOutput = fixture.Capture(sibling);
            fixture.DisposeFactory(factory);
            AssertReleased(pipelines);
            Assert.NotEqual(IntPtr.Zero, root.NativePointer);
            Assert.NotEqual(IntPtr.Zero, sibling.NativePointer);
            Assert.Equal(siblingOutput, fixture.Capture(sibling));
        }
        catch (Exception failure)
        {
            fixture.DisposeAfterFailure(failure);
            throw;
        }
        fixture.Dispose();
        Assert.Equal(IntPtr.Zero, root.NativePointer);
    }

    /// <summary>Requires the requested real game family and every lazy skin/eye owner to survive until explicit retirement.</summary>
    /// <param name="game">Game whose existing optional pipelines are instantiated.</param>
    [Theory]
    [InlineData(BethesdaGame.Oblivion)]
    [InlineData(BethesdaGame.Fallout4)]
    [InlineData(BethesdaGame.Fallout76)]
    [InlineData(BethesdaGame.Starfield)]
    public void OptionalGameFamiliesAndLazyPipelinesReleaseThroughTheirFactory(BethesdaGame game)
    {
        GpuTestGuard.SkipUnlessEnabled();
        var fixture = new ReferencePipelineGpuFixture(1);
        var root = fixture.Root;
        try
        {
            var fallout = game is BethesdaGame.Fallout4 or BethesdaGame.Fallout76;
            var factory = fixture.CreateFactory(game, fallout ? "1" : null);
            var pipelines = CollectMandatory(factory, 1);
            Assert.Equal(fallout, factory.FalloutModernStandardRequested);
            Assert.Equal(fallout, factory.DirectModernStandardOpaqueRequested);
            Assert.Equal(fallout, factory.ModernStandardOpaqueAvailable);
            Assert.Equal(fallout, factory.DirectModernStandardOpaqueAvailable);
            Assert.Equal(game == BethesdaGame.Starfield, factory.StarfieldDiffuseLitRequested);
            Assert.Equal(game == BethesdaGame.Starfield, factory.DirectStarfieldDiffuseLitRequested);
            Assert.Equal(game == BethesdaGame.Starfield, factory.StarfieldDiffuseLitOpaqueAvailable);
            Assert.Equal(game == BethesdaGame.Starfield, factory.DirectStarfieldDiffuseLitOpaqueAvailable);
            Assert.Equal(game == BethesdaGame.Oblivion, factory.DirectClassicSkinRequested);
            Assert.Equal(game == BethesdaGame.Oblivion, factory.DirectClassicSkinAvailable);

            if (game == BethesdaGame.Oblivion)
            {
                CollectClassicSkinAndEye(factory, pipelines);
            }
            else if (fallout)
            {
                CollectModern(factory, pipelines);
            }
            else
            {
                CollectStarfield(factory, pipelines);
            }

            Assert.Equal(game switch
            {
                BethesdaGame.Oblivion => 24, // Mandatory 15, ordinary skin 2, diagnostic 2, independent 4, eye 1.
                BethesdaGame.Starfield => 23, // Mandatory 15 plus four instanced and four direct variants.
                _ => 21 // Mandatory 15 plus three instanced and three direct Fallout variants.
            }, pipelines.Count);
            AssertAliveAndDistinct(pipelines);
            fixture.DisposeFactory(factory);
            AssertReleased(pipelines);
            Assert.NotEqual(IntPtr.Zero, root.NativePointer);
            var sibling = fixture.GetPipeline(fixture.Alternate, new ReferenceBlendPipelineKey(0, 1, true, false));
            Assert.NotEqual(IntPtr.Zero, sibling.NativePointer);
            Assert.Equal(ReferencePipelineGpuFixture.Size * ReferencePipelineGpuFixture.Size * 4,
                fixture.Capture(sibling).Length);
        }
        catch (Exception failure)
        {
            fixture.DisposeAfterFailure(failure);
            throw;
        }
        fixture.Dispose();
        Assert.Equal(IntPtr.Zero, root.NativePointer);
    }

    /// <summary>Replaces all three grass routes with missing and disabled profiles without losing fixed or shared pipelines.</summary>
    [Fact]
    public void GrassProfileFailureAndResetRetireOnlyReplacedFamilies()
    {
        GpuTestGuard.SkipUnlessEnabled();
        var fixture = new ReferencePipelineGpuFixture();
        try { VerifyGrassReplacement(fixture); }
        catch (Exception failure)
        {
            fixture.DisposeAfterFailure(failure);
            throw;
        }
        fixture.Dispose();
    }

    /// <summary>Collects the full mandatory native family while checking every direct selector and coverage alias.</summary>
    /// <param name="factory">Actual production factory.</param>
    /// <param name="samples">Expected scene sample count.</param>
    /// <returns>Each distinct owned native wrapper exactly once.</returns>
    private static HashSet<ID3D12PipelineState> CollectMandatory(ReferencePipelineFactory12 factory, int samples)
    {
        var result = new HashSet<ID3D12PipelineState>(ReferenceEqualityComparer.Instance)
        {
            factory.OpaqueBackPso, factory.OpaqueDoublePso, factory.OpaqueBackDecalPso, factory.OpaqueDoubleDecalPso,
            factory.OpaqueBackA2CPso, factory.OpaqueDoubleA2CPso, factory.ShadowOpaquePso, factory.ShadowAlphaTestPso,
            factory.GetMirrorPso(factory.OpaqueBackPso), factory.GetMirrorPso(factory.OpaqueBackA2CPso)
        };
        Assert.Equal(samples > 1, factory.AlphaToCoverageAvailable);
        for (var flags = 0; flags < 8; flags++)
        {
            var ordinary = factory.GetDirectOpaquePipeline((flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0);
            var coverage = factory.GetDirectAlphaToCoveragePipeline((flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0);
            if (samples == 1)
            {
                Assert.Same(ordinary, coverage);
            }
            else
            {
                Assert.NotSame(ordinary, coverage);
            }
            result.Add(ordinary);
            result.Add(coverage);
        }
        if (samples == 1)
        {
            Assert.Same(factory.OpaqueBackPso, factory.OpaqueBackA2CPso);
            Assert.Same(factory.OpaqueDoublePso, factory.OpaqueDoubleA2CPso);
            Assert.Same(factory.GetMirrorPso(factory.OpaqueBackPso), factory.GetMirrorPso(factory.OpaqueBackA2CPso));
        }
        else
        {
            Assert.NotSame(factory.OpaqueBackPso, factory.OpaqueBackA2CPso);
            Assert.NotSame(factory.OpaqueDoublePso, factory.OpaqueDoubleA2CPso);
            Assert.NotSame(factory.GetMirrorPso(factory.OpaqueBackPso), factory.GetMirrorPso(factory.OpaqueBackA2CPso));
        }
        Assert.NotSame(factory.OpaqueBackPso, factory.GetMirrorPso(factory.OpaqueBackPso));
        Assert.Same(factory.OpaqueDoublePso, factory.GetMirrorPso(factory.OpaqueDoublePso));
        Assert.Same(factory.OpaqueDoubleA2CPso, factory.GetMirrorPso(factory.OpaqueDoubleA2CPso));
        Assert.NotSame(factory.ShadowOpaquePso, factory.ShadowAlphaTestPso);
        return result;
    }

    /// <summary>Creates the real lazy Oblivion variants and verifies repeated requests preserve their identities.</summary>
    /// <param name="factory">Oblivion production factory with its complete ordinary skin pair.</param>
    /// <param name="pipelines">Distinct retained handles to inspect after factory disposal.</param>
    private static void CollectClassicSkinAndEye(ReferencePipelineFactory12 factory, HashSet<ID3D12PipelineState> pipelines)
    {
        for (var sided = 0; sided < 2; sided++)
        {
            var doubleSided = sided != 0;
            Assert.True(factory.TryGetDirectClassicSkinPso(doubleSided, out var ordinary));
            Assert.NotNull(ordinary);
            Assert.True(pipelines.Add(ordinary));
            var diagnostic = factory.GetDirectClassicSkinFactorOnePso(doubleSided);
            Assert.Same(diagnostic, factory.GetDirectClassicSkinFactorOnePso(doubleSided));
            Assert.True(pipelines.Add(diagnostic));
            for (var factor = 0; factor < 2; factor++)
            {
                var independent = factory.GetDirectClassicSkinIndependentPso(doubleSided, factor != 0);
                Assert.Same(independent, factory.GetDirectClassicSkinIndependentPso(doubleSided, factor != 0));
                Assert.True(pipelines.Add(independent));
            }
        }
        var eye = factory.GetOblivionEyePso();
        Assert.Same(eye, factory.GetOblivionEyePso());
        Assert.True(pipelines.Add(eye));
    }

    /// <summary>Checks all supported instanced and direct Fallout variants and their existing mirror fallback.</summary>
    /// <param name="factory">Fallout factory created with the exact established enable override.</param>
    /// <param name="pipelines">Retained distinct native handles.</param>
    private static void CollectModern(ReferencePipelineFactory12 factory, HashSet<ID3D12PipelineState> pipelines)
    {
        ModernStandardOpaqueShaderVariant[] variants =
        [
            ModernStandardOpaqueShaderVariant.SingleSidedOpaque,
            ModernStandardOpaqueShaderVariant.SingleSidedGreaterCutout,
            ModernStandardOpaqueShaderVariant.DoubleSidedGreaterCutout
        ];
        foreach (var variant in variants)
        {
            Assert.True(factory.TryGetModernStandardOpaquePso(variant, out var instanced));
            Assert.True(factory.TryGetDirectModernStandardOpaquePso(variant, out var direct));
            Assert.NotNull(instanced);
            Assert.NotNull(direct);
            Assert.True(pipelines.Add(instanced));
            Assert.True(pipelines.Add(direct));
            Assert.Same(variant == ModernStandardOpaqueShaderVariant.DoubleSidedGreaterCutout
                ? factory.OpaqueDoublePso : factory.GetMirrorPso(factory.OpaqueBackPso), factory.GetMirrorPso(instanced));
        }
        Assert.False(factory.TryGetModernStandardOpaquePso(ModernStandardOpaqueShaderVariant.None, out _));
        Assert.False(factory.TryGetDirectModernStandardOpaquePso(ModernStandardOpaqueShaderVariant.None, out _));
    }

    /// <summary>Checks all four Starfield specializations in both vertex ABIs under the unset default override.</summary>
    /// <param name="factory">Starfield production factory.</param>
    /// <param name="pipelines">Retained distinct native handles.</param>
    private static void CollectStarfield(ReferencePipelineFactory12 factory, HashSet<ID3D12PipelineState> pipelines)
    {
        for (var flags = 0; flags < 4; flags++)
        {
            var alphaGreater = (flags & 1) != 0;
            var doubleSided = (flags & 2) != 0;
            Assert.True(factory.TryGetStarfieldDiffuseLitPso(alphaGreater, doubleSided, out var instanced));
            Assert.True(factory.TryGetDirectStarfieldDiffuseLitPso(alphaGreater, doubleSided, out var direct));
            Assert.NotNull(instanced);
            Assert.NotNull(direct);
            Assert.True(pipelines.Add(instanced));
            Assert.True(pipelines.Add(direct));
            Assert.Same(doubleSided ? factory.OpaqueDoublePso : factory.GetMirrorPso(factory.OpaqueBackPso),
                factory.GetMirrorPso(instanced));
        }
    }

    /// <summary>Exercises real grass pairs, failed compilation and disabled fallback under explicit queue retirement.</summary>
    /// <param name="fixture">Fixture owning the factory, native device and live sibling route.</param>
    private static void VerifyGrassReplacement(ReferencePipelineGpuFixture fixture)
    {
        var factory = fixture.CreateFactory(BethesdaGame.Unknown);
        var mandatory = CollectMandatory(factory, 4);
        var shared = factory.GetBlendPipeline(6, 7, true);
        var directProfile = new GameShaderPair(true, "reference_grass_oblivion.vert.hlsl", "reference_grass_oblivion.frag.hlsl");
        var instancedProfile = new GameShaderPair(true, "reference_grass_fnv.vert.hlsl", "reference_grass_fnv.frag.hlsl");
        var missing = new GameShaderPair(true, "missing-reference-fixture.vert.hlsl", "missing-reference-fixture.frag.hlsl");
        Assert.False(factory.GrassShaderAvailable);
        Assert.False(factory.InstancedBlendGrassShaderAvailable);
        Assert.False(factory.InstancedGrassShaderAvailable);
        Assert.Same(factory.OpaqueBackA2CPso, factory.GetGrassCutoutPso(false));

        fixture.RetireGpu();
        factory.SetGrassShaderProfile(directProfile);
        factory.SetInstancedBlendGrassShaderProfile(directProfile);
        factory.SetInstancedGrassShaderProfile(instancedProfile);
        Assert.True(factory.GrassShaderAvailable);
        Assert.True(factory.InstancedBlendGrassShaderAvailable);
        Assert.True(factory.InstancedGrassShaderAvailable);
        var cutoutBack = factory.GetGrassCutoutPso(false);
        var cutoutDouble = factory.GetGrassCutoutPso(true);
        var direct = factory.GetBlendPipeline(6, 7, true, grassRoute: true);
        var directWriting = factory.GetBlendDepthWritePipeline(6, 7, true, grassRoute: true);
        var instanced = factory.GetBlendPipeline(6, 7, true, grassRoute: true, instancedGrass: true);
        var instancedWriting = factory.GetBlendDepthWritePipeline(6, 7, true, grassRoute: true, instancedGrass: true);
        ID3D12PipelineState[] replaced = [cutoutBack, cutoutDouble, direct, directWriting, instanced, instancedWriting];
        AssertAliveAndDistinct(replaced);
        Assert.DoesNotContain(shared, replaced);
        fixture.RetireGpu();
        factory.SetGrassShaderProfile(directProfile);
        factory.SetInstancedBlendGrassShaderProfile(directProfile);
        factory.SetInstancedGrassShaderProfile(instancedProfile);
        Assert.Same(cutoutBack, factory.GetGrassCutoutPso(false));
        Assert.Same(direct, factory.GetBlendPipeline(6, 7, true, grassRoute: true));
        Assert.Same(instanced, factory.GetBlendPipeline(6, 7, true, grassRoute: true, instancedGrass: true));

        fixture.RetireGpu();
        factory.SetGrassShaderProfile(missing);
        factory.SetInstancedBlendGrassShaderProfile(missing);
        factory.SetInstancedGrassShaderProfile(missing);
        Assert.False(factory.GrassShaderAvailable);
        Assert.False(factory.InstancedBlendGrassShaderAvailable);
        Assert.False(factory.InstancedGrassShaderAvailable);
        AssertReleased(replaced);
        Assert.Same(shared, factory.GetBlendPipeline(6, 7, true, grassRoute: true));
        Assert.Same(shared, factory.GetBlendPipeline(6, 7, true, grassRoute: true, instancedGrass: true));
        Assert.Same(factory.OpaqueBackA2CPso, factory.GetGrassCutoutPso(false));
        Assert.Same(factory.OpaqueDoubleA2CPso, factory.GetGrassCutoutPso(true));
        AssertAliveAndDistinct(mandatory);

        fixture.RetireGpu();
        factory.SetGrassShaderProfile(directProfile);
        factory.SetInstancedBlendGrassShaderProfile(directProfile);
        factory.SetInstancedGrassShaderProfile(instancedProfile);
        var restoredDirect = factory.GetBlendPipeline(6, 7, true, grassRoute: true);
        var restoredInstanced = factory.GetBlendPipeline(6, 7, true, grassRoute: true, instancedGrass: true);
        var restoredCutout = factory.GetGrassCutoutPso(false);
        Assert.NotSame(direct, restoredDirect);
        Assert.NotSame(instanced, restoredInstanced);
        Assert.NotSame(cutoutBack, restoredCutout);
        ID3D12PipelineState[] restored = [restoredDirect, restoredInstanced, restoredCutout];
        AssertAliveAndDistinct(restored);
        fixture.RetireGpu();
        factory.SetGrassShaderProfile(default);
        factory.SetInstancedBlendGrassShaderProfile(default);
        factory.SetInstancedGrassShaderProfile(default);
        AssertReleased(restored);
        Assert.False(factory.GrassShaderAvailable);
        Assert.False(factory.InstancedBlendGrassShaderAvailable);
        Assert.False(factory.InstancedGrassShaderAvailable);
        Assert.Same(shared, factory.GetBlendPipeline(6, 7, true, grassRoute: true));
        Assert.Same(factory.OpaqueBackA2CPso, factory.GetGrassCutoutPso(false));
        fixture.DisposeFactory(factory);
        AssertReleased(mandatory);
        Assert.Equal(IntPtr.Zero, shared.NativePointer);
        Assert.NotEqual(IntPtr.Zero, fixture.Root.NativePointer);
    }

    /// <summary>Checks actual opaque shadow coverage and the preserved small negative reversed-Z raster bias.</summary>
    /// <param name="depths">Retired single-sample D32 output.</param>
    private static void AssertShadow(float[] depths)
    {
        Assert.Equal(ReferencePipelineGpuFixture.Size * ReferencePipelineGpuFixture.Size, depths.Length);
        Assert.Equal(0f, depths[0]);
        foreach (var coordinate in new[] { (10, 10), (16, 16), (21, 21) })
        {
            var depth = depths[coordinate.Item2 * ReferencePipelineGpuFixture.Size + coordinate.Item1];
            Assert.InRange(depth, 0.4998f, 0.4999999f);
        }
    }

    /// <summary>Requires each retained owner to identify a distinct, live native pipeline.</summary>
    /// <param name="pipelines">Distinct expected pipeline wrappers.</param>
    private static void AssertAliveAndDistinct(IEnumerable<ID3D12PipelineState> pipelines)
    {
        var pointers = new HashSet<IntPtr>();
        foreach (var pipeline in pipelines)
        {
            Assert.NotEqual(IntPtr.Zero, pipeline.NativePointer);
            Assert.True(pointers.Add(pipeline.NativePointer));
        }
    }

    /// <summary>Requires every previously published handle to be released after its owning family retires.</summary>
    /// <param name="pipelines">Borrowed wrappers captured before owner disposal.</param>
    private static void AssertReleased(IEnumerable<ID3D12PipelineState> pipelines)
    {
        foreach (var pipeline in pipelines)
        {
            Assert.Equal(IntPtr.Zero, pipeline.NativePointer);
        }
    }
}
