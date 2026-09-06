namespace BethesdaMultitool.Core.Formats.Travels;

/// <summary>
///     One class's starting position in one skill, the <c>(13 + 2k, 14 + 2k)</c> short pair of a
///     <c>charin.dat</c> class row. <see cref="Rank" /> is 0 when the class does not know the
///     skill at all (retail: 0..4, and the known-spell mask is built from <c>Rank &gt; 0</c> on the
///     five magic-school skills), <see cref="BasePercent" /> is the starting percentage (retail:
///     35..45, plus 30 in Stormhold).
/// </summary>
internal sealed record TravelsClassSkill(short Rank, short BasePercent);
