namespace BethesdaMultitool.Core.RuntimeBuffer;

/// <summary>
///     How a runtime string was attributed to an owning record/struct during ownership analysis.
///     <para>
///         Each value must be distinct evidence, because these are the buckets the ownership
///         report counts and <see cref="OwnershipConfidenceMap" /> tiers. Until 2026-09-04 three
///         of them lied: <see cref="SecondPassVtable" /> was emitted both by the field-named RTTI
///         hit and by the positional cFormEditorID guess, and neither
///         <see cref="SecondPassReverseRelaxed" /> nor <see cref="RuntimeEditorId" /> was assigned
///         anywhere — the relaxed reverse lookup reported itself as strict, and every runtime
///         EditorID claim fell through to <see cref="ManagerGlobal" /> by taking the record
///         default. Adding a value here means assigning it at exactly one site and mapping it in
///         <see cref="OwnershipConfidenceMap" />.
///     </para>
/// </summary>
public enum ClaimSource
{
    RawRecordSubrecord,
    RuntimeStructField,
    TextContentMatch,

    /// <summary>
    ///     The referrer's containing object was identified by RTTI and the field offset matched a
    ///     declared field of that class. Not the positional guess — that is
    ///     <see cref="SecondPassCFormEditorIdPosition" />.
    /// </summary>
    SecondPassVtable,
    SecondPassReverse,

    /// <summary>
    ///     <see cref="SecondPassReverse" /> with BSStringT length validation skipped, compensated
    ///     by requiring a non-zero FormID. Weaker, so it is counted apart from its strict sibling.
    /// </summary>
    SecondPassReverseRelaxed,

    /// <summary>
    ///     An EditorId string sat 16 bytes past something that resolved as a TESForm vtable. Purely
    ///     positional and the last strategy tried; TESForms are densely packed, so this is the
    ///     weakest claim the pipeline makes and it must never share a counter with a resolved one.
    /// </summary>
    SecondPassCFormEditorIdPosition,

    /// <summary>
    ///     A referrer address falls inside a known runtime object's byte span
    ///     <c>[TesFormPointer, +StructSize)</c>, so that object holds the pointer. Names the owner
    ///     and the raw field offset without needing a layout for the field itself.
    /// </summary>
    SecondPassContainment,
    ManagerGlobal,
    RuntimeEditorId
}
