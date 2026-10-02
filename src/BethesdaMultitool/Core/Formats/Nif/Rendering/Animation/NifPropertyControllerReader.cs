using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Reads the type-specific fields of the three property controllers that store any (nif.xml at 20.2.0.7):
///     NiTextureTransformController (Shader Map, Texture Slot, Operation after the 30-byte NiSingleInterpController
///     header), NiMaterialColorController (Target Color after that header) and NiUVController (Texture Set and Data
///     after the 26-byte NiTimeController header). NiAlphaController, BSMaterialEmittanceMultController and
///     NiVisController store nothing beyond their Interpolator ref, which <see cref="NifTimeControllerReader" /> and the
///     model reader's Interpolator read already cover. ONE decode path serves the renderer and the model reader (cut-1b
///     owner ruling D5): the renderer's NifTextureAnimationEvaluator and NifRenderPropertyReader read the same bytes
///     and can adopt these views. Each block must have its type's exact size; nothing else is validated (an undefined
///     slot, operation or color is kept as stored).
/// </summary>
internal static class NifPropertyControllerReader
{
    /// <summary>The bytes an NiSingleInterpController stores: the NiTimeController header and the Interpolator ref.</summary>
    public const int SingleInterpControllerSize = NifTimeControllerHeader.HeaderSize + sizeof(int);

    /// <summary>The bytes an NiTextureTransformController stores: the single-interpolator header, a bool, two words.</summary>
    public const int TextureTransformControllerSize = SingleInterpControllerSize + 1 + 2 * sizeof(uint);

    /// <summary>The bytes an NiMaterialColorController stores: the single-interpolator header and a ushort.</summary>
    public const int MaterialColorControllerSize = SingleInterpControllerSize + sizeof(ushort);

    /// <summary>The bytes an NiUVController stores: the NiTimeController header, a ushort and the Data ref.</summary>
    public const int UvControllerSize = NifTimeControllerHeader.HeaderSize + sizeof(ushort) + sizeof(int);

    private const string TextureTransformControllerType = "NiTextureTransformController";
    private const string MaterialColorControllerType = "NiMaterialColorController";
    private const string UvControllerType = "NiUVController";

    /// <summary>Reads an NiTextureTransformController's own fields losslessly.</summary>
    /// <param name="data">The file bytes.</param>
    /// <param name="nif">NifParser's header and block table.</param>
    /// <param name="block">The controller block.</param>
    /// <param name="view">The view.</param>
    /// <returns>False when the block is not an NiTextureTransformController or is not exactly its 39 bytes.</returns>
    public static bool TryReadTextureTransformView(
        byte[] data, NifInfo nif, BlockInfo block, out NifTextureTransformControllerView view)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(nif);
        ArgumentNullException.ThrowIfNull(block);
        view = default;
        if (!string.Equals(block.TypeName, TextureTransformControllerType, StringComparison.Ordinal) ||
            !HasExactReadableSpan(data, block, TextureTransformControllerSize))
        {
            return false;
        }

        var be = nif.IsBigEndian;
        var pos = block.DataOffset + SingleInterpControllerSize;
        view = new NifTextureTransformControllerView(
            data[pos],
            BinaryUtils.ReadUInt32(data, pos + 1, be),
            BinaryUtils.ReadUInt32(data, pos + 5, be));
        return true;
    }

    /// <summary>Reads an NiMaterialColorController's Target Color losslessly.</summary>
    /// <param name="data">The file bytes.</param>
    /// <param name="nif">NifParser's header and block table.</param>
    /// <param name="block">The controller block.</param>
    /// <param name="view">The view.</param>
    /// <returns>False when the block is not an NiMaterialColorController or is not exactly its 32 bytes.</returns>
    public static bool TryReadMaterialColorView(
        byte[] data, NifInfo nif, BlockInfo block, out NifMaterialColorControllerView view)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(nif);
        ArgumentNullException.ThrowIfNull(block);
        view = default;
        if (!string.Equals(block.TypeName, MaterialColorControllerType, StringComparison.Ordinal) ||
            !HasExactReadableSpan(data, block, MaterialColorControllerSize))
        {
            return false;
        }

        view = new NifMaterialColorControllerView(
            BinaryUtils.ReadUInt16(data, block.DataOffset + SingleInterpControllerSize, nif.IsBigEndian));
        return true;
    }

    /// <summary>Reads an NiUVController's own fields losslessly.</summary>
    /// <param name="data">The file bytes.</param>
    /// <param name="nif">NifParser's header and block table.</param>
    /// <param name="block">The controller block.</param>
    /// <param name="view">The view.</param>
    /// <returns>False when the block is not an NiUVController or is not exactly its 32 bytes.</returns>
    public static bool TryReadUvControllerView(
        byte[] data, NifInfo nif, BlockInfo block, out NifUvControllerView view)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(nif);
        ArgumentNullException.ThrowIfNull(block);
        view = default;
        if (!string.Equals(block.TypeName, UvControllerType, StringComparison.Ordinal) ||
            !HasExactReadableSpan(data, block, UvControllerSize))
        {
            return false;
        }

        var be = nif.IsBigEndian;
        var pos = block.DataOffset + NifTimeControllerHeader.HeaderSize;
        view = new NifUvControllerView(BinaryUtils.ReadUInt16(data, pos, be), BinaryUtils.ReadInt32(data, pos + 2, be));
        return true;
    }

    private static bool HasExactReadableSpan(byte[] data, BlockInfo block, int size)
    {
        return block.DataOffset >= 0 && block.Size == size && (long)block.DataOffset + size <= data.LongLength;
    }
}
