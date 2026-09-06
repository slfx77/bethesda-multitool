using System.Collections.Immutable;

namespace BethesdaMultitool.Core.Formats.Travels;

/// <summary>
///     <c>spellsin.dat</c> — the spell list. Original RE from the bytes (2026-09-05).
///     <para>
///         Layout (big-endian): a counted list of spell names, then SIX column-major
///         <c>N</c>-byte arrays, then <c>N</c> description strings. Same column-major trap as
///         <see cref="TravelsItemTable" /> — with the descriptions stranded behind the columns, so
///         a reader that packs the six bytes per spell desynchronises before it reaches them.
///     </para>
///     <para>
///         Retail: 1,272 bytes, 25 spells (2 + 273 name bytes + 6 x 25 + 847 description bytes).
///         The file is <b>byte-identical in Stormhold and Dawnstar</b> — the two games ship the
///         same spell system.
///     </para>
/// </summary>
internal sealed record TravelsSpellTable(ImmutableArray<TravelsSpell> Spells)
{
    /// <summary>Column-major byte arrays between the names and the descriptions.</summary>
    public const int ColumnCount = 6;

    /// <summary>
    ///     Parses <c>spellsin.dat</c>, throwing <see cref="InvalidDataException" /> — naming the
    ///     file and the offending byte position — when the layout does not tile the payload.
    /// </summary>
    public static TravelsSpellTable Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var reader = new TravelsDataReader(bytes, name);
        var names = reader.ReadUtfList16();
        var count = names.Length;

        var columns = new sbyte[ColumnCount, count];
        for (var c = 0; c < ColumnCount; c++)
        {
            for (var i = 0; i < count; i++)
            {
                columns[c, i] = reader.ReadInt8();
            }
        }

        var descriptions = reader.ReadUtfArray(count);
        reader.ExpectEnd();

        var spells = ImmutableArray.CreateBuilder<TravelsSpell>(count);
        for (var i = 0; i < count; i++)
        {
            spells.Add(new TravelsSpell(
                i + 1,
                names[i],
                columns[0, i],
                columns[1, i],
                columns[2, i],
                columns[3, i],
                columns[4, i],
                columns[5, i],
                descriptions[i]));
        }

        return new TravelsSpellTable(spells.MoveToImmutable());
    }
}
