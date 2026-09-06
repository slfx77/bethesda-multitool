using System.Collections.Immutable;

namespace BethesdaMultitool.Core.Formats.Travels;

/// <summary>
///     <c>npcstrings.dat</c> — NPC dialogue and the generic system messages. Original RE from the
///     bytes (2026-09-05).
///     <para>
///         Layout (big-endian): groups of <c>i32 count</c> + that many strings, read back to back
///         <b>until EOF</b>. Nothing declares how many groups there are; the engine reads a fixed
///         number and asserts each count against a built-in table, but the file is self-describing
///         and reading to EOF reproduces those constants exactly.
///     </para>
///     <para>
///         Retail: Stormhold 12,529 bytes, 8 groups sized 20, 20, 20, 20, 5, 22, 5, 41 = 153
///         strings — one group per NPC in code order and a last group of generic/help text;
///         Dawnstar 11,883 bytes, 10 groups sized 3, 3, 3, 3, 14, 16, 16, 16, 16, 77 = 167. In
///         both games it is a LOOSE file: Dawnstar keeps its other tables inside
///         <c>datfiles.lmp</c> but not this one.
///     </para>
///     <para>
///         <b>Trap:</b> decoding the whole Dawnstar file as UTF-8 fails on 13 bytes ≥ 0x80. They
///         are string LENGTH prefixes; every one of its 167 strings is valid UTF-8 read
///         individually. Stormhold's four U+2019/U+2026 characters are the only non-ASCII text in
///         either game.
///     </para>
/// </summary>
internal sealed record TravelsNpcStringTable(ImmutableArray<ImmutableArray<string>> Groups)
{
    /// <summary>Strings across every group.</summary>
    public int TotalCount
    {
        get
        {
            var total = 0;
            foreach (var group in Groups)
            {
                total += group.Length;
            }

            return total;
        }
    }

    /// <summary>
    ///     Parses <c>npcstrings.dat</c>, throwing <see cref="InvalidDataException" /> — naming the
    ///     file and the offending byte position — on a negative group count or a group that runs
    ///     past the end of the file.
    /// </summary>
    public static TravelsNpcStringTable Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var reader = new TravelsDataReader(bytes, name);
        var groups = ImmutableArray.CreateBuilder<ImmutableArray<string>>();
        while (reader.Remaining > 0)
        {
            var countPosition = reader.Position;
            var count = reader.ReadInt32();
            if (count < 0)
            {
                throw new InvalidDataException(
                    $"'{name}': string group {groups.Count} declares {count} strings at byte "
                    + $"{countPosition}, which is negative.");
            }

            groups.Add(reader.ReadUtfArray(count));
        }

        reader.ExpectEnd();

        return new TravelsNpcStringTable(groups.ToImmutable());
    }
}
