using System.Numerics;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using Slfx77.Multitool.Core.Models;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>Applies the existing Bethesda export material policy once for both output paths.</summary>
internal static class NifMaterialPreparation
{
    /// <summary>Resolves and packs a surface with tint-aware identity at the legacy cache-hit boundary.</summary>
    /// <param name="submesh">The same prepared source geometry used by the legacy writer.</param>
    /// <param name="textureResolver">The export's source and material resolver.</param>
    /// <param name="materialCache">Results retained for this export only.</param>
    /// <param name="vertexLerpProjection">The existing vertex-color export projection.</param>
    /// <returns>A prepared surface; malformed inputs retain their existing failures.</returns>
    internal static NifPreparedMaterial Prepare(
        RenderableSubmesh submesh,
        NifTextureResolver textureResolver,
        Dictionary<NifMaterialCacheKey, NifPreparedMaterial> materialCache,
        StarfieldGlbVertexLerpProjectionResult vertexLerpProjection)
    {
        var isStarfieldWater = string.Equals(
            submesh.DiffuseTexturePath,
            RenderableSubmesh.WaterSurfaceTexturePath,
            StringComparison.Ordinal);
        var authoredSkyPreview = AuthoredSkyGlbPreviewProjection.AppliesTo(submesh);
        var diffuseTexture = !authoredSkyPreview &&
                             !isStarfieldWater &&
                             !string.IsNullOrWhiteSpace(submesh.DiffuseTexturePath)
            ? textureResolver.GetTexture(submesh.DiffuseTexturePath!)
            : null;
        // Textured tint is baked into pixels while the later base-color factor becomes white.
        // Retain that input separately so different baked images cannot share the legacy path key.
        var bakedDiffuseTint = diffuseTexture is not null ? submesh.TintColor : null;
        diffuseTexture = NpcGlbTintColorEncoder.BakeDiffuseTexture(submesh, diffuseTexture);
        if (vertexLerpProjection.OmitDiffuseTexture)
        {
            // With weight one CE2 returns interpolated vertex RGB exactly; retaining the albedo
            // image would make core glTF multiply it back in and change the authored result.
            diffuseTexture = null;
        }

        var starfieldColor = ResolveStarfieldColor(
            submesh,
            textureResolver,
            vertexLerpProjection);
        diffuseTexture = StarfieldGlbColorLerpBaker.BakeDiffuseTexture(diffuseTexture, starfieldColor);
        // Preserve whether Lerp was actually baked into authored RGB. AlphaSettings may synthesize
        // a white base texture later; that texture still needs the no-albedo Lerp factor rather than
        // being mistaken for already-baked colour.
        var starfieldLerpBakedIntoTexture = diffuseTexture is not null;
        var (starfieldAlpha, starfieldMaterialPath) = ResolveStarfieldAlpha(submesh, textureResolver);
        var starfieldEffectPolicy = !isStarfieldWater && starfieldMaterialPath is not null
            ? textureResolver.ResolveStarfieldEffectPolicy(starfieldMaterialPath)
            : default;
        var hasStaticStarfieldEffectAlpha =
            starfieldEffectPolicy.TryResolveStaticGlassAlphaBlend(out var starfieldEffectAlpha);
        var effectOpacityTexture = hasStaticStarfieldEffectAlpha &&
                                   starfieldEffectAlpha.OpacitySlot.TexturePath is { Length: > 0 } effectOpacityPath
            ? textureResolver.GetTexture(effectOpacityPath)
            : null;
        var effectAlphaBake = hasStaticStarfieldEffectAlpha
            ? StarfieldGlbOpacityBaker.BakeEffectAlpha(
                diffuseTexture,
                effectOpacityTexture,
                starfieldEffectAlpha)
            : new StarfieldGlbEffectAlphaBakeResult(diffuseTexture, false, 1f);
        hasStaticStarfieldEffectAlpha &= effectAlphaBake.Applied;

        var opacityTexture = !hasStaticStarfieldEffectAlpha &&
                             starfieldAlpha.IsLayer0OpacityCutout &&
                             starfieldMaterialPath is not null
            ? textureResolver.GetTexture(
                MaterialTexturePathResolver.BuildStarfieldOpacityMapRequest(starfieldMaterialPath))
            : null;
        var opacityBake = !hasStaticStarfieldEffectAlpha && starfieldAlpha.IsLayer0OpacityCutout
            ? StarfieldGlbOpacityBaker.Bake(diffuseTexture, opacityTexture)
            : new StarfieldGlbOpacityBakeResult(diffuseTexture, false);
        diffuseTexture = hasStaticStarfieldEffectAlpha
            ? effectAlphaBake.Texture
            : opacityBake.Texture;
        NpcGlbAlphaTexturePacker.PreparedAlphaTexture preparedAlpha;
        if (isStarfieldWater || authoredSkyPreview)
        {
            // KHR_materials_transmission models optical transparency; core alpha is geometric
            // coverage. A water sheet fully covers its triangles, so keep alpha OPAQUE/one instead
            // of multiplying the physical transmission by the old half-alpha visibility fallback.
            preparedAlpha = new NpcGlbAlphaTexturePacker.PreparedAlphaTexture(
                null,
                NifAlphaRenderMode.Opaque,
                0,
                false);
        }
        else if (hasStaticStarfieldEffectAlpha)
        {
            preparedAlpha = new NpcGlbAlphaTexturePacker.PreparedAlphaTexture(
                diffuseTexture,
                NifAlphaRenderMode.Blend,
                0,
                false);
        }
        else if (starfieldAlpha.IsLayer0OpacityCutout)
        {
            // PreparedAlphaTexture's byte threshold is the legacy NIF lane. Starfield keeps its
            // authored float below so LINEAR-filtered opacity is not quantized at the silhouette.
            preparedAlpha = new NpcGlbAlphaTexturePacker.PreparedAlphaTexture(
                diffuseTexture,
                opacityBake.Applied ? NifAlphaRenderMode.Cutout : NifAlphaRenderMode.Opaque,
                0,
                false);
        }
        else
        {
            preparedAlpha = NpcGlbAlphaTexturePacker.Prepare(submesh, diffuseTexture);
        }

        // A standalone Starfield water NIF has no WATR record from which to select authored noise
        // layers. Reuse only the shipped primary global normal already named by the source-backed
        // World Viewer approximation; portable GLB viewers receive it statically and the embedded
        // viewer scrolls it under an explicit approximation marker.
        string? normalTexturePath;
        if (authoredSkyPreview)
        {
            normalTexturePath = null;
        }
        else
        {
            normalTexturePath = isStarfieldWater
                ? StarfieldWaterMaterialRoute.MeshViewerPrimaryNormalTexturePath
                : submesh.NormalMapTexturePath;
        }

        var packedNormal = NpcGlbNormalMapPacker.ResolvePacked(textureResolver, normalTexturePath);
        var normalTexture = packedNormal.Texture;
        var shaderMetadata = submesh.ShaderMetadata;
        var starfieldOrmPolicy = !isStarfieldWater && starfieldMaterialPath is not null
            ? textureResolver.ResolveStarfieldOrmPolicy(starfieldMaterialPath)
            : default;
        var starfieldOrmState = default(StarfieldMaterialOrmState);
        var hasStaticStarfieldOrm = !isStarfieldWater &&
                                    !submesh.IsEmissive &&
                                    starfieldOrmPolicy.TryResolveStaticLayer0Orm(out starfieldOrmState);
        var starfieldOrm = hasStaticStarfieldOrm
            ? StarfieldGlbOrmPacker.Pack(
                starfieldOrmState,
                LoadStarfieldSlotTexture(textureResolver, starfieldOrmState.RoughnessSlot),
                LoadStarfieldSlotTexture(textureResolver, starfieldOrmState.MetalnessSlot),
                LoadStarfieldSlotTexture(textureResolver, starfieldOrmState.AmbientOcclusionSlot))
            : default;
        var bgsmEmissiveFactor = Vector3.One;
        var bgsmEmissiveStrength = 1f;
        var hasActiveBgsmEmission = !isStarfieldWater && TryEncodeGltfEmission(
            submesh.BgsmEmissionColor,
            out bgsmEmissiveFactor,
            out bgsmEmissiveStrength);
        var bgsmGlowTexture = hasActiveBgsmEmission &&
                              !string.IsNullOrWhiteSpace(submesh.BgsmGlowMapTexturePath)
            ? textureResolver.GetTexture(submesh.BgsmGlowMapTexturePath)
            : null;
        var hasExternalRegularBgsm = !isStarfieldWater && HasExternalRegularBgsmMaterial(submesh);
        var glowTexture = !isStarfieldWater &&
                          !string.IsNullOrWhiteSpace(shaderMetadata?.GlowMapPath)
            ? textureResolver.GetTexture(shaderMetadata.GlowMapPath)
            : null;
        var inlineEmissiveTexture = !isStarfieldWater &&
                                    !hasExternalRegularBgsm &&
                                    NpcGlbMaterialChannelDecider.ShouldExportGlowAsEmissive(
                                        submesh,
                                        shaderMetadata)
            ? glowTexture
            : null;
        // A regular BGSM owns glow enablement as well as its texture and colour/scale. Even an
        // inactive or malformed external material must not resurrect a stale inline slot-2 map.
        var emissiveTexture = hasActiveBgsmEmission ? bgsmGlowTexture : inlineEmissiveTexture;
        string? emissiveTexturePath;
        if (hasActiveBgsmEmission)
        {
            emissiveTexturePath = submesh.BgsmGlowMapTexturePath;
        }
        else
        {
            emissiveTexturePath = inlineEmissiveTexture != null ? shaderMetadata?.GlowMapPath : null;
        }

        var emissiveFactor = hasActiveBgsmEmission ? bgsmEmissiveFactor : Vector3.One;
        var emissiveStrength = hasActiveBgsmEmission ? bgsmEmissiveStrength : 1f;
        var heightTexture = !isStarfieldWater &&
                            !string.IsNullOrWhiteSpace(shaderMetadata?.HeightMapPath)
            ? textureResolver.GetTexture(shaderMetadata.HeightMapPath)
            : null;
        var environmentMaskTexture = !isStarfieldWater &&
                                     !string.IsNullOrWhiteSpace(shaderMetadata?.EnvironmentMaskPath)
            ? textureResolver.GetTexture(shaderMetadata.EnvironmentMaskPath)
            : null;
        var baseColor = authoredSkyPreview || isStarfieldWater
            ? Vector4.One
            : StarfieldGlbColorLerpBaker.BuildBaseColor(
                NpcGlbTintColorEncoder.BuildBaseColor(submesh, preparedAlpha.Texture != null),
                starfieldLerpBakedIntoTexture,
                starfieldColor);
        if (hasStaticStarfieldEffectAlpha)
        {
            // CE2 EffectSettings owns this alpha; inline NIF material alpha is not an additional
            // multiplier on the reference shader's Effect route.
            baseColor.W = effectAlphaBake.AlphaFactor;
        }

        var hasEnvironmentMapping = !isStarfieldWater &&
                                    NpcGlbMaterialTuning.HasEnvironmentMapping(submesh);
        var materialProfile = isStarfieldWater
            ? default
            : NpcGlbMaterialTuning.Derive(submesh, normalTexture, packedNormal.HasGlossAlpha);
        // Selecting the strict CE2 ORM lane is a one-way decision. If an authored image is missing
        // or dimensions disagree, emit neutral CE2 constructor factors and no ORM image; never fall
        // through to the legacy NPC normal-alpha/environment/height heuristics, which interpret
        // unrelated Starfield channels as plausible-looking gloss/specular/AO.
        float metallicFactor;
        if (authoredSkyPreview || isStarfieldWater)
        {
            metallicFactor = 0f;
        }
        else if (hasStaticStarfieldOrm)
        {
            metallicFactor = starfieldOrm.Applied ? starfieldOrm.MetallicFactor : 0f;
        }
        else
        {
            metallicFactor = materialProfile.MetallicFactor;
        }

        float roughnessFactor;
        if (authoredSkyPreview)
        {
            roughnessFactor = 1f;
        }
        else if (isStarfieldWater)
        {
            roughnessFactor = StarfieldWaterMaterialRoute.MeshViewerRoughness;
        }
        else if (hasStaticStarfieldOrm)
        {
            roughnessFactor = starfieldOrm.Applied ? starfieldOrm.RoughnessFactor : 0f;
        }
        else
        {
            roughnessFactor = materialProfile.RoughnessFactor;
        }

        var (clampTextureU, clampTextureV) = isStarfieldWater ||
                                             hasStaticStarfieldOrm ||
                                             hasStaticStarfieldEffectAlpha
            ? (false, false)
            : ResolveTextureAddressing(submesh, textureResolver);
        var alphaCutoff = opacityBake.Applied && starfieldAlpha.IsLayer0OpacityCutout
            ? ToGltfGreaterCutoff(starfieldAlpha.AlphaTestThreshold)
            : preparedAlpha.AlphaThreshold / 255f;
        var key = new NifMaterialCacheKey(
            submesh.DiffuseTexturePath,
            normalTexturePath,
            emissiveTexturePath,
            hasActiveBgsmEmission,
            emissiveFactor,
            emissiveStrength,
            isStarfieldWater ? null : shaderMetadata?.HeightMapPath,
            isStarfieldWater ? null : shaderMetadata?.EnvironmentMaskPath,
            submesh.IsEmissive && !isStarfieldWater,
            isStarfieldWater,
            authoredSkyPreview,
            submesh.UseVertexColors,
            submesh.IsDoubleSided || isStarfieldWater,
            preparedAlpha.RenderMode,
            preparedAlpha.AlphaThreshold,
            isStarfieldWater ? (byte)0 : submesh.AlphaTestFunction,
            preparedAlpha.HasTextureTransform,
            clampTextureU,
            clampTextureV,
            metallicFactor,
            roughnessFactor,
            materialProfile.SpecularFactor,
            starfieldColor,
            isStarfieldWater ? null : starfieldMaterialPath,
            starfieldAlpha,
            opacityBake.Applied,
            starfieldEffectPolicy,
            hasStaticStarfieldEffectAlpha,
            starfieldOrmPolicy,
            hasStaticStarfieldOrm,
            starfieldOrm.Applied,
            starfieldOrm.Texture is not null,
            starfieldOrm.HasAmbientOcclusion,
            vertexLerpProjection,
            baseColor,
            bakedDiffuseTint);

        if (materialCache.TryGetValue(key, out var material))
        {
            return material;
        }

        var materialName = submesh.ShapeName ?? "material";
        if (vertexLerpProjection.IsUnsupported)
        {
            // Keep malformed source data visible in the artifact. It must never receive the viewer
            // marker because the shader hook cannot recover a missing or incomplete RGBA stream.
            materialName += " [CE2 vertex Lerp omitted: missing or incomplete RGBA stream]";
        }
        else if (vertexLerpProjection.RequiresViewerShader)
        {
            materialName += " [CE2 varying vertex Lerp: exact in embedded Mesh Viewer; portable base fallback]";
        }

        if (starfieldEffectPolicy.IsResolved &&
            starfieldEffectPolicy.HasEffectSettings &&
            starfieldEffectPolicy.IsGlass &&
            !hasStaticStarfieldEffectAlpha)
        {
            materialName += " [CE2 glass alpha omitted: unsupported effect composition or missing opacity]";
        }

        if (authoredSkyPreview)
        {
            materialName += AuthoredSkyGlbPreviewProjection.NameSuffix;
        }

        material = new NifPreparedMaterial(key, materialName);
        var materialExtras = new JsonObject();
        if (vertexLerpProjection.RequiresViewerShader)
        {
            materialExtras[StarfieldGlbVertexLerpProjection.ViewerMaterialExtrasKey] = true;
        }

        if (isStarfieldWater)
        {
            materialName += " [CE2 water: global-normal physical preview (approx.)]";
            material.Name = materialName;
            materialExtras[StarfieldWaterMaterialRoute.MeshViewerMaterialExtrasKey] = true;
        }

        if (materialExtras.Count > 0)
        {
            material.Extras = materialExtras;
        }

        if ((!isStarfieldWater && submesh.IsEmissive) || authoredSkyPreview)
        {
            material.WithUnlitShader();
        }
        else
        {
            material.WithMetallicRoughnessShader();
            material.WithMetallicRoughness(
                metallicFactor,
                roughnessFactor);
        }

        if (isStarfieldWater)
        {
            // Standard glTF physical channels make the portable artifact reflective/transmissive
            // instead of an opaque white slab. These are deliberately neutral water optics, not a
            // claim that CE2's Water DXIL, CUR3, or material constants have been recovered.
            material.IndexOfRefraction = StarfieldWaterMaterialRoute.MeshViewerIndexOfRefraction;
            material.WithTransmission(StarfieldWaterMaterialRoute.MeshViewerTransmission);
            material.WithClearCoat(StarfieldWaterMaterialRoute.MeshViewerClearCoat);
            material.WithClearCoatRoughness(
                StarfieldWaterMaterialRoute.MeshViewerClearCoatRoughness);
        }

        // Generic alpha-blended materials stay single-sided to avoid unsorted back faces over front
        // faces. Authored sky domes and thin water sheets must remain visible from either side.
        material.WithDoubleSide(
            authoredSkyPreview || isStarfieldWater ||
            (submesh.IsDoubleSided &&
             preparedAlpha.RenderMode != NifAlphaRenderMode.Blend &&
             preparedAlpha.RenderMode != NifAlphaRenderMode.AlphaToCoverage));
        if (preparedAlpha.Texture != null)
        {
            var imageName = BuildBaseColorTextureName(
                submesh.DiffuseTexturePath,
                preparedAlpha.HasTextureTransform,
                starfieldColor.IsConstantLerp,
                opacityBake.Applied || hasStaticStarfieldEffectAlpha);
            var image = new NifPreparedImage(imageName, NpcGlbTextureEncoder.EncodePng(preparedAlpha.Texture));
            material.WithBaseColor(image, baseColor);
        }
        else
        {
            material.WithBaseColor(baseColor);
        }

        if ((isStarfieldWater || !submesh.IsEmissive) && normalTexture != null)
        {
            var image = new NifPreparedImage(BuildDerivedTextureName(normalTexturePath, "normal"), NpcGlbTextureEncoder.EncodePng(normalTexture));
            // ReSharper disable once RedundantArgumentDefaultValue -- Follow the shared policy if its value changes.
            material.WithNormal(image, NifNormalMapStrengthPolicy.GenericDefault);

            if (!isStarfieldWater && !hasStaticStarfieldOrm)
            {
                var metallicRoughnessTexture = NpcGlbMaterialTexturePacker.BuildMetallicRoughnessTexture(
                    normalTexture,
                    packedNormal.HasGlossAlpha,
                    environmentMaskTexture,
                    hasEnvironmentMapping);
                if (metallicRoughnessTexture != null)
                {
                    var metallicRoughnessImage = new NifPreparedImage(BuildDerivedTextureName(submesh.NormalMapTexturePath, "metallicRoughness"), NpcGlbTextureEncoder.EncodePng(metallicRoughnessTexture));
                    material.WithMetallicRoughness(
                        metallicRoughnessImage,
                        materialProfile.MetallicFactor,
                        materialProfile.RoughnessFactor);
                }
            }

            if (!isStarfieldWater && !hasStaticStarfieldOrm)
            {
                var specularFactorTexture = NpcGlbMaterialTexturePacker.BuildSpecularFactorTexture(
                    normalTexture,
                    packedNormal.HasGlossAlpha,
                    environmentMaskTexture,
                    hasEnvironmentMapping);
                if (specularFactorTexture != null)
                {
                    var specularFactorImage = new NifPreparedImage(BuildDerivedTextureName(submesh.NormalMapTexturePath, "specular"), NpcGlbTextureEncoder.EncodePng(specularFactorTexture));
                    material.WithSpecularFactor(specularFactorImage, materialProfile.SpecularFactor);
                }
            }
        }
        else if (!isStarfieldWater &&
                 !submesh.IsEmissive &&
                 environmentMaskTexture != null &&
                 hasEnvironmentMapping)
        {
            if (!hasStaticStarfieldOrm)
            {
                var metallicRoughnessTexture = NpcGlbMaterialTexturePacker.BuildMetallicRoughnessTexture(
                    null,
                    false,
                    environmentMaskTexture,
                    hasEnvironmentMapping);
                if (metallicRoughnessTexture != null)
                {
                    var metallicRoughnessImage = new NifPreparedImage(BuildDerivedTextureName(shaderMetadata?.EnvironmentMaskPath, "metallicRoughness"), NpcGlbTextureEncoder.EncodePng(metallicRoughnessTexture));
                    material.WithMetallicRoughness(
                        metallicRoughnessImage,
                        materialProfile.MetallicFactor,
                        materialProfile.RoughnessFactor);
                }
            }

            if (!hasStaticStarfieldOrm)
            {
                var specularFactorTexture = NpcGlbMaterialTexturePacker.BuildSpecularFactorTexture(
                    null,
                    false,
                    environmentMaskTexture,
                    hasEnvironmentMapping);
                if (specularFactorTexture != null)
                {
                    var specularFactorImage = new NifPreparedImage(BuildDerivedTextureName(shaderMetadata?.EnvironmentMaskPath, "specular"), NpcGlbTextureEncoder.EncodePng(specularFactorTexture));
                    material.WithSpecularFactor(specularFactorImage, materialProfile.SpecularFactor);
                }
            }
        }

        if (!isStarfieldWater &&
            !submesh.IsEmissive &&
            starfieldOrm.Applied &&
            starfieldOrm.Texture is { } ormTexture)
        {
            var ormImage = new NifPreparedImage(BuildDerivedTextureName(starfieldMaterialPath, "starfieldOrm"), NpcGlbTextureEncoder.EncodePng(ormTexture));
            material.WithMetallicRoughness(
                ormImage,
                starfieldOrm.MetallicFactor,
                starfieldOrm.RoughnessFactor);
            if (starfieldOrm.HasAmbientOcclusion)
            {
                // glTF reads occlusion from R and metallic-roughness from B/G, so one packed image
                // can back both texture slots without changing CE2's individual red-channel values.
                material.WithOcclusion(ormImage);
            }
        }

        if (!isStarfieldWater && !submesh.IsEmissive && emissiveTexture != null)
        {
            var emissiveImage = new NifPreparedImage(BuildDerivedTextureName(emissiveTexturePath, "emissive"), NpcGlbTextureEncoder.EncodePng(emissiveTexture));
            material.WithEmissive(emissiveImage, emissiveFactor, emissiveStrength);
        }
        else if (!isStarfieldWater && !submesh.IsEmissive && hasActiveBgsmEmission)
        {
            // BGSM can carry a lit constant emission with no glow map. An authored map that cannot
            // be resolved follows the renderer's white-texture fallback and therefore reaches the
            // same constant term. Keep both in glTF emissive, never the legacy unlit route.
            material.WithEmissive(emissiveFactor, emissiveStrength);
        }

        if (!isStarfieldWater &&
            !submesh.IsEmissive &&
            heightTexture != null &&
            !hasStaticStarfieldOrm)
        {
            var occlusionTexture = NpcGlbMaterialTexturePacker.BuildOcclusionTexture(heightTexture);
            if (occlusionTexture != null)
            {
                var occlusionImage = new NifPreparedImage(BuildDerivedTextureName(shaderMetadata?.HeightMapPath, "occlusion"), NpcGlbTextureEncoder.EncodePng(occlusionTexture));
                material.WithOcclusion(occlusionImage, 0.35f);
            }
        }

        switch (preparedAlpha.RenderMode)
        {
            case NifAlphaRenderMode.Blend:
                material.WithAlpha(SceneAlphaMode.Blend);
                break;
            case NifAlphaRenderMode.Cutout:
                material.WithAlpha(SceneAlphaMode.Mask, alphaCutoff);
                break;
            case NifAlphaRenderMode.AlphaToCoverage:
                // glTF has no native A2C; map to pure BLEND. The earlier two-primitive
                // MASK depth-prepass + BLEND color pass approximation gave correct
                // depth-write occlusion at hair-card intersections but produced a hard
                // visible boundary between the MASK opaque core and the BLEND soft halo.
                // Pure BLEND is the cleaner trade-off — soft strand-aligned silhouettes
                // matching the rasterizer's appearance, at the cost of z-fighting where
                // hair cards genuinely intersect. The
                // rasterizer's A2C stochastic dither stays in place for the WinUI
                // viewer; this is the GLB-export branch only.
                material.WithAlpha(SceneAlphaMode.Blend);
                break;
        }

        material.ClampU = clampTextureU;
        material.ClampV = clampTextureV;

        materialCache[key] = material;
        return material;
    }

    /// <summary>Uses external BGSM/BGEM tiling when present, otherwise the carried source addressing flags.</summary>
    /// <param name="submesh">The source surface and optional external material path.</param>
    /// <param name="textureResolver">Resolves an authored external material without changing source precedence.</param>
    /// <returns>Independent U and V clamp choices.</returns>
    private static (bool ClampU, bool ClampV) ResolveTextureAddressing(
        RenderableSubmesh submesh,
        NifTextureResolver textureResolver)
    {
        var materialPath = submesh.ShaderMetadata?.MaterialPath;
        if (string.IsNullOrWhiteSpace(materialPath) &&
            submesh.DiffuseTexturePath is { } diffuse &&
            (diffuse.EndsWith(".bgsm", StringComparison.OrdinalIgnoreCase) ||
             diffuse.EndsWith(".bgem", StringComparison.OrdinalIgnoreCase)))
        {
            materialPath = diffuse;
        }

        return !string.IsNullOrWhiteSpace(materialPath) &&
               textureResolver.TryGetMaterial(materialPath) is { } material
            ? (!material.TileU, !material.TileV)
            : (submesh.ClampTextureU, submesh.ClampTextureV);
    }

    /// <summary>Loads an authored CE2 texture slot; replacement-only and absent slots need no image.</summary>
    /// <param name="textureResolver">The export's source resolver.</param>
    /// <param name="slot">The already resolved material slot.</param>
    /// <returns>Decoded pixels, or null when the slot has no image or its source cannot resolve it.</returns>
    private static DecodedTexture? LoadStarfieldSlotTexture(
        NifTextureResolver textureResolver,
        StarfieldMaterialSlot slot)
    {
        return slot.TexturePath is { Length: > 0 } path
            ? textureResolver.GetTexture(path)
            : null;
    }

    /// <summary>Selects the existing water, vertex projection, carried constant-Lerp or material-database color state.</summary>
    /// <param name="submesh">The source surface, including carried render state.</param>
    /// <param name="textureResolver">Resolves database policy only when the earlier source states do not supply it.</param>
    /// <param name="vertexLerpProjection">The previously classified vertex-color projection.</param>
    /// <returns>The same normalized constant color state used by the legacy writer.</returns>
    private static StarfieldMaterialColorRenderState ResolveStarfieldColor(
        RenderableSubmesh submesh,
        NifTextureResolver textureResolver,
        StarfieldGlbVertexLerpProjectionResult vertexLerpProjection)
    {
        // The water sentinel has already left the ordinary CE2 material route. Do not resurrect a
        // layer tint from ShaderMetadata.MaterialPath in specialized/older export scenes that did
        // not carry the extractor's cleared state.
        if (string.Equals(
                submesh.DiffuseTexturePath,
                RenderableSubmesh.WaterSurfaceTexturePath,
                StringComparison.Ordinal))
        {
            return default;
        }

        if (vertexLerpProjection.IsUniformTextureBake)
        {
            return StarfieldGlbColorLerpBaker.Normalize(vertexLerpProjection.ConstantLerpState);
        }

        var carriedState = StarfieldGlbColorLerpBaker.Normalize(submesh.StarfieldMaterialColor);
        if (carriedState.IsConstantLerp)
        {
            return carriedState;
        }

        // Current extraction carries its resolved state on the submesh. Retain a direct constant-
        // Lerp fallback for older/specialized export scenes that preserve only the material path.
        // Vertex Lerp requires the carried external RGBA stream and is projected above instead.
        var materialPath = submesh.ShaderMetadata?.MaterialPath;
        if (string.IsNullOrWhiteSpace(materialPath) ||
            !MaterialTexturePathResolver.IsStarfieldMaterialPath(materialPath))
        {
            materialPath = submesh.DiffuseTexturePath;
        }

        if (string.IsNullOrWhiteSpace(materialPath) ||
            !MaterialTexturePathResolver.IsStarfieldMaterialPath(materialPath))
        {
            return default;
        }

        var policy = textureResolver.ResolveStarfieldBaseColorPolicy(materialPath);
        return policy.TryResolveConstantLerp(out var linearTint)
            ? StarfieldGlbColorLerpBaker.Normalize(
                new StarfieldMaterialColorRenderState(
                    StarfieldMaterialColorRenderMode.ConstantLerp,
                    linearTint))
            : default;
    }

    /// <summary>Selects valid carried CE2 cutout state before resolving its material-database fallback.</summary>
    /// <param name="submesh">The source surface and optional carried cutout threshold.</param>
    /// <param name="textureResolver">Resolves fallback coverage policy.</param>
    /// <returns>The coverage state and material path, or their defaults for non-CE2 and water surfaces.</returns>
    private static (
        StarfieldMaterialAlphaRenderState State,
        string? MaterialPath) ResolveStarfieldAlpha(
            RenderableSubmesh submesh,
            NifTextureResolver textureResolver)
    {
        // Water's explicit physical-preview coverage/no-cutout state is authoritative. Re-resolving
        // the .mat here could otherwise replace it with an ordinary CE2 opacity cutout.
        if (string.Equals(
                submesh.DiffuseTexturePath,
                RenderableSubmesh.WaterSurfaceTexturePath,
                StringComparison.Ordinal))
        {
            return default;
        }

        var materialPath = submesh.ShaderMetadata?.MaterialPath;
        if (string.IsNullOrWhiteSpace(materialPath) ||
            !MaterialTexturePathResolver.IsStarfieldMaterialPath(materialPath))
        {
            materialPath = submesh.DiffuseTexturePath;
        }

        if (string.IsNullOrWhiteSpace(materialPath) ||
            !MaterialTexturePathResolver.IsStarfieldMaterialPath(materialPath))
        {
            return default;
        }

        var carried = submesh.StarfieldMaterialAlpha;
        if (carried.IsLayer0OpacityCutout &&
            float.IsFinite(carried.AlphaTestThreshold) &&
            carried.AlphaTestThreshold is > 0f and < 1f)
        {
            return (carried, materialPath);
        }

        var resolved = textureResolver.ResolveStarfieldAlphaPolicy(materialPath).ResolveRenderState();
        return (resolved, materialPath);
    }

    /// <summary>Translates a strict-greater float comparison into glTF's greater-or-equal cutoff.</summary>
    /// <param name="threshold">The validated authored floating-point threshold.</param>
    /// <returns>The next representable float without quantizing to texture-channel bytes.</returns>
    internal static float ToGltfGreaterCutoff(float threshold)
    {
        // CE2 keeps alpha strictly GREATER than the threshold; glTF MASK keeps alpha >= cutoff.
        // The PNG's UNORM8 texels are LINEAR-filtered by glTF, so sampled coverage is continuous;
        // quantizing the authored threshold to a byte shifts silhouettes between adjacent texels.
        // The next float makes >= exactly equivalent to > for the shader's float-domain predicate.
        return MathF.BitIncrement(threshold);
    }

    /// <summary>Retains the legacy image suffixes identifying baked opacity, alpha and constant-Lerp transforms.</summary>
    private static string BuildBaseColorTextureName(
        string? texturePath,
        bool hasAlphaTransform,
        bool hasStarfieldLerp,
        bool hasStarfieldOpacity)
    {
        if (hasStarfieldOpacity)
        {
            return BuildDerivedTextureName(
                texturePath,
                hasStarfieldLerp
                    ? "baseColor.starfieldLerp.opacity"
                    : "baseColor.starfieldOpacity");
        }

        if (hasStarfieldLerp)
        {
            return BuildDerivedTextureName(
                texturePath,
                hasAlphaTransform
                    ? "baseColor.starfieldLerp.alpha"
                    : "baseColor.starfieldLerp");
        }

        return hasAlphaTransform
            ? BuildDerivedTextureName(texturePath, "baseColor.alpha")
            : BuildTextureName(texturePath, "baseColor.png");
    }

    /// <summary>Uses the source stem and PNG extension, or the supplied fallback when no stem exists.</summary>
    private static string BuildTextureName(string? texturePath, string fallbackFileName)
    {
        if (string.IsNullOrWhiteSpace(texturePath))
        {
            return fallbackFileName;
        }

        var fileName = EnginePath.FileNameWithoutExtension(texturePath);
        return string.IsNullOrWhiteSpace(fileName)
            ? fallbackFileName
            : fileName + ".png";
    }

    /// <summary>Appends a channel suffix to the source stem without using that display label as cache identity.</summary>
    private static string BuildDerivedTextureName(string? texturePath, string suffix)
    {
        if (string.IsNullOrWhiteSpace(texturePath))
        {
            return suffix + ".png";
        }

        var fileName = EnginePath.FileNameWithoutExtension(texturePath);
        return string.IsNullOrWhiteSpace(fileName)
            ? suffix + ".png"
            : fileName + "." + suffix + ".png";
    }

    /// <summary>Recognizes a regular external BGSM from shader metadata or the diffuse-path fallback.</summary>
    /// <param name="submesh">The source surface whose external material owns glow policy.</param>
    /// <returns>True only for a BGSM path; effect BGEM and other material types return false.</returns>
    /// <exception cref="ArgumentNullException">The source surface is null.</exception>
    internal static bool HasExternalRegularBgsmMaterial(RenderableSubmesh submesh)
    {
        ArgumentNullException.ThrowIfNull(submesh);

        var materialPath = submesh.ShaderMetadata?.MaterialPath;
        if (string.IsNullOrWhiteSpace(materialPath) &&
            submesh.DiffuseTexturePath?.EndsWith(".bgsm", StringComparison.OrdinalIgnoreCase) == true)
        {
            materialPath = submesh.DiffuseTexturePath;
        }

        return materialPath?.EndsWith(".bgsm", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    ///     Encodes an effective linear emissive RGB into glTF's bounded emissive factor plus
    ///     <c>KHR_materials_emissive_strength</c>. Their product represents the input within normal
    ///     floating-point rounding for finite, non-negative authored values; malformed or inactive
    ///     state fails closed.
    /// </summary>
    /// <param name="effectiveEmission">The effective linear RGB emission after Bethesda's material resolution.</param>
    /// <param name="emissiveFactor">The bounded RGB multiplier, or zero for inactive/malformed input.</param>
    /// <param name="emissiveStrength">The HDR multiplier, or one for inactive/malformed input.</param>
    /// <returns>True for finite nonnegative active emission; false preserves the existing inactive fallback.</returns>
    internal static bool TryEncodeGltfEmission(
        Vector3 effectiveEmission,
        out Vector3 emissiveFactor,
        out float emissiveStrength)
    {
        emissiveFactor = Vector3.Zero;
        emissiveStrength = 1f;

        if (!float.IsFinite(effectiveEmission.X) ||
            !float.IsFinite(effectiveEmission.Y) ||
            !float.IsFinite(effectiveEmission.Z) ||
            effectiveEmission.X < 0f ||
            effectiveEmission.Y < 0f ||
            effectiveEmission.Z < 0f)
        {
            return false;
        }

        var peak = MathF.Max(effectiveEmission.X, MathF.Max(effectiveEmission.Y, effectiveEmission.Z));
        if (!(peak > 0f))
        {
            return false;
        }

        emissiveStrength = MathF.Max(1f, peak);
        emissiveFactor = effectiveEmission / emissiveStrength;
        return true;
    }

}
