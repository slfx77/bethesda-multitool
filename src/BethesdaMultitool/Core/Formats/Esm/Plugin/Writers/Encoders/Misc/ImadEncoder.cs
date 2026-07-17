using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes an Image Space Modifier (IMAD) record. Animated post-processing layer applied
///     on top of an <see cref="ImageSpaceRecord" />. New records are serialized from the
///     ordered captured subrecord stream: the frame-table signatures include control-byte
///     names and may repeat, so rebuilding them from a signature-keyed projection loses data.
/// </summary>
public sealed class ImadEncoder : IRecordEncoder
{
    public string RecordType => "IMAD";

    public Type ModelType => typeof(ImageSpaceModifierRecord);

    internal static EncodedRecord EncodeNew(
        ImageSpaceModifierRecord imad,
        IReadOnlySet<uint>? validFormIds = null,
        IReadOnlyDictionary<uint, uint>? remapTable = null)
    {
        return EncodeOrdered(imad, (signature, sourceFormId) =>
        {
            if (sourceFormId == 0)
            {
                return 0u;
            }

            return FormIdReferenceResolver.Resolve(sourceFormId, validFormIds, remapTable);
        });
    }

    /// <summary>
    ///     Serialize a structurally complete captured stream in source order. The resolver
    ///     returns a final PC FormID for RDSD/RDSI, or null to drop a dangling sound link.
    /// </summary>
    internal static EncodedRecord EncodeOrdered(
        ImageSpaceModifierRecord imad,
        Func<string, uint, uint?> resolveSound)
    {
        ArgumentNullException.ThrowIfNull(imad);
        ArgumentNullException.ThrowIfNull(resolveSound);

        var subs = new List<EncodedSubrecord>(imad.OrderedSubrecords.Count);
        var warnings = new List<string>();
        if (!ImageSpaceModifierCaptureValidator.IsCompleteNewCapture(imad, out var reason))
        {
            warnings.Add(
                $"New IMAD 0x{imad.FormId:X8} suppressed — incomplete captured stream: {reason}.");
            return new EncodedRecord { Subrecords = subs, Warnings = warnings };
        }

        foreach (var raw in imad.OrderedSubrecords)
        {
            if (raw.Signature is "RDSD" or "RDSI")
            {
                var sourceFormId = imad.IsBigEndian
                    ? BinaryPrimitives.ReadUInt32BigEndian(raw.Data)
                    : BinaryPrimitives.ReadUInt32LittleEndian(raw.Data);
                var resolved = resolveSound(raw.Signature, sourceFormId);
                if (resolved is null)
                {
                    warnings.Add(
                        $"New IMAD 0x{imad.FormId:X8} dropped dangling {raw.Signature} "
                        + $"sound 0x{sourceFormId:X8}.");
                    continue;
                }

                var bytes = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, resolved.Value);
                subs.Add(new EncodedSubrecord(raw.Signature, bytes));
                continue;
            }

            var converted = imad.IsBigEndian
                ? SubrecordSchemaProcessor.ConvertWithSchema(raw.Signature, raw.Data, "IMAD")
                : raw.Data.ToArray();
            if (converted is null)
            {
                throw new InvalidOperationException(
                    $"Complete IMAD 0x{imad.FormId:X8} has no conversion schema for {raw.Signature}.");
            }

            subs.Add(new EncodedSubrecord(raw.Signature, converted));
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
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), data.AnimatableFlag != 0 ? 1u : 0u);
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
