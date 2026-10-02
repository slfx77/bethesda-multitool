using System.Buffers.Binary;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Utils;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Runtime;

namespace BethesdaMultitool.Core.RuntimeBuffer;

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

    private readonly OwnershipContainmentResolver _containmentResolver;

    private readonly BufferAnalysisContext _ctx;
    private readonly RuntimeMemoryContext _memory;
    private readonly Dictionary<uint, List<RuntimeEditorIdEntry>> _entriesByBaseVa = new();

    private readonly OwnershipTextMatcher _textMatcher;
    private readonly OwnershipVtableResolver _vtableResolver;

    public SecondPassOwnershipResolver(BufferAnalysisContext ctx)
    {
        _ctx = ctx;
        _memory = new RuntimeMemoryContext(new MmfMemoryAccessor(ctx.Accessor), ctx.FileSize, ctx.MinidumpInfo);
        foreach (var entry in ctx.RuntimeEditorIds ?? [])
        {
            var layout = PdbStructLayouts.Get(entry.FormType);
            var pointer = entry.TesFormPointer ?? (entry.TesFormOffset is { } offset
                ? ctx.MinidumpInfo.FileOffsetToVirtualAddress(offset) : null);
            if (layout == null || !pointer.HasValue) continue;
            var interior = PdbStructLayouts.GetTesFormInteriorOffset(layout);
            var va = unchecked((uint)pointer.Value);
            if (va < interior) continue;
            var baseVa = va - (uint)interior;
            if (!_entriesByBaseVa.TryGetValue(baseVa, out var entries))
                _entriesByBaseVa[baseVa] = entries = [];
            entries.Add(entry);
        }

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
        _containmentResolver = new OwnershipContainmentResolver(ctx);
    }

    /// <summary>
    ///     Run all second-pass strategies on ReferencedOwnerUnknown hits.
    ///     Reclassifies matching hits to Owned with appropriate ClaimSource.
    /// </summary>
    internal void Resolve(RuntimeStringOwnershipAnalysis analysis, AnalysisStages.Stage? stage = null)
    {
        var hits = analysis.OwnedHits.Concat(analysis.ReferencedOwnerUnknownHits).ToArray();
        long processed = 0;
        analysis.OwnedHits.Clear();
        analysis.ReferencedOwnerUnknownHits.Clear();
        analysis.ClaimSourceCounts.Clear();
        foreach (var hit in hits)
        {
            _ctx.CancellationToken.ThrowIfCancellationRequested();
            stage?.Checkpoint(processed++, hits.Length);
            var claims = (hit.OwnerResolution?.Candidates ?? []).Select(c =>
                new RuntimeStringOwnershipClaim(hit.FileOffset, hit.VirtualAddress, c.OwnerKind, c.OwnerName,
                    c.OwnerFormId, c.OwnerFileOffset, c.ClaimSource, c.OwnerRecordType, c.OwnerFieldOrSubrecord,
                    c.ReferrerVa, c.ReferrerFileOffset, c.Validation)).ToList();
            claims.AddRange(ResolveViaReferrers(hit));
            claims.AddRange(_textMatcher.ResolveTextCandidates(hit));
            claims.AddRange(_containmentResolver.ResolveCandidates(hit));
            if (OwnershipTextMatcher.TryAssetPathContentMatch(hit) is { } path) claims.Add(path);
            if (_textMatcher.TryCFormEditorIdFallback(hit) is { } positional) claims.Add(positional);
            if (claims.Count > 0)
                hit.OwnerResolution = RuntimeStringOwnerResolution.FromClaims(claims, hit.OwnerResolution?.AllReferrers);
            if (hit.OwnerResolution?.HasValidatedOwner == true)
            {
                hit.OwnershipStatus = RuntimeStringOwnershipStatus.Owned;
                analysis.OwnedHits.Add(hit);
                var source = hit.OwnerResolution.ClaimSource!.Value;
                analysis.ClaimSourceCounts[source] = analysis.ClaimSourceCounts.GetValueOrDefault(source) + 1;
            }
            else
            {
                hit.OwnershipStatus = RuntimeStringOwnershipStatus.ReferencedOwnerUnknown;
                analysis.ReferencedOwnerUnknownHits.Add(hit);
            }
        }
        analysis.StatusCounts[RuntimeStringOwnershipStatus.Owned] = analysis.OwnedHits.Count;
        analysis.StatusCounts[RuntimeStringOwnershipStatus.ReferencedOwnerUnknown] = analysis.ReferencedOwnerUnknownHits.Count;
    }

    /// <summary>Retains candidates from every recorded pointer, including conflicting owners.</summary>
    private List<RuntimeStringOwnershipClaim> ResolveViaReferrers(RuntimeStringHit hit)
    {
        var claims = new List<RuntimeStringOwnershipClaim>();
        var resolution = hit.OwnerResolution;
        if (resolution == null) return claims;
        var referrers = resolution.AllReferrers;
        if (referrers is not { Count: > 0 } && resolution.ReferrerFileOffset is { } offset &&
            resolution.ReferrerVa is { } va)
            referrers = [(offset, va, resolution.ReferrerContext)];
        foreach (var (fileOffset, pointerVa, _) in referrers ?? [])
        {
            if (pointerVa < int.MinValue || pointerVa > uint.MaxValue) continue;
            var referrerVa = unchecked((uint)pointerVa);
            var fromForms = ResolveTesForms(hit, referrerVa);
            claims.AddRange(fromForms);
            claims.AddRange(_vtableResolver.ResolveVtableCandidates(hit, referrerVa).Select(vtable =>
                vtable with { ReferrerVa = pointerVa, ReferrerFileOffset = fileOffset,
                    Validation = "RTTI class and field offset" }));
        }
        return claims;
    }

    private List<RuntimeStringOwnershipClaim> ResolveTesForms(RuntimeStringHit hit, uint referrerVa)
    {
        var claims = new List<RuntimeStringOwnershipClaim>();
        // VA-safe wrapper reads cannot borrow adjacent file bytes from an unrelated captured region.
        var wrapper = _memory.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(referrerVa), 8);
        if (wrapper == null || hit.VirtualAddress is not { } stringVa ||
            BinaryPrimitives.ReadUInt32BigEndian(wrapper) != unchecked((uint)stringVa)) return claims;
        var length = BinaryPrimitives.ReadUInt16BigEndian(wrapper.AsSpan(4, 2));
        var strict = length == hit.Text.Length || length == hit.Text.Length + 1;
        foreach (var fieldOffset in _bsStringTDistinctOffsets)
        {
            if (referrerVa < fieldOffset) continue;
            var baseVa = referrerVa - (uint)fieldOffset;
            if (!_entriesByBaseVa.TryGetValue(baseVa, out var entries)) continue;
            foreach (var entry in entries)
            {
                // Raw FormType numbers drift between prototypes. Only the recovered, calibrated
                // identity selects a PDB class; the bytes must still match that entry's raw type/ID.
                if (!_bsStringTFieldIndex.TryGetValue((entry.FormType, fieldOffset), out var match)) continue;
                var layout = PdbStructLayouts.Get(entry.FormType)!;
                var headerVa = baseVa + (uint)PdbStructLayouts.GetTesFormInteriorOffset(layout);
                var header = _memory.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(headerVa), 16);
                if (header == null || entry.FormId == 0 ||
                    BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12)) != entry.FormId ||
                    header[4] != (entry.OriginalFormType ?? entry.FormType) ||
                    !Xbox360MemoryUtils.IsModulePointer(BinaryPrimitives.ReadUInt32BigEndian(header))) continue;
                claims.Add(new RuntimeStringOwnershipClaim(hit.FileOffset, hit.VirtualAddress,
                    strict ? "SecondPassReverse" : "SecondPassReverseRelaxed",
                    $"{match.RecordCode} [{entry.FormId:X8}]", entry.FormId, _ctx.VaToFileOffset(baseVa),
                    strict ? ClaimSource.SecondPassReverse : ClaimSource.SecondPassReverseRelaxed,
                    match.RecordCode, strict ? match.FieldLabel : $"candidate field: {match.FieldLabel}",
                    Xbox360MemoryUtils.VaToLong(referrerVa), _ctx.VaToFileOffset(referrerVa),
                    $"canonical type 0x{entry.FormType:X2}; raw type 0x{header[4]:X2}; exact FormID and TESForm address; " +
                    (strict ? "BSStringT length validated" : "BSStringT length not validated")));
            }
        }
        return claims;
    }
}
