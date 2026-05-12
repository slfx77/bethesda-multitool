using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;

namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;

/// <summary>
///     Parsed Constructible Object (COBJ) record. FNV crafting blueprint used by workbenches —
///     ties ingredient list (CNTO*) and crafting conditions (CTDA*) to a created item (CNAM).
///     PDB struct: BGSConstructibleObject (196 bytes).
///     fopdoc canonical subrecord order:
///         EDID, OBND?, FULL?, MODL?, MODT?, COCT, CNTO*, CTDA*, CNAM, BNAM?.
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

    /// <summary>Crafting ingredients — each CNTO is parsed as an InventoryItem (FormID + Count).</summary>
    public List<InventoryItem> Ingredients { get; init; } = [];

    /// <summary>Crafting conditions (CTDA* with optional CIS1/CIS2 string parameters).</summary>
    public List<DialogueCondition> Conditions { get; init; } = [];

    /// <summary>FormID of the item produced by crafting (CNAM subrecord).</summary>
    public uint? CreatedItemFormId { get; init; }

    /// <summary>FormID of the workbench keyword that filters this recipe (BNAM subrecord, optional).</summary>
    public uint? WorkbenchKeywordFormId { get; init; }

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }
}
