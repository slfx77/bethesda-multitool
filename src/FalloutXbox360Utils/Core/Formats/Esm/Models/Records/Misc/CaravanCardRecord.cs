namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

/// <summary>
///     Caravan Card (CCRD) record. A single playing card in the New Vegas
///     caravan minigame. PDB struct: TESCaravanCard (204 bytes, FormType 0x73).
/// </summary>
public record CaravanCardRecord
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }
    public string? ModelPath { get; init; }

    /// <summary>Inventory display value (iValue at +140 / DATA subrecord).</summary>
    public uint Value { get; init; }

    /// <summary>Bound script FormID (pFormScript pointer at +148).</summary>
    public uint ScriptFormId { get; init; }

    /// <summary>Pickup sound FormID (pPickupSound pointer at +160 / YNAM).</summary>
    public uint PickupSoundFormId { get; init; }

    /// <summary>Putdown sound FormID (pPutdownSound pointer at +164 / ZNAM).</summary>
    public uint PutdownSoundFormId { get; init; }

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }
}
