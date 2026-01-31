namespace FalloutXbox360Utils.Core.Formats.EsmRecord;

/// <summary>
///     Group header (24 bytes).
/// </summary>
public record GroupHeader
{
    public uint GroupSize { get; init; }
    public byte[] Label { get; init; } = [];
    public int GroupType { get; init; }
    public uint Stamp { get; init; }

    public string LabelAsSignature => EsmRecordTypes.SignatureToString(Label);
    public int LabelAsInt => Label.Length >= 4 ? BitConverter.ToInt32(Label, 0) : 0;
}
