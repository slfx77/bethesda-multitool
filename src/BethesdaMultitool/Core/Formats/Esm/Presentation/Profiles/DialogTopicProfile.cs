using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Games;
using static BethesdaMultitool.Core.Formats.Esm.Presentation.Profiles.DecodedTreeReader;

namespace BethesdaMultitool.Core.Formats.Esm.Presentation.Profiles;

/// <summary>
///     The DIAL presentation profile — reproduces <see cref="RecordDetailBuilders.BuildDialogTopic" /> from a
///     schema-decoded tree plus the record set. The first profile to need cross-record data: the "INFO
///     Records" section is the topic's child INFO lines, pulled from <see cref="RecordCollection.Dialogues" />
///     (the same query the typed builder uses) — hence the <c>records</c> parameter on
///     <see cref="IRecordProfile.Build" />. FNV-exact for an ESM load (proven by
///     <c>DialogTopicProfileParityTests</c>); other games get the same sectioned shape.
///     <para>
///         Identity fields are subrecord-local: Type/Flags from the DATA struct, Quest from the (nested,
///         possibly fragmented) "Added Quests" array (last QSTI, mirroring the handler), Priority from PNAM,
///         Speaker from TNAM (unmodeled by the FNV schema, so a top-level raw node — absent on FNV topics).
///         Responses / Journal Index / Dummy Prompt are runtime-only (TESTopic DMP struct), so they default to
///         0 / 0 / null in an ESM load — exactly what the typed builder yields there.
///     </para>
/// </summary>
internal sealed class DialogTopicProfile : IRecordProfile
{
    public string RecordType => "DIAL";

    public RecordDetailModel Build(
        uint formId, string? editorId, string? displayName,
        IReadOnlyList<DecodedNode> tree, BethesdaGame game, FormIdResolver resolver, RecordCollection? records)
    {
        var data = TopBySignature(tree, "DATA");
        var topicType = (byte)(Int(ChildByLabel(data, "Type")) ?? 0);
        var flags = (byte)(Int(ChildByLabel(data, "Flags")) ?? 0);
        var priority = Float(TopBySignature(tree, "PNAM")) ?? 0f;

        var sections = new List<RecordDetailSection>
        {
            RecordDetailHelpers.Section("Identity",
            [
                RecordDetailHelpers.Scalar("Form ID", $"0x{formId:X8}"),
                RecordDetailHelpers.Scalar("Editor ID", editorId ?? "(none)"),
                RecordDetailHelpers.Scalar("Name", displayName ?? "(none)"),
                RecordDetailHelpers.Scalar("Type", DialogTopicRecord.GetTopicTypeName(topicType)),
                RecordDetailHelpers.Link("Quest", LastQuestFormId(tree), resolver),
                // TNAM is unmodeled by the FNV schema → a top-level raw node (absent on FNV topics).
                RecordDetailHelpers.Link("Speaker", ReadU32(Bytes(TopBySignature(tree, "TNAM")), 0), resolver),
                // ResponseCount is the topic's INFO count — derivable from the record set (the typed builder
                // computes the same during its dialogue pass).
                RecordDetailHelpers.Scalar("Responses",
                    (records?.Dialogues.Count(d => d.TopicFormId == formId) ?? 0).ToString()),
                RecordDetailHelpers.Scalar("Flags", $"0x{flags:X2}"),
                RecordDetailHelpers.Scalar("Priority", priority.ToString("F2")),
                // Journal Index is runtime-only (TESTopic) — 0 from an ESM. Dummy Prompt is an enrichment-
                // derived prompt (conditionally = FullName) the profile doesn't reproduce — FNV keeps the
                // typed builder for it; the parity gate strips it.
                RecordDetailHelpers.Scalar("Journal Index", "0"),
                RecordDetailHelpers.Scalar("Dummy Prompt", null)
            ])
        };

        if (records != null)
        {
            var infos = records.Dialogues
                .Where(dialogue => dialogue.TopicFormId == formId)
                .Take(20)
                .Select(dialogue => new RecordDetailListItem
                {
                    Label = $"0x{dialogue.FormId:X8}",
                    Value = dialogue.Responses.FirstOrDefault()?.Text
                            ?? dialogue.PromptText
                            ?? "(no text)",
                    LinkedFormId = dialogue.FormId
                })
                .ToList();
            sections.Add(RecordDetailHelpers.ListSection("INFO Records", infos));
        }

        return RecordDetailHelpers.Model("DIAL", formId, editorId, displayName, sections);
    }

    // QSTI is nested under "Added Quests" → "Added Quest [n]" → "Quest"; the typed handler overwrites on each
    // QSTI, so the parent quest is the LAST one in stream order.
    private static uint? LastQuestFormId(IReadOnlyList<DecodedNode> tree)
    {
        uint? last = null;
        foreach (var addedQuests in tree.Where(n => n.Label == "Added Quests"))
        {
            foreach (var addedQuest in addedQuests.Children)
            {
                if (ChildByLabel(addedQuest, "Quest")?.RawValue as uint? is { } q)
                {
                    last = q;
                }
            }
        }

        return last;
    }
}
