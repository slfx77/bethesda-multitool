namespace EsmAnalyzer.Core;

/// <summary>
///     Shared constants for ESM analysis operations.
/// </summary>
public static class EsmConstants
{
    // =====================================================================
    // LAND / Heightmap Constants
    // =====================================================================

    /// <summary>
    ///     Grid size for LAND vertex data (33×33 vertices per cell).
    /// </summary>
    public const int LandGridSize = 33;

    /// <summary>
    ///     Total points in a LAND grid (33×33 = 1089).
    /// </summary>
    public const int LandGridArea = LandGridSize * LandGridSize;

    /// <summary>
    ///     World units per cell (4096 units).
    /// </summary>
    public const int CellWorldUnits = 4096;

    /// <summary>
    ///     World units per LAND grid vertex.
    /// </summary>
    public const float UnitsPerVertex = (float)CellWorldUnits / LandGridSize;

    // =====================================================================
    // VHGT Subrecord Constants
    // =====================================================================

    /// <summary>
    ///     Size of VHGT subrecord data (4-byte base + 1089 deltas + 3 padding).
    /// </summary>
    public const int VhgtDataSize = 1096;

    /// <summary>
    ///     Size of VHGT base height field (float).
    /// </summary>
    public const int VhgtBaseHeightSize = 4;

    // =====================================================================
    // Record/Subrecord Constants
    // =====================================================================

    /// <summary>
    ///     Main record header size (20 bytes for records, 24 for GRUPs).
    /// </summary>
    public const int MainRecordHeaderSize = 20;

    /// <summary>
    ///     GRUP header size.
    /// </summary>
    public const int GrupHeaderSize = 24;

    /// <summary>
    ///     Subrecord header size (4-byte signature + 2-byte size).
    /// </summary>
    public const int SubrecordHeaderSize = 6;

    // =====================================================================
    // Compression
    // =====================================================================

    /// <summary>
    ///     Record flag indicating the record data is zlib-compressed.
    /// </summary>
    public const uint CompressedFlag = 0x00040000;
}

/// <summary>
///     Simple RGBA color struct (replaces ImageSharp dependency).
/// </summary>
public readonly record struct Rgba32(byte R, byte G, byte B, byte A)
{
    public static Rgba32 Black => new(0, 0, 0, 255);
    public static Rgba32 White => new(255, 255, 255, 255);
    public static Rgba32 Transparent => new(0, 0, 0, 0);
}
