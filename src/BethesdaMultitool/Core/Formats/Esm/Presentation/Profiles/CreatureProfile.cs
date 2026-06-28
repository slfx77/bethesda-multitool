using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Games;
using static BethesdaMultitool.Core.Formats.Esm.Presentation.Profiles.DecodedTreeReader;

namespace BethesdaMultitool.Core.Formats.Esm.Presentation.Profiles;

/// <summary>
///     The CREA presentation profile — reproduces <see cref="RecordDetailBuilders.BuildCreature" />'s six
///     sections (Identity / Combat / AI &amp; Runtime / Factions / Spells &amp; Abilities / AI Packages) from a
///     schema-decoded tree. Closest sibling to <see cref="NpcProfile" />: shares the ACBS-Level raw-tail read,
///     the SNAM "Factions" array shape, and SPLO/PKID FormID arrays. FNV byte-exact (proven by
///     <c>CreatureProfileParityTests</c>); other games best-effort on FNV-specific values.
///     <para>
///         One handler/schema offset divergence: the typed <c>ParseAiData</c> reads AIDT Assistance from byte
///         18 (not the schema's offset-14 "Assistance" field), which lands inside the decoded "Aggro Radius"
///         S32 — reconstructed here as its 3rd byte. DATA labels differ from the handler's SubrecordSchemaView
///         names (Type / Combat Skill / Magic Skill / Stealth Skill / Damage); MODL decodes as a child of the
///         one-level "Model" group.
///     </para>
/// </summary>
internal sealed class CreatureProfile : IRecordProfile
{
    public string RecordType => "CREA";

    public RecordDetailModel Build(
        uint formId, string? editorId, string? displayName,
        IReadOnlyList<DecodedNode> tree, BethesdaGame game, FormIdResolver resolver)
    {
        var data = TopBySignature(tree, "DATA");
        var aiData = AiData(tree);

        var creatureType = (byte)(Int(ChildByLabel(data, "Type")) ?? 0);

        var sections = new List<RecordDetailSection>
        {
            RecordDetailHelpers.Section("Identity",
            [
                RecordDetailHelpers.Scalar("Form ID", $"0x{formId:X8}"),
                RecordDetailHelpers.Scalar("Editor ID", editorId ?? "(none)"),
                RecordDetailHelpers.Scalar("Name", displayName ?? "(none)"),
                RecordDetailHelpers.Scalar("Type", new CreatureRecord { CreatureType = creatureType }.CreatureTypeName),
                RecordDetailHelpers.Scalar("Level", Level(tree))
            ]),
            RecordDetailHelpers.Section("Combat",
            [
                RecordDetailHelpers.Scalar("Attack Damage", ((short)(Int(ChildByLabel(data, "Damage")) ?? 0)).ToString()),
                RecordDetailHelpers.Scalar("Combat Skill", ((byte)(Int(ChildByLabel(data, "Combat Skill")) ?? 0)).ToString()),
                RecordDetailHelpers.Scalar("Magic Skill", ((byte)(Int(ChildByLabel(data, "Magic Skill")) ?? 0)).ToString()),
                RecordDetailHelpers.Scalar("Stealth Skill", ((byte)(Int(ChildByLabel(data, "Stealth Skill")) ?? 0)).ToString())
            ]),
            RecordDetailHelpers.Section("AI & Runtime",
            [
                RecordDetailHelpers.Link("Script", KeepZero(TopBySignature(tree, "SCRI")), resolver),
                RecordDetailHelpers.Link("Death Item", KeepZero(TopBySignature(tree, "INAM")), resolver),
                RecordDetailHelpers.Scalar("Aggression", aiData?.AggressionName),
                RecordDetailHelpers.Scalar("Confidence", aiData?.ConfidenceName),
                RecordDetailHelpers.Scalar("Assistance", aiData?.AssistanceName),
                RecordDetailHelpers.Scalar("Mood", aiData?.MoodName),
                RecordDetailHelpers.Scalar("Energy", aiData?.EnergyLevel.ToString()),
                RecordDetailHelpers.Scalar("Model", Str(ChildByLabel(TopByLabel(tree, "Model"), "Model FileName")))
            ]),
            RecordDetailHelpers.ListSection("Factions", Factions(tree, resolver)),
            RecordDetailHelpers.ListSection("Spells & Abilities",
                RefList(TopByLabel(tree, "Actor Effects")).Select(id => RecordDetailHelpers.ListLinkItem(id, resolver)).ToList()),
            RecordDetailHelpers.ListSection("AI Packages",
                RefList(TopByLabel(tree, "Packages")).Select(id => RecordDetailHelpers.ListLinkItem(id, resolver)).ToList())
        };

        return RecordDetailHelpers.Model("CREA", formId, editorId, displayName, sections);
    }

    // ACBS Level union → raw-tail child starting at ACBS offset 8 (S16); same read NpcProfile uses. The typed
    // Stats are only populated when ACBS == 24, in which case the tail is >= 16 bytes.
    private static string Level(IReadOnlyList<DecodedNode> tree)
    {
        var levelBytes = Bytes(ChildByLabel(TopBySignature(tree, "ACBS"), "Level"));
        return levelBytes is { Length: >= 16 } && ReadS16(levelBytes, 0) is { } level
            ? level.ToString()
            : "(unknown)";
    }

    // Reconstruct NpcAiData exactly as the typed ParseAiData does: bytes 0-4 + the U32 flags @8, and
    // Assistance from byte 18 (handler reads data[18], NOT the schema's offset-14 field) — that byte is the
    // 3rd of the decoded "Aggro Radius" S32. Null when AIDT is absent / < 12 bytes (no flags field).
    private static NpcAiData? AiData(IReadOnlyList<DecodedNode> tree)
    {
        var aidt = TopBySignature(tree, "AIDT");
        if (aidt is null || ChildByLabel(aidt, "Buys/Sells and Services") is null)
        {
            return null;
        }

        var aggroRadius = Int(ChildByLabel(aidt, "Aggro Radius"));
        var assistance = aggroRadius is { } ar ? (byte)((ar >> 16) & 0xFF) : (byte)0;
        return new NpcAiData(
            (byte)(Int(ChildByLabel(aidt, "Aggression")) ?? 0),
            (byte)(Int(ChildByLabel(aidt, "Confidence")) ?? 0),
            (byte)(Int(ChildByLabel(aidt, "Energy Level")) ?? 0),
            (byte)(Int(ChildByLabel(aidt, "Responsibility")) ?? 0),
            (byte)(Int(ChildByLabel(aidt, "Mood")) ?? 0),
            (uint)(Int(ChildByLabel(aidt, "Buys/Sells and Services")) ?? 0),
            assistance);
    }

    private static List<RecordDetailListItem> Factions(IReadOnlyList<DecodedNode> tree, FormIdResolver resolver)
    {
        var node = TopByLabel(tree, "Factions");
        if (node is null)
        {
            return [];
        }

        return node.Children.Select(elem =>
        {
            var faction = KeepZero(ChildByLabel(elem, "Faction")) ?? 0;
            var rank = Int(ChildByLabel(elem, "Rank")) ?? 0;
            return new RecordDetailListItem
            {
                Label = resolver.GetBestNameWithRefChain(faction) ?? $"0x{faction:X8}",
                Value = $"Rank {rank}",
                LinkedFormId = faction
            };
        }).ToList();
    }

    // A FormID kept even when zero (typed fields assigned straight from ReadFormID: Script, Death Item).
    private static uint? KeepZero(DecodedNode? node) => node?.RawValue as uint?;

    // The FormIDs of an array node's children (Actor Effects / Packages), keeping zero entries like the
    // typed handler (which adds every SPLO/PKID it reads).
    private static IEnumerable<uint> RefList(DecodedNode? arrayNode) =>
        arrayNode?.Children.Select(c => c.RawValue as uint?).Where(v => v.HasValue).Select(v => v!.Value) ?? [];
}
