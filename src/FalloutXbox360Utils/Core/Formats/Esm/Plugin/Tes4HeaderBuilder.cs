using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin;

/// <summary>
///     Synthesizes a PC plugin TES4 record. The TES4 lists the master file (FalloutNV.esm),
///     plugin metadata (author, description), and the HEDR record-count / next-object-id summary.
/// </summary>
public static class Tes4HeaderBuilder
{
    /// <summary>Fixed FNV TES4 version field — 1.34f, as observed in shipped FalloutNV.esm.</summary>
    public const float HedrVersion = 1.34f;

    /// <summary>FNV record header version — 0x000F (15).</summary>
    public const ushort RecordVersion = 0x000F;

    /// <summary>
    ///     Build the TES4 record bytes (24-byte header + subrecord stream).
    /// </summary>
    /// <param name="options">Plugin options (master file, metadata).</param>
    /// <param name="numRecords">Total number of records the plugin will contain (excluding TES4).</param>
    /// <param name="nextObjectId">
    ///     The next free local FormID. For plugins that only emit overrides, leave at a safe
    ///     stub value (e.g., 0x800) since GECK won't allocate from this until the user adds new records.
    /// </param>
    public static byte[] Build(PluginBuildOptions options, uint numRecords, uint nextObjectId)
    {
        // Build subrecord stream first so we know its size.
        using var subrecordStream = new MemoryStream();
        using (var subrecordWriter = new BinaryWriter(subrecordStream, System.Text.Encoding.Latin1, true))
        {
            WriteHedr(subrecordWriter, numRecords, nextObjectId);

            if (!string.IsNullOrEmpty(options.Author))
            {
                SubrecordEncoder.WriteStringSubrecord(subrecordWriter, "CNAM", options.Author);
            }

            if (!string.IsNullOrEmpty(options.Description))
            {
                SubrecordEncoder.WriteStringSubrecord(subrecordWriter, "SNAM", options.Description);
            }

            // Master dependency: MAST (filename) + DATA (8 bytes, must be zero in FO3/FNV).
            // Per fopdoc: "Always 0, probably vestigial. In TES3, the file size of the previous
            // master was recorded here." Earlier versions of this code wrote the actual file
            // size which is non-canonical and triggers an FNVEdit warning.
            SubrecordEncoder.WriteStringSubrecord(subrecordWriter, "MAST", options.MasterFileName);
            WriteMasterDataPlaceholder(subrecordWriter);
        }

        var subrecordBytes = subrecordStream.ToArray();

        using var recordStream = new MemoryStream();
        var header = new MainRecordHeader
        {
            Signature = "TES4",
            DataSize = (uint)subrecordBytes.Length,
            // ESP plugin flag set: 0x00000000. The master/ESM bit (0x00000001) stays clear.
            Flags = 0,
            FormId = 0,
            Timestamp = 0,
            VcsInfo = 0,
            Version = RecordVersion
        };
        RecordHeaderProcessor.WriteRecordHeader(recordStream, header);
        recordStream.Write(subrecordBytes);

        return recordStream.ToArray();
    }

    private static void WriteHedr(BinaryWriter writer, uint numRecords, uint nextObjectId)
    {
        Span<byte> data = stackalloc byte[12];
        SubrecordEncoder.WriteFloat(data, 0, HedrVersion);
        SubrecordEncoder.WriteUInt32(data, 4, numRecords);
        SubrecordEncoder.WriteUInt32(data, 8, nextObjectId);
        SubrecordEncoder.WriteSubrecord(writer, "HEDR", data);
    }

    private static void WriteMasterDataPlaceholder(BinaryWriter writer)
    {
        // 8 bytes of zeros — vestigial in FO3/FNV; only TES3 used this for the master's file size.
        Span<byte> data = stackalloc byte[8];
        SubrecordEncoder.WriteSubrecord(writer, "DATA", data);
    }
}
