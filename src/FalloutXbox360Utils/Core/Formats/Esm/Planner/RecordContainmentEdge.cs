namespace FalloutXbox360Utils.Core.Formats.Esm.Planner;

/// <summary>
///     The relationship a parent record holds over the current record in ESM containment
///     terms. Phase E uses these to topologically sort <see cref="EmitPlan.Records" />.
/// </summary>
public enum ContainmentRelationship
{
    /// <summary>INFO record contained in a DIAL's topic GRUP.</summary>
    DialogueTopic,

    /// <summary>REFR / ACHR / ACRE contained in a CELL's children GRUP.</summary>
    CellPlacement,

    /// <summary>NAVM / LAND contained in a CELL's children GRUP (Temporary).</summary>
    CellTerrain,

    /// <summary>CELL contained in a WRLD's children GRUP.</summary>
    WorldspaceCell,

    /// <summary>NAVI referencing a NAVM via NVMI / NVPP entries.</summary>
    NavmeshIsland,

    /// <summary>
    ///     A teleport reference (XTEL) on a placed REFR pointing at a destination door,
    ///     paired with the synthetic-door rescue path that may emit a matching REFR.
    /// </summary>
    TeleportRescue,
}

/// <summary>
///     One containment edge: this record's parent has this FormID. The plan uses these
///     to enforce emission order (DIAL before INFO, WRLD before CELL, etc.) and to cascade
///     <see cref="RecordDisposition.Skip" /> when the parent is dropped.
/// </summary>
public sealed record RecordContainmentEdge
{
    /// <summary>FormID of the parent record (DIAL, CELL, WRLD, NAVI, REFR).</summary>
    public required uint ParentFormId { get; init; }

    /// <summary>Nature of the containment.</summary>
    public required ContainmentRelationship Relationship { get; init; }
}
