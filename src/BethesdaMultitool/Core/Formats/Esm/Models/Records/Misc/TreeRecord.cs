namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

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
///     TREE CNAM payload — 32 bytes: seven floats and one SIGNED INT32.
///     <para>
///         Field order and types are the engine's <c>OBJ_TREE</c> struct, read straight out of
///         <c>Fallout_Release_MemDebug.pdb</c> (LF_FIELDLIST 0x0002dbf8, LF_STRUCTURE 0x0002dbf9,
///         Size = 32), and they match xEdit's <c>wbStruct(CNAM)</c> one-for-one:
///     </para>
///     <list type="table">
///         <item>
///             <description><c>+0  float  fCurveScalar</c>       → <see cref="LeafCurvature" /></description>
///         </item>
///         <item>
///             <description><c>+4  float  fMinimumLeafAngle</c>  → <see cref="MinLeafAngle" /></description>
///         </item>
///         <item>
///             <description><c>+8  float  fMaximumLeafAngle</c>  → <see cref="MaxLeafAngle" /></description>
///         </item>
///         <item>
///             <description><c>+12 float  fBranchDimming</c>     → <see cref="BranchDimmingValue" /></description>
///         </item>
///         <item>
///             <description><c>+16 float  fLeafDimming</c>       → <see cref="LeafDimmingValue" /></description>
///         </item>
///         <item>
///             <description><c>+20 int32  iCanopyShadowRadius</c> → <see cref="ShadowRadius" /></description>
///         </item>
///         <item>
///             <description><c>+24 float  fRockSpeed</c>         → <see cref="RockSpeed" /></description>
///         </item>
///         <item>
///             <description><c>+28 float  fRustleSpeed</c>       → <see cref="RustleSpeed" /></description>
///         </item>
///     </list>
/// </summary>
public record TreeData
{
    public float LeafCurvature { get; init; }
    public float MinLeafAngle { get; init; }
    public float MaxLeafAngle { get; init; }
    public float BranchDimmingValue { get; init; }
    public float LeafDimmingValue { get; init; }

    /// <summary>
    ///     Canopy shadow radius in cells. INTEGER, not float — the PDB types this
    ///     <c>iCanopyShadowRadius</c> as <c>T_INT4</c> and xEdit as <c>itS32</c>. Observed
    ///     values are 128 / 512 / 64; decoding those bytes as a float yields ~7e-43 denormals,
    ///     which is how the original float typing went unnoticed.
    /// </summary>
    public int ShadowRadius { get; init; }

    public float RockSpeed { get; init; }
    public float RustleSpeed { get; init; }
}

/// <summary>
///     TREE BNAM payload — 8 bytes, two floats. The engine's <c>NiPoint2 BillboardSize</c>
///     (PDB LF_CLASS 0x000116bc, Size = 8, members <c>x</c>@0 and <c>y</c>@4), which xEdit
///     names 'Billboard Dimensions' (Width, Height).
/// </summary>
public record TreeBillboardSize
{
    public float Width { get; init; }
    public float Height { get; init; }
}
