namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Scanning;

/// <summary>
///     An animation group discovered in a memory dump via RTTI-based scanning.
/// </summary>
public record DiscoveredAnimation
{
    /// <summary>File offset of the TESAnimGroup struct in the dump.</summary>
    public long FileOffset { get; init; }

    /// <summary>Animation group type enum value (0-255).</summary>
    public int GroupType { get; init; }

    /// <summary>Human-readable animation group type name.</summary>
    public required string GroupTypeName { get; init; }

    /// <summary>Animation name string from pParentName pointer, if resolvable.</summary>
    public string? Name { get; init; }

    /// <summary>Number of keyframes in this animation.</summary>
    public int FrameCount { get; init; }

    /// <summary>Number of associated sound events.</summary>
    public int SoundCount { get; init; }
}
