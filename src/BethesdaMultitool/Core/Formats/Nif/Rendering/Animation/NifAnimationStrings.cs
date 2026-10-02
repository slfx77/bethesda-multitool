using BethesdaMultitool.Core.Formats.Nif.Decoding;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Resolves the NiFixedString indices the animation views keep (<see cref="NifControllerSequenceView" />,
///     <see cref="NifControlledBlockView" />, <see cref="NifTextKeyView" />) against the RAW header string table
///     (<see cref="NifHeaderStringTable" />). NifInfo.Strings decodes the table as ASCII and turns every byte at or above
///     0x80 into '?'; the raw table keeps the bytes and Latin-1 maps each byte to one character, so nothing is lost.
/// </summary>
internal static class NifAnimationStrings
{
    /// <summary>The stored index of an absent string (the NULL ref, 0xFFFFFFFF).</summary>
    public const int NoString = -1;

    /// <summary>
    ///     The raw bytes of a string index: true with the bytes for an index into the table, true with
    ///     <paramref name="isNone" /> set for <see cref="NoString" />, false for any other index (out of range).
    /// </summary>
    public static bool TryGetRaw(
        NifHeaderStringTable strings,
        int index,
        out ReadOnlyMemory<byte> bytes,
        out bool isNone)
    {
        ArgumentNullException.ThrowIfNull(strings);
        bytes = ReadOnlyMemory<byte>.Empty;
        isNone = index == NoString;
        if (isNone)
        {
            return true;
        }

        if (index < 0 || index >= strings.Count)
        {
            return false;
        }

        bytes = strings.GetRawBytes(index);
        return true;
    }

    /// <summary>
    ///     A string index as Latin-1 text: true with the text for an index into the table, true with null for
    ///     <see cref="NoString" />, false for any other index (out of range).
    /// </summary>
    public static bool TryGetLatin1(NifHeaderStringTable strings, int index, out string? text)
    {
        ArgumentNullException.ThrowIfNull(strings);
        text = null;
        if (index == NoString)
        {
            return true;
        }

        if (index < 0 || index >= strings.Count)
        {
            return false;
        }

        text = strings.GetText(index);
        return true;
    }
}
