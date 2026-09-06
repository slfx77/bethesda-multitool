using System.Collections.Immutable;

namespace BethesdaMultitool.Core.Formats.Travels;

/// <summary>
///     <c>droppeditemsin.dat</c> — the loot table. Original RE from the bytes (2026-09-05).
///     <para>
///         Layout (big-endian): <c>i16 rows</c>, <c>i16 cols</c>, then <c>rows x cols</c> bytes,
///         row-major. The dimensions come from the file, so the parser accepts any grid that
///         tiles; retail is 43 x 5 in both games (4 + 215 = 219 bytes) and the two files are
///         <b>byte-identical</b>.
///     </para>
///     <para>
///         The cells are 1-based ids read unsigned. A row is picked by dungeon depth; the best of
///         the killed monster's loot rolls picks the column — 0 a Filled Crystal (Dawnstar: a
///         Magic Item), 1 the Scroll item, 2 the spell written on that scroll, 3 an armour piece,
///         4 a weapon.
///     </para>
/// </summary>
internal sealed record TravelsDropTable(int RowCount, int ColumnCount, ImmutableArray<ImmutableArray<byte>> Rows)
{
    /// <summary>
    ///     Parses <c>droppeditemsin.dat</c>, throwing <see cref="InvalidDataException" /> — naming
    ///     the file and the offending byte position — on negative dimensions or a grid that does
    ///     not tile the payload.
    /// </summary>
    public static TravelsDropTable Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var reader = new TravelsDataReader(bytes, name);
        var headerPosition = reader.Position;
        int rowCount = reader.ReadInt16();
        int columnCount = reader.ReadInt16();
        if (rowCount < 0 || columnCount < 0)
        {
            throw new InvalidDataException(
                $"'{name}': loot grid {rowCount}x{columnCount} at byte {headerPosition} has a negative dimension.");
        }

        var rows = ImmutableArray.CreateBuilder<ImmutableArray<byte>>(rowCount);
        for (var r = 0; r < rowCount; r++)
        {
            var row = ImmutableArray.CreateBuilder<byte>(columnCount);
            for (var c = 0; c < columnCount; c++)
            {
                row.Add(reader.ReadUInt8());
            }

            rows.Add(row.MoveToImmutable());
        }

        reader.ExpectEnd();

        return new TravelsDropTable(rowCount, columnCount, rows.MoveToImmutable());
    }
}
