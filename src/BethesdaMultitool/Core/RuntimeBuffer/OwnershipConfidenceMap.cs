namespace BethesdaMultitool.Core.RuntimeBuffer;

/// <summary>
///     Maps each <see cref="ClaimSource" /> to the <see cref="OwnershipConfidence" /> tier its
///     evidence actually supports. Every mapping carries its one-line rationale below; a source
///     with no stated rationale should not be here.
/// </summary>
public static class OwnershipConfidenceMap
{
    /// <summary>
    ///     The tier for <paramref name="source" />. Throws on an unmapped value rather than
    ///     silently bucketing a new strategy as trustworthy —
    ///     <c>OwnershipConfidenceMapTests.EveryClaimSource_MapsToATier</c> turns that throw into a
    ///     build-time failure when someone adds a <see cref="ClaimSource" /> and forgets this file.
    /// </summary>
    public static OwnershipConfidence For(ClaimSource source)
    {
        return source switch
        {
            // Read straight out of a parsed record's subrecord — the field is the subrecord.
            ClaimSource.RawRecordSubrecord => OwnershipConfidence.FieldNamed,

            // A PDB-declared field of a runtime struct, read at its declared offset.
            ClaimSource.RuntimeStructField => OwnershipConfidence.FieldNamed,

            // The string IS this form's EditorID, taken from the runtime EditorID table that
            // recovered the form in the first place.
            ClaimSource.RuntimeEditorId => OwnershipConfidence.FieldNamed,

            // Referrer reverse-mapped to a TESForm and validated on vtable + FormType + FormID,
            // with the BSStringT wrapper length agreeing with the string.
            ClaimSource.SecondPassReverse => OwnershipConfidence.FieldNamed,

            // Referrer's containing object identified by RTTI and the offset matched a declared
            // field of that class.
            ClaimSource.SecondPassVtable => OwnershipConfidence.FieldNamed,

            // The text equals a known EditorID / game setting / dialogue line exactly.
            ClaimSource.TextContentMatch => OwnershipConfidence.ExactTextMatch,

            // Same reverse lookup as SecondPassReverse but with BSStringT length validation
            // skipped, so it is deliberately ranked one tier below its strict sibling.
            ClaimSource.SecondPassReverseRelaxed => OwnershipConfidence.OwnerNamed,

            // Referrer address lies inside a known object's byte span: the owner is unambiguous,
            // the field is only a raw offset.
            ClaimSource.SecondPassContainment => OwnershipConfidence.OwnerNamed,

            // A manager/global singleton walker owns the string; the walker names the manager,
            // not the member.
            ClaimSource.ManagerGlobal => OwnershipConfidence.OwnerNamed,

            // Inferred purely from the string sitting 16 bytes past something that looks like a
            // TESForm vtable. Position only.
            ClaimSource.SecondPassCFormEditorIdPosition => OwnershipConfidence.Positional,

            _ => throw new ArgumentOutOfRangeException(nameof(source), source,
                "ClaimSource has no OwnershipConfidence mapping; add one in OwnershipConfidenceMap.")
        };
    }
}
