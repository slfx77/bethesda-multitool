using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Shared helpers for emitting subrecords in new-record encoder paths. Each helper
///     produces an <see cref="EncodedSubrecord" /> with the appropriate byte payload —
///     avoids duplicating the same one-liner across every type-specific encoder.
/// </summary>
internal static class NewRecordSubrecords
{
    /// <summary>
    ///     Emit a null-terminated Latin-1 string subrecord (EDID, FULL, MODL, DESC, ...).
    /// </summary>
    public static EncodedSubrecord EncodeStringSubrecord(string signature, string value)
    {
        var byteCount = Encoding.Latin1.GetByteCount(value);
        var buffer = new byte[byteCount + 1];
        Encoding.Latin1.GetBytes(value, buffer);
        // Final byte already 0 (null terminator).
        return new EncodedSubrecord(signature, buffer);
    }

    /// <summary>
    ///     Emit null-terminated Windows-1252 game text. Use this for recovered authored text
    ///     such as SCTX, whose decoded characters must round-trip bytes 0x80-0x9F.
    /// </summary>
    public static EncodedSubrecord EncodeGameTextSubrecord(string signature, string value)
    {
        var encoded = EsmStringUtils.EncodeGameText(value);
        var buffer = new byte[encoded.Length + 1];
        encoded.CopyTo(buffer, 0);
        // Final byte already 0 (null terminator).
        return new EncodedSubrecord(signature, buffer);
    }

    /// <summary>Emit a 4-byte little-endian uint32 subrecord (FNAM, RNAM, ...).</summary>
    public static EncodedSubrecord EncodeUInt32Subrecord(string signature, uint value)
    {
        var bytes = new byte[4];
        SubrecordEncoder.WriteUInt32(bytes, 0, value);
        return new EncodedSubrecord(signature, bytes);
    }

    /// <summary>Emit a 4-byte little-endian int32 subrecord (DATA for int GMSTs, ...).</summary>
    public static EncodedSubrecord EncodeInt32Subrecord(string signature, int value)
    {
        var bytes = new byte[4];
        SubrecordEncoder.WriteInt32(bytes, 0, value);
        return new EncodedSubrecord(signature, bytes);
    }

    /// <summary>Emit a 4-byte little-endian float subrecord (FLTV, XCLW, ...).</summary>
    public static EncodedSubrecord EncodeFloatSubrecord(string signature, float value)
    {
        var bytes = new byte[4];
        SubrecordEncoder.WriteFloat(bytes, 0, value);
        return new EncodedSubrecord(signature, bytes);
    }

    /// <summary>Emit a 4-byte FormID subrecord (NAME, XOWN, XEZN, ...).</summary>
    public static EncodedSubrecord EncodeFormIdSubrecord(string signature, uint formId)
    {
        var bytes = new byte[4];
        SubrecordEncoder.WriteFormId(bytes, 0, formId);
        return new EncodedSubrecord(signature, bytes);
    }

    /// <summary>
    ///     Emit a packed array of 4-byte FormIDs as one subrecord (IDLM's IDLA, DOBJ's DATA, ...).
    ///     xEdit models these as a repeating FormID with the element count carried by a sibling
    ///     subrecord, so the caller owns keeping the two consistent.
    /// </summary>
    public static EncodedSubrecord EncodeFormIdArraySubrecord(string signature, IReadOnlyList<uint> formIds)
    {
        ArgumentNullException.ThrowIfNull(formIds);

        var bytes = new byte[formIds.Count * 4];
        for (var i = 0; i < formIds.Count; i++)
        {
            SubrecordEncoder.WriteFormId(bytes, i * 4, formIds[i]);
        }

        return new EncodedSubrecord(signature, bytes);
    }

    /// <summary>Emit a single-byte subrecord (FNAM for GLOB, DATA for CELL flags, ...).</summary>
    public static EncodedSubrecord EncodeByteSubrecord(string signature, byte value)
    {
        return new EncodedSubrecord(signature, [value]);
    }

    /// <summary>
    ///     Emit an opaque byte-array subrecord (MODT/MO2T/MO3T texture hashes, ...).
    ///     The schema marks these as unstructured byte arrays — no endian swap, no parsing.
    ///     The engine validates the bytes; we pass them through as-is.
    /// </summary>
    public static EncodedSubrecord EncodeByteArraySubrecord(string signature, byte[] data)
    {
        return new EncodedSubrecord(signature, data);
    }

    /// <summary>
    ///     Emit OBND — 12 bytes, 6 int16 values: X1, Y1, Z1, X2, Y2, Z2 (min/max bounds).
    ///     Per fopdoc, this is the canonical object-bounds layout for most record types.
    /// </summary>
    public static EncodedSubrecord EncodeObndSubrecord(ObjectBounds bounds)
    {
        var data = new byte[12];
        SubrecordEncoder.WriteInt16(data, 0, bounds.X1);
        SubrecordEncoder.WriteInt16(data, 2, bounds.Y1);
        SubrecordEncoder.WriteInt16(data, 4, bounds.Z1);
        SubrecordEncoder.WriteInt16(data, 6, bounds.X2);
        SubrecordEncoder.WriteInt16(data, 8, bounds.Y2);
        SubrecordEncoder.WriteInt16(data, 10, bounds.Z2);
        return new EncodedSubrecord("OBND", data);
    }

    /// <summary>
    ///     Append MODS for a record whose model carries alternate textures, if any were walked.
    ///     Call this immediately after the MODL block — xEdit's model group is
    ///     MODL / MODB / MODT / MODS / MODD, and MODS out of order is a parse error, not a warning.
    ///     <para>
    ///         The runtime member is <c>TESModelTextureSwap.TextureSwapList</c> on all 28 types that
    ///         carry one, so the key is the same everywhere.
    ///     </para>
    /// </summary>
    public static void AppendAlternateTextures(List<EncodedSubrecord> subs, GenericEsmRecord record)
    {
        ArgumentNullException.ThrowIfNull(subs);

        if (GenericRecordFields.TryAlternateTextures(
                record, "MODS", "TESModelTextureSwap.TextureSwapList") is { } swaps)
        {
            subs.Add(EncodeAlternateTexturesSubrecord("MODS", swaps));
        }
    }

    /// <summary>
    ///     Append the DEST/DSTD/DSTF destruction block, if one was walked. The runtime member is
    ///     <c>BGSDestructibleObjectForm.pData</c> on all 26 types that carry one.
    /// </summary>
    public static void AppendDestruction(List<EncodedSubrecord> subs, GenericEsmRecord record)
    {
        ArgumentNullException.ThrowIfNull(subs);

        if (GenericRecordFields.TryDestruction(
                record, "DEST", "BGSDestructibleObjectForm.pData") is { } destruction)
        {
            subs.AddRange(EncodeDestructionBlock(destruction));
        }
    }

    /// <summary>
    ///     Emit MODS — the alternate-texture ("texture swap") array. Wire format is the inverse of
    ///     <c>AlternateTextureParser.Parse</c>: a <c>u32</c> count, then per entry a length-prefixed
    ///     3D name, the TXST FormID, and the signed 3D index. Written little-endian, since the
    ///     emitted plugin is a PC plugin.
    /// </summary>
    public static EncodedSubrecord EncodeAlternateTexturesSubrecord(
        string signature, IReadOnlyList<AlternateTextureEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var size = 4;
        foreach (var entry in entries)
        {
            size += 4 + Encoding.Latin1.GetByteCount(entry.ShapeName) + 8;
        }

        var data = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)entries.Count);
        var pos = 4;

        foreach (var entry in entries)
        {
            // The name is NOT null-terminated here — its length prefix is the delimiter, and
            // adding a terminator would put a stray byte inside the next entry.
            var nameLength = Encoding.Latin1.GetBytes(entry.ShapeName, data.AsSpan(pos + 4));
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(pos), (uint)nameLength);
            pos += 4 + nameLength;

            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(pos), entry.TextureSetFormId);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(pos + 4), entry.Index);
            pos += 8;
        }

        return new EncodedSubrecord(signature, data);
    }

    /// <summary>
    ///     Emit one LSCR <c>LNAM</c> location — 12 bytes: direct FormID, indirect worldspace
    ///     FormID, packed exterior grid. LNAM repeats rather than carrying an array, so callers
    ///     emit one subrecord per location.
    /// </summary>
    public static EncodedSubrecord EncodeLoadScreenLocationSubrecord(LoadScreenLocationEntry entry)
    {
        var data = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(data, entry.DirectFormId);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), entry.IndirectWorldspaceFormId);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), entry.GridKey);
        return new EncodedSubrecord("LNAM", data);
    }

    /// <summary>
    ///     Emit a destruction block: the 8-byte <c>DEST</c> header followed by one
    ///     <c>DSTD</c> / optional <c>DMDL</c> / <c>DSTF</c> group per stage.
    ///     <para>
    ///         DEST's count is written from the stages actually emitted, not from the captured
    ///         <c>cNumStages</c>. The engine sizes its stage array from that count and fills it from
    ///         the DSTD blocks that follow, so a count larger than the number of blocks leaves slots
    ///         unpopulated — the same discipline IDLM's IDLC follows against its IDLA.
    ///     </para>
    /// </summary>
    public static List<EncodedSubrecord> EncodeDestructionBlock(DestructionData destruction)
    {
        ArgumentNullException.ThrowIfNull(destruction);

        // DEST's count and DSTD's stage index are both u8, so 255 stages is the hard ceiling of the
        // format. Clamp once and emit from the clamped bound, so the header count and the number of
        // DSTD blocks can never disagree however the reader's own cap is tuned.
        var stageCount = Math.Min(destruction.Stages.Count, byte.MaxValue);
        var subs = new List<EncodedSubrecord>(1 + stageCount * 3);

        var header = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header, destruction.Health);
        header[4] = (byte)stageCount;
        header[5] = destruction.Flags;
        subs.Add(new EncodedSubrecord("DEST", header));

        for (var index = 0; index < stageCount; index++)
        {
            var stage = destruction.Stages[index];

            var body = new byte[20];
            body[0] = stage.HealthPercent;
            body[1] = (byte)index;
            body[2] = stage.DamageStage;
            body[3] = stage.Flags;
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), stage.SelfDamagePerSecond);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), stage.ExplosionFormId);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), stage.DebrisFormId);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(16), stage.DebrisCount);
            subs.Add(new EncodedSubrecord("DSTD", body));

            if (!string.IsNullOrEmpty(stage.ReplacementModel))
            {
                subs.Add(EncodeStringSubrecord("DMDL", stage.ReplacementModel));
            }

            // DSTF is the zero-length terminator that closes each stage group.
            subs.Add(new EncodedSubrecord("DSTF", []));
        }

        return subs;
    }
}
