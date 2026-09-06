using System.Collections.Immutable;

namespace BethesdaMultitool.Core.Formats.Travels;

/// <summary>
///     <c>monstersin.dat</c> — the monster stat table. Original RE from the bytes (2026-09-05).
///     <para>
///         Layout (big-endian): a <b>32-bit</b> count — the odd one out in this family, every
///         other table counts with a 16-bit word — then that many name strings, then one
///         <see cref="StatColumnCount" />-byte stat row per monster, ROW-major (unlike the item
///         and spell tables, which are column-major).
///     </para>
///     <para>
///         Retail: Stormhold 1,238 bytes / 41 monsters (4 + 537 + 41 x 17); Dawnstar 1,322 bytes /
///         42. Twelve of the rows carry identical stat bytes under different names — the two games
///         reskin the same bestiary.
///     </para>
/// </summary>
internal sealed record TravelsMonsterTable(ImmutableArray<TravelsMonster> Monsters)
{
    /// <summary>Stat bytes per monster row, column 0 (the id) included.</summary>
    public const int StatColumnCount = 17;

    /// <summary>
    ///     Parses <c>monstersin.dat</c>, throwing <see cref="InvalidDataException" /> — naming the
    ///     file and the offending byte position — on a negative count or a layout that does not
    ///     tile the payload.
    /// </summary>
    public static TravelsMonsterTable Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var reader = new TravelsDataReader(bytes, name);
        var countPosition = reader.Position;
        var count = reader.ReadInt32();
        if (count < 0)
        {
            throw new InvalidDataException(
                $"'{name}': monster count {count} at byte {countPosition} is negative.");
        }

        var names = reader.ReadUtfArray(count);

        var monsters = ImmutableArray.CreateBuilder<TravelsMonster>(count);
        for (var i = 0; i < count; i++)
        {
            var stats = ImmutableArray.CreateBuilder<byte>(StatColumnCount);
            for (var c = 0; c < StatColumnCount; c++)
            {
                stats.Add(reader.ReadUInt8());
            }

            var row = stats.MoveToImmutable();
            monsters.Add(new TravelsMonster(row[0], names[i], row));
        }

        reader.ExpectEnd();

        return new TravelsMonsterTable(monsters.MoveToImmutable());
    }
}
