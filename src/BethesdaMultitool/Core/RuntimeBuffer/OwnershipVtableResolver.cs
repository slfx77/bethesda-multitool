using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.RuntimeBuffer;

/// <summary>
///     Vtable-based reverse lookup strategy for second-pass ownership resolution.
///     Scans backwards from a referrer to find a vtable pointer, resolves RTTI,
///     and matches field offset to PDB layout.
/// </summary>
internal sealed class OwnershipVtableResolver
{
    private const int MaxVtableScanBack = 512;

    /// <summary>
    ///     Backward window sizes tried, largest first. All multiples of 4 so the walk inside the
    ///     window stays 4-aligned, and 0 is included so a referrer at the very start of a captured
    ///     run still gets its own word examined.
    /// </summary>
    private static readonly int[] BackwardWindowSizes = [512, 256, 128, 64, 32, 16, 8, 4, 0];

    /// <summary>
    ///     Maps PDB class name to (formType, list of char* pointer field offsets).
    /// </summary>
    private readonly Dictionary<string, (byte FormType, List<(int Offset, string Label)> Fields)>
        _charPointerFieldIndex;

    /// <summary>
    ///     Maps PDB class name to (formType, list of BSStringT field offsets).
    /// </summary>
    private readonly Dictionary<string, (byte FormType, List<(int Offset, string Label)> Fields)>
        _classNameFieldIndex;

    private readonly BufferAnalysisContext _ctx;

    /// <summary>
    ///     VA-space reader for the backward window. A flat file-offset read cannot fail closed at a
    ///     region boundary; this one does.
    /// </summary>
    private readonly RuntimeMemoryContext _memory;

    /// <summary>
    ///     Hardcoded NiObject class to string field offsets (not in PDB layouts).
    /// </summary>
    private readonly Dictionary<string, List<(int Offset, string Label)>> _niObjectFieldIndex;

    /// <summary>
    ///     Reused backward-window buffer. Safe as instance state because
    ///     <see cref="SecondPassOwnershipResolver.Resolve" /> walks its hits on one thread; revisit
    ///     this if that loop is ever parallelised.
    /// </summary>
    private readonly byte[] _window = new byte[MaxVtableScanBack + 4];

    public OwnershipVtableResolver(
        BufferAnalysisContext ctx,
        Dictionary<string, (byte FormType, List<(int Offset, string Label)> Fields)> classNameFieldIndex,
        Dictionary<string, (byte FormType, List<(int Offset, string Label)> Fields)> charPointerFieldIndex,
        Dictionary<string, List<(int Offset, string Label)>> niObjectFieldIndex)
    {
        _ctx = ctx;
        _memory = new RuntimeMemoryContext(new MmfMemoryAccessor(ctx.Accessor), ctx.FileSize, ctx.MinidumpInfo);
        _classNameFieldIndex = classNameFieldIndex;
        _charPointerFieldIndex = charPointerFieldIndex;
        _niObjectFieldIndex = niObjectFieldIndex;
    }

    /// <summary>
    ///     Read the largest captured window ending just past the referrer word, into
    ///     <see cref="_window" />. Returns how many bytes precede the referrer inside it, or -1 when
    ///     even the referrer's own word is unreadable in VA space.
    /// </summary>
    private int TryReadBackwardWindow(uint referrerVa)
    {
        foreach (var back in BackwardWindowSizes)
        {
            if ((uint)back > referrerVa)
            {
                continue;
            }

            if (_memory.ReadBytesAtVaInto(
                    Xbox360MemoryUtils.VaToLong(referrerVa - (uint)back), _window, 0, back + 4))
            {
                return back;
            }
        }

        return -1;
    }

    internal RuntimeStringOwnershipClaim? TryVtableReverseLookup(RuntimeStringHit hit, uint referrerVa)
    {
        // Scan backwards from the referrer to find a vtable pointer.
        //
        // This walks VA space, not file-offset space. Until 2026-09-04 the candidate was read at
        // `referrerFileOffset - backOffset` while the resulting object base was computed as
        // `referrerVa - backOffset`. Minidump regions are laid out contiguously by FILE OFFSET
        // while their VAs are arbitrary, so any referrer within 512 bytes of a region start walked
        // into the PREVIOUS region's bytes and then attributed that unrelated allocation's vtable —
        // and its field labels — to this string. Reading one VA-space window instead both fixes
        // that and replaces up to 129 four-byte reads with a single stitched read.
        var maxScanBack = TryReadBackwardWindow(referrerVa);
        if (maxScanBack < 0)
        {
            return null;
        }

        for (var backOffset = 0; backOffset <= maxScanBack; backOffset += 4)
        {
            var candidateVtable =
                BinaryPrimitives.ReadUInt32BigEndian(_window.AsSpan(maxScanBack - backOffset, 4));

            if (!Xbox360MemoryUtils.IsModulePointer(candidateVtable))
            {
                continue;
            }

            // Try to resolve RTTI at this vtable
            var rtti = ResolveVtableMinimal(_ctx, candidateVtable);
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
    internal static (string ClassName, uint ObjectOffset)? ResolveVtableMinimal(
        BufferAnalysisContext ctx, uint vtableVa)
    {
        if (vtableVa < 4)
        {
            return null;
        }

        // vtable[-1] -> COL pointer
        var colPointer = ReadUInt32AtVa(ctx, vtableVa - 4);
        if (colPointer == null || !Xbox360MemoryUtils.IsModulePointer(colPointer.Value))
        {
            return null;
        }

        // COL: [+0: signature=0] [+4: offset] [+8: cdOffset] [+C: pTypeDescriptor]
        var signature = ReadUInt32AtVa(ctx, colPointer.Value);
        if (signature is not 0)
        {
            return null;
        }

        var objectOffset = ReadUInt32AtVa(ctx, colPointer.Value + 4);
        var pTypeDescriptor = ReadUInt32AtVa(ctx, colPointer.Value + 12);
        if (objectOffset == null || pTypeDescriptor == null ||
            !Xbox360MemoryUtils.IsModulePointer(pTypeDescriptor.Value))
        {
            return null;
        }

        // TypeDescriptor: [+8: mangled name string]
        var nameVa = pTypeDescriptor.Value + 8;
        var nameFileOffset = ctx.MinidumpInfo.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(nameVa));
        if (nameFileOffset == null)
        {
            return null;
        }

        // Read mangled name (up to 128 bytes)
        var nameBuffer = new byte[128];
        var maxRead = (int)Math.Min(128, ctx.FileSize - nameFileOffset.Value);
        if (maxRead <= 4)
        {
            return null;
        }

        ctx.Accessor.ReadArray(nameFileOffset.Value, nameBuffer, 0, maxRead);

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

    private static uint? ReadUInt32AtVa(BufferAnalysisContext ctx, uint va)
    {
        var fileOffset = ctx.MinidumpInfo.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(va));
        if (fileOffset == null || fileOffset.Value + 4 > ctx.FileSize)
        {
            return null;
        }

        var buf = new byte[4];
        ctx.Accessor.ReadArray(fileOffset.Value, buf, 0, 4);
        return BinaryPrimitives.ReadUInt32BigEndian(buf);
    }
}
