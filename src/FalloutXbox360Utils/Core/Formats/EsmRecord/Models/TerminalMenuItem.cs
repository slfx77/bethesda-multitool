namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Terminal menu item from ITXT/RNAM subrecords.
/// </summary>
public record TerminalMenuItem
{
    /// <summary>Menu item text.</summary>
    public string? Text { get; init; }

    /// <summary>Result script FormID.</summary>
    public uint? ResultScript { get; init; }

    /// <summary>Sub-terminal FormID (if this links to another terminal).</summary>
    public uint? SubTerminal { get; init; }
}
