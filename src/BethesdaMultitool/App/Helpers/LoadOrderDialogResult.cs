using System.Collections.ObjectModel;

namespace BethesdaMultitool;

internal enum LoadOrderDialogAction
{
    Cancel,
    Apply,
    ClearAll
}

internal sealed record LoadOrderDialogResult(
    LoadOrderDialogAction Action,
    ObservableCollection<LoadOrderEntry> Entries,
    string? SubtitleCsvPath);
