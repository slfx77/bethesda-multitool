using System.Buffers.Binary;
using System.CommandLine;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Text;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Coverage;
using BethesdaMultitool.Core.Formats.Bsa.Models;
using BethesdaMultitool.Core.Formats.Bsa.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Dmp;

/// <summary>
///     Probes uncovered minidump gap regions for recoverable compressed/container data:
///     BSA archive headers whose folder/file tables can be VA-stitched and walked in-memory,
///     and RFC-1950 zlib streams that trial-inflate to asset-shaped content. This is a
///     measurement command (audit item M3) — it decides whether a recovery pass is worth
///     building; it does not extract anything.
/// </summary>
public static class DmpRecoveryProbeCommand
{
    private const int BsaHeaderSize = 36;
    private const int BsaMagicScanWindow = 4096;
    private const int ZlibScanChunkSize = 4 * 1024 * 1024;
    private const int SniffPrefixSize = 512;

    public static Command Create()
    {
        var inputArg = new Argument<string>("path")
        {
            Description = "Path to a .dmp file or directory containing .dmp files"
        };
        var csvOpt = new Option<string?>("--csv")
        {
            Description = "Directory to write recovery_probe_bsa.csv / recovery_probe_zlib.csv (omit for console-only)"
        };
        var maxZlibStreamsOpt = new Option<int>("--max-zlib-streams")
        {
            Description = "Maximum zlib candidates to trial-inflate per dump",
            DefaultValueFactory = _ => 50
        };
        var maxInflateMbOpt = new Option<int>("--max-inflate-mb")
        {
            Description = "Total inflated-byte budget per dump, in MB (8 MB per-stream cap applies on top)",
            DefaultValueFactory = _ => 64
        };
        var recursiveOpt = new Option<bool>("-r", "--recursive")
        {
            Description = "Search input directory recursively"
        };
        var fastOpt = new Option<bool>("--fast")
        {
            Description = "Use lightweight DMP analysis before coverage; faster but less faithful to GUI coverage"
        };

        var command = new Command(
            "recovery-probe",
            "Probe uncovered DMP gap regions for walkable BSA tables and trial-inflatable zlib streams");
        command.Arguments.Add(inputArg);
        command.Options.Add(csvOpt);
        command.Options.Add(maxZlibStreamsOpt);
        command.Options.Add(maxInflateMbOpt);
        command.Options.Add(recursiveOpt);
        command.Options.Add(fastOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var csvDir = parseResult.GetValue(csvOpt);
            var maxZlibStreams = parseResult.GetValue(maxZlibStreamsOpt);
            var maxInflateMb = parseResult.GetValue(maxInflateMbOpt);
            var recursive = parseResult.GetValue(recursiveOpt);
            var fast = parseResult.GetValue(fastOpt);
            await ExecuteAsync(input, csvDir, maxZlibStreams, maxInflateMb, recursive, fast, cancellationToken);
        });

        return command;
    }

    private static async Task ExecuteAsync(
        string input,
        string? csvDir,
        int maxZlibStreams,
        int maxInflateMb,
        bool recursive,
        bool fast,
        CancellationToken cancellationToken)
    {
        var dumps = DiscoverDumps(input, recursive);
        if (dumps.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]No .dmp files found at:[/] {Markup.Escape(input)}");
            return;
        }

        var options = new RecoveryProbeOptions
        {
            MaxZlibStreams = Math.Max(0, maxZlibStreams),
            MaxTotalInflateBytes = Math.Max(1, maxInflateMb) * 1024L * 1024L
        };

        AnsiConsole.MarkupLine(
            $"[blue]Recovery probe:[/] {dumps.Count:N0} DMP(s)" +
            (csvDir != null ? $", CSV -> {Markup.Escape(csvDir)}" : ""));

        var results = new List<RecoveryProbeDumpResult>();
        var analyzer = new MinidumpAnalyzer();
        for (var i = 0; i < dumps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dump = dumps[i];
            var fileName = Path.GetFileName(dump);
            AnsiConsole.MarkupLine($"[cyan][[{i + 1}/{dumps.Count}]][/] {Markup.Escape(fileName)}");

            try
            {
                var result = await AnalyzeDumpAsync(analyzer, dump, fast, cancellationToken);
                if (result.MinidumpInfo is not { IsValid: true } minidump)
                {
                    AnsiConsole.MarkupLine("  [red]not a valid minidump[/]");
                    results.Add(RecoveryProbeDumpResult.Failed(fileName, "not a valid minidump"));
                    continue;
                }

                using var mmf = MemoryMappedFile.CreateFromFile(
                    dump, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
                using var accessor = mmf.CreateViewAccessor(0, result.FileSize, MemoryMappedFileAccess.Read);

                var coverage = CoverageAnalyzer.Analyze(result, accessor);
                if (coverage.Error != null)
                {
                    AnsiConsole.MarkupLine($"  [red]coverage failed:[/] {Markup.Escape(coverage.Error)}");
                    results.Add(RecoveryProbeDumpResult.Failed(fileName, coverage.Error));
                    continue;
                }

                var probe = ProbeDump(fileName, minidump, coverage, new MmfMemoryAccessor(accessor), options);
                results.Add(probe);

                var parsedBsa = probe.BsaRows.Count(r => r.ParseSuccess);
                var cleanZlib = probe.ZlibRows.Count(r => r.Success);
                AnsiConsole.MarkupLine(
                    $"  BSA: [green]{probe.BsaMagicHits:N0}[/] magic hit(s), {parsedBsa:N0} parsed; " +
                    $"zlib: [green]{probe.ZlibCandidatesFound:N0}[/] candidate(s), " +
                    $"{probe.ZlibAttempted:N0} attempted, {cleanZlib:N0} clean, " +
                    $"{probe.TotalInflatedBytes / 1024.0 / 1024.0:F1} MB inflated");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"  [red]failed:[/] {Markup.Escape(ex.Message)}");
                results.Add(RecoveryProbeDumpResult.Failed(fileName, ex.Message));
            }
        }

        RenderSummary(results);

        if (csvDir != null)
        {
            Directory.CreateDirectory(csvDir);
            var bsaPath = Path.Combine(csvDir, "recovery_probe_bsa.csv");
            var zlibPath = Path.Combine(csvDir, "recovery_probe_zlib.csv");
            await File.WriteAllTextAsync(
                bsaPath, WriteBsaCsv(results.SelectMany(r => r.BsaRows).ToList()), cancellationToken);
            await File.WriteAllTextAsync(
                zlibPath, WriteZlibCsv(results.SelectMany(r => r.ZlibRows).ToList()), cancellationToken);
            AnsiConsole.MarkupLine($"[green]Wrote:[/] {Markup.Escape(bsaPath)}");
            AnsiConsole.MarkupLine($"[green]Wrote:[/] {Markup.Escape(zlibPath)}");
        }

        AnsiConsole.MarkupLine(
            "[dim]Note: zlib candidates start inside coverage gaps by construction (gaps are the " +
            "unrecognized intervals); extends_beyond_gap marks streams whose consumed range crosses " +
            "into already-recognized data.[/]");
    }

    private static async Task<AnalysisResult> AnalyzeDumpAsync(
        MinidumpAnalyzer analyzer,
        string dump,
        bool fast,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(dump);
        using var mmf = MemoryMappedFile.CreateFromFile(dump, FileMode.Open, null, 0,
            MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        return await analyzer.AnalyzeAsync(
            dump,
            accessor,
            fileInfo.Length,
            includeMetadata: !fast,
            cancellationToken: cancellationToken);
    }

    // ===== probe core (internal for tests) =====

    /// <summary>
    ///     Runs both probe branches over every coverage gap of one dump. Internal seam so tests can
    ///     drive it with a synthetic <see cref="MinidumpInfo" /> + sparse accessor + synthetic gaps.
    /// </summary>
    internal static RecoveryProbeDumpResult ProbeDump(
        string dumpName,
        MinidumpInfo minidump,
        CoverageResult coverage,
        IMemoryAccessor accessor,
        RecoveryProbeOptions options)
    {
        var result = new RecoveryProbeDumpResult { Dump = dumpName };

        foreach (var gap in coverage.Gaps)
        {
            var gapEnd = Math.Min(gap.FileOffset + gap.Size, coverage.FileSize);
            if (gapEnd <= gap.FileOffset)
            {
                continue;
            }

            ProbeGapForBsa(dumpName, minidump, accessor, coverage.FileSize, gap, gapEnd, options, result);
            ScanGapForZlib(dumpName, minidump, accessor, coverage.FileSize, gap, gapEnd, options, result);
        }

        return result;
    }

    // ===== BSA branch =====

    private static void ProbeGapForBsa(
        string dumpName,
        MinidumpInfo minidump,
        IMemoryAccessor accessor,
        long fileSize,
        CoverageGap gap,
        long gapEnd,
        RecoveryProbeOptions options,
        RecoveryProbeDumpResult result)
    {
        var gapSize = gapEnd - gap.FileOffset;
        if (gapSize < BsaHeaderSize)
        {
            return;
        }

        // Check the gap start plus the first 4KB at 4-byte stride for the "BSA\0" magic.
        var prefixLen = (int)Math.Min(gapSize, BsaMagicScanWindow);
        var prefix = new byte[prefixLen];
        accessor.ReadArray(gap.FileOffset, prefix, 0, prefixLen);

        for (var off = 0; off + 4 <= prefixLen; off += 4)
        {
            if (prefix[off] != (byte)'B' || prefix[off + 1] != (byte)'S' ||
                prefix[off + 2] != (byte)'A' || prefix[off + 3] != 0)
            {
                continue;
            }

            result.BsaMagicHits++;
            if (result.BsaRows.Count >= options.MaxBsaProbes)
            {
                continue; // keep counting magic hits, stop full probes
            }

            result.BsaRows.Add(
                ProbeBsaHeader(dumpName, minidump, accessor, fileSize, gap, gap.FileOffset + off, options));
        }
    }

    /// <summary>
    ///     VA-stitches the BSA header + folder records + file record blocks + name block starting at
    ///     <paramref name="fileOffset" /> (zero-filling unmapped VA ranges), then tries the real
    ///     <see cref="BsaParser" /> on the stitched bytes and samples how much of the declared
    ///     entry-data VA range is captured in the dump.
    /// </summary>
    internal static BsaProbeRow ProbeBsaHeader(
        string dumpName,
        MinidumpInfo minidump,
        IMemoryAccessor accessor,
        long fileSize,
        CoverageGap gap,
        long fileOffset,
        RecoveryProbeOptions options)
    {
        var va = minidump.FileOffsetToVirtualAddress(fileOffset);
        var (header, _) = ReadVaOrFlat(minidump, accessor, fileSize, fileOffset, va, BsaHeaderSize);

        var version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        var flags = (BsaArchiveFlags)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        var folderCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
        var fileCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));
        var totalFolderNameLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24));
        var totalFileNameLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28));

        var row = new BsaProbeRow
        {
            Dump = dumpName,
            GapOffset = gap.FileOffset,
            GapSize = gap.Size,
            FileOffset = fileOffset,
            VirtualAddress = va,
            Version = version,
            Flags = (uint)flags,
            FolderCount = folderCount,
            FileCount = fileCount
        };

        if (version is < 103 or > 105)
        {
            return row with { Error = $"unsupported version {version}" };
        }

        if (folderCount > 100_000 || fileCount > 1_000_000 ||
            totalFolderNameLength > 16 * 1024 * 1024 || totalFileNameLength > 64 * 1024 * 1024)
        {
            return row with
            {
                Error = $"implausible header (folders={folderCount}, files={fileCount}, " +
                        $"folderNames={totalFolderNameLength}, fileNames={totalFileNameLength})"
            };
        }

        // On-disk layout: 36-byte header, folderCount folder records (16B, or 24B at v105),
        // per-folder [1 length byte + folder name] (when IncludeDirectoryNames) interleaved with
        // that folder's 16B file records, then the null-terminated file names block.
        var folderRecordSize = version >= 105 ? 24L : 16L;
        var stitchLen = BsaHeaderSize
                        + folderCount * folderRecordSize
                        + (flags.HasFlag(BsaArchiveFlags.IncludeDirectoryNames)
                            ? folderCount + (long)totalFolderNameLength
                            : 0)
                        + fileCount * 16L
                        + (flags.HasFlag(BsaArchiveFlags.IncludeFileNames) ? totalFileNameLength : 0L);

        if (stitchLen > options.MaxBsaTableBytes)
        {
            return row with { Error = $"declared table span {stitchLen:N0} bytes exceeds probe cap" };
        }

        var (stitched, present) = ReadVaOrFlat(minidump, accessor, fileSize, fileOffset, va, (int)stitchLen);
        row = row with { TableBytes = stitchLen, TablePresentBytes = present };

        BsaArchive archive;
        try
        {
            using var stream = new MemoryStream(stitched, false);
            archive = BsaParser.Parse(stream);
        }
        catch (Exception ex)
        {
            return row with { Error = $"{ex.GetType().Name}: {ex.Message}" };
        }

        var resolvableNames = archive.AllFiles.Count(f => !string.IsNullOrWhiteSpace(f.Name));
        long declaredDataBytes = 0;
        foreach (var file in archive.AllFiles)
        {
            declaredDataBytes += file.Size;
        }

        long sampledBytes = 0;
        long residentBytes = 0;
        if (va.HasValue)
        {
            (sampledBytes, residentBytes) = SampleEntryDataResidency(minidump, va.Value, archive);
        }

        return row with
        {
            ParseSuccess = true,
            FolderCount = archive.Header.FolderCount,
            FileCount = archive.Header.FileCount,
            ResolvableNames = resolvableNames,
            DeclaredDataBytes = declaredDataBytes,
            EntryDataSampledBytes = sampledBytes,
            EntryDataResidentBytes = residentBytes
        };
    }

    /// <summary>
    ///     Walks every parsed file record's declared [archiveVa + Offset, +Size) range in 64KB chunks
    ///     and sums how many of those bytes fall in captured minidump regions.
    /// </summary>
    internal static (long SampledBytes, long ResidentBytes) SampleEntryDataResidency(
        MinidumpInfo minidump,
        long archiveVa,
        BsaArchive archive)
    {
        const int sampleChunk = 64 * 1024;
        long sampled = 0;
        long resident = 0;

        foreach (var file in archive.AllFiles)
        {
            long size = file.Size;
            if (size == 0)
            {
                continue;
            }

            size = Math.Min(size, 512L * 1024 * 1024); // cap a lying size dword
            var entryVa = archiveVa + file.Offset;
            for (long pos = 0; pos < size; pos += sampleChunk)
            {
                var chunk = (int)Math.Min(sampleChunk, size - pos);
                sampled += chunk;
                if (minidump.IsVaRangeCaptured(entryVa + pos, chunk))
                {
                    resident += chunk;
                }
            }
        }

        return (sampled, resident);
    }

    /// <summary>
    ///     Reads <paramref name="length" /> bytes as they appear in VA space starting at
    ///     <paramref name="va" /> (zero-filling unmapped ranges), following the CarveExtractor
    ///     ReadFileData shape. Falls back to a flat file read when the offset has no VA mapping.
    ///     Returns the bytes and how many of them were actually present in the dump.
    /// </summary>
    internal static (byte[] Data, long BytesPresent) ReadVaOrFlat(
        MinidumpInfo minidump,
        IMemoryAccessor accessor,
        long fileSize,
        long fileOffset,
        long? va,
        int length)
    {
        var data = new byte[length];

        if (va.HasValue)
        {
            var startVa = va.Value;
            var endVa = startVa + length;
            long present = 0;
            foreach (var region in minidump.GetRegionsInRange(startVa, endVa))
            {
                var overlapStart = Math.Max(region.VirtualAddress, startVa);
                var overlapEnd = Math.Min(region.VirtualAddress + region.Size, endVa);
                var overlapSize = (int)(overlapEnd - overlapStart);
                var bufferOffset = (int)(overlapStart - startVa);
                var regionFileOffset = region.FileOffset + (overlapStart - region.VirtualAddress);
                if (regionFileOffset >= fileSize)
                {
                    continue; // lying region table: file range not actually in the dump
                }

                overlapSize = (int)Math.Min(overlapSize, fileSize - regionFileOffset);
                present += accessor.ReadArray(regionFileOffset, data, bufferOffset, overlapSize);
            }

            return (data, present);
        }

        var flatLength = (int)Math.Max(0, Math.Min(length, fileSize - fileOffset));
        var read = flatLength > 0 ? accessor.ReadArray(fileOffset, data, 0, flatLength) : 0;
        return (data, read);
    }

    // ===== zlib branch =====

    /// <summary>
    ///     Whether a byte pair is a plausible RFC-1950 zlib header: CMF 0x78 (deflate, 32K window),
    ///     FLG restricted to the common compression levels {0x01, 0x9C, 0xDA}, and the RFC-1950
    ///     FCHECK constraint (the 16-bit CMF·FLG value is a multiple of 31).
    /// </summary>
    internal static bool IsZlibCandidate(byte first, byte second)
    {
        if (first != 0x78)
        {
            return false;
        }

        if (second is not (0x01 or 0x9C or 0xDA))
        {
            return false;
        }

        return ((first << 8) | second) % 31 == 0;
    }

    private static void ScanGapForZlib(
        string dumpName,
        MinidumpInfo minidump,
        IMemoryAccessor accessor,
        long fileSize,
        CoverageGap gap,
        long gapEnd,
        RecoveryProbeOptions options,
        RecoveryProbeDumpResult result)
    {
        var gapSize = gapEnd - gap.FileOffset;
        if (gapSize < 2)
        {
            return;
        }

        var buffer = new byte[(int)Math.Min(ZlibScanChunkSize + 1, gapSize)];
        long pos = 0;
        var nextAttemptableOffset = gap.FileOffset;

        while (true)
        {
            var remaining = gapSize - pos;
            if (remaining < 2)
            {
                break;
            }

            var toRead = (int)Math.Min(buffer.Length, remaining);
            accessor.ReadArray(gap.FileOffset + pos, buffer, 0, toRead);

            for (var i = 0; i + 1 < toRead; i++)
            {
                if (buffer[i] != 0x78 || !IsZlibCandidate(buffer[i], buffer[i + 1]))
                {
                    continue;
                }

                result.ZlibCandidatesFound++;

                var candidateOffset = gap.FileOffset + pos + i;
                if (candidateOffset < nextAttemptableOffset ||
                    result.ZlibAttempted >= options.MaxZlibStreams ||
                    result.TotalInflatedBytes >= options.MaxTotalInflateBytes)
                {
                    continue; // count it, but budget/dedupe says do not attempt
                }

                result.ZlibAttempted++;
                var perStreamCap = Math.Min(
                    options.PerStreamInflateCap,
                    options.MaxTotalInflateBytes - result.TotalInflatedBytes);

                // The compressed stream physically continues past the gap boundary as long as the
                // file bytes stay VA-contiguous, so the input window extends into recognized data
                // (recorded as ExtendsBeyondGap) up to a 64MB cap.
                var windowEnd = GetFlatReadableEnd(
                    minidump, candidateOffset, Math.Min(fileSize, candidateOffset + (64L << 20)));
                using var slice = new AccessorSliceStream(
                    accessor, candidateOffset, Math.Max(0, windowEnd - candidateOffset));
                var outcome = ProbeZlibStream(slice, perStreamCap);

                result.TotalInflatedBytes += outcome.InflatedBytes;
                result.ZlibRows.Add(new ZlibProbeRow
                {
                    Dump = dumpName,
                    GapOffset = gap.FileOffset,
                    GapSize = gap.Size,
                    CandidateOffset = candidateOffset,
                    CandidateVa = minidump.FileOffsetToVirtualAddress(candidateOffset),
                    HeaderBytes = $"78 {buffer[i + 1]:X2}",
                    Success = outcome.Success,
                    Error = outcome.Error ?? "",
                    ConsumedUpperBound = outcome.ConsumedUpperBound,
                    InflatedBytes = outcome.InflatedBytes,
                    HitCap = outcome.HitCap,
                    ContentSniff = outcome.ContentSniff,
                    ExtendsBeyondGap = candidateOffset + outcome.ConsumedUpperBound > gapEnd
                });

                if (outcome.Success && outcome.ConsumedUpperBound > 2)
                {
                    // Skip the interior of a cleanly-inflated stream so its inner bytes are not
                    // re-attempted as fresh candidates. ConsumedUpperBound over-reports by up to
                    // one decompressor read-ahead buffer; acceptable for a probe.
                    nextAttemptableOffset = candidateOffset + outcome.ConsumedUpperBound;
                }
            }

            if (toRead >= remaining)
            {
                break;
            }

            pos += toRead - 1; // 1-byte overlap so a header pair can span chunk boundaries
        }
    }

    /// <summary>
    ///     Trial-inflates a zlib stream from the current position of <paramref name="input" />,
    ///     capping output at <paramref name="maxInflateBytes" />. Never throws: failures are
    ///     reported in the outcome. ConsumedUpperBound is the input-position delta, which
    ///     over-reports actual consumption by up to one decompressor read-ahead buffer.
    /// </summary>
    internal static ZlibProbeOutcome ProbeZlibStream(Stream input, long maxInflateBytes)
    {
        var start = input.Position;
        var sniffBuffer = new byte[SniffPrefixSize];
        var sniffLength = 0;
        long inflated = 0;

        try
        {
            using var zlib = new ZLibStream(input, CompressionMode.Decompress, true);
            var buffer = new byte[64 * 1024];
            while (inflated < maxInflateBytes)
            {
                var toRead = (int)Math.Min(buffer.Length, maxInflateBytes - inflated);
                var read = zlib.Read(buffer, 0, toRead);
                if (read <= 0)
                {
                    break;
                }

                if (sniffLength < sniffBuffer.Length)
                {
                    var copy = Math.Min(read, sniffBuffer.Length - sniffLength);
                    Array.Copy(buffer, 0, sniffBuffer, sniffLength, copy);
                    sniffLength += copy;
                }

                inflated += read;
            }

            return new ZlibProbeOutcome(
                true,
                input.Position - start,
                inflated,
                inflated >= maxInflateBytes,
                SniffContent(sniffBuffer.AsSpan(0, sniffLength)),
                null);
        }
        catch (Exception ex)
        {
            return new ZlibProbeOutcome(
                false,
                input.Position - start,
                inflated,
                false,
                inflated > 0 ? SniffContent(sniffBuffer.AsSpan(0, sniffLength)) : "",
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    ///     Classifies an inflated prefix by magic: ESM (TES4/GRUP), NIF (Gamebryo/NetImmerse),
    ///     DDS, BSA, PNG, RIFF/WAVE (XMA), mostly-ASCII text, or other.
    /// </summary>
    internal static string SniffContent(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length == 0)
        {
            return "empty";
        }

        if (prefix.StartsWith("TES4"u8))
        {
            return "esm-tes4";
        }

        if (prefix.StartsWith("GRUP"u8))
        {
            return "esm-grup";
        }

        if (prefix.StartsWith("Gamebryo File Format"u8) || prefix.StartsWith("NetImmerse File Format"u8))
        {
            return "nif";
        }

        if (prefix.StartsWith("DDS "u8))
        {
            return "dds";
        }

        if (prefix.StartsWith("BSA\0"u8))
        {
            return "bsa";
        }

        if (prefix.Length >= 4 && prefix[0] == 0x89 && prefix[1] == (byte)'P' &&
            prefix[2] == (byte)'N' && prefix[3] == (byte)'G')
        {
            return "png";
        }

        if (prefix.StartsWith("RIFF"u8))
        {
            return prefix.Length >= 12 && prefix.Slice(8, 4).SequenceEqual("WAVE"u8)
                ? "riff-wave-xma"
                : "riff";
        }

        if (prefix.Length >= 16 && IsMostlyAscii(prefix))
        {
            return "ascii-text";
        }

        return "other";
    }

    private static bool IsMostlyAscii(ReadOnlySpan<byte> data)
    {
        var printable = 0;
        foreach (var b in data)
        {
            if (b is >= 32 and < 127 or 9 or 10 or 13)
            {
                printable++;
            }
        }

        return printable >= data.Length * 0.9;
    }

    /// <summary>
    ///     Returns the end of the run starting at <paramref name="fileOffset" /> where a flat file
    ///     read stays VA-correct (regions contiguous in both file offset and VA), clamped to
    ///     <paramref name="maxEnd" />. Mirrors CarveExtractor.ClampToFlatReadableRun.
    /// </summary>
    private static long GetFlatReadableEnd(MinidumpInfo minidump, long fileOffset, long maxEnd)
    {
        var region = minidump.FindRegionByFileOffset(fileOffset);
        if (region == null)
        {
            return Math.Min(maxEnd, fileOffset);
        }

        var flatFileEnd = region.FileOffset + region.Size;
        var vaEnd = region.VirtualAddress + region.Size;
        while (flatFileEnd < maxEnd)
        {
            var next = minidump.GetRegionsInRange(vaEnd, vaEnd + 1).FirstOrDefault();
            if (next == null || next.VirtualAddress != vaEnd || next.FileOffset != flatFileEnd)
            {
                break;
            }

            flatFileEnd += next.Size;
            vaEnd += next.Size;
        }

        return Math.Min(maxEnd, flatFileEnd);
    }

    // ===== output =====

    private static void RenderSummary(List<RecoveryProbeDumpResult> results)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[bold]Dump[/]");
        table.AddColumn(new TableColumn("[bold]BSA magic[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]BSA parsed[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Best names[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Best data %[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]zlib cand[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]tried[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]clean[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]inflated MB[/]").RightAligned());
        table.AddColumn("[bold]Top sniffs[/]");

        foreach (var r in results)
        {
            if (r.Error != null)
            {
                table.AddRow(Markup.Escape(r.Dump), "[red]failed[/]", "", "", "", "", "", "", "",
                    Markup.Escape(Truncate(r.Error, 60)));
                continue;
            }

            var parsed = r.BsaRows.Where(b => b.ParseSuccess).ToList();
            var bestNames = parsed.Count > 0 ? parsed.Max(b => b.ResolvableNames) : 0;
            var bestData = parsed.Count > 0
                ? parsed.Max(b => b.EntryDataResidentPercent)
                : 0;
            var clean = r.ZlibRows.Where(z => z.Success).ToList();
            var topSniffs = string.Join(", ", clean
                .GroupBy(z => z.ContentSniff)
                .OrderByDescending(g => g.Count())
                .Take(3)
                .Select(g => $"{g.Key}:{g.Count()}"));

            table.AddRow(
                Markup.Escape(r.Dump),
                r.BsaMagicHits.ToString("N0"),
                parsed.Count.ToString("N0"),
                bestNames.ToString("N0"),
                parsed.Count > 0 ? $"{bestData:F1}" : "",
                r.ZlibCandidatesFound.ToString("N0"),
                r.ZlibAttempted.ToString("N0"),
                clean.Count.ToString("N0"),
                (r.TotalInflatedBytes / 1024.0 / 1024.0).ToString("F1"),
                Markup.Escape(topSniffs));
        }

        AnsiConsole.Write(table);
    }

    private static string WriteBsaCsv(List<BsaProbeRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "dump,gap_offset,gap_size,bsa_file_offset,bsa_va,parse_success,error,version,flags," +
            "folder_count,file_count,resolvable_names,declared_data_bytes,table_bytes," +
            "table_present_bytes,table_present_percent,entry_data_sampled_bytes," +
            "entry_data_resident_bytes,entry_data_resident_percent");

        foreach (var row in rows
                     .OrderBy(r => r.Dump, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(r => r.FileOffset))
        {
            sb.AppendLine(string.Join(',',
                Csv(row.Dump),
                FormatHex(row.GapOffset),
                row.GapSize.ToString(),
                FormatHex(row.FileOffset),
                row.VirtualAddress.HasValue ? FormatHex(row.VirtualAddress.Value) : "",
                row.ParseSuccess.ToString().ToLowerInvariant(),
                Csv(row.Error),
                row.Version.ToString(),
                $"0x{row.Flags:X8}",
                row.FolderCount.ToString(),
                row.FileCount.ToString(),
                row.ResolvableNames.ToString(),
                row.DeclaredDataBytes.ToString(),
                row.TableBytes.ToString(),
                row.TablePresentBytes.ToString(),
                row.TableBytes > 0
                    ? (row.TablePresentBytes * 100.0 / row.TableBytes).ToString("F1")
                    : "",
                row.EntryDataSampledBytes.ToString(),
                row.EntryDataResidentBytes.ToString(),
                row.EntryDataSampledBytes > 0 ? row.EntryDataResidentPercent.ToString("F1") : ""));
        }

        return sb.ToString();
    }

    private static string WriteZlibCsv(List<ZlibProbeRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "dump,gap_offset,gap_size,candidate_file_offset,candidate_va,header_bytes,success," +
            "error,consumed_upper_bound,inflated_bytes,hit_cap,content_sniff,extends_beyond_gap");

        foreach (var row in rows
                     .OrderBy(r => r.Dump, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(r => r.CandidateOffset))
        {
            sb.AppendLine(string.Join(',',
                Csv(row.Dump),
                FormatHex(row.GapOffset),
                row.GapSize.ToString(),
                FormatHex(row.CandidateOffset),
                row.CandidateVa.HasValue ? FormatHex(row.CandidateVa.Value) : "",
                Csv(row.HeaderBytes),
                row.Success.ToString().ToLowerInvariant(),
                Csv(row.Error),
                row.ConsumedUpperBound.ToString(),
                row.InflatedBytes.ToString(),
                row.HitCap.ToString().ToLowerInvariant(),
                Csv(row.ContentSniff),
                row.ExtendsBeyondGap.ToString().ToLowerInvariant()));
        }

        return sb.ToString();
    }

    private static List<string> DiscoverDumps(string input, bool recursive)
    {
        if (File.Exists(input))
        {
            return Path.GetExtension(input).Equals(".dmp", StringComparison.OrdinalIgnoreCase)
                ? [input]
                : [];
        }

        if (!Directory.Exists(input))
        {
            return [];
        }

        return Directory
            .GetFiles(input, "*.dmp", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        value = value.Replace("\r", " ").Replace("\n", " ");
        if (!value.Contains(',') && !value.Contains('"'))
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string FormatHex(long value)
    {
        return $"0x{value:X8}";
    }

    private static string Truncate(string value, int max)
    {
        return value.Length <= max ? value : value[..max];
    }

    // ===== nested types =====

    /// <summary>Probe tuning knobs; defaults match the CLI defaults.</summary>
    internal sealed record RecoveryProbeOptions
    {
        public int MaxZlibStreams { get; init; } = 50;
        public long MaxTotalInflateBytes { get; init; } = 64L * 1024 * 1024;
        public long PerStreamInflateCap { get; init; } = 8L * 1024 * 1024;
        public int MaxBsaProbes { get; init; } = 64;
        public long MaxBsaTableBytes { get; init; } = 128L * 1024 * 1024;
    }

    /// <summary>Per-dump probe results across both branches.</summary>
    internal sealed class RecoveryProbeDumpResult
    {
        public required string Dump { get; init; }
        public string? Error { get; init; }
        public List<BsaProbeRow> BsaRows { get; } = [];
        public List<ZlibProbeRow> ZlibRows { get; } = [];
        public int BsaMagicHits { get; set; }
        public int ZlibCandidatesFound { get; set; }
        public int ZlibAttempted { get; set; }
        public long TotalInflatedBytes { get; set; }

        public static RecoveryProbeDumpResult Failed(string dump, string error)
        {
            return new RecoveryProbeDumpResult { Dump = dump, Error = error };
        }
    }

    /// <summary>One probed BSA magic hit inside a coverage gap.</summary>
    internal sealed record BsaProbeRow
    {
        public required string Dump { get; init; }
        public long GapOffset { get; init; }
        public long GapSize { get; init; }
        public long FileOffset { get; init; }
        public long? VirtualAddress { get; init; }
        public bool ParseSuccess { get; init; }
        public string Error { get; init; } = "";
        public uint Version { get; init; }
        public uint Flags { get; init; }
        public uint FolderCount { get; init; }
        public uint FileCount { get; init; }
        public int ResolvableNames { get; init; }
        public long DeclaredDataBytes { get; init; }
        public long TableBytes { get; init; }
        public long TablePresentBytes { get; init; }
        public long EntryDataSampledBytes { get; init; }
        public long EntryDataResidentBytes { get; init; }

        public double EntryDataResidentPercent => EntryDataSampledBytes > 0
            ? EntryDataResidentBytes * 100.0 / EntryDataSampledBytes
            : 0;
    }

    /// <summary>One attempted zlib candidate inside a coverage gap.</summary>
    internal sealed record ZlibProbeRow
    {
        public required string Dump { get; init; }
        public long GapOffset { get; init; }
        public long GapSize { get; init; }
        public long CandidateOffset { get; init; }
        public long? CandidateVa { get; init; }
        public string HeaderBytes { get; init; } = "";
        public bool Success { get; init; }
        public string Error { get; init; } = "";
        public long ConsumedUpperBound { get; init; }
        public long InflatedBytes { get; init; }
        public bool HitCap { get; init; }
        public string ContentSniff { get; init; } = "";
        public bool ExtendsBeyondGap { get; init; }
    }

    /// <summary>Result of one trial inflation.</summary>
    internal sealed record ZlibProbeOutcome(
        bool Success,
        long ConsumedUpperBound,
        long InflatedBytes,
        bool HitCap,
        string ContentSniff,
        string? Error);

    /// <summary>
    ///     Read-only seekable window [start, start+length) over an <see cref="IMemoryAccessor" />,
    ///     so trial inflation streams bytes on demand instead of materializing multi-MB windows.
    /// </summary>
    private sealed class AccessorSliceStream(IMemoryAccessor accessor, long start, long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = length - Position;
            if (remaining <= 0)
            {
                return 0;
            }

            var toRead = (int)Math.Min(count, remaining);
            var read = accessor.ReadArray(start + Position, buffer, offset, toRead);
            if (read <= 0)
            {
                return 0;
            }

            Position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            return Position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}
