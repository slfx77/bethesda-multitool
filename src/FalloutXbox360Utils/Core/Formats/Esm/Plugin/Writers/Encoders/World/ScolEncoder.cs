using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

/// <summary>
///     Encodes a <see cref="StaticCollectionRecord" /> (SCOL) as PC little-endian subrecord
///     bytes. Override path is a no-op — master ESM bytes are retained verbatim because the
///     DMP doesn't capture SCOL deltas today. <see cref="EncodeNew" /> emits the full canonical
///     subrecord stream: EDID + OBND? + MODL? + MODT? + (ONAM + DATA)* per part.
/// </summary>
public sealed class ScolEncoder : IRecordEncoder
{
    public string RecordType => "SCOL";
    public Type ModelType => typeof(StaticCollectionRecord);

    /// <summary>
    ///     Encode a new SCOL record from scratch. Parts whose <see cref="StaticCollectionPart.OnamFormId" />
    ///     is unreachable in the output (neither in the master ESM nor among newly-emitted STATs)
    ///     are dropped with a warning; if zero parts survive validation, returns an empty
    ///     subrecord list so the PluginBuilder short-circuit drops the record entirely rather
    ///     than emitting a bare EDID stub.
    /// </summary>
    internal static EncodedRecord EncodeNew(
        StaticCollectionRecord scol,
        IReadOnlySet<uint> masterFormIds,
        IReadOnlySet<uint> emittedNewStats)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(scol.EditorId))
        {
            warnings.Add($"New SCOL 0x{scol.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", scol.EditorId ?? string.Empty));

        if (scol.Bounds is not null)
        {
            subs.Add(NewRecordSubrecords.EncodeObndSubrecord(scol.Bounds));
        }

        if (!string.IsNullOrEmpty(scol.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", scol.ModelPath));
        }

        if (scol.TextureHashData is { Length: > 0 } modt)
        {
            subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord("MODT", modt));
        }

        var validParts = 0;
        foreach (var part in scol.Parts)
        {
            if (part.OnamFormId == 0
                || (!masterFormIds.Contains(part.OnamFormId)
                    && !emittedNewStats.Contains(part.OnamFormId)))
            {
                warnings.Add(
                    $"SCOL 0x{scol.FormId:X8} part ONAM 0x{part.OnamFormId:X8} unreachable " +
                    "(not in master, not a newly-emitted STAT) — dropping part.");
                continue;
            }

            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("ONAM", part.OnamFormId));
            subs.Add(EncodePlacementData(part.Placements));
            validParts++;
        }

        if (validParts == 0)
        {
            warnings.Add(
                $"SCOL 0x{scol.FormId:X8} \"{scol.EditorId ?? "<no EDID>"}\" had no reachable parts " +
                "after ONAM validation — dropping record (PluginBuilder short-circuits empty subrecord lists).");
            return new EncodedRecord { Subrecords = [], Warnings = warnings };
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static EncodedSubrecord EncodePlacementData(List<StaticCollectionPlacement> placements)
    {
        var bytes = new byte[placements.Count * 28];
        var span = bytes.AsSpan();
        for (var i = 0; i < placements.Count; i++)
        {
            var baseOffset = i * 28;
            var p = placements[i];
            SubrecordEncoder.WriteFloat(span, baseOffset + 0, p.X);
            SubrecordEncoder.WriteFloat(span, baseOffset + 4, p.Y);
            SubrecordEncoder.WriteFloat(span, baseOffset + 8, p.Z);
            SubrecordEncoder.WriteFloat(span, baseOffset + 12, p.RotX);
            SubrecordEncoder.WriteFloat(span, baseOffset + 16, p.RotY);
            SubrecordEncoder.WriteFloat(span, baseOffset + 20, p.RotZ);
            SubrecordEncoder.WriteFloat(span, baseOffset + 24, p.Scale);
        }

        return new EncodedSubrecord("DATA", bytes);
    }
}
