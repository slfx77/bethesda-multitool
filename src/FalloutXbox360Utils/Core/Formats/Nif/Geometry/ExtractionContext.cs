namespace FalloutXbox360Utils.Core.Formats.Nif.Geometry;

/// <summary>
///     Context for extraction operations, grouping common parameters.
/// </summary>
/// <param name="Data">Source byte array containing packed geometry.</param>
/// <param name="RawDataOffset">Offset to raw vertex data within the block.</param>
/// <param name="NumVertices">Number of vertices to extract.</param>
/// <param name="Stride">Bytes per vertex (36, 40, or 48).</param>
/// <param name="IsBigEndian">Whether source data is big-endian.</param>
internal readonly record struct ExtractionContext(
    byte[] Data,
    int RawDataOffset,
    int NumVertices,
    int Stride,
    bool IsBigEndian)
{
    /// <summary>Whether this is a skinned mesh (stride 48).</summary>
    public bool IsSkinned => Stride == 48;
}
