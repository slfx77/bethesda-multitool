using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes a FLOR (Flora) record as PC-format subrecord bytes. FLOR has no dedicated typed
///     model — it is captured (by <c>RuntimeFlorReader</c>) and parsed as a
///     <see cref="GenericEsmRecord" /> — so this encoder reads straight from that generic shape.
///     Emits the full record from scratch in fopdoc/xEdit <c>wbFLOR</c> canonical order:
///     EDID, OBND?, FULL?, MODL?, SCRI?, PFIG?, PFPC?, SNAM?. The override path is a no-op
///     (FNV masters contain no FLOR, so every captured FLOR is a new record).
/// </summary>
public sealed class FlorEncoder : IRecordEncoder
{
    public string RecordType => "FLOR";
    public Type ModelType => typeof(GenericEsmRecord);

    /// <summary>
    ///     Encode a new FLOR record from scratch in fopdoc canonical order:
    ///     EDID, OBND, FULL, MODL, SCRI, PFIG, PFPC, SNAM.
    /// </summary>
    internal static EncodedRecord EncodeNew(GenericEsmRecord flor)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(flor.EditorId))
        {
            warnings.Add($"New FLOR 0x{flor.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", flor.EditorId ?? string.Empty));

        if (flor.Bounds is not null)
        {
            subs.Add(NewRecordSubrecords.EncodeObndSubrecord(flor.Bounds));
        }

        if (!string.IsNullOrEmpty(flor.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", flor.FullName));
        }

        if (!string.IsNullOrEmpty(flor.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", flor.ModelPath));
        }

        // No MODS and no DEST here even though TESFlora carries both members at runtime: FLOR is
        // absent from xEdit's FNV and FO3 definitions (both only have a commented-out group-order
        // line), and the canonical order this encoder follows has neither subrecord. Emitting one
        // because the engine struct has the field would write a subrecord no reader expects.

        if (FormIdField(flor, "SCRI") is { } script)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SCRI", script));
        }

        if (FormIdField(flor, "PFIG") is { } ingredient)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("PFIG", ingredient));
        }

        // PFPC = per-season harvest chance (spring/summer/fall/winter, each 0-100). Opaque byte
        // array, emitted verbatim when present (the runtime reader does not capture it today).
        if (ByteArrayField(flor, "PFPC") is { } harvestChances)
        {
            subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord("PFPC", harvestChances));
        }

        if (FormIdField(flor, "SNAM") is { } sound)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SNAM", sound));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    /// <summary>
    ///     Reads a FormID-bearing field from the generic record's <c>Fields</c> dictionary.
    ///     The runtime reader stores a boxed <see cref="uint" /> directly; the ESM parser stores a
    ///     schema-decoded nested field dictionary — unwrap either to a single non-zero FormID.
    /// </summary>
    private static uint? FormIdField(GenericEsmRecord flor, string key)
    {
        if (!flor.Fields.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        var formId = value switch
        {
            uint u => u,
            IReadOnlyDictionary<string, object?> nested => nested.Values.OfType<uint>().FirstOrDefault(),
            _ => 0u
        };

        return formId != 0 ? formId : null;
    }

    private static byte[]? ByteArrayField(GenericEsmRecord flor, string key)
    {
        return flor.Fields.TryGetValue(key, out var value) && value is byte[] { Length: > 0 } bytes
            ? bytes
            : null;
    }
}
