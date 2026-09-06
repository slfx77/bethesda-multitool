using System.Collections.Immutable;

namespace BethesdaMultitool.Core.Formats.Travels;

/// <summary>
///     One row of <c>monstersin.dat</c>: a name plus the row's
///     <see cref="TravelsMonsterTable.StatColumnCount" /> stat bytes, kept in file order.
///     <para>
///         The engine's accessor reads these bytes UNSIGNED, so they are stored as
///         <see cref="byte" />. Only the columns with a settled read site are surfaced as named
///         properties; the rest stay in <see cref="Stats" /> rather than being given invented
///         meanings.
///     </para>
/// </summary>
/// <param name="Id">Column 0, a 1-based id that equals the row ordinal plus one in both games.</param>
/// <param name="Name">Monster name from the file's string list.</param>
/// <param name="Stats">All 17 stat bytes, column 0 included, in file order.</param>
internal sealed record TravelsMonster(int Id, string Name, ImmutableArray<byte> Stats)
{
    /// <summary>
    ///     Column 1: the spawn family that decides which dungeon set the monster appears in
    ///     (Stormhold 8 families of five plus Warden Varus alone; Dawnstar adds a tenth).
    /// </summary>
    public byte Family => Stats[1];

    /// <summary>Column 14: hit points, copied onto the instance when the monster spawns.</summary>
    public byte HitPoints => Stats[14];

    /// <summary>Column 15: percentage chance of dropping loot on death (retail 40 / 80 / 100).</summary>
    public byte DropChance => Stats[15];

    /// <summary>
    ///     Column 16: how many loot-quality rolls the drop gets, best result winning (retail 1..5).
    /// </summary>
    public byte LootRolls => Stats[16];
}
