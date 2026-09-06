using System.Collections.Immutable;

namespace BethesdaMultitool.Core.Formats.Travels;

/// <summary>
///     One playable class from <c>charin.dat</c>: a row of
///     <see cref="TravelsCharacterTable.ClassRowLength" /> big-endian shorts, joined here with its
///     name from the file's class-name list.
///     <para>
///         Column map (verified on both games' 7 rows): 0 = the row's own index,
///         1 = <see cref="DefaultRaceIndex" />, 2..9 = <see cref="AttributeBases" /> in the
///         attribute-label order Strength, Intelligence, Willpower, Agility, Speed, Endurance,
///         Personality, Luck, 10 = <see cref="MagickaMultiplier" />, 11 and 12 unidentified, then
///         14 rank/percentage pairs.
///     </para>
/// </summary>
/// <param name="Index">Row index, which the row also stores in column 0 (0..6 on retail).</param>
/// <param name="Name">Class name from the file's third string list, joined by index.</param>
/// <param name="DefaultRaceIndex">
///     Column 1: a 0-based index into the race-name list, displayed with the class on the
///     character-creation screen. Retail values 0..4 across the seven classes (Knight-Redguard,
///     Barbarian-Nord, Battlemage-Breton, Sorcerer-High Elf, Nightblade-Wood Elf).
/// </param>
/// <param name="AttributeBases">Columns 2..9: the eight starting attribute values (30/40/50).</param>
/// <param name="MagickaMultiplier">
///     Column 10: maximum Magicka is this times Intelligence divided by 4. Retail 0..12.
/// </param>
/// <param name="Field11">
///     Column 11. Known: a small integer (retail 0..12) that is written into the save game; no
///     read site was found, so it is deliberately unnamed rather than guessed at.
/// </param>
/// <param name="Field12">Column 12. Same standing as <paramref name="Field11" />.</param>
/// <param name="Skills">
///     Columns 13.. : one <see cref="TravelsClassSkill" /> per skill, in skill-name order.
/// </param>
internal sealed record TravelsClassRow(
    int Index,
    string Name,
    short DefaultRaceIndex,
    ImmutableArray<short> AttributeBases,
    short MagickaMultiplier,
    short Field11,
    short Field12,
    ImmutableArray<TravelsClassSkill> Skills);
