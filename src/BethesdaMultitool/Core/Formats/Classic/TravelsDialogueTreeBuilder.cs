using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Classic;

/// <summary>
///     Shapes the NPC dialogue lines the Travels record synthesizers already produce into the
///     <see cref="DialogueTreeResult" /> the dialogue viewers consume.
///     <para>
///         Nothing is decoded here. Stormhold and Dawnstar ship <c>npcstrings.dat</c> as a run of
///         groups — one per NPC in code order, then the generic/system set — and
///         <c>TravelsRecordSynthesizer</c> already emits one record per line carrying its
///         <c>Group</c>, <c>Line</c> and <c>Text</c>. This is a projection of those records, so a
///         game whose strings parse gets a dialogue tree for free.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Every topic lands in <see cref="DialogueTreeResult.OrphanTopics" />, and that is the
///             honest bucket rather than a shortcut.
///         </b>
///         "Orphan" means a topic no quest reaches, which
///         is exactly true here: these games organise dialogue by SPEAKER, not by quest, and there
///         is no quest-to-dialogue link in the data to represent. Inventing
///         <see cref="DialogueTreeResult.QuestTrees" /> entries would put a structure on screen that
///         the games do not have — the same mistake as faking an ESM worldspace for a tile-grid
///         game.
///     </para>
///     <para>
///         For the same reason the synthesized <see cref="DialogueRecord" /> carries only what the
///         source actually says: a FormID, an editor id, the line's index and its text. Speaker,
///         quest, conditions, emotions and voice types stay empty because none of them exist in
///         <c>npcstrings.dat</c>, and defaulting them to zero would read as "neutral emotion,
///         speaker 0" rather than "not present".
///     </para>
/// </summary>
internal static class TravelsDialogueTreeBuilder
{
    /// <summary>
    ///     Field names the record synthesizer writes. Read by name rather than by position because
    ///     the record is a general-purpose bag, and a silently renamed field should produce an empty
    ///     tree that is obviously wrong rather than a plausible one built from defaults.
    /// </summary>
    public const string GroupField = "Group";

    /// <summary>Field holding the line's ordinal within its group.</summary>
    public const string LineField = "Line";

    /// <summary>Field holding the line's text.</summary>
    public const string TextField = "Text";

    /// <summary>
    ///     Builds a tree from every record of <paramref name="signature" /> in
    ///     <paramref name="records" />.
    /// </summary>
    /// <param name="records">The synthesized records; anything not matching is ignored.</param>
    /// <param name="signature">
    ///     The dialogue record signature — <c>SNPC</c> for Stormhold, <c>DNPC</c> for Dawnstar.
    /// </param>
    /// <param name="speakerName">
    ///     Optional resolver from a group ordinal to a speaker name. Groups are NPCs in code order
    ///     and the file itself carries no names, so a caller that knows them can supply them; when
    ///     it cannot, the topic is labelled by its ordinal rather than given an invented name.
    /// </param>
    public static DialogueTreeResult Build(
        IEnumerable<GenericEsmRecord> records,
        string signature,
        Func<int, string?>? speakerName = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);

        var byGroup = new SortedDictionary<int, List<(int Line, GenericEsmRecord Record)>>();
        foreach (var record in records)
        {
            if (!string.Equals(record.RecordType, signature, StringComparison.Ordinal) ||
                !TryReadInt(record, GroupField, out var group) ||
                !TryReadInt(record, LineField, out var line))
            {
                continue;
            }

            if (!byGroup.TryGetValue(group, out var lines))
            {
                lines = [];
                byGroup[group] = lines;
            }

            lines.Add((line, record));
        }

        var result = new DialogueTreeResult();
        foreach (var (group, lines) in byGroup)
        {
            lines.Sort(static (left, right) => left.Line.CompareTo(right.Line));
            result.OrphanTopics.Add(BuildTopic(group, lines, speakerName));
        }

        return result;
    }

    /// <summary>One speaker's lines, in file order.</summary>
    private static TopicDialogueNode BuildTopic(
        int group,
        List<(int Line, GenericEsmRecord Record)> lines,
        Func<int, string?>? speakerName)
    {
        var name = speakerName?.Invoke(group);
        var topic = new TopicDialogueNode
        {
            TopicFormId = lines.Count > 0 ? lines[0].Record.FormId : 0u,
            TopicName = string.IsNullOrWhiteSpace(name)
                ? string.Create(CultureInfo.InvariantCulture, $"Group {group}")
                : name
        };

        foreach (var (line, record) in lines)
        {
            topic.InfoChain.Add(new InfoDialogueNode
            {
                Info = new DialogueRecord
                {
                    FormId = record.FormId,
                    EditorId = record.EditorId,
                    TopicFormId = topic.TopicFormId,
                    InfoIndex = (ushort)Math.Clamp(line, 0, ushort.MaxValue),
                    Responses =
                    [
                        new DialogueResponse
                        {
                            Text = ReadText(record),

                            // One response per line: these games have no multi-part responses, so
                            // the number is always 1 rather than a running index that would imply
                            // a structure the source does not carry.
                            ResponseNumber = 1
                        }
                    ]
                }
            });
        }

        return topic;
    }

    private static string? ReadText(GenericEsmRecord record)
    {
        return record.Fields.TryGetValue(TextField, out var value) ? value as string : null;
    }

    /// <summary>
    ///     Reads an integer field. The synthesizer boxes them as <see cref="int" />, but a record
    ///     that has been round-tripped through a serializer can arrive as another numeric type, so
    ///     this converts rather than casting — a hard cast would drop every line of a reloaded
    ///     session and leave an empty tree with no error.
    /// </summary>
    private static bool TryReadInt(GenericEsmRecord record, string field, out int value)
    {
        value = 0;
        if (!record.Fields.TryGetValue(field, out var boxed) || boxed is null)
        {
            return false;
        }

        switch (boxed)
        {
            case int direct:
                value = direct;
                return true;
            case IConvertible convertible:
                try
                {
                    value = convertible.ToInt32(CultureInfo.InvariantCulture);
                    return true;
                }
                catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
                {
                    return false;
                }

            default:
                return false;
        }
    }
}
