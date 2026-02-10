namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Reconstructed Leveled List record (LVLI/LVLN/LVLC).
/// </summary>
public record LeveledListRecord
{
    /// <summary>FormID of the leveled list.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>List type (LVLI=Item, LVLN=NPC, LVLC=Creature).</summary>
    public string ListType { get; init; } = "LVLI";

    /// <summary>LVLD - Chance none (0-100).</summary>
    public byte ChanceNone { get; init; }

    /// <summary>LVLF - Leveled list flags.</summary>
    public byte Flags { get; init; }

    /// <summary>LVLG - Global variable FormID for chance calculation.</summary>
    public uint? GlobalFormId { get; init; }

    /// <summary>LVLO entries - items/NPCs/creatures in the list.</summary>
    public List<LeveledEntry> Entries { get; init; } = [];

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }

    /// <summary>Human-readable flags description.</summary>
    public string FlagsDescription
    {
        get
        {
            var parts = new List<string>();
            if ((Flags & 0x01) != 0) parts.Add("CalcFromAllLevels");
            if ((Flags & 0x02) != 0) parts.Add("CalcForEachItem");
            if ((Flags & 0x04) != 0) parts.Add("UseAll");
            return parts.Count > 0 ? string.Join(", ", parts) : "None";
        }
    }
}
