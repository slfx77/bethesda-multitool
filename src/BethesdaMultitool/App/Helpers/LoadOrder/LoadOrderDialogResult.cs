using System.Collections.ObjectModel;

namespace BethesdaMultitool;

/// <summary>Outcome chosen by the user when dismissing the load-order picker dialog.</summary>
internal enum LoadOrderDialogAction
{
    Cancel,
    Apply,
    ClearAll
}

/// <summary>Result of the load-order picker dialog, including the chosen primary source when offered.</summary>
internal sealed record LoadOrderDialogResult(
    LoadOrderDialogAction Action,
    ObservableCollection<LoadOrderEntry> Entries,
    string? SubtitleCsvPath,
    string? PrimaryFilePath = null);
