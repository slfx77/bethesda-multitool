namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

/// <summary>
///     Tree (TREE) record. Trees use SpeedTree-derived geometry baked from procedural seeds
///     in SNAM; the LOD billboard and leaf-animation parameters live in CNAM/BNAM. Missing
///     this encoder strips trees from converted ESPs, leaving exterior worldspaces visually
///     deforested.
/// </summary>
public record TreeRecord
{
    public uint FormId { get; init; }

    public string? EditorId { get; init; }

    public ObjectBounds? Bounds { get; init; }

    /// <summary>Trunk model path (MODL subrecord).</summary>
    public string? ModelPath { get; init; }

    /// <summary>Model texture data (MODT subrecord, opaque binary blob — unparsed).</summary>
    public byte[]? ModelTextureData { get; init; }

    /// <summary>Leaf texture path (ICON subrecord).</summary>
    public string? IconPath { get; init; }

    /// <summary>SpeedTree seeds (SNAM subrecord, variable-length uint32 array).</summary>
    public IReadOnlyList<uint>? Seeds { get; init; }

    /// <summary>Tree animation/dimming parameters (CNAM subrecord, 8 floats / 32 bytes).</summary>
    public TreeData? Data { get; init; }

    /// <summary>Billboard width × height (BNAM subrecord, 2 floats / 8 bytes).</summary>
    public TreeBillboardSize? BillboardSize { get; init; }

    public long Offset { get; init; }

    public bool IsBigEndian { get; init; }
}

/// <summary>
///     TREE CNAM payload (32 bytes, 8 LE floats). Matches the schema at
///     <c>SubrecordCellAndMiscSchemas (CNAM, TREE, 32)</c>.
/// </summary>
public record TreeData
{
    public float LeafCurvature { get; init; }
    public float MinLeafAngle { get; init; }
    public float MaxLeafAngle { get; init; }
    public float BranchDimmingValue { get; init; }
    public float LeafDimmingValue { get; init; }
    public float ShadowRadius { get; init; }
    public float RockSpeed { get; init; }
    public float RustleSpeed { get; init; }
}

/// <summary>
///     TREE BNAM payload (8 bytes, 2 LE floats). Matches the schema at
///     <c>SubrecordCellAndMiscSchemas (BNAM, TREE, 8)</c>.
/// </summary>
public record TreeBillboardSize
{
    public float Width { get; init; }
    public float Height { get; init; }
}
