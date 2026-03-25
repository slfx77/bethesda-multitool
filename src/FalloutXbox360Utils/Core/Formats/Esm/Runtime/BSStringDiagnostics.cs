using System.Collections.Concurrent;
using static FalloutXbox360Utils.Core.Formats.Esm.RuntimeMemoryContext;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Collects BSStringT read failure statistics across all runtime struct readers.
///     Thread-safe. Call <see cref="Reset" /> before a scan, then <see cref="GetReport" /> after.
/// </summary>
internal static class BSStringDiagnostics
{
    private const int MaxSamplesPerBucket = 3;
    private static readonly ConcurrentDictionary<(string FieldName, BSStringFailure Reason), int> _counts = new();

    /// <summary>
    ///     Samples of raw BSStringT struct data for each (field, failure) pair.
    ///     Captures up to <see cref="MaxSamplesPerBucket" /> examples for diagnostic display.
    /// </summary>
    private static readonly ConcurrentDictionary<(string FieldName, BSStringFailure Reason), ConcurrentBag<DiagSample>>
        _samples = new();

    public static void Record(string fieldName, BSStringFailure failure)
    {
        _counts.AddOrUpdate((fieldName, failure), 1, (_, c) => c + 1);
    }

    /// <summary>
    ///     Record a failure with raw BSStringT values for sample collection.
    /// </summary>
    public static void RecordWithSample(string fieldName, BSStringFailure failure, DiagSample sample)
    {
        _counts.AddOrUpdate((fieldName, failure), 1, (_, c) => c + 1);

        if (failure == BSStringFailure.None) return;

        var bag = _samples.GetOrAdd((fieldName, failure), _ => []);
        if (bag.Count < MaxSamplesPerBucket)
        {
            bag.Add(sample);
        }
    }

    public static void Reset()
    {
        _counts.Clear();
        _samples.Clear();
    }

    /// <summary>
    ///     Returns a human-readable diagnostic report of BSStringT read outcomes.
    /// </summary>
    public static string GetReport(bool includeSamples = false)
    {
        if (_counts.IsEmpty)
        {
            return "No BSStringT reads recorded.";
        }

        var lines = new List<string>();
        var byField = _counts
            .GroupBy(kv => kv.Key.FieldName)
            .OrderBy(g => g.Key);

        foreach (var fieldGroup in byField)
        {
            var total = fieldGroup.Sum(kv => kv.Value);
            var succeeded = fieldGroup
                .Where(kv => kv.Key.Reason == BSStringFailure.None)
                .Sum(kv => kv.Value);
            var failed = total - succeeded;

            lines.Add($"  {fieldGroup.Key}: {succeeded}/{total} succeeded ({failed} failed)");

            foreach (var kv in fieldGroup.Where(kv => kv.Key.Reason != BSStringFailure.None)
                         .OrderByDescending(kv => kv.Value))
            {
                lines.Add($"    {kv.Key.Reason}: {kv.Value}");

                if (!includeSamples) continue;

                if (_samples.TryGetValue(kv.Key, out var bag))
                {
                    foreach (var s in bag)
                    {
                        var typeCode = RuntimeBuildOffsets.GetRecordTypeCode(s.FormType) ?? $"0x{s.FormType:X2}";
                        var id = s.EditorId != null
                            ? $"[{typeCode}] {s.EditorId} (0x{s.FormId:X8})"
                            : $"[{typeCode}] 0x{s.FormId:X8}";
                        var detail = kv.Key.Reason switch
                        {
                            BSStringFailure.LengthTooLarge =>
                                $"ptr=0x{s.Pointer:X8}, len={s.Length}, raw=[{s.RawHex}]",
                            BSStringFailure.InvalidPointer =>
                                $"ptr=0x{s.Pointer:X8}, len={s.Length}",
                            BSStringFailure.InvalidAscii =>
                                $"ptr=0x{s.Pointer:X8}, len={s.Length}, data=[{s.PartialData}]",
                            BSStringFailure.VaNotMapped =>
                                $"ptr=0x{s.Pointer:X8}, len={s.Length}",
                            BSStringFailure.NullPointer =>
                                $"raw=[{s.RawHex}]",
                            BSStringFailure.ZeroLength =>
                                $"ptr=0x{s.Pointer:X8}, raw=[{s.RawHex}]",
                            _ => $"offset=0x{s.TesFormOffset:X}, fieldOff={s.FieldOffset}"
                        };
                        lines.Add($"      {id}: {detail}");
                    }
                }
            }
        }

        return string.Join("\n", lines);
    }

    internal record DiagSample(
        uint FormId,
        string? EditorId,
        byte FormType,
        long TesFormOffset,
        int FieldOffset,
        uint Pointer,
        ushort Length,
        string? RawHex,
        string? PartialData);
}
