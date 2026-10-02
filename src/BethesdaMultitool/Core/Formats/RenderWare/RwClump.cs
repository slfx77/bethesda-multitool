namespace BethesdaMultitool.Core.Formats.RenderWare;

/// <summary>
///     A decoded source CLUMP, not a renderable scene. Coordinates and authored flags are unchanged.
///     Opaque data references a private copy of the input. Diagnostics identify semantics this reader
///     deliberately does not implement; successful structural decoding is not rendering acceptance.
/// </summary>
internal sealed record RwClump(
    uint LibraryId,
    IReadOnlyList<RwClumpFrame> Frames,
    IReadOnlyList<RwClumpGeometry> Geometries,
    IReadOnlyList<RwClumpAtomic> Atomics,
    IReadOnlyList<RwClumpAttachment> Attachments,
    IReadOnlyList<RwClumpChunk> Extensions,
    IReadOnlyList<RwClumpDiagnostic> Diagnostics,
    ReadOnlyMemory<byte> Source);
