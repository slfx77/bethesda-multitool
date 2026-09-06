using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Classic;

/// <summary>One named value on an actor. The name may be an ordinal when the column is unnamed.</summary>
/// <param name="Name">Column name, or an ordinal label such as <c>Stat 7</c>.</param>
/// <param name="Value">The value as text, formatted invariantly.</param>
/// <param name="IsNamed">
///     False when <see cref="Name" /> is an ordinal placeholder rather than a meaning the reader
///     stands behind. A view should render these differently — an unnamed column is data, not a
///     labelled attribute.
/// </param>
internal readonly record struct ClassicActorStat(string Name, string Value, bool IsNamed);

/// <summary>One actor from a pre-plugin-era game.</summary>
/// <param name="FormId">The synthesized FormID, so a row can be joined back to its record.</param>
/// <param name="Name">Display name.</param>
/// <param name="EditorId">The synthesized editor id.</param>
/// <param name="Kind">What the source called it — <c>Monster</c>, <c>Speaker</c>.</param>
/// <param name="Stats">Named and unnamed columns, in source order.</param>
internal sealed record ClassicActor(
    uint FormId, string Name, string EditorId, string Kind, IReadOnlyList<ClassicActorStat> Stats);

/// <summary>A game's actors, ready for a list view.</summary>
/// <param name="GameName">Label for the list.</param>
/// <param name="Actors">The actors, in source order.</param>
internal sealed record ClassicActorList(string GameName, IReadOnlyList<ClassicActor> Actors);

/// <summary>
///     Projects the actor-shaped records the Travels synthesizers already produce into a
///     game-neutral list.
///     <para>
///         The Actors tab is bound to raw ESM bytes plus a meshes archive and has no abstraction
///         seam, so a classic game cannot reuse it. Rather than fake an ESM actor — which would mean
///         inventing a race, class, faction and FaceGen head none of these games have — this is a
///         small dedicated model a list view can bind to directly. That is the plan's own
///         conclusion, and it is the same reasoning that kept classic games off the worldspace
///         pipeline.
///     </para>
///     <para>
///         ⚠ <b>Unnamed stat columns keep their ordinal labels.</b> The monster table has 17
///         columns and the reader names four; the rest are carried verbatim and marked
///         <see cref="ClassicActorStat.IsNamed" /> false. Giving them plausible names here would
///         launder a hypothesis into the UI, where it would outlive the note explaining it — the
///         record synthesizer makes exactly this call and this projection must not undo it.
///     </para>
/// </summary>
internal static class TravelsActorListBuilder
{
    /// <summary>Record field holding the comma-separated unnamed stat columns.</summary>
    public const string StatsField = "Stats";

    /// <summary>Fields the monster reader is willing to name, in display order.</summary>
    private static readonly string[] NamedMonsterFields =
        ["Id", "Family", "HitPoints", "DropChance", "LootRolls"];

    /// <summary>
    ///     Builds the actor list from every record of <paramref name="monsterSignature" />.
    /// </summary>
    /// <param name="records">The synthesized records; anything else is ignored.</param>
    /// <param name="monsterSignature">
    ///     The monster record signature — <c>SMON</c> for Stormhold, <c>DMON</c> for Dawnstar.
    /// </param>
    /// <param name="gameName">Label for the resulting list.</param>
    public static ClassicActorList Build(
        IEnumerable<GenericEsmRecord> records, string monsterSignature, string gameName)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentException.ThrowIfNullOrWhiteSpace(monsterSignature);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameName);

        var actors = new List<ClassicActor>();
        foreach (var record in records)
        {
            if (!string.Equals(record.RecordType, monsterSignature, StringComparison.Ordinal))
            {
                continue;
            }

            actors.Add(new ClassicActor(
                record.FormId,
                record.FullName ?? record.EditorId ?? string.Empty,
                record.EditorId ?? string.Empty,
                "Monster",
                ReadStats(record)));
        }

        return new ClassicActorList(gameName, actors);
    }

    /// <summary>
    ///     The named columns first, then the unnamed ones as ordinals. Order is stable so a list
    ///     view's columns do not shuffle between actors.
    /// </summary>
    private static List<ClassicActorStat> ReadStats(GenericEsmRecord record)
    {
        var stats = new List<ClassicActorStat>();
        foreach (var field in NamedMonsterFields)
        {
            if (record.Fields.TryGetValue(field, out var value) && value is not null)
            {
                stats.Add(new ClassicActorStat(field, Format(value), true));
            }
        }

        if (!record.Fields.TryGetValue(StatsField, out var packed) || packed is not string text ||
            text.Length == 0)
        {
            return stats;
        }

        var columns = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < columns.Length; i++)
        {
            stats.Add(new ClassicActorStat(
                string.Create(CultureInfo.InvariantCulture, $"Stat {i}"), columns[i], false));
        }

        return stats;
    }

    private static string Format(object value) =>
        value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : value.ToString() ?? string.Empty;
}
