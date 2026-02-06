using System.Buffers.Binary;
using System.Text;

namespace FalloutXbox360Utils.Core;

internal sealed partial class RuntimeBufferAnalyzer
{
    #region Binary Signature Scanning

    /// <summary>
    ///     Scan binary data gaps for known file format signatures.
    /// </summary>
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

    /// <summary>
    ///     Scan a buffer for known file format signatures.
    /// </summary>
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

    #region Structure Walking

    /// <summary>
    ///     Walk a large structure and count pointers.
    /// </summary>
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
    ///     Walk a ModelLoader structure and extract loaded file paths.
    /// </summary>
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

    /// <summary>
    ///     Walk a BSTextureManager structure.
    /// </summary>
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

    #endregion

    #region Manager Walking

    /// <summary>
    ///     Walk known manager globals to extract runtime data.
    /// </summary>
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

    /// <summary>
    ///     Extract strings from StringPool and AsciiText gaps.
    /// </summary>
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

        // Sort samples by length descending - longer strings are more meaningful
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

    #endregion
}
