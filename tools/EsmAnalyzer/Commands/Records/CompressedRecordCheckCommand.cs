using System.Buffers;
using System.Buffers.Binary;
using System.CommandLine;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Records;
using Spectre.Console;

namespace EsmAnalyzer.Commands.Records;

/// <summary>
///     Diagnostic: scans a DMP (or ESM), or a directory of them, for compressed ESM records (flag
///     0x00040000) and reports — per file and in aggregate — how many decompress cleanly, how many are
///     real-but-truncated (a lenient partial inflate recovers usable bytes), and how many are dead
///     (false-positive signature matches / unrecoverable). Answers whether the DMP path encounters
///     compressed (and big-endian) records, and how much data a partial-recovery feature would salvage —
///     relevant because the pre-July-2010 crash dumps are the only surviving data from that era.
/// </summary>
internal static class CompressedRecordCheckCommand
{
    // A lenient inflate must clear this many bytes for the record to count as "recoverable" (enough for an
    // EDID + a subrecord or two); smaller yields are treated as false-positive noise.
    private const int MinUsefulBytes = 32;
    private const uint MaxSaneDecompressedSize = 16 * 1024 * 1024;

    internal static Command Create()
    {
        var command = new Command("compressed-record-check",
            "Report compressed ESM record counts, endianness, and clean/partial/dead decompression for a DMP/ESM or a directory");

        var pathArg = new Argument<string>("path") { Description = "A .dmp/.esm file, or a directory of them" };
        command.Arguments.Add(pathArg);
        command.SetAction(parseResult => Execute(parseResult.GetValue(pathArg)!));
        return command;
    }

    private sealed class FileStats
    {
        public required string Name;
        public int MainRecords;
        public int BeMain;
        public int Compressed;
        public int CompressedBe;
        public int CleanOk;
        public int PartialRecoverable;
        public long PartialBytes;
        public int Dead;
    }

    private static int Execute(string path)
    {
        List<string> files;
        if (Directory.Exists(path))
        {
            files = Directory.GetFiles(path, "*.dmp").Concat(Directory.GetFiles(path, "*.esm"))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        else if (File.Exists(path))
        {
            files = [path];
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Not found:[/] {Markup.Escape(path)}");
            return 1;
        }

        var all = new List<FileStats>();
        foreach (var file in files)
        {
            AnsiConsole.MarkupLine($"[grey]Scanning[/] {Markup.Escape(Path.GetFileName(file))} ...");
            all.Add(AnalyzeFile(file));
        }

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("File")
            .AddColumn("Records (BE)", c => c.RightAligned())
            .AddColumn("Compressed (BE)", c => c.RightAligned())
            .AddColumn("Clean", c => c.RightAligned())
            .AddColumn("Partial", c => c.RightAligned())
            .AddColumn("Recoverable", c => c.RightAligned())
            .AddColumn("Dead", c => c.RightAligned());

        foreach (var s in all)
        {
            _ = table.AddRow(
                Markup.Escape(s.Name),
                $"{s.MainRecords:N0} ({s.BeMain:N0})",
                $"{s.Compressed:N0} ({s.CompressedBe:N0})",
                s.CleanOk.ToString(),
                s.PartialRecoverable > 0 ? $"[green]{s.PartialRecoverable}[/]" : "0",
                s.PartialBytes > 0 ? $"{s.PartialBytes / 1024.0:N1} KB" : "-",
                s.Dead > 0 ? $"[yellow]{s.Dead}[/]" : "0");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);

        if (all.Count > 1)
        {
            var withPartials = all.Count(s => s.PartialRecoverable > 0);
            var totalPartial = all.Sum(s => s.PartialRecoverable);
            var totalBytes = all.Sum(s => s.PartialBytes);
            AnsiConsole.MarkupLine(
                $"\n[cyan]{withPartials}[/] of [cyan]{all.Count}[/] files have ≥ 1 real truncated (partially recoverable) compressed record. " +
                $"Total: [green]{totalPartial}[/] records, ~[green]{totalBytes / 1024.0:N1} KB[/] recoverable.");
        }

        return 0;
    }

    private static FileStats AnalyzeFile(string file)
    {
        var stats = new FileStats { Name = Path.GetFileName(file) };
        var fileSize = new FileInfo(file).Length;
        using var mmf = MemoryMappedFile.CreateFromFile(
            file, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

        var scan = EsmRecordScanner.ScanForRecordsMemoryMapped(accessor, fileSize);
        var mainRecords = scan.MainRecords.Where(r => r.RecordType != "GRUP").ToList();
        stats.MainRecords = mainRecords.Count;
        stats.BeMain = mainRecords.Count(r => r.IsBigEndian);

        var compressed = mainRecords.Where(r => r.IsCompressed).ToList();
        stats.Compressed = compressed.Count;
        stats.CompressedBe = compressed.Count(r => r.IsBigEndian);

        var context = new RecordParserContext(scan, formIdCorrelations: null, accessor, fileSize, minidumpInfo: null);
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            foreach (var record in compressed)
            {
                if (context.ReadRecordData(record, buffer) is { Size: > 0 })
                {
                    stats.CleanOk++;
                    continue;
                }

                // Strict decompress failed. Attempt a lenient partial inflate of whatever payload bytes are
                // present, to see if a real truncated record's leading subrecords are salvageable.
                var payloadStart = record.Offset + record.HeaderSize;
                var available = (int)Math.Min(record.DataSize, fileSize - payloadStart);
                var declSane = IsDeclaredSizeSane(accessor, payloadStart, available, record.IsBigEndian);
                var recovered = available > 6 ? LenientInflate(accessor, payloadStart + 4, available - 4) : 0;

                if (declSane && recovered >= MinUsefulBytes)
                {
                    stats.PartialRecoverable++;
                    stats.PartialBytes += recovered;
                }
                else
                {
                    stats.Dead++;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return stats;
    }

    // The first 4 payload bytes are the declared decompressed size (in the record's endianness). Sane = a
    // small positive value, which separates real truncated records from garbage false-positive matches.
    private static bool IsDeclaredSizeSane(MemoryMappedViewAccessor accessor, long payloadStart, int available, bool bigEndian)
    {
        if (available < 4)
        {
            return false;
        }

        var pfx = new byte[4];
        accessor.ReadArray(payloadStart, pfx, 0, 4);
        var size = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(pfx) : BinaryPrimitives.ReadUInt32LittleEndian(pfx);
        return size is > 0 and <= MaxSaneDecompressedSize;
    }

    // Raw-inflate the zlib stream (skip the 2-byte CMF/FLG header), keeping whatever bytes come out before
    // the truncation point. Returns the count of recovered bytes (0 if the header is invalid / nothing inflates).
    private static int LenientInflate(MemoryMappedViewAccessor accessor, long zlibStart, int zlibLen)
    {
        if (zlibLen <= 2)
        {
            return 0;
        }

        var payload = new byte[zlibLen];
        accessor.ReadArray(zlibStart, payload, 0, zlibLen);

        var recovered = 0;
        try
        {
            using var rawIn = new MemoryStream(payload, 2, payload.Length - 2);
            using var deflate = new DeflateStream(rawIn, CompressionMode.Decompress);
            var chunk = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                int n;
                while ((n = deflate.Read(chunk, 0, chunk.Length)) > 0)
                {
                    recovered += n;
                }
            }
            catch (InvalidDataException)
            {
                // Truncated/short stream — keep the bytes inflated before the cut.
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }
        }
        catch (InvalidDataException)
        {
            // Bad zlib header → not a real compressed stream.
        }

        return recovered;
    }
}
