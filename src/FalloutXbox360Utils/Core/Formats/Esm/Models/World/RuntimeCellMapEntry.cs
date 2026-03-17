namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     A cell entry extracted from a TESWorldSpace's runtime pCellMap hash table.
///     Maps grid coordinates to a real TESObjectCELL FormID.
/// </summary>
public record RuntimeCellMapEntry
{
    /// <summary>FormID of the TESObjectCELL.</summary>
    public uint CellFormId { get; init; }

    /// <summary>Virtual address of the TESObjectCELL runtime struct.</summary>
    public uint? CellPointer { get; init; }

    /// <summary>Grid X coordinate (decoded from NiTMap key).</summary>
    public int GridX { get; init; }

    /// <summary>Grid Y coordinate (decoded from NiTMap key).</summary>
    public int GridY { get; init; }

    /// <summary>Whether this is an interior cell (cCellFlags bit 0).</summary>
    public bool IsInterior { get; init; }

    /// <summary>Whether this is the worldspace's persistent cell.</summary>
    public bool IsPersistent { get; init; }

    /// <summary>FormID of the parent worldspace (from pWorldSpace pointer).</summary>
    public uint? WorldspaceFormId { get; init; }

    /// <summary>FormID of the associated LAND record (from pCellLand pointer).</summary>
    public uint? LandFormId { get; init; }

    /// <summary>
    ///     FormIDs of placed references currently linked from TESObjectCELL.listReferences.
    ///     This is runtime membership data, not carved REFR subrecord ownership.
    /// </summary>
    public List<uint> ReferenceFormIds { get; init; } = [];
}
