using System.CommandLine;
using FalloutXbox360Utils.Core.Formats.Nif.Geometry;
using NifAnalyzer.Parsers;
using Spectre.Console;
using static FalloutXbox360Utils.Core.Formats.Nif.Conversion.NifEndianUtils;

namespace NifAnalyzer.Commands;

/// <summary>
///     Commands for BSPackedAdditionalGeometryData analysis: packed.
///     Also exposes <see cref="ParsePackedGeometry" /> for use by related commands.
/// </summary>
internal static class PackedCommands
{
    private static void Packed(string path, int blockIndex, int numVertsToShow)
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

        AnsiConsole.WriteLine($"Block {blockIndex}: {typeName}");
        AnsiConsole.WriteLine($"Offset: 0x{offset:X4}");
        AnsiConsole.WriteLine($"Size: {size} bytes");
        AnsiConsole.WriteLine($"Endian: {(nif.IsBigEndian ? "Big (Xbox 360)" : "Little (PC)")}");
        AnsiConsole.WriteLine();

        var pos = offset;

        // Parse BSPackedAdditionalGeometryData
        var numVertices = ReadUInt16(data, pos, nif.IsBigEndian);
        pos += 2;
        var numBlockInfos = ReadUInt32(data, pos, nif.IsBigEndian);
        pos += 4;

        AnsiConsole.WriteLine("=== BSPackedAdditionalGeometryData ===");
        AnsiConsole.WriteLine($"NumVertices: {numVertices}");
        AnsiConsole.WriteLine($"NumBlockInfos (streams): {numBlockInfos}");
        AnsiConsole.WriteLine();

        // Parse stream infos (25 bytes each)
        var streams = new DataStreamInfo[numBlockInfos];
        AnsiConsole.WriteLine("=== Data Streams ===");
        Console.WriteLine(
            $"{"#",-3} {"Type",-6} {"Unit",-5} {"Total",-8} {"Stride",-7} {"BlkIdx",-7} {"Offset",-7} {"Semantic",-25}");
        AnsiConsole.WriteLine(new string('-', 80));

        // First pass: collect all streams
        for (var i = 0; i < numBlockInfos; i++)
        {
            streams[i] = new DataStreamInfo
            {
                Type = ReadUInt32(data, pos, nif.IsBigEndian),
                UnitSize = ReadUInt32(data, pos + 4, nif.IsBigEndian),
                TotalSize = ReadUInt32(data, pos + 8, nif.IsBigEndian),
                Stride = ReadUInt32(data, pos + 12, nif.IsBigEndian),
                BlockIndex = ReadUInt32(data, pos + 16, nif.IsBigEndian),
                BlockOffset = ReadUInt32(data, pos + 20, nif.IsBigEndian),
                Flags = data[pos + 24]
            };
            pos += 25;
        }

        // Second pass: display with semantic info (requires knowing all half4 stream offsets)
        var half4Streams = streams.Where(s => s.Type == 16 && s.UnitSize == 8)
            .OrderBy(s => s.BlockOffset).ToList();

        foreach (var stream in streams.OrderBy(s => s.BlockOffset))
        {
            var half4Index = stream.Type == 16 && stream.UnitSize == 8
                ? half4Streams.FindIndex(s => s.BlockOffset == stream.BlockOffset)
                : -1;
            var semantic = stream.GetSemanticName(half4Index);

            var idx = Array.IndexOf(streams, stream);
            Console.WriteLine(
                $"{idx,-3} {stream.Type,-6} {stream.UnitSize,-5} {stream.TotalSize,-8} {stream.Stride,-7} {stream.BlockIndex,-7} {stream.BlockOffset,-7} {semantic,-25}");
        }

        AnsiConsole.WriteLine();

        // NumDataBlocks
        var numDataBlocks = ReadUInt32(data, pos, nif.IsBigEndian);
        pos += 4;
        AnsiConsole.WriteLine($"=== Data Blocks ({numDataBlocks}) ===");

        var rawDataOffset = -1;
        var rawDataSize = 0;

        for (var b = 0; b < numDataBlocks; b++)
        {
            var hasData = data[pos++];
            AnsiConsole.WriteLine($"Block {b}: hasData = {hasData}");

            if (hasData == 0) continue;

            var blockSize = ReadUInt32(data, pos, nif.IsBigEndian);
            var numInnerBlocks = ReadUInt32(data, pos + 4, nif.IsBigEndian);
            pos += 8;

            AnsiConsole.WriteLine($"  blockSize: {blockSize} (0x{blockSize:X4})");
            AnsiConsole.WriteLine($"  numInnerBlocks: {numInnerBlocks}");

            // Block offsets
            for (var ib = 0; ib < numInnerBlocks; ib++)
            {
                var blkOff = ReadUInt32(data, pos, nif.IsBigEndian);
                AnsiConsole.WriteLine($"  blockOffset[{ib}]: {blkOff}");
                pos += 4;
            }

            var numData = ReadUInt32(data, pos, nif.IsBigEndian);
            pos += 4;
            AnsiConsole.WriteLine($"  numData: {numData}");

            // Data sizes
            for (var d = 0; d < numData; d++)
            {
                var dSize = ReadUInt32(data, pos, nif.IsBigEndian);
                AnsiConsole.WriteLine($"  dataSize[{d}]: {dSize}");
                pos += 4;
            }

            rawDataOffset = pos;
            // blockSize is the total raw data size for this data block
            rawDataSize = (int)blockSize;
            AnsiConsole.WriteLine($"  >>> RAW DATA at 0x{pos:X4}, size {rawDataSize} bytes");
            pos += rawDataSize;

            // shaderIndex and totalSize (BSPackedAGDDataBlock extension)
            var shaderIndex = ReadUInt32(data, pos, nif.IsBigEndian);
            var totalSize = ReadUInt32(data, pos + 4, nif.IsBigEndian);
            pos += 8;
            AnsiConsole.WriteLine($"  shaderIndex: {shaderIndex}");
            AnsiConsole.WriteLine($"  totalSize: {totalSize}");
        }

        AnsiConsole.WriteLine();

        // Extract and display sample vertices
        if (rawDataOffset > 0 && numBlockInfos > 0)
        {
            var stride = (int)streams[0].Stride;
            AnsiConsole.WriteLine($"=== Sample Vertex Data (first {numVertsToShow}) ===");
            AnsiConsole.WriteLine($"Stride: {stride} bytes per vertex");
            AnsiConsole.WriteLine();

            // Find streams by offset order - VERIFIED ORDER:
            // half4[0] = Position, half4[1] = Normal (matches PC), half4[2] = Tangent, half4[3] = Bitangent
            var half4StreamsForVerts = streams.Where(s => s.Type == 16 && s.UnitSize == 8)
                .OrderBy(s => s.BlockOffset).ToList();
            var posStream = half4StreamsForVerts.ElementAtOrDefault(0);
            var uvStream = streams.FirstOrDefault(s => s.Type == 14 && s.UnitSize == 4);
            var normalStream = half4StreamsForVerts.ElementAtOrDefault(1); // 2nd half4 stream is Normal (offset 8)

            Console.WriteLine(
                $"{"Idx",-5} {"X",-12} {"Y",-12} {"Z",-12} {"U",-10} {"V",-10} {"Nx",-10} {"Ny",-10} {"Nz",-10}");
            AnsiConsole.WriteLine(new string('-', 105));

            for (var v = 0; v < Math.Min(numVertsToShow, numVertices); v++)
            {
                var vertexBase = rawDataOffset + v * stride;

                // Position (half4 at stream offset)
                float px = 0, py = 0, pz = 0;
                if (posStream != null)
                {
                    var pOff = vertexBase + (int)posStream.BlockOffset;
                    px = HalfToFloat(ReadUInt16(data, pOff, nif.IsBigEndian));
                    py = HalfToFloat(ReadUInt16(data, pOff + 2, nif.IsBigEndian));
                    pz = HalfToFloat(ReadUInt16(data, pOff + 4, nif.IsBigEndian));
                }

                // UV (half2)
                float u = 0, uv = 0;
                if (uvStream != null)
                {
                    var uvOff = vertexBase + (int)uvStream.BlockOffset;
                    u = HalfToFloat(ReadUInt16(data, uvOff, nif.IsBigEndian));
                    uv = HalfToFloat(ReadUInt16(data, uvOff + 2, nif.IsBigEndian));
                }

                // Normal (half4 at second type-16 stream)
                float nx = 0, ny = 0, nz = 0;
                if (normalStream != null)
                {
                    var nOff = vertexBase + (int)normalStream.BlockOffset;
                    nx = HalfToFloat(ReadUInt16(data, nOff, nif.IsBigEndian));
                    ny = HalfToFloat(ReadUInt16(data, nOff + 2, nif.IsBigEndian));
                    nz = HalfToFloat(ReadUInt16(data, nOff + 4, nif.IsBigEndian));
                }

                Console.WriteLine(
                    $"{v,-5} {px,-12:F4} {py,-12:F4} {pz,-12:F4} {u,-10:F4} {uv,-10:F4} {nx,-10:F4} {ny,-10:F4} {nz,-10:F4}");
            }

            AnsiConsole.WriteLine();

            // Show raw hex for first vertex
            AnsiConsole.WriteLine($"=== Raw Hex for Vertex 0 ({stride} bytes at 0x{rawDataOffset:X4}) ===");
            HexDump(data, rawDataOffset, stride);
        }
    }

    /// <summary>
    ///     Parse BSPackedAdditionalGeometryData structure to extract stream info and raw data offset.
    ///     Used by <see cref="PackedNormalComparer" /> and <see cref="PackedStreamAnalyzer" />.
    /// </summary>
    internal static (int NumVertices, int Stride, int RawDataOffset, List<DataStreamInfo> Streams)
        ParsePackedGeometry(
            byte[] data, int offset, int size, bool bigEndian)
    {
        var pos = offset;
        var numVertices = ReadUInt16(data, pos, bigEndian);
        pos += 2;
        var numBlockInfos = ReadUInt32(data, pos, bigEndian);
        pos += 4;

        var streams = new List<DataStreamInfo>();
        for (var i = 0; i < numBlockInfos; i++)
        {
            streams.Add(new DataStreamInfo
            {
                Type = ReadUInt32(data, pos, bigEndian),
                UnitSize = ReadUInt32(data, pos + 4, bigEndian),
                TotalSize = ReadUInt32(data, pos + 8, bigEndian),
                Stride = ReadUInt32(data, pos + 12, bigEndian),
                BlockIndex = ReadUInt32(data, pos + 16, bigEndian),
                BlockOffset = ReadUInt32(data, pos + 20, bigEndian),
                Flags = data[pos + 24]
            });
            pos += 25;
        }

        var stride = streams.Count > 0 ? (int)streams[0].Stride : 0;

        var numDataBlocks = ReadUInt32(data, pos, bigEndian);
        pos += 4;

        var rawDataOffset = -1;
        for (var b = 0; b < numDataBlocks; b++)
        {
            var hasData = data[pos++];
            if (hasData == 0) continue;

            var blockSize = ReadUInt32(data, pos, bigEndian);
            var numInnerBlocks = ReadUInt32(data, pos + 4, bigEndian);
            pos += 8;
            pos += (int)numInnerBlocks * 4;

            var numData = ReadUInt32(data, pos, bigEndian);
            pos += 4;
            pos += (int)numData * 4;

            rawDataOffset = pos;
            pos += (int)blockSize;
            pos += 8;
        }

        return (numVertices, stride, rawDataOffset, streams);
    }

    public static Command CreatePackedCommand()
    {
        var command = new Command("packed", "Parse BSPackedAdditionalGeometryData block");
        var fileArg = new Argument<string>("file") { Description = "NIF file path" };
        var blockArg = new Argument<int>("block") { Description = "Block index" };
        var countOpt = new Option<int>("-c", "--count")
        { Description = "Number of vertices to show", DefaultValueFactory = _ => 10 };
        command.Arguments.Add(fileArg);
        command.Arguments.Add(blockArg);
        command.Options.Add(countOpt);
        command.SetAction(parseResult => Packed(
            parseResult.GetValue(fileArg), parseResult.GetValue(blockArg), parseResult.GetValue(countOpt)));
        return command;
    }
}
