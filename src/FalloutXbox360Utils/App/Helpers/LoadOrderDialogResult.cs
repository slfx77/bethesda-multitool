using System.Collections.ObjectModel;

namespace FalloutXbox360Utils;

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
