using System.Buffers.Binary;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.RegularExpressions;
using FalloutXbox360Utils.Core.Formats.EsmRecord;
using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Core;

/// <summary>
///     Parses PDB global symbols, resolves them to virtual addresses in a memory dump,
///     reads their values, and walks data structures they point to.
/// </summary>
internal sealed partial class PdbGlobalResolver
{
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly long _fileSize;
    private readonly MinidumpModule _gameModule;
    private readonly MinidumpInfo _minidumpInfo;
    private readonly List<EsmRecordFormat.PeSectionInfo> _peSections;

    public PdbGlobalResolver(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        MinidumpInfo minidumpInfo,
        MinidumpModule gameModule,
        List<EsmRecordFormat.PeSectionInfo> peSections)
    {
        _accessor = accessor;
        _fileSize = fileSize;
        _minidumpInfo = minidumpInfo;
        _gameModule = gameModule;
        _peSections = peSections;
    }

    #region Helpers

    private long? VaToFileOffset(uint va)
    {
        if (va == 0)
        {
            return null;
        }

        return _minidumpInfo.VirtualAddressToFileOffset(EsmRecordFormat.Xbox360VaToLong(va));
    }

    #endregion

    #region Step 1: PDB Parsing

    /// <summary>
    ///     Parse PDB globals file for S_GDATA32 and S_LDATA32 entries.
    /// </summary>
    public static List<PdbGlobal> ParseGlobals(string pdbGlobalsPath)
    {
        var globals = new List<PdbGlobal>();
        var regex = PdbGlobalRegex();

        foreach (var line in File.ReadLines(pdbGlobalsPath))
        {
            var match = regex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var kind = match.Groups[1].Value; // GDATA32 or LDATA32
            var section = int.Parse(match.Groups[2].Value, NumberStyles.HexNumber);
            var offset = uint.Parse(match.Groups[3].Value, NumberStyles.HexNumber);
            var name = match.Groups[4].Value.Trim();

            globals.Add(new PdbGlobal
            {
                Kind = kind,
                Section = section,
                Offset = offset,
                Name = name
            });
        }

        return globals;
    }

    [GeneratedRegex(@"S_(GDATA32|LDATA32):\s+\[(\w+):(\w+)\],\s+Type:\s+\S+,\s+(.+)")]
    private static partial Regex PdbGlobalRegex();

    #endregion

    #region Step 2: VA Resolution

    /// <summary>
    ///     Resolve PDB globals to virtual addresses and read their values from the dump.
    /// </summary>
    public PdbAnalysisResult ResolveAndAnalyze(
        List<PdbGlobal> globals,
        HashSet<long> assetVAs)
    {
        var result = new PdbAnalysisResult();

        // Filter to data section globals only (section 7 = index 6 in PE)
        var dataGlobals = globals
            .Where(g => g.Section >= 1 && g.Section <= _peSections.Count)
            .ToList();

        result.TotalParsed = globals.Count;
        result.DataSectionGlobals = dataGlobals.Count;

        foreach (var global in dataGlobals)
        {
            var resolved = ResolveGlobal(global);
            if (resolved == null)
            {
                result.UnresolvableCount++;
                continue;
            }

            result.ResolvedGlobals.Add(resolved);
        }

        // Classify resolved globals
        foreach (var resolved in result.ResolvedGlobals)
        {
            switch (resolved.Classification)
            {
                case PointerClassification.Null:
                    result.NullCount++;
                    break;
                case PointerClassification.Unmapped:
                    result.UnmappedCount++;
                    break;
                case PointerClassification.ModuleRange:
                    result.ModuleRangeCount++;
                    break;
                case PointerClassification.Heap:
                    result.HeapCount++;
                    break;
            }
        }

        // Explore interesting globals
        ExploreInterestingGlobals(result, assetVAs);

        // Deduplicate by name + pointer value (PDB can list same global multiple times)
        result.DeduplicateInterestingGlobals();

        return result;
    }

    private ResolvedGlobal? ResolveGlobal(PdbGlobal global)
    {
        // PDB sections are 1-indexed, PE sections are 0-indexed
        var peIndex = global.Section - 1;
        if (peIndex < 0 || peIndex >= _peSections.Count)
        {
            return null;
        }

        var section = _peSections[peIndex];
        var globalVA = _gameModule.BaseAddress + section.VirtualAddress + global.Offset;

        // Check if offset is within section bounds
        if (global.Offset >= section.VirtualSize)
        {
            return null;
        }

        // Convert VA to file offset
        var fileOffset = _minidumpInfo.VirtualAddressToFileOffset(globalVA);
        if (!fileOffset.HasValue || fileOffset.Value + 4 > _fileSize)
        {
            return null;
        }

        // Read the 4-byte BE value at this location
        var buffer = new byte[4];
        _accessor.ReadArray(fileOffset.Value, buffer, 0, 4);
        var value = BinaryPrimitives.ReadUInt32BigEndian(buffer);

        // Classify the pointer value
        var classification = ClassifyPointer(value);

        return new ResolvedGlobal
        {
            Global = global,
            VirtualAddress = globalVA,
            FileOffset = fileOffset.Value,
            PointerValue = value,
            Classification = classification
        };
    }

    private PointerClassification ClassifyPointer(uint value)
    {
        if (value == 0)
        {
            return PointerClassification.Null;
        }

        // Check if it's within module range
        if (value >= (uint)_gameModule.BaseAddress &&
            value < (uint)(_gameModule.BaseAddress + _gameModule.Size))
        {
            return PointerClassification.ModuleRange;
        }

        // Check if it maps to a captured memory region
        var va = EsmRecordFormat.Xbox360VaToLong(value);
        if (_minidumpInfo.VirtualAddressToFileOffset(va).HasValue)
        {
            return PointerClassification.Heap;
        }

        return PointerClassification.Unmapped;
    }

    #endregion

    #region Step 3: Structure Exploration

    private void ExploreInterestingGlobals(PdbAnalysisResult result, HashSet<long> assetVAs)
    {
        foreach (var resolved in result.ResolvedGlobals)
        {
            // Only explore globals classified as Heap or ModuleRange
            // (game singletons on Xbox 360 often live in the module's .data section)
            if (resolved.Classification is not PointerClassification.Heap
                and not PointerClassification.ModuleRange)
            {
                continue;
            }

            // Pointer must be 4-byte aligned
            if (resolved.PointerValue % 4 != 0)
            {
                continue;
            }

            // Only explore globals whose names indicate they're actual pointers
            if (!IsLikelyPointerGlobal(resolved.Global.Name))
            {
                continue;
            }

            var name = resolved.Global.Name;

            // Try to identify NiTMapBase hash tables
            if (name.Contains("Map", StringComparison.Ordinal) ||
                name.Contains("AllForms", StringComparison.Ordinal) ||
                name.Contains("EditorID", StringComparison.Ordinal))
            {
                var mapInfo = TryReadNiTMapBase(resolved.PointerValue);
                if (mapInfo != null)
                {
                    resolved.StructureInfo = mapInfo;
                    result.InterestingGlobals.Add(resolved);
                    continue;
                }
            }

            // BSShaderManager::pTextureManager
            if (name.Contains("TextureManager", StringComparison.Ordinal) ||
                name.Contains("pTextureManager", StringComparison.Ordinal))
            {
                var texInfo = TryExploreTextureManager(resolved.PointerValue, assetVAs);
                if (texInfo != null)
                {
                    resolved.StructureInfo = texInfo;
                    result.InterestingGlobals.Add(resolved);
                    continue;
                }
            }

            // Generic pointer-to-structure identification — only add if we find real structure
            var genericInfo = TryIdentifyStructure(resolved.PointerValue, assetVAs);
            if (genericInfo != null)
            {
                resolved.StructureInfo = genericInfo;
                result.InterestingGlobals.Add(resolved);
            }
        }
    }

    /// <summary>
    ///     Returns true if the global name indicates it's likely an actual pointer to a heap object
    ///     rather than a scalar value (float, int, vector, constant, etc.).
    /// </summary>
    private static bool IsLikelyPointerGlobal(string name)
    {
        // For class-qualified names (Foo::bar), check the member name after the last ::
        var memberName = name;
        var lastSep = name.LastIndexOf("::", StringComparison.Ordinal);
        if (lastSep >= 0 && lastSep + 2 < name.Length)
        {
            memberName = name[(lastSep + 2)..];
        }

        // Skip scalar type prefixes: f=float, i=int, b=bool, u/us=unsigned, s=string
        if (memberName.Length >= 2 && char.IsUpper(memberName[1]) &&
            memberName[0] is 'f' or 'i' or 'b' or 'u' or 's')
        {
            return false;
        }

        // Skip pParam* entries (script parameter definition arrays, not heap pointers)
        if (memberName.StartsWith("pParam", StringComparison.Ordinal))
        {
            return false;
        }

        // Pointer naming: p/sp/g_p prefix followed by uppercase letter
        if (memberName.Length >= 2 && memberName[0] == 'p' && char.IsUpper(memberName[1]))
        {
            return true;
        }

        if (memberName.Length >= 3 && memberName.StartsWith("sp", StringComparison.Ordinal) &&
            char.IsUpper(memberName[2]))
        {
            return true;
        }

        if (memberName.StartsWith("g_p", StringComparison.Ordinal) ||
            memberName.StartsWith("g_sp", StringComparison.Ordinal))
        {
            return true;
        }

        // Singleton/manager/collection patterns (anywhere in full name)
        return name.Contains("Instance", StringComparison.Ordinal) ||
               name.Contains("Singleton", StringComparison.Ordinal) ||
               name.Contains("AllForms", StringComparison.Ordinal) ||
               name.Contains("AlteredForms", StringComparison.Ordinal) ||
               name.Contains("DataHandler", StringComparison.Ordinal);
    }

    /// <summary>
    ///     Try to read a NiTMapBase hash table structure at the given VA.
    /// </summary>
    private string? TryReadNiTMapBase(uint va)
    {
        var fileOffset = VaToFileOffset(va);
        if (fileOffset == null || fileOffset.Value + 16 > _fileSize)
        {
            return null;
        }

        var buffer = new byte[16];
        _accessor.ReadArray(fileOffset.Value, buffer, 0, 16);

        var vfptr = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(0, 4));
        var hashSize = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(4, 4));
        var bucketArrayVa = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(8, 4));
        var entryCount = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(12, 4));

        // Validate: hashSize should be reasonable (prime numbers typically used)
        if (hashSize < 2 || hashSize > 1_000_000)
        {
            return null;
        }

        // Validate: vfptr and bucketArray should be valid pointers
        if (!EsmRecordFormat.IsValidPointerInDump(vfptr, _minidumpInfo) ||
            !EsmRecordFormat.IsValidPointerInDump(bucketArrayVa, _minidumpInfo))
        {
            return null;
        }

        // Sample a few buckets to verify structure
        var bucketFileOffset = VaToFileOffset(bucketArrayVa);
        if (bucketFileOffset == null)
        {
            return null;
        }

        var nonNullBuckets = 0;
        var sampleSize = (int)Math.Min(hashSize, 100);
        var bucketBuffer = new byte[4];
        for (var i = 0; i < sampleSize; i++)
        {
            var offset = bucketFileOffset.Value + i * 4;
            if (offset + 4 > _fileSize)
            {
                break;
            }

            _accessor.ReadArray(offset, bucketBuffer, 0, 4);
            var val = BinaryPrimitives.ReadUInt32BigEndian(bucketBuffer);
            if (val != 0 && EsmRecordFormat.IsValidPointerInDump(val, _minidumpInfo))
            {
                nonNullBuckets++;
            }
        }

        if (nonNullBuckets == 0)
        {
            return null;
        }

        // Walk a small sample of entries to determine key/value types
        var (keyType, valType, sampleCount) = SampleHashTableEntries(bucketArrayVa, hashSize);

        return $"NiTMapBase: hashSize={hashSize:N0}, entries={entryCount:N0}, " +
               $"nonNullBuckets={nonNullBuckets}/{sampleSize}, " +
               $"keys={keyType}, values={valType} (sampled {sampleCount})";
    }

    private (string keyType, string valType, int count) SampleHashTableEntries(
        uint bucketArrayVa, uint hashSize)
    {
        var keyTypes = new Dictionary<string, int>();
        var valTypes = new Dictionary<string, int>();
        var sampled = 0;
        var maxSamples = 50;

        var bucketFileOffset = VaToFileOffset(bucketArrayVa);
        if (bucketFileOffset == null)
        {
            return ("unknown", "unknown", 0);
        }

        var bucketBuffer = new byte[4];
        var itemBuffer = new byte[12];

        for (uint i = 0; i < hashSize && sampled < maxSamples; i++)
        {
            var offset = bucketFileOffset.Value + i * 4;
            if (offset + 4 > _fileSize)
            {
                break;
            }

            _accessor.ReadArray(offset, bucketBuffer, 0, 4);
            var itemVa = BinaryPrimitives.ReadUInt32BigEndian(bucketBuffer);

            if (itemVa == 0 || !EsmRecordFormat.IsValidPointerInDump(itemVa, _minidumpInfo))
            {
                continue;
            }

            // Read NiTMapItem: next(4) + key(4) + val(4)
            var itemFileOffset = VaToFileOffset(itemVa);
            if (itemFileOffset == null || itemFileOffset.Value + 12 > _fileSize)
            {
                continue;
            }

            _accessor.ReadArray(itemFileOffset.Value, itemBuffer, 0, 12);
            var keyVa = BinaryPrimitives.ReadUInt32BigEndian(itemBuffer.AsSpan(4, 4));
            var valVa = BinaryPrimitives.ReadUInt32BigEndian(itemBuffer.AsSpan(8, 4));

            // Classify key
            var keyClass = ClassifyHashEntry(keyVa);
            keyTypes.TryGetValue(keyClass, out var kc);
            keyTypes[keyClass] = kc + 1;

            // Classify value
            var valClass = ClassifyHashEntry(valVa);
            valTypes.TryGetValue(valClass, out var vc);
            valTypes[valClass] = vc + 1;

            sampled++;
        }

        var topKey = keyTypes.OrderByDescending(kv => kv.Value).FirstOrDefault().Key ?? "unknown";
        var topVal = valTypes.OrderByDescending(kv => kv.Value).FirstOrDefault().Key ?? "unknown";
        return (topKey, topVal, sampled);
    }

    private string ClassifyHashEntry(uint va)
    {
        if (va == 0)
        {
            return "null";
        }

        if (!EsmRecordFormat.IsValidPointerInDump(va, _minidumpInfo))
        {
            return "unmapped";
        }

        var fileOffset = VaToFileOffset(va);
        if (fileOffset == null || fileOffset.Value + 16 > _fileSize)
        {
            return "ptr";
        }

        // Try reading as TESForm header (check formType byte at offset 4)
        var buffer = new byte[16];
        _accessor.ReadArray(fileOffset.Value, buffer, 0, 16);
        var formType = buffer[4];
        if (formType > 0 && formType < 120)
        {
            var formId = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(12, 4));
            if (formId != 0 && formId != 0xFFFFFFFF && formId >> 24 <= 0xFF)
            {
                return $"TESForm(type={formType:X2})";
            }
        }

        // Try reading as string
        var strLen = 0;
        while (strLen < 16 && buffer[strLen] >= 0x20 && buffer[strLen] <= 0x7E)
        {
            strLen++;
        }

        if (strLen >= 3 && buffer[strLen] == 0)
        {
            return "string";
        }

        return "ptr";
    }

    /// <summary>
    ///     Try to explore a BSTextureManager instance.
    /// </summary>
    private string? TryExploreTextureManager(uint va, HashSet<long> assetVAs)
    {
        var fileOffset = VaToFileOffset(va);
        if (fileOffset == null || fileOffset.Value + 156 > _fileSize)
        {
            return null;
        }

        // Read the full 156-byte BSTextureManager structure
        var buffer = new byte[156];
        _accessor.ReadArray(fileOffset.Value, buffer, 0, 156);

        var sb = new StringBuilder("BSTextureManager (156 bytes):");

        // Read pointer fields and count valid vs null
        var validPtrs = 0;
        var nullPtrs = 0;
        var assetRefs = 0;

        for (var i = 0; i < 156 - 3; i += 4)
        {
            var ptr = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(i, 4));
            if (ptr == 0)
            {
                nullPtrs++;
                continue;
            }

            if (EsmRecordFormat.IsValidPointerInDump(ptr, _minidumpInfo))
            {
                validPtrs++;

                // Check if this pointer matches a known carved file VA
                var ptrVa = EsmRecordFormat.Xbox360VaToLong(ptr);
                if (assetVAs.Contains(ptrVa))
                {
                    assetRefs++;
                }
            }
        }

        sb.Append($" {validPtrs} valid ptrs, {nullPtrs} null fields");
        if (assetRefs > 0)
        {
            sb.Append($", {assetRefs} asset cross-refs!");
        }

        // Try to follow specific pool pointers at known offsets
        // Offsets 0 and 48: spUnusedAliasedTextureList and spUsedAliasedTextureList (each 48 bytes)
        var unusedPoolEntries = CountLinkedListEntries(
            BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(0, 4)));
        var usedPoolEntries = CountLinkedListEntries(
            BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(48, 4)));

        if (unusedPoolEntries > 0 || usedPoolEntries > 0)
        {
            sb.Append($"\n    Alias pools: unused={unusedPoolEntries}, used={usedPoolEntries}");
        }

        // Depth buffer pointers at offsets 144, 148, 152
        var depthBufs = 0;
        for (var i = 144; i <= 152; i += 4)
        {
            var ptr = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(i, 4));
            if (ptr != 0 && EsmRecordFormat.IsValidPointerInDump(ptr, _minidumpInfo))
            {
                depthBufs++;
            }
        }

        if (depthBufs > 0)
        {
            sb.Append($"\n    Depth buffers: {depthBufs}/3 present");
        }

        return sb.ToString();
    }

    private int CountLinkedListEntries(uint headVa)
    {
        if (headVa == 0 || !EsmRecordFormat.IsValidPointerInDump(headVa, _minidumpInfo))
        {
            return 0;
        }

        var count = 0;
        var visited = new HashSet<uint>();
        var currentVa = headVa;

        while (currentVa != 0 && count < 10000 && visited.Add(currentVa))
        {
            var fileOffset = VaToFileOffset(currentVa);
            if (fileOffset == null || fileOffset.Value + 8 > _fileSize)
            {
                break;
            }

            count++;

            // BSSimpleList node: data(4) + next(4)
            var buffer = new byte[8];
            _accessor.ReadArray(fileOffset.Value, buffer, 0, 8);
            currentVa = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(4, 4));
        }

        return count;
    }

    private string? TryIdentifyStructure(uint va, HashSet<long> assetVAs)
    {
        var fileOffset = VaToFileOffset(va);
        if (fileOffset == null || fileOffset.Value + 32 > _fileSize)
        {
            return null;
        }

        var buffer = new byte[32];
        _accessor.ReadArray(fileOffset.Value, buffer, 0, 32);

        // Check first field — if it's a pointer into module range, it's likely a vtable
        var firstField = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(0, 4));
        var hasVtable = firstField != 0 &&
                        firstField >= (uint)_gameModule.BaseAddress &&
                        firstField < (uint)(_gameModule.BaseAddress + _gameModule.Size);

        // Count valid pointers in the first 32 bytes
        var validPtrs = 0;
        var assetRefs = 0;
        for (var i = 0; i < 32; i += 4)
        {
            var ptr = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(i, 4));
            if (ptr != 0 && EsmRecordFormat.IsValidPointerInDump(ptr, _minidumpInfo))
            {
                validPtrs++;
                var ptrVa = EsmRecordFormat.Xbox360VaToLong(ptr);
                if (assetVAs.Contains(ptrVa))
                {
                    assetRefs++;
                }
            }
        }

        // Require evidence: vtable, multiple pointers, or asset refs
        if (!hasVtable && validPtrs < 2 && assetRefs == 0)
        {
            return null;
        }

        var parts = new List<string>();
        if (hasVtable)
        {
            parts.Add($"vtable 0x{firstField:X8}");
        }

        parts.Add($"{validPtrs} ptrs in 32 bytes");
        if (assetRefs > 0)
        {
            parts.Add($"{assetRefs} asset cross-refs!");
        }

        return string.Join(", ", parts);
    }

    #endregion
}
