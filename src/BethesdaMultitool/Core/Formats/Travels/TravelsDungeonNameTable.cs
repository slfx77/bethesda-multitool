using System.Collections.Immutable;

namespace BethesdaMultitool.Core.Formats.Travels;

/// <summary>
///     <c>dungnamesin.dat</c> — Stormhold's dungeon names. Original RE from the bytes
///     (2026-09-05).
///     <para>
///         <b>Headerless and countless:</b> exactly <see cref="TravelsGeometryTable.RowCount" />
///         pairs of strings — two display lines per dungeon, in dungeon-id order — with no count
///         word and no terminator. The engine hard-codes the 37, matching
///         <c>geomin.dat</c>'s 37 rows.
///     </para>
///     <para>
///         Retail: 563 bytes, 37 pairs, tiling exactly. <b>Stormhold-only</b> — Dawnstar's
///         <c>datfiles.lmp</c> ships <c>helptext.dat</c> in this slot and keeps its dungeon names
///         somewhere other than a data table.
///     </para>
/// </summary>
internal sealed record TravelsDungeonNameTable(ImmutableArray<TravelsDungeonName> Names)
{
    /// <summary>Name pairs in the file, one per dungeon.</summary>
    public const int PairCount = TravelsGeometryTable.RowCount;

    /// <summary>
    ///     Parses <c>dungnamesin.dat</c>, throwing <see cref="InvalidDataException" /> — naming the
    ///     file and the offending byte position — when the 74 strings do not tile the payload.
    /// </summary>
    public static TravelsDungeonNameTable Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var reader = new TravelsDataReader(bytes, name);
        var names = ImmutableArray.CreateBuilder<TravelsDungeonName>(PairCount);
        for (var i = 0; i < PairCount; i++)
        {
            var first = reader.ReadUtf();
            var second = reader.ReadUtf();
            names.Add(new TravelsDungeonName(i + 1, first, second));
        }

        reader.ExpectEnd();

        return new TravelsDungeonNameTable(names.MoveToImmutable());
    }
}
