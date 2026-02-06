namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Reconstructed Message (MESG) from memory dump.
///     In-game popup messages, tutorials, and notifications.
/// </summary>
public record ReconstructedMessage
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }
    public string? Description { get; init; }

    /// <summary>Icon path from ICON subrecord.</summary>
    public string? Icon { get; init; }

    /// <summary>Associated quest FormID from QNAM subrecord.</summary>
    public uint QuestFormId { get; init; }

    /// <summary>Message flags from DNAM: 1=MessageBox, 2=AutoDisplay.</summary>
    public uint Flags { get; init; }

    /// <summary>Display time from TNAM subrecord.</summary>
    public uint DisplayTime { get; init; }

    /// <summary>Button text entries from ITXT subrecords.</summary>
    public List<string> Buttons { get; init; } = [];

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }

    public bool IsMessageBox => (Flags & 1) != 0;
    public bool IsAutoDisplay => (Flags & 2) != 0;
}
