using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The outcome of <see cref="NifModelTextKeyEvents.Map" />: one Shared event per stored key in file order, with the key's
///     stored bytes beside it, or a reason the block stays native.
/// </summary>
internal sealed class NifModelTextKeyEventsResult
{
    private NifModelTextKeyEventsResult(
        IReadOnlyList<SceneAnimationEvent> events,
        IReadOnlyList<NifModelTextKeyRecord> keys,
        bool inlineStrings,
        NifModelTextKeyBlock block,
        int? blockedKeyIndex)
    {
        Events = events;
        Keys = keys;
        InlineStrings = inlineStrings;
        Block = block;
        BlockedKeyIndex = blockedKeyIndex;
    }

    /// <summary>The events, one per stored key, in file order; empty when blocked.</summary>
    public IReadOnlyList<SceneAnimationEvent> Events { get; }

    /// <summary>The stored keys behind <see cref="Events" />, index for index; empty when blocked.</summary>
    public IReadOnlyList<NifModelTextKeyRecord> Keys { get; }

    /// <summary>True when the labels are inline SizedStrings (before 20.1.0.1), false for string-table indices.</summary>
    public bool InlineStrings { get; }

    /// <summary>Why the block stays native; <see cref="NifModelTextKeyBlock.None" /> when every key mapped.</summary>
    public NifModelTextKeyBlock Block { get; }

    /// <summary>The file-order index of the key that blocked the mapping; null when not blocked or not key-specific.</summary>
    public int? BlockedKeyIndex { get; }

    /// <summary>True when the block stays native.</summary>
    public bool IsBlocked => Block != NifModelTextKeyBlock.None;

    /// <summary>A mapped block.</summary>
    /// <param name="events">One event per key, in file order.</param>
    /// <param name="keys">The stored keys, index for index.</param>
    /// <param name="inlineStrings">Whether the labels are inline.</param>
    /// <returns>The result.</returns>
    /// <exception cref="ArgumentException">The two lists differ in length.</exception>
    public static NifModelTextKeyEventsResult Mapped(
        IReadOnlyList<SceneAnimationEvent> events,
        IReadOnlyList<NifModelTextKeyRecord> keys,
        bool inlineStrings)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(keys);
        if (events.Count != keys.Count)
        {
            throw new ArgumentException("Every event needs exactly one stored key.", nameof(keys));
        }

        return new NifModelTextKeyEventsResult(events, keys, inlineStrings, NifModelTextKeyBlock.None, null);
    }

    /// <summary>A block that stays native.</summary>
    /// <param name="block">The reason; never <see cref="NifModelTextKeyBlock.None" />.</param>
    /// <param name="inlineStrings">Whether the labels are inline.</param>
    /// <param name="keyIndex">The key that blocked the mapping, when one did.</param>
    /// <returns>The result.</returns>
    public static NifModelTextKeyEventsResult Blocked(NifModelTextKeyBlock block, bool inlineStrings, int? keyIndex)
    {
        if (block == NifModelTextKeyBlock.None)
        {
            throw new ArgumentOutOfRangeException(nameof(block), block, "A blocked result needs a reason.");
        }

        return new NifModelTextKeyEventsResult([], [], inlineStrings, block, keyIndex);
    }
}
