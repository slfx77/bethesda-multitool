namespace BethesdaMultitool.Core.Formats.Travels;

/// <summary>
///     One row of <c>spellsin.dat</c>, assembled from the file's six parallel columns plus its
///     name and description strings.
/// </summary>
/// <param name="Id">
///     1-based spell id. The loot table writes it into a scroll as <c>item | spell &lt;&lt; 8</c>,
///     so the ordinal is the spell's identity.
/// </param>
/// <param name="Name">Spell name from the file's leading string list.</param>
/// <param name="SchoolSkill">
///     Column 0: a 0-based index into <c>charin.dat</c>'s skill list. Retail carries exactly
///     1, 3, 4, 6 and 10 — Alteration, Conjuration, Destruction, Illusion, Restoration, the five
///     magic schools — five consecutive spells each, so spell <c>i</c> is the
///     <c>i % 5</c>-th spell of school <c>i / 5</c> and the known-spell bitmask uses bit <c>i</c>.
/// </param>
/// <param name="Cost">Column 1: Magicka cost, shown as "Cost:" (retail 6..20).</param>
/// <param name="Duration">
///     Column 2: 30 or 60 turns on the timed self-buffs and the conjured weapon. The −1/−2/−3 the
///     other twenty rows carry are stored but never read as a duration; whether they encode an
///     effect class is unconfirmed.
/// </param>
/// <param name="Target">Column 3: 1 = self, 2 = monster, 3 = trap (Disarm Trap alone).</param>
/// <param name="BaseChance">
///     Column 4: base success percentage for the self spells that can fail (25..45), 0 otherwise.
/// </param>
/// <param name="RankRequired">Column 5: skill rank needed to cast (2..9), 0 otherwise.</param>
/// <param name="Description">Description string, from the list after the columns.</param>
internal sealed record TravelsSpell(
    int Id,
    string Name,
    sbyte SchoolSkill,
    sbyte Cost,
    sbyte Duration,
    sbyte Target,
    sbyte BaseChance,
    sbyte RankRequired,
    string Description);
