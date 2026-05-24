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
    ///     Produces the encoded subrecord payloads in canonical override order, for the
    ///     case where a DMP record overrides a master-ESM record. Returns an empty
    ///     <see cref="EncodedRecord" /> to signal "no override — preserve master verbatim";
    ///     the <see cref="Pipeline.PluginBuilder" /> override loop skips records with no
    ///     subrecords and falls through to ESM passthrough.
    /// </summary>
    /// <param name="model">An instance of <see cref="ModelType" />.</param>
    /// <returns>Encoded subrecords plus warnings.</returns>
    /// <remarks>
    ///     The default implementation returns an empty record — encoders that don't have
    ///     any runtime-mutable fields worth overriding (most types: STAT, CLAS, EYES,
    ///     etc.) can omit the method entirely. Only encoders that produce real override
    ///     subrecords (FACT flags, NPC stats, REFR positions, GMST values, CELL/INFO/
    ///     QUST runtime deltas) need to provide an explicit implementation.
    /// </remarks>
    EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }
}
