using BethesdaMultitool.Core.Formats.Esm.Enums;

namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;

/// <summary>
///     Parsed Consumable (ALCH) record.
///     Aggregates data from ALCH main record header, DATA, ENIT, and repeated
///     EFID/EFIT/CTDA effect groups.
/// </summary>
public record ConsumableRecord
{
    /// <summary>FormID of the consumable record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    // DATA subrecord (4 bytes)
    /// <summary>Weight in units.</summary>
    public float Weight { get; init; }

    // ENIT subrecord (20 bytes)
    /// <summary>Base value in caps.</summary>
    public uint Value { get; init; }

    /// <summary>ENIT flags (No Auto-Calc, Food Item, Medicine).</summary>
    public uint Flags { get; init; }

    /// <summary>Withdrawal-effect FormID (SPEL) — ENIT bytes 8-11.</summary>
    public uint? WithdrawalEffectFormId { get; init; }

    /// <summary>Addiction chance (0.0-1.0) — ENIT bytes 12-15.</summary>
    public float AddictionChance { get; init; }

    /// <summary>Consume sound FormID (SOUN) — ENIT bytes 16-19.</summary>
    public uint? ConsumeSoundFormId { get; init; }

    /// <summary>Effects and their conditions (EFID + EFIT + CTDA* groups).</summary>
    public List<EnchantmentEffect> Effects { get; init; } = [];

    /// <summary>Script FormID (SCRI subrecord).</summary>
    public uint? ScriptFormId { get; init; }

    /// <summary>Pickup sound FormID (YNAM subrecord — SOUN).</summary>
    public uint? PickupSoundFormId { get; init; }

    /// <summary>Drop sound FormID (ZNAM subrecord — SOUN).</summary>
    public uint? DropSoundFormId { get; init; }

    /// <summary>Equipment type (ETYP subrecord — int32 enum).</summary>
    public EquipmentType EquipmentType { get; init; } = EquipmentType.None;

    /// <summary>Model file path (MODL subrecord).</summary>
    public string? ModelPath { get; init; }

    /// <summary>Object bounds (OBND subrecord).</summary>
    public ObjectBounds? Bounds { get; init; }

    /// <summary>Inventory image path from ICON subrecord.</summary>
    public string? IconPath { get; init; }

    /// <summary>Message icon path from MICO subrecord.</summary>
    public string? MessageIconPath { get; init; }

    /// <summary>Texture hash data from MODT subrecord (opaque bytes — engine validates).</summary>
    public byte[]? TextureHashData { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
