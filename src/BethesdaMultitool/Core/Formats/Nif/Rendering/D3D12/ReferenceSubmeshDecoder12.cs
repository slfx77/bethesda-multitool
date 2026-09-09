using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Skinning;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Per-call context that is not intrinsic to one <see cref="RenderableSubmesh" />: placed-record
///     texture overrides, stream-version policy, and animation routes discovered beside extraction.
/// </summary>
internal readonly record struct ReferenceSubmeshDecodeOptions12(
    string? DiffuseTexturePath,
    string? NormalMapTexturePath,
    float? GradientMapVOverride = null,
    NifInfo? Nif = null,
    NifSubmeshSkin? Skin = null,
    PhysicsLiteSwayDescriptor? PhysicsLiteSway = null,
    NifRigidNodeAnimation? RigidNodeAnimation = null,
    bool IncludeParticleRuntime = false);

/// <summary>
///     The single CPU mapping from Bethesda-native <see cref="RenderableSubmesh" /> semantics to
///     the D3D12 reference renderer's decoded payload. Both placed-world decode and the standalone
///     native viewer use this mapper so alpha, vertex, specular, emission, environment, and shader
///     routing cannot drift between the two entry points.
/// </summary>
internal static class ReferenceSubmeshDecoder12
{
    internal static DecodedSubmesh12 Decode(
        RenderableSubmesh submesh,
        in ReferenceSubmeshDecodeOptions12 options)
    {
        ArgumentNullException.ThrowIfNull(submesh);

        var diffusePath = options.DiffuseTexturePath;
        var normalPath = options.NormalMapTexturePath;
        var alphaState = NifAlphaClassifier.Classify(submesh, null);
        var alphaRenderMode = alphaState.RenderMode == NifAlphaRenderMode.AlphaToCoverage
            ? NifAlphaRenderMode.Blend
            : alphaState.RenderMode;
        var hasBump = submesh.Tangents != null &&
                      submesh.Bitangents != null &&
                      !string.IsNullOrEmpty(normalPath);

        var specularColor = new Vector3(
            submesh.SpecularColor.R,
            submesh.SpecularColor.G,
            submesh.SpecularColor.B);
        var specularEnabled = NifSpecularPolicy.IsEnabled(submesh);
        // Emissive (no-lighting/effect) shapes use the existing specular-color constant lane as
        // their material glow tint. Animated emissive wins; authored black is the engine's
        // "unmodulated" case and therefore falls back to white instead of blacking out the mesh.
        if (submesh.IsEmissive)
        {
            var glow = submesh.AnimatedEmissiveColor ?? submesh.EmissiveColor;
            specularColor = glow is { } value && (value.R > 0f || value.G > 0f || value.B > 0f)
                ? new Vector3(value.R, value.G, value.B)
                : Vector3.One;
        }

        // The environment term still needs the _s mask/smoothness map when ordinary specular is
        // disabled. This matches the existing reference shader's FO4/FO76 route.
        var hasEnvironmentMap = !string.IsNullOrEmpty(submesh.EnvironmentMapTexturePath) &&
                                submesh.EnvironmentMapScale > 0f;
        var isTallGrass = string.Equals(
            submesh.ShaderMetadata?.PropertyType,
            "TallGrassShaderProperty",
            StringComparison.Ordinal);
        var localBounds = NifLocalBoundsResolver.Resolve(submesh);

        return new DecodedSubmesh12(
            // TallGrass, TREE_ANIM, and CE2 vertex-Lerp consume authored alpha as shader data,
            // never generic surface coverage. Their shader routes explicitly keep it out of the
            // pixel alpha test.
            GpuMeshUploader.BuildVertices(
                submesh,
                isTallGrass || submesh.IsTreeAnimation),
            submesh.Triangles,
            diffusePath,
            hasBump ? normalPath : null,
            hasBump,
            alphaRenderMode,
            alphaState.HasAlphaBlend,
            alphaState.HasAlphaTest,
            submesh.StarfieldMaterialAlpha.IsLayer0OpacityCutout
                ? submesh.StarfieldMaterialAlpha.AlphaTestThreshold
                : alphaState.AlphaTestThreshold / 255f,
            alphaState.AlphaTestFunction,
            alphaState.SrcBlendMode,
            alphaState.DstBlendMode,
            alphaState.MaterialAlpha,
            submesh.IsDoubleSided,
            submesh.IsEmissive,
            localBounds.Center,
            localBounds.Radius,
            submesh.IsBillboard,
            specularColor,
            submesh.MaterialGlossiness,
            specularEnabled,
            submesh.IsLeafBillboard,
            alphaState.DepthWritingBlend,
            specularEnabled || hasEnvironmentMap ? submesh.SpecularMapTexturePath : null,
            submesh.GradientMapTexturePath,
            options.GradientMapVOverride is { } remapV && submesh.GradientMapTexturePath is not null
                ? remapV
                : submesh.GradientMapV,
            submesh.IsDecal,
            new Vector3(submesh.EffectTint.R, submesh.EffectTint.G, submesh.EffectTint.B),
            submesh.EffectFalloff is { } falloff
                ? new Vector4(
                    falloff.StartAngle,
                    falloff.StopAngle,
                    falloff.StartOpacity,
                    falloff.StopOpacity)
                : default,
            submesh.EffectFalloff is not null,
            hasEnvironmentMap
                ? submesh.EnvironmentMapTexturePath
                : null,
            hasEnvironmentMap ? submesh.EnvironmentMapScale : 0f,
            hasEnvironmentMap
                ? submesh.EnvironmentMapSmoothness
                : 0f,
            submesh.UvScrollVelocity,
            options.Skin,
            submesh.IsSpeedTreeBranch,
            submesh.SpeedTreeWindSpeeds,
            submesh.ClampTextureU,
            submesh.ClampTextureV,
            submesh.IsParticleCloud,
            submesh.SoftParticleFalloffDepth,
            submesh.MaterialAlphaController,
            options.PhysicsLiteSway,
            options.RigidNodeAnimation,
            options.IncludeParticleRuntime ? submesh.ParticleRuntime : null,
            submesh.SpeedTreeLod,
            submesh.IsLighting30,
            submesh.Lighting30GlowMapTexturePath,
            submesh.Lighting30EmissionColor is { } lighting30Emission
                ? new Vector3(lighting30Emission.R, lighting30Emission.G, lighting30Emission.B)
                : Vector3.Zero,
            submesh.Lighting30EmissionMultiplier,
            isTallGrass,
            submesh.ClassicEnvironmentMapTexturePath,
            submesh.ClassicEnvironmentMaskTexturePath,
            submesh.ClassicEnvironmentMapScale,
            submesh.ClassicEnvironmentMapUsesWindowReflection,
            ClassicEnvironmentMapIsSphereMap: submesh.ClassicEnvironmentMapIsSphereMap,
            ClassicParallaxHeightMapTexturePath: submesh.ClassicParallaxHeightMapTexturePath,
            ClassicBasicShaderMode: options.Nif is not null
                ? FnvClassicBasicShaderPolicy.Resolve(
                    options.Nif,
                    submesh,
                    diffusePath,
                    normalPath)
                : FnvClassicBasicShaderMode.None,
            SourceBlockIndex: submesh.SourceBlockIndex,
            BillboardMode: submesh.BillboardMode,
            EngineZWriteOff: alphaState.EngineZWriteOff,
            DepthTestOff: alphaState.DepthTestOff,
            // Emissive material output already uses the glow-tint constant above. Applying the
            // legacy diffuse lane too would incorrectly multiply that output.
            MaterialDiffuse: !submesh.IsEmissive && submesh.MaterialDiffuse is { } materialDiffuse
                ? new Vector3(materialDiffuse.R, materialDiffuse.G, materialDiffuse.B)
                : null,
            StarfieldMaterialColor: submesh.StarfieldMaterialColor,
            StarfieldMaterialAlpha: submesh.StarfieldMaterialAlpha,
            BgsmGlowMapTexturePath: submesh.BgsmGlowMapTexturePath,
            BgsmEmissionColor: submesh.BgsmEmissionColor,
            UsesExternalEmittance: submesh.UsesExternalEmittance,
            ExternalEmittanceInfluence: submesh.ExternalEmittanceInfluence,
            IsTreeAnimation: submesh.IsTreeAnimation,
            UsesOblivionOrdinarySpecularPolicy:
            OblivionOrdinarySpecularPolicy.IsEligible(submesh, diffusePath, normalPath),
            OblivionHairLayerTexturePath:
            OblivionHairLayerPolicy.ResolveTexturePath(submesh, diffusePath));
    }
}
