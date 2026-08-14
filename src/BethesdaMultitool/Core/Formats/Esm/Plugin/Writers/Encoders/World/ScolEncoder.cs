using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.World;

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
    ///     subrecord list. Production planning reserves and skips that known-empty New SCOL;
    ///     the writer treats any remaining New-empty result as a planner-contract failure.
    /// </summary>
    internal static EncodedRecord EncodeNew(
        StaticCollectionRecord scol,
        IReadOnlySet<uint> masterFormIds,
        IReadOnlySet<uint> emittedNewStats,
        IReadOnlyDictionary<uint, uint>? sourceToEmitted = null)
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
            if (!IsPartReachable(part, masterFormIds, emittedNewStats, sourceToEmitted))
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
            // A runtime-sourced SCOL never has parts — the PDB layout carries no part list —
            // but it does carry the BAKED collection model, which is what the engine renders.
            // Emitting EDID+OBND+MODL keeps refs to it resolvable instead of dangling (USER
            // RULING 2026-08-05, playtest finding 3: 27 proto-Strip sidewalk refs dropped this
            // way). A part-less SCOL with no model is still dropped — there is nothing to draw.
            if (!string.IsNullOrEmpty(scol.ModelPath))
            {
                warnings.Add(
                    $"SCOL 0x{scol.FormId:X8} \"{scol.EditorId ?? "<no EDID>"}\" emitted PART-LESS " +
                    "(runtime capture carries only the baked model — the PDB layout has no part list).");
                return new EncodedRecord { Subrecords = subs, Warnings = warnings };
            }

            warnings.Add(
                $"SCOL 0x{scol.FormId:X8} \"{scol.EditorId ?? "<no EDID>"}\" had no reachable parts " +
                "and no baked model — record requires planner-owned non-emission.");
            return new EncodedRecord { Subrecords = [], Warnings = warnings };
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    /// <summary>
    ///     True when a new SCOL can produce a load-bearing record with the supplied final
    ///     liveness sets. A baked model is sufficient without parts; otherwise at least one
    ///     ONAM target must be reachable. The planner uses this before reference resolution
    ///     so a known-empty SCOL becomes an explicit reservation instead of a late decline.
    /// </summary>
    internal static bool CanEmitNew(
        StaticCollectionRecord scol,
        IReadOnlySet<uint> masterFormIds,
        IReadOnlySet<uint> emittedNewStats,
        IReadOnlyDictionary<uint, uint>? sourceToEmitted = null)
    {
        return !string.IsNullOrEmpty(scol.ModelPath)
               || scol.Parts.Any(part =>
                   IsPartReachable(part, masterFormIds, emittedNewStats, sourceToEmitted));
    }

    private static bool IsPartReachable(
        StaticCollectionPart part,
        IReadOnlySet<uint> masterFormIds,
        IReadOnlySet<uint> emittedNewStats,
        IReadOnlyDictionary<uint, uint>? sourceToEmitted)
    {
        if (part.OnamFormId == 0)
        {
            return false;
        }

        var target = sourceToEmitted is not null
                     && sourceToEmitted.TryGetValue(part.OnamFormId, out var emitted)
            ? emitted
            : part.OnamFormId;
        return masterFormIds.Contains(target) || emittedNewStats.Contains(target);
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
