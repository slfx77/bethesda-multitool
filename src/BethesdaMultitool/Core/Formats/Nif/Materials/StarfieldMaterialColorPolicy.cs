using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Materials;

/// <summary>
///     How a CE2 material combines its authored material colour with its sampled albedo/tint input.
///     Values match <c>BSMaterial::MaterialOverrideColorTypeComponent</c>.
/// </summary>
internal enum StarfieldMaterialColorOverrideMode : byte
{
    Multiply = 0,
    Lerp = 1
}

/// <summary>
///     The vertex-colour channel a CE2 blender uses as its layer mask. This is deliberately separate
///     from <see cref="StarfieldMaterialColorPolicy.UsesVertexColorAsTint" />: selecting a blender
///     channel does not enable base-surface vertex tinting.
/// </summary>
internal enum StarfieldMaterialColorChannel : byte
{
    Red = 0,
    Green = 1,
    Blue = 2,
    Alpha = 3
}

/// <summary>
///     Persistent renderer operation selected from the broader CE2 material-colour policy. The
///     vertex-Lerp case consumes the external mesh colour's alpha as a blend weight; it is distinct
///     from both coverage alpha and the constant-Lerp payload below.
/// </summary>
internal enum StarfieldMaterialColorRenderMode : byte
{
    None = 0,
    ConstantLerp = 1,
    VertexLerp = 2
}

/// <summary>
///     First-class Starfield colour state carried from extraction through the decoded-mesh cache.
///     For constant Lerp, <see cref="LinearTint" /> stores CE2-expanded RGB and the original linear
///     weight in W. Vertex Lerp instead reads exact RGBA from the vertex stream and keeps this tuple
///     zero. Neither weight is coverage/opacity.
/// </summary>
internal readonly record struct StarfieldMaterialColorRenderState(
    StarfieldMaterialColorRenderMode Mode,
    Vector4 LinearTint)
{
    internal bool IsConstantLerp => Mode == StarfieldMaterialColorRenderMode.ConstantLerp;

    internal bool IsVertexLerp => Mode == StarfieldMaterialColorRenderMode.VertexLerp;

    internal bool IsLerp => IsConstantLerp || IsVertexLerp;
}

/// <summary>
///     Effective colour policy of a Starfield material's lowest (base-surface) layer.
/// </summary>
/// <param name="IsResolved">
///     True when the material path resolved through <c>material → layer[0] → material</c>.
/// </param>
/// <param name="UsesVertexColorAsTint">
///     Value of <c>BSMaterial::ParamBool</c> slot 0 on the layer material. On a root material object
///     that same component means two-sided rendering, so it must not be read from the root.
/// </param>
/// <param name="OverrideMode">
///     Effective <c>MaterialOverrideColorTypeComponent</c>, including inheritance. CE2 defaults to
///     <see cref="StarfieldMaterialColorOverrideMode.Lerp" /> when the component is absent.
/// </param>
/// <param name="Color">
///     Effective authored <c>BSMaterial::Color</c> at its original XMFLOAT4 precision. CE2 expands
///     its RGB channels from sRGB before the material shader consumes them; alpha stays linear.
/// </param>
internal readonly record struct StarfieldMaterialColorPolicy(
    bool IsResolved,
    bool UsesVertexColorAsTint,
    StarfieldMaterialColorOverrideMode OverrideMode,
    Vector4 Color)
{
    internal StarfieldMaterialColorPolicy(
        bool isResolved,
        bool usesVertexColorAsTint,
        StarfieldMaterialColorOverrideMode overrideMode,
        uint colorRgba)
        : this(
            isResolved,
            usesVertexColorAsTint,
            overrideMode,
            new Vector4(
                (byte)colorRgba / 255f,
                (byte)(colorRgba >> 8) / 255f,
                (byte)(colorRgba >> 16) / 255f,
                (byte)(colorRgba >> 24) / 255f))
    {
    }

    /// <summary>Diagnostic RGBA8 projection retained for parser/census assertions.</summary>
    internal uint ColorRgba => PackColor(Color.X, Color.Y, Color.Z, Color.W);

    /// <summary>
    ///     Resolves the bounded constant-Lerp operation shared by the world renderer and export
    ///     consumers. RGB follows CE2's <c>uni4srgb</c> expansion; alpha remains the authored linear
    ///     Lerp weight. An exact zero weight is elided as the inherited no-op. Vertex-driven Lerp is
    ///     resolved by the stream-aware overload below, and none of these values represents output
    ///     opacity.
    /// </summary>
    internal bool TryResolveConstantLerp(out Vector4 linearTint)
    {
        linearTint = default;
        if (!IsResolved ||
            UsesVertexColorAsTint ||
            OverrideMode != StarfieldMaterialColorOverrideMode.Lerp ||
            !IsRepresentable(Color.X) ||
            !IsRepresentable(Color.Y) ||
            !IsRepresentable(Color.Z) ||
            !IsRepresentable(Color.W) ||
            Color.W <= 0f)
        {
            return false;
        }

        linearTint = new Vector4(
            SrgbExpand(Color.X),
            SrgbExpand(Color.Y),
            SrgbExpand(Color.Z),
            Color.W);
        return true;
    }

    /// <summary>Projects the decoded policy onto the bounded persistent renderer state.</summary>
    internal StarfieldMaterialColorRenderState ResolveRenderState()
    {
        return TryResolveConstantLerp(out var linearTint)
            ? new StarfieldMaterialColorRenderState(
                StarfieldMaterialColorRenderMode.ConstantLerp,
                linearTint)
            : default;
    }

    /// <summary>
    ///     Selects the colour stream that the current renderer can represent exactly through either
    ///     its multiplicative lane or its dedicated Starfield vertex-Lerp branch.
    ///     <para>
    ///         <c>Multiply + UsesVertexColorAsTint</c> carries the decoded external-mesh RGB bytes.
    ///         <c>Multiply</c> with vertex tint disabled carries the authored constant after CE2's
    ///         sRGB expansion by repeating it per vertex. The neutral white constant needs no stream.
    ///     </para>
    ///     <para>
    ///         <c>Lerp + UsesVertexColorAsTint</c> carries the decoded RGBA bytes unchanged: RGB is
    ///         the target colour and alpha is the per-fragment Lerp weight in NifSkope's recovered
    ///         CE2 shader. In Multiply mode tint alpha is unused, so every supported stream emits
    ///         alpha 255. CE2 opacity is a separate AlphaSettings policy decoded elsewhere.
    ///     </para>
    /// </summary>
    internal byte[]? ResolveSupportedVertexColors(byte[]? decodedVertexColors, int vertexCount)
    {
        if (!IsResolved || vertexCount <= 0)
        {
            return null;
        }

        var requiredLength = (long)vertexCount * 4;
        if (requiredLength > int.MaxValue)
        {
            return null;
        }

        if (UsesVertexColorAsTint)
        {
            if (decodedVertexColors is null || decodedVertexColors.LongLength != requiredLength)
            {
                return null;
            }

            var decodedColors = (byte[])decodedVertexColors.Clone();
            if (OverrideMode == StarfieldMaterialColorOverrideMode.Lerp)
            {
                // Recovered CE2 shader: tintColor = C; layerBaseMap = mix(layerBaseMap,
                // tintColor.rgb, tintColor.a). The external stream is already normalized BGRA8
                // reordered to RGBA8, and neither RGB nor the weight receives a transfer conversion.
                return decodedColors;
            }

            if (OverrideMode == StarfieldMaterialColorOverrideMode.Multiply)
            {
                // Multiply ignores C.a; retaining it would let the viewer's generic vertex-colour
                // path reinterpret a material input as opacity. Clone keeps the raw mesh immutable.
                for (var offset = 3; offset < decodedColors.Length; offset += 4)
                {
                    decodedColors[offset] = byte.MaxValue;
                }

                return decodedColors;
            }

            return null;
        }

        if (OverrideMode != StarfieldMaterialColorOverrideMode.Multiply)
        {
            return null;
        }

        // NifSkope's CE2 reference renderer uploads material.color through uni4srgb(), whose RGB
        // path is DDSTexture16::srgbExpand. Preserve that exact polynomial before quantizing to the
        // existing R8G8B8A8 vertex lane. Values outside [0,1] cannot be represented there, so fail
        // closed rather than clamp an authored HDR/negative tint to different semantics.
        if (!IsRepresentable(Color.X) || !IsRepresentable(Color.Y) || !IsRepresentable(Color.Z))
        {
            return null;
        }

        var red = ToUnorm8(SrgbExpand(Color.X));
        var green = ToUnorm8(SrgbExpand(Color.Y));
        var blue = ToUnorm8(SrgbExpand(Color.Z));
        if (red == byte.MaxValue && green == byte.MaxValue && blue == byte.MaxValue)
        {
            return null;
        }

        var constantColors = new byte[(int)requiredLength];
        for (var offset = 0; offset < constantColors.Length; offset += 4)
        {
            constantColors[offset] = red;
            constantColors[offset + 1] = green;
            constantColors[offset + 2] = blue;
            constantColors[offset + 3] = byte.MaxValue;
        }

        return constantColors;
    }

    /// <summary>
    ///     Projects policy plus the already-selected colour stream onto persistent renderer state.
    ///     Constant Lerp does not need a stream. Vertex Lerp is admitted only when extraction proved
    ///     that one complete RGBA value exists for every vertex; a missing/truncated stream remains
    ///     fail-closed instead of turning the white fallback vertex into a full tint.
    /// </summary>
    internal StarfieldMaterialColorRenderState ResolveRenderState(
        byte[]? supportedVertexColors,
        int vertexCount)
    {
        var constant = ResolveRenderState();
        if (constant.IsConstantLerp)
        {
            return constant;
        }

        var requiredLength = (long)vertexCount * 4;
        return IsResolved &&
               UsesVertexColorAsTint &&
               OverrideMode == StarfieldMaterialColorOverrideMode.Lerp &&
               vertexCount > 0 &&
               requiredLength <= int.MaxValue &&
               supportedVertexColors is not null &&
               supportedVertexColors.LongLength == requiredLength
            ? new StarfieldMaterialColorRenderState(
                StarfieldMaterialColorRenderMode.VertexLerp,
                Vector4.Zero)
            : default;
    }

    private static bool IsRepresentable(float value)
    {
        return float.IsFinite(value) && value is >= 0f and <= 1f;
    }

    private static float SrgbExpand(float value)
    {
        const float a4 = -0.13984761f;
        const float a3 = 0.58740202f;
        const float a2 = 0.50849240f;
        const float a1 = 0.04395319f;
        var squared = value * value;
        return (squared * a4 + (value * a3 + a2)) * squared + value * a1;
    }

    private static byte ToUnorm8(float value)
    {
        return (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
    }

    private static uint PackColor(float red, float green, float blue, float alpha)
    {
        return (uint)ToUnorm8(red) |
               ((uint)ToUnorm8(green) << 8) |
               ((uint)ToUnorm8(blue) << 16) |
               ((uint)ToUnorm8(alpha) << 24);
    }
}
