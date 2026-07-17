using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Models;

/// <summary>
///     Terminal menu item from ITXT/RNAM/ANAM/INAM/TNAM/SCHR/SCDA/SCTX/CTDA subrecords.
///     RNAM is result text, INAM links a display NOTE, TNAM links a sub-terminal, and the
///     result script is embedded inline. Conditions (CTDA) filter when the item is visible.
/// </summary>
public record TerminalMenuItem
{
    /// <summary>Menu item text.</summary>
    public string? Text { get; init; }

    /// <summary>
    ///     Legacy diagnostic field retained for older callers. FNV terminal result scripts
    ///     are embedded and no on-disk TERM subrecord serializes an external script FormID.
    /// </summary>
    public uint? ResultScript { get; init; }

    /// <summary>Result text shown after the menu item is selected (RNAM).</summary>
    public string? ResultText { get; init; }

    /// <summary>NOTE displayed by the menu item (INAM), when present.</summary>
    public uint? DisplayNoteFormId { get; init; }

    /// <summary>Sub-terminal FormID (if this links to another terminal).</summary>
    public uint? SubTerminal { get; init; }

    /// <summary>Terminal item action/type byte from ANAM, when present.</summary>
    public byte? ActionType { get; init; }

    /// <summary>
    ///     CTDA conditions guarding this menu item's visibility. Multiple conditions ANDed
    ///     by default; the per-condition <see cref="Records.Quest.DialogueCondition.IsOr" />
    ///     flag flips the join to OR.
    /// </summary>
    public List<DialogueCondition> Conditions { get; init; } = [];

    /// <summary>Embedded result-script compiled bytecode (SCDA). Null when not embedded.</summary>
    public byte[]? CompiledData { get; init; }

    /// <summary>Embedded result-script source text (SCTX). Null when not embedded.</summary>
    public string? SourceText { get; init; }

    /// <summary>Decompiled embedded bytecode used only to prove captured SCTX correspondence.</summary>
    public string? DecompiledText { get; init; }

    /// <summary>Where the recovered SCTX came from within the current dump.</summary>
    public ScriptSourceTextOrigin SourceTextOrigin { get; init; }

    /// <summary>True for terminal script material recovered from a minidump.</summary>
    public bool IsDmpDerived { get; init; }

    /// <summary>Ordered embedded-script local table from SLSD/SCVR pairs.</summary>
    public List<ScriptVariableInfo> Variables { get; init; } = [];

    /// <summary>
    ///     FormIDs referenced by the embedded result script. High bit (0x80000000) flags
    ///     variable-index references (SCRV); otherwise these are SCRO FormIDs.
    /// </summary>
    public List<uint> ReferencedObjects { get; init; } = [];

    /// <summary>
    ///     True when <see cref="CompiledData" /> holds Xbox 360 (big-endian) bytecode and
    ///     must be byte-swapped before being emitted to a PC ESP. Set by parsers from the
    ///     containing record's endianness flag; false by default for tests and any LE source.
    /// </summary>
    public bool IsBigEndianBytecode { get; init; }

    /// <summary>
    ///     The runtime ResultScript declared executable content, but its fixed SCDA/SLSD/
    ///     SCRO/SCRV bundle was incomplete in this dump. Planner safety must retain the
    ///     master TERM or suppress a new one instead of emitting an enabled SCTX-only block.
    /// </summary>
    public bool IsIncompleteExecutableBundle { get; init; }
}
