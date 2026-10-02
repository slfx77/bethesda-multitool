using System.Numerics;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     The probe's enum spellings (nif.xml option names, as <c>nif_feature_probe.py</c> writes them) mapped to the Shared
///     vocabulary the reader is expected to produce. Transcribed here from nif.xml and the plan's mapping table
///     (section 3), independently of <c>NifModelRenderStateMapping</c>, so hop A1 compares the reader with a second
///     reading of the same tables rather than with itself.
/// </summary>
/// <remarks>
///     Two entries are reader policies rather than nif.xml facts and are pinned as such: <c>DRAW_CCW_OR_BOTH</c> (the
///     application default) is read as counterclockwise, and <c>ANISOTROPIC</c> filtering, which has no Shared carrier,
///     as trilinear.
/// </remarks>
internal static class NifModelProbeVocabulary
{
    /// <summary>The BSShaderTextureSet slot roles the reader binds (slots 0-5).</summary>
    public static IReadOnlyList<SceneTextureLayerRole> TextureSetRoles { get; } =
    [
        SceneTextureLayerRole.BaseColor, SceneTextureLayerRole.Normal, SceneTextureLayerRole.Glow,
        SceneTextureLayerRole.Parallax, SceneTextureLayerRole.Environment, SceneTextureLayerRole.EnvironmentMask
    ];

    /// <summary>An AlphaFunction option name as the affine blend term the design assigns it.</summary>
    public static SceneBlendTerm BlendTerm(string alphaFunction)
    {
        return alphaFunction switch
        {
            "ONE" => SceneBlendTerm.One,
            "ZERO" => SceneBlendTerm.Zero,
            "SRC_COLOR" => Scaled(SceneBlendInput.SourceColor),
            "INV_SRC_COLOR" => Inverted(SceneBlendInput.SourceColor),
            "DEST_COLOR" => Scaled(SceneBlendInput.DestinationColor),
            "INV_DEST_COLOR" => Inverted(SceneBlendInput.DestinationColor),
            "SRC_ALPHA" => Scaled(SceneBlendInput.SourceAlpha),
            "INV_SRC_ALPHA" => Inverted(SceneBlendInput.SourceAlpha),
            "DEST_ALPHA" => Scaled(SceneBlendInput.DestinationAlpha),
            "INV_DEST_ALPHA" => Inverted(SceneBlendInput.DestinationAlpha),
            "SRC_ALPHA_SATURATE" => Scaled(SceneBlendInput.SourceAlphaSaturate),
            _ => throw new ArgumentOutOfRangeException(nameof(alphaFunction), alphaFunction, "Not an AlphaFunction.")
        };
    }

    /// <summary>A TestFunction option name (NiAlphaProperty, NiZBufferProperty).</summary>
    public static SceneCompareFunction TestFunction(string name)
    {
        return name switch
        {
            "ALWAYS" => SceneCompareFunction.Always,
            "LESS" => SceneCompareFunction.Less,
            "EQUAL" => SceneCompareFunction.Equal,
            "LESS_EQUAL" => SceneCompareFunction.LessEqual,
            "GREATER" => SceneCompareFunction.Greater,
            "NOT_EQUAL" => SceneCompareFunction.NotEqual,
            "GREATER_EQUAL" => SceneCompareFunction.GreaterEqual,
            "NEVER" => SceneCompareFunction.Never,
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a TestFunction.")
        };
    }

    /// <summary>A StencilTestFunc option name (the same compare vocabulary in a different stored order).</summary>
    public static SceneCompareFunction StencilTest(string name)
    {
        return TestFunction(name);
    }

    /// <summary>A StencilAction option name; INCREMENT and DECREMENT are read as saturating (reader assumption).</summary>
    public static SceneStencilOperation StencilAction(string name)
    {
        return name switch
        {
            "KEEP" => SceneStencilOperation.Keep,
            "ZERO" => SceneStencilOperation.Zero,
            "REPLACE" => SceneStencilOperation.Replace,
            "INCREMENT" => SceneStencilOperation.IncrementSaturate,
            "DECREMENT" => SceneStencilOperation.DecrementSaturate,
            "INVERT" => SceneStencilOperation.Invert,
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a StencilAction.")
        };
    }

    /// <summary>A StencilDrawMode option name; CCW_OR_BOTH (the application default) is read as counterclockwise.</summary>
    public static SceneStencilDrawMode StencilDrawMode(string name)
    {
        return name switch
        {
            "CCW_OR_BOTH" or "CCW" => SceneStencilDrawMode.CounterClockwise,
            "CW" => SceneStencilDrawMode.Clockwise,
            "BOTH" => SceneStencilDrawMode.Both,
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a StencilDrawMode.")
        };
    }

    /// <summary>A TexClampMode option name as (U, V) wrapping, S to U and T to V.</summary>
    public static (SceneTextureWrap U, SceneTextureWrap V) Clamp(string name)
    {
        return name switch
        {
            "CLAMP_S_CLAMP_T" => (SceneTextureWrap.ClampToEdge, SceneTextureWrap.ClampToEdge),
            "CLAMP_S_WRAP_T" => (SceneTextureWrap.ClampToEdge, SceneTextureWrap.Repeat),
            "WRAP_S_CLAMP_T" => (SceneTextureWrap.Repeat, SceneTextureWrap.ClampToEdge),
            "WRAP_S_WRAP_T" => (SceneTextureWrap.Repeat, SceneTextureWrap.Repeat),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a TexClampMode.")
        };
    }

    /// <summary>A stored TexClampMode value (0-3) by its nif.xml order.</summary>
    public static (SceneTextureWrap U, SceneTextureWrap V) Clamp(long mode)
    {
        return Clamp(mode switch
        {
            0 => "CLAMP_S_CLAMP_T",
            1 => "CLAMP_S_WRAP_T",
            2 => "WRAP_S_CLAMP_T",
            3 => "WRAP_S_WRAP_T",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a TexClampMode.")
        });
    }

    /// <summary>A TexFilterMode option name as (minification, magnification); ANISOTROPIC reads as trilinear.</summary>
    public static (SceneTextureFilter Min, SceneTextureFilter Mag) Filter(string name)
    {
        return name switch
        {
            "NEAREST" => (SceneTextureFilter.Nearest, SceneTextureFilter.Nearest),
            "BILERP" => (SceneTextureFilter.Linear, SceneTextureFilter.Linear),
            "TRILERP" => (SceneTextureFilter.LinearMipmapLinear, SceneTextureFilter.Linear),
            "NEAREST_MIPNEAREST" => (SceneTextureFilter.NearestMipmapNearest, SceneTextureFilter.Nearest),
            "NEAREST_MIPLERP" => (SceneTextureFilter.NearestMipmapLinear, SceneTextureFilter.Nearest),
            "BILERP_MIPNEAREST" => (SceneTextureFilter.LinearMipmapNearest, SceneTextureFilter.Linear),
            "ANISOTROPIC" => (SceneTextureFilter.LinearMipmapLinear, SceneTextureFilter.Linear),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a TexFilterMode.")
        };
    }

    /// <summary>An ApplyMode option name as its stored value.</summary>
    public static int ApplyMode(string name)
    {
        return name switch
        {
            "REPLACE" => 0,
            "DECAL" => 1,
            "MODULATE" => 2,
            "HILIGHT" => 3,
            "HILIGHT2" => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not an ApplyMode.")
        };
    }

    /// <summary>A SourceVertexMode option name.</summary>
    public static SceneVertexColorSource VertexColorSource(string name)
    {
        return name switch
        {
            "SRC_IGNORE" => SceneVertexColorSource.Ignore,
            "SRC_EMISSIVE" => SceneVertexColorSource.Emissive,
            "SRC_AMB_DIF" => SceneVertexColorSource.AmbientDiffuse,
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a SourceVertexMode.")
        };
    }

    /// <summary>A LightingMode option name.</summary>
    public static SceneVertexLightingMode LightingMode(string name)
    {
        return name switch
        {
            "E" => SceneVertexLightingMode.Emissive,
            "E_A_D" => SceneVertexLightingMode.EmissiveAmbientDiffuse,
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a LightingMode.")
        };
    }

    /// <summary>The probe's NiTexturingProperty map key as the reader's slot name.</summary>
    public static string TexturingSlot(string probeSlot)
    {
        return probeSlot switch
        {
            "base" => "Base",
            "dark" => "Dark",
            "detail" => "Detail",
            "gloss" => "Gloss",
            "glow" => "Glow",
            "bump" => "Bump Map",
            "normal" => "Normal",
            "parallax" => "Parallax",
            "decal0" => "Decal 0",
            "decal1" => "Decal 1",
            "decal2" => "Decal 2",
            "decal3" => "Decal 3",
            _ => throw new ArgumentOutOfRangeException(nameof(probeSlot), probeSlot, "Not a probe map slot.")
        };
    }

    private static SceneBlendTerm Scaled(SceneBlendInput input)
    {
        return new SceneBlendTerm(Vector4.Zero, Vector4.One, input);
    }

    private static SceneBlendTerm Inverted(SceneBlendInput input)
    {
        return new SceneBlendTerm(Vector4.One, -Vector4.One, input);
    }
}
