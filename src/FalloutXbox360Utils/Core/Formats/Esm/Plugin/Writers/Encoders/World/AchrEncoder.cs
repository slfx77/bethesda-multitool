using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

/// <summary>
///     Encodes a placed-actor record (ACHR) — same subrecord shape as REFR.
/// </summary>
public sealed class AchrEncoder : IRecordEncoder
{
    public string RecordType => "ACHR";
    public Type ModelType => typeof(PlacedReference);

    public EncodedRecord Encode(object model)
    {
        return RefrEncoder.EncodePlacedReference((PlacedReference)model);
    }
}
