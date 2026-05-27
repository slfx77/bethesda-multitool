using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Script;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

/// <summary>
///     Encodes a <see cref="TerminalRecord" /> (TERM) as PC-format subrecord bytes.
///     Emits EDID + OBND? + FULL? + MODL? + SCRI? + DESC? + SNAM? + PNAM? +
///     DNAM(4B Difficulty + Flags + ServerType + Unused) +
///     per menu item: ITXT + (RNAM or embedded SCHR+SCDA?+SCTX?+SCRO*+SCRV*) + NEXT separator.
///     Override path is a no-op.
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
        // ITXT — menu-item display text (may be empty if model only carries linkage).
        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ITXT", item.Text ?? string.Empty));

        if (item.ActionType.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeByteSubrecord("ANAM", item.ActionType.Value));
        }

        // CTDA conditions (with optional CIS1/CIS2 string params) come between ITXT and the
        // result-script block per fopdoc. Conditions filter when the menu item is visible.
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

        if (item.CompiledData is { Length: > 0 } || !string.IsNullOrEmpty(item.SourceText))
        {
            // Embedded result-script block — same SCHR/SCDA/SCTX/SCRO/SCRV layout as INFO.
            EmitEmbeddedScriptBlock(subs, item, validFormIds, remapTable);
        }
        else
        {
            // External link via RNAM. ResultScript takes precedence when both are present.
            // The RNAM target must resolve through the alias table the same way SCROs do —
            // proto-only result scripts that the converter has reallocated would otherwise
            // dangle.
            var linkFormId = item.ResultScript ?? item.SubTerminal;
            if (linkFormId.HasValue)
            {
                var resolved = FormIdReferenceResolver.Resolve(linkFormId.Value, validFormIds, remapTable)
                               ?? linkFormId.Value;
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("RNAM", resolved));
            }
            else
            {
                warnings.Add(
                    $"TERM 0x{termFormId:X8} menu item '{item.Text ?? "(empty)"}' has neither embedded " +
                    "script bytecode nor an external link — item emitted with no action.");
            }
        }

        // NEXT separator after every menu item except the last. Mirrors fopdoc convention.
        if (!isLast)
        {
            subs.Add(new EncodedSubrecord("NEXT", []));
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
