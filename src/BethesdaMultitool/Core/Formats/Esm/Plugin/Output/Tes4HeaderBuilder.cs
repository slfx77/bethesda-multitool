using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Output;

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
    /// <param name="numRecords">
    ///     HEDR record count: every main record AND GRUP header in the file, excluding TES4
    ///     itself. Measured against shipped FalloutNV.esm — its HEDR reads 542,016, which is
    ///     exactly 465,016 non-TES4 records + 77,000 GRUP headers. Callers should derive this
    ///     from the assembled bytes (<see cref="PluginEmissionCensus" />), never from
    ///     per-write-site counters.
    /// </param>
    /// <param name="nextObjectId">
    ///     The next free local FormID. For plugins that only emit overrides, leave at a safe
    ///     stub value (e.g., 0x800) since GECK won't allocate from this until the user adds new records.
    /// </param>
    public static byte[] Build(
        PluginBuildOptions options,
        uint numRecords,
        uint nextObjectId,
        IReadOnlyCollection<uint>? overriddenCellChildFormIds = null)
    {
        // Build subrecord stream first so we know its size.
        using var subrecordStream = new MemoryStream();
        using (var subrecordWriter = new BinaryWriter(subrecordStream, Encoding.Latin1, true))
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

            // ONAM: required in ESM-flagged files — the FO3/FNV runtime consults this list
            // to apply overrides of cell-child records (REFR/ACHR/ACRE/PGRE/PMIS/LAND/NAVM);
            // without it those overrides are mishandled at load (why xEdit's "ESMify" adds
            // ONAM). Tied to the presence of cell-child overrides, NOT to navmesh augmentation
            // (they were coupled until the ESM flag was decoupled below). Sorted ascending,
            // XXXX-extended when the array exceeds 64KB.
            if (overriddenCellChildFormIds is { Count: > 0 })
            {
                WriteOnam(subrecordWriter, overriddenCellChildFormIds);
            }
        }

        var subrecordBytes = subrecordStream.ToArray();

        using var recordStream = new MemoryStream();
        var header = new MainRecordHeader
        {
            Signature = "TES4",
            DataSize = (uint)subrecordBytes.Length,
            // ESM/master flag (0x00000001) is set whenever the plugin overrides master
            // cell-child records (REFR/ACHR/ACRE/LAND/NAVM/…). The FO3/FNV runtime only
            // honours cell-child overrides — and their ONAM list — from ESM-flagged masters;
            // a plain ESP's interior overrides route through a fragile deferred path. The
            // flag is independent of navmesh augmentation: the program needs it for NPC
            // skin-tone + map-marker edits regardless of navmesh, and (proven in-game) the
            // eager-init AV class traces to dangling bases, not the flag itself.
            // No overrides → plain ESP, no flag.
            Flags = overriddenCellChildFormIds is { Count: > 0 } ? 0x00000001u : 0u,
            FormId = 0,
            Timestamp = 0,
            // VcsInfo = header offset 20 = the engine's form-version slot (retail TES4
            // headers carry 15 there); see MainRecordHeader remarks for the naming trap.
            VcsInfo = RecordVersion,
            Version = 0
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

    private static void WriteOnam(BinaryWriter writer, IReadOnlyCollection<uint> formIds)
    {
        var sorted = formIds.ToArray();
        Array.Sort(sorted);
        var data = new byte[sorted.Length * 4];
        for (var i = 0; i < sorted.Length; i++)
        {
            SubrecordEncoder.WriteUInt32(data, i * 4, sorted[i]);
        }

        SubrecordEncoder.WriteSubrecord(writer, "ONAM", data);
    }

    private static void WriteMasterDataPlaceholder(BinaryWriter writer)
    {
        // 8 bytes of zeros — vestigial in FO3/FNV; only TES3 used this for the master's file size.
        Span<byte> data = stackalloc byte[8];
        SubrecordEncoder.WriteSubrecord(writer, "DATA", data);
    }
}
