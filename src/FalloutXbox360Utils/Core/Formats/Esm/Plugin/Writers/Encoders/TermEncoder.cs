using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="TerminalRecord" /> (TERM) as PC-format subrecord bytes.
///     Emits EDID + FULL? + DESC? + DNAM(4B Difficulty + Flags + ServerType + Unused) +
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

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    /// <summary>
    ///     Encode a new TERM record from scratch in fopdoc canonical order:
    ///     EDID, FULL, DESC, DNAM, per menu item: ITXT, (RNAM or embedded script block), NEXT.
    ///     OBND/MODL/SCRI/SNAM/PNAM deferred.
    /// </summary>
    internal static EncodedRecord EncodeNew(TerminalRecord term)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(term.EditorId))
        {
            warnings.Add($"New TERM 0x{term.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", term.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(term.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", term.FullName));
        }

        if (!string.IsNullOrEmpty(term.HeaderText))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("DESC", term.HeaderText));
        }

        subs.Add(new EncodedSubrecord("DNAM", BuildDnamSubrecord(term)));

        for (var i = 0; i < term.MenuItems.Count; i++)
        {
            EmitMenuItem(subs, warnings, term.FormId, term.MenuItems[i], i == term.MenuItems.Count - 1);
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[] BuildDnamSubrecord(TerminalRecord term)
    {
        // DNAM (4 bytes): Difficulty, Flags, ServerType, Unused.
        // Model lacks ServerType — encode as 0 (default).
        return [term.Difficulty, term.Flags, 0, 0];
    }

    private static void EmitMenuItem(
        List<EncodedSubrecord> subs,
        List<string> warnings,
        uint termFormId,
        TerminalMenuItem item,
        bool isLast)
    {
        // ITXT — menu-item display text (may be empty if model only carries linkage).
        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ITXT", item.Text ?? string.Empty));

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
            EmitEmbeddedScriptBlock(subs, item);
        }
        else
        {
            // External link via RNAM. ResultScript takes precedence when both are present.
            var linkFormId = item.ResultScript ?? item.SubTerminal;
            if (linkFormId.HasValue)
            {
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("RNAM", linkFormId.Value));
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

    private static void EmitEmbeddedScriptBlock(List<EncodedSubrecord> subs, TerminalMenuItem item)
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
            subs.Add(new EncodedSubrecord("SCDA", compiled));
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
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SCRO", refFormId));
            }
        }
    }
}
