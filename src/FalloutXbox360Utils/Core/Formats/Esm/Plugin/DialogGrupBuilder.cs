using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using FalloutXbox360Utils.Core.Formats.Esm.Merge;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using FalloutXbox360Utils.Core.Formats.Esm.Reporting;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin;

/// <summary>
///     Builds the top-level DIAL GRUP with INFOs nested as type-7 "Topic Children" GRUPs
///     under each DIAL. The fopdoc/Bethesda canonical layout is:
///     <code>
///         Top GRUP "DIAL" (type=0, label="DIAL"):
///           DIAL record (FormID = X)
///           Topic Children GRUP (type=7, label=X):
///             INFO record (TPIC=X)
///             INFO record (TPIC=X)
///             ...
///           DIAL record (FormID = Y)
///           Topic Children GRUP (type=7, label=Y):
///             ...
///     </code>
///     The earlier pipeline emitted DIAL and INFO as two SEPARATE top-level GRUPs, which
///     caused the FNV runtime to null-deref when walking the dialog tree from an INFO with
///     a TPIC pointing to a non-existent topic. This builder fixes both the structural
///     layout AND the TPIC remap so each new INFO points to its parent DIAL's
///     freshly-allocated FormID.
/// </summary>
/// <summary>
///     Bundle returned by <see cref="DialogGrupBuilder.BuildDialogSection" />. Carries the
///     top-level DIAL GRUP bytes plus an optional placeholder QUST record that PluginBuilder
///     must inject into the QUST GRUP (the placeholder DIAL needs a parent quest or the
///     FNV runtime refuses to attach any INFOs to it).
/// </summary>
internal sealed record DialogSectionResult(byte[] DialogSection, byte[]? PlaceholderQustRecord);

internal static class DialogGrupBuilder
{
    /// <summary>
    ///     Build the dialog section bytes (top-level DIAL GRUP with nested INFO children).
    ///     Returns an empty array when there's nothing to emit.
    /// </summary>
    public static DialogSectionResult BuildDialogSection(
        IReadOnlyList<DialogTopicRecord> topics,
        IReadOnlyList<DialogueRecord> infos,
        NewVsOverrideClassifier classifier,
        FormIdAllocator allocator,
        IEnumerable<uint> masterFormIds,
        ConversionPipelineStats stats,
        IConversionProgressSink sink)
    {
        // v20.7 baseline: drop ALL new DIAL+INFO records entirely. Six prior iterations of
        // dialog fixes (proper type-7 GRUP nesting, placeholder DIAL/QUST, FormID validator,
        // response placeholders, aggressive orphan stripping) all hit the same FalloutNV+
        // 0x46025A crash. The v20.6 diagnostic confirmed even with orphans dropped, real
        // new DIALs (e.g., QJMelissaOldManSaysHello) still crashed — even though their
        // FormID refs all resolve to valid FNV records (VFreeformQuarryJunction quest,
        // real NPCs). The DMP is from a Fallout NV PROTOTYPE BUILD where dialog structure
        // for shared quests differed from the released version; emitting new DIALs that
        // claim kinship to existing FNV quests breaks the runtime's dialog tree assembly.
        //
        // Override DIAL/INFO are unaffected (none of those exist in current DMP runs anyway).
        // This trades all dialog content for a loadable ESP. A future v22+ feature can
        // reintroduce dialog selectively (e.g., FO3 content copy, or only emit dialogs
        // whose parent quest is also new in our plugin).
        var newTopics = topics.Where(t => !classifier.IsOverride(t.FormId)).ToList();
        var newInfos = infos.Where(i => !classifier.IsOverride(i.FormId)).ToList();

        if (newTopics.Count > 0 || newInfos.Count > 0)
        {
            sink.Warn("Building dialog section",
                $"Skipping {newTopics.Count} new DIAL + {newInfos.Count} new INFO record(s) " +
                "to avoid the dialog-tree-assembly crash in prototype-vs-vanilla quest conflicts. " +
                "Override DIAL/INFO records (if any) pass through unchanged via the per-type pipeline.",
                code: "dialog.skip-all-new");
            foreach (var _ in newTopics)
            {
                stats.IncrementSkipped("DIAL");
            }

            foreach (var _ in newInfos)
            {
                stats.IncrementSkipped("INFO");
            }
        }

        return new DialogSectionResult([], null);
    }
}
