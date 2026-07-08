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
///     TNAM?(sub-menu) + embedded SCHR+SCDA?+SCTX?+SCRO*+SCRV*(required) + CTDA*.
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
    ///     per menu item: ITXT, (RNAM or embedded script block), NEXT.
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
            EmitMenuItem(subs, warnings, term.FormId, term.MenuItems[i], i == term.MenuItems.Count - 1,
                validFormIds, remapTable);
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[] BuildDnamSubrecord(TerminalRecord term)
    {
        // DNAM (4 bytes): Difficulty, Flags, ServerType, Unused.
        return [term.Difficulty, term.Flags, term.ServerType, 0];
    }

    private static void EmitMenuItem(
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

        // No result text is captured in proto terminals; an empty string satisfies the
        // required-subrecord contract.
        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("RNAM", string.Empty));

        subs.Add(NewRecordSubrecords.EncodeByteSubrecord("ANAM", item.ActionType ?? 0));

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
        EmitEmbeddedScriptBlock(subs, item, validFormIds, remapTable);

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
    }

    private static void EmitEmbeddedScriptBlock(
        List<EncodedSubrecord> subs,
        TerminalMenuItem item,
        IReadOnlySet<uint>? validFormIds = null,
        IReadOnlyDictionary<uint, uint>? remapTable = null)
    {
        var compiledSize = item.CompiledData?.Length ?? 0;
        var refCount = (uint)item.ReferencedObjects.Count;

        // SCHR (20 bytes) per PDB SCRIPT_HEADER. Object-type script (not quest, not magic-effect).
        var schr = new byte[20];
        SubrecordEncoder.WriteUInt32(schr, 0, 0); // VariableCount — terminal scripts have no locals.
        SubrecordEncoder.WriteUInt32(schr, 4, refCount);
        SubrecordEncoder.WriteUInt32(schr, 8, (uint)compiledSize);
        SubrecordEncoder.WriteUInt32(schr, 12, 0); // LastVariableId
        schr[16] = 0; // IsQuestScript
        schr[17] = 0; // IsMagicEffectScript
        schr[18] = compiledSize > 0 ? (byte)1 : (byte)0; // IsCompiled
        subs.Add(new EncodedSubrecord("SCHR", schr));

        if (item.CompiledData is { Length: > 0 } compiled)
        {
            // BE bytecode from DMP-sourced TERM menu items must be swapped to LE for the
            // PC engine — same reason as SCPT/INFO. See ScptEncoder.cs.
            var scda = item.IsBigEndianBytecode
                ? ScriptBytecodeEndianConverter.SwapBigEndianToLittleEndian(
                    compiled, variables: null, item.ReferencedObjects)
                : compiled;
            subs.Add(new EncodedSubrecord("SCDA", scda));
        }

        if (!string.IsNullOrEmpty(item.SourceText))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("SCTX", item.SourceText));
        }

        foreach (var refFormId in item.ReferencedObjects)
        {
            if ((refFormId & 0x80000000) != 0)
            {
                var varIndex = refFormId & 0x7FFFFFFF;
                subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord("SCRV", varIndex));
            }
            else
            {
                // Same alias/validity check as SCPT and INFO SCROs — see ScptEncoder.EncodeNew.
                var resolved = FormIdReferenceResolver.Resolve(refFormId, validFormIds, remapTable) ?? 0u;
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SCRO", resolved));
            }
        }
    }
}
