using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

/// <summary>
///     Encodes a placed-creature record (ACRE) — same subrecord shape as REFR/ACHR.
/// </summary>
public sealed class AcreEncoder : IRecordEncoder
{
    public string RecordType => "ACRE";
    public Type ModelType => typeof(PlacedReference);

    public EncodedRecord Encode(object model)
    {
        return RefrEncoder.EncodePlacedReference((PlacedReference)model);
    }
}
