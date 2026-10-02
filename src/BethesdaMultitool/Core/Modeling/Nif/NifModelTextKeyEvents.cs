using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 5: a sequence's NiTextKeyExtraData keys, read through the slice-1 lossless view
///     (<see cref="NifTextKeyExtraDataView" />, <see cref="NifTextKeyView" />), become Shared
///     <see cref="SceneAnimationEvent" />s (plan section 1.5). Pure and synchronous.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>
///             One event per stored key, in FILE ORDER: no sort (equal times keep their stored order, and a time lower than
///             its predecessor stays where it is), no trim, no dedupe, no drop.
///         </item>
///         <item>
///             The time is the key's Float32 exactly as stored (negative times and -0 included). The text is the Latin-1
///             decoding of the raw label bytes, which maps each byte to one character, so CR LF survives, an empty label
///             gives an empty event, and a label holding two events ("a\r\nb\r\n") stays one event with that exact text.
///             A NULL string-table label (index -1) also gives an empty event; <see cref="NifModelTextKeyRecord.IsNullLabel" />
///             keeps the difference.
///         </item>
///         <item>
///             The renderer's <see cref="NifTextKeyReader" /> (ASCII decoding, a line split, trimming, dropping empty lines,
///             a sort) is not used; its behavior is what the tests use as the control.
///         </item>
///         <item>
///             The raw bytes of every key are returned beside the events for native state. A key Shared cannot hold (a
///             non-finite time) or a label that does not resolve keeps the whole block native (fail closed).
///         </item>
///         <item>
///             BSAnimNotes stay NativeOnly (<see cref="AnimNotes" />, <see cref="NifModelAnimNotes.Reason" />).
///         </item>
///     </list>
/// </remarks>
internal static class NifModelTextKeyEvents
{
    /// <summary>Maps every stored key of one NiTextKeyExtraData block to an event.</summary>
    /// <param name="view">The block's lossless view.</param>
    /// <param name="strings">The file's raw header string table (consulted only for indexed labels).</param>
    /// <returns>The events and their stored keys, or the reason the block stays native.</returns>
    public static NifModelTextKeyEventsResult Map(NifTextKeyExtraDataView view, NifHeaderStringTable strings)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(strings);
        if (!view.ConsumedExactly)
        {
            return NifModelTextKeyEventsResult.Blocked(NifModelTextKeyBlock.NotConsumedExactly, view.InlineStrings,
                null);
        }

        var events = new SceneAnimationEvent[view.Keys.Length];
        var keys = new NifModelTextKeyRecord[view.Keys.Length];
        for (var index = 0; index < view.Keys.Length; index++)
        {
            var key = view.Keys[index];
            var time = BitConverter.UInt32BitsToSingle(key.TimeBits);
            if (!float.IsFinite(time))
            {
                return NifModelTextKeyEventsResult.Blocked(NifModelTextKeyBlock.NonFiniteTime, view.InlineStrings,
                    index);
            }

            ReadOnlyMemory<byte> raw;
            var isNull = false;
            if (view.InlineStrings)
            {
                raw = key.InlineLabel;
            }
            else if (!NifAnimationStrings.TryGetRaw(strings, key.LabelIndex, out raw, out isNull))
            {
                return NifModelTextKeyEventsResult.Blocked(NifModelTextKeyBlock.LabelOutOfRange, view.InlineStrings,
                    index);
            }

            events[index] = new SceneAnimationEvent(time, Encoding.Latin1.GetString(raw.Span));
            keys[index] = new NifModelTextKeyRecord(key.TimeBits, key.LabelIndex, raw, isNull);
        }

        return NifModelTextKeyEventsResult.Mapped(events, keys, view.InlineStrings);
    }

    /// <summary>
    ///     The BSAnimNotes a sequence references, which stay NativeOnly: its anim-note array (BS above 28) or its single
    ///     Anim Notes ref (BS 24 to 28), exactly as stored; <see cref="NifModelAnimNotes.None" /> when the stream stores
    ///     neither.
    /// </summary>
    /// <param name="sequence">The sequence's lossless view.</param>
    /// <returns>The native-only anim notes.</returns>
    public static NifModelAnimNotes AnimNotes(NifControllerSequenceView sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        if (sequence.AnimNoteArrayRefs is { } array)
        {
            return new NifModelAnimNotes(array.ToArray(), true);
        }

        return sequence.AnimNotesRef is { } single ? new NifModelAnimNotes([single], false) : NifModelAnimNotes.None;
    }
}
