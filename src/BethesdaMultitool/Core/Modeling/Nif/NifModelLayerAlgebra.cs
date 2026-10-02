using System.Numerics;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The layer equations the NIF reader declares (plan section 3; Shared's layer algebra: the swizzled sample times the
///     layer constant is the equations' source input, the accumulated value their destination input). Modulate is the
///     product the plan names for MODULATE and for the Bethesda shader slots; Replace, Decal and the doubled Detail
///     modulation follow Gamebryo's fixed-function apply modes (recalled, not measured: Assumed, and recorded per layer).
///     No equation clamps: a clamp the source does not declare is not added.
/// </summary>
internal static class NifModelLayerAlgebra
{
    /// <summary>The product of the sample and the accumulated value on every channel.</summary>
    public static SceneBlendEquation Product { get; } = new(
        new SceneBlendTerm(Vector4.Zero, Vector4.One, SceneBlendInput.DestinationColor), SceneBlendTerm.Zero,
        SceneBlendOperation.Add, false);

    /// <summary>The sample replaces the accumulated value.</summary>
    public static SceneBlendEquation Replacement { get; } = new(SceneBlendTerm.One, SceneBlendTerm.Zero,
        SceneBlendOperation.Add, false);

    /// <summary>The accumulated value passes through unchanged.</summary>
    public static SceneBlendEquation Keep { get; } = new(SceneBlendTerm.Zero, SceneBlendTerm.One,
        SceneBlendOperation.Add, false);

    /// <summary>Decal color: sample RGB times its alpha plus the accumulated RGB times one minus that alpha.</summary>
    public static SceneBlendEquation DecalColor { get; } = new(
        new SceneBlendTerm(Vector4.Zero, Vector4.One, SceneBlendInput.SourceAlpha),
        new SceneBlendTerm(Vector4.One, -Vector4.One, SceneBlendInput.SourceAlpha), SceneBlendOperation.Add, false);

    /// <summary>A layer multiplying its sample into the accumulated value (identity swizzle unless one is given).</summary>
    public static SceneTextureLayer Modulate(SceneTextureLayerRole role, SceneTextureBinding binding,
        Vector4? constant = null, SceneTextureSwizzle? swizzle = null)
    {
        return new SceneTextureLayer(role, binding, swizzle ?? SceneTextureSwizzle.Identity, Product, Product,
            Vector4.One, constant ?? Vector4.One);
    }

    /// <summary>A layer replacing the accumulated value (APPLY_REPLACE).</summary>
    public static SceneTextureLayer Replace(SceneTextureLayerRole role, SceneTextureBinding binding)
    {
        return new SceneTextureLayer(role, binding, SceneTextureSwizzle.Identity, Replacement, Replacement, Vector4.One,
            Vector4.One);
    }

    /// <summary>A decal layer (APPLY_DECAL and the Decal maps): color blended by the sample's alpha, alpha kept.</summary>
    public static SceneTextureLayer Decal(SceneTextureLayerRole role, SceneTextureBinding binding)
    {
        return new SceneTextureLayer(role, binding, SceneTextureSwizzle.Identity, DecalColor, Keep, Vector4.One,
            Vector4.One);
    }

    /// <summary>A detail layer: the product doubled on RGB (fixed-function MODULATE2X, Assumed), alpha a plain product.</summary>
    public static SceneTextureLayer DoubledModulate(SceneTextureLayerRole role, SceneTextureBinding binding)
    {
        return new SceneTextureLayer(role, binding, SceneTextureSwizzle.Identity, Product, Product,
            new Vector4(2f, 2f, 2f, 1f), Vector4.One);
    }
}
