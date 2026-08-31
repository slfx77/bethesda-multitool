using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes the Default Object Manager (DOBJ) record. No typed model — it arrives as a
///     <see cref="GenericEsmRecord" />.
///     <para>
///         Canonical shape from xEdit <c>wbRecord(DOBJ)</c> (wbDefinitionsFNV.pas): EDID, then a
///         single DATA struct of 34 FormIDs, each a fixed slot with a fixed meaning — Stimpak,
///         Super Stimpak, Rad-X, RadAway, Morphine, Perk Paralysis, Player Faction, Mysterious
///         Stranger NPC and Faction, the five music types, the voice types, and so on. Every slot
///         is <c>[X, NULL]</c>, so an unfilled one is legal.
///     </para>
///     <para>
///         PDB <c>BGSDefaultObjectManager</c> (size 176): <c>pObjectArray</c> @40 is a 136-byte
///         inline array of <c>TESForm *</c> — exactly 34 pointers, exactly matching the file's
///         34-FormID DATA. Position is the meaning, so <c>RuntimeContainerFieldReader</c> keeps
///         empty slots as NULL rather than compacting them; a dropped slot would shift every later
///         entry onto the wrong default object.
///     </para>
///     <para>
///         Reachable only since the layout database was regenerated with <c>LF_ARRAY</c> leaves
///         resolved: <c>pObjectArray</c> previously exported as <c>size:0, kind:"unknown"</c> and
///         was dropped by <c>GetReadableFields</c> before any reader saw it, which is why DOBJ
///         carried no recoverable content despite appearing in all 32 corpus dumps.
///     </para>
/// </summary>
public sealed class DobjEncoder : IRecordEncoder
{
    /// <summary>Slot count: 136 bytes of <c>TESForm *</c> = 34 pointers = xEdit's 34 FormIDs.</summary>
    private const int DefaultObjectSlots = 34;

    public string RecordType => "DOBJ";

    public Type ModelType => typeof(GenericEsmRecord);

    internal static EncodedRecord EncodeNew(GenericEsmRecord dobj)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(dobj.EditorId))
        {
            warnings.Add($"New DOBJ 0x{dobj.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", dobj.EditorId ?? string.Empty));

        var slots = GenericRecordFields.TryFormIdSlots(
            dobj, DefaultObjectSlots, "DATA", "BGSDefaultObjectManager.pObjectArray");
        if (slots is null)
        {
            warnings.Add(
                $"DOBJ 0x{dobj.FormId:X8} default-object table did not resolve to {DefaultObjectSlots} " +
                "slots — omitting DATA rather than emitting a mis-indexed table.");
        }
        else
        {
            // No schema conversion: the slots were resolved to FormIDs individually, so there are
            // no big-endian bytes left to swap — EncodeFormIdArraySubrecord writes them PC-side.
            subs.Add(NewRecordSubrecords.EncodeFormIdArraySubrecord("DATA", slots));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
