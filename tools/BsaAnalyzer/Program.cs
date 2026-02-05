using FalloutXbox360Utils.Core.Formats.Bsa;

namespace BsaAnalyzer;

/// <summary>
///     Tool for analyzing BSA archives and verifying file extraction correctness.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            PrintUsage();
            return 1;
        }

        var command = args[0].ToLowerInvariant();
        var bsaPath = args[1];

        if (!File.Exists(bsaPath))
        {
            Console.WriteLine($"ERROR: BSA file not found: {bsaPath}");
            return 1;
        }

        return command switch
        {
            "find" when args.Length >= 3 => FindFile(bsaPath, args[2]),
            "inspect" when args.Length >= 3 => InspectFile(bsaPath, args[2]),
            "compare" when args.Length >= 4 => CompareExtracted(bsaPath, args[2], args[3]),
            "rawdump" when args.Length >= 4 => RawDump(bsaPath, long.Parse(args[2]), int.Parse(args[3])),
            "stats" => ShowStats(bsaPath),
            _ => PrintUsage()
        };
    }

    private static int PrintUsage()
    {
        Console.WriteLine("BsaAnalyzer - BSA archive analysis tool");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  BsaAnalyzer find <bsa> <pattern>       - Find files matching pattern");
        Console.WriteLine("  BsaAnalyzer inspect <bsa> <filename>   - Inspect a file's raw bytes in BSA");
        Console.WriteLine("  BsaAnalyzer compare <bsa> <filename> <extracted>  - Compare BSA vs extracted");
        Console.WriteLine("  BsaAnalyzer rawdump <bsa> <offset> <length>       - Dump raw bytes at offset");
        Console.WriteLine("  BsaAnalyzer stats <bsa>                - Show file type statistics");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  BsaAnalyzer find archive.bsa \"lily*.xma\"");
        Console.WriteLine("  BsaAnalyzer inspect archive.bsa \"sound/voice/test.xma\"");
        Console.WriteLine("  BsaAnalyzer compare archive.bsa \"sound/voice/test.xma\" extracted.xma");
        return 1;
    }

    private static int FindFile(string bsaPath, string pattern)
    {
        var archive = BsaParser.Parse(bsaPath);
        var searchPattern = pattern.Replace("*", "").ToLowerInvariant();
        var matches = archive.AllFiles
            .Where(f => f.FullPath.Contains(searchPattern, StringComparison.OrdinalIgnoreCase))
            .Take(50)
            .ToList();

        Console.WriteLine($"Found {matches.Count} files matching '{pattern}':");
        Console.WriteLine();

        foreach (var file in matches)
        {
            Console.WriteLine($"  {file.FullPath}");
            Console.WriteLine($"    Offset: 0x{file.Offset:X8} ({file.Offset})");
            Console.WriteLine($"    Size: {file.Size} bytes");
            Console.WriteLine($"    Compression Toggle: {file.CompressionToggle}");
            Console.WriteLine();
        }

        return 0;
    }

    private static int InspectFile(string bsaPath, string filename)
    {
        var archive = BsaParser.Parse(bsaPath);
        var searchName = filename.ToLowerInvariant();

        var file = archive.AllFiles.FirstOrDefault(f =>
            f.FullPath.Equals(searchName, StringComparison.OrdinalIgnoreCase) ||
            f.FullPath.Contains(searchName, StringComparison.OrdinalIgnoreCase));

        if (file == null)
        {
            Console.WriteLine($"File not found: {filename}");
            return 1;
        }

        Console.WriteLine($"File: {file.FullPath}");
        Console.WriteLine($"Offset in BSA: 0x{file.Offset:X8} ({file.Offset})");
        Console.WriteLine($"Size: {file.Size} bytes");
        Console.WriteLine($"Compression Toggle: {file.CompressionToggle}");
        Console.WriteLine($"Archive Default Compressed: {archive.Header.DefaultCompressed}");
        Console.WriteLine($"Actual Compressed: {archive.Header.DefaultCompressed != file.CompressionToggle}");
        Console.WriteLine();

        // Read raw bytes from BSA
        using var fs = File.OpenRead(bsaPath);
        fs.Position = file.Offset;

        var readSize = Math.Min((int)file.Size, 256);
        var buffer = new byte[readSize];
        var bytesRead = fs.Read(buffer, 0, readSize);

        Console.WriteLine($"First {bytesRead} bytes at offset 0x{file.Offset:X8}:");
        Console.WriteLine();
        HexDump(buffer, bytesRead, file.Offset);

        // Try to identify file type
        Console.WriteLine();
        Console.WriteLine("File type analysis:");
        IdentifyFileType(buffer);

        // Show surrounding context
        Console.WriteLine();
        Console.WriteLine("--- Context: 64 bytes BEFORE file offset ---");
        if (file.Offset >= 64)
        {
            fs.Position = file.Offset - 64;
            var before = new byte[64];
            fs.Read(before, 0, 64);
            HexDump(before, 64, file.Offset - 64);
        }
        else
        {
            Console.WriteLine("(At start of file)");
        }

        return 0;
    }

    private static int CompareExtracted(string bsaPath, string filename, string extractedPath)
    {
        if (!File.Exists(extractedPath))
        {
            Console.WriteLine($"Extracted file not found: {extractedPath}");
            return 1;
        }

        var archive = BsaParser.Parse(bsaPath);
        var searchName = filename.ToLowerInvariant();

        var file = archive.AllFiles.FirstOrDefault(f =>
            f.FullPath.Contains(searchName, StringComparison.OrdinalIgnoreCase));

        if (file == null)
        {
            Console.WriteLine($"File not found in BSA: {filename}");
            return 1;
        }

        // Read from BSA
        using var extractor = new BsaExtractor(bsaPath);
        var bsaData = extractor.ExtractFile(file);

        // Read extracted file
        var extractedData = File.ReadAllBytes(extractedPath);

        Console.WriteLine($"BSA file: {file.FullPath}");
        Console.WriteLine($"  BSA offset: 0x{file.Offset:X8}");
        Console.WriteLine($"  BSA size: {file.Size} bytes");
        Console.WriteLine($"  Extracted size: {bsaData.Length} bytes (after decompression if any)");
        Console.WriteLine();
        Console.WriteLine($"Extracted file: {extractedPath}");
        Console.WriteLine($"  File size: {extractedData.Length} bytes");
        Console.WriteLine();

        if (bsaData.Length != extractedData.Length)
        {
            Console.WriteLine($"SIZE MISMATCH: BSA={bsaData.Length}, Extracted={extractedData.Length}");
        }
        else if (bsaData.SequenceEqual(extractedData))
        {
            Console.WriteLine("MATCH: Extracted file matches BSA content exactly");
        }
        else
        {
            Console.WriteLine("CONTENT MISMATCH: Files have same size but different content");

            // Find first difference
            for (var i = 0; i < bsaData.Length; i++)
            {
                if (bsaData[i] != extractedData[i])
                {
                    Console.WriteLine($"  First difference at offset 0x{i:X8}");
                    Console.WriteLine($"    BSA byte: 0x{bsaData[i]:X2}");
                    Console.WriteLine($"    Extracted byte: 0x{extractedData[i]:X2}");
                    break;
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("--- BSA content first 64 bytes ---");
        HexDump(bsaData, Math.Min(64, bsaData.Length), 0);

        Console.WriteLine();
        Console.WriteLine("--- Extracted file first 64 bytes ---");
        HexDump(extractedData, Math.Min(64, extractedData.Length), 0);

        return 0;
    }

    private static int RawDump(string bsaPath, long offset, int length)
    {
        using var fs = File.OpenRead(bsaPath);

        if (offset >= fs.Length)
        {
            Console.WriteLine($"Offset 0x{offset:X8} is beyond file size {fs.Length}");
            return 1;
        }

        fs.Position = offset;
        var buffer = new byte[Math.Min(length, (int)(fs.Length - offset))];
        var bytesRead = fs.Read(buffer, 0, buffer.Length);

        Console.WriteLine($"Raw dump at offset 0x{offset:X8}, {bytesRead} bytes:");
        Console.WriteLine();
        HexDump(buffer, bytesRead, offset);

        return 0;
    }

    private static int ShowStats(string bsaPath)
    {
        var archive = BsaParser.Parse(bsaPath);

        Console.WriteLine($"BSA: {Path.GetFileName(bsaPath)}");
        Console.WriteLine($"Version: {archive.Header.Version}");
        Console.WriteLine($"Folders: {archive.Header.FolderCount}");
        Console.WriteLine($"Files: {archive.Header.FileCount}");
        Console.WriteLine($"Default Compressed: {archive.Header.DefaultCompressed}");
        Console.WriteLine();

        // Extension statistics
        var extStats = archive.AllFiles
            .GroupBy(f => Path.GetExtension(f.Name ?? "").ToLowerInvariant())
            .Select(g => new { Ext = g.Key, Count = g.Count(), TotalSize = g.Sum(f => (long)f.Size) })
            .OrderByDescending(x => x.Count)
            .ToList();

        Console.WriteLine("File type statistics:");
        Console.WriteLine($"{"Extension",-12} {"Count",10} {"Total Size",15}");
        Console.WriteLine(new string('-', 40));

        foreach (var stat in extStats)
        {
            Console.WriteLine($"{stat.Ext,-12} {stat.Count,10:N0} {stat.TotalSize,15:N0}");
        }

        // Sample first bytes of each extension type
        Console.WriteLine();
        Console.WriteLine("Sample first bytes by extension:");

        using var fs = File.OpenRead(bsaPath);

        foreach (var ext in extStats.Take(5))
        {
            var sample = archive.AllFiles.First(f =>
                Path.GetExtension(f.Name ?? "").Equals(ext.Ext, StringComparison.OrdinalIgnoreCase));

            fs.Position = sample.Offset;
            var buffer = new byte[16];
            fs.Read(buffer, 0, 16);
            var hex = string.Join(" ", buffer.Select(b => b.ToString("X2")));

            Console.WriteLine($"  {ext.Ext,-8}: {hex}");
        }

        return 0;
    }

    private static void HexDump(byte[] data, int length, long baseOffset)
    {
        for (var i = 0; i < length; i += 16)
        {
            var lineOffset = baseOffset + i;
            Console.Write($"{lineOffset:X8}  ");

            // Hex bytes
            for (var j = 0; j < 16; j++)
            {
                if (i + j < length)
                {
                    Console.Write($"{data[i + j]:X2} ");
                }
                else
                {
                    Console.Write("   ");
                }

                if (j == 7)
                {
                    Console.Write(" ");
                }
            }

            Console.Write(" |");

            // ASCII
            for (var j = 0; j < 16 && i + j < length; j++)
            {
                var b = data[i + j];
                Console.Write(b is >= 32 and < 127 ? (char)b : '.');
            }

            Console.WriteLine("|");
        }
    }

    private static void IdentifyFileType(byte[] data)
    {
        if (data.Length < 4)
        {
            Console.WriteLine("  Too small to identify");
            return;
        }

        var magic = BitConverter.ToUInt32(data, 0);
        var magicStr = System.Text.Encoding.ASCII.GetString(data, 0, 4);

        Console.WriteLine($"  First 4 bytes: 0x{magic:X8} = '{magicStr.Replace("\0", "\\0")}'");

        // Known signatures
        if (magicStr == "RIFF")
        {
            Console.WriteLine("  Type: RIFF container (WAV/XMA)");
            if (data.Length >= 12)
            {
                var format = System.Text.Encoding.ASCII.GetString(data, 8, 4);
                Console.WriteLine($"  RIFF format: {format}");
            }
        }
        else if (magicStr == "OggS")
        {
            Console.WriteLine("  Type: OGG Vorbis audio");
        }
        else if (magic == 0x00000001)
        {
            Console.WriteLine("  Type: Likely LIP (lip sync) file");
            if (data.Length >= 8)
            {
                var size = BitConverter.ToUInt32(data, 4);
                Console.WriteLine($"  LIP data size field: {size}");
            }
        }
        else if (magic == 0xFFFFFFFF)
        {
            Console.WriteLine("  Type: Likely empty/placeholder or corrupted");
        }
        else if (data[0] == 0x00 && data[1] == 0x00)
        {
            Console.WriteLine("  Type: Unknown (starts with null bytes)");
        }
        else
        {
            Console.WriteLine("  Type: Unknown format");

            // Check for text
            var isPrintable = data.Take(32).All(b => b is >= 32 and < 127 or 0x0A or 0x0D or 0x09);
            if (isPrintable)
            {
                Console.WriteLine("  Note: Content appears to be text/ASCII");
            }
        }
    }
}
