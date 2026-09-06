using System.Globalization;

namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     One row of a zone's <c>.stn</c> table: a condition string and the <c>.ent</c> instance name
///     it applies to. All 59 retail rows read <c>resistDisarm[N]</c> paired with an instance name
///     (<c>z6</c>, <c>door12</c>, <c>Chest02</c>, <c>noname</c>), N being one of 2, 3, 4, 5, 14,
///     15, 16, 17, 18, 20, 25, 28, 30 — a lock/disarm difficulty for a door or chest placed in the
///     same zone.
///     <para>
///         The join is BY NAME. <see cref="EntityName" /> matches the 8-byte instance name of at
///         least one <see cref="ShadowkeyEntity" /> of the same zone in 59/59, and the file holds
///         no numeric index at all. Instance names repeat — <c>noname</c> is the editor default —
///         so resolving one of these yields a LIST of placements, never a single one.
///     </para>
/// </summary>
/// <param name="Condition">The raw condition text, e.g. <c>resistDisarm[15]</c>.</param>
/// <param name="EntityName">An instance name of this zone's <c>.ent</c>.</param>
internal sealed record ShadowkeyLockEntry(string Condition, string EntityName)
{
    private const string ResistDisarmPrefix = "resistDisarm[";

    /// <summary>
    ///     The N of <c>resistDisarm[N]</c>, or <see langword="null" /> when the condition does not
    ///     have that shape. Null never happens on retail, so it is worth reporting rather than
    ///     rejecting: the condition is free text that the engine's script layer evaluates, and a
    ///     second verb would be content, not corruption.
    /// </summary>
    public int? ResistDisarm
    {
        get
        {
            if (!Condition.StartsWith(ResistDisarmPrefix, StringComparison.Ordinal) || !Condition.EndsWith(']'))
            {
                return null;
            }

            var inner = Condition[ResistDisarmPrefix.Length..^1];
            return int.TryParse(inner, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }
    }
}
