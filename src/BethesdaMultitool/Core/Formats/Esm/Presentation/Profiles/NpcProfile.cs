using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Games;
using static BethesdaMultitool.Core.Formats.Esm.Presentation.Profiles.DecodedTreeReader;

namespace BethesdaMultitool.Core.Formats.Esm.Presentation.Profiles;

/// <summary>
///     The NPC_ presentation profile — reproduces <see cref="RecordDetailBuilders.BuildNpc" />'s nine
///     sections from a schema-decoded tree, so every game's NPC renders with FNV-quality curation. For FNV
///     this is byte-for-byte equivalent to the typed builder (proven by the parity test); for the other
///     games it produces the same sectioned shape from their (differing) tree. Reuses the exact same
///     <see cref="RecordDetailHelpers" /> factories so empty-entry/empty-section filtering matches.
///     <para>
///         Three FNV fields sit behind a union / dynamic array in the schema and so arrive as a raw tail
///         child: ACBS Level, DATA Attributes (SPECIAL), DNAM Skill Values — read via leading bytes. CNTO
///         (inventory) isn't expanded by the generator, so each entry is a top-level raw node decoded here
///         (FormID + count). SPECIAL/Skills are FNV/FO3-only (other games' stat models differ — omitted).
///     </para>
/// </summary>
internal sealed class NpcProfile : IRecordProfile
{
    public string RecordType => "NPC_";

    private static readonly string[] SpecialNames =
        ["Strength", "Perception", "Endurance", "Charisma", "Intelligence", "Agility", "Luck"];

    public RecordDetailModel Build(
        uint formId, string? editorId, string? displayName,
        IReadOnlyList<DecodedNode> tree, BethesdaGame game, FormIdResolver resolver, RecordCollection? records)
    {
        var isFalloutSpecial = game is BethesdaGame.FalloutNewVegas or BethesdaGame.Fallout3;

        var sections = new List<RecordDetailSection>
        {
            RecordDetailHelpers.Section("Identity",
            [
                RecordDetailHelpers.Scalar("Form ID", $"0x{formId:X8}"),
                RecordDetailHelpers.Scalar("Editor ID", editorId ?? "(none)"),
                RecordDetailHelpers.Scalar("Name", displayName ?? "(none)"),
                RefEntry("Race", TopBySignature(tree, "RNAM"), resolver),
                RefEntry("Class", TopBySignature(tree, "CNAM"), resolver),
                RecordDetailHelpers.Scalar("Female", Female(tree)),
                RecordDetailHelpers.Scalar("Level", Level(tree))
            ]),
            RecordDetailHelpers.Section("Appearance",
            [
                RecordDetailHelpers.Link("Hair", RefValue(TopBySignature(tree, "HNAM")), resolver),
                RecordDetailHelpers.Scalar("Hair Color", NpcRecord.FormatHairColor(HairColor(tree))),
                RecordDetailHelpers.Link("Eyes", RefValue(TopBySignature(tree, "ENAM")), resolver),
                RecordDetailHelpers.Scalar("Height", NpcFloat(TopBySignature(tree, "NAM6"), 0.1f, 10.0f)?.ToString("F2")),
                RecordDetailHelpers.Scalar("Weight", NpcFloat(TopBySignature(tree, "NAM7"), 0.0f, 1000.0f)?.ToString("F1")),
                // Original Race / Face NPC / Race Preset are runtime/PDB-only (no subrecord) — null from an ESM.
                RecordDetailHelpers.Link("Original Race", null, resolver),
                RecordDetailHelpers.Link("Face NPC", null, resolver),
                RecordDetailHelpers.Scalar("Race Preset", null)
            ]),
            RecordDetailHelpers.Section("AI & Scripts",
            [
                RecordDetailHelpers.Link("Script", RefValue(TopBySignature(tree, "SCRI")), resolver),
                RecordDetailHelpers.Link("Death Item", RefValue(TopBySignature(tree, "INAM")), resolver),
                RecordDetailHelpers.Link("Voice Type", RefValue(TopBySignature(tree, "VTCK")), resolver),
                RecordDetailHelpers.Link("Template", RefValue(TopBySignature(tree, "TPLT")), resolver),
                RecordDetailHelpers.Link("Combat Style", RefValue(TopBySignature(tree, "ZNAM")), resolver)
            ]),
            RecordDetailHelpers.ListSection("Head Parts",
                RefList(TopByLabel(tree, "Head Parts")).Select(id => RecordDetailHelpers.ListLinkItem(id, resolver)).ToList()),
            RecordDetailHelpers.ListSection("SPECIAL", RecordDetailHelpers.BuildStatItems(
                SpecialNames, isFalloutSpecial ? Special(tree) : null)),
            RecordDetailHelpers.ListSection("Skills",
                RecordDetailHelpers.BuildSkillItems(isFalloutSpecial ? Skills(tree) : null, resolver)),
            RecordDetailHelpers.ListSection("Factions", Factions(tree, resolver)),
            RecordDetailHelpers.ListSection("Inventory", Inventory(tree, resolver)),
            RecordDetailHelpers.ListSection("AI Packages",
                RefList(TopByLabel(tree, "Packages")).Select(id => RecordDetailHelpers.ListLinkItem(id, resolver)).ToList())
        };

        return RecordDetailHelpers.Model("NPC_", formId, editorId, displayName, sections);
    }

    // A reference entry: a FormID Link when the node carries a FormID, else a string Scalar (Morrowind's
    // RNAM/CNAM are editor-id strings, not FormIDs).
    private static RecordDetailEntry RefEntry(string label, DecodedNode? node, FormIdResolver resolver) =>
        RefValue(node) is { } formId
            ? RecordDetailHelpers.Link(label, formId, resolver)
            : RecordDetailHelpers.Scalar(label, Str(node));

    // The FormID value of a present reference node (including 0), or null when the node is absent — so an
    // absent subrecord stays null (filtered) exactly like the typed model's uint? fields.
    private static uint? RefValue(DecodedNode? node) => node?.RawValue as uint?;

    private static IEnumerable<uint> RefList(DecodedNode? arrayNode) =>
        arrayNode?.Children.Select(c => c.RawValue as uint?).Where(v => v.HasValue).Select(v => v!.Value) ?? [];

    // ACBS Flags bit 0. The typed model only populates Stats when ACBS >= 24, in which case the union "Level"
    // tail child is >= 16 bytes; otherwise "Female" defaults to "No".
    private static string Female(IReadOnlyList<DecodedNode> tree)
    {
        var acbs = TopBySignature(tree, "ACBS");
        if (Bytes(ChildByLabel(acbs, "Level")) is not { Length: >= 16 })
        {
            return "No";
        }

        return ((Int(ChildByLabel(acbs, "Flags")) ?? 0) & 1) != 0 ? "Yes" : "No";
    }

    // ACBS Level — a UnionDef, so it lands in the raw tail child starting at ACBS offset 8 (S16).
    private static string Level(IReadOnlyList<DecodedNode> tree)
    {
        var levelBytes = Bytes(ChildByLabel(TopBySignature(tree, "ACBS"), "Level"));
        return levelBytes is { Length: >= 16 } && ReadS16(levelBytes, 0) is { } level
            ? level.ToString()
            : "(unknown)";
    }

    // HCLR is an R/G/B struct; repack to the 0x00BBGGRR uint NpcRecord.FormatHairColor expects.
    private static uint? HairColor(IReadOnlyList<DecodedNode> tree)
    {
        var hclr = TopBySignature(tree, "HCLR");
        if (hclr is null ||
            Int(ChildByLabel(hclr, "Red")) is not { } r ||
            Int(ChildByLabel(hclr, "Green")) is not { } g ||
            Int(ChildByLabel(hclr, "Blue")) is not { } b)
        {
            return null;
        }

        return (uint)((r & 0xFF) | ((g & 0xFF) << 8) | ((b & 0xFF) << 16));
    }

    // NAM6/NAM7 float with the typed clamp: NaN/Inf -> null; ~0 -> 1.0; in-range -> value, else null.
    private static float? NpcFloat(DecodedNode? node, float min, float max)
    {
        if (Float(node) is not { } value || float.IsNaN(value) || float.IsInfinity(value))
        {
            return null;
        }

        if (MathF.Abs(value) <= 0.0001f)
        {
            return 1.0f;
        }

        return value >= min && value <= max ? value : null;
    }

    // FNV/FO3 DATA: 7 SPECIAL bytes after Int32 base health, preserved as the "Attributes" raw tail. The
    // typed model only reads them when DATA == 11, i.e. exactly 7 attribute bytes.
    private static string[]? Special(IReadOnlyList<DecodedNode> tree)
    {
        var attrs = Bytes(ChildByLabel(TopBySignature(tree, "DATA"), "Attributes"));
        return attrs is { Length: 7 } ? attrs.Select(b => b.ToString()).ToArray() : null;
    }

    // FNV/FO3 DNAM: 14 skill values + 14 offsets (28 bytes), preserved whole as the "Skill Values" raw tail.
    private static byte[]? Skills(IReadOnlyList<DecodedNode> tree)
    {
        var dnam = Bytes(ChildByLabel(TopBySignature(tree, "DNAM"), "Skill Values"));
        return dnam is { Length: 28 } ? dnam[..14] : null;
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
            var faction = RefValue(ChildByLabel(elem, "Faction")) ?? 0;
            var rank = Int(ChildByLabel(elem, "Rank")) ?? 0;
            return new RecordDetailListItem
            {
                Label = resolver.GetBestNameWithRefChain(faction) ?? $"0x{faction:X8}",
                Value = $"Rank {rank}",
                LinkedFormId = faction
            };
        }).ToList();
    }

    // CNTO isn't expanded by the generator, so each inventory entry is a top-level raw node: FormID(4) + Count(4).
    private static List<RecordDetailListItem> Inventory(IReadOnlyList<DecodedNode> tree, FormIdResolver resolver)
    {
        var items = new List<RecordDetailListItem>();
        foreach (var node in AllTopBySignature(tree, "CNTO"))
        {
            var bytes = Bytes(node);
            if (ReadU32(bytes, 0) is not { } itemFormId)
            {
                continue;
            }

            var count = ReadS32(bytes, 4) ?? 0;
            items.Add(new RecordDetailListItem
            {
                Label = resolver.GetBestNameWithRefChain(itemFormId) ?? $"0x{itemFormId:X8}",
                Value = $"x{count}",
                LinkedFormId = itemFormId
            });
        }

        return items;
    }
}
