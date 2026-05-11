namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;

/// <summary>
///     Encodes a typed DMP-derived record model into PC little-endian subrecord bytes.
///     Each implementation handles exactly one ESM record type (e.g., "WEAP", "GLOB").
/// </summary>
public interface IRecordEncoder
{
    /// <summary>The 4-character ESM record type signature this encoder handles.</summary>
    string RecordType { get; }

    /// <summary>The CLR type of model this encoder accepts.</summary>
    Type ModelType { get; }

    /// <summary>
    ///     Produces the encoded subrecord payloads in canonical order.
    /// </summary>
    /// <param name="model">An instance of <see cref="ModelType" />.</param>
    /// <returns>Encoded subrecords plus warnings.</returns>
    EncodedRecord Encode(object model);
}
