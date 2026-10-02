using System.Numerics;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Pure mappings from NIF enum values to Shared render-state vocabulary (plan section 3, "Materials and properties").
///     Every mapping returns null for a stored value its nif.xml enum does not define; the caller reports it and keeps the
///     raw value in native state instead of guessing.
/// </summary>
internal static class NifModelRenderStateMapping
{
    /// <summary>
    ///     An AlphaFunction (nif.xml:4353-4368) as an affine blend factor, per the plan's table: ONE (1, 0, Zero); ZERO
    ///     (0, 0, Zero); SRC_COLOR (0, 1, SourceColor); INV_SRC_COLOR (1, -1, SourceColor); DEST_COLOR and
    ///     INV_DEST_COLOR on DestinationColor; SRC_ALPHA (0, 1, SourceAlpha); INV_SRC_ALPHA (1, -1, SourceAlpha);
    ///     DEST_ALPHA and INV_DEST_ALPHA on DestinationAlpha; SRC_ALPHA_SATURATE (0, 1, SourceAlphaSaturate).
    /// </summary>
    public static SceneBlendTerm? BlendFactor(int function)
    {
        return function switch
        {
            0 => SceneBlendTerm.One,
            1 => SceneBlendTerm.Zero,
            2 => Scaled(SceneBlendInput.SourceColor),
            3 => Inverted(SceneBlendInput.SourceColor),
            4 => Scaled(SceneBlendInput.DestinationColor),
            5 => Inverted(SceneBlendInput.DestinationColor),
            6 => Scaled(SceneBlendInput.SourceAlpha),
            7 => Inverted(SceneBlendInput.SourceAlpha),
            8 => Scaled(SceneBlendInput.DestinationAlpha),
            9 => Inverted(SceneBlendInput.DestinationAlpha),
            10 => Scaled(SceneBlendInput.SourceAlphaSaturate),
            _ => null
        };
    }

    /// <summary>The AlphaFunction option name, for native state and messages.</summary>
    public static string BlendFactorName(int function)
    {
        return function switch
        {
            0 => "ONE",
            1 => "ZERO",
            2 => "SRC_COLOR",
            3 => "INV_SRC_COLOR",
            4 => "DEST_COLOR",
            5 => "INV_DEST_COLOR",
            6 => "SRC_ALPHA",
            7 => "INV_SRC_ALPHA",
            8 => "DEST_ALPHA",
            9 => "INV_DEST_ALPHA",
            10 => "SRC_ALPHA_SATURATE",
            _ => "undefined"
        };
    }

    /// <summary>
    ///     A TestFunction (nif.xml:4322-4351), used by the alpha test and the z-buffer, where the incoming value is the
    ///     left operand: 0 ALWAYS, 1 LESS, 2 EQUAL, 3 LESS_EQUAL, 4 GREATER, 5 NOT_EQUAL, 6 GREATER_EQUAL, 7 NEVER.
    /// </summary>
    public static SceneCompareFunction? TestFunction(int value)
    {
        return value switch
        {
            0 => SceneCompareFunction.Always,
            1 => SceneCompareFunction.Less,
            2 => SceneCompareFunction.Equal,
            3 => SceneCompareFunction.LessEqual,
            4 => SceneCompareFunction.Greater,
            5 => SceneCompareFunction.NotEqual,
            6 => SceneCompareFunction.GreaterEqual,
            7 => SceneCompareFunction.Never,
            _ => null
        };
    }

    /// <summary>
    ///     A StencilTestFunc (nif.xml:4247-4272; "VRef op VBuf", so the reference is the left operand, as Shared's stencil
    ///     test defines): 0 NEVER, 1 LESS, 2 EQUAL, 3 LESS_EQUAL, 4 GREATER, 5 NOT_EQUAL, 6 GREATER_EQUAL, 7 ALWAYS.
    /// </summary>
    public static SceneCompareFunction? StencilFunction(int value)
    {
        return value switch
        {
            0 => SceneCompareFunction.Never,
            1 => SceneCompareFunction.Less,
            2 => SceneCompareFunction.Equal,
            3 => SceneCompareFunction.LessEqual,
            4 => SceneCompareFunction.Greater,
            5 => SceneCompareFunction.NotEqual,
            6 => SceneCompareFunction.GreaterEqual,
            7 => SceneCompareFunction.Always,
            _ => null
        };
    }

    /// <summary>
    ///     A StencilAction (nif.xml:4274-4298): KEEP, ZERO, REPLACE, INCREMENT, DECREMENT, INVERT. INCREMENT and DECREMENT
    ///     are read as saturating (Assumed: nif.xml does not say; the reader records the assumption).
    /// </summary>
    public static SceneStencilOperation? StencilAction(int value)
    {
        return value switch
        {
            0 => SceneStencilOperation.Keep,
            1 => SceneStencilOperation.Zero,
            2 => SceneStencilOperation.Replace,
            3 => SceneStencilOperation.IncrementSaturate,
            4 => SceneStencilOperation.DecrementSaturate,
            5 => SceneStencilOperation.Invert,
            _ => null
        };
    }

    /// <summary>
    ///     A StencilDrawMode (nif.xml:4300-4320): DRAW_CCW and DRAW_CCW_OR_BOTH (0, "application default") map to
    ///     CounterClockwise, the latter Assumed; DRAW_CW to Clockwise; DRAW_BOTH to Both.
    /// </summary>
    public static SceneStencilDrawMode StencilDrawMode(int value, out bool assumed)
    {
        assumed = value == 0;
        return value switch
        {
            2 => SceneStencilDrawMode.Clockwise,
            3 => SceneStencilDrawMode.Both,
            _ => SceneStencilDrawMode.CounterClockwise
        };
    }

    /// <summary>A TexClampMode (nif.xml:4066-4085) as per-axis wrapping, S to U and T to V.</summary>
    public static (SceneTextureWrap U, SceneTextureWrap V)? Clamp(long mode)
    {
        return mode switch
        {
            0 => (SceneTextureWrap.ClampToEdge, SceneTextureWrap.ClampToEdge),
            1 => (SceneTextureWrap.ClampToEdge, SceneTextureWrap.Repeat),
            2 => (SceneTextureWrap.Repeat, SceneTextureWrap.ClampToEdge),
            3 => (SceneTextureWrap.Repeat, SceneTextureWrap.Repeat),
            _ => null
        };
    }

    /// <summary>
    ///     A TexFilterMode (nif.xml:4108-4139) as minification and magnification filters. Anisotropic filtering has no
    ///     typed carrier: it is read as trilinear and the raw mode stays in native state.
    /// </summary>
    public static (SceneTextureFilter Min, SceneTextureFilter Mag)? Filter(int mode)
    {
        return mode switch
        {
            0 => (SceneTextureFilter.Nearest, SceneTextureFilter.Nearest),
            1 => (SceneTextureFilter.Linear, SceneTextureFilter.Linear),
            2 => (SceneTextureFilter.LinearMipmapLinear, SceneTextureFilter.Linear),
            3 => (SceneTextureFilter.NearestMipmapNearest, SceneTextureFilter.Nearest),
            4 => (SceneTextureFilter.NearestMipmapLinear, SceneTextureFilter.Nearest),
            5 => (SceneTextureFilter.LinearMipmapNearest, SceneTextureFilter.Linear),
            6 => (SceneTextureFilter.LinearMipmapLinear, SceneTextureFilter.Linear),
            _ => null
        };
    }

    /// <summary>A SourceVertexMode: 0 ignore, 1 emissive, 2 ambient and diffuse.</summary>
    public static SceneVertexColorSource? VertexColorSource(int mode)
    {
        return mode switch
        {
            0 => SceneVertexColorSource.Ignore,
            1 => SceneVertexColorSource.Emissive,
            2 => SceneVertexColorSource.AmbientDiffuse,
            _ => null
        };
    }

    /// <summary>A LightingMode: 0 emissive, 1 emissive + ambient + diffuse.</summary>
    public static SceneVertexLightingMode VertexLighting(int mode)
    {
        return mode == 0 ? SceneVertexLightingMode.Emissive : SceneVertexLightingMode.EmissiveAmbientDiffuse;
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
