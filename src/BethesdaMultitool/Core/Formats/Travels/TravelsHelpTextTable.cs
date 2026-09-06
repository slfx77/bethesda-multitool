using System.Collections.Immutable;

namespace BethesdaMultitool.Core.Formats.Travels;

/// <summary>
///     <c>helptext.dat</c> — Dawnstar's in-game help screens. Original RE from the bytes
///     (2026-09-05).
///     <para>
///         Layout (big-endian): a <b>32-bit</b> count followed by that many strings — the same
///         wide count <c>monstersin.dat</c> uses, not the 16-bit one the string lists use.
///     </para>
///     <para>
///         Retail: 3,850 bytes, 35 non-empty lines, alternating short section headings ("Goal",
///         "Combat", "Experience", "Items", …) with body paragraphs. Which line is a heading is a
///         presentation question the format does not answer. <b>Dawnstar-only</b> — Stormhold
///         ships <c>dungnamesin.dat</c> in this slot.
///     </para>
/// </summary>
internal sealed record TravelsHelpTextTable(ImmutableArray<string> Lines)
{
    /// <summary>
    ///     Parses <c>helptext.dat</c>, throwing <see cref="InvalidDataException" /> — naming the
    ///     file and the offending byte position — on a negative count or a layout that does not
    ///     tile the payload.
    /// </summary>
    public static TravelsHelpTextTable Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var reader = new TravelsDataReader(bytes, name);
        var countPosition = reader.Position;
        var count = reader.ReadInt32();
        if (count < 0)
        {
            throw new InvalidDataException(
                $"'{name}': help-line count {count} at byte {countPosition} is negative.");
        }

        var lines = reader.ReadUtfArray(count);
        reader.ExpectEnd();

        return new TravelsHelpTextTable(lines);
    }
}
