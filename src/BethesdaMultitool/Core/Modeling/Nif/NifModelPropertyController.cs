using System.Globalization;
using System.Text;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 14 (SA4): the property controller types the reader maps and how each names the Shared
///     <see cref="ScenePropertyKind" /> it drives, from its own stored fields (an embedded controller) or from the
///     Controller ID string a sequence controlled block carries (the engine's NiInterpController::GetCtlrID, nif.xml:
///     NiMaterialColorController <c>AMB</c>, <c>DIFF</c>, <c>SPEC</c>, <c>SELF_ILLUM</c>; NiTextureTransformController
///     <c>%1-%2-TT_op</c> with %1 the Shader Map value and %2 the Texture Slot value; the other types return no ID).
///     Pure. Every table is nif.xml's (MaterialColor, TransformMember, TexType); a value outside a table fails closed.
/// </summary>
/// <remarks>
///     Retail (FNV and FO3 mesh archives, measured 2026-09-26 on the slice-0 census): NiMaterialColorController IDs are
///     <c>SELF_ILLUM</c> (1,932) and <c>SPEC</c> (76); NiTextureTransformController IDs are <c>0-0-TT_TRANSLATE_V</c>,
///     <c>0-0-TT_TRANSLATE_U</c>, <c>0-0-TT_SCALE_U</c>, <c>0-0-TT_SCALE_V</c> and <c>0-0-TT_ROTATE</c> (2,247, every one
///     shader map 0, slot BASE); NiAlphaController, BSMaterialEmittanceMultController and NiVisController IDs are all
///     NULL.
/// </remarks>
internal static class NifModelPropertyController
{
    /// <summary>NiAlphaController: the target NiMaterialProperty's alpha (<see cref="ScenePropertyKind.MaterialAlpha" />).</summary>
    public const string AlphaControllerType = "NiAlphaController";

    /// <summary>NiMaterialColorController: one of the target NiMaterialProperty's colors, by Target Color.</summary>
    public const string MaterialColorControllerType = "NiMaterialColorController";

    /// <summary>BSMaterialEmittanceMultController: the target NiMaterialProperty's Emit Mult (<see cref="ScenePropertyKind.MaterialEmissiveStrength" />).</summary>
    public const string EmittanceMultControllerType = "BSMaterialEmittanceMultController";

    /// <summary>NiTextureTransformController: one member of one map's NiTextureTransform on the target NiTexturingProperty.</summary>
    public const string TextureTransformControllerType = "NiTextureTransformController";

    /// <summary>NiVisController: the target node's visibility (<see cref="ScenePropertyKind.NodeVisibility" />).</summary>
    public const string VisControllerType = "NiVisController";

    /// <summary>NiUVController: the base map's UV offset and scale of the target geometry's texturing property (TES3 era).</summary>
    public const string UvControllerType = "NiUVController";

    /// <summary>The property block type the material controllers drive.</summary>
    public const string MaterialPropertyType = "NiMaterialProperty";

    /// <summary>The property block type the texture transform controller drives.</summary>
    public const string TexturingPropertyType = "NiTexturingProperty";

    /// <summary>nif.xml TexType BUMP_MAP, the one named map the cut-1a reader never types as a layer.</summary>
    public const uint BumpMapSlot = 5;

    private const string TranslateUName = "TT_TRANSLATE_U";
    private const string TranslateVName = "TT_TRANSLATE_V";
    private const string RotateName = "TT_ROTATE";
    private const string ScaleUName = "TT_SCALE_U";
    private const string ScaleVName = "TT_SCALE_V";

    /// <summary>The nif.xml TexType names the cut-1a texturing view gives its maps, by slot value (5 is the bump map).</summary>
    private static readonly string[] SlotNames =
    [
        "Base", "Dark", "Detail", "Gloss", "Glow", "Bump Map", "Normal", "Parallax", "Decal 0", "Decal 1", "Decal 2",
        "Decal 3"
    ];

    /// <summary>The nif.xml MaterialColor IDs the engine formats, by Target Color value.</summary>
    private static readonly string[] MaterialColorIds = ["AMB", "DIFF", "SPEC", "SELF_ILLUM"];

    /// <summary>The nif.xml TransformMember names, by Operation value.</summary>
    private static readonly string[] OperationNames = [TranslateUName, TranslateVName, RotateName, ScaleUName, ScaleVName];

    /// <summary>True for the five property controller types a sequence controlled block can name (NiInterpControllers).</summary>
    /// <param name="type">A Controller Type string or block type name.</param>
    /// <returns>True for the alpha, material color, emittance, texture transform and visibility controllers.</returns>
    public static bool IsSequenceControllerType(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type is AlphaControllerType or MaterialColorControllerType or EmittanceMultControllerType
            or TextureTransformControllerType or VisControllerType;
    }

    /// <summary>True for the six property controller types the reader maps (the five above plus NiUVController).</summary>
    /// <param name="type">A block type name.</param>
    /// <returns>True for a property controller type.</returns>
    public static bool IsControllerType(string type)
    {
        return IsSequenceControllerType(type) || string.Equals(type, UvControllerType, StringComparison.Ordinal);
    }

    /// <summary>The Shared width of a kind's values (3 for the colors, 1 otherwise), as SceneCurveTrackValidation requires.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The component count.</returns>
    public static int Width(ScenePropertyKind kind)
    {
        return kind is ScenePropertyKind.MaterialBaseColor or ScenePropertyKind.MaterialSpecularColor
            or ScenePropertyKind.MaterialAmbientColor or ScenePropertyKind.MaterialEmissiveColor
            ? 3
            : 1;
    }

    /// <summary>True for the kinds that address a material texture layer.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>True for the five layer kinds.</returns>
    public static bool IsLayerKind(ScenePropertyKind kind)
    {
        return kind is ScenePropertyKind.LayerOffsetU or ScenePropertyKind.LayerOffsetV or ScenePropertyKind.LayerScaleU
            or ScenePropertyKind.LayerScaleV or ScenePropertyKind.LayerRotation;
    }

    /// <summary>The kind an NiMaterialColorController Target Color drives.</summary>
    /// <param name="targetColor">The stored Target Color (nif.xml MaterialColor).</param>
    /// <param name="kind">The kind.</param>
    /// <returns>False for a value outside 0 to 3.</returns>
    public static bool TryMaterialColorKind(ushort targetColor, out ScenePropertyKind kind)
    {
        switch (targetColor)
        {
            case 0:
                kind = ScenePropertyKind.MaterialAmbientColor;
                return true;
            case 1:
                kind = ScenePropertyKind.MaterialBaseColor;
                return true;
            case 2:
                kind = ScenePropertyKind.MaterialSpecularColor;
                return true;
            case 3:
                kind = ScenePropertyKind.MaterialEmissiveColor;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    /// <summary>The Controller ID the engine formats for an NiMaterialColorController (nif.xml MaterialColor).</summary>
    /// <param name="targetColor">The stored Target Color, 0 to 3.</param>
    /// <returns>The ID text.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside the table.</exception>
    public static string MaterialColorId(ushort targetColor)
    {
        if (targetColor >= MaterialColorIds.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(targetColor), targetColor, "Not a nif.xml MaterialColor.");
        }

        return MaterialColorIds[targetColor];
    }

    /// <summary>Interprets a sequence controlled block's Controller ID for an NiMaterialColorController (exact bytes).</summary>
    /// <param name="id">The stored ID bytes.</param>
    /// <param name="kind">The kind the ID names.</param>
    /// <param name="targetColor">The Target Color value the ID names.</param>
    /// <returns>False when the bytes are not one of the four IDs.</returns>
    public static bool TryParseMaterialColorId(ReadOnlySpan<byte> id, out ScenePropertyKind kind, out ushort targetColor)
    {
        for (ushort color = 0; color < MaterialColorIds.Length; color++)
        {
            if (id.SequenceEqual(Encoding.ASCII.GetBytes(MaterialColorIds[color])))
            {
                targetColor = color;
                return TryMaterialColorKind(color, out kind);
            }
        }

        kind = default;
        targetColor = 0;
        return false;
    }

    /// <summary>The kind an NiTextureTransformController Operation drives.</summary>
    /// <param name="operation">The stored Operation (nif.xml TransformMember).</param>
    /// <param name="kind">The kind.</param>
    /// <returns>False for a value outside 0 to 4.</returns>
    public static bool TryOperationKind(uint operation, out ScenePropertyKind kind)
    {
        switch (operation)
        {
            case 0:
                kind = ScenePropertyKind.LayerOffsetU;
                return true;
            case 1:
                kind = ScenePropertyKind.LayerOffsetV;
                return true;
            case 2:
                kind = ScenePropertyKind.LayerRotation;
                return true;
            case 3:
                kind = ScenePropertyKind.LayerScaleU;
                return true;
            case 4:
                kind = ScenePropertyKind.LayerScaleV;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    /// <summary>The nif.xml TransformMember name of an operation.</summary>
    /// <param name="operation">The stored Operation, 0 to 4.</param>
    /// <returns>The name.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside the table.</exception>
    public static string OperationName(uint operation)
    {
        if (operation >= OperationNames.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Not a nif.xml TransformMember.");
        }

        return OperationNames[operation];
    }

    /// <summary>The Controller ID the engine formats for an NiTextureTransformController: <c>%1-%2-TT_op</c>.</summary>
    /// <param name="shaderMap">The stored Shader Map byte (formatted as its number).</param>
    /// <param name="textureSlot">The stored Texture Slot.</param>
    /// <param name="operation">The stored Operation, 0 to 4.</param>
    /// <returns>The ID text.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The operation is outside the table.</exception>
    public static string TextureTransformId(byte shaderMap, uint textureSlot, uint operation)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{shaderMap}-{textureSlot}-{OperationName(operation)}");
    }

    /// <summary>
    ///     Interprets a sequence controlled block's Controller ID for an NiTextureTransformController: decimal digits, a
    ///     hyphen, decimal digits, a hyphen, then exactly one TransformMember name. Anything else fails closed.
    /// </summary>
    /// <param name="id">The stored ID bytes.</param>
    /// <param name="shaderMap">The Shader Map value the ID names.</param>
    /// <param name="textureSlot">The Texture Slot value the ID names.</param>
    /// <param name="operation">The Operation value the ID names.</param>
    /// <returns>False when the bytes do not follow the engine's format.</returns>
    public static bool TryParseTextureTransformId(ReadOnlySpan<byte> id, out byte shaderMap, out uint textureSlot,
        out uint operation)
    {
        shaderMap = 0;
        textureSlot = 0;
        operation = 0;
        var position = 0;
        if (!TryReadNumber(id, ref position, out var map) || map > byte.MaxValue || !TryReadHyphen(id, ref position) ||
            !TryReadNumber(id, ref position, out var slot) || !TryReadHyphen(id, ref position))
        {
            return false;
        }

        var tail = id[position..];
        for (var index = 0; index < OperationNames.Length; index++)
        {
            if (tail.SequenceEqual(Encoding.ASCII.GetBytes(OperationNames[index])))
            {
                shaderMap = (byte)map;
                textureSlot = slot;
                operation = (uint)index;
                return true;
            }
        }

        return false;
    }

    /// <summary>The NiTexturingProperty map slot name a Texture Slot names (the name the cut-1a view gives the map).</summary>
    /// <param name="textureSlot">The stored Texture Slot (nif.xml TexType).</param>
    /// <param name="slot">The map slot name.</param>
    /// <returns>False for a value outside 0 to 11.</returns>
    public static bool TrySlotName(uint textureSlot, out string slot)
    {
        if (textureSlot < SlotNames.Length)
        {
            slot = SlotNames[textureSlot];
            return true;
        }

        slot = string.Empty;
        return false;
    }

    private static bool TryReadNumber(ReadOnlySpan<byte> id, ref int position, out uint value)
    {
        value = 0;
        var start = position;
        while (position < id.Length && id[position] >= (byte)'0' && id[position] <= (byte)'9')
        {
            var digit = (uint)(id[position] - (byte)'0');
            if (value > (uint.MaxValue - digit) / 10)
            {
                return false;
            }

            value = value * 10 + digit;
            position++;
        }

        return position > start;
    }

    private static bool TryReadHyphen(ReadOnlySpan<byte> id, ref int position)
    {
        if (position < id.Length && id[position] == (byte)'-')
        {
            position++;
            return true;
        }

        return false;
    }
}
