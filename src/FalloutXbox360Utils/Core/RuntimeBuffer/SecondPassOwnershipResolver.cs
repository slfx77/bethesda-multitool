using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Strings;
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
    private const int MaxVtableScanBack = 512;

    /// <summary>
    ///     Pre-built lookup: maps (formType, bsStringTFieldOffset) → (recordCode, fieldLabel).
    /// </summary>
    private readonly Dictionary<(byte FormType, int FieldOffset), (string RecordCode, string FieldLabel)>
        _bsStringTFieldIndex;

    /// <summary>
    ///     Sorted distinct field offsets from _bsStringTFieldIndex, used to avoid full
    ///     dictionary iteration in TryTESFormReverseLookup. For each unique offset we
    ///     compute one candidate base VA, peek the formType byte, then do a direct
    ///     dictionary lookup instead of scanning all entries.
    /// </summary>
    private readonly int[] _bsStringTDistinctOffsets;

    /// <summary>
    ///     Maps PDB class name → (formType, list of char* pointer field offsets).
    /// </summary>
    private readonly Dictionary<string, (byte FormType, List<(int Offset, string Label)> Fields)>
        _charPointerFieldIndex;

    /// <summary>
    ///     Maps PDB class name → (formType, list of BSStringT field offsets).
    /// </summary>
    private readonly Dictionary<string, (byte FormType, List<(int Offset, string Label)> Fields)>
        _classNameFieldIndex;

    private readonly BufferAnalysisContext _ctx;

    /// <summary>
    ///     Case-insensitive lookup from dialogue line text → RuntimeEditorIdEntry.
    /// </summary>
    private readonly Dictionary<string, RuntimeEditorIdEntry>? _dialogueTextLookup;

    /// <summary>
    ///     Case-insensitive lookup from EditorID text → RuntimeEditorIdEntry.
    /// </summary>
    private readonly Dictionary<string, RuntimeEditorIdEntry>? _editorIdTextLookup;

    /// <summary>
    ///     Case-insensitive lookup from GMST setting name → GmstRecord.
    /// </summary>
    private readonly Dictionary<string, GmstRecord>? _gmstTextLookup;

    /// <summary>
    ///     Hardcoded NiObject class → string field offsets (not in PDB layouts).
    /// </summary>
    private readonly Dictionary<string, List<(int Offset, string Label)>> _niObjectFieldIndex;

    /// <summary>
    ///     Set of all PDB class names (for cFormEditorID fallback validation).
    /// </summary>
    private readonly HashSet<string> _pdbClassNames;

    public SecondPassOwnershipResolver(BufferAnalysisContext ctx)
    {
        _ctx = ctx;
        (_bsStringTFieldIndex, _classNameFieldIndex, _charPointerFieldIndex) = BuildFieldIndices();
        _bsStringTDistinctOffsets = _bsStringTFieldIndex.Keys
            .Select(k => k.FieldOffset)
            .Distinct()
            .OrderBy(o => o)
            .ToArray();
        _editorIdTextLookup = BuildEditorIdTextLookup();
        _gmstTextLookup = BuildGmstTextLookup();
        _dialogueTextLookup = BuildDialogueTextLookup();
        _niObjectFieldIndex = BuildNiObjectFieldIndex();
        _pdbClassNames = new HashSet<string>(
            PdbStructLayouts.Layouts.Values.Select(l => l.ClassName));
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
            claim ??= TryEditorIdTextMatch(hit);
            claim ??= TryGameSettingTextMatch(hit);
            claim ??= TryDialogueTextMatch(hit);

            // Content-based file path matching: asset paths with known extensions
            claim ??= TryAssetPathContentMatch(hit);

            // Low-priority fallback: cFormEditorID at +16 for EditorId strings
            // near any TESForm vtable. Runs last since TESForms are densely packed.
            claim ??= TryCFormEditorIdFallback(hit);

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
                claim ??= TryVtableReverseLookup(hit, fileOffset, (uint)va);

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
        result ??= TryVtableReverseLookup(hit, singleFileOffset, singleVa);
        return result;
    }

    /// <summary>
    ///     Strategy 3: Match ReferencedOwnerUnknown EditorId strings by text content
    ///     against the known EditorID inventory.
    /// </summary>
    private RuntimeStringOwnershipClaim? TryEditorIdTextMatch(RuntimeStringHit hit)
    {
        if (_editorIdTextLookup == null || hit.Category != StringCategory.EditorId)
        {
            return null;
        }

        if (!_editorIdTextLookup.TryGetValue(hit.Text, out var entry))
        {
            return null;
        }

        return new RuntimeStringOwnershipClaim(
            hit.FileOffset,
            hit.VirtualAddress,
            "TextContentMatch",
            entry.EditorId,
            entry.FormId != 0 ? entry.FormId : null,
            entry.TesFormOffset,
            ClaimSource.TextContentMatch);
    }

    /// <summary>
    ///     Match ReferencedOwnerUnknown GameSetting strings by text content
    ///     against the GMST record inventory and EditorID inventory.
    /// </summary>
    private RuntimeStringOwnershipClaim? TryGameSettingTextMatch(RuntimeStringHit hit)
    {
        if (hit.Category != StringCategory.GameSetting)
        {
            return null;
        }

        // Try GMST record inventory first
        if (_gmstTextLookup != null && _gmstTextLookup.TryGetValue(hit.Text, out var gmst))
        {
            return new RuntimeStringOwnershipClaim(
                hit.FileOffset,
                hit.VirtualAddress,
                "TextContentMatch",
                $"GMST [{gmst.Name}]",
                null,
                gmst.Offset,
                ClaimSource.TextContentMatch,
                "GMST",
                gmst.Name);
        }

        // Fall back to EditorID inventory (GMST records have EditorIDs too)
        if (_editorIdTextLookup != null && _editorIdTextLookup.TryGetValue(hit.Text, out var entry))
        {
            return new RuntimeStringOwnershipClaim(
                hit.FileOffset,
                hit.VirtualAddress,
                "TextContentMatch",
                entry.EditorId,
                entry.FormId != 0 ? entry.FormId : null,
                entry.TesFormOffset,
                ClaimSource.TextContentMatch,
                "GMST",
                entry.EditorId);
        }

        return null;
    }

    /// <summary>
    ///     Match ReferencedOwnerUnknown DialogueLine strings by text content
    ///     against dialogue lines extracted from RuntimeEditorIdEntry inventory.
    /// </summary>
    private RuntimeStringOwnershipClaim? TryDialogueTextMatch(RuntimeStringHit hit)
    {
        if (_dialogueTextLookup == null || hit.Category != StringCategory.DialogueLine)
        {
            return null;
        }

        if (!_dialogueTextLookup.TryGetValue(hit.Text, out var entry))
        {
            return null;
        }

        return new RuntimeStringOwnershipClaim(
            hit.FileOffset,
            hit.VirtualAddress,
            "TextContentMatch",
            $"INFO [{entry.EditorId}]",
            entry.FormId != 0 ? entry.FormId : null,
            entry.TesFormOffset,
            ClaimSource.TextContentMatch,
            "INFO",
            "DialogueLine");
    }

    /// <summary>
    ///     Content-based claiming for file path strings with known game asset extensions.
    ///     These strings have inbound pointers (they're in ReferencedOwnerUnknown) and their
    ///     content pattern strongly identifies them as game asset paths.
    /// </summary>
    private static RuntimeStringOwnershipClaim? TryAssetPathContentMatch(RuntimeStringHit hit)
    {
        if (hit.Category != StringCategory.FilePath)
        {
            return null;
        }

        // Must contain a path separator or look like a filename with an extension
        var text = hit.Text;
        if (text.Length < 5)
        {
            return null;
        }

        // Check for known game asset extensions (case-insensitive)
        var dotIdx = text.LastIndexOf('.');
        if (dotIdx < 1 || dotIdx >= text.Length - 2)
        {
            return null;
        }

        var ext = text[dotIdx..].ToLowerInvariant();
        var isKnownAssetExt = ext is ".nif" or ".kf" or ".dds" or ".psa" or ".egt" or ".egm"
            or ".bsa" or ".esm" or ".esp" or ".lip" or ".fuz" or ".wav" or ".ogg" or ".mp3"
            or ".spt" or ".tre" or ".tri" or ".tga" or ".bmp" or ".xml" or ".ctl"
            or ".ddx" or ".xdo" or ".psd" or ".txt" or ".ini" or ".lst";

        if (!isKnownAssetExt)
        {
            return null;
        }

        return new RuntimeStringOwnershipClaim(
            hit.FileOffset,
            hit.VirtualAddress,
            "TextContentMatch",
            "AssetPath",
            null,
            null,
            ClaimSource.TextContentMatch,
            "AssetPath",
            ext[1..].ToUpperInvariant());
    }

    /// <summary>
    ///     Low-priority fallback: check if an EditorId string is at cFormEditorID (+16)
    ///     relative to any TESForm vtable among its referrers. Only matches EditorId-category
    ///     strings and runs after all higher-confidence strategies.
    /// </summary>
    private RuntimeStringOwnershipClaim? TryCFormEditorIdFallback(RuntimeStringHit hit)
    {
        if (hit.Category != StringCategory.EditorId || hit.OwnerResolution == null)
        {
            return null;
        }

        var allReferrers = hit.OwnerResolution.AllReferrers;
        if (allReferrers is { Count: > 0 })
        {
            foreach (var (fileOffset, va, _) in allReferrers)
            {
                if (va < 0 || va > uint.MaxValue)
                {
                    continue;
                }

                var claim = TryCFormEditorIdAtReferrer(hit, fileOffset, (uint)va);
                if (claim != null)
                {
                    return claim;
                }
            }

            return null;
        }

        if (hit.OwnerResolution.ReferrerFileOffset == null || hit.OwnerResolution.ReferrerVa == null)
        {
            return null;
        }

        return TryCFormEditorIdAtReferrer(hit,
            hit.OwnerResolution.ReferrerFileOffset.Value,
            (uint)hit.OwnerResolution.ReferrerVa.Value);
    }

    /// <summary>
    ///     Check if the referrer is at offset +16 (cFormEditorID) from a TESForm vtable.
    /// </summary>
    private RuntimeStringOwnershipClaim? TryCFormEditorIdAtReferrer(
        RuntimeStringHit hit, long referrerFileOffset, uint referrerVa)
    {
        // cFormEditorID is at offset +16 from TESForm base.
        // TESForm base has vtable at +0. So vtable is at referrer - 16.
        if (referrerVa < 16)
        {
            return null;
        }

        var vtableFileOffset = referrerFileOffset - 16;
        if (vtableFileOffset < 0)
        {
            return null;
        }

        var vtableBytes = new byte[4];
        _ctx.Accessor.ReadArray(vtableFileOffset, vtableBytes, 0, 4);
        var vtablePtr = BinaryPrimitives.ReadUInt32BigEndian(vtableBytes);

        if (!Xbox360MemoryUtils.IsModulePointer(vtablePtr))
        {
            return null;
        }

        var rtti = ResolveVtableMinimal(vtablePtr);
        if (rtti == null || !_pdbClassNames.Contains(rtti.Value.ClassName))
        {
            return null;
        }

        // Validate: ObjectOffset should be 0 (primary vtable = TESForm base)
        if (rtti.Value.ObjectOffset != 0)
        {
            return null;
        }

        var layout = PdbStructLayouts.Layouts.Values
            .FirstOrDefault(l => l.ClassName == rtti.Value.ClassName);
        var recordCode = layout?.RecordCode ?? rtti.Value.ClassName;

        return new RuntimeStringOwnershipClaim(
            hit.FileOffset,
            hit.VirtualAddress,
            "SecondPassVtable",
            $"{recordCode} ({rtti.Value.ClassName})",
            null,
            vtableFileOffset,
            ClaimSource.SecondPassVtable,
            recordCode,
            "TESForm.cFormEditorID");
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

    #region Strategy 2: Vtable-Based Reverse Lookup

    private RuntimeStringOwnershipClaim? TryVtableReverseLookup(
        RuntimeStringHit hit, long referrerFileOffset, uint referrerVa)
    {
        // Scan backwards from the referrer to find a vtable pointer
        var scanStart = referrerFileOffset;
        var maxScanBack = Math.Min(MaxVtableScanBack, referrerFileOffset);
        for (var backOffset = 0L; backOffset <= maxScanBack; backOffset += 4)
        {
            var candidateOffset = scanStart - backOffset;
            if (candidateOffset < 0)
            {
                break;
            }

            var candidateBytes = new byte[4];
            _ctx.Accessor.ReadArray(candidateOffset, candidateBytes, 0, 4);
            var candidateVtable = BinaryPrimitives.ReadUInt32BigEndian(candidateBytes);

            if (!Xbox360MemoryUtils.IsModulePointer(candidateVtable))
            {
                continue;
            }

            // Try to resolve RTTI at this vtable
            var rtti = ResolveVtableMinimal(candidateVtable);
            if (rtti == null)
            {
                continue;
            }

            // Calculate object base: vtable location - ObjectOffset
            var vtableLocationVa = referrerVa - (uint)backOffset;
            var objectBaseVa = vtableLocationVa - rtti.Value.ObjectOffset;
            var fieldOffset = (int)(referrerVa - objectBaseVa);

            // Look up class name in PDB field index (BSStringT fields first, then char* fields)
            (int Offset, string Label) matchedField = default;
            byte matchedFormType = 0;

            if (_classNameFieldIndex.TryGetValue(rtti.Value.ClassName, out var layoutInfo))
            {
                matchedField = layoutInfo.Fields.FirstOrDefault(f => f.Offset == fieldOffset);
                matchedFormType = layoutInfo.FormType;
            }

            if (matchedField == default &&
                _charPointerFieldIndex.TryGetValue(rtti.Value.ClassName, out var charLayoutInfo))
            {
                matchedField = charLayoutInfo.Fields.FirstOrDefault(f => f.Offset == fieldOffset);
                matchedFormType = charLayoutInfo.FormType;
            }

            // Fallback: check hardcoded NiObject/embedded class field index
            if (matchedField == default &&
                _niObjectFieldIndex.TryGetValue(rtti.Value.ClassName, out var niFields))
            {
                matchedField = niFields.FirstOrDefault(f => f.Offset == fieldOffset);
            }

            if (matchedField == default)
            {
                continue;
            }

            var layout = matchedFormType != 0 ? PdbStructLayouts.Get(matchedFormType) : null;
            var recordCode = layout?.RecordCode ?? rtti.Value.ClassName;
            var objectBaseFileOffset = _ctx.VaToFileOffset(objectBaseVa);

            return new RuntimeStringOwnershipClaim(
                hit.FileOffset,
                hit.VirtualAddress,
                "SecondPassVtable",
                $"{recordCode} ({rtti.Value.ClassName})",
                null,
                objectBaseFileOffset,
                ClaimSource.SecondPassVtable,
                recordCode,
                matchedField.Label);
        }

        return null;
    }

    /// <summary>
    ///     Minimal RTTI resolution using the accessor (no Stream required).
    ///     Returns class name and ObjectOffset, or null on failure.
    /// </summary>
    private (string ClassName, uint ObjectOffset)? ResolveVtableMinimal(uint vtableVa)
    {
        if (vtableVa < 4)
        {
            return null;
        }

        // vtable[-1] → COL pointer
        var colPointer = ReadUInt32AtVa(vtableVa - 4);
        if (colPointer == null || !Xbox360MemoryUtils.IsModulePointer(colPointer.Value))
        {
            return null;
        }

        // COL: [+0: signature=0] [+4: offset] [+8: cdOffset] [+C: pTypeDescriptor]
        var signature = ReadUInt32AtVa(colPointer.Value);
        if (signature is not 0)
        {
            return null;
        }

        var objectOffset = ReadUInt32AtVa(colPointer.Value + 4);
        var pTypeDescriptor = ReadUInt32AtVa(colPointer.Value + 12);
        if (objectOffset == null || pTypeDescriptor == null ||
            !Xbox360MemoryUtils.IsModulePointer(pTypeDescriptor.Value))
        {
            return null;
        }

        // TypeDescriptor: [+8: mangled name string]
        var nameVa = pTypeDescriptor.Value + 8;
        var nameFileOffset = _ctx.MinidumpInfo.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(nameVa));
        if (nameFileOffset == null)
        {
            return null;
        }

        // Read mangled name (up to 128 bytes)
        var nameBuffer = new byte[128];
        var maxRead = (int)Math.Min(128, _ctx.FileSize - nameFileOffset.Value);
        if (maxRead <= 4)
        {
            return null;
        }

        _ctx.Accessor.ReadArray(nameFileOffset.Value, nameBuffer, 0, maxRead);

        var nullIdx = Array.IndexOf(nameBuffer, (byte)0, 0, maxRead);
        if (nullIdx < 0)
        {
            nullIdx = maxRead;
        }

        var mangledName = Encoding.ASCII.GetString(nameBuffer, 0, nullIdx);
        var className = RttiReader.DemangleName(mangledName);
        if (className == null)
        {
            return null;
        }

        return (className, objectOffset.Value);
    }

    private uint? ReadUInt32AtVa(uint va)
    {
        var fileOffset = _ctx.MinidumpInfo.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(va));
        if (fileOffset == null || fileOffset.Value + 4 > _ctx.FileSize)
        {
            return null;
        }

        var buf = new byte[4];
        _ctx.Accessor.ReadArray(fileOffset.Value, buf, 0, 4);
        return BinaryPrimitives.ReadUInt32BigEndian(buf);
    }

    #endregion

    #region Index Building

    /// <summary>
    ///     Build all three PDB-based field indices in a single pass over PdbStructLayouts.Layouts.
    ///     Returns: (bsStringTFieldIndex, classNameFieldIndex, charPointerFieldIndex).
    /// </summary>
    private static (
        Dictionary<(byte FormType, int FieldOffset), (string RecordCode, string FieldLabel)> BsStringT,
        Dictionary<string, (byte FormType, List<(int Offset, string Label)> Fields)> ClassName,
        Dictionary<string, (byte FormType, List<(int Offset, string Label)> Fields)> CharPointer
        ) BuildFieldIndices()
    {
        var bsIndex = new Dictionary<(byte, int), (string, string)>();
        var classIndex = new Dictionary<string, (byte, List<(int, string)>)>();
        var charIndex = new Dictionary<string, (byte, List<(int, string)>)>();

        foreach (var (formType, layout) in PdbStructLayouts.Layouts)
        {
            // BSStringT fields → bsIndex and classIndex
            var bsFields = PdbStructLayouts.GetBSStringTFields(formType);
            List<(int, string)>? classFieldList = null;

            foreach (var field in bsFields)
            {
                if (field.Name is "cFormEditorID")
                {
                    continue;
                }

                var fieldLabel = field.Owner != null ? $"{field.Owner}.{field.Name}" : field.Name;
                bsIndex.TryAdd((formType, field.Offset), (layout.RecordCode, fieldLabel));

                classFieldList ??= [];
                classFieldList.Add((field.Offset, fieldLabel));
            }

            if (classFieldList is { Count: > 0 })
            {
                classIndex[layout.ClassName] = (formType, classFieldList);
            }

            // char* pointer fields → charIndex
            List<(int, string)>? charFieldList = null;

            foreach (var f in layout.Fields)
            {
                if (f.Kind is not "pointer" || f.TypeDetail is not "char")
                {
                    continue;
                }

                var label = f.Owner != null ? $"{f.Owner}.{f.Name}" : f.Name;
                charFieldList ??= [];
                charFieldList.Add((f.Offset, label));
            }

            if (charFieldList is { Count: > 0 })
            {
                charIndex[layout.ClassName] = (formType, charFieldList);
            }
        }

        return (bsIndex, classIndex, charIndex);
    }

    /// <summary>
    ///     Build case-insensitive lookup from EditorID text → RuntimeEditorIdEntry.
    /// </summary>
    private Dictionary<string, RuntimeEditorIdEntry>? BuildEditorIdTextLookup()
    {
        if (_ctx.RuntimeEditorIds is not { Count: > 0 })
        {
            return null;
        }

        var lookup = new Dictionary<string, RuntimeEditorIdEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _ctx.RuntimeEditorIds)
        {
            lookup.TryAdd(entry.EditorId, entry);
        }

        return lookup;
    }

    /// <summary>
    ///     Build case-insensitive lookup from GMST setting name → GmstRecord.
    /// </summary>
    private Dictionary<string, GmstRecord>? BuildGmstTextLookup()
    {
        if (_ctx.GameSettings is not { Count: > 0 })
        {
            return null;
        }

        var lookup = new Dictionary<string, GmstRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var gmst in _ctx.GameSettings)
        {
            lookup.TryAdd(gmst.Name, gmst);
        }

        return lookup;
    }

    /// <summary>
    ///     Build case-insensitive lookup from dialogue line text → RuntimeEditorIdEntry.
    /// </summary>
    private Dictionary<string, RuntimeEditorIdEntry>? BuildDialogueTextLookup()
    {
        if (_ctx.RuntimeEditorIds is not { Count: > 0 })
        {
            return null;
        }

        var lookup = new Dictionary<string, RuntimeEditorIdEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _ctx.RuntimeEditorIds)
        {
            if (!string.IsNullOrEmpty(entry.DialogueLine))
            {
                lookup.TryAdd(entry.DialogueLine, entry);
            }
        }

        return lookup.Count > 0 ? lookup : null;
    }

    /// <summary>
    ///     Build hardcoded class name → string field offsets index for types not in PDB layouts.
    ///     Covers Gamebryo NiObject types and TES embedded component classes.
    /// </summary>
    private static Dictionary<string, List<(int Offset, string Label)>> BuildNiObjectFieldIndex()
    {
        var index = new Dictionary<string, List<(int, string)>>();

        // --- Gamebryo NiObjectNET types: m_kName (NiFixedString) at +8 ---
        var nameField = (Offset: 8, Label: "NiObjectNET.m_kName");
        var niObjectNetClasses = new[]
        {
            "NiNode", "BSFadeNode", "NiTriShape", "NiTriStrips",
            "NiCamera", "NiLight", "NiPointLight", "NiDirectionalLight",
            "NiAmbientLight", "NiProperty", "NiMaterialProperty",
            "BSShaderPPLightingProperty", "NiAlphaProperty",
            "NiTexturingProperty", "NiStencilProperty",
            "NiVertexColorProperty", "NiWireframeProperty",
            "NiZBufferProperty", "NiSourceTexture",
            "BSTreeNode", "NiSwitchNode", "NiBillboardNode",
            "NiGeometry", "NiParticles", "NiParticleSystem",
            "BSShaderNoLightingProperty", "BSShaderLightingProperty",
            // Animation sequence types (NiObjectNET → NiSequence → ...)
            "BSAnimGroupSequence"
        };
        foreach (var cls in niObjectNetClasses)
        {
            index[cls] = [nameField];
        }

        index["NiSourceTexture"].Add((48, "NiSourceTexture.m_kFilename"));

        // --- TES embedded component classes (BaseFormComponent subclasses) ---
        // These have their own vtables when embedded in TESForm types via MI.
        // BSStringT at +4 = char* ptr right after the vtable.
        index["TESTexture"] = [(4, "TESTexture.texture")];
        index["TESIcon"] = [(4, "TESIcon.icon")];
        index["TESModel"] = [(4, "TESModel.model")];
        index["TESModelTextureSwap"] =
        [
            (4, "TESModelTextureSwap.model"),
            (44, "TESModelTextureSwap.altTextureName")
        ];

        // TESTexture1024: subclass of TESTexture, same layout
        index["TESTexture1024"] = [(4, "TESTexture1024.texture")];

        // BGSTextureModel: another texture model component
        index["BGSTextureModel"] = [(4, "BGSTextureModel.model")];

        // QueuedModel: engine model loading queue entry, path at +40
        index["QueuedModel"] = [(40, "QueuedModel.modelPath")];

        // BSShaderTextureSet: inherits NiObject (vtable+4=refcount), then 6+ texture slots.
        // Each slot is a NiFixedString (char*).
        index["BSShaderTextureSet"] =
        [
            (8, "BSShaderTextureSet.diffuse"),
            (12, "BSShaderTextureSet.normal"),
            (16, "BSShaderTextureSet.glow"),
            (20, "BSShaderTextureSet.parallax"),
            (24, "BSShaderTextureSet.envMap"),
            (28, "BSShaderTextureSet.slot5"),
            (32, "BSShaderTextureSet.slot6"),
            (48, "BSShaderTextureSet.slot10"),
            (56, "BSShaderTextureSet.slot12")
        ];

        // SettingT<GameSettingCollection>: RTTI demangles to this template form.
        // pKey (setting name char*) at +8.
        index["?$SettingT@VGameSettingCollection"] = [(8, "SettingT.pKey")];

        // --- TESForm-derived classes with string fields not in PDB BSStringT index ---

        // BGSBodyPart: body part definition with mesh/bone paths
        index["BGSBodyPart"] =
        [
            (12, "BGSBodyPart.boneName"),
            (20, "BGSBodyPart.partNode"),
            (36, "BGSBodyPart.targetNode")
        ];

        // BGSQuestObjective: quest objective display text (BSStringT at +8)
        index["BGSQuestObjective"] = [(8, "BGSQuestObjective.displayText")];

        // TESLoadScreen: loading screen tip text
        index["TESLoadScreen"] = [(68, "TESLoadScreen.screenText")];

        // Script: compiled script contains string references
        index["Script"] =
        [
            (144, "Script.varName1"),
            (152, "Script.varName2")
        ];

        // TESCreature: creature model/animation paths
        index["TESCreature"] =
        [
            (216, "TESCreature.animPath"),
            (296, "TESCreature.modelPath")
        ];

        // BGSTerminal: terminal UI text fields
        index["BGSTerminal"] =
        [
            (208, "BGSTerminal.resultText"),
            (216, "BGSTerminal.headerText")
        ];

        return index;
    }

    #endregion
}
