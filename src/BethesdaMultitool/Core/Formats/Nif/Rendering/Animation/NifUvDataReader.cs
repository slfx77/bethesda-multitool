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
///     Reads a NiUVData block — exactly four float KeyGroups: U translation, V translation, U scale,
///     V scale. This is the TES3-era UV-animation payload (waterfalls, lava) referenced by
///     NiUVController; the TES4+ equivalent (NiTextureTransformController → NiFloatData) is handled
///     by <c>NifTextureAnimationEvaluator</c>.
/// </summary>
internal static class NifUvDataReader
{
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
}
