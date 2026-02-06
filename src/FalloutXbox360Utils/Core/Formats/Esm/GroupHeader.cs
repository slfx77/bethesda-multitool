namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Group header (24 bytes).
///     Layout: "GRUP"(4) + GroupSize(4) + Label(4) + GroupType(4) + Stamp(4) + Unknown(4)
/// </summary>
public record GroupHeader
{
    public uint GroupSize { get; init; }
    public byte[] Label { get; init; } = [];
    public int GroupType { get; init; }
    public uint Stamp { get; init; }
    public uint Unknown { get; init; }

    public string LabelAsSignature => EsmRecordTypes.SignatureToString(Label);
    public int LabelAsInt => Label.Length >= 4 ? BitConverter.ToInt32(Label, 0) : 0;
    public uint LabelAsUInt => Label.Length >= 4 ? BitConverter.ToUInt32(Label, 0) : 0;
}
