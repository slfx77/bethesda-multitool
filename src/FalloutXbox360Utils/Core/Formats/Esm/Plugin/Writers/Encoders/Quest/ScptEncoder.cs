using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;
using FalloutXbox360Utils.Core.Formats.Esm.Script;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;

/// <summary>
///     Encodes a <see cref="ScriptRecord" /> (SCPT) as PC-format subrecord bytes.
///     Emits the full record from scratch: EDID + SCHR + SCDA? + SCTX? + SLSD/SCVR pairs +
///     SCRO/SCRV referenced objects. Override path is a no-op — script bytecode in the DMP
///     matches the master ESM's bytecode and is retained verbatim.
///     SCHR (20 bytes) per PDB SCRIPT_HEADER:
///     uint32 VariableCount(0) + uint32 RefObjectCount(4) + uint32 CompiledSize(8) +
///     uint32 LastVariableId(12) + bool IsQuestScript(16) + bool IsMagicEffectScript(17) +
///     bool IsCompiled(18) + pad(19).
///     SLSD (24 bytes) per PDB SCRIPT_LOCAL:
///     uint32 Index(0) + pad(4..7) + double Value(8..15) + bool IsInteger(16) + pad(17..23).
/// </summary>
public sealed class ScptEncoder : IRecordEncoder
{
    // SCHR canonical ESM layout per fopdoc (Records/Subrecords/SCHR.md):
    //   Padding(4) + RefCount(uint32) + CompiledSize(uint32) + VariableCount(uint32) +
    //   Type(uint16) + Flags(uint16).
    // The runtime SCRIPT_HEADER struct has a different layout (VariableCount at offset 0,
    // uiLastID at offset 12, 3 separate bool bytes for IsQuestScript/IsMagicEffectScript/
    // IsCompiled); we map runtime model fields to canonical positions here.
    //
    // VariableCount: bound the engine uses to size SLSD lookup. Emit the actual count of
    //   SLSD entries we'll write (script.Variables.Count) rather than the runtime's
    //   uiLastID — uiLastID can exceed Variables.Count after the runtime allocates dynamic
    //   variables we don't capture, causing the engine to look for SLSD slots that don't
    //   exist and log "Variable ID NNNNNNNN not found in 'UNKNOWN' script".
    // Type: 0 = Object, 1 = Quest, 0x100 = Effect (mutually exclusive in vanilla content).
    // Flags: 0x0001 = Enabled.
    private static readonly Dictionary<string, Func<ScriptRecord, object?>> SchrExtractors = new(StringComparer.Ordinal)
    {
        ["RefCount"] = m => m.RefObjectCount,
        ["CompiledSize"] = m => m.CompiledSize,
        ["VariableCount"] = m => (uint)m.Variables.Count,
        ["Type"] = m => GetSchrType(m),
        ["Flags"] = m => (ushort)(m.IsCompiled ? 0x0001 : 0x0000),
    };

    private static readonly Dictionary<string, Func<ScriptVariableInfo, object?>> SlsdExtractors = new(StringComparer.Ordinal)
    {
        ["Index"] = m => m.Index,
        // Value (double) intentionally omitted — runtime stores the last-evaluated value but
        // the encoder writes zero so the engine recomputes on first script run.
        ["IsInteger"] = m => m.Type,
    };

    public string RecordType => "SCPT";
    public Type ModelType => typeof(ScriptRecord);

    private static ushort GetSchrType(ScriptRecord m)
    {
        if (m.IsMagicEffectScript) return 0x100;
        if (m.IsQuestScript) return 1;
        return 0;
    }

    /// <summary>
    ///     Encode a new SCPT record from scratch in fopdoc canonical order:
    ///     EDID, SCHR, SCDA?, SCTX?, then per local: SLSD + SCVR, then per ref: SCRO/SCRV.
    /// </summary>
    /// <param name="script">SCPT model to emit.</param>
    /// <param name="validFormIds">
    ///     Master ∪ newly-emitted FormID set, used to validate SCRO references. When supplied,
    ///     unresolvable refs are emitted as 0x00000000 (engine treats as null/no-op) so the
    ///     engine doesn't refuse to execute the script. Index order is preserved so bytecode
    ///     operands (1-based into the combined SCRO+SCRV list) stay valid.
    /// </param>
    /// <param name="remapTable">
    ///     Source→allocated FormID alias map. Proto FormIDs the converter has reallocated
    ///     into plugin space resolve through this before validity checking. Without this,
    ///     scripts that reference new NPCs/objects fail with "Could not find referenced
    ///     object … Script will not be executed".
    /// </param>
    internal static EncodedRecord EncodeNew(
        ScriptRecord script,
        IReadOnlySet<uint>? validFormIds = null,
        IReadOnlyDictionary<uint, uint>? remapTable = null)
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
        subs.Add(SchemaModelSerializer.SerializeSubrecord("SCHR", "", 20, script, SchrExtractors));

        if (script.CompiledData is { Length: > 0 } compiledData)
        {
            // Xbox 360 stores SCDA bytecode in native (big-endian) form. The PC engine
            // reads opcodes/operands as little-endian; emitting BE bytes verbatim makes the
            // very first opcode (ScriptName, 0x001D BE) decode as 0x1D00 — the "command
            // 7424" spam the engine logs for every converted script.
            var scda = script.IsBigEndian
                ? ScriptBytecodeEndianConverter.SwapBigEndianToLittleEndian(
                    compiledData, script.Variables, script.ReferencedObjects)
                : compiledData;
            subs.Add(new EncodedSubrecord("SCDA", scda));
        }

        if (!string.IsNullOrEmpty(script.SourceText))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("SCTX", script.SourceText));
        }

        foreach (var variable in script.Variables)
        {
            subs.Add(SchemaModelSerializer.SerializeSubrecord("SLSD", "", 24, variable, SlsdExtractors));
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
                // Route every SCRO through the alias table + validity check. The bytecode
                // refers to SCRO entries by 1-based index into the combined SCRO+SCRV list,
                // so we MUST preserve order/count — a null result becomes 0x00000000 in
                // place rather than a dropped entry. The engine treats null FormID refs as
                // no-ops, but a dangling FormID causes "Script will not be executed".
                var resolved = FormIdReferenceResolver.Resolve(refFormId, validFormIds, remapTable) ?? 0u;
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SCRO", resolved));
            }
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
