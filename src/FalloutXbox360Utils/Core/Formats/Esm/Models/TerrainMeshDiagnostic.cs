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

    /// <summary>Complete, Partial, Flat, or FewPixels.</summary>
    public required string Classification { get; init; }
}
