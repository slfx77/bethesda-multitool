namespace FalloutXbox360Utils;

/// <summary>
///     Aggregated dependency check result for a tab.
/// </summary>
public sealed class TabDependencyResult
{
    public required string TabName { get; init; }
    public required List<DependencyStatus> Dependencies { get; init; }

    public bool AllAvailable => Dependencies.All(d => d.IsAvailable);
    public bool AnyAvailable => Dependencies.Any(d => d.IsAvailable);
    public IEnumerable<DependencyStatus> Missing => Dependencies.Where(d => !d.IsAvailable);
    public IEnumerable<DependencyStatus> Available => Dependencies.Where(d => d.IsAvailable);
}
