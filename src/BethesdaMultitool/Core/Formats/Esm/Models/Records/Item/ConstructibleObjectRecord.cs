using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;

/// <summary>
///     Forensic Constructible Object (COBJ) capture model. The FNV PDB identifies a
///     196-byte <c>BGSConstructibleObject</c>, but current FNV xEdit defines its on-disk
///     record as a MISC-like base object rather than the modern crafting-recipe layout.
///     Recipe-shaped fields below are retained for existing probes and synthetic fixtures;
///     this hybrid is deliberately not registered for production FNV emission.
/// </summary>
public record ConstructibleObjectRecord
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }
    public ObjectBounds? Bounds { get; init; }

    /// <summary>Model path (MODL subrecord).</summary>
    public string? ModelPath { get; init; }

    /// <summary>Texture-hash blob from MODT (opaque byte-array passthrough).</summary>
    public byte[]? TextureHashData { get; init; }

    /// <summary>Recipe-shaped CNTO data retained for forensic/non-FNV fixtures; absent from the FNV COBJ schema.</summary>
    public List<InventoryItem> Ingredients { get; init; } = [];

    /// <summary>Recipe-shaped CTDA data retained for forensic/non-FNV fixtures; absent from the FNV COBJ schema.</summary>
    public List<DialogueCondition> Conditions { get; init; } = [];

    /// <summary>Runtime pCreatedItem recovery or modern-layout CNAM; FNV xEdit defines no COBJ CNAM.</summary>
    public uint? CreatedItemFormId { get; init; }

    /// <summary>Modern-layout workbench keyword; FNV xEdit defines no COBJ BNAM.</summary>
    public uint? WorkbenchKeywordFormId { get; init; }

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }
}
