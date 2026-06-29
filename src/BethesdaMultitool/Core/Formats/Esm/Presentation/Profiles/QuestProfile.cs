using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Games;
using static BethesdaMultitool.Core.Formats.Esm.Presentation.Profiles.DecodedTreeReader;

namespace BethesdaMultitool.Core.Formats.Esm.Presentation.Profiles;

/// <summary>
///     The QUST presentation profile — reproduces the tree-derivable sections of
///     <see cref="RecordDetailBuilders.BuildQuest" /> (Identity / Objectives / Stages) from a schema-decoded
///     tree. FNV byte-exact for those sections (proven by <c>QuestProfileParityTests</c>, which compares
///     against a BuildQuest whose enrichment is stripped); other games get the same sectioned shape.
///     <para>
///         BuildQuest's "Variables" and "Related NPCs" are NOT subrecord-local — they are cross-record
///         enrichment (Variables = the linked SCPT's locals via SCRI; Related NPCs = a dialogue-speaker
///         reverse-lookup with HashSet ordering). They're FNV/FO3-only and absent for the schema games this
///         profile serves, so the profile omits them (the QUST analogue of NPC's runtime-only fields), and
///         FNV keeps BuildQuest for its full display.
///     </para>
///     <para>
///         The decoder fragments the nested quest layout: each INDX is its own single-element "Stages" node,
///         each QOBJ its own "Objectives" node (bundling Objective Index + Description), and QSDT stage-flags
///         land as top-level raw nodes. Stages therefore require a stateful walk over the top-level nodes (in
///         stream order) that associates each QSDT with its preceding INDX and flushes on the next INDX/QOBJ —
///         mirroring the typed handler.
///     </para>
/// </summary>
internal sealed class QuestProfile : IRecordProfile
{
    public string RecordType => "QUST";

    public RecordDetailModel Build(
        uint formId, string? editorId, string? displayName,
        IReadOnlyList<DecodedNode> tree, BethesdaGame game, FormIdResolver resolver, RecordCollection? records)
    {
        var data = TopBySignature(tree, "DATA");

        var sections = new List<RecordDetailSection>
        {
            RecordDetailHelpers.Section("Identity",
            [
                RecordDetailHelpers.Scalar("Form ID", $"0x{formId:X8}"),
                RecordDetailHelpers.Scalar("Editor ID", editorId ?? "(none)"),
                RecordDetailHelpers.Scalar("Name", displayName ?? "(none)"),
                RecordDetailHelpers.Scalar("Priority", ((byte)(Int(ChildByLabel(data, "Priority")) ?? 0)).ToString()),
                RecordDetailHelpers.Scalar("Flags", $"0x{(byte)(Int(ChildByLabel(data, "Flags")) ?? 0):X2}"),
                RecordDetailHelpers.Scalar("Quest Delay", (Float(ChildByLabel(data, "Quest Delay")) ?? 0f).ToString("F2")),
                RecordDetailHelpers.Link("Script", KeepZero(TopBySignature(tree, "SCRI")), resolver)
            ]),
            RecordDetailHelpers.ListSection("Objectives", ReadObjectives(tree)
                .OrderBy(o => o.Index)
                .Select(o => new RecordDetailListItem { Label = $"[{o.Index}]", Value = o.Desc ?? "(no text)" })
                .ToList()),
            RecordDetailHelpers.ListSection("Stages", ReadStages(tree)
                .OrderBy(s => s.Index)
                .Select(s => new RecordDetailListItem { Label = $"[{s.Index}]", Value = $"Flags 0x{s.Flags:X2}" })
                .ToList())
        };

        return RecordDetailHelpers.Model("QUST", formId, editorId, displayName, sections);
    }

    // Each QOBJ + NNAM is an "Objective [n]" element bundling "Objective Index" + "Description". Consecutive
    // QOBJs (no QSTA between) group as multiple elements under one "Objectives" node, so iterate every child.
    private static List<(int Index, string? Desc)> ReadObjectives(IReadOnlyList<DecodedNode> tree)
    {
        var objectives = new List<(int, string?)>();
        foreach (var node in tree.Where(n => n.Label == "Objectives"))
        {
            foreach (var objStruct in node.Children)
            {
                if (Int(ChildByLabel(objStruct, "Objective Index")) is { } index)
                {
                    objectives.Add(((int)index, Str(ChildByLabel(objStruct, "Description"))));
                }
            }
        }

        return objectives;
    }

    // Stateful walk mirroring the typed handler: a "Stages" node (one INDX) opens a stage; following top-level
    // QSDT nodes set its flags (last wins); the next INDX or any QOBJ flushes it.
    private static List<(int Index, byte Flags)> ReadStages(IReadOnlyList<DecodedNode> tree)
    {
        var stages = new List<(int, byte)>();
        int? index = null;
        byte flags = 0;

        void Flush()
        {
            if (index is { } i)
            {
                stages.Add((i, flags));
            }

            index = null;
            flags = 0;
        }

        foreach (var node in tree)
        {
            if (node.Label == "Stages")
            {
                // Consecutive INDX (a stage with no log entry) group as multiple "Stage [n]" children; each is
                // its own INDX event, flushing the previous.
                foreach (var stageStruct in node.Children)
                {
                    Flush();
                    index = Int(ChildByLabel(stageStruct, "Stage Index")) is { } v ? (int)v : null;
                }
            }
            else if (node.Signature == "QSDT")
            {
                if (index is not null && Bytes(node) is { Length: >= 1 } b)
                {
                    flags = b[0];
                }
            }
            else if (node.Label == "Objectives")
            {
                Flush();
            }
        }

        Flush();
        return stages;
    }

    private static uint? KeepZero(DecodedNode? node) => node?.RawValue as uint?;
}
