using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Effects;

/// <summary>Why a blended NIF submesh is allowed to use scene-depth feathering.</summary>
internal enum NifSoftParticleSource
{
    None = 0,
    AuthoredSoftEffect = 1,
    ParticleSystemFallback = 2,
    EffectsGeometryFallback = 3,
}

/// <summary>
///     Which shader output must approach its neutral value as a soft particle intersects opaque
///     geometry. Alpha is correct for SRC_ALPHA blends, black is neutral for ONE-source additive
///     contributions, and white is neutral for multiplicative destination modulation.
/// </summary>
internal enum NifSoftParticleFadeTarget
{
    Alpha = 0,
    ColorTowardZero = 1,
    ColorTowardWhite = 2,
}

internal readonly record struct NifSoftParticleSettings(
    NifSoftParticleSource Source,
    NifSoftParticleFadeTarget FadeTarget,
    float FalloffDepth)
{
    public bool Enabled => Source != NifSoftParticleSource.None && FalloffDepth > 0f;

    public static NifSoftParticleSettings Disabled =>
        new(NifSoftParticleSource.None, NifSoftParticleFadeTarget.Alpha, 0f);
}

/// <summary>Pure policy inputs retained by the decoded/cached NIF pipeline.</summary>
internal readonly record struct NifSoftParticleCandidate(
    string? ModelPath,
    bool AlphaBlend,
    bool DepthWritingBlend,
    bool IsDecal,
    bool IsParticleCloud,
    bool IsBillboard,
    bool HasEffectFalloff,
    float AuthoredFalloffDepth,
    byte SrcBlendMode,
    byte DstBlendMode);

/// <summary>
///     Restricts scene-depth feathering to authored soft effects and recognizable particle/effect
///     geometry. Ordinary transparent architecture, alpha decals, and foliage never become soft
///     merely because they share the blended pass.
/// </summary>
internal static class NifSoftParticlePolicy
{
    // Skyrim's inline BSEffectShaderProperty default is 100 game units. FNV has no serialized soft
    // depth on its legacy effect properties, so this is the explicitly-labelled engine-family
    // fallback for particle systems and the small set of legacy effect-mesh signals below.
    internal const float DefaultFalloffDepth = 100f;
    internal const float MinimumFalloffDepth = 1f;
    internal const float MaximumFalloffDepth = 4096f;

    private static readonly HashSet<string> NamedLegacyEffectMeshes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Retail FNV acceptance assets. Some are static effect shells rather than NiParticleSystem
        // clouds and therefore have no authored particle marker to key from.
        "testsmokepillermesh01.nif",
        "nvlimestoneduststorm.nif",
        "nvlimestoneduststormhalfviz.nif",
        "nvlimestoneduststormnight.nif",
        "fxmistlow01.nif",
        "fxmistlow01halfvis.nif",
        "fxmistlow01long.nif",
        "fxmistlow01longhalfvis.nif",
    };

    internal static NifSoftParticleSettings Resolve(in NifSoftParticleCandidate candidate)
    {
        // Depth-writing blend is cutout-like foliage and cannot sample the bound DSV. Decals must
        // retain a hard surface intersection. These gates also make the policy safe for a broad
        // shared NIF renderer rather than silently softening every alpha blend.
        if (!candidate.AlphaBlend || candidate.DepthWritingBlend || candidate.IsDecal)
        {
            return NifSoftParticleSettings.Disabled;
        }

        var fadeTarget = ResolveFadeTarget(candidate.SrcBlendMode, candidate.DstBlendMode);
        if (float.IsFinite(candidate.AuthoredFalloffDepth) && candidate.AuthoredFalloffDepth > 0f)
        {
            return new NifSoftParticleSettings(
                NifSoftParticleSource.AuthoredSoftEffect,
                fadeTarget,
                Math.Clamp(candidate.AuthoredFalloffDepth, MinimumFalloffDepth, MaximumFalloffDepth));
        }

        if (candidate.IsParticleCloud)
        {
            return new NifSoftParticleSettings(
                NifSoftParticleSource.ParticleSystemFallback,
                fadeTarget,
                DefaultFalloffDepth);
        }

        var normalizedPath = NormalizePath(candidate.ModelPath);
        if (!IsEffectsPath(normalizedPath))
        {
            return NifSoftParticleSettings.Disabled;
        }

        var fileName = Path.GetFileName(normalizedPath);
        var isNamedLegacyEffect = NamedLegacyEffectMeshes.Contains(fileName);
        if (!isNamedLegacyEffect && !candidate.IsBillboard && !candidate.HasEffectFalloff)
        {
            return NifSoftParticleSettings.Disabled;
        }

        return new NifSoftParticleSettings(
            NifSoftParticleSource.EffectsGeometryFallback,
            fadeTarget,
            DefaultFalloffDepth);
    }

    internal static NifSoftParticleFadeTarget ResolveFadeTarget(byte srcBlendMode, byte dstBlendMode)
    {
        // ZERO/SRC_COLOR and DST_COLOR/ZERO are the two equivalent dst*src equations.
        if ((srcBlendMode == 1 && dstBlendMode == 2) ||
            (srcBlendMode == 4 && dstBlendMode == 1))
        {
            return NifSoftParticleFadeTarget.ColorTowardWhite;
        }

        // SRC_ALPHA and SRC_ALPHA_SATURATE derive their RGB contribution from output alpha.
        // ONE-source/additive and rarer color-factor effects need their RGB contribution faded.
        return srcBlendMode is 6 or 10
            ? NifSoftParticleFadeTarget.Alpha
            : NifSoftParticleFadeTarget.ColorTowardZero;
    }

    private static string NormalizePath(string? path) =>
        (path ?? string.Empty).Replace('/', '\\').TrimStart('\\');

    private static bool IsEffectsPath(string normalizedPath) =>
        normalizedPath.StartsWith("effects\\", StringComparison.OrdinalIgnoreCase) ||
        normalizedPath.Contains("\\effects\\", StringComparison.OrdinalIgnoreCase);
}

/// <summary>CPU reference for the HLSL reversed-Z occlusion and soft-intersection equations.</summary>
internal static class NifSoftParticleDepthMath
{
    internal const float OcclusionTieBias = 0.01f;

    internal static float LinearizeReversedDepth(float ndcDepth, float nearPlane, float farPlane)
    {
        if (!float.IsFinite(ndcDepth) || !float.IsFinite(nearPlane) || !float.IsFinite(farPlane) ||
            nearPlane <= 0f || farPlane <= nearPlane)
        {
            throw new ArgumentOutOfRangeException(nameof(nearPlane), "Depth inputs must describe a finite perspective range.");
        }

        return nearPlane * farPlane /
               MathF.Max(nearPlane + ndcDepth * (farPlane - nearPlane), 1e-4f);
    }

    internal static NifSoftParticleDepthResult Evaluate(
        float sceneNdcDepth,
        float particleNdcDepth,
        float nearPlane,
        float farPlane,
        float falloffDepth)
    {
        var sceneDistance = LinearizeReversedDepth(sceneNdcDepth, nearPlane, farPlane);
        var particleDistance = LinearizeReversedDepth(particleNdcDepth, nearPlane, farPlane);
        var gap = sceneDistance - particleDistance;
        var visible = gap + OcclusionTieBias >= 0f;
        var feather = falloffDepth > 0f
            ? Math.Clamp(MathF.Max(gap, 0f) / falloffDepth, 0f, 1f)
            : 1f;
        return new NifSoftParticleDepthResult(visible, feather, gap);
    }

    internal static Vector4 ApplyFade(
        Vector4 source,
        float feather,
        NifSoftParticleFadeTarget target)
    {
        feather = Math.Clamp(feather, 0f, 1f);
        return target switch
        {
            NifSoftParticleFadeTarget.Alpha => source with { W = source.W * feather },
            NifSoftParticleFadeTarget.ColorTowardZero => new Vector4(
                source.X * feather, source.Y * feather, source.Z * feather, source.W * feather),
            NifSoftParticleFadeTarget.ColorTowardWhite => new Vector4(
                1f + (source.X - 1f) * feather,
                1f + (source.Y - 1f) * feather,
                1f + (source.Z - 1f) * feather,
                source.W * feather),
            _ => source,
        };
    }
}

/// <summary>
///     Packs the bindless depth slot and its view dimension into one exactly-representable float:
///     zero disables sampling, +(slot+1) selects Texture2D, and -(slot+1) selects Texture2DMS.
/// </summary>
internal static class NifSoftParticleDepthBinding
{
    internal const float Disabled = 0f;
    private const uint LargestExactlyRepresentableSlot = 16_777_214u;

    internal static float Encode(uint bindlessIndex, int sampleCount)
    {
        if (bindlessIndex > LargestExactlyRepresentableSlot)
        {
            throw new ArgumentOutOfRangeException(nameof(bindlessIndex));
        }

        if (sampleCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        }

        var encoded = bindlessIndex + 1f;
        return sampleCount > 1 ? -encoded : encoded;
    }

    internal static bool TryDecode(float encoded, out uint bindlessIndex, out bool multisampled)
    {
        bindlessIndex = 0;
        multisampled = false;
        if (!float.IsFinite(encoded) || encoded == Disabled)
        {
            return false;
        }

        var magnitude = MathF.Abs(encoded);
        var integral = MathF.Round(magnitude);
        if (integral < 1f || integral > LargestExactlyRepresentableSlot + 1f ||
            MathF.Abs(magnitude - integral) > 0f)
        {
            return false;
        }

        bindlessIndex = (uint)integral - 1u;
        multisampled = encoded < 0f;
        return true;
    }
}

internal readonly record struct NifSoftParticleDepthResult(
    bool Visible,
    float Feather,
    float SceneGap);
