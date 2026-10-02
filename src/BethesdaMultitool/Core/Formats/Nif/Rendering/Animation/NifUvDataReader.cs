using BethesdaMultitool.Core.Formats.Nif.Parser;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>The four NiUVData channels, in file order (nif.xml NiUVData: UV Groups[4]).</summary>
internal sealed record NifUvData(
    NifKeyInterpolation UTranslationInterpolation,
    NifFloatKey[] UTranslationKeys,
    NifKeyInterpolation VTranslationInterpolation,
    NifFloatKey[] VTranslationKeys,
    NifKeyInterpolation UScaleInterpolation,
    NifFloatKey[] UScaleKeys,
    NifKeyInterpolation VScaleInterpolation,
    NifFloatKey[] VScaleKeys);

/// <summary>
///     Reads a NiUVData block: exactly four float KeyGroups: U translation, V translation, U scale,
///     V scale. This is the TES3-era UV-animation payload (waterfalls, lava) referenced by
///     NiUVController; the TES4+ equivalent (NiTextureTransformController over NiFloatData) is handled
///     by <c>NifTextureAnimationEvaluator</c>.
/// </summary>
/// <remarks>
///     <see cref="TryReadView" /> is the lossless walk of the same four groups (cut-1b owner ruling D5: one decode path
///     for the renderer and the model reader); <see cref="TryRead" /> is the renderer's projection of it, unchanged.
/// </remarks>
internal static class NifUvDataReader
{
    private const string UvDataType = "NiUVData";

    internal static NifUvData? TryRead(byte[] data, BlockInfo block, bool be)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        if (!NifKeyGroupReader.TryReadFloatKeys(data, ref pos, end, be, out var uTransInterp, out var uTransKeys) ||
            !NifKeyGroupReader.TryReadFloatKeys(data, ref pos, end, be, out var vTransInterp, out var vTransKeys) ||
            !NifKeyGroupReader.TryReadFloatKeys(data, ref pos, end, be, out var uScaleInterp, out var uScaleKeys) ||
            !NifKeyGroupReader.TryReadFloatKeys(data, ref pos, end, be, out var vScaleInterp, out var vScaleKeys))
        {
            return null;
        }

        return new NifUvData(
            uTransInterp, uTransKeys,
            vTransInterp, vTransKeys,
            uScaleInterp, uScaleKeys,
            vScaleInterp, vScaleKeys);
    }

    /// <summary>
    ///     Reads the four float key groups of an NiUVData block losslessly (<see cref="NifKeyGroupView" />), in file order.
    ///     The block span must lie inside <paramref name="data" />; whether the groups consume it exactly is reported by
    ///     the view.
    /// </summary>
    /// <param name="data">The file bytes.</param>
    /// <param name="nif">NifParser's header and block table.</param>
    /// <param name="block">The NiUVData block.</param>
    /// <param name="view">The view.</param>
    /// <returns>False when the block is not an NiUVData or one of its groups does not read.</returns>
    internal static bool TryReadView(byte[] data, NifInfo nif, BlockInfo block, out NifUvDataView view)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(nif);
        ArgumentNullException.ThrowIfNull(block);
        view = default;
        if (!string.Equals(block.TypeName, UvDataType, StringComparison.Ordinal) ||
            block.DataOffset < 0 || block.Size < 0 || (long)block.DataOffset + block.Size > data.LongLength)
        {
            return false;
        }

        var be = nif.IsBigEndian;
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;
        if (!NifKeyGroupReader.TryReadGroupView(data, ref pos, end, be, NifKeyValueLayout.Float, out var uOffset) ||
            !NifKeyGroupReader.TryReadGroupView(data, ref pos, end, be, NifKeyValueLayout.Float, out var vOffset) ||
            !NifKeyGroupReader.TryReadGroupView(data, ref pos, end, be, NifKeyValueLayout.Float, out var uScale) ||
            !NifKeyGroupReader.TryReadGroupView(data, ref pos, end, be, NifKeyValueLayout.Float, out var vScale))
        {
            return false;
        }

        view = new NifUvDataView(uOffset, vOffset, uScale, vScale, pos == end);
        return true;
    }
}
