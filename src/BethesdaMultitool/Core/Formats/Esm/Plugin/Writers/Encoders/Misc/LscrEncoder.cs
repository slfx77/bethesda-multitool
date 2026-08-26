using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes a Load Screen (LSCR) record. LSCR has no typed model — it arrives as a
///     <see cref="GenericEsmRecord" /> from <c>RuntimeGenericReader</c> (Fields keyed by PDB
///     identifier) or the ESM carve path (keyed by subrecord signature), so every lookup goes
///     through <see cref="GenericRecordFields" /> with both key forms.
///     <para>
///         Canonical order from xEdit <c>wbRecord(LSCR)</c> (wbDefinitionsFNV.pas):
///         EDID(req), ICON(req), DESC(req), LNAM(array, opt), WMI1(opt).
///     </para>
///     <para>
///         PDB <c>TESLoadScreen</c> (size 80): <c>TESTexture.TextureName</c> @44 → ICON,
///         <c>TESLoadScreen.cDescText</c> @72 → DESC, <c>TESLoadScreen.pLoadScreenType</c> @68 →
///         WMI1. Both string members are <c>BSStringT&lt;char&gt;</c>, which the runtime reader
///         resolves to real text before the encoder sees it.
///     </para>
///     <para>
///         LNAM is deliberately not emitted. <c>TESLoadScreen.LoadFormList</c> @60 is a
///         <c>BSSimpleList&lt;LOAD_FORM_DATA *&gt;</c>; the generic reader never walks list heads,
///         so there is no captured location data to write and a synthesized entry would invent
///         content. The member is optional in the schema, so omitting it still loads.
///     </para>
/// </summary>
public sealed class LscrEncoder : IRecordEncoder
{
    public string RecordType => "LSCR";

    public Type ModelType => typeof(GenericEsmRecord);

    internal static EncodedRecord EncodeNew(GenericEsmRecord lscr)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(lscr.EditorId))
        {
            warnings.Add($"New LSCR 0x{lscr.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", lscr.EditorId ?? string.Empty));

        // ICON and DESC are both .SetRequired in the FNV schema. Emit them unconditionally so the
        // record still loads, but say so when the capture had nothing behind them — an empty
        // load-screen texture is cosmetic, a missing required subrecord is a parse failure.
        var icon = GenericRecordFields.TryString(lscr, "ICON", "TESTexture.TextureName");
        if (icon is null)
        {
            warnings.Add(
                $"LSCR 0x{lscr.FormId:X8} has no captured TextureName — emitting required ICON as empty.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ICON", icon ?? string.Empty));

        var desc = GenericRecordFields.TryString(lscr, "DESC", "TESLoadScreen.cDescText");
        if (desc is null)
        {
            warnings.Add(
                $"LSCR 0x{lscr.FormId:X8} has no captured cDescText — emitting required DESC as empty.");
        }

        // DESC is player-facing authored text, and the ESM carve path decodes it as Windows-1252
        // (EsmStringUtils.DecodeGameText). Re-encoding through Latin-1 would drop every character
        // that came from a 0x80-0x9F byte, so the game-text writer is the lossless round trip here.
        subs.Add(NewRecordSubrecords.EncodeGameTextSubrecord("DESC", desc ?? string.Empty));

        // WMI1 names an LSCT (load-screen type). Optional, so a slot the capture could not resolve
        // is simply left out rather than written as a null reference.
        if (GenericRecordFields.TryFormId(lscr, "WMI1", "TESLoadScreen.pLoadScreenType") is { } screenType)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("WMI1", screenType));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
