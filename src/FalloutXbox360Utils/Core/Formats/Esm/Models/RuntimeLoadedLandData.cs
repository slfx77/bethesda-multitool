using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Runtime LoadedLandData structure containing cell coordinates for heightmap stitching.
///     Read from TESObjectLAND.pLoadedData pointer at runtime offset +56.
/// </summary>
public record RuntimeLoadedLandData
{
    /// <summary>FormID of the parent LAND record.</summary>
    public uint FormId { get; init; }

    /// <summary>FormID of the parent CELL record when the runtime TESObjectLAND pointer is available.</summary>
    public uint? ParentCellFormId { get; init; }

    /// <summary>Cell grid X coordinate.</summary>
    public int CellX { get; init; }

    /// <summary>Cell grid Y coordinate.</summary>
    public int CellY { get; init; }

    /// <summary>Base elevation for the cell.</summary>
    public float BaseHeight { get; init; }

    /// <summary>Minimum terrain height in this cell (from HeightExtents at offset +24).</summary>
    public float? MinHeight { get; init; }

    /// <summary>Maximum terrain height in this cell (from HeightExtents at offset +28).</summary>
    public float? MaxHeight { get; init; }

    /// <summary>File offset of the TESObjectLAND runtime struct.</summary>
    public long LandOffset { get; init; }

    /// <summary>File offset of the LoadedLandData struct.</summary>
    public long LoadedDataOffset { get; init; }

    /// <summary>Terrain mesh extracted from heap pointers (ppVertices, ppNormals, ppColorsA).</summary>
    public RuntimeTerrainMesh? TerrainMesh { get; init; }

    /// <summary>LAND visual data reconstructed from runtime LoadedLandData texture pointers.</summary>
    public LandVisualData? VisualData { get; init; }

    /// <summary>Runtime TESLandTexture records referenced by reconstructed LAND texture layers.</summary>
    public IReadOnlyList<LandscapeTextureRecord> RuntimeLandTextures { get; init; } = [];

    /// <summary>Runtime BGSTextureSet records referenced by reconstructed LAND texture records.</summary>
    public IReadOnlyList<TextureSetRecord> RuntimeTextureSets { get; init; } = [];

    /// <summary>PDB-derived pointer diagnostics for the full LoadedLandData structure.</summary>
    public RuntimeLoadedLandDiagnostics? Diagnostics { get; init; }
}

/// <summary>
///     Diagnostic snapshot of TESObjectLAND::LoadedLandData pointer fields.
///     These values are intentionally observational; terrain reconstruction does not depend on them.
/// </summary>
public record RuntimeLoadedLandDiagnostics
{
    public RuntimePointerDiagnostic Mesh { get; init; } = RuntimePointerDiagnostic.Empty;

    public RuntimePointerDiagnostic Vertices { get; init; } = RuntimePointerDiagnostic.Empty;

    public IReadOnlyList<RuntimePointerDiagnostic> VertexArrays { get; init; } = [];

    public RuntimePointerDiagnostic Normals { get; init; } = RuntimePointerDiagnostic.Empty;

    public IReadOnlyList<RuntimePointerDiagnostic> NormalArrays { get; init; } = [];

    public RuntimePointerDiagnostic Colors { get; init; } = RuntimePointerDiagnostic.Empty;

    public IReadOnlyList<RuntimePointerDiagnostic> ColorArrays { get; init; } = [];

    public RuntimePointerDiagnostic NormalsSet { get; init; } = RuntimePointerDiagnostic.Empty;

    public RuntimePointerDiagnostic Border { get; init; } = RuntimePointerDiagnostic.Empty;

    public RuntimePointerDiagnostic MoppCode { get; init; } = RuntimePointerDiagnostic.Empty;

    public RuntimePointerDiagnostic LandRigidBody { get; init; } = RuntimePointerDiagnostic.Empty;

    public IReadOnlyList<RuntimeLandTexturePointerDiagnostic> DefaultQuadTextures { get; init; } = [];

    public IReadOnlyList<RuntimeLandTextureArrayDiagnostic> QuadTextureArrays { get; init; } = [];

    public IReadOnlyList<RuntimePercentArrayDiagnostic> PercentArrays { get; init; } = [];

    public IReadOnlyList<uint> GrassMapWords { get; init; } = [];
}

public record RuntimePointerDiagnostic
{
    public static RuntimePointerDiagnostic Empty { get; } = new();

    public uint Pointer { get; init; }

    public long? FileOffset { get; init; }

    public uint? DereferencedPointer { get; init; }

    public long? DereferencedFileOffset { get; init; }

    public bool IsPresent => Pointer != 0;

    public bool IsMapped => FileOffset.HasValue;

    public bool DereferencedIsMapped => DereferencedFileOffset.HasValue;
}

public record RuntimeLandTexturePointerDiagnostic
{
    public int Quadrant { get; init; }

    public RuntimePointerDiagnostic Pointer { get; init; } = RuntimePointerDiagnostic.Empty;

    public uint? TextureFormId { get; init; }
}

public record RuntimeLandTextureArrayDiagnostic
{
    public int Quadrant { get; init; }

    public RuntimePointerDiagnostic Pointer { get; init; } = RuntimePointerDiagnostic.Empty;

    public int SampledPointerCount { get; init; }

    public int ResolvedTextureCount { get; init; }

    public IReadOnlyList<uint> TextureFormIds { get; init; } = [];
}

public record RuntimePercentArrayDiagnostic
{
    public int Quadrant { get; init; }

    public RuntimePointerDiagnostic Pointer { get; init; } = RuntimePointerDiagnostic.Empty;

    public int SampledCount { get; init; }

    public int NormalFloatCount { get; init; }

    public int UnitRangeCount { get; init; }

    public int NonZeroUnitRangeCount { get; init; }

    public float? MinValue { get; init; }

    public float? MaxValue { get; init; }
}
