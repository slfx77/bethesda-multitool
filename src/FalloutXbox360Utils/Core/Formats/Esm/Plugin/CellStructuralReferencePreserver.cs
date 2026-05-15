using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Reporting;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin;

/// <summary>
///     Preserves room, portal, and occlusion marker references that are required for
///     interior cell structure but are often absent from runtime DMP captures.
/// </summary>
internal static class CellStructuralReferencePreserver
{
    private static readonly HashSet<string> StructuralCellRefSubrecords = new(StringComparer.Ordinal)
    {
        "XPOD", // room/portal connection
        "XOCP", // occlusion plane geometry
        "XORD", // linked occlusion planes
        "XMBO", // room/bound marker extents
        "XPRM", // primitive marker geometry
        "XNDP" // navigation door portal
    };

    private static readonly string[] StructuralBaseEditorIdNeedles =
    [
        "RoomMarker",
        "PortalMarker",
        "Occlusion",
        "MultiBound",
        "Culling"
    ];

    public static int PreserveMissing(
        IReadOnlyList<ParsedMainRecord> masterRefs,
        ISet<uint> dmpFormIdsInCell,
        IReadOnlyDictionary<uint, ParsedMainRecord> pcRecordsByFormId,
        List<byte[]> persistentRecords,
        List<byte[]> temporaryRecords,
        ConversionPipelineStats stats)
    {
        var preserved = 0;
        foreach (var masterRef in masterRefs)
        {
            if (dmpFormIdsInCell.Contains(masterRef.Header.FormId)
                || !IsStructuralCellRef(masterRef, pcRecordsByFormId))
            {
                continue;
            }

            var bytes = CellGrupBuilder.ReconstructRecordBytes(masterRef);
            if ((masterRef.Header.Flags & 0x00000400u) != 0)
            {
                persistentRecords.Add(bytes);
            }
            else
            {
                temporaryRecords.Add(bytes);
            }

            preserved++;
            stats.IncrementEmitted(masterRef.Header.Signature);
        }

        return preserved;
    }

    public static bool IsStructuralCellRef(
        ParsedMainRecord masterRef,
        IReadOnlyDictionary<uint, ParsedMainRecord> pcRecordsByFormId)
    {
        if (masterRef.Header.Signature != "REFR")
        {
            return false;
        }

        if (masterRef.Subrecords.Any(sub => StructuralCellRefSubrecords.Contains(sub.Signature)))
        {
            return true;
        }

        var baseFormId = ReadNameFormId(masterRef);
        if (!baseFormId.HasValue
            || !pcRecordsByFormId.TryGetValue(baseFormId.Value, out var baseRecord)
            || string.IsNullOrEmpty(baseRecord.EditorId))
        {
            return false;
        }

        return StructuralBaseEditorIdNeedles.Any(needle =>
            baseRecord.EditorId!.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    public static uint? ReadNameFormId(ParsedMainRecord record)
    {
        var name = record.Subrecords.FirstOrDefault(sub => sub.Signature == "NAME" && sub.Data.Length >= 4);
        return name is null
            ? null
            : BinaryPrimitives.ReadUInt32LittleEndian(name.Data);
    }
}
