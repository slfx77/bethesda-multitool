using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes an Animated Object (ANIO) record — 144-148 forms in every captured dump. ANIO binds
///     a model to the IDLE animation that plays on it; without the encoder a proto-only ANIO is
///     stripped and anything referencing it animates as a static prop.
///     <para>
///         ANIO has no dedicated typed model and arrives as a <see cref="GenericEsmRecord" /> from
///         <c>RuntimeGenericReader</c>, whose <see cref="GenericEsmRecord.Fields" /> keys are PDB
///         identifiers.
///     </para>
///     <para>
///         Canonical order from xEdit <c>wbRecord(ANIO)</c> (wbDefinitionsFNV.pas):
///         EDID(req), MODL(req), DATA(req — IDLE FormID). Note ANIO has no OBND and no FULL.
///     </para>
/// </summary>
public sealed class AnioEncoder : IRecordEncoder
{
    public string RecordType => "ANIO";

    public Type ModelType => typeof(GenericEsmRecord);

    internal static EncodedRecord EncodeNew(GenericEsmRecord anio)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(anio.EditorId))
        {
            warnings.Add($"New ANIO 0x{anio.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", anio.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(anio.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", anio.ModelPath));
        }

        NewRecordSubrecords.AppendAlternateTextures(subs, anio);

        // DATA is the animation link (TESObjectANIO.pIdleAnim @72 → TESIdleForm), marked required
        // in the xEdit schema. Unlike MSTT's DATA there is no safe default: a zero FormID is not a
        // valid IDLE reference and the engine would fail to resolve it, so omit the subrecord and
        // warn instead of writing a dangling link.
        if (GenericRecordFields.TryFormId(anio, "DATA", "TESObjectANIO.pIdleAnim") is { } idle)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("DATA", idle));
        }
        else
        {
            warnings.Add(
                $"ANIO 0x{anio.FormId:X8} has no captured pIdleAnim — omitting required DATA " +
                "rather than emitting a null IDLE reference.");
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
