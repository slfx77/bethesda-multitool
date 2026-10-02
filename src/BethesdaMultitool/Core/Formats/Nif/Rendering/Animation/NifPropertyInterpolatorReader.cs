using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Reads the two key-based interpolators the property controllers use beside NiFloatInterpolator
///     (<see cref="NifGeomMorpherReader.TryReadFloatInterpolatorView" />) losslessly: NiPoint3Interpolator (nif.xml:
///     Value Vector3, Data) and NiBoolInterpolator or NiBoolTimelineInterpolator (nif.xml: Value bool, Data). ONE decode
///     path serves the renderer and the model reader (cut-1b owner ruling D5): the renderer's
///     NifRenderPropertyReader.ReadAnimatedEmissiveColor and NifParticleSystemParser read the same bytes and can adopt
///     these views. Each block must have its type's exact 20.2.0.7 size (a bool is one byte since 4.1.0.1).
/// </summary>
internal static class NifPropertyInterpolatorReader
{
    /// <summary>The bytes an NiPoint3Interpolator stores: three floats and the Data ref.</summary>
    public const int Point3InterpolatorSize = 16;

    /// <summary>The bytes an NiBoolInterpolator stores: the Value byte and the Data ref.</summary>
    public const int BoolInterpolatorSize = 5;

    private const string Point3InterpolatorType = "NiPoint3Interpolator";
    private const string BoolInterpolatorType = "NiBoolInterpolator";
    private const string BoolTimelineInterpolatorType = "NiBoolTimelineInterpolator";

    /// <summary>Reads an NiPoint3Interpolator losslessly.</summary>
    /// <param name="data">The file bytes.</param>
    /// <param name="nif">NifParser's header and block table.</param>
    /// <param name="block">The interpolator block.</param>
    /// <param name="view">The view.</param>
    /// <returns>False when the block is not an NiPoint3Interpolator or is not exactly its 16 bytes.</returns>
    public static bool TryReadPoint3InterpolatorView(
        byte[] data, NifInfo nif, BlockInfo block, out NifPoint3InterpolatorView view)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(nif);
        ArgumentNullException.ThrowIfNull(block);
        view = default;
        if (!string.Equals(block.TypeName, Point3InterpolatorType, StringComparison.Ordinal) ||
            !HasExactReadableSpan(data, block, Point3InterpolatorSize))
        {
            return false;
        }

        var be = nif.IsBigEndian;
        var pos = block.DataOffset;
        view = new NifPoint3InterpolatorView(
            BinaryUtils.ReadUInt32(data, pos, be),
            BinaryUtils.ReadUInt32(data, pos + 4, be),
            BinaryUtils.ReadUInt32(data, pos + 8, be),
            BinaryUtils.ReadInt32(data, pos + 12, be));
        return true;
    }

    /// <summary>Reads an NiBoolInterpolator or NiBoolTimelineInterpolator losslessly.</summary>
    /// <param name="data">The file bytes.</param>
    /// <param name="nif">NifParser's header and block table.</param>
    /// <param name="block">The interpolator block.</param>
    /// <param name="view">The view.</param>
    /// <returns>False when the block is neither bool interpolator type or is not exactly its 5 bytes.</returns>
    public static bool TryReadBoolInterpolatorView(
        byte[] data, NifInfo nif, BlockInfo block, out NifBoolInterpolatorView view)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(nif);
        ArgumentNullException.ThrowIfNull(block);
        view = default;
        if (!IsBoolInterpolatorType(block.TypeName) || !HasExactReadableSpan(data, block, BoolInterpolatorSize))
        {
            return false;
        }

        var pos = block.DataOffset;
        view = new NifBoolInterpolatorView(data[pos], BinaryUtils.ReadInt32(data, pos + 1, nif.IsBigEndian));
        return true;
    }

    /// <summary>True for the two bool interpolator types.</summary>
    /// <param name="typeName">A block type name.</param>
    /// <returns>True for NiBoolInterpolator and NiBoolTimelineInterpolator.</returns>
    public static bool IsBoolInterpolatorType(string typeName)
    {
        return string.Equals(typeName, BoolInterpolatorType, StringComparison.Ordinal) ||
               string.Equals(typeName, BoolTimelineInterpolatorType, StringComparison.Ordinal);
    }

    private static bool HasExactReadableSpan(byte[] data, BlockInfo block, int size)
    {
        return block.DataOffset >= 0 && block.Size == size && (long)block.DataOffset + size <= data.LongLength;
    }
}
