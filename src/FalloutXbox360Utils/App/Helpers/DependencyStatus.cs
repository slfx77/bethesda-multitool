namespace FalloutXbox360Utils;

/// <summary>
///     Result of a dependency check with details about availability.
/// </summary>
public sealed record DependencyStatus
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required bool IsAvailable { get; init; }
    public string? Version { get; init; }
    public string? Path { get; init; }
    public string? DownloadUrl { get; init; }
    public string? InstallInstructions { get; init; }
}
