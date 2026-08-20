using BethesdaMultitool.Core.Formats.Esm.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Utils;
using static BethesdaMultitool.Core.Formats.Esm.Presentation.Profiles.DecodedTreeReader;

namespace BethesdaMultitool.Core.Formats.Esm.Presentation.Profiles;

/// <summary>
///     The ARMO presentation profile — reproduces <see cref="RecordDetailBuilders.BuildArmor" />'s three
///     sections (Identity / Stats / Presentation) from a schema-decoded tree. For FNV this is byte-for-byte
///     equivalent to the typed builder (proven by <c>ArmorProfileParityTests</c>); for the other games it
///     produces the same sectioned shape from their (differing) tree — the FNV-specific stat <em>values</em>
///     (DT/DR/biped flags/equipment type) are best-effort elsewhere, like NPC's Level/SPECIAL.
///     <para>
///         Two schema quirks: the generator can't descend ARMO's <c>Male → Biped Model</c> group, so MODL
///         arrives as a top-level <em>raw</em> node (read its bytes as the model path). DNAM's DR/DT and the
///         DATA/BMDT structs decode as ordinary struct children, read and clamped exactly as the typed
///         <see cref="Parsing.Handlers.ArmorDefenseData" /> + <see cref="GameStatNormalizer" /> do.
///     </para>
/// </summary>
internal sealed class ArmorProfile : IRecordProfile
{
    public string RecordType => "ARMO";

    public RecordDetailModel Build(
        uint formId, string? editorId, string? displayName,
        IReadOnlyList<DecodedNode> tree, BethesdaGame game, FormIdResolver resolver, RecordCollection? records)
    {
        var (damageResistance, damageThreshold) = DefenseStats(tree);
        var data = TopBySignature(tree, "DATA");
        var bmdt = TopBySignature(tree, "BMDT");

        var sections = new List<RecordDetailSection>
        {
            RecordDetailHelpers.Section("Identity",
            [
                RecordDetailHelpers.Scalar("Form ID", $"0x{formId:X8}"),
                RecordDetailHelpers.Scalar("Editor ID", editorId ?? "(none)"),
                RecordDetailHelpers.Scalar("Name", displayName ?? "(none)"),
                RecordDetailHelpers.Scalar("Equipment Type", EquipmentTypeName(tree))
            ]),
            RecordDetailHelpers.Section("Stats",
            [
                RecordDetailHelpers.Scalar("Damage Threshold", damageThreshold.ToString("F1")),
                RecordDetailHelpers.Scalar("Damage Resistance", damageResistance.ToString()),
                RecordDetailHelpers.Scalar("Weight", (Float(ChildByLabel(data, "Weight")) ?? 0f).ToString("F1")),
                RecordDetailHelpers.Scalar("Value", ((int)(Int(ChildByLabel(data, "Value")) ?? 0)).ToString()),
                RecordDetailHelpers.Scalar("Health", ((int)(Int(ChildByLabel(data, "Health")) ?? 0)).ToString()),
                RecordDetailHelpers.Scalar("Biped Flags",
                    $"0x{(uint)(Int(ChildByLabel(bmdt, "Biped Flags")) ?? 0):X8}"),
                RecordDetailHelpers.Scalar("General Flags",
                    $"0x{(byte)(Int(ChildByLabel(bmdt, "General Flags")) ?? 0):X2}")
            ]),
            RecordDetailHelpers.Section("Presentation",
            [
                RecordDetailHelpers.Scalar("Model", ModelPath(tree)),
                RecordDetailHelpers.Scalar("Bounds", RecordDetailHelpers.FormatBounds(Bounds(tree)))
            ])
        };

        return RecordDetailHelpers.Model("ARMO", formId, editorId, displayName, sections);
    }

    // DNAM: DR (Int16 @0) + DT (float @4), each clamped by the same domain guard the typed handler applies.
    private static (int DamageResistance, float DamageThreshold) DefenseStats(IReadOnlyList<DecodedNode> tree)
    {
        var dnam = TopBySignature(tree, "DNAM");
        var dr = GameStatNormalizer.ArmorDamageResistance((int)(Int(ChildByLabel(dnam, "DR")) ?? 0));
        var dt = GameStatNormalizer.ArmorDamageThreshold(Float(ChildByLabel(dnam, "DT")) ?? 0f);
        return (dr, dt);
    }

    // ETYP int → EquipmentType (the typed handler accepts only -1..13), formatted via the record's name map.
    private static string EquipmentTypeName(IReadOnlyList<DecodedNode> tree)
    {
        var etyp = Int(TopBySignature(tree, "ETYP"));
        var type = etyp is >= -1 and <= 13 ? (EquipmentType)(int)etyp.Value : EquipmentType.None;
        return new ArmorRecord { EquipmentType = type }.EquipmentTypeName;
    }

    // The generator can't descend ARMO's Male→Biped Model group, so MODL stays a top-level raw node whose
    // bytes are the null-terminated model path (read exactly as the typed handler does).
    private static string? ModelPath(IReadOnlyList<DecodedNode> tree)
    {
        var modl = Bytes(TopBySignature(tree, "MODL"));
        return modl is { Length: > 0 } ? EsmStringUtils.ReadNullTermString(modl) : null;
    }

    private static ObjectBounds? Bounds(IReadOnlyList<DecodedNode> tree)
    {
        var obnd = TopBySignature(tree, "OBND");
        if (obnd is null)
        {
            return null;
        }

        return new ObjectBounds
        {
            X1 = (short)(Int(ChildByLabel(obnd, "X1")) ?? 0),
            Y1 = (short)(Int(ChildByLabel(obnd, "Y1")) ?? 0),
            Z1 = (short)(Int(ChildByLabel(obnd, "Z1")) ?? 0),
            X2 = (short)(Int(ChildByLabel(obnd, "X2")) ?? 0),
            Y2 = (short)(Int(ChildByLabel(obnd, "Y2")) ?? 0),
            Z2 = (short)(Int(ChildByLabel(obnd, "Z2")) ?? 0)
        };
    }
}
