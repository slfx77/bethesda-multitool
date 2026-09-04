namespace BethesdaMultitool.Core.RuntimeBuffer;

/// <summary>
///     How much an ownership claim can be trusted, independent of which strategy produced it.
///     <para>
///         Ownership analysis had nine <see cref="ClaimSource" /> values of wildly different
///         strength — an exact subrecord read and a "there is a vtable 16 bytes back" guess sat
///         side by side in one <c>Owned</c> total. Grouping the sources into tiers is what lets a
///         report say how much of the owned population rests on resolved pointers versus position,
///         and it is the hook a later ruling needs to promote only the strong tiers into emitted
///         records without reworking the claim type.
///     </para>
///     <para>
///         Ordered strongest to weakest, so <c>&lt;=</c> comparisons express "at least this
///         trustworthy".
///     </para>
/// </summary>
public enum OwnershipConfidence
{
    /// <summary>
    ///     A pointer was followed to a known owner and the specific field holding the string is
    ///     named. The claim says both who owns the string and what it means.
    /// </summary>
    FieldNamed,

    /// <summary>
    ///     The string's text exactly equals an entry in an inventory recovered from this dump
    ///     (EditorID, game setting, dialogue line). Identity is certain; the holder is inferred
    ///     from that identity rather than by following a pointer.
    /// </summary>
    ExactTextMatch,

    /// <summary>
    ///     A pointer was followed to a known owner, but the field is a raw offset or unknown —
    ///     or a validation step the strict sibling performs was skipped. Who holds the string is
    ///     evidenced; what it means is not.
    /// </summary>
    OwnerNamed,

    /// <summary>
    ///     The owner was inferred from the string sitting at a plausible offset near a structure,
    ///     not from a resolved pointer or matching text. Weakest tier; densely packed objects make
    ///     coincidence cheap.
    /// </summary>
    Positional
}
