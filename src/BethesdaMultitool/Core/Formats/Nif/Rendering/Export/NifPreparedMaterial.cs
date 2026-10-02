using System.Numerics;
using System.Text.Json.Nodes;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>Collects the resolved export surface once, independently of either output implementation.</summary>
/// <remarks>Only NifMaterialPreparation populates this result. Images and factors already include
/// Bethesda packing and fallback policy; consumers translate channels without resolving game inputs.</remarks>
/// <param name="key">The source/profile identity, including tint baked into resolved diffuse pixels.</param>
/// <param name="name">The selected material label, including any approximation diagnostics.</param>
internal sealed class NifPreparedMaterial(NifMaterialCacheKey key, string name)
{
    /// <summary>The source/profile cache identity including baked diffuse tint.</summary>
    internal NifMaterialCacheKey Key { get; } = key;
    /// <summary>The legacy label, including explicit approximation diagnostics.</summary>
    internal string Name { get; set; } = name;
    /// <summary>Reserved viewer metadata produced by preparation.</summary>
    internal JsonObject? Extras { get; set; }
    /// <summary>Whether the prepared surface bypasses lighting.</summary>
    internal bool Unlit { get; private set; }
    /// <summary>Whether both triangle faces are exported.</summary>
    internal bool DoubleSided { get; private set; }
    /// <summary>The already selected portable alpha route.</summary>
    internal SceneAlphaMode AlphaMode { get; private set; }
    /// <summary>The exact floating-point threshold of mask coverage.</summary>
    internal float AlphaCutoff { get; private set; } = 0.5f;
    /// <summary>The shared addressing choice for every prepared channel.</summary>
    internal bool ClampU { get; set; }
    /// <summary>The shared addressing choice for every prepared channel.</summary>
    internal bool ClampV { get; set; }
    /// <summary>The linear base-color multiplier.</summary>
    internal Vector4 BaseColor { get; private set; } = Vector4.One;
    /// <summary>The prepared base-color image.</summary>
    internal NifPreparedImage? BaseColorImage { get; private set; }
    /// <summary>The prepared tangent-space normal image.</summary>
    internal NifPreparedImage? NormalImage { get; private set; }
    /// <summary>The normal-channel multiplier.</summary>
    internal float NormalScale { get; private set; } = 1f;
    /// <summary>The prepared G/B roughness/metalness image.</summary>
    internal NifPreparedImage? MetallicRoughnessImage { get; private set; }
    /// <summary>The already selected metalness multiplier.</summary>
    internal float MetallicFactor { get; private set; }
    /// <summary>The already selected roughness multiplier.</summary>
    internal float RoughnessFactor { get; private set; } = 1f;
    /// <summary>The prepared specular-strength image, read from alpha.</summary>
    internal NifPreparedImage? SpecularImage { get; private set; }
    /// <summary>The emitted specular multiplier; absent channels retain the glTF default.</summary>
    internal float SpecularFactor { get; private set; } = 1f;
    /// <summary>The prepared red-channel occlusion image.</summary>
    internal NifPreparedImage? OcclusionImage { get; private set; }
    /// <summary>The emitted occlusion strength.</summary>
    internal float OcclusionStrength { get; private set; } = 1f;
    /// <summary>Whether an emissive term was actually selected, including constant emission.</summary>
    internal bool HasEmission { get; private set; }
    /// <summary>The optional emissive image.</summary>
    internal NifPreparedImage? EmissiveImage { get; private set; }
    /// <summary>The bounded linear emissive multiplier.</summary>
    internal Vector3 EmissiveFactor { get; private set; }
    /// <summary>The separate high-dynamic-range emission strength.</summary>
    internal float EmissiveStrength { get; private set; } = 1f;
    /// <summary>The explicitly emitted optical index, when a specialized surface requires it.</summary>
    internal float? IndexOfRefraction { get; set; }
    /// <summary>The explicit transmission multiplier, which shared SceneMaterial cannot yet represent.</summary>
    internal float? Transmission { get; private set; }
    /// <summary>The explicit clearcoat multiplier, which shared SceneMaterial cannot yet represent.</summary>
    internal float? ClearCoat { get; private set; }
    /// <summary>The explicit clearcoat roughness, which shared SceneMaterial cannot yet represent.</summary>
    internal float? ClearCoatRoughness { get; private set; }

    /// <summary>Selects the prepared unlit route.</summary>
    internal void WithUnlitShader() => Unlit = true;
    /// <summary>Selects the prepared lit route.</summary>
    internal void WithMetallicRoughnessShader() => Unlit = false;
    /// <summary>Retains the selected metalness and roughness multipliers.</summary>
    internal void WithMetallicRoughness(float metallic, float roughness) =>
        (MetallicFactor, RoughnessFactor) = (metallic, roughness);
    /// <summary>Retains a packed image and its selected metalness/roughness multipliers.</summary>
    internal void WithMetallicRoughness(NifPreparedImage image, float metallic, float roughness) =>
        (MetallicRoughnessImage, MetallicFactor, RoughnessFactor) = (image, metallic, roughness);
    /// <summary>Retains the selected two-sided presentation.</summary>
    internal void WithDoubleSide(bool enabled) => DoubleSided = enabled;
    /// <summary>Retains a constant base-color multiplier.</summary>
    internal void WithBaseColor(Vector4 color) => BaseColor = color;
    /// <summary>Retains the prepared base-color image and multiplier.</summary>
    internal void WithBaseColor(NifPreparedImage image, Vector4 color) => (BaseColorImage, BaseColor) = (image, color);
    /// <summary>Retains the prepared tangent-space image and strength.</summary>
    internal void WithNormal(NifPreparedImage image, float scale) => (NormalImage, NormalScale) = (image, scale);
    /// <summary>Retains the prepared specular-strength image and multiplier.</summary>
    internal void WithSpecularFactor(NifPreparedImage image, float factor) => (SpecularImage, SpecularFactor) = (image, factor);
    /// <summary>Retains the prepared occlusion image and its selected strength.</summary>
    internal void WithOcclusion(NifPreparedImage image, float strength = 1f) => (OcclusionImage, OcclusionStrength) = (image, strength);
    /// <summary>Retains an image-backed emission term without recomputing the policy that selected it.</summary>
    internal void WithEmissive(NifPreparedImage image, Vector3 factor, float strength)
    {
        EmissiveImage = image;
        WithEmissive(factor, strength);
    }
    /// <summary>Retains the selected constant emission term.</summary>
    internal void WithEmissive(Vector3 factor, float strength) =>
        (HasEmission, EmissiveFactor, EmissiveStrength) = (true, factor, strength);
    /// <summary>Retains the already projected portable alpha mode and threshold.</summary>
    internal void WithAlpha(SceneAlphaMode mode, float cutoff = 0.5f) => (AlphaMode, AlphaCutoff) = (mode, cutoff);
    /// <summary>Retains the constant specialized transmission channel.</summary>
    internal void WithTransmission(float factor) => Transmission = factor;
    /// <summary>Retains the constant specialized clearcoat channel.</summary>
    internal void WithClearCoat(float factor) => ClearCoat = factor;
    /// <summary>Retains the constant specialized clearcoat roughness channel.</summary>
    internal void WithClearCoatRoughness(float factor) => ClearCoatRoughness = factor;
}
