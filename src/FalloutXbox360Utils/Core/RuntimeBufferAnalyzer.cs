using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Formats.EsmRecord;
using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Core;

/// <summary>
///     Deep exploration of runtime buffers in Xbox 360 memory dumps.
///     Walks PDB globals, extracts strings from memory pools,
///     scans for format signatures, and analyzes pointer graphs.
/// </summary>
internal sealed partial class RuntimeBufferAnalyzer
{
    private const int MinStringLength = 4;
    private const int MaxStringLength = 512;
    private const int MaxSampleStrings = 20;
    private const int SignatureScanBytes = 512;

    private static readonly HashSet<string> KnownFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nif", ".dds", ".ddx", ".kf", ".wav", ".lip", ".psc", ".txt",
        ".esm", ".esp", ".bsa", ".xml", ".ini", ".fuz", ".xwm", ".bik",
        ".mp3", ".ogg", ".xur", ".xui", ".scda"
    };

    private readonly MemoryMappedViewAccessor _accessor;
    private readonly CoverageResult _coverage;
    private readonly long _fileSize;
    private readonly MinidumpInfo _minidumpInfo;
    private readonly uint _moduleEnd;
    private readonly uint _moduleStart;
    private readonly PdbAnalysisResult? _pdbAnalysis;

    public RuntimeBufferAnalyzer(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        MinidumpInfo minidumpInfo,
        CoverageResult coverage,
        PdbAnalysisResult? pdbAnalysis)
    {
        _accessor = accessor;
        _fileSize = fileSize;
        _minidumpInfo = minidumpInfo;
        _coverage = coverage;
        _pdbAnalysis = pdbAnalysis;

        var gameModule = MemoryDumpAnalyzer.FindGameModule(minidumpInfo);
        if (gameModule != null)
        {
            _moduleStart = gameModule.BaseAddress32;
            _moduleEnd = (uint)(gameModule.BaseAddress + gameModule.Size);
        }
    }

    public BufferExplorationResult Analyze()
    {
        var result = new BufferExplorationResult();

        if (_pdbAnalysis != null)
        {
            RunManagerWalk(result);
        }

        RunStringPoolExtraction(result);
        RunBinarySignatureScan(result);
        RunPointerGraphAnalysis(result);

        return result;
    }

    /// <summary>
    ///     Run only the string pool extraction pass (no PDB required).
    ///     Used by the analyze command to enrich ESM reconstruction output.
    /// </summary>
    public StringPoolSummary ExtractStringPoolOnly()
    {
        var result = new BufferExplorationResult();
        RunStringPoolExtraction(result);
        return result.StringPools!;
    }

    /// <summary>
    ///     Cross-reference string pool file paths with carved files from analysis.
    /// </summary>
    public static void CrossReferenceWithCarvedFiles(StringPoolSummary summary,
        IReadOnlyList<CarvedFileInfo> carvedFiles)
    {
        if (summary.AllFilePaths.Count == 0 || carvedFiles.Count == 0)
        {
            return;
        }

        // Build a set of carved file name suffixes for fast lookup
        var carvedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var carved in carvedFiles)
        {
            var name = carved.FileName;
            if (!string.IsNullOrEmpty(name))
            {
                carvedNames.Add(Path.GetFileName(name));
            }
        }

        var matched = 0;
        foreach (var path in summary.AllFilePaths)
        {
            var fileName = path;
            var lastSep = path.LastIndexOfAny(['\\', '/']);
            if (lastSep >= 0 && lastSep < path.Length - 1)
            {
                fileName = path[(lastSep + 1)..];
            }

            if (carvedNames.Contains(fileName))
            {
                matched++;
            }
        }

        summary.MatchedToCarvedFiles = matched;
        summary.UnmatchedFilePaths = summary.AllFilePaths.Count - matched;
    }

    #region Pass 4: Pointer Graph Analysis

    private void RunPointerGraphAnalysis(BufferExplorationResult result)
    {
        var summary = new PointerGraphSummary();
        var vtableCounts = new Dictionary<uint, int>();

        var pointerGaps = _coverage.Gaps
            .Where(g => g.Classification == GapClassification.PointerDense)
            .ToList();

        summary.TotalPointerDenseGaps = pointerGaps.Count;
        summary.TotalPointerDenseBytes = pointerGaps.Sum(g => g.Size);

        foreach (var gap in pointerGaps)
        {
            var sampleSize = (int)Math.Min(gap.Size, 256);
            sampleSize = sampleSize / 4 * 4; // Align to 4 bytes
            if (sampleSize < 4)
            {
                continue;
            }

            var buffer = new byte[sampleSize];
            _accessor.ReadArray(gap.FileOffset, buffer, 0, sampleSize);

            var vtableCount = 0;
            var heapCount = 0;
            var nullCount = 0;
            var slots = sampleSize / 4;

            for (var i = 0; i < sampleSize; i += 4)
            {
                var val = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(i, 4));

                if (val == 0)
                {
                    nullCount++;
                    continue;
                }

                if (val >= _moduleStart && val < _moduleEnd)
                {
                    vtableCount++;
                    vtableCounts.TryGetValue(val, out var c);
                    vtableCounts[val] = c + 1;
                    summary.TotalVtablePointersFound++;
                }
                else if (IsValidPointer(val))
                {
                    heapCount++;
                }
            }

            // Classify gap based on pointer distribution
            if (vtableCount > 0 && vtableCount >= slots * 0.15)
            {
                summary.ObjectArrayGaps++;
            }
            else if (heapCount > slots * 0.4 && nullCount > slots * 0.15)
            {
                summary.HashTableGaps++;
            }
            else if (heapCount > slots * 0.5)
            {
                summary.LinkedListGaps++;
            }
            else
            {
                summary.MixedStructureGaps++;
            }
        }

        // Top vtable addresses (most frequently referenced)
        foreach (var (addr, count) in vtableCounts.OrderByDescending(kv => kv.Value).Take(10))
        {
            summary.TopVtableAddresses[addr] = count;
        }

        result.PointerGraph = summary;
    }

    #endregion

    #region Pass 1: Manager Singleton Walk

    private void RunManagerWalk(BufferExplorationResult result)
    {
        var globals = _pdbAnalysis!.ResolvedGlobals;

        var targets = new (string nameContains, Action<BufferExplorationResult, ResolvedGlobal> walker)[]
        {
            ("pModelLoader", WalkModelLoader),
            ("pAllFormsByEditorID", WalkMapAsStrings),
            ("pMemoryPoolsBySize", WalkMemoryPoolMap),
            ("pMemoryPoolsByAddress", WalkMemoryPoolMap),
            ("pTextureManager", WalkTextureManager),
            ("sEssentialFileCacheList", WalkStringAtPointer),
            ("sUnessentialFileCacheList", WalkStringAtPointer),
            ("pDataHandler", WalkLargeStruct),
            ("pAudioInstance", WalkLargeStruct),
            ("sMasterArchiveList", WalkStringAtPointer),
            ("sMasterFilePath", WalkStringAtPointer)
        };

        var seen = new HashSet<string>();

        foreach (var (nameContains, walker) in targets)
        {
            foreach (var global in globals)
            {
                if (!global.Global.Name.Contains(nameContains, StringComparison.Ordinal))
                {
                    continue;
                }

                if (global.PointerValue == 0)
                {
                    break;
                }

                if (global.Classification is not (PointerClassification.Heap
                    or PointerClassification.ModuleRange))
                {
                    break;
                }

                if (global.PointerValue % 4 != 0)
                {
                    break;
                }

                var key = $"{global.Global.Name}:{global.PointerValue:X8}";
                if (!seen.Add(key))
                {
                    continue;
                }

                walker(result, global);
                break;
            }
        }
    }

    private void WalkModelLoader(BufferExplorationResult result, ResolvedGlobal global)
    {
        // ModelLoader is 48 bytes; pLoadedFileMap at offset 36 is a NiTMapBase pointer
        var fileOffset = VaToFileOffset(global.PointerValue);
        if (fileOffset == null || fileOffset.Value + 48 > _fileSize)
        {
            return;
        }

        var structBuf = new byte[48];
        _accessor.ReadArray(fileOffset.Value, structBuf, 0, 48);

        // Count valid pointers in the struct
        var validPtrs = CountValidPointers(structBuf);

        // Read pLoadedFileMap pointer at offset 36
        var mapVa = BinaryPrimitives.ReadUInt32BigEndian(structBuf.AsSpan(36, 4));
        if (mapVa == 0 || !IsValidPointer(mapVa))
        {
            result.ManagerResults.Add(new ManagerWalkResult
            {
                GlobalName = global.Global.Name,
                PointerValue = global.PointerValue,
                TargetType = "ModelLoader (48 bytes)",
                ChildPointers = validPtrs,
                Summary = $"{validPtrs} valid ptrs, pLoadedFileMap=null"
            });
            return;
        }

        var (hashSize, entryCount, strings) = WalkNiTMapBaseStrings(mapVa);

        var walkResult = new ManagerWalkResult
        {
            GlobalName = global.Global.Name,
            PointerValue = global.PointerValue,
            TargetType = "ModelLoader (48 bytes)",
            ChildPointers = validPtrs,
            WalkableEntries = entryCount,
            Summary = $"pLoadedFileMap: hashSize={hashSize:N0}, entries={entryCount:N0}, " +
                      $"extracted {strings.Count:N0} file paths"
        };
        walkResult.ExtractedStrings.AddRange(strings.Take(MaxSampleStrings));
        result.ManagerResults.Add(walkResult);
    }

    private void WalkMapAsStrings(BufferExplorationResult result, ResolvedGlobal global)
    {
        var (hashSize, entryCount, strings) = WalkNiTMapBaseStrings(global.PointerValue);

        // Even if empty, report if we can confirm it's a real NiTMapBase (valid vfptr)
        if (hashSize == 0)
        {
            var isRealMap = TryConfirmNiTMapBase(global.PointerValue);
            if (!isRealMap)
            {
                return;
            }

            result.ManagerResults.Add(new ManagerWalkResult
            {
                GlobalName = global.Global.Name,
                PointerValue = global.PointerValue,
                TargetType = "NiTMapBase",
                Summary = "empty (not populated at crash time)"
            });
            return;
        }

        var walkResult = new ManagerWalkResult
        {
            GlobalName = global.Global.Name,
            PointerValue = global.PointerValue,
            TargetType = "NiTMapBase",
            WalkableEntries = entryCount,
            Summary = $"hashSize={hashSize:N0}, entries={entryCount:N0}, " +
                      $"extracted {strings.Count:N0} strings"
        };
        walkResult.ExtractedStrings.AddRange(strings.Take(MaxSampleStrings));
        result.ManagerResults.Add(walkResult);
    }

    private void WalkMemoryPoolMap(BufferExplorationResult result, ResolvedGlobal global)
    {
        var fileOffset = VaToFileOffset(global.PointerValue);
        if (fileOffset == null || fileOffset.Value + 16 > _fileSize)
        {
            return;
        }

        var header = new byte[16];
        _accessor.ReadArray(fileOffset.Value, header, 0, 16);

        var vfptr = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
        var hashSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4));
        var entryCount = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12, 4));

        // Confirm it's a real NiTMapBase via vtable pointer
        var isRealMap = vfptr >= _moduleStart && vfptr < _moduleEnd;
        if (!isRealMap)
        {
            return;
        }

        var summary = hashSize >= 2 && hashSize <= 1_000_000
            ? $"hashSize={hashSize:N0}, pools={entryCount:N0}"
            : "empty (not populated at crash time)";

        result.ManagerResults.Add(new ManagerWalkResult
        {
            GlobalName = global.Global.Name,
            PointerValue = global.PointerValue,
            TargetType = "NiTMapBase (MemoryPool)",
            WalkableEntries = (int)entryCount,
            Summary = summary
        });
    }

    private void WalkTextureManager(BufferExplorationResult result, ResolvedGlobal global)
    {
        const int structSize = 156;
        var fileOffset = VaToFileOffset(global.PointerValue);
        if (fileOffset == null || fileOffset.Value + structSize > _fileSize)
        {
            return;
        }

        var buffer = new byte[structSize];
        _accessor.ReadArray(fileOffset.Value, buffer, 0, structSize);

        var validPtrs = 0;
        var nullPtrs = 0;

        for (var i = 0; i <= structSize - 4; i += 4)
        {
            var ptr = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(i, 4));
            if (ptr == 0)
            {
                nullPtrs++;
            }
            else if (IsValidPointer(ptr))
            {
                validPtrs++;
            }
        }

        // Check depth buffer pointers at offsets 144, 148, 152
        var depthBufs = 0;
        for (var i = 144; i <= 152; i += 4)
        {
            var ptr = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(i, 4));
            if (ptr != 0 && IsValidPointer(ptr))
            {
                depthBufs++;
            }
        }

        var summary = $"{validPtrs} valid ptrs, {nullPtrs} null fields";
        if (depthBufs > 0)
        {
            summary += $", {depthBufs}/3 depth buffers";
        }

        result.ManagerResults.Add(new ManagerWalkResult
        {
            GlobalName = global.Global.Name,
            PointerValue = global.PointerValue,
            TargetType = "BSTextureManager (156 bytes)",
            ChildPointers = validPtrs,
            Summary = summary
        });
    }

    private void WalkStringAtPointer(BufferExplorationResult result, ResolvedGlobal global)
    {
        // Try 1: PointerValue is a direct char* to string data
        var str = TryReadCString(global.PointerValue);

        // Try 2: PointerValue is the start of a BSStringT struct (char* at offset 0)
        if (str == null)
        {
            var fileOffset = VaToFileOffset(global.PointerValue);
            if (fileOffset != null && fileOffset.Value + 8 <= _fileSize)
            {
                var buf = new byte[4];
                _accessor.ReadArray(fileOffset.Value, buf, 0, 4);
                var innerPtr = BinaryPrimitives.ReadUInt32BigEndian(buf);
                if (innerPtr != 0 && IsValidPointer(innerPtr))
                {
                    str = TryReadCString(innerPtr);
                }
            }
        }

        if (str == null)
        {
            return;
        }

        var walkResult = new ManagerWalkResult
        {
            GlobalName = global.Global.Name,
            PointerValue = global.PointerValue,
            TargetType = "BSStringT",
            WalkableEntries = 1,
            Summary = $"\"{str}\""
        };
        walkResult.ExtractedStrings.Add(str);
        result.ManagerResults.Add(walkResult);
    }

    private void WalkLargeStruct(BufferExplorationResult result, ResolvedGlobal global)
    {
        const int readSize = 256;
        var fileOffset = VaToFileOffset(global.PointerValue);
        if (fileOffset == null || fileOffset.Value + readSize > _fileSize)
        {
            return;
        }

        var buffer = new byte[readSize];
        _accessor.ReadArray(fileOffset.Value, buffer, 0, readSize);

        var validPtrs = 0;
        var nullPtrs = 0;
        var vtablePtrs = 0;

        for (var i = 0; i < readSize; i += 4)
        {
            var ptr = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(i, 4));
            if (ptr == 0)
            {
                nullPtrs++;
            }
            else if (ptr >= _moduleStart && ptr < _moduleEnd)
            {
                vtablePtrs++;
            }
            else if (IsValidPointer(ptr))
            {
                validPtrs++;
            }
        }

        result.ManagerResults.Add(new ManagerWalkResult
        {
            GlobalName = global.Global.Name,
            PointerValue = global.PointerValue,
            TargetType = "Structure",
            ChildPointers = validPtrs + vtablePtrs,
            Summary = $"{validPtrs + vtablePtrs} valid ptrs ({vtablePtrs} vtable), " +
                      $"{nullPtrs} null (first {readSize} bytes)"
        });
    }

    /// <summary>
    ///     Walk a NiTMapBase hash table and extract string keys.
    /// </summary>
    private (int hashSize, int entryCount, List<string> strings) WalkNiTMapBaseStrings(
        uint va, int maxEntries = 10_000)
    {
        var strings = new List<string>();

        var fileOffset = VaToFileOffset(va);
        if (fileOffset == null || fileOffset.Value + 16 > _fileSize)
        {
            return (0, 0, strings);
        }

        var header = new byte[16];
        _accessor.ReadArray(fileOffset.Value, header, 0, 16);

        var vfptr = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
        var hashSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4));
        var bucketArrayVa = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));
        var entryCount = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12, 4));

        if (hashSize < 2 || hashSize > 1_000_000)
        {
            return (0, 0, strings);
        }

        if (!IsValidPointer(vfptr) || !IsValidPointer(bucketArrayVa))
        {
            return ((int)hashSize, (int)entryCount, strings);
        }

        var bucketFileOffset = VaToFileOffset(bucketArrayVa);
        if (bucketFileOffset == null)
        {
            return ((int)hashSize, (int)entryCount, strings);
        }

        var visited = new HashSet<uint>();
        var extracted = 0;
        var bucketBuf = new byte[4];
        var itemBuf = new byte[12];

        for (uint i = 0; i < hashSize && extracted < maxEntries; i++)
        {
            var bOffset = bucketFileOffset.Value + i * 4;
            if (bOffset + 4 > _fileSize)
            {
                break;
            }

            _accessor.ReadArray(bOffset, bucketBuf, 0, 4);
            var itemVa = BinaryPrimitives.ReadUInt32BigEndian(bucketBuf);

            while (itemVa != 0 && extracted < maxEntries && visited.Add(itemVa))
            {
                if (!IsValidPointer(itemVa))
                {
                    break;
                }

                var itemFileOffset = VaToFileOffset(itemVa);
                if (itemFileOffset == null || itemFileOffset.Value + 12 > _fileSize)
                {
                    break;
                }

                _accessor.ReadArray(itemFileOffset.Value, itemBuf, 0, 12);
                var nextVa = BinaryPrimitives.ReadUInt32BigEndian(itemBuf.AsSpan(0, 4));
                var keyVa = BinaryPrimitives.ReadUInt32BigEndian(itemBuf.AsSpan(4, 4));

                var str = TryReadCString(keyVa);
                if (str != null)
                {
                    strings.Add(str);
                }

                extracted++;
                itemVa = nextVa;
            }
        }

        return ((int)hashSize, (int)entryCount, strings);
    }

    /// <summary>
    ///     Check if a VA points to a valid NiTMapBase by reading the vfptr.
    /// </summary>
    private bool TryConfirmNiTMapBase(uint va)
    {
        var fileOffset = VaToFileOffset(va);
        if (fileOffset == null || fileOffset.Value + 4 > _fileSize)
        {
            return false;
        }

        var buf = new byte[4];
        _accessor.ReadArray(fileOffset.Value, buf, 0, 4);
        var vfptr = BinaryPrimitives.ReadUInt32BigEndian(buf);

        // Valid vfptr should point into module code range
        return vfptr >= _moduleStart && vfptr < _moduleEnd;
    }

    #endregion

    #region Pass 2: String Pool Extraction

    private void RunStringPoolExtraction(BufferExplorationResult result)
    {
        var summary = new StringPoolSummary();
        var uniqueStrings = new HashSet<string>(StringComparer.Ordinal);
        var filePathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var editorIdSet = new HashSet<string>(StringComparer.Ordinal);
        var dialogueSet = new HashSet<string>(StringComparer.Ordinal);
        var settingSet = new HashSet<string>(StringComparer.Ordinal);

        // Scan both StringPool and AsciiText gaps for string content
        var textGaps = _coverage.Gaps
            .Where(g => g.Classification is GapClassification.StringPool or GapClassification.AsciiText)
            .ToList();

        summary.RegionCount = textGaps.Count;
        summary.TotalBytes = textGaps.Sum(g => g.Size);

        foreach (var gap in textGaps)
        {
            var readSize = (int)Math.Min(gap.Size, 1024 * 1024);
            var buffer = new byte[readSize];
            _accessor.ReadArray(gap.FileOffset, buffer, 0, readSize);

            ExtractStringsFromBuffer(
                buffer, uniqueStrings, filePathSet, editorIdSet, dialogueSet, settingSet, summary);
        }

        summary.UniqueStrings = uniqueStrings.Count;
        summary.FilePaths = filePathSet.Count;
        summary.EditorIds = editorIdSet.Count;
        summary.DialogueLines = dialogueSet.Count;
        summary.GameSettings = settingSet.Count;
        summary.Other = uniqueStrings.Count -
                        filePathSet.Count - editorIdSet.Count -
                        dialogueSet.Count - settingSet.Count;

        // Sort samples by length descending — longer strings are more meaningful
        summary.SampleFilePaths.AddRange(
            filePathSet.OrderByDescending(s => s.Length).Take(MaxSampleStrings));
        summary.SampleEditorIds.AddRange(
            editorIdSet.OrderByDescending(s => s.Length).Take(MaxSampleStrings));
        summary.SampleDialogue.AddRange(
            dialogueSet.OrderByDescending(s => s.Length).Take(MaxSampleStrings));
        summary.SampleSettings.AddRange(
            settingSet.OrderByDescending(s => s.Length).Take(MaxSampleStrings));

        // Retain full sets for export
        summary.AllFilePaths = filePathSet;
        summary.AllEditorIds = editorIdSet;
        summary.AllDialogue = dialogueSet;
        summary.AllSettings = settingSet;

        result.StringPools = summary;
    }

    private static void ExtractStringsFromBuffer(
        byte[] buffer,
        HashSet<string> uniqueStrings,
        HashSet<string> filePaths,
        HashSet<string> editorIds,
        HashSet<string> dialogue,
        HashSet<string> settings,
        StringPoolSummary summary)
    {
        var start = -1;

        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] >= 0x20 && buffer[i] <= 0x7E)
            {
                if (start < 0)
                {
                    start = i;
                }
            }
            else
            {
                if (start >= 0 && buffer[i] == 0)
                {
                    var len = i - start;
                    if (len >= MinStringLength && len <= MaxStringLength)
                    {
                        summary.TotalStrings++;
                        var str = Encoding.ASCII.GetString(buffer, start, len);

                        if (uniqueStrings.Add(str))
                        {
                            var category = CategorizeString(str);
                            switch (category)
                            {
                                case StringCategory.FilePath:
                                    filePaths.Add(str);
                                    break;
                                case StringCategory.EditorId:
                                    editorIds.Add(str);
                                    break;
                                case StringCategory.DialogueLine:
                                    dialogue.Add(str);
                                    break;
                                case StringCategory.GameSetting:
                                    settings.Add(str);
                                    break;
                            }
                        }
                    }
                }

                start = -1;
            }
        }
    }

    private static readonly HashSet<string> GameAssetPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "meshes", "textures", "sound", "interface", "menus", "scripts",
        "shaders", "music", "video", "strings", "grass", "trees",
        "landscape", "actors", "characters", "creatures", "effects",
        "clutter", "architecture", "weapons", "armor", "lodsettings",
        "data", "bsa", "esm", "esp"
    };

    private static StringCategory CategorizeString(string s)
    {
        // File path detection — require strong evidence
        if (IsLikelyFilePath(s))
        {
            return StringCategory.FilePath;
        }

        // Analyze character composition once for remaining checks
        var hasUnderscore = false;
        var hasSpace = false;
        var allAlphaNumOrUnderscore = true;
        var upperCount = 0;
        var lowerCount = 0;

        foreach (var c in s)
        {
            if (c == '_')
            {
                hasUnderscore = true;
            }
            else if (c == ' ')
            {
                hasSpace = true;
                allAlphaNumOrUnderscore = false;
            }
            else if (char.IsUpper(c))
            {
                upperCount++;
            }
            else if (char.IsLower(c))
            {
                lowerCount++;
            }
            else if (!char.IsDigit(c))
            {
                allAlphaNumOrUnderscore = false;
            }
        }

        // Game setting: fXxx/iXxx/bXxx/sXxx/uXxx, all alphanumeric, CamelCase, 8+ chars
        if (s.Length >= 8 && !hasUnderscore && !hasSpace && allAlphaNumOrUnderscore &&
            s[0] is 'f' or 'i' or 'b' or 'u' && char.IsUpper(s[1]) && char.IsLower(s[2]) &&
            upperCount >= 2 && lowerCount >= 4)
        {
            return StringCategory.GameSetting;
        }

        // EditorID: alphanumeric + underscore, starts with uppercase, CamelCase-like, 6+ chars
        // Require character diversity to filter repeating binary noise (e.g. "katSkatSkatS")
        if (s.Length >= 6 && !hasSpace && allAlphaNumOrUnderscore &&
            char.IsUpper(s[0]) && lowerCount >= 2 && upperCount >= 1)
        {
            var distinctChars = CountDistinctChars(s);
            var minDistinct = Math.Max(4, s.Length / 5);
            if (distinctChars >= minDistinct)
            {
                return StringCategory.EditorId;
            }
        }

        // Dialogue: natural language — has spaces, 25+ chars, starts with letter,
        // mostly lowercase, no technical patterns
        if (hasSpace && s.Length >= 25 && char.IsLetter(s[0]) &&
            lowerCount > upperCount * 2 && !IsTechnicalString(s))
        {
            return StringCategory.DialogueLine;
        }

        return StringCategory.Other;
    }

    private static bool IsLikelyFilePath(string s)
    {
        // First character must be alphanumeric or underscore (filter stray binary bytes)
        if (s.Length < 6 || (!char.IsLetterOrDigit(s[0]) && s[0] != '_'))
        {
            return false;
        }

        // Check for known file extensions (strong signal)
        var dotIndex = s.LastIndexOf('.');
        if (dotIndex >= 1 && dotIndex < s.Length - 1)
        {
            var ext = s[dotIndex..];
            if (KnownFileExtensions.Contains(ext))
            {
                return true;
            }
        }

        // Path separators — require additional evidence to avoid false positives
        var separatorCount = 0;
        foreach (var c in s)
        {
            if (c is '\\' or '/')
            {
                separatorCount++;
            }
        }

        if (separatorCount == 0)
        {
            return false;
        }

        // Require 2+ separators for strings without known directory prefix
        if (separatorCount >= 2 && s.Length >= 10)
        {
            return true;
        }

        // Single separator: check if first component is a known game directory
        var sepIndex = s.IndexOfAny(['\\', '/']);
        if (sepIndex >= 3)
        {
            var firstDir = s[..sepIndex];
            if (GameAssetPrefixes.Contains(firstDir))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTechnicalString(string s)
    {
        // Filter out debug/technical strings that aren't player-visible dialogue
        return s.Contains("LOD", StringComparison.Ordinal) ||
               (s.Contains("Level ", StringComparison.Ordinal) && s.Contains("Cells", StringComparison.Ordinal)) ||
               s.Contains("MULTIBOUND", StringComparison.Ordinal) ||
               s.Contains("0x", StringComparison.Ordinal) ||
               s.Contains("NULL", StringComparison.Ordinal) ||
               s.Contains("WARNING", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("ASSERT", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("DEBUG", StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Pass 3: Binary Data Signature Scan

    private void RunBinarySignatureScan(BufferExplorationResult result)
    {
        var binaryGaps = _coverage.Gaps
            .Where(g => g.Classification == GapClassification.BinaryData)
            .ToList();

        foreach (var gap in binaryGaps)
        {
            var scanSize = (int)Math.Min(gap.Size, SignatureScanBytes);
            if (scanSize < 4)
            {
                continue;
            }

            var buffer = new byte[scanSize];
            _accessor.ReadArray(gap.FileOffset, buffer, 0, scanSize);

            var discovered = ScanForSignatures(buffer, gap.FileOffset, gap.VirtualAddress, gap.Size);
            result.DiscoveredBuffers.AddRange(discovered);
        }
    }

    private static List<DiscoveredBuffer> ScanForSignatures(
        byte[] buffer, long fileOffset, long? va, long gapSize)
    {
        var results = new List<DiscoveredBuffer>();
        var len = buffer.Length;

        // DDX: "3XDO" or "3XDR"
        if (len >= 4 &&
            buffer[0] == (byte)'3' && buffer[1] == (byte)'X' && buffer[2] == (byte)'D' &&
            buffer[3] is (byte)'O' or (byte)'R')
        {
            var variant = buffer[3] == (byte)'O' ? "original" : "reference";
            results.Add(new DiscoveredBuffer
            {
                FileOffset = fileOffset,
                VirtualAddress = va,
                FormatType = "DDX",
                Details = $"DDX texture ({variant})",
                EstimatedSize = gapSize
            });
        }

        // DDS: "DDS " (0x44445320)
        if (len >= 4 &&
            buffer[0] == (byte)'D' && buffer[1] == (byte)'D' &&
            buffer[2] == (byte)'S' && buffer[3] == (byte)' ')
        {
            var details = "DDS texture";
            if (len >= 20)
            {
                var height = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(12, 4));
                var width = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(16, 4));
                if (width > 0 && width <= 8192 && height > 0 && height <= 8192)
                {
                    details += $" {width}x{height}";
                }
            }

            results.Add(new DiscoveredBuffer
            {
                FileOffset = fileOffset,
                VirtualAddress = va,
                FormatType = "DDS",
                Details = details,
                EstimatedSize = gapSize
            });
        }

        // RIFF/XMA: "RIFF" at offset 0
        if (len >= 4 &&
            buffer[0] == (byte)'R' && buffer[1] == (byte)'I' &&
            buffer[2] == (byte)'F' && buffer[3] == (byte)'F')
        {
            var details = "RIFF container";
            var formatType = "RIFF";
            if (len >= 12)
            {
                var fourcc = Encoding.ASCII.GetString(buffer, 8, 4);
                if (fourcc == "WAVE")
                {
                    details = "XMA2 audio (RIFF/WAVE)";
                    formatType = "XMA2";
                }
                else
                {
                    details = $"RIFF/{fourcc}";
                }
            }

            results.Add(new DiscoveredBuffer
            {
                FileOffset = fileOffset,
                VirtualAddress = va,
                FormatType = formatType,
                Details = details,
                EstimatedSize = gapSize
            });
        }

        // NIF: "Gamebryo File Format" or "NetImmerse File Format"
        if (len >= 20)
        {
            var headerText = Encoding.ASCII.GetString(buffer, 0, Math.Min(25, len));
            if (headerText.StartsWith("Gamebryo File Format", StringComparison.Ordinal) ||
                headerText.StartsWith("NetImmerse File Format", StringComparison.Ordinal))
            {
                results.Add(new DiscoveredBuffer
                {
                    FileOffset = fileOffset,
                    VirtualAddress = va,
                    FormatType = "NIF",
                    Details = "3D model (Gamebryo/NetImmerse)",
                    EstimatedSize = gapSize
                });
            }
        }

        // BSA: "BSA\0"
        if (len >= 4 &&
            buffer[0] == (byte)'B' && buffer[1] == (byte)'S' &&
            buffer[2] == (byte)'A' && buffer[3] == 0)
        {
            results.Add(new DiscoveredBuffer
            {
                FileOffset = fileOffset,
                VirtualAddress = va,
                FormatType = "BSA",
                Details = "Bethesda archive",
                EstimatedSize = gapSize
            });
        }

        // BIK: "BIK" video
        if (len >= 3 &&
            buffer[0] == (byte)'B' && buffer[1] == (byte)'I' && buffer[2] == (byte)'K')
        {
            results.Add(new DiscoveredBuffer
            {
                FileOffset = fileOffset,
                VirtualAddress = va,
                FormatType = "BIK",
                Details = "Bink video",
                EstimatedSize = gapSize
            });
        }

        // zlib streams: 0x78 followed by 0x9C/0x01/0xDA (check first 64 bytes)
        if (results.Count == 0)
        {
            for (var i = 0; i < Math.Min(len - 1, 64); i++)
            {
                if (buffer[i] != 0x78 || buffer[i + 1] is not (0x9C or 0x01 or 0xDA))
                {
                    continue;
                }

                var level = buffer[i + 1] switch
                {
                    0x9C => "default",
                    0xDA => "best",
                    0x01 => "none",
                    _ => "unknown"
                };

                results.Add(new DiscoveredBuffer
                {
                    FileOffset = fileOffset + i,
                    VirtualAddress = va.HasValue ? va.Value + i : null,
                    FormatType = "zlib",
                    Details = $"zlib stream ({level} compression) at +{i}",
                    EstimatedSize = gapSize - i
                });
                break; // Only first zlib header per gap
            }
        }

        return results;
    }

    #endregion

    #region Helpers

    private long? VaToFileOffset(uint va)
    {
        if (va == 0)
        {
            return null;
        }

        return _minidumpInfo.VirtualAddressToFileOffset(EsmRecordFormat.Xbox360VaToLong(va));
    }

    private bool IsValidPointer(uint va)
    {
        return va != 0 && EsmRecordFormat.IsValidPointerInDump(va, _minidumpInfo);
    }

    private string? TryReadCString(uint va, int maxLen = 256)
    {
        if (va == 0)
        {
            return null;
        }

        var fileOffset = VaToFileOffset(va);
        if (fileOffset == null)
        {
            return null;
        }

        var readLen = (int)Math.Min(maxLen, _fileSize - fileOffset.Value);
        if (readLen < MinStringLength)
        {
            return null;
        }

        var buf = new byte[readLen];
        _accessor.ReadArray(fileOffset.Value, buf, 0, readLen);

        var end = 0;
        while (end < readLen && buf[end] != 0)
        {
            end++;
        }

        if (end < MinStringLength || end >= readLen)
        {
            return null;
        }

        for (var i = 0; i < end; i++)
        {
            if (buf[i] < 0x20 || buf[i] > 0x7E)
            {
                return null;
            }
        }

        return Encoding.ASCII.GetString(buf, 0, end);
    }

    private static int CountDistinctChars(string s)
    {
        Span<bool> seen = stackalloc bool[128];
        var count = 0;
        foreach (var c in s)
        {
            if (c < 128 && !seen[c])
            {
                seen[c] = true;
                count++;
            }
        }

        return count;
    }

    private int CountValidPointers(byte[] buffer)
    {
        var count = 0;
        for (var i = 0; i <= buffer.Length - 4; i += 4)
        {
            var ptr = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(i, 4));
            if (ptr != 0 && IsValidPointer(ptr))
            {
                count++;
            }
        }

        return count;
    }

    #endregion
}

#region Models

#endregion
