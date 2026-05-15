namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Quality diagnostic for a single RuntimeTerrainMesh's vertex data.
///     Used to determine whether terrain data is complete, partial, or corrupt.
/// </summary>
public record TerrainMeshDiagnostic
{
    public int CellX { get; init; }
    public int CellY { get; init; }
    public uint FormId { get; init; }

    /// <summary>Minimum runtime mesh X coordinate accepted by the grid detector.</summary>
    public float? MeshMinX { get; init; }

    /// <summary>Maximum runtime mesh X coordinate accepted by the grid detector.</summary>
    public float? MeshMaxX { get; init; }

    /// <summary>Minimum runtime mesh Y coordinate accepted by the grid detector.</summary>
    public float? MeshMinY { get; init; }

    /// <summary>Maximum runtime mesh Y coordinate accepted by the grid detector.</summary>
    public float? MeshMaxY { get; init; }

    /// <summary>Cell X inferred from runtime mesh world-coordinate bounds when plausible.</summary>
    public int? MeshInferredCellX { get; init; }

    /// <summary>Cell Y inferred from runtime mesh world-coordinate bounds when plausible.</summary>
    public int? MeshInferredCellY { get; init; }

    public float MinZ { get; init; }
    public float MaxZ { get; init; }
    public float ZRange { get; init; }
    public float ZStdDev { get; init; }
    public int UniqueZCount { get; init; }
    public int ZeroZCount { get; init; }

    /// <summary>Percentage of vertices sharing the single most common Z value (0-100).</summary>
    public float DominantZPercent { get; init; }

    /// <summary>Highest row index (0-32) that has meaningful Z variation.</summary>
    public int LastActiveRow { get; init; }

    /// <summary>Number of adjacent-row transitions with abrupt Z-range changes.</summary>
    public int RowDiscontinuities { get; init; }

    /// <summary>Number of vertices with garbage Z values (|Z| > 100k, NaN, or Infinity) before sanitization.</summary>
    public int GarbageZCount { get; init; }

    /// <summary>Percentage of vertices that were sanitized (replaced by interpolation), 0-100.</summary>
    public float SanitizedPercent { get; init; }

    /// <summary>Source used for LAND height emission, such as RuntimeMESH, ESM_VHGT, or None.</summary>
    public string HeightSource { get; init; } = string.Empty;

    /// <summary>Detected sparse terrain grid size before reconstruction.</summary>
    public int DetectedGridSize { get; init; }

    /// <summary>Estimated LOD level derived from the detected source grid.</summary>
    public int DetectedLodLevel { get; init; }

    /// <summary>Number of source samples accepted into the detected grid.</summary>
    public int SourceSampleCount { get; init; }

    /// <summary>Percentage of detected source grid cells covered by accepted samples, 0-100.</summary>
    public float SourceCoveragePercent { get; init; }

    /// <summary>Maximum absolute height error after VHGT encode/decode, in game units.</summary>
    public float EncodedRoundTripMaxError { get; init; }

    /// <summary>Whether runtime vertex colors were present with usable coverage.</summary>
    public bool HasRuntimeVertexColors { get; init; }

    /// <summary>Complete, Partial, Flat, FewPixels, or Corrupt.</summary>
    public required string Classification { get; init; }
}
