using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.World;

/// <summary>
///     Encodes a placed-creature record (ACRE) — same subrecord shape as REFR/ACHR.
/// </summary>
public sealed class AcreEncoder : IRecordEncoder
{
    public string RecordType => "ACRE";
    public Type ModelType => typeof(PlacedReference);

    /// <summary>Produces override subrecords for an existing ACRE (a placed creature) from its runtime-mutable fields.</summary>
    public EncodedRecord Encode(object model)
    {
        return RefrEncoder.EncodePlacedReference((PlacedReference)model);
    }
}
