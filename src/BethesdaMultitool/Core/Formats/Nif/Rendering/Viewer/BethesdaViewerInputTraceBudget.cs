namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;

/// <summary>Bounded diagnostic admission; callers serialize access with their log emission.</summary>
internal sealed class BethesdaViewerInputTraceBudget
{
    internal const int DefaultRecordLimit = 256;
    private readonly int _recordLimit;
    private int _records;
    private bool _truncationReported;

    internal BethesdaViewerInputTraceBudget(int recordLimit = DefaultRecordLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(recordLimit);
        _recordLimit = recordLimit;
    }

    internal bool TryTake(out int sequence, out bool truncation)
    {
        truncation = false;
        if (_records < _recordLimit)
        {
            sequence = ++_records;
            return true;
        }

        sequence = _records + 1;
        if (_truncationReported) return false;
        _truncationReported = true;
        truncation = true;
        return true;
    }
}
