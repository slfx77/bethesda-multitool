#if WINDOWS_GUI
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using static BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.ReferenceRendererConstants12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.Viewer;

/// <summary>
///     Small direct-draw front end over the established reference shader/material ABI. Viewer
///     geometry is already baked into assembled scene space, so each draw uses the non-instanced
///     b1 world-matrix path while retaining the same cached texture and render-state payload.
/// </summary>
internal sealed class BethesdaViewerStaticRenderer12
{
    private const uint PerFrameByteSize = 64 + 256;
    private const uint PerDrawByteSize = 256;

    private readonly GpuDescriptorHeapAllocator12 _descriptorHeap;
    private readonly int _alphaToCoverageFallbackCount;
    private readonly DepthOrderedDraw[] _depthOrdered;
    private readonly int _falloutSpecializationEligibleCount;
    private readonly int _falloutSpecializationUsedCount;
    private readonly ReferencePipelineFactory12 _pipelines;
    private readonly bool _requiresContinuousFrames;
    private readonly GpuRingBuffer12 _ringBuffer;
    private readonly ViewerDraw[] _sky;
    private readonly int _starfieldSpecializationEligibleCount;
    private readonly int _starfieldSpecializationUsedCount;
    private readonly ViewerDraw[] _transparent;
    private readonly (int Start, int Length)[] _transparentRenderOrderRanges;
    private readonly float[] _transparentSortKeys;
    private readonly int[] _transparentSortOrder;

    internal BethesdaViewerStaticRenderer12(
        CachedNifMesh12 mesh,
        BethesdaViewerPosedScene12 posedScene,
        ReferencePipelineFactory12 pipelines,
        GpuRingBuffer12 ringBuffer,
        GpuDescriptorHeapAllocator12 descriptorHeap)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(posedScene);
        _pipelines = pipelines ?? throw new ArgumentNullException(nameof(pipelines));
        _ringBuffer = ringBuffer ?? throw new ArgumentNullException(nameof(ringBuffer));
        _descriptorHeap = descriptorHeap ?? throw new ArgumentNullException(nameof(descriptorHeap));

        var draws = mesh.Submeshes
            .Where(static submesh => submesh.IndexCount > 0)
            .Select(submesh => CreateViewerDraw(posedScene, submesh))
            .ToArray();
        // Exact RawNif Sky/Stars/Clouds layers are owned by SkyGeometryRenderer12. Do not leave a
        // second copy in this generic path: it would draw once as a camera-centred sky and again as
        // ordinary material geometry. Sky tags in assembled NPC/creature scenes deliberately remain.
        var routedDraws = draws
            .Where(draw => !BethesdaViewerNativeSkyPolicy.IsDedicatedRawNifLayer(
                posedScene.Source.Purpose,
                draw.NativeSemantics.SkyType))
            .ToArray();
        var alphaToCoverageAvailable = _pipelines.AlphaToCoverageAvailable;
        _alphaToCoverageFallbackCount = alphaToCoverageAvailable
            ? 0
            : routedDraws.Count(static draw =>
                draw.NativeSemantics.SkyType is null &&
                draw.NativeSemantics.NativeAlphaRenderMode == NifAlphaRenderMode.AlphaToCoverage);
        _sky = routedDraws
            .Where(static draw => draw.NativeSemantics.SkyType is not null)
            .OrderBy(static draw => draw.NativeSemantics.RenderOrder)
            .ThenBy(static draw => draw.Submesh.MaterializationSourceIndex)
            .ToArray();
        var depthOrdered = new List<DepthOrderedDraw>();
        var transparent = new List<ViewerDraw>();
        var falloutEligible = 0;
        var falloutUsed = 0;
        var starfieldEligible = 0;
        var starfieldUsed = 0;
        foreach (var draw in routedDraws)
        {
            if (draw.NativeSemantics.SkyType is not null)
            {
                continue;
            }

            var nativeAlphaToCoverage =
                draw.NativeSemantics.NativeAlphaRenderMode == NifAlphaRenderMode.AlphaToCoverage;
            if (!draw.Submesh.AlphaBlend || (alphaToCoverageAvailable && nativeAlphaToCoverage))
            {
                var specialization = ResolveOpaqueSpecialization(
                    posedScene.Source.Game,
                    draw,
                    _pipelines);
                switch (specialization.Family)
                {
                    case OpaqueSpecializationFamily.FalloutModernStandard:
                        falloutEligible++;
                        if (specialization.Pipeline is not null) falloutUsed++;
                        break;
                    case OpaqueSpecializationFamily.StarfieldDiffuseLit:
                        starfieldEligible++;
                        if (specialization.Pipeline is not null) starfieldUsed++;
                        break;
                }
                depthOrdered.Add(new DepthOrderedDraw(
                    draw,
                    DepthDrawKind.Opaque,
                    specialization.Pipeline));
            }
            else if (!nativeAlphaToCoverage && !draw.Submesh.EngineZWriteOff)
            {
                depthOrdered.Add(new DepthOrderedDraw(
                    draw,
                    DepthDrawKind.DepthWritingBlend,
                    SpecializedPipeline: null));
            }
            else
            {
                transparent.Add(draw);
            }
        }

        // RenderOrder is the primary Bethesda layer boundary. Within each group, establish depth
        // with opaque/cutout/A2C first and then apply engine depth-writing blends. Sorting the two
        // categories independently would incorrectly move every z-writing layer after later groups.
        _depthOrdered = depthOrdered
            .OrderBy(static entry => entry.Draw.NativeSemantics.RenderOrder)
            .ThenBy(static entry => entry.Kind)
            .ThenBy(static entry => entry.Draw.Submesh.IsDecal)
            .ThenBy(static entry => entry.Draw.Submesh.MaterializationSourceIndex)
            .ToArray();
        _transparent = transparent
            .OrderBy(static draw => draw.NativeSemantics.RenderOrder)
            .ThenBy(static draw => draw.Submesh.MaterializationSourceIndex)
            .ToArray();
        var renderOrderRanges = new List<(int Start, int Length)>();
        for (var start = 0; start < _transparent.Length;)
        {
            var renderOrder = _transparent[start].NativeSemantics.RenderOrder;
            var end = start + 1;
            while (end < _transparent.Length &&
                   _transparent[end].NativeSemantics.RenderOrder == renderOrder)
            {
                end++;
            }

            renderOrderRanges.Add((start, end - start));
            start = end;
        }
        _transparentRenderOrderRanges = renderOrderRanges.ToArray();
        _transparentSortKeys = new float[_transparent.Length];
        _transparentSortOrder = new int[_transparent.Length];
        _falloutSpecializationEligibleCount = falloutEligible;
        _falloutSpecializationUsedCount = falloutUsed;
        _starfieldSpecializationEligibleCount = starfieldEligible;
        _starfieldSpecializationUsedCount = starfieldUsed;
        _requiresContinuousFrames = routedDraws.Any(static draw =>
            draw.Submesh.UvScrollVelocity != Vector2.Zero ||
            draw.Submesh.MaterialAlphaController is not null);
    }

    internal int DrawableCount =>
        _sky.Length + _depthOrdered.Length + _transparent.Length;

    internal bool RequiresContinuousFrames => _requiresContinuousFrames;

    internal int AlphaToCoverageFallbackCount => _alphaToCoverageFallbackCount;

    /// <summary>
    ///     One-time scene census for the narrow direct FO76/Starfield opaque families. Eligibility
    ///     is material-authored and independent of texture residency; a used count below eligible
    ///     therefore means activation was disabled or the complete PSO family failed to compile.
    /// </summary>
    internal string? DescribeOpaqueSpecialization()
    {
        var messages = new List<string>(2);
        if (_falloutSpecializationEligibleCount > 0)
        {
            messages.Add(DescribeSpecializationFamily(
                "FO4/FO76 modern-standard",
                _falloutSpecializationEligibleCount,
                _falloutSpecializationUsedCount,
                _pipelines.DirectModernStandardOpaqueRequested,
                _pipelines.DirectModernStandardOpaqueAvailable,
                "FALLOUT_VIEWER_REFERENCE_MODERN_STANDARD_SHADER=1"));
        }
        if (_starfieldSpecializationEligibleCount > 0)
        {
            messages.Add(DescribeSpecializationFamily(
                "Starfield diffuse-lit",
                _starfieldSpecializationEligibleCount,
                _starfieldSpecializationUsedCount,
                _pipelines.DirectStarfieldDiffuseLitRequested,
                _pipelines.DirectStarfieldDiffuseLitOpaqueAvailable,
                "the Starfield default/override"));
        }

        return messages.Count == 0 ? null : string.Join(" ", messages);
    }

    /// <summary>
    ///     Authored sky (depth disabled), then opaque and engine-depth-writing blend geometry, all
    ///     recorded before water. Every set retains ascending Bethesda render-order groups.
    /// </summary>
    internal int RenderDepthWriting(
        ID3D12GraphicsCommandList commandList,
        int frameIndex,
        Matrix4x4 viewProjection,
        Vector3 cameraPosition,
        Vector3 cameraForward,
        Vector3 cameraRight,
        Vector3 cameraUp,
        float elapsedSeconds)
    {
        if (_sky.Length == 0 && _depthOrdered.Length == 0)
        {
            return 0;
        }

        if (!BindFrame(commandList, frameIndex, viewProjection))
        {
            return 0;
        }

        var draws = 0;
        ID3D12PipelineState? currentPipeline = null;
        foreach (var draw in _sky)
        {
            var submesh = draw.Submesh;
            // SkyShaderProperty geometry is an authored background layer: preserve its source order,
            // draw before scene geometry, and never test/write scene depth. Its authored alpha/blend
            // factors still select the existing material PSO.
            var pipeline = submesh.AlphaBlend
                ? _pipelines.GetBlendPipeline(
                    submesh.SrcBlendMode,
                    submesh.DstBlendMode,
                    submesh.DoubleSided,
                    submesh.IsDecal,
                    depthTestOff: true)
                : _pipelines.GetDirectOpaquePipeline(
                    submesh.DoubleSided,
                    submesh.IsDecal,
                    depthTestOff: true);
            if (Draw(
                    commandList,
                    frameIndex,
                    submesh,
                    pipeline,
                    cameraPosition,
                    cameraForward,
                    cameraRight,
                    cameraUp,
                    elapsedSeconds,
                    false,
                    ref currentPipeline))
            {
                draws++;
            }
        }

        foreach (var entry in _depthOrdered)
        {
            var draw = entry.Draw;
            var submesh = draw.Submesh;
            var nativeAlphaToCoverage =
                draw.NativeSemantics.NativeAlphaRenderMode == NifAlphaRenderMode.AlphaToCoverage;
            ID3D12PipelineState pipeline;
            if (entry.Kind == DepthDrawKind.DepthWritingBlend)
            {
                pipeline = _pipelines.GetBlendDepthWritePipeline(
                    submesh.SrcBlendMode,
                    submesh.DstBlendMode,
                    submesh.DoubleSided,
                    submesh.IsDecal,
                    depthTestOff: submesh.DepthTestOff);
            }
            else if (nativeAlphaToCoverage)
            {
                pipeline = _pipelines.GetDirectAlphaToCoveragePipeline(
                    submesh.DoubleSided,
                    submesh.IsDecal,
                    submesh.DepthTestOff);
            }
            else if (entry.SpecializedPipeline is { } specializedPipeline)
            {
                pipeline = specializedPipeline;
            }
            else
            {
                pipeline = _pipelines.GetDirectOpaquePipeline(
                    submesh.DoubleSided,
                    submesh.IsDecal,
                    submesh.DepthTestOff);
            }

            if (Draw(
                    commandList,
                    frameIndex,
                    submesh,
                    pipeline,
                    cameraPosition,
                    cameraForward,
                    cameraRight,
                    cameraUp,
                    elapsedSeconds,
                    nativeAlphaToCoverage,
                    ref currentPipeline))
            {
                draws++;
            }
        }

        return draws;
    }

    private static string DescribeSpecializationFamily(
        string name,
        int eligible,
        int used,
        bool requested,
        bool available,
        string activation)
    {
        if (used == eligible)
        {
            return $"{used}/{eligible} eligible opaque part(s) use the direct {name} shader.";
        }

        var reason = !requested
            ? $"{activation} is disabled"
            : !available
                ? "the atomic direct PSO family was unavailable"
                : "the specialized PSO lookup failed closed";
        return $"{used}/{eligible} eligible opaque part(s) use the direct {name} shader; " +
               $"{eligible - used} use the generic material shader because {reason}.";
    }

    /// <summary>All non-depth-writing alpha geometry, sorted farthest first.</summary>
    internal int RenderTransparent(
        ID3D12GraphicsCommandList commandList,
        int frameIndex,
        Matrix4x4 viewProjection,
        Vector3 cameraPosition,
        Vector3 cameraForward,
        Vector3 cameraRight,
        Vector3 cameraUp,
        float elapsedSeconds)
        => RenderTransparentCore(
            commandList,
            frameIndex,
            viewProjection,
            cameraPosition,
            cameraForward,
            cameraRight,
            cameraUp,
            elapsedSeconds,
            TransparentWaterPartition.All,
            waterProbe: null);

    /// <summary>
    ///     Draws only alpha geometry wholly below its local authored water surface. The water pass
    ///     writes no depth, so this complement must be recorded first or submerged decals/cards
    ///     composite over the surface.
    /// </summary>
    internal int RenderTransparentBelowWater(
        ID3D12GraphicsCommandList commandList,
        int frameIndex,
        Matrix4x4 viewProjection,
        Vector3 cameraPosition,
        Vector3 cameraForward,
        Vector3 cameraRight,
        Vector3 cameraUp,
        float elapsedSeconds,
        IWaterHeightProbe waterProbe)
        => RenderTransparentCore(
            commandList,
            frameIndex,
            viewProjection,
            cameraPosition,
            cameraForward,
            cameraRight,
            cameraUp,
            elapsedSeconds,
            TransparentWaterPartition.WhollyBelow,
            waterProbe);

    /// <summary>Draws the intersecting/above-water complement after the authored water pass.</summary>
    internal int RenderTransparentAtOrAboveWater(
        ID3D12GraphicsCommandList commandList,
        int frameIndex,
        Matrix4x4 viewProjection,
        Vector3 cameraPosition,
        Vector3 cameraForward,
        Vector3 cameraRight,
        Vector3 cameraUp,
        float elapsedSeconds,
        IWaterHeightProbe waterProbe)
        => RenderTransparentCore(
            commandList,
            frameIndex,
            viewProjection,
            cameraPosition,
            cameraForward,
            cameraRight,
            cameraUp,
            elapsedSeconds,
            TransparentWaterPartition.NotWhollyBelow,
            waterProbe);

    private int RenderTransparentCore(
        ID3D12GraphicsCommandList commandList,
        int frameIndex,
        Matrix4x4 viewProjection,
        Vector3 cameraPosition,
        Vector3 cameraForward,
        Vector3 cameraRight,
        Vector3 cameraUp,
        float elapsedSeconds,
        TransparentWaterPartition waterPartition,
        IWaterHeightProbe? waterProbe)
    {
        if (_transparent.Length == 0 || !BindFrame(commandList, frameIndex, viewProjection))
        {
            return 0;
        }

        var draws = 0;
        ID3D12PipelineState? currentPipeline = null;
        foreach (var range in _transparentRenderOrderRanges)
        {
            var selectedCount = 0;
            for (var index = range.Start; index < range.Start + range.Length; index++)
            {
                var draw = _transparent[index];
                if (!BelongsToWaterPartition(draw, cameraPosition.Z, waterPartition, waterProbe))
                {
                    continue;
                }

                var destination = range.Start + selectedCount++;
                _transparentSortOrder[destination] = index;
                // Ascending negative distance is farthest first, and avoids a closure/comparer per frame.
                _transparentSortKeys[destination] = -Vector3.DistanceSquared(
                    draw.Submesh.LocalBoundsCenter,
                    cameraPosition);
            }

            // RenderOrder is the primary authored layer boundary. Filter and depth-sort only within
            // that group: both water partitions therefore retain the Bethesda ordering contract.
            Array.Sort(
                _transparentSortKeys,
                _transparentSortOrder,
                range.Start,
                selectedCount);

            for (var position = range.Start; position < range.Start + selectedCount; position++)
            {
                var submesh = _transparent[_transparentSortOrder[position]].Submesh;
                var pipeline = _pipelines.GetBlendPipeline(
                    submesh.SrcBlendMode,
                    submesh.DstBlendMode,
                    submesh.DoubleSided,
                    submesh.IsDecal,
                    depthTestOff: submesh.DepthTestOff);
                if (Draw(
                        commandList,
                        frameIndex,
                        submesh,
                        pipeline,
                        cameraPosition,
                        cameraForward,
                        cameraRight,
                        cameraUp,
                        elapsedSeconds,
                        false,
                        ref currentPipeline))
                {
                    draws++;
                }
            }
        }

        return draws;
    }

    private static bool BelongsToWaterPartition(
        ViewerDraw draw,
        float cameraZ,
        TransparentWaterPartition partition,
        IWaterHeightProbe? probe)
    {
        if (partition == TransparentWaterPartition.All || probe is null)
        {
            return true;
        }

        var below = WaterTransparencyPartition.IsWhollyBelow(
            probe,
            draw.Submesh.LocalBoundsCenter.X,
            draw.Submesh.LocalBoundsCenter.Y,
            draw.BoundsMaxZ,
            cameraZ);
        return partition == TransparentWaterPartition.WhollyBelow ? below : !below;
    }

    private static ViewerDraw CreateViewerDraw(
        BethesdaViewerPosedScene12 posedScene,
        CachedSubmesh12 submesh)
    {
        var semantics = ResolveNativeSemantics(posedScene, submesh);
        var center = submesh.LocalBoundsCenter;
        var minZ = center.Z - submesh.LocalBoundsRadius;
        var maxZ = center.Z + submesh.LocalBoundsRadius;

        // Current-pose vertices are already baked into viewer scene space. Their exact vertical
        // extent avoids the established sphere-apex false negative for flat decals. Billboards can
        // rotate out of that static AABB, so they deliberately retain the conservative sphere.
        var sourceIndex = submesh.MaterializationSourceIndex;
        if (!submesh.IsBillboard &&
            (uint)sourceIndex < (uint)posedScene.Mesh.Submeshes.Count)
        {
            var vertices = posedScene.Mesh.Submeshes[sourceIndex].Vertices;
            if (vertices.Length > 0)
            {
                minZ = float.PositiveInfinity;
                maxZ = float.NegativeInfinity;
                foreach (var vertex in vertices)
                {
                    minZ = MathF.Min(minZ, vertex.Position.Z);
                    maxZ = MathF.Max(maxZ, vertex.Position.Z);
                }
            }
        }

        return new ViewerDraw(submesh, semantics, minZ, maxZ);
    }

    private static OpaqueSpecializationRoute ResolveOpaqueSpecialization(
        Core.Games.BethesdaGame game,
        ViewerDraw draw,
        ReferencePipelineFactory12 pipelines)
    {
        var submesh = draw.Submesh;
        // These axes require different depth/blend/raster state or generic interpolators. Keep the
        // specialized families ordinary opaque/cutout only, even if a future policy becomes broader.
        if (draw.NativeSemantics.SkyType is not null ||
            submesh.AlphaBlend ||
            submesh.IsDecal ||
            submesh.DepthTestOff ||
            draw.NativeSemantics.NativeAlphaRenderMode == NifAlphaRenderMode.AlphaToCoverage ||
            submesh.MaterialAlphaController is not null)
        {
            return default;
        }

        var facts = new ModernStandardOpaqueShaderFacts(
            Game: game,
            HeatmapEnabled: false,
            IsScatteredGrass: false,
            AlphaBlend: submesh.AlphaBlend,
            IsDecal: submesh.IsDecal,
            IsEmissive: submesh.IsEmissive,
            IsLighting30: submesh.IsLighting30,
            HasLighting30GlowMap: submesh.Lighting30GlowMap is not null,
            HasEffectFalloff: submesh.HasEffectFalloff,
            IsEffectTintNeutral: submesh.EffectTint == Vector3.One,
            HasSoftParticle: submesh.SoftParticle.Enabled,
            IsBillboard: submesh.IsBillboard,
            IsLeafBillboard: submesh.IsLeafBillboard,
            IsTallGrass: submesh.IsTallGrass,
            IsParticle: submesh.ParticleCenters is not null || submesh.LiveParticles is not null,
            HasRuntimeSpeedTreeLod: submesh.SpeedTreeLod is not null,
            HasClassicBasicShader: submesh.ClassicBasicShaderMode != FnvClassicBasicShaderMode.None,
            HasClassicEnvironmentMap:
                submesh.ClassicEnvMap is not null ||
                submesh.ClassicEnvMask is not null ||
                submesh.ClassicEnvMapUsesWindowReflection ||
                submesh.ClassicEnvMapIsSphereMap,
            HasClassicParallax: submesh.ClassicParallaxHeightMap is not null,
            HasGradientMap: submesh.GradientMap is not null,
            StarfieldMaterialPath: submesh.StarfieldMaterialPath,
            HasDerivedStarfieldNormal: submesh.HasDerivedStarfieldNormal,
            TextureFeatureMask: (uint)MathF.Round(submesh.TextureState.Z),
            HasBump: submesh.HasBump,
            HasSpecularMap: submesh.SpecularMap is not null,
            SpecularExponent: submesh.Specular.W,
            ModernEnvironmentMapDeclared: submesh.EnvMap is not null,
            ModernEnvironmentMapScale: submesh.EnvMapScale,
            WrapTextureU: !submesh.ClampTextureU,
            WrapTextureV: !submesh.ClampTextureV,
            AlphaTestEnabled: submesh.AlphaState.Y >= 0f,
            AlphaTestFunction: submesh.AlphaTestFunction,
            DoubleSided: submesh.DoubleSided);
        var variant = ModernStandardOpaqueShaderPolicy.Resolve(in facts);
        if (TryResolveStarfieldVariant(variant, out var alphaGreater, out var doubleSided))
        {
            pipelines.TryGetDirectStarfieldDiffuseLitPso(
                alphaGreater,
                doubleSided,
                out var starfieldPipeline);
            return new OpaqueSpecializationRoute(
                OpaqueSpecializationFamily.StarfieldDiffuseLit,
                starfieldPipeline);
        }

        if (variant is
            ModernStandardOpaqueShaderVariant.SingleSidedOpaque or
            ModernStandardOpaqueShaderVariant.SingleSidedGreaterCutout or
            ModernStandardOpaqueShaderVariant.DoubleSidedGreaterCutout)
        {
            pipelines.TryGetDirectModernStandardOpaquePso(variant, out var falloutPipeline);
            return new OpaqueSpecializationRoute(
                OpaqueSpecializationFamily.FalloutModernStandard,
                falloutPipeline);
        }

        return default;
    }

    private static bool TryResolveStarfieldVariant(
        ModernStandardOpaqueShaderVariant variant,
        out bool alphaGreater,
        out bool doubleSided)
    {
        (alphaGreater, doubleSided) = variant switch
        {
            ModernStandardOpaqueShaderVariant.StarfieldSingleSidedOpaque => (false, false),
            ModernStandardOpaqueShaderVariant.StarfieldDoubleSidedOpaque => (false, true),
            ModernStandardOpaqueShaderVariant.StarfieldSingleSidedGreaterCutout => (true, false),
            ModernStandardOpaqueShaderVariant.StarfieldDoubleSidedGreaterCutout => (true, true),
            _ => default,
        };
        return variant is >= ModernStandardOpaqueShaderVariant.StarfieldSingleSidedOpaque and
            <= ModernStandardOpaqueShaderVariant.StarfieldDoubleSidedGreaterCutout;
    }

    private static DecodedBethesdaViewerSubmeshSemantics12 ResolveNativeSemantics(
        BethesdaViewerPosedScene12 posedScene,
        CachedSubmesh12 submesh)
    {
        var sourceIndex = submesh.MaterializationSourceIndex;
        if ((uint)sourceIndex >= (uint)posedScene.Source.MeshParts.Count)
        {
            throw new InvalidDataException(
                $"Materialized submesh has no matching native viewer semantics (source index {sourceIndex}).");
        }

        return posedScene.Source.MeshParts[sourceIndex].NativeSemantics;
    }

    private unsafe bool BindFrame(
        ID3D12GraphicsCommandList commandList,
        int frameIndex,
        Matrix4x4 viewProjection)
    {
        if (!_ringBuffer.TryAllocate(
                frameIndex,
                PerFrameByteSize,
                out var allocation,
                GpuRingBuffer12.CbAlignment))
        {
            return false;
        }

        *(Matrix4x4*)allocation.CpuPtr = viewProjection;
        var windMatrices = (Matrix4x4*)((byte*)allocation.CpuPtr + 64);
        for (var index = 0; index < 4; index++)
        {
            windMatrices[index] = Matrix4x4.Identity;
        }

        commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commandList.SetGraphicsRootConstantBufferView(
            GpuRootSignature12.Slots.PerFrameCbv,
            allocation.GpuAddress);
        GpuRootSignature12.SetGraphicsBindlessTables(
            commandList,
            _descriptorHeap.BindlessHeapStartGpu);
        return true;
    }

    private unsafe bool Draw(
        ID3D12GraphicsCommandList commandList,
        int frameIndex,
        CachedSubmesh12 submesh,
        ID3D12PipelineState pipeline,
        Vector3 cameraPosition,
        Vector3 cameraForward,
        Vector3 cameraRight,
        Vector3 cameraUp,
        float elapsedSeconds,
        bool nativeAlphaToCoverage,
        ref ID3D12PipelineState? currentPipeline)
    {
        var indexCount = submesh.EffectiveIndexCount;
        if (indexCount <= 0 ||
            !_ringBuffer.TryAllocate(
                frameIndex,
                PerDrawByteSize,
                out var allocation,
                GpuRingBuffer12.CbAlignment))
        {
            return false;
        }

        var alphaState = submesh.AlphaState;
        if (nativeAlphaToCoverage && submesh.AlphaTest)
        {
            // The shared decoded payload intentionally downgrades A2C to Blend for placed-world
            // compatibility, which disables its alpha-test constants during cache materialization.
            // Restore the viewer's authored comparison for the dedicated A2C PSO. Hair without an
            // authored test keeps the -1 sentinel and lets output alpha drive hardware coverage.
            alphaState.X = submesh.AlphaTestThreshold;
            alphaState.Y = submesh.AlphaTestFunction;
        }
        if (submesh.MaterialAlphaController is { } alphaController)
        {
            alphaState.Z = alphaController.ResolveTargetAlpha(elapsedSeconds, animationsEnabled: true);
        }

        var world = ResolveWorld(submesh, cameraPosition, cameraForward);
        var constants = new PerDrawConstants
        {
            World = world,
            AlphaState = alphaState,
            RenderState = submesh.RenderState,
            TextureState = submesh.TextureState,
            TexIndices = new TexIndexQuad(
                submesh.Diffuse.BindlessIndex,
                submesh.Normal.BindlessIndex,
                submesh.StarfieldOpacity?.BindlessIndex ??
                submesh.ClassicParallaxHeightMap?.BindlessIndex ??
                submesh.ClassicEnvMask?.BindlessIndex ??
                submesh.SpecularMap?.BindlessIndex ?? 0,
                submesh.GradientMap?.BindlessIndex ??
                submesh.Lighting30GlowMap?.BindlessIndex ?? 0),
            Specular = submesh.Specular,
            CameraRight = new Vector4(cameraRight, 0f),
            CameraUp = new Vector4(cameraUp, 0f),
            EffectTint = new Vector4(submesh.EffectTint, submesh.HasEffectFalloff ? 1f : 0f),
            EffectFalloff = ResolveEffectFalloff(submesh),
            EnvMap = submesh.EnvMapState,
            UvScroll = new Vector4(
                WrapUv(submesh.UvScrollVelocity.X, elapsedSeconds),
                WrapUv(submesh.UvScrollVelocity.Y, elapsedSeconds),
                1f,
                0f),
            // The standalone host currently exposes hardware depth but not a sampled scene-depth
            // alias. Keep soft-particle feathering disabled rather than binding an invalid SRV.
            SoftParticle = Vector4.Zero,
        };

        *(PerDrawConstants*)allocation.CpuPtr = constants;
        if (!ReferenceEquals(currentPipeline, pipeline))
        {
            commandList.SetPipelineState(pipeline);
            currentPipeline = pipeline;
        }
        commandList.SetGraphicsRootConstantBufferView(
            GpuRootSignature12.Slots.PerDrawCbv,
            allocation.GpuAddress);
        commandList.IASetVertexBuffers(0, submesh.EffectiveVertexBufferView);
        commandList.IASetIndexBuffer(submesh.EffectiveIndexBufferView);
        commandList.DrawIndexedInstanced((uint)indexCount, 1, 0, 0, 0);
        return true;
    }

    private static Matrix4x4 ResolveWorld(
        CachedSubmesh12 submesh,
        Vector3 cameraPosition,
        Vector3 cameraForward)
    {
        if (!submesh.IsBillboard)
        {
            return Matrix4x4.Identity;
        }

        var center = submesh.LocalBoundsCenter;
        var rotation = NifBillboardFacing.ResolveRotation(
            submesh.BillboardMode,
            submesh.BillboardFrontNormal,
            cameraPosition - center,
            cameraForward);
        rotation.Translation = center - Vector3.TransformNormal(center, rotation);
        return rotation;
    }

    private static Vector4 ResolveEffectFalloff(CachedSubmesh12 submesh)
    {
        if (submesh.StarfieldMaterialColor.IsConstantLerp)
        {
            return submesh.StarfieldMaterialColor.LinearTint;
        }

        if (submesh.HasBgsmEmission)
        {
            return new Vector4(
                submesh.BgsmEmissionColor,
                submesh.BgsmGlowMap is { } glowMap ? glowMap.BindlessIndex + 1f : 0f);
        }

        return submesh.HasEffectFalloff
            ? submesh.EffectFalloffParams
            : submesh.Lighting30Emission;
    }

    private static float WrapUv(float velocity, float elapsedSeconds)
    {
        var value = velocity * elapsedSeconds;
        return value - MathF.Floor(value);
    }

    private enum DepthDrawKind
    {
        Opaque = 0,
        DepthWritingBlend = 1,
    }

    private readonly record struct DepthOrderedDraw(
        ViewerDraw Draw,
        DepthDrawKind Kind,
        ID3D12PipelineState? SpecializedPipeline);

    private enum OpaqueSpecializationFamily
    {
        None,
        FalloutModernStandard,
        StarfieldDiffuseLit,
    }

    private readonly record struct OpaqueSpecializationRoute(
        OpaqueSpecializationFamily Family,
        ID3D12PipelineState? Pipeline);

    private enum TransparentWaterPartition
    {
        All,
        WhollyBelow,
        NotWhollyBelow,
    }

    private readonly record struct ViewerDraw(
        CachedSubmesh12 Submesh,
        DecodedBethesdaViewerSubmeshSemantics12 NativeSemantics,
        float BoundsMinZ,
        float BoundsMaxZ);
}
#endif
