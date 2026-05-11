using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="TerminalRecord" /> (TERM) as PC-format subrecord bytes.
///     v7 simplified path: EDID + FULL? + DESC? + DNAM(4B Difficulty + Flags + ServerType + Unused) +
///     per menu item: ITXT + RNAM(4B FormID — ResultScript or SubTerminal).
///     Embedded per-menu result-script bytecode (SCHR/SCDA/SCTX) is deferred — surfaces a warning
///     for menu items that have neither ResultScript nor SubTerminal (would have been embedded).
///     Override path is a no-op.
///     DNAM layout per PDB TERMINAL_DATA (4 bytes):
///         byte Difficulty(0) + byte Flags(1) + byte ServerType(2) + byte Unused(3).
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
    ///     EDID, FULL, DESC, DNAM, per menu item: ITXT, RNAM.
    ///     OBND/MODL/SCRI/SNAM/PNAM and embedded scripts deferred to v8.
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

        foreach (var item in term.MenuItems)
        {
            EmitMenuItem(subs, warnings, term.FormId, item);
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
        TerminalMenuItem item)
    {
        // ITXT — menu-item display text (may be empty if model only carries linkage).
        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ITXT", item.Text ?? string.Empty));

        // RNAM — FormID linking to either a sub-terminal (TERM) or an external result script (SCPT).
        // ResultScript takes precedence when both are present (matches the model's parsing intent).
        var linkFormId = item.ResultScript ?? item.SubTerminal;
        if (linkFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("RNAM", linkFormId.Value));
        }
        else
        {
            warnings.Add(
                $"TERM 0x{termFormId:X8} menu item '{item.Text ?? "(empty)"}' has no ResultScript or " +
                "SubTerminal — embedded result-script bytecode is not supported in v7. Item emitted " +
                "without action linkage.");
        }
    }
}
