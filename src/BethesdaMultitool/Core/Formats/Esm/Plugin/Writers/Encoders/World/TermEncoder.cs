using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;
using BethesdaMultitool.Core.Formats.Esm.Script;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.World;

/// <summary>
///     Encodes a <see cref="TerminalRecord" /> (TERM) as PC-format subrecord bytes.
///     Emits EDID + OBND? + FULL? + MODL? + SCRI? + DESC? + SNAM? + PNAM? +
///     DNAM(4B Difficulty + Flags + ServerType + Unused) +
///     per menu item (xEdit-canonical): ITXT + RNAM(required string) + ANAM(required) +
///     TNAM?(sub-menu) + embedded SCHR+SCDA?+SCTX?+(SLSD+SCVR)*+(SCRO|SCRV)*(required) + CTDA*.
///     FNV has no NEXT separator. Override path is a no-op.
///     DNAM layout per PDB TERMINAL_DATA (4 bytes):
///     byte Difficulty(0) + byte Flags(1) + byte ServerType(2) + byte Unused(3).
///     Embedded scripts use the same on-disk pattern as INFO result scripts (see InfoEncoder).
/// </summary>
public sealed class TermEncoder : IRecordEncoder
{
    public string RecordType => "TERM";
    public Type ModelType => typeof(TerminalRecord);

    /// <summary>
    ///     Encode a new TERM record from scratch in fopdoc canonical order:
    ///     EDID, OBND, FULL, MODL, SCRI, DESC, SNAM, PNAM, DNAM,
    ///     per menu item: ITXT, RNAM, ANAM, TNAM?, embedded script block, CTDA*.
    /// </summary>
    /// <param name="term">TERM model to emit.</param>
    /// <param name="validFormIds">
    ///     Master ∪ newly-emitted FormID set for validating embedded result-script SCROs.
    ///     See <see cref="ScptEncoder.EncodeNew" />.
    /// </param>
    /// <param name="remapTable">
    ///     Source→allocated FormID alias map for embedded result-script SCROs.
    /// </param>
    internal static EncodedRecord EncodeNew(
        TerminalRecord term,
        IReadOnlySet<uint>? validFormIds = null,
        IReadOnlyDictionary<uint, uint>? remapTable = null)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();
        var emittedScriptPaths = new List<string>();
        var inlineScriptIssue = InlineScriptReferenceValidator.FindFirstIssue(
            term, validFormIds, remapTable);
        if (inlineScriptIssue is not null)
        {
            warnings.Add(
                $"New TERM 0x{term.FormId:X8} suppressed: {inlineScriptIssue.Message} " +
                "Inline SCDA/SLSD/SCRO/SCRV is atomic; no slot was dropped or zero-filled.");
            return new EncodedRecord { Subrecords = [], Warnings = warnings };
        }

        warnings.AddRange(
            InlineScriptReferenceValidator.FindSourceContractIssues(term, validFormIds, remapTable)
                .Select(static issue => issue.Message));

        if (string.IsNullOrEmpty(term.EditorId))
        {
            warnings.Add($"New TERM 0x{term.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", term.EditorId ?? string.Empty));

        if (term.Bounds is not null)
        {
            subs.Add(NewRecordSubrecords.EncodeObndSubrecord(term.Bounds));
        }

        if (!string.IsNullOrEmpty(term.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", term.FullName));
        }

        if (!string.IsNullOrEmpty(term.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", term.ModelPath));
        }

        if (term.ScriptFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SCRI", term.ScriptFormId.Value));
        }

        if (!string.IsNullOrEmpty(term.HeaderText))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("DESC", term.HeaderText));
        }

        if (term.SoundLoopFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SNAM", term.SoundLoopFormId.Value));
        }

        if (term.PasswordNoteFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("PNAM", term.PasswordNoteFormId.Value));
        }

        subs.Add(new EncodedSubrecord("DNAM", BuildDnamSubrecord(term)));

        for (var i = 0; i < term.MenuItems.Count; i++)
        {
            var item = term.MenuItems[i];
            var unknownCondition = item.Conditions.FirstOrDefault(static condition =>
                !PerkConditionParameterResolver.IsKnownConditionFunction(condition.FunctionIndex));
            if (unknownCondition is not null)
            {
                warnings.Add(
                    $"TERM 0x{term.FormId:X8} menu[{i}] '{item.Text ?? "(empty)"}' suppressed: " +
                    $"CTDA function 0x{unknownCondition.FunctionIndex:X4} is absent from the retail " +
                    "FNV command table. The whole menu item is atomic; malformed conditions are " +
                    "never emitted or widened.");
                continue;
            }

            if (validFormIds is not null && item.Conditions.Count > 0)
            {
                var remappedParameters = 0;
                var droppedConditions = 0;
                var sanitizedConditions = ConditionSanitizer.Filter(
                    item.Conditions,
                    validFormIds as HashSet<uint> ?? new HashSet<uint>(validFormIds),
                    remapTable,
                    ref remappedParameters,
                    ref droppedConditions);
                if (droppedConditions > 0)
                {
                    warnings.Add(
                        $"TERM 0x{term.FormId:X8} menu[{i}] '{item.Text ?? "(empty)"}' suppressed: " +
                        $"{droppedConditions} CTDA FormID parameter(s) did not resolve. The whole menu " +
                        "item is atomic; no condition was dropped or widened.");
                    continue;
                }

                if (remappedParameters > 0)
                {
                    item = item with { Conditions = sanitizedConditions };
                    warnings.Add(
                        $"TERM 0x{term.FormId:X8} menu[{i}] remapped {remappedParameters} CTDA " +
                        "FormID parameter(s) to emitted identities.");
                }
            }

            var emittedScript = EmitMenuItem(
                subs,
                warnings,
                term.FormId,
                item,
                i == term.MenuItems.Count - 1,
                validFormIds,
                remapTable);
            if (emittedScript)
            {
                emittedScriptPaths.Add($"{term.EditorId ?? $"TERM 0x{term.FormId:X8}"}/menu[{i}]");
            }
        }

        return new EncodedRecord
        {
            Subrecords = subs,
            Warnings = warnings,
            EmittedScriptPaths = emittedScriptPaths
        };
    }

    private static byte[] BuildDnamSubrecord(TerminalRecord term)
    {
        // DNAM (4 bytes): Difficulty, Flags, ServerType, Unused.
        return [term.Difficulty, term.Flags, term.ServerType, 0];
    }

    private static bool EmitMenuItem(
        List<EncodedSubrecord> subs,
        List<string> warnings,
        uint termFormId,
        TerminalMenuItem item,
        bool isLast,
        IReadOnlySet<uint>? validFormIds = null,
        IReadOnlyDictionary<uint, uint>? remapTable = null)
    {
        _ = isLast; // Retained for signature stability; FNV has no NEXT separator.

        // xEdit-canonical FNV menu item: ITXT, RNAM (required STRING "Result Text"),
        // ANAM (required flags byte), INAM?, TNAM? (sub-menu TERM link), embedded script
        // block (required — SCHR always present), then CTDA conditions. The previous
        // emission wrote RNAM as a 4-byte FormID link (FNVEdit: "unused data in RNAM"),
        // put CTDAs before the script, added a NEXT separator FNV doesn't define, and
        // made ANAM/RNAM/SCHR conditional — FNVEdit flagged every terminal as
        // out-of-order and the engine's sequential reader misparsed the items.
        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ITXT", item.Text ?? string.Empty));

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("RNAM", item.ResultText ?? string.Empty));

        subs.Add(NewRecordSubrecords.EncodeByteSubrecord("ANAM", item.ActionType ?? 0));

        if (item.DisplayNoteFormId.HasValue)
        {
            var resolved = FormIdReferenceResolver.Resolve(item.DisplayNoteFormId.Value, validFormIds, remapTable);
            if (resolved.HasValue)
            {
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("INAM", resolved.Value));
            }
            else
            {
                warnings.Add(
                    $"TERM 0x{termFormId:X8} menu item '{item.Text ?? "(empty)"}' display-note link " +
                    $"0x{item.DisplayNoteFormId.Value:X8} does not resolve — dropped.");
            }
        }

        if (item.SubTerminal.HasValue)
        {
            var resolved = FormIdReferenceResolver.Resolve(item.SubTerminal.Value, validFormIds, remapTable);
            if (resolved.HasValue)
            {
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("TNAM", resolved.Value));
            }
            else
            {
                warnings.Add(
                    $"TERM 0x{termFormId:X8} menu item '{item.Text ?? "(empty)"}' sub-menu link " +
                    $"0x{item.SubTerminal.Value:X8} does not resolve — dropped.");
            }
        }

        if (item.ResultScript.HasValue
            && item.CompiledData is not { Length: > 0 } && string.IsNullOrEmpty(item.SourceText))
        {
            warnings.Add(
                $"TERM 0x{termFormId:X8} menu item '{item.Text ?? "(empty)"}' carries an external " +
                $"result-script link 0x{item.ResultScript.Value:X8}; FNV terminals only support " +
                "embedded result scripts — item emitted with an empty script block.");
        }

        // Embedded script block is REQUIRED per item — emit an empty SCHR when the proto
        // captured no bytecode/source.
        var emittedScript = EmitEmbeddedScriptBlock(subs, item, validFormIds, remapTable);

        // CTDA conditions (with optional CIS1/CIS2 string params) come AFTER the script
        // block. Conditions filter when the menu item is visible.
        foreach (var condition in item.Conditions)
        {
            subs.Add(new EncodedSubrecord("CTDA", InfoEncoder.BuildCtdaSubrecord(condition)));
            if (!string.IsNullOrEmpty(condition.Parameter1String))
            {
                subs.Add(NewRecordSubrecords.EncodeStringSubrecord("CIS1", condition.Parameter1String));
            }

            if (!string.IsNullOrEmpty(condition.Parameter2String))
            {
                subs.Add(NewRecordSubrecords.EncodeStringSubrecord("CIS2", condition.Parameter2String));
            }
        }

        return emittedScript;
    }

    private static bool EmitEmbeddedScriptBlock(
        List<EncodedSubrecord> subs,
        TerminalMenuItem item,
        IReadOnlySet<uint>? validFormIds = null,
        IReadOnlyDictionary<uint, uint>? remapTable = null)
    {
        var validation = InlineScriptReferenceValidator.Validate(
            item.CompiledData,
            item.SourceText,
            item.Variables,
            item.ReferencedObjects,
            "MenuItem",
            validFormIds,
            remapTable,
            item.IsIncompleteExecutableBundle,
            item.IsDmpDerived,
            item.SourceTextOrigin,
            item.DecompiledText,
            item.IsBigEndianBytecode);
        if (!validation.IsSafe)
        {
            return false;
        }

        var compiledSize = item.CompiledData?.Length ?? 0;
        var refCount = (uint)validation.ResolvedReferences.Length;
        var variableCount = (uint)item.Variables.Count;

        // SCHR uses the canonical serialized ESM layout, which differs from the runtime
        // SCRIPT_HEADER struct: padding(4), RefCount(4), CompiledSize(4), VariableCount(4),
        // Type(2), Flags(2). Embedded TERM result scripts are enabled Object scripts, including
        // required empty blocks; this matches retail TERM records.
        var schr = new byte[20];
        SubrecordEncoder.WriteUInt32(schr, 4, refCount);
        SubrecordEncoder.WriteUInt32(schr, 8, (uint)compiledSize);
        SubrecordEncoder.WriteUInt32(schr, 12, variableCount);
        SubrecordEncoder.WriteUInt16(schr, 16, 0); // Type = Object
        SubrecordEncoder.WriteUInt16(schr, 18, 0x0001); // Flags = Enabled
        subs.Add(new EncodedSubrecord("SCHR", schr));

        if (item.CompiledData is { Length: > 0 } compiled)
        {
            // BE bytecode from DMP-sourced TERM menu items must be swapped to LE for the
            // PC engine — same reason as SCPT/INFO. See ScptEncoder.cs.
            var scda = item.IsBigEndianBytecode
                ? ScriptBytecodeEndianConverter.SwapBigEndianToLittleEndian(
                    compiled, item.Variables, validation.ResolvedReferences)
                : compiled;
            subs.Add(new EncodedSubrecord("SCDA", scda));
        }

        if (!string.IsNullOrEmpty(validation.SourceTextForEmission))
        {
            subs.Add(NewRecordSubrecords.EncodeGameTextSubrecord(
                "SCTX", validation.SourceTextForEmission));
        }

        foreach (var variable in item.Variables)
        {
            var slsd = new byte[24];
            SubrecordEncoder.WriteUInt32(slsd, 0, variable.Index);
            slsd[16] = variable.Type;
            subs.Add(new EncodedSubrecord("SLSD", slsd));
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("SCVR", variable.Name ?? string.Empty));
        }

        foreach (var refFormId in validation.ResolvedReferences)
        {
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

        return true;
    }
}
