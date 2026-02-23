using System.CommandLine;
using FalloutXbox360Utils.Core.Formats.Nif.Geometry;
using NifAnalyzer.Parsers;
using Spectre.Console;
using static FalloutXbox360Utils.Core.Formats.Nif.Conversion.NifEndianUtils;

namespace NifAnalyzer.Commands;

/// <summary>
///     Commands for analyzing BSPackedAdditionalGeometryData streams: analyzestreams, streamdump.
/// </summary>
internal static class PackedStreamAnalyzer
{
    /// <summary>
    ///     Comprehensive analysis of BSPackedAdditionalGeometryData streams with semantic identification.
    /// </summary>
    private static void AnalyzeStreams(string path, int blockIndex, int numVertices)
    {
        var data = File.ReadAllBytes(path);
        var nif = NifParser.Parse(data);

        if (blockIndex < 0 || blockIndex >= nif.NumBlocks)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] " +
                                   $"Block index {blockIndex} out of range (0-{nif.NumBlocks - 1})");
            return;
        }

        var offset = nif.GetBlockOffset(blockIndex);
        var typeName = nif.GetBlockTypeName(blockIndex);
        var size = (int)nif.BlockSizes[blockIndex];

        if (!typeName.Contains("PackedAdditionalGeometryData"))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] " +
                                   $"Block {blockIndex} is {typeName}, not BSPackedAdditionalGeometryData");
            return;
        }

        AnsiConsole.WriteLine("\u2554\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2557");
        AnsiConsole.WriteLine("\u2551           BSPackedAdditionalGeometryData Stream Analysis              \u2551");
        AnsiConsole.WriteLine("\u255a\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u255d");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine($"File: {Path.GetFileName(path)}");
        AnsiConsole.WriteLine($"Block: {blockIndex} ({typeName})");
        AnsiConsole.WriteLine($"Offset: 0x{offset:X4} ({offset} bytes)");
        AnsiConsole.WriteLine($"Size: {size} bytes");
        AnsiConsole.WriteLine($"Endianness: {(nif.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
        AnsiConsole.WriteLine();

        // Parse packed geometry
        var result = PackedCommands.ParsePackedGeometry(data, offset, size, nif.IsBigEndian);

        if (result.RawDataOffset < 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] " + "Failed to locate raw vertex data in packed block");
            return;
        }

        AnsiConsole.WriteLine("\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550");
        AnsiConsole.WriteLine("                           STREAM LAYOUT                                   ");
        AnsiConsole.WriteLine("\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine($"NumVertices: {result.NumVertices}");
        AnsiConsole.WriteLine($"Stride: {result.Stride} bytes per vertex");
        AnsiConsole.WriteLine($"Raw Data Offset: 0x{result.RawDataOffset:X4}");
        AnsiConsole.WriteLine($"NumStreams: {result.Streams.Count}");
        AnsiConsole.WriteLine();

        // Identify half4 streams for semantic assignment
        var half4Streams = result.Streams
            .Where(s => s.Type == 16 && s.UnitSize == 8)
            .OrderBy(s => s.BlockOffset)
            .ToList();

        // Display stream table with semantic identification
        AnsiConsole.WriteLine(
            $"{"#",-3} {"Type",-6} {"Unit",-5} {"Offset",-8} {"Semantic",-20} {"Interpretation",-25}");
        AnsiConsole.WriteLine(new string('\u2500', 80));

        for (var i = 0; i < result.Streams.Count; i++)
        {
            var stream = result.Streams.OrderBy(s => s.BlockOffset).ElementAt(i);
            var half4Index = stream.Type == 16 && stream.UnitSize == 8
                ? half4Streams.FindIndex(s => s.BlockOffset == stream.BlockOffset)
                : -1;

            var semantic = stream.GetSemanticName(half4Index);
            var interp = stream.GetInterpretation();

            Console.WriteLine(
                $"{i,-3} {stream.Type,-6} {stream.UnitSize,-5} {stream.BlockOffset,-8} {semantic,-20} {interp,-25}");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550");
        AnsiConsole.WriteLine("                         STRIDE LAYOUT DIAGRAM                             ");
        AnsiConsole.WriteLine("\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550");
        AnsiConsole.WriteLine();

        // Visual stride diagram
        Console.Write("  0         8        16       20       24       32       40");
        AnsiConsole.WriteLine();
        Console.Write("  \u2502         \u2502         \u2502    \u2502    \u2502         \u2502         \u2502");
        AnsiConsole.WriteLine();
        Console.Write("  ");
        for (var b = 0; b < result.Stride; b++)
        {
            var stream = result.Streams.OrderBy(s => s.BlockOffset)
                .FirstOrDefault(s => s.BlockOffset <= b && b < s.BlockOffset + s.UnitSize);

            if (stream != null)
            {
                var half4Idx = stream.Type == 16 && stream.UnitSize == 8
                    ? half4Streams.FindIndex(s => s.BlockOffset == stream.BlockOffset)
                    : -1;
                var sem = stream.GetSemantic(half4Idx);
                var c = sem switch
                {
                    StreamSemantic.Position => 'P',
                    StreamSemantic.Tangent => 'T',
                    StreamSemantic.Bitangent => 'B',
                    StreamSemantic.Normal => 'N',
                    StreamSemantic.UV => 'U',
                    StreamSemantic.VertexColor => 'C',
                    _ => '?'
                };
                Console.Write(c);
            }
            else
            {
                Console.Write('.');
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("  P=Position  T=Tangent  C=Color  U=UV  B=Bitangent  N=Normal");

        // Sample vertices
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550");
        Console.WriteLine(
            $"                      SAMPLE VERTEX DATA ({Math.Min(numVertices, result.NumVertices)} vertices)");
        AnsiConsole.WriteLine("\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550");
        AnsiConsole.WriteLine();

        // Find streams by offset - VERIFIED against PC reference:
        // - Offset 0:  Position (model-scale values)
        // - Offset 8:  Unknown/auxiliary data (NOT normals, avg length ~0.82)
        // - Offset 20: Normal (unit-length ~1.0) - VERIFIED matches PC
        // - Offset 32: Tangent (unit-length ~1.0)
        // - Offset 40: Bitangent (unit-length ~1.0)
        var sortedHalf4 = result.Streams.Where(s => s.Type == 16 && s.UnitSize == 8).OrderBy(s => s.BlockOffset)
            .ToList();
        var posStream = sortedHalf4.FirstOrDefault(s => s.BlockOffset == 0);
        var normalStream = sortedHalf4.FirstOrDefault(s => s.BlockOffset == 20); // unit-length normals
        var tangentStream = sortedHalf4.FirstOrDefault(s => s.BlockOffset == 32); // unit-length tangents
        var bitangentStream = sortedHalf4.FirstOrDefault(s => s.BlockOffset == 40); // unit-length bitangents
        var colorStream = result.Streams.FirstOrDefault(s => s.Type == 28 && s.UnitSize == 4);
        var uvStream = result.Streams.FirstOrDefault(s => s.Type == 14 && s.UnitSize == 4);

        var displayCount = Math.Min(numVertices, result.NumVertices);

        // Position table
        if (posStream != null)
        {
            AnsiConsole.WriteLine("\u250c\u2500\u2500\u2500 Position (half4 -> float3) \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2510");
            AnsiConsole.WriteLine($"\u2502 {"Vtx",-5} {"X",-14} {"Y",-14} {"Z",-14} {"W",-10} \u2502");
            AnsiConsole.WriteLine("\u251c\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2524");

            for (var v = 0; v < displayCount; v++)
            {
                var vertBase = result.RawDataOffset + v * result.Stride;
                var pOff = vertBase + (int)posStream.BlockOffset;
                var x = HalfToFloat(ReadUInt16(data, pOff, nif.IsBigEndian));
                var y = HalfToFloat(ReadUInt16(data, pOff + 2, nif.IsBigEndian));
                var z = HalfToFloat(ReadUInt16(data, pOff + 4, nif.IsBigEndian));
                var w = HalfToFloat(ReadUInt16(data, pOff + 6, nif.IsBigEndian));
                AnsiConsole.WriteLine($"\u2502 {v,-5} {x,-14:F6} {y,-14:F6} {z,-14:F6} {w,-10:F4} \u2502");
            }

            AnsiConsole.WriteLine("\u2514\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2518");
            AnsiConsole.WriteLine();
        }

        // Normal table
        if (normalStream != null)
        {
            AnsiConsole.WriteLine("\u250c\u2500\u2500\u2500 Normal (half4 -> float3, normalized) \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2510");
            AnsiConsole.WriteLine($"\u2502 {"Vtx",-5} {"Nx",-12} {"Ny",-12} {"Nz",-12} {"W",-8} {"Length",-10} \u2502");
            AnsiConsole.WriteLine("\u251c\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2524");

            float avgLen = 0;
            var validCount = 0;
            for (var v = 0; v < displayCount; v++)
            {
                var vertBase = result.RawDataOffset + v * result.Stride;
                var nOff = vertBase + (int)normalStream.BlockOffset;
                var nx = HalfToFloat(ReadUInt16(data, nOff, nif.IsBigEndian));
                var ny = HalfToFloat(ReadUInt16(data, nOff + 2, nif.IsBigEndian));
                var nz = HalfToFloat(ReadUInt16(data, nOff + 4, nif.IsBigEndian));
                var nw = HalfToFloat(ReadUInt16(data, nOff + 6, nif.IsBigEndian));
                var len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                avgLen += len;
                if (len > 0.9f && len < 1.1f) validCount++;
                AnsiConsole.WriteLine($"\u2502 {v,-5} {nx,-12:F6} {ny,-12:F6} {nz,-12:F6} {nw,-8:F4} {len,-10:F6} \u2502");
            }

            AnsiConsole.WriteLine("\u2514\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2518");
            avgLen /= displayCount;
            AnsiConsole.WriteLine($"  Average length: {avgLen:F4} (should be ~1.0 for normalized normals)");
            AnsiConsole.WriteLine($"  Valid normals (0.9-1.1 length): {validCount}/{displayCount}");
            AnsiConsole.WriteLine();
        }

        // UV table
        if (uvStream != null)
        {
            AnsiConsole.WriteLine("\u250c\u2500\u2500\u2500 UV (half2 -> float2) \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2510");
            AnsiConsole.WriteLine($"\u2502 {"Vtx",-5} {"U",-20} {"V",-20} \u2502");
            AnsiConsole.WriteLine("\u251c\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2524");

            var outOfRange = 0;
            for (var v = 0; v < displayCount; v++)
            {
                var vertBase = result.RawDataOffset + v * result.Stride;
                var uvOff = vertBase + (int)uvStream.BlockOffset;
                var u = HalfToFloat(ReadUInt16(data, uvOff, nif.IsBigEndian));
                var vCoord = HalfToFloat(ReadUInt16(data, uvOff + 2, nif.IsBigEndian));
                if (u < 0 || u > 1 || vCoord < 0 || vCoord > 1) outOfRange++;
                AnsiConsole.WriteLine($"\u2502 {v,-5} {u,-20:F6} {vCoord,-20:F6} \u2502");
            }

            AnsiConsole.WriteLine("\u2514\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2518");
            AnsiConsole.WriteLine($"  UVs outside 0-1 range: {outOfRange}/{displayCount}");
            AnsiConsole.WriteLine();
        }

        // Vertex color table
        if (colorStream != null)
        {
            AnsiConsole.WriteLine("\u250c\u2500\u2500\u2500 Vertex Color (ubyte4 -> RGBA) \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2510");
            AnsiConsole.WriteLine($"\u2502 {"Vtx",-5} {"R",-8} {"G",-8} {"B",-8} {"A",-8} {"Hex",-12} \u2502");
            AnsiConsole.WriteLine("\u251c\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2524");

            for (var v = 0; v < displayCount; v++)
            {
                var vertBase = result.RawDataOffset + v * result.Stride;
                var cOff = vertBase + (int)colorStream.BlockOffset;
                var r = data[cOff];
                var g = data[cOff + 1];
                var b = data[cOff + 2];
                var a = data[cOff + 3];
                AnsiConsole.WriteLine($"\u2502 {v,-5} {r,-8} {g,-8} {b,-8} {a,-8} #{r:X2}{g:X2}{b:X2}{a:X2,-4} \u2502");
            }

            AnsiConsole.WriteLine("\u2514\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2518");
            AnsiConsole.WriteLine();
        }

        // Raw hex dump of first vertex
        AnsiConsole.WriteLine("\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550");
        AnsiConsole.WriteLine($"                    RAW HEX: VERTEX 0 ({result.Stride} bytes)");
        AnsiConsole.WriteLine("\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550\u2550");
        AnsiConsole.WriteLine();
        HexDump(data, result.RawDataOffset, result.Stride);
    }

    /// <summary>
    ///     Dump all half4 streams for first N vertices to identify where normals are stored.
    /// </summary>
    private static void StreamDump(string path, int blockIndex, int count)
    {
        var data = File.ReadAllBytes(path);
        var nif = NifParser.Parse(data);

        if (blockIndex < 0 || blockIndex >= nif.NumBlocks)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] " + $"Block index {blockIndex} out of range");
            return;
        }

        var typeName = nif.GetBlockTypeName(blockIndex);
        if (!typeName.Contains("PackedAdditionalGeometryData"))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] " +
                                   $"Block {blockIndex} is {typeName}, not BSPackedAdditionalGeometryData");
            return;
        }

        var offset = nif.GetBlockOffset(blockIndex);
        var size = (int)nif.BlockSizes[blockIndex];
        var packedResult = PackedCommands.ParsePackedGeometry(data, offset, size, nif.IsBigEndian);

        if (packedResult.RawDataOffset < 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] " + "Failed to parse packed data");
            return;
        }

        AnsiConsole.WriteLine($"=== All Half4 Streams for First {count} Vertices ===");
        AnsiConsole.WriteLine($"Stride: {packedResult.Stride}, NumVertices: {packedResult.NumVertices}");
        AnsiConsole.WriteLine();

        // Find all half4 streams
        var half4Streams = packedResult.Streams.Where(s => s.Type == 16 && s.UnitSize == 8)
            .OrderBy(s => s.BlockOffset).ToList();

        Console.WriteLine(
            $"Found {half4Streams.Count} half4 streams at offsets: {string.Join(", ", half4Streams.Select(s => s.BlockOffset))}");
        AnsiConsole.WriteLine();

        // Header
        Console.Write($"{"Vtx",-4}");
        for (var s = 0; s < half4Streams.Count; s++)
            Console.Write($" | Stream{s} @{half4Streams[s].BlockOffset,-2} (x, y, z, w)".PadRight(40));
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine(new string('-', 4 + half4Streams.Count * 41));

        count = Math.Min(count, packedResult.NumVertices);
        for (var v = 0; v < count; v++)
        {
            Console.Write($"{v,-4}");
            var vertexBase = packedResult.RawDataOffset + v * packedResult.Stride;

            for (var s = 0; s < half4Streams.Count; s++)
            {
                var streamOff = vertexBase + (int)half4Streams[s].BlockOffset;
                var x = HalfToFloat(ReadUInt16(data, streamOff, true));
                var y = HalfToFloat(ReadUInt16(data, streamOff + 2, true));
                var z = HalfToFloat(ReadUInt16(data, streamOff + 4, true));
                var w = HalfToFloat(ReadUInt16(data, streamOff + 6, true));
                var len = MathF.Sqrt(x * x + y * y + z * z);
                Console.Write($" | ({x,7:F3},{y,7:F3},{z,7:F3},{w,6:F2}) len={len:F2}");
            }

            AnsiConsole.WriteLine();
        }

        // Check which stream has values that look like normals (length ~1, values in -1..1)
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("=== Stream Analysis ===");
        for (var s = 0; s < half4Streams.Count; s++)
        {
            float avgLen = 0;
            var inRange = 0;
            for (var v = 0; v < Math.Min(100, packedResult.NumVertices); v++)
            {
                var vertexBase = packedResult.RawDataOffset + v * packedResult.Stride;
                var streamOff = vertexBase + (int)half4Streams[s].BlockOffset;
                var x = HalfToFloat(ReadUInt16(data, streamOff, true));
                var y = HalfToFloat(ReadUInt16(data, streamOff + 2, true));
                var z = HalfToFloat(ReadUInt16(data, streamOff + 4, true));
                var len = MathF.Sqrt(x * x + y * y + z * z);
                avgLen += len;
                if (MathF.Abs(x) <= 1.1f && MathF.Abs(y) <= 1.1f && MathF.Abs(z) <= 1.1f)
                    inRange++;
            }

            avgLen /= Math.Min(100, packedResult.NumVertices);
            Console.WriteLine(
                $"Stream {s} @offset {half4Streams[s].BlockOffset}: avgLen={avgLen:F3}, inRange(-1..1)={inRange}%");
        }
    }

    public static Command CreateStreamDumpCommand()
    {
        var command = new Command("streamdump", "Dump raw stream data from packed geometry");
        var fileArg = new Argument<string>("file") { Description = "NIF file path" };
        var blockArg = new Argument<int>("block") { Description = "Block index" };
        var countOpt = new Option<int>("-c", "--count")
        { Description = "Number of vertices to dump", DefaultValueFactory = _ => 10 };
        command.Arguments.Add(fileArg);
        command.Arguments.Add(blockArg);
        command.Options.Add(countOpt);
        command.SetAction(parseResult => StreamDump(
            parseResult.GetValue(fileArg), parseResult.GetValue(blockArg), parseResult.GetValue(countOpt)));
        return command;
    }

    public static Command CreateAnalyzeStreamsCommand()
    {
        var command = new Command("analyzestreams", "Analyze packed geometry streams for semantic detection");
        var fileArg = new Argument<string>("file") { Description = "NIF file path" };
        var blockArg = new Argument<int>("block") { Description = "Block index" };
        var countOpt = new Option<int>("-c", "--count")
        { Description = "Number of vertices to analyze", DefaultValueFactory = _ => 100 };
        command.Arguments.Add(fileArg);
        command.Arguments.Add(blockArg);
        command.Options.Add(countOpt);
        command.SetAction(parseResult => AnalyzeStreams(
            parseResult.GetValue(fileArg), parseResult.GetValue(blockArg), parseResult.GetValue(countOpt)));
        return command;
    }
}
