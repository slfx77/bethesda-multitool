using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Minidump;

/// <summary>
///     Reads MSVC Run-Time Type Information (RTTI) from Xbox 360 memory dumps.
///     Given a vtable virtual address, follows the pointer chain:
///     vtable[-1] → CompleteObjectLocator → TypeDescriptor → class name
///     and optionally walks the ClassHierarchyDescriptor for full inheritance.
///     All multi-byte reads use big-endian byte order (PowerPC).
/// </summary>
public sealed class RttiReader
{
    private const int MaxBaseClasses = 32;

    private readonly MinidumpInfo _minidumpInfo;
    private readonly Stream _stream;

    public RttiReader(MinidumpInfo minidumpInfo, Stream stream)
    {
        _minidumpInfo = minidumpInfo;
        _stream = stream;
    }

    /// <summary>
    ///     Resolve a vtable virtual address to its RTTI class name and hierarchy.
    ///     Returns null if any pointer in the chain is invalid or not captured in the dump.
    /// </summary>
    public RttiResult? ResolveVtable(uint vtableVA)
    {
        if (vtableVA < 4)
        {
            return null;
        }

        // Step 1: vtable[-1] → COL pointer
        var colPointer = ReadUInt32AtVA(vtableVA - 4);
        if (colPointer == null || !Xbox360MemoryUtils.IsModulePointer(colPointer.Value))
        {
            return null;
        }

        // Step 2: Read CompleteObjectLocator (20 bytes)
        var col = ReadCOL(colPointer.Value);
        if (col == null)
        {
            return null;
        }

        // Step 3: Read TypeDescriptor → class name
        var td = ReadTypeDescriptor(col.Value.PTypeDescriptor);
        if (td == null)
        {
            return null;
        }

        // Step 4: Read ClassHierarchyDescriptor (optional — hierarchy is nice-to-have)
        var hierarchy = ReadClassHierarchy(col.Value.PClassHierarchyDescriptor);

        return new RttiResult
        {
            VtableVA = vtableVA,
            ClassName = DemangleName(td.Value.MangledName) ?? td.Value.MangledName,
            MangledName = td.Value.MangledName,
            ObjectOffset = col.Value.Offset,
            BaseClasses = hierarchy?.BaseClasses,
            HasMultipleInheritance = hierarchy?.HasMI ?? false
        };
    }

    /// <summary>
    ///     Scan a memory range for unique vtable pointers and resolve RTTI for each.
    ///     Reads the first uint32 at each stride position and checks if it's a module pointer.
    /// </summary>
    public List<RttiResult> ScanRange(uint startVA, uint endVA, int stride = 4)
    {
        var seen = new HashSet<uint>();
        var results = new List<RttiResult>();

        for (var va = startVA; va < endVA; va += (uint)stride)
        {
            var candidate = ReadUInt32AtVA(va);
            if (candidate == null || !Xbox360MemoryUtils.IsModulePointer(candidate.Value))
            {
                continue;
            }

            if (!seen.Add(candidate.Value))
            {
                continue;
            }

            var result = ResolveVtable(candidate.Value);
            if (result != null)
            {
                results.Add(result);
            }
        }

        return results;
    }

    /// <summary>
    ///     Scan all heap memory regions for vtable pointers and build a census of C++ classes.
    ///     Reads each region in buffered chunks, counts unique module-range pointers,
    ///     resolves RTTI for each, and flags TESForm-derived classes.
    /// </summary>
    /// <param name="progress">Optional callback: (regionsScanned, totalRegions, bytesScanned).</param>
    public List<CensusEntry> RunCensus(Action<int, int, long>? progress = null)
    {
        // Phase 1: Scan all heap regions and count module-range pointer occurrences
        var vtableCounts = new Dictionary<uint, int>();
        var heapRegions = _minidumpInfo.MemoryRegions
            .Where(r =>
            {
                // Convert sign-extended VA back to uint32 for range check
                var va32 = unchecked((uint)r.VirtualAddress);
                return va32 >= Xbox360MemoryUtils.HeapBase && va32 < Xbox360MemoryUtils.HeapEnd;
            })
            .OrderBy(r => r.VirtualAddress)
            .ToList();

        var totalRegions = heapRegions.Count;
        long totalBytesScanned = 0;

        const int bufferSize = 1024 * 1024; // 1MB chunks
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

        try
        {
            for (var ri = 0; ri < heapRegions.Count; ri++)
            {
                var region = heapRegions[ri];
                var remaining = region.Size;
                var filePos = region.FileOffset;

                while (remaining > 0)
                {
                    var toRead = (int)Math.Min(remaining, bufferSize);
                    _stream.Seek(filePos, SeekOrigin.Begin);
                    var bytesRead = _stream.Read(buffer, 0, toRead);
                    if (bytesRead < 4)
                    {
                        break;
                    }

                    // Scan buffer for BE uint32 values in module range, 4-byte aligned
                    var limit = bytesRead - 3;
                    for (var i = 0; i < limit; i += 4)
                    {
                        var value = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(i));
                        if (value >= Xbox360MemoryUtils.ModuleBase)
                        {
                            if (vtableCounts.TryGetValue(value, out var count))
                            {
                                vtableCounts[value] = count + 1;
                            }
                            else
                            {
                                vtableCounts[value] = 1;
                            }
                        }
                    }

                    filePos += bytesRead;
                    remaining -= bytesRead;
                    totalBytesScanned += bytesRead;
                }

                progress?.Invoke(ri + 1, totalRegions, totalBytesScanned);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // Phase 2: Resolve RTTI for each unique candidate, sorted by count descending
        var resolved = new Dictionary<uint, RttiResult>(); // vtable VA → result cache
        var entries = new List<CensusEntry>();

        foreach (var (vtableCandidate, instanceCount) in vtableCounts.OrderByDescending(kv => kv.Value))
        {
            // Skip very rare hits (likely false positives from data that happens to be in module range)
            if (instanceCount < 2)
            {
                continue;
            }

            if (resolved.ContainsKey(vtableCandidate))
            {
                continue;
            }

            var result = ResolveVtable(vtableCandidate);
            if (result == null)
            {
                continue;
            }

            resolved[vtableCandidate] = result;

            var isTesForm = result.BaseClasses?.Any(b =>
                b.ClassName == "TESForm" || b.ClassName == "TESObject") ?? false;

            entries.Add(new CensusEntry
            {
                Rtti = result,
                InstanceCount = instanceCount,
                IsTesForm = isTesForm
            });
        }

        return entries.OrderByDescending(e => e.InstanceCount).ToList();
    }

    #region Private Helpers

    /// <summary>
    ///     Read a big-endian uint32 at a virtual address. Returns null if VA is not captured.
    /// </summary>
    private uint? ReadUInt32AtVA(uint va)
    {
        var fileOffset = _minidumpInfo.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(va));
        if (fileOffset == null)
        {
            return null;
        }

        return MinidumpInfo.ReadBigEndianUInt32(_stream, fileOffset.Value);
    }

    /// <summary>
    ///     Read a signed big-endian int32 at a virtual address. Returns null if VA is not captured.
    /// </summary>
    private int? ReadInt32AtVA(uint va)
    {
        var fileOffset = _minidumpInfo.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(va));
        if (fileOffset == null)
        {
            return null;
        }

        _stream.Seek(fileOffset.Value, SeekOrigin.Begin);
        Span<byte> buf = stackalloc byte[4];
        _stream.ReadExactly(buf);
        return BinaryPrimitives.ReadInt32BigEndian(buf);
    }

    /// <summary>
    ///     Read the CompleteObjectLocator struct at a given VA.
    ///     Layout (20 bytes, all BE uint32):
    ///     +0: signature (must be 0)
    ///     +4: offset (vtable position within complete object)
    ///     +8: cdOffset (constructor displacement)
    ///     +C: pTypeDescriptor
    ///     +10: pClassHierarchyDescriptor
    /// </summary>
    private COL? ReadCOL(uint colVA)
    {
        var signature = ReadUInt32AtVA(colVA);
        if (signature == null || signature.Value != 0)
        {
            return null;
        }

        var offset = ReadUInt32AtVA(colVA + 4);
        var cdOffset = ReadUInt32AtVA(colVA + 8);
        var pTypeDescriptor = ReadUInt32AtVA(colVA + 12);
        var pCHD = ReadUInt32AtVA(colVA + 16);

        if (offset == null || cdOffset == null || pTypeDescriptor == null || pCHD == null)
        {
            return null;
        }

        if (!Xbox360MemoryUtils.IsModulePointer(pTypeDescriptor.Value))
        {
            return null;
        }

        return new COL(offset.Value, cdOffset.Value, pTypeDescriptor.Value, pCHD.Value);
    }

    /// <summary>
    ///     Read the TypeDescriptor at a given VA, extracting the mangled class name.
    ///     Layout:
    ///     +0: pVFTable (uint32)
    ///     +4: spare (uint32)
    ///     +8: name (null-terminated ASCII, e.g., ".?AVTESIdleForm@@")
    /// </summary>
    private TypeDesc? ReadTypeDescriptor(uint tdVA)
    {
        var nameVA = tdVA + 8;
        var name = _minidumpInfo.ReadStringAtVA(_stream, Xbox360MemoryUtils.VaToLong(nameVA));

        if (name == null || !name.StartsWith(".?A", StringComparison.Ordinal))
        {
            return null;
        }

        return new TypeDesc(name);
    }

    /// <summary>
    ///     Read the ClassHierarchyDescriptor and all base classes.
    ///     Layout (16 bytes):
    ///     +0: signature (uint32)
    ///     +4: attributes (uint32, bit 0 = MI, bit 1 = VI)
    ///     +8: numBaseClasses (uint32)
    ///     +C: pBaseClassArray (uint32, pointer to array of BCD pointers)
    /// </summary>
    private ClassHierarchy? ReadClassHierarchy(uint chdVA)
    {
        if (!Xbox360MemoryUtils.IsModulePointer(chdVA))
        {
            return null;
        }

        var attributes = ReadUInt32AtVA(chdVA + 4);
        var numBaseClasses = ReadUInt32AtVA(chdVA + 8);
        var pBaseClassArray = ReadUInt32AtVA(chdVA + 12);

        if (attributes == null || numBaseClasses == null || pBaseClassArray == null)
        {
            return null;
        }

        if (numBaseClasses.Value == 0 || numBaseClasses.Value > MaxBaseClasses)
        {
            return null;
        }

        if (!Xbox360MemoryUtils.IsModulePointer(pBaseClassArray.Value))
        {
            return null;
        }

        var baseClasses = new List<RttiBaseClass>();

        for (uint i = 0; i < numBaseClasses.Value; i++)
        {
            var bcdPointer = ReadUInt32AtVA(pBaseClassArray.Value + i * 4);
            if (bcdPointer == null || !Xbox360MemoryUtils.IsModulePointer(bcdPointer.Value))
            {
                break;
            }

            var baseClass = ReadBaseClassDescriptor(bcdPointer.Value);
            if (baseClass == null)
            {
                break;
            }

            baseClasses.Add(baseClass);
        }

        return new ClassHierarchy(
            (attributes.Value & 1) != 0,
            (attributes.Value & 2) != 0,
            baseClasses);
    }

    /// <summary>
    ///     Read a BaseClassDescriptor at a given VA.
    ///     Layout:
    ///     +0: pTypeDescriptor (uint32)
    ///     +4: numContainedBases (uint32)
    ///     +8: PMD.mdisp (int32, member displacement)
    ///     +C: PMD.pdisp (int32, vtable displacement)
    ///     +10: PMD.vdisp (int32, virtual base displacement)
    ///     +14: attributes (uint32)
    /// </summary>
    private RttiBaseClass? ReadBaseClassDescriptor(uint bcdVA)
    {
        var pTypeDescriptor = ReadUInt32AtVA(bcdVA);
        if (pTypeDescriptor == null || !Xbox360MemoryUtils.IsModulePointer(pTypeDescriptor.Value))
        {
            return null;
        }

        var numContainedBases = ReadUInt32AtVA(bcdVA + 4);
        var mdisp = ReadInt32AtVA(bcdVA + 8);

        if (numContainedBases == null || mdisp == null)
        {
            return null;
        }

        var td = ReadTypeDescriptor(pTypeDescriptor.Value);
        if (td == null)
        {
            return null;
        }

        return new RttiBaseClass
        {
            ClassName = DemangleName(td.Value.MangledName) ?? td.Value.MangledName,
            MangledName = td.Value.MangledName,
            MemberDisplacement = mdisp.Value,
            NumContainedBases = numContainedBases.Value
        };
    }

    /// <summary>
    ///     Demangle an MSVC mangled name like ".?AVTESIdleForm@@" to "TESIdleForm".
    ///     Handles both class (.?AV) and struct (.?AU) prefixes.
    /// </summary>
    internal static string? DemangleName(string mangledName)
    {
        if (!mangledName.StartsWith(".?AV", StringComparison.Ordinal) &&
            !mangledName.StartsWith(".?AU", StringComparison.Ordinal))
        {
            return null;
        }

        var name = mangledName[4..];
        var atIndex = name.IndexOf("@@", StringComparison.Ordinal);
        if (atIndex <= 0)
        {
            return null;
        }

        return name[..atIndex];
    }

    #endregion

    #region Private Types

    private readonly record struct COL(uint Offset, uint CdOffset, uint PTypeDescriptor, uint PClassHierarchyDescriptor);

    private readonly record struct TypeDesc(string MangledName);

    private sealed record ClassHierarchy(bool HasMI, bool HasVI, List<RttiBaseClass> BaseClasses);

    #endregion
}
