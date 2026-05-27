using System.CommandLine;
using FalloutXbox360Utils.Core.Formats.Nif;
using FalloutXbox360Utils.Core.Formats.Nif.Conversion;
using Spectre.Console;

namespace NifAnalyzer.Commands;

/// <summary>
///     Sweep every <c>.nif</c> in an Xbox 360 source tree, convert it via
///     <see cref="NifConverter.Convert" />, byte-diff the result against the matching PC
///     vanilla NIF (same relative path under <c>--pc-dir</c>), and report divergence stats:
///     identical, equal-size-with-diffs, size-mismatch (likely BSPackedAdditionalGeometryData
///     expansion or other structural differences expected from BE→LE conversion).
///     Used as a coarse correctness gate for the schema converter — if the BSPartFlag fix is
///     the only quirk, most equal-size pairs should diff at exactly zero positions.
/// </summary>
internal static class ParityCommands
{
    public static Command CreateConvertCommand()
    {
        var command = new Command("convert", "Convert a single Xbox 360 NIF to PC LE and write to disk");
        var fileArg = new Argument<string>("file") { Description = "Path to Xbox 360 NIF" };
        var outArg = new Argument<string>("output") { Description = "Output path for converted PC NIF" };
        command.Arguments.Add(fileArg);
        command.Arguments.Add(outArg);
        command.SetAction(p => ConvertOne(p.GetValue(fileArg)!, p.GetValue(outArg)!));
        return command;
    }

    private static void ConvertOne(string path, string output)
    {
        var bytes = File.ReadAllBytes(path);
        var result = NifConverter.Convert(bytes);
        if (!result.Success || result.OutputData is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Convert failed: {result.ErrorMessage}[/]");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllBytes(output, result.OutputData);
        AnsiConsole.MarkupLineInterpolated($"[green]Wrote {result.OutputData.Length:N0} bytes to {output}[/]");
    }

    public static Command CreateParityCommand()
    {
        var command = new Command("parity",
            "Sweep matched Xbox/PC NIF pairs and report byte-diff stats after conversion");
        var xboxDirArg = new Argument<string>("xbox-dir")
        {
            Description = "Directory containing Xbox 360 NIFs (e.g., Sample/Meshes/meshes_360_final)"
        };
        var pcDirArg = new Argument<string>("pc-dir")
        {
            Description = "Directory containing PC NIFs in matching layout (e.g., Sample/Meshes/meshes_pc)"
        };
        var limitOpt = new Option<int>("--limit")
        {
            Description = "Maximum number of pairs to process (0 = all)",
            DefaultValueFactory = _ => 0
        };
        var topOpt = new Option<int>("--top")
        {
            Description = "Show top N divergent files",
            DefaultValueFactory = _ => 10
        };
        var equalSizeOnlyOpt = new Option<bool>("--equal-size-only")
        {
            Description = "Skip pairs where Xbox-converted and PC NIFs have different lengths " +
                          "(those usually differ for legitimate structural reasons)"
        };
        var smallestEqualSizeOpt = new Option<int>("--smallest-equal-size")
        {
            Description = "Show N equal-size pairs with the SMALLEST non-zero diff (best for bug-hunting)",
            DefaultValueFactory = _ => 0
        };
        var identicalContainingOpt = new Option<string?>("--identical-containing")
        {
            Description = "Among identical-byte pairs, list those whose Xbox NIF references this block type (e.g. BSDismemberSkinInstance)"
        };
        command.Arguments.Add(xboxDirArg);
        command.Arguments.Add(pcDirArg);
        command.Options.Add(limitOpt);
        command.Options.Add(topOpt);
        command.Options.Add(equalSizeOnlyOpt);
        command.Options.Add(smallestEqualSizeOpt);
        command.Options.Add(identicalContainingOpt);
        command.SetAction(p => Sweep(
            p.GetValue(xboxDirArg)!,
            p.GetValue(pcDirArg)!,
            p.GetValue(limitOpt),
            p.GetValue(topOpt),
            p.GetValue(equalSizeOnlyOpt),
            p.GetValue(smallestEqualSizeOpt),
            p.GetValue(identicalContainingOpt)));
        return command;
    }

    private static void Sweep(string xboxDir, string pcDir, int limit, int top, bool equalSizeOnly, int smallestEqualSize, string? identicalContaining)
    {
        var xboxRoot = new DirectoryInfo(xboxDir);
        var pcRoot = new DirectoryInfo(pcDir);
        if (!xboxRoot.Exists || !pcRoot.Exists)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Missing input directory.[/] xbox={xboxRoot.Exists} pc={pcRoot.Exists}");
            return;
        }

        var xboxNifs = xboxRoot.EnumerateFiles("*.nif", SearchOption.AllDirectories).ToList();
        AnsiConsole.MarkupLineInterpolated($"Xbox NIFs: [cyan]{xboxNifs.Count:N0}[/]");

        var pairs = new List<(FileInfo Xbox, FileInfo Pc)>(xboxNifs.Count);
        foreach (var xboxFile in xboxNifs)
        {
            var rel = Path.GetRelativePath(xboxRoot.FullName, xboxFile.FullName);
            var pcCandidate = new FileInfo(Path.Combine(pcRoot.FullName, rel));
            if (pcCandidate.Exists)
            {
                pairs.Add((xboxFile, pcCandidate));
            }
        }

        AnsiConsole.MarkupLineInterpolated($"Matched pairs: [cyan]{pairs.Count:N0}[/]");
        if (limit > 0 && pairs.Count > limit)
        {
            pairs = pairs.Take(limit).ToList();
            AnsiConsole.MarkupLineInterpolated($"Limited to: [cyan]{pairs.Count:N0}[/]");
        }

        var stats = new Stats();
        var divergent = new List<DivergentPair>();
        var identicalWithType = new List<string>();

        foreach (var (xboxFile, pcFile) in pairs)
        {
            stats.Total++;
            byte[] xboxBytes;
            byte[] pcBytes;
            try
            {
                xboxBytes = File.ReadAllBytes(xboxFile.FullName);
                pcBytes = File.ReadAllBytes(pcFile.FullName);
            }
            catch
            {
                stats.ReadFailed++;
                continue;
            }

            NifConversionResult result;
            try
            {
                result = NifConverter.Convert(xboxBytes);
            }
            catch (Exception ex)
            {
                stats.ConvertThrew++;
                divergent.Add(new DivergentPair(xboxFile.FullName, pcFile.FullName,
                    -1, -1, $"convert threw: {ex.GetType().Name}: {ex.Message}"));
                continue;
            }

            if (!result.Success || result.OutputData is null)
            {
                stats.ConvertFailed++;
                divergent.Add(new DivergentPair(xboxFile.FullName, pcFile.FullName,
                    -1, -1, result.ErrorMessage ?? "convert failed"));
                continue;
            }

            var converted = result.OutputData;
            if (converted.Length != pcBytes.Length)
            {
                stats.SizeMismatch++;
                if (!equalSizeOnly)
                {
                    divergent.Add(new DivergentPair(xboxFile.FullName, pcFile.FullName,
                        Math.Abs(converted.Length - pcBytes.Length), -1,
                        $"size mismatch: converted={converted.Length:N0} pc={pcBytes.Length:N0}"));
                }

                continue;
            }

            // Track whether xbox source had the geometry-packed block. Useful for explaining
            // size-mismatch entries (packed blocks expand into NiTriShapeData on PC).
            if (ContainsBlockType(xboxBytes, "BSPackedAdditionalGeometryData"))
            {
                stats.XboxHadPackedGeometry++;
            }

            var diffCount = CountByteDiffs(converted, pcBytes);
            if (diffCount == 0)
            {
                stats.Identical++;
                if (identicalContaining is not null && ContainsBlockType(converted, identicalContaining))
                {
                    identicalWithType.Add(Path.GetRelativePath(xboxRoot.FullName, xboxFile.FullName));
                }
            }
            else
            {
                stats.Diff++;
                stats.DiffByteTotal += diffCount;

                // The most valuable signal for finding converter bugs is "same structure,
                // different bytes". Parse both files and compare structural metadata
                // (block count, block types, block sizes, strings). When everything
                // structural lines up but bytes still differ, the diff *must* be a value-
                // level conversion bug — most likely an endian-swap quirk like BSPartFlag.
                var structureMatch = StructureMatches(converted, pcBytes, out var diffByBlockType);
                if (structureMatch)
                {
                    stats.StructureMatchDiff++;
                    foreach (var (typeName, count) in diffByBlockType)
                    {
                        stats.DiffBytesByBlockType.TryGetValue(typeName, out var existing);
                        stats.DiffBytesByBlockType[typeName] = existing + count;
                        stats.DiffFilesByBlockType.TryGetValue(typeName, out var fileExisting);
                        stats.DiffFilesByBlockType[typeName] = fileExisting + (count > 0 ? 1 : 0);
                    }
                }

                divergent.Add(new DivergentPair(xboxFile.FullName, pcFile.FullName,
                    diffCount, FindFirstDiffOffset(converted, pcBytes),
                    structureMatch ? "structure-match" : null));
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Parity sweep summary[/]");
        AnsiConsole.MarkupLineInterpolated($"  total pairs:          [cyan]{stats.Total:N0}[/]");
        AnsiConsole.MarkupLineInterpolated($"  identical bytes:      [green]{stats.Identical:N0}[/]");
        AnsiConsole.MarkupLineInterpolated($"  size match, diffs:    [yellow]{stats.Diff:N0}[/]");
        AnsiConsole.MarkupLineInterpolated($"  size mismatch:        [yellow]{stats.SizeMismatch:N0}[/]");
        AnsiConsole.MarkupLineInterpolated($"  convert failed:       [red]{stats.ConvertFailed:N0}[/]");
        AnsiConsole.MarkupLineInterpolated($"  convert threw:        [red]{stats.ConvertThrew:N0}[/]");
        AnsiConsole.MarkupLineInterpolated($"  read failed:          [red]{stats.ReadFailed:N0}[/]");
        AnsiConsole.MarkupLineInterpolated($"  xbox had packed geom: [cyan]{stats.XboxHadPackedGeometry:N0}[/]");
        AnsiConsole.MarkupLineInterpolated($"  structure-match diffs:[red]{stats.StructureMatchDiff:N0}[/] [grey](these are the converter-bug candidates)[/]");

        if (stats.StructureMatchDiff > 0 && stats.DiffBytesByBlockType.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Structure-match diff bytes attributed to block type[/]");
            AnsiConsole.MarkupLine("[grey](each row = a block type that still has byte-level diffs after structural match)[/]");
            var typeTable = new Table().Border(TableBorder.Simple);
            typeTable.AddColumn("Block Type");
            typeTable.AddColumn(new TableColumn("Diff Bytes").RightAligned());
            typeTable.AddColumn(new TableColumn("Files").RightAligned());
            foreach (var (typeName, bytes) in stats.DiffBytesByBlockType.OrderByDescending(kv => kv.Value))
            {
                var files = stats.DiffFilesByBlockType.TryGetValue(typeName, out var f) ? f : 0;
                typeTable.AddRow(typeName, bytes.ToString("N0"), files.ToString("N0"));
            }

            AnsiConsole.Write(typeTable);
        }
        if (stats.Diff > 0)
        {
            AnsiConsole.MarkupLineInterpolated($"  total diff bytes:     [yellow]{stats.DiffByteTotal:N0}[/]");
            AnsiConsole.MarkupLineInterpolated($"  avg per diff file:    [yellow]{stats.DiffByteTotal / Math.Max(1, stats.Diff):N0}[/]");
        }

        if (top > 0 && divergent.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLineInterpolated($"[bold]Top {Math.Min(top, divergent.Count)} divergent pairs (by diff-byte count):[/]");
            var table = new Table().Border(TableBorder.Simple);
            table.AddColumn("Diffs");
            table.AddColumn("FirstAt");
            table.AddColumn("File");
            table.AddColumn("Note");
            foreach (var p in divergent.OrderByDescending(d => d.DiffCount).Take(top))
            {
                table.AddRow(
                    p.DiffCount.ToString("N0"),
                    p.FirstDiffOffset >= 0 ? $"0x{p.FirstDiffOffset:X}" : "—",
                    Path.GetRelativePath(xboxRoot.FullName, p.XboxPath),
                    p.Note ?? "");
            }
            AnsiConsole.Write(table);
        }

        if (identicalContaining is not null)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLineInterpolated(
                $"[bold]Identical-byte pairs containing '{identicalContaining}':[/] [green]{identicalWithType.Count}[/]");
            foreach (var path in identicalWithType.Take(20))
            {
                AnsiConsole.WriteLine($"  {path}");
            }

            if (identicalWithType.Count > 20)
            {
                AnsiConsole.MarkupLineInterpolated($"  ...and [cyan]{identicalWithType.Count - 20}[/] more");
            }
        }

        if (smallestEqualSize > 0)
        {
            // Limit to structure-match diffs — these are the real converter-bug candidates.
            // Pairs with structural differences (different block count / strings / sizes)
            // have legitimate authoring drift, not converter bugs.
            var smallest = divergent
                .Where(d => d.Note == "structure-match" && d.DiffCount > 0)
                .OrderBy(d => d.DiffCount)
                .Take(smallestEqualSize)
                .ToList();
            if (smallest.Count > 0)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLineInterpolated($"[bold]Smallest {smallest.Count} equal-size diffs (best for bug-hunting):[/]");
                var table = new Table().Border(TableBorder.Simple);
                table.AddColumn("Diffs");
                table.AddColumn("FirstAt");
                table.AddColumn("File");
                foreach (var p in smallest)
                {
                    table.AddRow(
                        p.DiffCount.ToString("N0"),
                        $"0x{p.FirstDiffOffset:X}",
                        Path.GetRelativePath(xboxRoot.FullName, p.XboxPath));
                }
                AnsiConsole.Write(table);
            }
        }
    }

    private static int CountByteDiffs(byte[] a, byte[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        var diffs = 0;
        for (var i = 0; i < len; i++)
        {
            if (a[i] != b[i])
            {
                diffs++;
            }
        }

        diffs += Math.Abs(a.Length - b.Length);
        return diffs;
    }

    private static int FindFirstDiffOffset(byte[] a, byte[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        for (var i = 0; i < len; i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }

        return len;
    }

    private static bool ContainsBlockType(byte[] nifBytes, string blockType)
    {
        try
        {
            var info = NifParser.Parse(nifBytes);
            return info?.Blocks.Any(b => b.TypeName == blockType) == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Compare two NIF buffers structurally. Returns true when block count, per-block
    ///     type names + sizes, and the string table are all identical — i.e. the only
    ///     remaining bytewise differences must be value-level conversion errors. Populates
    ///     <paramref name="diffByBlockType" /> with diff-byte counts per block type, so the
    ///     caller can aggregate which types are responsible for the remaining bytes.
    /// </summary>
    private static bool StructureMatches(byte[] convertedBytes, byte[] pcBytes,
        out Dictionary<string, long> diffByBlockType)
    {
        diffByBlockType = new Dictionary<string, long>(StringComparer.Ordinal);
        NifInfo? convInfo;
        NifInfo? pcInfo;
        try
        {
            convInfo = NifParser.Parse(convertedBytes);
            pcInfo = NifParser.Parse(pcBytes);
        }
        catch
        {
            return false;
        }

        if (convInfo is null || pcInfo is null)
        {
            return false;
        }

        if (convInfo.BlockCount != pcInfo.BlockCount)
        {
            return false;
        }

        if (!convInfo.BlockTypeNames.SequenceEqual(pcInfo.BlockTypeNames, StringComparer.Ordinal))
        {
            return false;
        }

        if (!convInfo.Strings.SequenceEqual(pcInfo.Strings, StringComparer.Ordinal))
        {
            return false;
        }

        for (var i = 0; i < convInfo.Blocks.Count; i++)
        {
            if (convInfo.Blocks[i].Size != pcInfo.Blocks[i].Size
                || !string.Equals(convInfo.Blocks[i].TypeName, pcInfo.Blocks[i].TypeName, StringComparison.Ordinal))
            {
                return false;
            }
        }

        // Structure matches — attribute any diff bytes to their block.
        for (var i = 0; i < convInfo.Blocks.Count; i++)
        {
            var block = convInfo.Blocks[i];
            var blockDiffs = 0L;
            for (var p = block.DataOffset; p < block.DataOffset + block.Size && p < convertedBytes.Length && p < pcBytes.Length; p++)
            {
                if (convertedBytes[p] != pcBytes[p])
                {
                    blockDiffs++;
                }
            }

            if (blockDiffs > 0)
            {
                diffByBlockType.TryGetValue(block.TypeName, out var existing);
                diffByBlockType[block.TypeName] = existing + blockDiffs;
            }
        }

        return true;
    }

    private sealed class Stats
    {
        public int Total;
        public int Identical;
        public int Diff;
        public int SizeMismatch;
        public int ConvertFailed;
        public int ConvertThrew;
        public int ReadFailed;
        public long DiffByteTotal;
        public int XboxHadPackedGeometry;
        public int StructureMatchDiff;
        public Dictionary<string, long> DiffBytesByBlockType { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> DiffFilesByBlockType { get; } = new(StringComparer.Ordinal);
    }

    private sealed record DivergentPair(string XboxPath, string PcPath, int DiffCount, int FirstDiffOffset, string? Note);
}
