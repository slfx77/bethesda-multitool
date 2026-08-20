using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes an Addon Node (ADDN) record — 37 forms in every captured dump. ADDN binds a node
///     index to a model and optional sound so effects can attach to it; a stripped proto-only ADDN
///     leaves anything referencing that node index unresolved.
///     <para>
///         Canonical order from xEdit <c>wbRecord(ADDN)</c> (wbDefinitionsFNV.pas):
///         EDID(req), OBND(req), MODL(req), DATA(req, s32 Node Index), SNAM?, DNAM(req, 4B).
///     </para>
/// </summary>
public sealed class AddnEncoder : IRecordEncoder
{
    public string RecordType => "ADDN";

    public Type ModelType => typeof(GenericEsmRecord);

    internal static EncodedRecord EncodeNew(GenericEsmRecord addn)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(addn.EditorId))
        {
            warnings.Add($"New ADDN 0x{addn.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", addn.EditorId ?? string.Empty));

        if (addn.Bounds is not null && GenericRecordFields.IsPlausibleBounds(addn.Bounds))
        {
            subs.Add(NewRecordSubrecords.EncodeObndSubrecord(addn.Bounds));
        }
        else if (addn.Bounds is not null)
        {
            warnings.Add($"ADDN 0x{addn.FormId:X8} captured implausible bounds — omitting OBND.");
        }

        if (!string.IsNullOrEmpty(addn.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", addn.ModelPath));
        }

        // DATA is the node index (BGSAddonNode.iIndex @96), required by the schema.
        var nodeIndex = GenericRecordFields.TryUInt(addn, "DATA", "BGSAddonNode.iIndex");
        if (nodeIndex is null)
        {
            warnings.Add(
                $"ADDN 0x{addn.FormId:X8} has no captured node index — emitting required DATA as 0.");
        }

        subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord("DATA", nodeIndex ?? 0u));

        if (GenericRecordFields.TryFormId(addn, "SNAM", "BGSAddonNode.pSound") is { } sound)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SNAM", sound));
        }

        // DNAM is a required 4-byte struct: uint16 "Master Particle System Cap" + 2 unknown bytes.
        // It maps to the 4-byte BGSAddonNode.Data (ADDON_DATA @104), which the generic reader
        // surfaces as a hex string. Prefer those captured bytes verbatim; otherwise synthesize
        // from iMasterParticleSystemIndex @108 and leave the trailing 2 bytes zero.
        var dnam = GenericRecordFields.TryBytes(addn, 4, "DNAM", "BGSAddonNode.Data");
        if (dnam is null)
        {
            dnam = new byte[4];
            var cap = GenericRecordFields.TryUInt(addn, "BGSAddonNode.iMasterParticleSystemIndex");
            if (cap is not null)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(dnam.AsSpan(0, 2),
                    (ushort)Math.Min(cap.Value, ushort.MaxValue));
            }
            else
            {
                warnings.Add(
                    $"ADDN 0x{addn.FormId:X8} has no captured ADDON_DATA — emitting required DNAM as zeros.");
            }
        }

        subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord("DNAM", dnam));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
