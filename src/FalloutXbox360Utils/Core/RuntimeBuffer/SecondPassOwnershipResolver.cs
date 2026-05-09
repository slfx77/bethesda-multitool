using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.RuntimeBuffer;

/// <summary>
///     Second-pass ownership resolution for strings that remain ReferencedOwnerUnknown
///     after the initial claim-building pass. Uses three strategies:
///     1. BSStringT reverse lookup — validates BSStringT wrapper at referrer, then
///     reverse-maps field offset to a TESForm instance (with relaxed fallback).
///     2. Vtable-based reverse lookup — scans backwards from referrer to find a vtable,
///     resolves RTTI, and matches field offset to PDB layout.
///     3. EditorID text-content matching — matches string text against known EditorIDs.
/// </summary>
internal sealed class SecondPassOwnershipResolver
{
    /// <summary>
    ///     Sorted distinct field offsets from _bsStringTFieldIndex, used to avoid full
    ///     dictionary iteration in TryTESFormReverseLookup. For each unique offset we
    ///     compute one candidate base VA, peek the formType byte, then do a direct
    ///     dictionary lookup instead of scanning all entries.
    /// </summary>
    private readonly int[] _bsStringTDistinctOffsets;

    /// <summary>
    ///     Pre-built lookup: maps (formType, bsStringTFieldOffset) to (recordCode, fieldLabel).
    /// </summary>
    private readonly Dictionary<(byte FormType, int FieldOffset), (string RecordCode, string FieldLabel)>
        _bsStringTFieldIndex;

    private readonly BufferAnalysisContext _ctx;

    private readonly OwnershipTextMatcher _textMatcher;
    private readonly OwnershipVtableResolver _vtableResolver;

    public SecondPassOwnershipResolver(BufferAnalysisContext ctx)
    {
        _ctx = ctx;

        var (bsStringTFieldIndex, classNameFieldIndex, charPointerFieldIndex) =
            OwnershipFieldIndexBuilder.BuildFieldIndices();
        _bsStringTFieldIndex = bsStringTFieldIndex;
        _bsStringTDistinctOffsets = bsStringTFieldIndex.Keys
            .Select(k => k.FieldOffset)
            .Distinct()
            .OrderBy(o => o)
            .ToArray();

        var niObjectFieldIndex = OwnershipFieldIndexBuilder.BuildNiObjectFieldIndex();
        _vtableResolver = new OwnershipVtableResolver(
            ctx, classNameFieldIndex, charPointerFieldIndex, niObjectFieldIndex);
        _textMatcher = new OwnershipTextMatcher(ctx);
    }

    /// <summary>
    ///     Run all second-pass strategies on ReferencedOwnerUnknown hits.
    ///     Reclassifies matching hits to Owned with appropriate ClaimSource.
    /// </summary>
    internal void Resolve(RuntimeStringOwnershipAnalysis analysis)
    {
        if (analysis.ReferencedOwnerUnknownHits.Count == 0)
        {
            return;
        }

        var promoted = new List<RuntimeStringHit>();

        foreach (var hit in analysis.ReferencedOwnerUnknownHits)
        {
            var claim = TryResolveViaReferrers(hit);

            // Text-content matching strategies (if pointer strategies didn't match)
            claim ??= _textMatcher.TryEditorIdTextMatch(hit);
            claim ??= _textMatcher.TryGameSettingTextMatch(hit);
            claim ??= _textMatcher.TryDialogueTextMatch(hit);

            // Content-based file path matching: asset paths with known extensions
            claim ??= OwnershipTextMatcher.TryAssetPathContentMatch(hit);

            // Low-priority fallback: cFormEditorID at +16 for EditorId strings
            // near any TESForm vtable. Runs last since TESForms are densely packed.
            claim ??= _textMatcher.TryCFormEditorIdFallback(hit);

            if (claim == null)
            {
                continue;
            }

            hit.OwnershipStatus = RuntimeStringOwnershipStatus.Owned;
            hit.OwnerResolution = new RuntimeStringOwnerResolution
            {
                OwnerKind = claim.OwnerKind,
                OwnerName = claim.OwnerName,
                OwnerFormId = claim.OwnerFormId,
                OwnerFileOffset = claim.OwnerFileOffset,
                ReferrerVa = hit.OwnerResolution?.ReferrerVa,
                ReferrerFileOffset = hit.OwnerResolution?.ReferrerFileOffset,
                ReferrerContext = hit.OwnerResolution?.ReferrerContext,
                ClaimSource = claim.ClaimSource,
                OwnerRecordType = claim.OwnerRecordType,
                OwnerFieldOrSubrecord = claim.OwnerFieldOrSubrecord,
                AllReferrers = hit.OwnerResolution?.AllReferrers
            };
            promoted.Add(hit);

            analysis.ClaimSourceCounts.TryGetValue(claim.ClaimSource, out var srcCount);
            analysis.ClaimSourceCounts[claim.ClaimSource] = srcCount + 1;
        }

        // Move promoted hits from ReferencedOwnerUnknown to Owned
        foreach (var hit in promoted)
        {
            analysis.ReferencedOwnerUnknownHits.Remove(hit);
            analysis.OwnedHits.Add(hit);

            // Update status counts
            analysis.StatusCounts.TryGetValue(RuntimeStringOwnershipStatus.ReferencedOwnerUnknown,
                out var unknownCount);
            analysis.StatusCounts[RuntimeStringOwnershipStatus.ReferencedOwnerUnknown] =
                Math.Max(0, unknownCount - 1);

            analysis.StatusCounts.TryGetValue(RuntimeStringOwnershipStatus.Owned, out var ownedCount);
            analysis.StatusCounts[RuntimeStringOwnershipStatus.Owned] = ownedCount + 1;
        }
    }

    /// <summary>
    ///     Try all referrers (not just the first) for BSStringT and vtable strategies.
    /// </summary>
    private RuntimeStringOwnershipClaim? TryResolveViaReferrers(RuntimeStringHit hit)
    {
        if (hit.OwnerResolution == null)
        {
            return null;
        }

        // Build the list of referrers to try
        var allReferrers = hit.OwnerResolution.AllReferrers;

        if (allReferrers is { Count: > 0 })
        {
            // Try each referrer for both strategies
            foreach (var (fileOffset, va, _) in allReferrers)
            {
                if (va < 0 || va > uint.MaxValue)
                {
                    continue;
                }

                var claim = TryBSStringTReverseLookup(hit, fileOffset, (uint)va);
                claim ??= _vtableResolver.TryVtableReverseLookup(hit, fileOffset, (uint)va);

                if (claim != null)
                {
                    return claim;
                }
            }

            return null;
        }

        // Fall back to single referrer (backward compat)
        if (hit.OwnerResolution.ReferrerFileOffset == null || hit.OwnerResolution.ReferrerVa == null)
        {
            return null;
        }

        var singleFileOffset = hit.OwnerResolution.ReferrerFileOffset.Value;
        var singleVa = (uint)hit.OwnerResolution.ReferrerVa.Value;

        var result = TryBSStringTReverseLookup(hit, singleFileOffset, singleVa);
        result ??= _vtableResolver.TryVtableReverseLookup(hit, singleFileOffset, singleVa);
        return result;
    }

    #region Strategy 1: BSStringT Reverse TESForm Lookup

    private RuntimeStringOwnershipClaim? TryBSStringTReverseLookup(
        RuntimeStringHit hit, long referrerFileOffset, uint referrerVa)
    {
        // Validate BSStringT wrapper: read 4 bytes after the char* pointer
        // BSStringT layout: [4B char* VA] [2B uint16 length] [2B unused]
        var bsStringTValid = false;
        if (referrerFileOffset + 8 <= _ctx.FileSize)
        {
            var lengthBytes = new byte[4];
            _ctx.Accessor.ReadArray(referrerFileOffset + 4, lengthBytes, 0, 4);
            var storedLength = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes.AsSpan(0, 2));
            bsStringTValid = storedLength == hit.Text.Length || storedLength == hit.Text.Length + 1;
        }

        // Fast path: BSStringT validated — try BSStringT field index
        if (bsStringTValid)
        {
            var claim = TryTESFormReverseLookup(hit, referrerVa, _bsStringTFieldIndex);
            if (claim != null)
            {
                return claim;
            }
        }

        // Relaxed fallback: skip BSStringT validation, try TESForm header validation directly.
        // This catches raw char* fields (Script.m_text, ActorValueInfo.sScriptName, etc.)
        // and cases where the BSStringT length field is corrupted or zero'd.
        return TryTESFormReverseLookup(hit, referrerVa, _bsStringTFieldIndex, true);
    }

    /// <summary>
    ///     Try to reverse-map a referrer VA to a TESForm instance using the field index.
    ///     When <paramref name="relaxed" /> is true, BSStringT length validation was skipped
    ///     so we only match if the triple validation (vtable + FormType + FormID) passes.
    ///     Uses pre-built distinct offset array: for each unique offset, peeks the formType
    ///     byte at the candidate base, then does a direct dictionary lookup instead of
    ///     iterating the entire field index.
    /// </summary>
    private RuntimeStringOwnershipClaim? TryTESFormReverseLookup(
        RuntimeStringHit hit, uint referrerVa,
        Dictionary<(byte FormType, int FieldOffset), (string RecordCode, string FieldLabel)> fieldIndex,
        bool relaxed = false)
    {
        foreach (var fieldOffset in _bsStringTDistinctOffsets)
        {
            if (referrerVa < (uint)fieldOffset)
            {
                continue;
            }

            var candidateBaseVa = referrerVa - (uint)fieldOffset;
            var candidateBaseFileOffset = _ctx.VaToFileOffset(candidateBaseVa);
            if (candidateBaseFileOffset == null)
            {
                continue;
            }

            // Peek the formType byte at the candidate base (+4) before full validation.
            // This avoids the heavier vtable + FormID checks for non-matching types.
            if (candidateBaseFileOffset.Value + 5 > _ctx.FileSize)
            {
                continue;
            }

            var formTypeBuf = new byte[1];
            _ctx.Accessor.ReadArray(candidateBaseFileOffset.Value + 4, formTypeBuf, 0, 1);
            var candidateFormType = formTypeBuf[0];

            if (!fieldIndex.TryGetValue((candidateFormType, fieldOffset), out var match))
            {
                continue;
            }

            if (!ValidateTESFormHeader(candidateBaseFileOffset.Value, candidateFormType,
                    out var candidateFormId))
            {
                continue;
            }

            // When relaxed, require a non-zero FormID (tighter validation to compensate for missing BSStringT check)
            if (relaxed && candidateFormId == 0)
            {
                continue;
            }

            return new RuntimeStringOwnershipClaim(
                hit.FileOffset,
                hit.VirtualAddress,
                relaxed ? "SecondPassReverseRelaxed" : "SecondPassReverse",
                $"{match.RecordCode} [{candidateFormId:X8}]",
                candidateFormId,
                candidateBaseFileOffset.Value,
                ClaimSource.SecondPassReverse,
                match.RecordCode,
                match.FieldLabel);
        }

        return null;
    }

    /// <summary>
    ///     Validate a TESForm header at the given file offset.
    ///     Checks vtable in module range, FormType match, and FormID validity.
    /// </summary>
    private bool ValidateTESFormHeader(long fileOffset, byte expectedFormType, out uint formId)
    {
        formId = 0;
        if (fileOffset + 16 > _ctx.FileSize)
        {
            return false;
        }

        var headerBytes = new byte[16];
        _ctx.Accessor.ReadArray(fileOffset, headerBytes, 0, 16);

        var vtablePtr = BinaryPrimitives.ReadUInt32BigEndian(headerBytes.AsSpan(0, 4));
        if (!Xbox360MemoryUtils.IsModulePointer(vtablePtr))
        {
            return false;
        }

        var candidateFormType = headerBytes[4];
        if (candidateFormType != expectedFormType)
        {
            return false;
        }

        formId = BinaryPrimitives.ReadUInt32BigEndian(headerBytes.AsSpan(12, 4));
        return formId > 0 && formId <= 0x00FFFFFF;
    }

    #endregion
}
