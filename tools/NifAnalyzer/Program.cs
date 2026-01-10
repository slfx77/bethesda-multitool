using NifAnalyzer.Commands;

namespace NifAnalyzer;

/// <summary>
/// NIF Analyzer - Standalone tool for analyzing NIF files.
/// Useful for debugging Xbox 360 to PC NIF conversion.
/// </summary>
internal class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0)
            return PrintUsage();

        var command = args[0].ToLowerInvariant();

        try
        {
            return command switch
            {
                "info" when args.Length >= 2 => InfoCommands.Info(args[1]),
                "blocks" when args.Length >= 2 => InfoCommands.Blocks(args[1]),
                "block" when args.Length >= 3 => InfoCommands.Block(args[1], int.Parse(args[2])),
                "compare" when args.Length >= 3 => InfoCommands.Compare(args[1], args[2]),
                "geometry" when args.Length >= 3 => GeometryCommands.Geometry(args[1], int.Parse(args[2])),
                "geomcompare" when args.Length >= 4 => GeometryCommands.GeomCompare(args[1], args[2], int.Parse(args[3])),
                "vertices" when args.Length >= 3 => GeometryCommands.Vertices(args[1], int.Parse(args[2]), args.Length > 3 ? int.Parse(args[3]) : 10),
                "skinpart" when args.Length >= 3 => PackedCommands.SkinPart(args[1], int.Parse(args[2])),
                "packed" when args.Length >= 3 => PackedCommands.Packed(args[1], int.Parse(args[2]), args.Length > 3 ? int.Parse(args[3]) : 5),
                "streamdump" when args.Length >= 3 => PackedCommands.StreamDump(args[1], int.Parse(args[2]), args.Length > 3 ? int.Parse(args[3]) : 10),
                "normalcompare" when args.Length >= 5 => PackedCommands.NormalCompare(args[1], args[2], int.Parse(args[3]), int.Parse(args[4]), args.Length > 5 ? int.Parse(args[5]) : 50),
                "hex" when args.Length >= 4 => HexCommands.Hex(args[1], ParseOffset(args[2]), int.Parse(args[3])),
                "havok" when args.Length >= 3 => HavokCommands.Havok(args[1], int.Parse(args[2])),
                "havokcompare" when args.Length >= 5 => HavokCommands.HavokCompare(args[1], args[2], int.Parse(args[3]), int.Parse(args[4])),
                _ => PrintUsage()
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    static int PrintUsage()
    {
        Console.WriteLine("""
            NIF Analyzer - Xbox 360 NIF debugging tool

            Usage:
              NifAnalyzer info <file>                        Show NIF header info
              NifAnalyzer blocks <file>                      List all blocks with types and sizes
              NifAnalyzer block <file> <index>               Show detailed block info
              NifAnalyzer compare <file1> <file2>            Compare two NIF files (blocks/types)
              NifAnalyzer geometry <file> <block_index>      Parse NiTriShapeData/NiTriStripsData
              NifAnalyzer geomcompare <file1> <file2> <blk>  Compare geometry blocks side-by-side
              NifAnalyzer vertices <file> <block> [count]    Show vertex/normal/UV data (default 10)
              NifAnalyzer skinpart <file> <block_index>      Parse NiSkinPartition block
              NifAnalyzer packed <file> <block> [verts]      Parse BSPackedAdditionalGeometryData
              NifAnalyzer streamdump <file> <block> [count] Dump all half4 streams to find normals
              NifAnalyzer normalcompare <xbox> <pc> <xblk> <pcblk> [count]
                                                             Compare Xbox packed normals vs PC
              NifAnalyzer havok <file> <block_index>         Parse Havok physics blocks
              NifAnalyzer havokcompare <xbox> <pc> <xblk> <pcblk>
                                                             Compare Havok blocks side-by-side
              NifAnalyzer hex <file> <offset> <length>       Hex dump at offset

            Examples:
              NifAnalyzer info lefthand.nif
              NifAnalyzer blocks lefthand.nif
              NifAnalyzer compare xbox.nif converted.nif
              NifAnalyzer geomcompare xbox.nif pc.nif 5
              NifAnalyzer vertices converted.nif 5 20
              NifAnalyzer skinpart lefthand.nif 9
              NifAnalyzer packed lefthand.nif 6 10
              NifAnalyzer normalcompare xbox.nif pc.nif 6 5 50
              NifAnalyzer hex lefthand.nif 0x055E 64
            """);
        return 1;
    }

    static long ParseOffset(string s)
    {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return Convert.ToInt64(s[2..], 16);
        return long.Parse(s);
    }
}
