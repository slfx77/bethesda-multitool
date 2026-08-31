using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes a Casino Chip (CHIP) record. Like FLOR/MSTT it has no typed model and arrives as a
///     <see cref="GenericEsmRecord" />, so every field is read through
///     <see cref="GenericRecordFields" /> with both the subrecord signature (ESM carve path) and
///     the PDB <c>Owner.Name</c> identifier (runtime path).
///     <para>
///         Canonical order from xEdit <c>wbRecord(CHIP)</c> (wbDefinitionsFNV.pas):
///         EDID(req), OBND(req), FULL?, MODL?, ICON?, DEST?, YNAM?, ZNAM?.
///     </para>
///     <para>
///         PDB <c>TESCasinoChips</c> (size 172): <c>BoundData</c> @52 → OBND,
///         <c>cFullName</c> @68 → FULL, <c>cModel</c> @80 → MODL,
///         <c>TESTexture.TextureName</c> @112 → ICON,
///         <c>BGSPickupPutdownSounds.pPickupSound</c> @156 → YNAM,
///         <c>BGSPickupPutdownSounds.pPutdownSound</c> @160 → ZNAM. The bounds, name, and model
///         members are surfaced on the record itself rather than in <c>Fields</c>, which is why
///         those three read from properties.
///     </para>
///     <para>
///         DEST comes from the <c>DestructibleObjectData</c> allocation behind
///         <c>BGSDestructibleObjectForm.pData</c> @148, and MODS from the
///         <c>BSSimpleList&lt;TEX_SWAP *&gt;</c> at <c>TextureSwapList</c> @100 — both now walked
///         through the exported auxiliary struct layouts.
///     </para>
/// </summary>
public sealed class ChipEncoder : IRecordEncoder
{
    public string RecordType => "CHIP";

    public Type ModelType => typeof(GenericEsmRecord);

    internal static EncodedRecord EncodeNew(GenericEsmRecord chip)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(chip.EditorId))
        {
            warnings.Add($"New CHIP 0x{chip.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", chip.EditorId ?? string.Empty));

        // OBND is .SetRequired but a misaligned capture produces inverted extents; per the
        // MSTT/ASPC precedent an absent bound is benign and a garbage one is not, so drop it.
        if (chip.Bounds is not null && GenericRecordFields.IsPlausibleBounds(chip.Bounds))
        {
            subs.Add(NewRecordSubrecords.EncodeObndSubrecord(chip.Bounds));
        }
        else if (chip.Bounds is not null)
        {
            warnings.Add($"CHIP 0x{chip.FormId:X8} captured implausible bounds — omitting OBND.");
        }

        if (!string.IsNullOrEmpty(chip.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", chip.FullName));
        }

        if (!string.IsNullOrEmpty(chip.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", chip.ModelPath));
        }

        // MODS closes xEdit's wbGenericModel group (MODL/MODB/MODT/MODS/MODD) and so precedes ICON.
        NewRecordSubrecords.AppendAlternateTextures(subs, chip);

        if (GenericRecordFields.TryString(chip, "ICON", "TESTexture.TextureName") is { } icon)
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ICON", icon));
        }

        NewRecordSubrecords.AppendDestruction(subs, chip);

        // TESCasinoChips.cDesc @164 is read by the generic sweep but has no CHIP subrecord in the
        // FNV schema — wbRecord(CHIP) has no DESC — so it is deliberately not emitted. Writing it
        // would produce a subrecord xEdit and the engine both reject as unknown.

        if (GenericRecordFields.TryFormId(chip, "YNAM", "BGSPickupPutdownSounds.pPickupSound") is { } pickup)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("YNAM", pickup));
        }

        if (GenericRecordFields.TryFormId(chip, "ZNAM", "BGSPickupPutdownSounds.pPutdownSound") is { } putdown)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("ZNAM", putdown));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
