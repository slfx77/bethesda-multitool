using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;

using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;
namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;

/// <summary>
///     Decoder for the 28-byte CTDA condition subrecord, shared across record types that
///     carry conditions (INFO, TERM, QUST, COBJ, ...). Mirrors
///     <see cref="FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.InfoEncoder" />'s
///     BuildCtdaSubrecord exactly: Type(1) + pad(3) + ComparisonValue(f32) +
///     FunctionIndex(u16) + pad(2) + Parameter1(u32) + Parameter2(u32) + RunOn(u32) +
///     Reference(u32).
/// </summary>
internal static class CtdaParser
{
    internal static DialogueCondition Decode(ReadOnlySpan<byte> data, bool bigEndian)
    {
        return new DialogueCondition
        {
            Type = data[0],
            ComparisonValue = bigEndian
                ? BinaryPrimitives.ReadSingleBigEndian(data[4..])
                : BinaryPrimitives.ReadSingleLittleEndian(data[4..]),
            FunctionIndex = bigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data[8..])
                : BinaryPrimitives.ReadUInt16LittleEndian(data[8..]),
            Parameter1 = RecordParserContext.ReadFormId(data[12..16], bigEndian),
            Parameter2 = bigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(data[16..])
                : BinaryPrimitives.ReadUInt32LittleEndian(data[16..]),
            RunOn = bigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(data[20..])
                : BinaryPrimitives.ReadUInt32LittleEndian(data[20..]),
            Reference = RecordParserContext.ReadFormId(data[24..28], bigEndian)
        };
    }
}
