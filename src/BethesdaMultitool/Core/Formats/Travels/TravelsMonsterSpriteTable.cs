using System.Collections.Immutable;

namespace BethesdaMultitool.Core.Formats.Travels;

/// <summary>
///     <c>monsterfilenamesin.dat</c> — the monster sprite-part resource names. Original RE from
///     the bytes (2026-09-05).
///     <para>
///         <b>Headerless and countless:</b> exactly <see cref="FamilyCount" /> x
///         <see cref="PartCount" /> = <see cref="EntryCount" /> strings, row-major, with no count
///         word and no terminator — the engine hard-codes the 5 x 7 grid. An earlier note calling
///         this a terminator-delimited list of 33 strings is REFUTED: 33 is simply Stormhold's
///         non-empty count, and unused parts are empty strings that keep the grid square. Both
///         games tile the file exactly at 35 strings.
///     </para>
///     <para>
///         Retail: Stormhold 698 bytes, 33 non-empty <c>/name.cus</c> paths (the fifth family uses
///         5 of its 7 slots); Dawnstar 482 bytes, 26 non-empty <c>/name.png</c> paths naming
///         members of its <c>imgfiles.lmp</c> — bandit male, bandit female, ice, troll, warden.
///     </para>
/// </summary>
internal sealed record TravelsMonsterSpriteTable(ImmutableArray<ImmutableArray<string>> Families)
{
    /// <summary>Sprite families (graphics chunks) the engine loads.</summary>
    public const int FamilyCount = 5;

    /// <summary>Sprite parts per family; unused ones are empty strings.</summary>
    public const int PartCount = 7;

    /// <summary>Strings in the whole file — no count word declares it.</summary>
    public const int EntryCount = FamilyCount * PartCount;

    /// <summary>
    ///     Parses <c>monsterfilenamesin.dat</c>, throwing <see cref="InvalidDataException" /> —
    ///     naming the file and the offending byte position — when the 35 strings do not tile the
    ///     payload exactly.
    /// </summary>
    public static TravelsMonsterSpriteTable Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var reader = new TravelsDataReader(bytes, name);
        var families = ImmutableArray.CreateBuilder<ImmutableArray<string>>(FamilyCount);
        for (var f = 0; f < FamilyCount; f++)
        {
            families.Add(reader.ReadUtfArray(PartCount));
        }

        reader.ExpectEnd();

        return new TravelsMonsterSpriteTable(families.MoveToImmutable());
    }
}
