using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;

/// <summary>
///     Final type-aware XTEL guard for NEW refs. The generic encoder validates only FormID
///     existence; an existing REFR whose NAME is a STAT is not a legal teleport destination.
/// </summary>
internal static class PlacedRefTeleportSanitizer
{
    public static List<EncodedSubrecord> Sanitize(
        IReadOnlyList<EncodedSubrecord> subrecords,
        CellChildEncodeContext context)
    {
        var result = new List<EncodedSubrecord>(subrecords.Count);
        foreach (var subrecord in subrecords)
        {
            if (subrecord.Signature != "XTEL" || subrecord.Bytes.Length < 4)
            {
                result.Add(subrecord);
                continue;
            }

            var target = BinaryPrimitives.ReadUInt32LittleEndian(subrecord.Bytes.AsSpan(0, 4));
            if (context.IsLiveDoorReference(target))
            {
                result.Add(subrecord);
                continue;
            }

            context.Stats?.IncrementDropReason("refr.xtel-target-not-door");
        }

        return result;
    }
}
