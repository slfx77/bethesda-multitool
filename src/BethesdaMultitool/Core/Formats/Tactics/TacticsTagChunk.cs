using System.Text;

namespace BethesdaMultitool.Core.Formats.Tactics;

/// <summary>
///     The framing every Fallout Tactics asset opens with: <c>'&lt;' name '&gt;'</c>, a NUL, an
///     ASCII version string, and another NUL. Original RE 2026-09-06; every Tactics reference is
///     GPL, so nothing is ported.
///     <para>
///         ⚑ <b>This is the whole game's file header, not one format's.</b> Measured over every
///         entry in the shipped <c>core\*.bos</c> archives:
///         <b>
///             33,368 of 33,368 tagged files match,
///             with zero malformed
///         </b>
///         , across seven tags —
///         <c>&lt;tile&gt;</c> 29,957, <c>&lt;entity&gt;</c> 1,506, <c>&lt;sprite&gt;</c> 922,
///         <c>&lt;zar&gt;</c> 839, <c>&lt;world&gt;</c> 104, <c>&lt;character&gt;</c> 39 and a
///         single <c>&lt;campaign&gt;</c>.
///     </para>
///     <para>
///         ⚠ The version is a STRING, not a byte, and its length varies: <c>&lt;zar&gt;</c> is
///         <c>"4"</c> while <c>&lt;world&gt;</c> is <c>"68"</c> or <c>"69"</c> and
///         <c>&lt;tile&gt;</c> spans <c>"6"</c>–<c>"10"</c>. So the body offset is NOT a constant —
///         it depends on both the tag and the version length, which is why this is read rather
///         than assumed per format.
///     </para>
///     <para>
///         ⛔ <b>Correction:</b> earlier notes recorded the <c>.chr</c> tag as <c>&lt;esh&gt;</c>.
///         It is <c>&lt;character&gt;</c> — no file in the install carries an <c>&lt;esh&gt;</c>
///         framing.
///     </para>
/// </summary>
internal readonly record struct TacticsTagChunk(string Tag, string Version, int BodyOffset)
{
    /// <summary>Longest tag name accepted, so a stray '&lt;' in binary data cannot scan forever.</summary>
    public const int MaximumTagLength = 32;

    /// <summary>Longest version string accepted.</summary>
    public const int MaximumVersionLength = 16;

    /// <summary>
    ///     Reads the framing, or returns false when the bytes do not carry one.
    ///     <para>
    ///         The version must be terminated by a NUL: without that check a binary payload
    ///         beginning with '&lt;' scans until it happens to find one, which is the difference
    ///         between rejecting a non-Tactics file and mis-reporting its body offset.
    ///     </para>
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> bytes, out TacticsTagChunk chunk)
    {
        chunk = default;
        if (bytes.Length < 4 || bytes[0] != (byte)'<')
        {
            return false;
        }

        var close = bytes[..Math.Min(bytes.Length, MaximumTagLength + 2)].IndexOf((byte)'>');
        if (close <= 1 || close + 1 >= bytes.Length || bytes[close + 1] != 0)
        {
            return false;
        }

        var versionStart = close + 2;
        var end = versionStart;
        while (end < bytes.Length
               && end - versionStart <= MaximumVersionLength
               && bytes[end] is >= 0x20 and < 0x7F)
        {
            end++;
        }

        if (end >= bytes.Length || bytes[end] != 0 || end == versionStart)
        {
            return false;
        }

        chunk = new TacticsTagChunk(
            Encoding.ASCII.GetString(bytes[1..close]),
            Encoding.ASCII.GetString(bytes[versionStart..end]),
            end + 1);
        return true;
    }

    /// <summary>Whether the bytes carry the framing with a specific tag, e.g. <c>zar</c>.</summary>
    public static bool Is(ReadOnlySpan<byte> bytes, string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);

        return TryRead(bytes, out var chunk) && string.Equals(chunk.Tag, tag, StringComparison.Ordinal);
    }
}
