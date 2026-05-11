using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;

namespace FalloutXbox360Utils.Core.Formats.Esm.Merge;

/// <summary>
///     Bundle of deletion-flag override records, partitioned by which sub-GRUP they belong in.
/// </summary>
public sealed record DeletedRefBundle
{
    public required List<byte[]> Persistent { get; init; }
    public required List<byte[]> Temporary { get; init; }
}

/// <summary>
///     For cells in <see cref="CellMergeMode.HasTemporary" /> mode, computes the set difference
///     between the master ESM's refs in a cell and the DMP's refs in the same cell, and emits a
///     deletion-flag override (record header flag <c>0x00000020</c>) for each ref that's in the
///     master but not in the DMP. This implements the "wipeout" semantics from the user's spec.
///
///     Per xEdit convention, a deleted override has a minimal subrecord stream — just the EDID
///     copied verbatim from the master record. The compressed flag is cleared on output.
/// </summary>
public static class DeletedRefSynthesizer
{
    private const uint DeletedFlag = 0x00000020;
    private const uint CompressedFlag = 0x00040000;
    private const uint PersistentFlag = 0x00000400;

    /// <summary>
    ///     Build a <see cref="DeletedRefBundle" /> for the given cell.
    /// </summary>
    /// <param name="masterRefsInCell">All master ESM REFR/ACHR/ACRE records belonging to this cell.</param>
    /// <param name="dmpFormIdsInCell">Set of FormIDs the DMP has for refs in this cell.</param>
    public static DeletedRefBundle Synthesize(
        IEnumerable<ParsedMainRecord> masterRefsInCell,
        ISet<uint> dmpFormIdsInCell)
    {
        var persistent = new List<byte[]>();
        var temporary = new List<byte[]>();

        foreach (var masterRef in masterRefsInCell)
        {
            if (dmpFormIdsInCell.Contains(masterRef.Header.FormId))
            {
                continue;
            }

            var bytes = BuildDeletedOverride(masterRef);
            if ((masterRef.Header.Flags & PersistentFlag) != 0)
            {
                persistent.Add(bytes);
            }
            else
            {
                temporary.Add(bytes);
            }
        }

        return new DeletedRefBundle
        {
            Persistent = persistent,
            Temporary = temporary
        };
    }

    /// <summary>
    ///     Build a single deleted-flag override record from its master source.
    /// </summary>
    private static byte[] BuildDeletedOverride(ParsedMainRecord masterRef)
    {
        // Subrecord stream: just the master's EDID, if present. xEdit shows deleted overrides
        // with their EDID for human identification — a totally-empty payload also works but is
        // less helpful in the editor.
        using var subStream = new MemoryStream();
        var edid = masterRef.Subrecords.FirstOrDefault(s => s.Signature == "EDID");
        if (edid is not null)
        {
            using var subWriter = new BinaryWriter(subStream, Encoding.Latin1, true);
            SubrecordEncoder.WriteSubrecord(subWriter, "EDID", edid.Data);
        }

        var subBytes = subStream.ToArray();

        var header = masterRef.Header with
        {
            DataSize = (uint)subBytes.Length,
            Flags = (masterRef.Header.Flags & ~CompressedFlag) | DeletedFlag,
            Version = Tes4HeaderBuilder.RecordVersion
        };

        using var stream = new MemoryStream();
        RecordHeaderProcessor.WriteRecordHeader(stream, header);
        stream.Write(subBytes);
        return stream.ToArray();
    }
}
