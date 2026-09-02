namespace BethesdaMultitool.Core.Formats.Nif.Rendering;

/// <summary>
///     Central copy policy for <see cref="RenderableSubmesh" />. Export, preview, and NPC assembly
///     mutate geometry buffers while applying transforms and morphs, so every geometry array is
///     owned by the returned submesh. Extracted descriptor objects are shared deliberately: strings
///     are immutable, value-type render state is copied, and <see cref="NifShaderTextureMetadata" />,
///     material-controller, and particle-runtime graphs are consumed as read-only source descriptors.
/// </summary>
internal static class RenderableSubmeshCloner
{
    /// <summary>Creates an independent geometry copy while preserving every render semantic.</summary>
    internal static RenderableSubmesh DeepClone(RenderableSubmesh source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return CloneCore(source, source);
    }

    /// <summary>
    ///     Keeps hierarchy/skin geometry and identity while replacing its render state with the
    ///     fully resolved renderer extraction. Geometry and bind-pose arrays are still deep-cloned;
    ///     callers may therefore transform the result without mutating either input scene.
    ///     Shape name, bounds, source block, and every vertex/index buffer come from
    ///     <paramref name="geometry" />. Source path and typed sky classification prefer
    ///     <paramref name="renderState" /> but retain the geometry value when resolution supplied
    ///     no override. Every other writable property is owned by <paramref name="renderState" />.
    /// </summary>
    internal static RenderableSubmesh CloneGeometryWithRenderState(
        RenderableSubmesh geometry,
        RenderableSubmesh renderState)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(renderState);
        return CloneCore(geometry, renderState);
    }

    private static RenderableSubmesh CloneCore(
        RenderableSubmesh geometry,
        RenderableSubmesh renderState)
    {
        return new RenderableSubmesh
        {
            // Geometry/identity belongs to the hierarchy-preserving source.
            ShapeName = geometry.ShapeName,
            Positions = (float[])geometry.Positions.Clone(),
            LocalBounds = geometry.LocalBounds,
            Triangles = (ushort[])geometry.Triangles.Clone(),
            Normals = geometry.Normals is null ? null : (float[])geometry.Normals.Clone(),
            UVs = geometry.UVs is null ? null : (float[])geometry.UVs.Clone(),
            VertexColors = geometry.VertexColors is null ? null : (byte[])geometry.VertexColors.Clone(),
            Tangents = geometry.Tangents is null ? null : (float[])geometry.Tangents.Clone(),
            Bitangents = geometry.Bitangents is null ? null : (float[])geometry.Bitangents.Clone(),
            BindPosePositions = geometry.BindPosePositions is null
                ? null
                : (float[])geometry.BindPosePositions.Clone(),
            SourceBlockIndex = geometry.SourceBlockIndex,
            SourceNifPath = renderState.SourceNifPath ?? geometry.SourceNifPath,

            // Everything below is renderer state. Descriptor references are intentionally shared;
            // downstream consumers treat them as immutable extraction results.
            StarfieldMaterialColor = renderState.StarfieldMaterialColor,
            StarfieldMaterialAlpha = renderState.StarfieldMaterialAlpha,
            DiffuseTexturePath = renderState.DiffuseTexturePath,
            ClampTextureU = renderState.ClampTextureU,
            ClampTextureV = renderState.ClampTextureV,
            NormalMapTexturePath = renderState.NormalMapTexturePath,
            SpecularMapTexturePath = renderState.SpecularMapTexturePath,
            GradientMapTexturePath = renderState.GradientMapTexturePath,
            GradientMapV = renderState.GradientMapV,
            BgsmGlowMapTexturePath = renderState.BgsmGlowMapTexturePath,
            BgsmEmissionColor = renderState.BgsmEmissionColor,
            EnvironmentMapTexturePath = renderState.EnvironmentMapTexturePath,
            EnvironmentMapScale = renderState.EnvironmentMapScale,
            EnvironmentMapSmoothness = renderState.EnvironmentMapSmoothness,
            ClassicEnvironmentMapTexturePath = renderState.ClassicEnvironmentMapTexturePath,
            ClassicEnvironmentMaskTexturePath = renderState.ClassicEnvironmentMaskTexturePath,
            ClassicEnvironmentMapScale = renderState.ClassicEnvironmentMapScale,
            ClassicEnvironmentMapUsesWindowReflection = renderState.ClassicEnvironmentMapUsesWindowReflection,
            ClassicEnvironmentMapIsSphereMap = renderState.ClassicEnvironmentMapIsSphereMap,
            ClassicParallaxHeightMapTexturePath = renderState.ClassicParallaxHeightMapTexturePath,
            IsDecal = renderState.IsDecal,
            EffectTint = renderState.EffectTint,
            EffectFalloff = renderState.EffectFalloff,
            SoftParticleFalloffDepth = renderState.SoftParticleFalloffDepth,
            ShaderMetadata = renderState.ShaderMetadata,
            IsEmissive = renderState.IsEmissive,
            UseVertexColors = renderState.UseVertexColors,
            UseVertexAlphaForOpacity = renderState.UseVertexAlphaForOpacity,
            IsDoubleSided = renderState.IsDoubleSided,
            HasAlphaBlend = renderState.HasAlphaBlend,
            HasAlphaTest = renderState.HasAlphaTest,
            AlphaTestThreshold = renderState.AlphaTestThreshold,
            AlphaTestFunction = renderState.AlphaTestFunction,
            SrcBlendMode = renderState.SrcBlendMode,
            DstBlendMode = renderState.DstBlendMode,
            MaterialAlpha = renderState.MaterialAlpha,
            MaterialAlphaController = renderState.MaterialAlphaController,
            MaterialGlossiness = renderState.MaterialGlossiness,
            SpecularColor = renderState.SpecularColor,
            MaterialDiffuse = renderState.MaterialDiffuse,
            IsEyeEnvmap = renderState.IsEyeEnvmap,
            EnvMapScale = renderState.EnvMapScale,
            RenderOrder = renderState.RenderOrder,
            TintColor = renderState.TintColor,
            IsFaceGen = renderState.IsFaceGen,
            SubsurfaceColor = renderState.SubsurfaceColor,
            AnimatedEmissiveColor = renderState.AnimatedEmissiveColor,
            EmissiveColor = renderState.EmissiveColor,
            Lighting30EmissionColor = renderState.Lighting30EmissionColor,
            IsLighting30 = renderState.IsLighting30,
            Lighting30EmissionMultiplier = renderState.Lighting30EmissionMultiplier,
            Lighting30GlowMapTexturePath = renderState.Lighting30GlowMapTexturePath,
            UvScrollVelocity = renderState.UvScrollVelocity,
            SkyType = renderState.SkyType ?? geometry.SkyType,
            IsBillboard = renderState.IsBillboard,
            BillboardMode = renderState.BillboardMode,
            IsLeafBillboard = renderState.IsLeafBillboard,
            IsParticleCloud = renderState.IsParticleCloud,
            ParticleRuntime = renderState.ParticleRuntime,
            IsSpeedTreeBranch = renderState.IsSpeedTreeBranch,
            SpeedTreeWindSpeeds = renderState.SpeedTreeWindSpeeds,
            SpeedTreeLod = renderState.SpeedTreeLod,
            IsFarLodFallback = renderState.IsFarLodFallback
        };
    }
}
