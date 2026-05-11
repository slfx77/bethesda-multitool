namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;

/// <summary>
///     A single encoded subrecord — signature plus PC little-endian bytes (without the 6-byte
///     subrecord header). The merge engine treats this as the unit of override/retain.
/// </summary>
public sealed record EncodedSubrecord(string Signature, byte[] Bytes);

/// <summary>
///     The encoded form of a single record produced by an <see cref="IRecordEncoder" />.
/// </summary>
public sealed record EncodedRecord
{
    /// <summary>
    ///     Encoder-defined canonical order of subrecords. The merge engine uses this order
    ///     when emitting DMP-only subrecords (those not present in the source ESM record).
    /// </summary>
    public required IReadOnlyList<EncodedSubrecord> Subrecords { get; init; }

    /// <summary>Non-fatal warnings produced during encoding.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }
}
