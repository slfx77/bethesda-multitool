using System.Collections.ObjectModel;

namespace BethesdaMultitool;

/// <summary>Outcome chosen by the user when dismissing the load-order picker dialog.</summary>
internal enum LoadOrderDialogAction
{
    Cancel,
    Apply,
    ClearAll
}

/// <summary>Result of the load-order picker dialog: the chosen action plus the edited entries and subtitle path.</summary>
internal sealed record LoadOrderDialogResult(
    LoadOrderDialogAction Action,
    ObservableCollection<LoadOrderEntry> Entries,
    string? SubtitleCsvPath);
