using NifAnalyzer.Parsers;
using static NifAnalyzer.Utils.BinaryHelpers;

namespace NifAnalyzer.Commands;

/// <summary>
///     Commands for basic NIF information: info, blocks, block, compare.
/// </summary>
internal static class InfoCommands
{
    public static int Info(string path)
    {
        var data = File.ReadAllBytes(path);
        var nif = NifParser.Parse(data);

        Console.WriteLine($"File: {Path.GetFileName(path)}");
        Console.WriteLine($"Size: {data.Length:N0} bytes");
        Console.WriteLine();
        Console.WriteLine($"Version String: {nif.VersionString}");
        Console.WriteLine($"Version: {FormatVersion(nif.Version)}");
        Console.WriteLine($"Endian: {(nif.IsBigEndian ? "Big (Xbox 360)" : "Little (PC)")}");
        Console.WriteLine($"User Version: {nif.UserVersion}");
        Console.WriteLine($"BS Version: {nif.BsVersion}");
        Console.WriteLine($"Num Blocks: {nif.NumBlocks}");
        Console.WriteLine($"Block Types: {nif.BlockTypes.Count}");
        Console.WriteLine($"Strings: {nif.NumStrings}");
        Console.WriteLine($"Block Data Offset: 0x{nif.BlockDataOffset:X4}");

        return 0;
    }

    public static int Blocks(string path)
    {
        var data = File.ReadAllBytes(path);
        var nif = NifParser.Parse(data);

        Console.WriteLine($"File: {Path.GetFileName(path)} ({nif.NumBlocks} blocks)");
        Console.WriteLine($"Endian: {(nif.IsBigEndian ? "Big (Xbox 360)" : "Little (PC)")}");
        Console.WriteLine();
        Console.WriteLine($"{"Idx",-5} {"Offset",-12} {"Size",-10} {"Type",-40}");
        Console.WriteLine(new string('-', 75));

        var offset = nif.BlockDataOffset;
        for (var i = 0; i < nif.NumBlocks; i++)
        {
            var typeIdx = nif.BlockTypeIndices[i];
            var typeName = nif.BlockTypes[typeIdx];
            var size = nif.BlockSizes[i];

            Console.WriteLine($"{i,-5} 0x{offset,-10:X4} {size,-10} {typeName,-40}");
            offset += (int)size;
        }

        return 0;
    }

    public static int Block(string path, int blockIndex)
    {
        var data = File.ReadAllBytes(path);
        var nif = NifParser.Parse(data);

        if (blockIndex < 0 || blockIndex >= nif.NumBlocks)
        {
            Console.Error.WriteLine($"Block index {blockIndex} out of range (0-{nif.NumBlocks - 1})");
            return 1;
        }

        var offset = nif.GetBlockOffset(blockIndex);
        var typeName = nif.GetBlockTypeName(blockIndex);
        var size = (int)nif.BlockSizes[blockIndex];

        Console.WriteLine($"Block {blockIndex}: {typeName}");
        Console.WriteLine($"Offset: 0x{offset:X4}");
        Console.WriteLine($"Size: {size} bytes");
        Console.WriteLine($"Endian: {(nif.IsBigEndian ? "Big (Xbox 360)" : "Little (PC)")}");
        Console.WriteLine();

        // Hex dump first 128 bytes
        Console.WriteLine("First bytes:");
        HexDump(data, offset, Math.Min(128, size));

        return 0;
    }

    public static int Compare(string xboxPath, string pcPath)
    {
        var xboxData = File.ReadAllBytes(xboxPath);
        var pcData = File.ReadAllBytes(pcPath);

        var xbox = NifParser.Parse(xboxData);
        var pc = NifParser.Parse(pcData);

        Console.WriteLine("=== NIF Comparison ===");
        Console.WriteLine();
        Console.WriteLine($"{"Property",-25} {"Xbox 360",-20} {"PC",-20}");
        Console.WriteLine(new string('-', 65));
        Console.WriteLine($"{"File Size",-25} {xboxData.Length,-20:N0} {pcData.Length,-20:N0}");
        Console.WriteLine(
            $"{"Endian",-25} {(xbox.IsBigEndian ? "Big" : "Little"),-20} {(pc.IsBigEndian ? "Big" : "Little"),-20}");
        Console.WriteLine($"{"Version",-25} {FormatVersion(xbox.Version),-20} {FormatVersion(pc.Version),-20}");
        Console.WriteLine($"{"User Version",-25} {xbox.UserVersion,-20} {pc.UserVersion,-20}");
        Console.WriteLine($"{"BS Version",-25} {xbox.BsVersion,-20} {pc.BsVersion,-20}");
        Console.WriteLine($"{"Num Blocks",-25} {xbox.NumBlocks,-20} {pc.NumBlocks,-20}");
        Console.WriteLine($"{"Num Block Types",-25} {xbox.BlockTypes.Count,-20} {pc.BlockTypes.Count,-20}");
        Console.WriteLine($"{"Block Data Offset",-25} 0x{xbox.BlockDataOffset,-18:X4} 0x{pc.BlockDataOffset,-18:X4}");

        Console.WriteLine();
        Console.WriteLine("=== Block Type Comparison ===");
        Console.WriteLine();

        var allTypes = xbox.BlockTypes.Union(pc.BlockTypes).OrderBy(t => t).ToList();
        var xboxTypeCounts = xbox.BlockTypeIndices.GroupBy(i => xbox.BlockTypes[i])
            .ToDictionary(g => g.Key, g => g.Count());
        var pcTypeCounts = pc.BlockTypeIndices.GroupBy(i => pc.BlockTypes[i]).ToDictionary(g => g.Key, g => g.Count());

        Console.WriteLine($"{"Block Type",-40} {"Xbox",-8} {"PC",-8}");
        Console.WriteLine(new string('-', 60));
        foreach (var type in allTypes)
        {
            var xc = xboxTypeCounts.GetValueOrDefault(type, 0);
            var pcc = pcTypeCounts.GetValueOrDefault(type, 0);
            var marker = xc != pcc ? " <--" : "";
            Console.WriteLine($"{type,-40} {xc,-8} {pcc,-8}{marker}");
        }

        Console.WriteLine();
        Console.WriteLine("=== Block-by-Block Comparison ===");
        Console.WriteLine();

        var maxBlocks = Math.Max(xbox.NumBlocks, pc.NumBlocks);

        Console.WriteLine($"{"Idx",-4} {"Xbox Type",-35} {"Xbox Size",-10} {"PC Type",-35} {"PC Size",-10}");
        Console.WriteLine(new string('-', 100));

        for (var i = 0; i < maxBlocks; i++)
        {
            var xboxType = i < xbox.NumBlocks ? xbox.BlockTypes[xbox.BlockTypeIndices[i]] : "-";
            var pcType = i < pc.NumBlocks ? pc.BlockTypes[pc.BlockTypeIndices[i]] : "-";
            var xboxSize = i < xbox.NumBlocks ? xbox.BlockSizes[i].ToString() : "-";
            var pcSize = i < pc.NumBlocks ? pc.BlockSizes[i].ToString() : "-";
            var marker = xboxType != pcType ? " <--" : "";

            Console.WriteLine($"{i,-4} {xboxType,-35} {xboxSize,-10} {pcType,-35} {pcSize,-10}{marker}");
        }

        return 0;
    }
}