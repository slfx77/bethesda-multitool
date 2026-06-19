using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.World;

/// <summary>
///     Encodes a placed-actor record (ACHR) — same subrecord shape as REFR.
/// </summary>
public sealed class AchrEncoder : IRecordEncoder
{
    public string RecordType => "ACHR";
    public Type ModelType => typeof(PlacedReference);

    /// <summary>Produces override subrecords for an existing ACHR (a placed actor) from its runtime-mutable fields.</summary>
    public EncodedRecord Encode(object model)
    {
        return RefrEncoder.EncodePlacedReference((PlacedReference)model);
    }
}
