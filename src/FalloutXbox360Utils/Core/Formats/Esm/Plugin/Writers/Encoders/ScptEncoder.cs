using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="ScriptRecord" /> (SCPT) as PC-format subrecord bytes.
///     v6 emits the full record from scratch: EDID + SCHR + SCDA? + SCTX? + SLSD/SCVR pairs +
///     SCRO/SCRV referenced objects. Override path is a no-op — script bytecode in the DMP
///     matches the master ESM's bytecode and is retained verbatim.
///     SCHR (20 bytes) per PDB SCRIPT_HEADER:
///         uint32 VariableCount(0) + uint32 RefObjectCount(4) + uint32 CompiledSize(8) +
///         uint32 LastVariableId(12) + bool IsQuestScript(16) + bool IsMagicEffectScript(17) +
///         bool IsCompiled(18) + pad(19).
///     SLSD (24 bytes) per PDB SCRIPT_LOCAL:
///         uint32 Index(0) + pad(4..7) + double Value(8..15) + bool IsInteger(16) + pad(17..23).
/// </summary>
public sealed class ScptEncoder : IRecordEncoder
{
    public string RecordType => "SCPT";
    public Type ModelType => typeof(ScriptRecord);

    public EncodedRecord Encode(object model)
    {
        // SCPT override path is a no-op — bytecode in DMP equals master ESM, no mutations possible.
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    /// <summary>
    ///     Encode a new SCPT record from scratch in fopdoc canonical order:
    ///     EDID, SCHR, SCDA?, SCTX?, then per local: SLSD + SCVR, then per ref: SCRO/SCRV.
    /// </summary>
    internal static EncodedRecord EncodeNew(ScriptRecord script)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        // Skip stub scripts entirely — empty EditorID plus no bytecode means the parser
        // detected a SCPT signature on stale memory; the FNV runtime would print "Script
        // '' has not been compiled" and refuse to bind any SCRI references.
        if (string.IsNullOrEmpty(script.EditorId)
            && (script.CompiledData is null || script.CompiledData.Length == 0)
            && script.Variables.Count == 0
            && script.ReferencedObjects.Count == 0)
        {
            warnings.Add($"New SCPT 0x{script.FormId:X8} has no EditorId, bytecode, or vars — skipping stub.");
            return new EncodedRecord { Subrecords = subs, Warnings = warnings };
        }

        if (string.IsNullOrEmpty(script.EditorId))
        {
            warnings.Add($"New SCPT 0x{script.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", script.EditorId ?? string.Empty));
        subs.Add(new EncodedSubrecord("SCHR", BuildSchrSubrecord(script)));

        if (script.CompiledData is { Length: > 0 } compiledData)
        {
            subs.Add(new EncodedSubrecord("SCDA", compiledData));
        }

        if (!string.IsNullOrEmpty(script.SourceText))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("SCTX", script.SourceText));
        }

        foreach (var variable in script.Variables)
        {
            subs.Add(new EncodedSubrecord("SLSD", BuildSlsdSubrecord(variable)));
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("SCVR", variable.Name ?? string.Empty));
        }

        foreach (var refFormId in script.ReferencedObjects)
        {
            // SCRV entries are stored in the model with the high bit set (0x80000000 | varIdx).
            // Mask it off and emit as SCRV; everything else is a plain SCRO FormID.
            if ((refFormId & 0x80000000) != 0)
            {
                var varIndex = refFormId & 0x7FFFFFFF;
                subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord("SCRV", varIndex));
            }
            else
            {
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SCRO", refFormId));
            }
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[] BuildSchrSubrecord(ScriptRecord script)
    {
        var schr = new byte[20];
        SubrecordEncoder.WriteUInt32(schr, 0, script.VariableCount);
        SubrecordEncoder.WriteUInt32(schr, 4, script.RefObjectCount);
        SubrecordEncoder.WriteUInt32(schr, 8, script.CompiledSize);
        SubrecordEncoder.WriteUInt32(schr, 12, script.LastVariableId);
        schr[16] = script.IsQuestScript ? (byte)1 : (byte)0;
        schr[17] = script.IsMagicEffectScript ? (byte)1 : (byte)0;
        schr[18] = script.IsCompiled ? (byte)1 : (byte)0;
        // byte 19 padding (zero)
        return schr;
    }

    private static byte[] BuildSlsdSubrecord(ScriptVariableInfo variable)
    {
        var slsd = new byte[24];
        SubrecordEncoder.WriteUInt32(slsd, 0, variable.Index);
        // bytes 4-7: padding (alignment for the double at offset 8)
        // bytes 8-15: fValue (double) — runtime stores last-evaluated value; encoder writes zero.
        // bytes 16: bIsInteger (0 = float, non-zero = integer)
        slsd[16] = variable.Type;
        // bytes 17-23: padding
        return slsd;
    }
}
