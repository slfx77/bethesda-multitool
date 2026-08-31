using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes an Impact Data Set (IPDS) record. No typed model — it arrives as a
///     <see cref="GenericEsmRecord" />.
///     <para>
///         Canonical shape from xEdit <c>wbRecord(IPDS)</c> (wbDefinitionsFNV.pas): EDID(req), then
///         a DATA struct of 12 IPCT FormIDs, one per material — Stone, Dirt, Grass, Glass, Metal,
///         Wood, Organic, Cloth, Water, Hollow Metal, Organic Bug, Organic Glow. Each is
///         <c>[IPCT, NULL]</c>, so an unused material is legal.
///     </para>
///     <para>
///         PDB <c>BGSImpactDataSet</c> (size 92): <c>ppImpactData</c> @44 is a 48-byte inline array
///         of <c>BGSImpactData *</c> — 12 pointers, matching the file's 12 FormIDs slot for slot.
///         Position is the material, so empty slots keep their place as NULL.
///     </para>
///     <para>
///         Like DOBJ, reachable only since the layout database gained <c>LF_ARRAY</c> resolution:
///         <c>ppImpactData</c> is IPDS's <i>entire</i> payload and used to export as
///         <c>size:0, kind:"unknown"</c>, which is why 1,394 captured records across the corpus
///         could produce nothing but an EDID.
///     </para>
/// </summary>
public sealed class IpdsEncoder : IRecordEncoder
{
    /// <summary>Slot count: 48 bytes of <c>BGSImpactData *</c> = 12 materials.</summary>
    private const int MaterialSlots = 12;

    public string RecordType => "IPDS";

    public Type ModelType => typeof(GenericEsmRecord);

    internal static EncodedRecord EncodeNew(GenericEsmRecord ipds)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(ipds.EditorId))
        {
            warnings.Add($"New IPDS 0x{ipds.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", ipds.EditorId ?? string.Empty));

        var slots = GenericRecordFields.TryFormIdSlots(
            ipds, MaterialSlots, "DATA", "BGSImpactDataSet.ppImpactData");
        if (slots is null)
        {
            warnings.Add(
                $"IPDS 0x{ipds.FormId:X8} impact table did not resolve to {MaterialSlots} material " +
                "slots — omitting required DATA rather than emitting a mis-indexed table.");
        }
        else
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdArraySubrecord("DATA", slots));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
