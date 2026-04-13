using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Strings;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.RuntimeBuffer;

/// <summary>
///     Vtable-based reverse lookup strategy for second-pass ownership resolution.
///     Scans backwards from a referrer to find a vtable pointer, resolves RTTI,
///     and matches field offset to PDB layout.
/// </summary>
internal sealed class OwnershipVtableResolver
{
    private const int MaxVtableScanBack = 512;

    private readonly BufferAnalysisContext _ctx;

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

    /// <summary>
    ///     Hardcoded NiObject class to string field offsets (not in PDB layouts).
    /// </summary>
    private readonly Dictionary<string, List<(int Offset, string Label)>> _niObjectFieldIndex;

    public OwnershipVtableResolver(
        BufferAnalysisContext ctx,
        Dictionary<string, (byte FormType, List<(int Offset, string Label)> Fields)> classNameFieldIndex,
        Dictionary<string, (byte FormType, List<(int Offset, string Label)> Fields)> charPointerFieldIndex,
        Dictionary<string, List<(int Offset, string Label)>> niObjectFieldIndex)
    {
        _ctx = ctx;
        _classNameFieldIndex = classNameFieldIndex;
        _charPointerFieldIndex = charPointerFieldIndex;
        _niObjectFieldIndex = niObjectFieldIndex;
    }

    internal RuntimeStringOwnershipClaim? TryVtableReverseLookup(
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
