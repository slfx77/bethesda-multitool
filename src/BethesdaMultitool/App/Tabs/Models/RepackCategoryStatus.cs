namespace BethesdaMultitool;

/// <summary>
///     Status of a category during repacking.
/// </summary>
public enum RepackCategoryStatus
{
    Pending,
    Processing,
    Complete,
    Skipped,
    Failed
}
