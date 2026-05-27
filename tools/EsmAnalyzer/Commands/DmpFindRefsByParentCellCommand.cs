using System.Buffers.Binary;
using System.CommandLine;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Minidump;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     For each REFR-shaped heap object in a DMP, dereferences <c>pParentCell</c> and
///     reads the target CELL's FormID. Reports REFRs whose runtime parent matches one of
///     the target cell FormIDs.
///
///     Closes the gap between <c>dmp sweep-refrs</c> (only counts NULL-pParentCell refs in
///     per-REFR output) and <c>dmp cell-inventory</c> (only walks cells the pCellMap knew
///     about). A REFR can be parented to a CELL the regular parser never reached — neither
///     command would surface it; this one does.
/// </summary>
internal static class DmpFindRefsByParentCellCommand
{
    private const int VftableOffset = 0;
    private const int FormIdOffset = 12;
    private const int BaseObjectPtrOffset = 48;
    private const int LocationXOffset = 64;
    private const int LocationYOffset = 68;
    private const int LocationZOffset = 72;
    private const int RefScaleOffset = 76;
    private const int ParentCellOffset = 80;
    private const int RefrStructTail = 92;

    private const uint ModuleVaLo = 0x82000000u;
    private const uint ModuleVaHi = 0x84000000u;
    private const uint HeapVaLo = 0x40000000u;

    private const float WorldExtent = 500_000f;
    private const float MaxZ = 200_000f;

    public static Command CreateFindRefsByParentCellCommand()
    {
        var command = new Command(
            "find-refs-by-parent-cell",
            "Scan a DMP (or directory of DMPs) for REFRs whose pParentCell — when dereferenced — " +
            "points to one of the supplied CELL FormIDs.");

        var dumpArg = new Argument<string>("input")
        {
            Description = "Path to a .dmp file or directory of .dmp files"
        };
        var cellFidOpt = new Option<string[]>("--cell")
        {
            Description = "Target CELL FormID (e.g. 0x000E1A22). Repeat for multiple targets.",
            Required = true,
            AllowMultipleArgumentsPerToken = true
        };
        var earlyOpt = new Option<bool>("--early")
        {
            Description = "Force early-era REFR layout (shift -4). Default: probe per-DMP."
        };

        command.Arguments.Add(dumpArg);
        command.Options.Add(cellFidOpt);
        command.Options.Add(earlyOpt);

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(dumpArg)!;
            var fids = parseResult.GetValue(cellFidOpt)!;
            var early = parseResult.GetValue(earlyOpt);
            return Run(input, fids, early);
        });

        return command;
    }

    private static int Run(string input, string[] cellFidArgs, bool forceEarly)
    {
        var targets = new HashSet<uint>();
        foreach (var f in cellFidArgs)
        {
            if (TryParseHexUInt(f, out var fid))
            {
                targets.Add(fid);
            }
        }

        if (targets.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No valid --cell FormIDs supplied.[/]");
            return 1;
        }

        var dmpFiles = ResolveDumps(input);
        if (dmpFiles.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]No .dmp files found at:[/] {Markup.Escape(input)}");
            return 1;
        }

        AnsiConsole.MarkupLine(
            $"[blue]Scanning[/] {dmpFiles.Count} DMP(s) for refs parented to: " +
            string.Join(", ", targets.Select(f => $"0x{f:X8}")));

        var totalMatches = 0;
        var dumpsWithMatches = 0;

        foreach (var dmp in dmpFiles.OrderBy(d => d, StringComparer.Ordinal))
        {
            var matches = ScanOne(dmp, targets, forceEarly);
            if (matches.Count == 0)
            {
                continue;
            }

            dumpsWithMatches++;
            totalMatches += matches.Count;

            AnsiConsole.MarkupLine($"\n[yellow]{Path.GetFileName(dmp)}[/]: {matches.Count} match(es)");
            var byParent = matches.GroupBy(m => m.ParentFid).OrderBy(g => g.Key);
            foreach (var group in byParent)
            {
                AnsiConsole.MarkupLine($"  Parent CELL 0x{group.Key:X8} ({group.Count()} REFRs):");
                foreach (var m in group.OrderBy(m => m.RefFid))
                {
                    AnsiConsole.WriteLine(
                        $"    REFR 0x{m.RefFid:X8}  base=0x{m.BaseFid:X8}  " +
                        $"pos=({m.X:F1}, {m.Y:F1}, {m.Z:F1})  scale={m.Scale:F2}");
                }
            }
        }

        AnsiConsole.MarkupLine(
            $"\n[green]Done.[/] {dumpsWithMatches}/{dmpFiles.Count} dumps had matches, " +
            $"{totalMatches} REFR(s) total.");
        return 0;
    }

    private sealed record RefMatch(
        uint RefFid, uint BaseFid, uint ParentFid, float X, float Y, float Z, float Scale);

    private static List<RefMatch> ScanOne(string dumpPath, HashSet<uint> targets, bool forceEarly)
    {
        var matches = new List<RefMatch>();
        try
        {
            var info = MinidumpParser.Parse(dumpPath);
            var fileInfo = new FileInfo(dumpPath);
            using var mmf = MemoryMappedFile.CreateFromFile(
                dumpPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

            var shift = forceEarly ? -4 : ProbeLayout(accessor, info);
            var xOff = LocationXOffset + shift;
            var yOff = LocationYOffset + shift;
            var zOff = LocationZOffset + shift;
            var scaleOff = RefScaleOffset + shift;
            var pCellOff = ParentCellOffset + shift;
            var baseOff = BaseObjectPtrOffset + shift;

            var tesFormBuf = new byte[16];
            var fileSize = fileInfo.Length;
            var seen = new HashSet<uint>();

            foreach (var region in info.MemoryRegions)
            {
                if (region.Size < RefrStructTail)
                {
                    continue;
                }

                var buf = new byte[region.Size];
                accessor.ReadArray(region.FileOffset, buf, 0, (int)region.Size);
                var maxStart = (int)(region.Size - Math.Max(zOff + 4, pCellOff + 4));

                for (var start = 0; start <= maxStart; start += 4)
                {
                    var vft = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(start + VftableOffset));
                    if (vft < ModuleVaLo || vft >= ModuleVaHi)
                    {
                        continue;
                    }

                    var fid = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(start + FormIdOffset));
                    if (fid == 0 || fid == 0xFFFFFFFFu)
                    {
                        continue;
                    }

                    var x = BinaryPrimitives.ReadSingleBigEndian(buf.AsSpan(start + xOff));
                    if (!IsBoundedFloat(x, WorldExtent))
                    {
                        continue;
                    }

                    var y = BinaryPrimitives.ReadSingleBigEndian(buf.AsSpan(start + yOff));
                    if (!IsBoundedFloat(y, WorldExtent))
                    {
                        continue;
                    }

                    var z = BinaryPrimitives.ReadSingleBigEndian(buf.AsSpan(start + zOff));
                    if (!IsBoundedFloat(z, MaxZ))
                    {
                        continue;
                    }

                    var scale = BinaryPrimitives.ReadSingleBigEndian(buf.AsSpan(start + scaleOff));
                    if (float.IsNaN(scale) || scale <= 0.01f || scale > 100f)
                    {
                        continue;
                    }

                    var pCellVa = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(start + pCellOff));
                    if (pCellVa == 0 || pCellVa < HeapVaLo || pCellVa >= ModuleVaHi)
                    {
                        continue;
                    }

                    var pCellFo = info.VirtualAddressToFileOffset(pCellVa);
                    if (!pCellFo.HasValue || pCellFo.Value + 16 > fileSize)
                    {
                        continue;
                    }

                    try
                    {
                        accessor.ReadArray(pCellFo.Value, tesFormBuf, 0, 16);
                    }
                    catch
                    {
                        continue;
                    }

                    // TESForm at +4 is cFormType; we don't enforce a specific value here
                    // because cell types are 0x39 in modern builds but the +1 shift at
                    // 0x46 doesn't affect cells. Still, sanity-check the upper bound.
                    if (tesFormBuf[4] > 200)
                    {
                        continue;
                    }

                    var parentFid = BinaryPrimitives.ReadUInt32BigEndian(tesFormBuf.AsSpan(12));
                    if (!targets.Contains(parentFid))
                    {
                        continue;
                    }

                    if (!seen.Add(fid))
                    {
                        continue;
                    }

                    var basePtr = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(start + baseOff));
                    uint baseFid = 0;
                    if (basePtr >= HeapVaLo && basePtr < ModuleVaHi)
                    {
                        var baseFo = info.VirtualAddressToFileOffset(basePtr);
                        if (baseFo.HasValue && baseFo.Value + 16 <= fileSize)
                        {
                            try
                            {
                                accessor.ReadArray(baseFo.Value, tesFormBuf, 0, 16);
                                baseFid = BinaryPrimitives.ReadUInt32BigEndian(tesFormBuf.AsSpan(12));
                            }
                            catch
                            {
                                // leave baseFid = 0
                            }
                        }
                    }

                    matches.Add(new RefMatch(fid, baseFid, parentFid, x, y, z, scale));
                }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]{Path.GetFileName(dumpPath)}: {Markup.Escape(ex.Message)}[/]");
        }

        return matches;
    }

    private static int ProbeLayout(MemoryMappedViewAccessor accessor, MinidumpInfo info)
    {
        long probedBytes = 0;
        const long probeBudget = 32 * 1024 * 1024;

        var finalHits = 0;
        var earlyHits = 0;

        foreach (var region in info.MemoryRegions)
        {
            if (probedBytes >= probeBudget)
            {
                break;
            }

            var bytesToRead = (int)Math.Min(region.Size, probeBudget - probedBytes);
            if (bytesToRead < 96)
            {
                continue;
            }

            var buf = new byte[bytesToRead];
            accessor.ReadArray(region.FileOffset, buf, 0, bytesToRead);
            for (var i = 0; i + RefrStructTail <= bytesToRead; i += 4)
            {
                if (ProbeRefrShape(buf, i, 0))
                {
                    finalHits++;
                }
                if (ProbeRefrShape(buf, i, -4))
                {
                    earlyHits++;
                }
            }
            probedBytes += bytesToRead;
        }

        return earlyHits > finalHits * 1.2 ? -4 : 0;
    }

    private static bool ProbeRefrShape(byte[] buf, int start, int shift)
    {
        var xOff = LocationXOffset + shift;
        var pCellOff = ParentCellOffset + shift;
        if (start + pCellOff + 4 > buf.Length || start + xOff + 4 > buf.Length)
        {
            return false;
        }
        var vft = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(start + VftableOffset));
        if (vft < ModuleVaLo || vft >= ModuleVaHi)
        {
            return false;
        }
        var fid = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(start + FormIdOffset));
        if (fid == 0 || fid == 0xFFFFFFFFu)
        {
            return false;
        }
        var x = BinaryPrimitives.ReadSingleBigEndian(buf.AsSpan(start + xOff));
        var y = BinaryPrimitives.ReadSingleBigEndian(buf.AsSpan(start + xOff + 4));
        var z = BinaryPrimitives.ReadSingleBigEndian(buf.AsSpan(start + xOff + 8));
        if (!IsBoundedFloat(x, WorldExtent) || !IsBoundedFloat(y, WorldExtent) || !IsBoundedFloat(z, MaxZ))
        {
            return false;
        }
        var scale = BinaryPrimitives.ReadSingleBigEndian(buf.AsSpan(start + RefScaleOffset + shift));
        if (float.IsNaN(scale) || scale <= 0.01f || scale > 100f)
        {
            return false;
        }
        var pCell = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(start + pCellOff));
        return pCell == 0 || (pCell >= HeapVaLo && pCell < ModuleVaHi);
    }

    private static bool IsBoundedFloat(float v, float bound)
    {
        return !float.IsNaN(v) && !float.IsInfinity(v) && Math.Abs(v) <= bound;
    }

    private static List<string> ResolveDumps(string input)
    {
        if (Directory.Exists(input))
        {
            return Directory.GetFiles(input, "*.dmp")
                .Where(f => !Path.GetFileName(f).Contains("hangdump", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }
        if (File.Exists(input))
        {
            return [input];
        }
        return [];
    }

    private static bool TryParseHexUInt(string? s, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s))
        {
            return false;
        }
        var span = s.AsSpan();
        if (span.Length > 2 && span[0] == '0' && (span[1] == 'x' || span[1] == 'X'))
        {
            span = span[2..];
        }
        return uint.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}
