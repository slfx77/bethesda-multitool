using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;

namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Terminal menu item from ITXT/CTDA/RNAM/SCHR/SCDA/SCTX subrecords.
///     Can carry either an external link (ResultScript FormID, SubTerminal FormID) when
///     populated from runtime DMP, or embedded result-script bytecode when parsed from
///     the on-disk ESM. The encoder emits the embedded form when CompiledData is present,
///     otherwise falls back to RNAM with the linked FormID. Conditions (CTDA) filter when
///     the menu item is visible in-game.
/// </summary>
public record TerminalMenuItem
{
    /// <summary>Menu item text.</summary>
    public string? Text { get; init; }

    /// <summary>Result script FormID (when linked externally; null if embedded).</summary>
    public uint? ResultScript { get; init; }

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
}
