using System.Collections.Immutable;

namespace BethesdaMultitool.Core.Formats.Travels;

/// <summary>
///     <c>geomin.dat</c> — the dungeon adjacency graph. Original RE from the bytes (2026-09-05).
///     <para>
///         <b>Headerless:</b> exactly <see cref="RowCount" /> x <see cref="ColumnCount" /> signed
///         bytes, row-major, and nothing else — the engine hard-codes both dimensions, so the file
///         length <see cref="ByteLength" /> IS the format check.
///     </para>
///     <para>
///         Retail: 222 bytes, 37 rows, <b>byte-identical in Stormhold and Dawnstar</b> — the two
///         games ship the same dungeon graph under different names (and Dawnstar ships no dungeon
///         names at all). Row 0 is the camp, linking N/E/S/W to dungeons 2, 11, 20 and 29 with no
///         doorways of its own; the graph is reciprocal on 100 % of retail links.
///     </para>
/// </summary>
internal sealed record TravelsGeometryTable(ImmutableArray<TravelsDungeonLink> Dungeons)
{
    /// <summary>Dungeon rows the engine hard-codes (row 0 is the camp).</summary>
    public const int RowCount = 37;

    /// <summary>Bytes per row: four neighbour ids and two doorway direction codes.</summary>
    public const int ColumnCount = 6;

    /// <summary>The file's only possible length.</summary>
    public const int ByteLength = RowCount * ColumnCount;

    /// <summary>
    ///     Parses <c>geomin.dat</c>, throwing <see cref="InvalidDataException" /> — naming the file
    ///     and the offending byte position — when the payload is not exactly the 37 x 6 grid.
    /// </summary>
    public static TravelsGeometryTable Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (bytes.Length != ByteLength)
        {
            throw new InvalidDataException(
                $"'{name}': the dungeon graph is a fixed {RowCount}x{ColumnCount} grid of "
                + $"{ByteLength} bytes; this payload is {bytes.Length} bytes.");
        }

        var reader = new TravelsDataReader(bytes, name);
        var rows = ImmutableArray.CreateBuilder<TravelsDungeonLink>(RowCount);
        for (var r = 0; r < RowCount; r++)
        {
            rows.Add(new TravelsDungeonLink(
                r + 1,
                reader.ReadInt8(),
                reader.ReadInt8(),
                reader.ReadInt8(),
                reader.ReadInt8(),
                reader.ReadInt8(),
                reader.ReadInt8()));
        }

        reader.ExpectEnd();

        return new TravelsGeometryTable(rows.MoveToImmutable());
    }
}
