using BethesdaMultitool.Core.Formats.Esm.Models;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing;

/// <summary>
///     Synchronously turns typed-parser record reads into bounded, monotonic progress updates.
///     Record payload bytes are the work unit, and a descriptor is counted only on its first read:
///     several handlers commonly revisit the same record while building different semantic indexes.
/// </summary>
internal sealed class RecordParseProgressReporter
{
    // The two installed-Skyrim session-B residual runs (waterfall-ref + ice-ref) across the base
    // game and four DLC plugins put schema decode at 49.7% of aggregate semantic-parse time
    // (25.28s / 50.87s). Individual plugins vary, so a stable 50/50 stage split is more
    // representative than trying to tune per record mix.
    internal const int SchemaStageEndPercent = 50;
    internal const int LastWorkPercent = 99;

    private readonly bool _schemaPrimary;
    private readonly HashSet<RecordDescriptor> _seenRecords = [];
    private readonly IProgress<(int percent, string phase)> _sink;
    private readonly IProgress<(int percent, string phase)> _schemaSink;
    private readonly long _totalRecordBytes;
    private long _seenRecordBytes;
    private int _lastPercent = -1;
    private string _lastPhase = string.Empty;
    private string _schemaPhase = "Decoding records...";

    internal RecordParseProgressReporter(
        IProgress<(int percent, string phase)> sink,
        IReadOnlyList<DetectedMainRecord> records,
        bool schemaPrimary)
    {
        _sink = sink;
        _schemaPrimary = schemaPrimary;
        _schemaSink = new InlineProgress<(int percent, string phase)>(ReportSchemaProgress);

        // The scan can retain repeated references to the same descriptor. Use the same identity as
        // ObserveRecordRead so the denominator and numerator agree and progress cannot exceed 99.
        _totalRecordBytes = records
            .Select(RecordDescriptor.From)
            .Distinct()
            .Sum(static descriptor => descriptor.WorkBytes);
    }

    /// <summary>
    ///     Progress sink for the schema-primary decode. Its 0..100 domain occupies the initial
    ///     0..50 range; its internal "Complete" is deliberately replaced by the active decode label,
    ///     because only the complete semantic parse may announce final completion.
    /// </summary>
    internal IProgress<(int percent, string phase)> SchemaProgress => _schemaSink;

    /// <summary>
    ///     Installs the read observer for the typed pass. Call this only after the display-name
    ///     prescan, otherwise that preliminary whole-file read would make semantic progress jump.
    /// </summary>
    internal IDisposable BeginTypedRecordTracking(RecordParserContext context)
    {
        return context.ObserveRecordReads(ObserveRecordRead);
    }

    /// <summary>Reports one of the parser's existing semantic phase boundaries.</summary>
    internal void ReportPhase(int legacyPercent, string phase)
    {
        var clamped = Math.Clamp(legacyPercent, 0, 100);
        var typedRange = LastWorkPercent - SchemaStageEndPercent;
        var typedOffset = clamped == 0 ? 0 : (int)Math.Ceiling(clamped * typedRange / 100d);
        var mapped = _schemaPrimary ? SchemaStageEndPercent + typedOffset : clamped;

        Report(mapped, phase, forcePhase: true);
    }

    /// <summary>Emits the sole final 100% update.</summary>
    internal void Complete()
    {
        Report(100, "Complete", forcePhase: true);
    }

    private void ReportSchemaProgress((int percent, string phase) update)
    {
        var clamped = Math.Clamp(update.percent, 0, 100);
        var mapped = (int)Math.Floor(clamped * SchemaStageEndPercent / 100d);

        // SchemaDrivenRecordParser is also usable by itself and therefore reports Complete at 100.
        // In the combined parser that is only the end of stage one, so keep the last real decode label.
        if (!string.Equals(update.phase, "Complete", StringComparison.Ordinal))
        {
            _schemaPhase = update.phase;
        }

        Report(mapped, _schemaPhase, forcePhase: true);
    }

    private void ObserveRecordRead(DetectedMainRecord record)
    {
        var descriptor = RecordDescriptor.From(record);
        if (!_seenRecords.Add(descriptor))
        {
            return;
        }

        _seenRecordBytes += descriptor.WorkBytes;
        if (_totalRecordBytes <= 0)
        {
            return;
        }

        var rangeStart = _schemaPrimary ? SchemaStageEndPercent : 0;
        var fraction = Math.Min(1d, (double)_seenRecordBytes / _totalRecordBytes);
        var mapped = rangeStart +
                     (int)Math.Floor(fraction * (LastWorkPercent - rangeStart));

        // Integer-percent throttling caps record-driven traffic at 99 notifications even for a
        // million-record plugin. Phase boundaries are still forced so their labels stay visible.
        Report(mapped, _lastPhase, forcePhase: false);
    }

    private void Report(int percent, string phase, bool forcePhase)
    {
        var monotonicPercent = Math.Max(_lastPercent, Math.Clamp(percent, 0, 100));
        if (!forcePhase && monotonicPercent <= _lastPercent)
        {
            return;
        }

        if (monotonicPercent == _lastPercent && string.Equals(phase, _lastPhase, StringComparison.Ordinal))
        {
            return;
        }

        _lastPercent = monotonicPercent;
        _lastPhase = phase;
        _sink.Report((monotonicPercent, phase));
    }

    private readonly record struct RecordDescriptor(
        long Offset,
        int HeaderSize,
        uint DataSize,
        uint FormId,
        string RecordType)
    {
        internal long WorkBytes => Math.Max(1L, DataSize);

        internal static RecordDescriptor From(DetectedMainRecord record)
        {
            return new RecordDescriptor(
                record.Offset,
                record.HeaderSize,
                record.DataSize,
                record.FormId,
                record.RecordType);
        }
    }

    /// <summary>
    ///     Unlike <see cref="Progress{T}"/>, this adapter does not post through a synchronization
    ///     context: scaling and monotonicity are applied in the exact parser call that reports them.
    /// </summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value)
        {
            report(value);
        }
    }
}
