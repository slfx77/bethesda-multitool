namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Terminal menu item from ITXT/RNAM/SCHR/SCDA/SCTX subrecords.
///     Can carry either an external link (ResultScript FormID, SubTerminal FormID) when
///     populated from runtime DMP, or embedded result-script bytecode when parsed from
///     the on-disk ESM. The encoder emits the embedded form when CompiledData is present,
///     otherwise falls back to RNAM with the linked FormID.
/// </summary>
public record TerminalMenuItem
{
    /// <summary>Menu item text.</summary>
    public string? Text { get; init; }

    /// <summary>Result script FormID (when linked externally; null if embedded).</summary>
    public uint? ResultScript { get; init; }

    /// <summary>Sub-terminal FormID (if this links to another terminal).</summary>
    public uint? SubTerminal { get; init; }

    /// <summary>Embedded result-script compiled bytecode (SCDA). Null when not embedded.</summary>
    public byte[]? CompiledData { get; init; }

    /// <summary>Embedded result-script source text (SCTX). Null when not embedded.</summary>
    public string? SourceText { get; init; }

    /// <summary>
    ///     FormIDs referenced by the embedded result script. High bit (0x80000000) flags
    ///     variable-index references (SCRV); otherwise these are SCRO FormIDs.
    /// </summary>
    public List<uint> ReferencedObjects { get; init; } = [];
}
