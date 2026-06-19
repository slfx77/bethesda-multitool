using System.Diagnostics;

namespace BethesdaMultitool.Core.Orchestration;

/// <summary>
///     A composite per-frame budget for upload/build pumps: item count, byte total, and optional
///     wall-clock ceiling, consulted <i>before</i> each unit of work starts. The first item of a
///     frame is always permitted — even when it alone exceeds the budget — so a single oversized
///     item can never deadlock; it just gets a frame to itself.
///     <para>
///         Capture one at the start of a frame's pump, gate each unit on
///         <see cref="CanStart" />, and <see cref="Record" /> what ran. Mutable struct by design
///         (mirrors the existing per-frame budget structs); pass by <c>ref</c> if it must escape the
///         declaring scope.
///     </para>
/// </summary>
internal struct FrameBudget
{
    private readonly int _maxItems;
    private readonly long _maxBytes;
    private readonly long _deadlineTimestamp;
    private long _bytesUsed;
    private int _itemsUsed;

    /// <summary>A budget that never refuses work.</summary>
    public static FrameBudget Unlimited => new(int.MaxValue, long.MaxValue);

    public FrameBudget(int maxItems, long maxBytes, double maxMilliseconds = double.PositiveInfinity)
        : this(maxItems, maxBytes, maxMilliseconds, Stopwatch.GetTimestamp(), Stopwatch.Frequency)
    {
    }

    /// <summary>Deterministic constructor for tests.</summary>
    internal FrameBudget(int maxItems, long maxBytes, double maxMilliseconds, long startTimestamp, long frequency)
    {
        _maxItems = Math.Max(1, maxItems);
        _maxBytes = Math.Max(1L, maxBytes);
        _deadlineTimestamp = double.IsPositiveInfinity(maxMilliseconds)
            ? long.MaxValue
            : startTimestamp + (long)(maxMilliseconds / 1000.0 * frequency);
    }

    public readonly int ItemsUsed => _itemsUsed;

    public readonly long BytesUsed => _bytesUsed;

    /// <summary>
    ///     True if a unit of <paramref name="bytes" /> may start now: always true for the first unit
    ///     of the frame; otherwise only while item count, byte total, and wall clock all remain under
    ///     their ceilings.
    /// </summary>
    public readonly bool CanStart(long bytes = 0) => CanStartAt(Stopwatch.GetTimestamp(), bytes);

    /// <summary>Deterministic variant for tests.</summary>
    internal readonly bool CanStartAt(long timestamp, long bytes = 0)
    {
        if (_itemsUsed == 0)
        {
            return true;
        }

        return _itemsUsed < _maxItems &&
               _bytesUsed + Math.Max(1L, bytes) <= _maxBytes &&
               timestamp < _deadlineTimestamp;
    }

    /// <summary>Records a completed unit of <paramref name="bytes" /> against the budget.</summary>
    public void Record(long bytes = 0)
    {
        _bytesUsed += Math.Max(1L, bytes);
        _itemsUsed++;
    }
}
