namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     One channel of a measured packed layout: the semantic it carries, the stored (Type, Unit Size) pair it must
///     declare, its byte offset inside a vertex, how its bytes are decoded and, when the decoding rule was carried over
///     from another layout rather than measured on this one, the evidence that says so.
/// </summary>
/// <param name="Kind">What the channel carries.</param>
/// <param name="Type">The NiAGDDataStream Type the file must declare (16 half4, 14 half2, 28 4-byte, 3 float3).</param>
/// <param name="UnitSize">The NiAGDDataStream Unit Size the file must declare.</param>
/// <param name="Offset">The channel's Block Offset: its byte offset inside one vertex of the stride.</param>
/// <param name="Encoding">How the channel's bytes are decoded.</param>
/// <param name="Inference">
///     Null when the channel's decoding was measured on this layout; otherwise the text quoting where the rule was
///     carried over from and why the measurement could not separate it (only L6's vertex color carries one).
/// </param>
internal sealed record NifPackedStream(
    NifPackedStreamKind Kind,
    uint Type,
    uint UnitSize,
    int Offset,
    NifPackedStreamEncoding Encoding,
    string? Inference = null);
