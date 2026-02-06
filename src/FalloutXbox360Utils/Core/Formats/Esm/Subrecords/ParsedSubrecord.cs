using System.Text;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Parsed subrecord.
/// </summary>
public record ParsedSubrecord
{
    public required string Signature { get; init; }
    public required byte[] Data { get; init; }

    public string DataAsString => Encoding.UTF8.GetString(Data).TrimEnd('\0');
    public uint DataAsFormId => Data.Length >= 4 ? BinaryUtils.ReadUInt32LE(Data) : 0;
    public float DataAsFloat => Data.Length >= 4 ? BinaryUtils.ReadFloatLE(Data) : 0f;
    public int DataAsInt32 => Data.Length >= 4 ? BinaryUtils.ReadInt32LE(Data) : 0;
}
