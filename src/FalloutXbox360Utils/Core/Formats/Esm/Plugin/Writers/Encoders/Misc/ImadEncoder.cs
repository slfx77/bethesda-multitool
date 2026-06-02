using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes an Image Space Modifier (IMAD) record. Animated post-processing layer applied
///     on top of an <see cref="ImageSpaceRecord" />. Missing the encoder strips proto-only
///     IMADs and causes engine to load an undefined post-processing slot.
///     fopdoc canonical order: EDID, DNAM(244B), [frame-table subrecords — out of scope].
/// </summary>
public sealed class ImadEncoder : IRecordEncoder
{
    public string RecordType => "IMAD";

    public Type ModelType => typeof(ImageSpaceModifierRecord);

    internal static EncodedRecord EncodeNew(ImageSpaceModifierRecord imad)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(imad.EditorId))
        {
            warnings.Add($"New IMAD 0x{imad.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", imad.EditorId ?? string.Empty));

        if (imad.Data is not null)
        {
            subs.Add(new EncodedSubrecord("DNAM", EncodeDnam(imad.Data)));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    /// <summary>
    ///     IMAD DNAM payload (244 bytes, little-endian per PC ESM format).
    ///     Bytes 0..3: AnimatableFlag (uint32 LE). Bytes 4..7: Duration (float LE).
    ///     Bytes 8..243: 59 × 4-byte values (mixed uint32/float per fopdoc).
    /// </summary>
    internal static byte[] EncodeDnam(ImageSpaceModifierData data)
    {
        const int DnamSize = 244;
        var bytes = new byte[DnamSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), data.AnimatableFlag);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4, 4), data.Duration);

        // Write remaining 59 × 4-byte slots from the raw payload. Trailing slots default
        // to zero when the model provides fewer entries; extra entries are clipped to
        // the 244-byte canonical size.
        var maxSlots = (DnamSize - 8) / 4;
        var slotsToWrite = Math.Min(data.RawPayload.Count, maxSlots);
        for (var i = 0; i < slotsToWrite; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8 + i * 4, 4), data.RawPayload[i]);
        }

        return bytes;
    }
}
