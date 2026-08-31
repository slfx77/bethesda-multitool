using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes a Casino (CSNO) record. No typed model — it arrives as a
///     <see cref="GenericEsmRecord" />, so every field is read through
///     <see cref="GenericRecordFields" /> with both key forms.
///     <para>
///         Canonical order from xEdit <c>wbRecord(CSNO)</c> (wbDefinitionsFNV.pas):
///         EDID(req), FULL, DATA, then the chip-model and reel-texture MODL/ICON groups.
///     </para>
///     <para>
///         PDB <c>TESCasino</c> (size 560): <c>cFullName</c> @44 → FULL, <c>data</c> @504 (56-byte
///         <c>CASINO_DATA</c>) → DATA. The size matches xEdit's Data block exactly — 2 floats, seven
///         u32 reel stops, deck count, max winnings, a CHIP FormID, a QUST FormID and flags — and a
///         56-byte CSNO DATA schema is already registered.
///     </para>
///     <para>
///         <b>The model and texture groups are unreachable, not omitted by choice.</b>
///         <c>modelArrayList</c> @52 and <c>textureArrayList</c> @372 — 452 bytes between them,
///         holding the seven chip models, the slot/blackjack/roulette table models and the reel
///         textures — arrive from the PDB export as <c>size:0, kind:"unknown"</c> (unresolved type
///         indices <c>0x0001324E</c> / <c>0x0001324F</c>), so
///         <c>PdbStructLayouts.GetReadableFields</c> drops them before any reader runs. No reader
///         change can reach them; only regenerating the layout database with those indices resolved
///         can.
///     </para>
/// </summary>
public sealed class CsnoEncoder : IRecordEncoder
{
    /// <summary>Runtime <c>CASINO_DATA</c> size, identical to xEdit's DATA block.</summary>
    private const int CasinoDataSize = 56;

    public string RecordType => "CSNO";

    public Type ModelType => typeof(GenericEsmRecord);

    internal static EncodedRecord EncodeNew(GenericEsmRecord csno)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(csno.EditorId))
        {
            warnings.Add($"New CSNO 0x{csno.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", csno.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(csno.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", csno.FullName));
        }

        var data = EncodeData(csno);
        if (data is null)
        {
            warnings.Add($"CSNO 0x{csno.FormId:X8} has no convertible CASINO_DATA — omitting DATA.");
        }
        else
        {
            subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord("DATA", data));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[]? EncodeData(GenericEsmRecord csno)
    {
        if (GenericRecordFields.TryBytes(csno, CasinoDataSize, "DATA", "TESCasino.data") is { } raw)
        {
            return SubrecordSchemaProcessor.ConvertWithSchema("DATA", raw, "CSNO");
        }

        if (csno.Fields.TryGetValue("DATA", out var value)
            && value is IReadOnlyDictionary<string, object?> { Count: > 0 } decoded)
        {
            var schema = SubrecordSchemaRegistry.GetSchema("DATA", "CSNO", CasinoDataSize);
            return schema is null ? null : SchemaDictionarySerializer.Serialize(schema, decoded);
        }

        return null;
    }
}
