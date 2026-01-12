using NifAnalyzer.Models;
using NifAnalyzer.Parsers;
using static NifAnalyzer.Utils.BinaryHelpers;

namespace NifAnalyzer.Commands;

/// <summary>
///     Commands for packed data and skin partition analysis: packed, skinpart, normalcompare.
/// </summary>
internal static class PackedCommands
{
    /// <summary>
    ///     Compare normals from Xbox 360 packed data against PC reference normals.
    /// </summary>
    public static int NormalCompare(string xboxPath, string pcPath, int packedBlockIndex, int pcGeomBlockIndex,
        int count)
    {
        var xboxData = File.ReadAllBytes(xboxPath);
        var pcData = File.ReadAllBytes(pcPath);
        var xboxNif = NifParser.Parse(xboxData);
        var pcNif = NifParser.Parse(pcData);

        Console.WriteLine("=== Normal Comparison: Xbox 360 Packed vs PC Reference ===");
        Console.WriteLine();
        Console.WriteLine($"Xbox file: {Path.GetFileName(xboxPath)} (Block {packedBlockIndex})");
        Console.WriteLine($"PC file:   {Path.GetFileName(pcPath)} (Block {pcGeomBlockIndex})");
        Console.WriteLine();

        // Validate blocks
        if (packedBlockIndex >= xboxNif.NumBlocks)
        {
            Console.Error.WriteLine($"Xbox block {packedBlockIndex} out of range");
            return 1;
        }

        if (pcGeomBlockIndex >= pcNif.NumBlocks)
        {
            Console.Error.WriteLine($"PC block {pcGeomBlockIndex} out of range");
            return 1;
        }

        var xboxTypeName = xboxNif.GetBlockTypeName(packedBlockIndex);
        var pcTypeName = pcNif.GetBlockTypeName(pcGeomBlockIndex);

        if (!xboxTypeName.Contains("PackedAdditionalGeometryData"))
        {
            Console.Error.WriteLine(
                $"Xbox block {packedBlockIndex} is {xboxTypeName}, not BSPackedAdditionalGeometryData");
            return 1;
        }

        // Parse Xbox packed data
        var xboxOffset = xboxNif.GetBlockOffset(packedBlockIndex);
        var xboxSize = (int)xboxNif.BlockSizes[packedBlockIndex];
        var packedResult = ParsePackedGeometry(xboxData, xboxOffset, xboxSize, xboxNif.IsBigEndian);

        if (packedResult.RawDataOffset < 0)
        {
            Console.Error.WriteLine("Failed to parse Xbox packed data");
            return 1;
        }

        // Parse PC geometry data
        var pcOffset = pcNif.GetBlockOffset(pcGeomBlockIndex);
        var pcSize = (int)pcNif.BlockSizes[pcGeomBlockIndex];
        var pcGeom = GeometryParser.Parse(pcData.AsSpan(pcOffset, pcSize), pcNif.IsBigEndian, pcNif.BsVersion,
            pcTypeName);

        Console.WriteLine($"Xbox: {packedResult.NumVertices} vertices, stride {packedResult.Stride}");
        Console.WriteLine($"PC:   {pcGeom.NumVertices} vertices, HasNormals={pcGeom.HasNormals}");
        Console.WriteLine();

        // Find normal stream in packed data - VERIFIED: unit-length stream at offset 20 matches PC normals
        // NOTE: Stream headers may label offset 8 as "Normal" but that data has avg length ~0.82, not unit-length.
        // Actual normals are at offset 20 (unit-length ~1.0).
        var half4Streams = packedResult.Streams.Where(s => s.Type == 16 && s.UnitSize == 8)
            .OrderBy(s => s.BlockOffset).ToList();

        // Find the stream at offset 20 (actual normals)
        var normalStream = half4Streams.FirstOrDefault(s => s.BlockOffset == 20);
        if (normalStream == null)
        {
            Console.Error.WriteLine("Could not find normal stream at offset 20");
            return 1;
        }

        Console.WriteLine(
            $"Xbox normal stream: type={normalStream.Type}, offset={normalStream.BlockOffset}, unitSize={normalStream.UnitSize}");
        Console.WriteLine();

        // Extract Xbox normals
        var xboxNormals = new List<(float X, float Y, float Z)>();
        for (var v = 0; v < packedResult.NumVertices && v < count; v++)
        {
            var vertexBase = packedResult.RawDataOffset + v * packedResult.Stride;
            var nOff = vertexBase + (int)normalStream.BlockOffset;

            var nx = HalfToFloat(ReadUInt16(xboxData, nOff, true));
            var ny = HalfToFloat(ReadUInt16(xboxData, nOff + 2, true));
            var nz = HalfToFloat(ReadUInt16(xboxData, nOff + 4, true));
            xboxNormals.Add((nx, ny, nz));
        }

        // Extract PC normals
        var pcNormals = ExtractPcNormals(pcData.AsSpan(pcOffset, pcSize), pcNif.IsBigEndian, pcGeom, count);

        // Show raw bytes for first few normals
        Console.WriteLine("=== Raw Normal Bytes (first 5 vertices) ===");
        Console.WriteLine();
        Console.WriteLine($"{"Vtx",-4} {"Xbox Bytes",-26} {"Xbox (Nx, Ny, Nz)",-35} {"PC (Nx, Ny, Nz)",-35}");
        Console.WriteLine(new string('-', 110));

        var displayCount = Math.Min(5, Math.Min(xboxNormals.Count, pcNormals.Count));
        for (var v = 0; v < displayCount; v++)
        {
            var vertexBase = packedResult.RawDataOffset + v * packedResult.Stride;
            var nOff = vertexBase + (int)normalStream.BlockOffset;

            // Raw bytes
            var hexBytes =
                $"{xboxData[nOff]:X2} {xboxData[nOff + 1]:X2} {xboxData[nOff + 2]:X2} {xboxData[nOff + 3]:X2} " +
                $"{xboxData[nOff + 4]:X2} {xboxData[nOff + 5]:X2}";

            var xn = xboxNormals[v];
            var pn = pcNormals[v];

            Console.WriteLine(
                $"{v,-4} {hexBytes,-26} ({xn.X,8:F4}, {xn.Y,8:F4}, {xn.Z,8:F4})   ({pn.X,8:F4}, {pn.Y,8:F4}, {pn.Z,8:F4})");
        }

        Console.WriteLine();

        // Full comparison with statistics
        Console.WriteLine($"=== Normal Comparison (first {count}) ===");
        Console.WriteLine();
        Console.WriteLine($"{"Vtx",-5} {"Xbox (Nx, Ny, Nz)",-35} {"PC (Nx, Ny, Nz)",-35} {"Delta",-10} {"Match"}");
        Console.WriteLine(new string('-', 100));

        var matchCount = 0;
        float maxDelta = 0;
        var maxDeltaVert = 0;

        for (var i = 0; i < Math.Min(xboxNormals.Count, pcNormals.Count); i++)
        {
            var xn = xboxNormals[i];
            var pn = pcNormals[i];

            var delta = MathF.Sqrt((xn.X - pn.X) * (xn.X - pn.X) +
                                   (xn.Y - pn.Y) * (xn.Y - pn.Y) +
                                   (xn.Z - pn.Z) * (xn.Z - pn.Z));
            var match = delta < 0.02f;
            if (match) matchCount++;
            if (delta > maxDelta)
            {
                maxDelta = delta;
                maxDeltaVert = i;
            }

            if (i < 20 || !match) // Show first 20 or any mismatches
                Console.WriteLine(
                    $"{i,-5} ({xn.X,8:F4}, {xn.Y,8:F4}, {xn.Z,8:F4})   ({pn.X,8:F4}, {pn.Y,8:F4}, {pn.Z,8:F4})   {delta,8:F4}   {(match ? "✓" : "✗")}");
        }

        Console.WriteLine();
        Console.WriteLine("=== Summary ===");
        Console.WriteLine($"Compared: {Math.Min(xboxNormals.Count, pcNormals.Count)} normals");
        Console.WriteLine(
            $"Matches (delta < 0.02): {matchCount} ({100.0 * matchCount / Math.Min(xboxNormals.Count, pcNormals.Count):F1}%)");
        Console.WriteLine($"Max delta: {maxDelta:F4} at vertex {maxDeltaVert}");

        // Check if normals look like they need remapping
        Console.WriteLine();
        Console.WriteLine("=== Checking for potential issues ===");

        // Check if Xbox normals are normalized
        float avgLen = 0;
        for (var i = 0; i < xboxNormals.Count; i++)
        {
            var n = xboxNormals[i];
            avgLen += MathF.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
        }

        avgLen /= xboxNormals.Count;
        Console.WriteLine($"Xbox normal avg length: {avgLen:F4} (should be ~1.0 for normalized)");

        // Check if values look like valid normals (-1 to 1 range)
        var outOfRange = 0;
        for (var i = 0; i < xboxNormals.Count; i++)
        {
            var n = xboxNormals[i];
            if (MathF.Abs(n.X) > 1.1f || MathF.Abs(n.Y) > 1.1f || MathF.Abs(n.Z) > 1.1f)
                outOfRange++;
        }

        Console.WriteLine($"Xbox normals out of [-1,1] range: {outOfRange}");

        return 0;
    }

    private static (int NumVertices, int Stride, int RawDataOffset, List<PackedGeomStreamInfo> Streams)
        ParsePackedGeometry(
            byte[] data, int offset, int size, bool bigEndian)
    {
        var pos = offset;
        var numVertices = ReadUInt16(data, pos, bigEndian);
        pos += 2;
        var numBlockInfos = ReadUInt32(data, pos, bigEndian);
        pos += 4;

        var streams = new List<PackedGeomStreamInfo>();
        for (var i = 0; i < numBlockInfos; i++)
        {
            streams.Add(new PackedGeomStreamInfo
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

    private static List<(float X, float Y, float Z)> ExtractPcNormals(ReadOnlySpan<byte> blockData, bool bigEndian,
        GeometryInfo geom, int count)
    {
        var normals = new List<(float X, float Y, float Z)>();
        if (geom.HasNormals == 0) return normals;

        // Use the actual parsed normals offset from GeometryParser
        if (!geom.FieldOffsets.TryGetValue("Normals", out var normalsOffset))
        {
            Console.Error.WriteLine("  Warning: Normals offset not found in PC geometry, trying fallback...");
            // Fallback: after vertices
            normalsOffset = geom.FieldOffsets.GetValueOrDefault("Vertices") + geom.NumVertices * 12;
        }

        count = Math.Min(count, geom.NumVertices);

        for (var i = 0; i < count; i++)
        {
            var off = normalsOffset + i * 12;
            var x = ReadFloat(blockData.ToArray(), off, bigEndian);
            var y = ReadFloat(blockData.ToArray(), off + 4, bigEndian);
            var z = ReadFloat(blockData.ToArray(), off + 8, bigEndian);
            normals.Add((x, y, z));
        }

        return normals;
    }

    public static int Packed(string path, int blockIndex, int numVertsToShow)
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

        if (!typeName.Contains("PackedAdditionalGeometryData"))
        {
            Console.Error.WriteLine($"Block {blockIndex} is {typeName}, not BSPackedAdditionalGeometryData");
            return 1;
        }

        Console.WriteLine($"Block {blockIndex}: {typeName}");
        Console.WriteLine($"Offset: 0x{offset:X4}");
        Console.WriteLine($"Size: {size} bytes");
        Console.WriteLine($"Endian: {(nif.IsBigEndian ? "Big (Xbox 360)" : "Little (PC)")}");
        Console.WriteLine();

        var pos = offset;

        // Parse BSPackedAdditionalGeometryData
        var numVertices = ReadUInt16(data, pos, nif.IsBigEndian);
        pos += 2;
        var numBlockInfos = ReadUInt32(data, pos, nif.IsBigEndian);
        pos += 4;

        Console.WriteLine("=== BSPackedAdditionalGeometryData ===");
        Console.WriteLine($"NumVertices: {numVertices}");
        Console.WriteLine($"NumBlockInfos (streams): {numBlockInfos}");
        Console.WriteLine();

        // Parse stream infos (25 bytes each)
        var streams = new PackedGeomStreamInfo[numBlockInfos];
        Console.WriteLine("=== Data Streams ===");
        Console.WriteLine(
            $"{"#",-3} {"Type",-6} {"Unit",-5} {"Total",-8} {"Stride",-7} {"BlkIdx",-7} {"Offset",-7} {"Semantic",-25}");
        Console.WriteLine(new string('-', 80));

        // First pass: collect all streams
        for (var i = 0; i < numBlockInfos; i++)
        {
            streams[i] = new PackedGeomStreamInfo
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

        Console.WriteLine();

        // NumDataBlocks
        var numDataBlocks = ReadUInt32(data, pos, nif.IsBigEndian);
        pos += 4;
        Console.WriteLine($"=== Data Blocks ({numDataBlocks}) ===");

        var rawDataOffset = -1;
        var rawDataSize = 0;

        for (var b = 0; b < numDataBlocks; b++)
        {
            var hasData = data[pos++];
            Console.WriteLine($"Block {b}: hasData = {hasData}");

            if (hasData == 0) continue;

            var blockSize = ReadUInt32(data, pos, nif.IsBigEndian);
            var numInnerBlocks = ReadUInt32(data, pos + 4, nif.IsBigEndian);
            pos += 8;

            Console.WriteLine($"  blockSize: {blockSize} (0x{blockSize:X4})");
            Console.WriteLine($"  numInnerBlocks: {numInnerBlocks}");

            // Block offsets
            for (var ib = 0; ib < numInnerBlocks; ib++)
            {
                var blkOff = ReadUInt32(data, pos, nif.IsBigEndian);
                Console.WriteLine($"  blockOffset[{ib}]: {blkOff}");
                pos += 4;
            }

            var numData = ReadUInt32(data, pos, nif.IsBigEndian);
            pos += 4;
            Console.WriteLine($"  numData: {numData}");

            // Data sizes
            for (var d = 0; d < numData; d++)
            {
                var dSize = ReadUInt32(data, pos, nif.IsBigEndian);
                Console.WriteLine($"  dataSize[{d}]: {dSize}");
                pos += 4;
            }

            rawDataOffset = pos;
            // blockSize is the total raw data size for this data block
            rawDataSize = (int)blockSize;
            Console.WriteLine($"  >>> RAW DATA at 0x{pos:X4}, size {rawDataSize} bytes");
            pos += rawDataSize;

            // shaderIndex and totalSize (BSPackedAGDDataBlock extension)
            var shaderIndex = ReadUInt32(data, pos, nif.IsBigEndian);
            var totalSize = ReadUInt32(data, pos + 4, nif.IsBigEndian);
            pos += 8;
            Console.WriteLine($"  shaderIndex: {shaderIndex}");
            Console.WriteLine($"  totalSize: {totalSize}");
        }

        Console.WriteLine();

        // Extract and display sample vertices
        if (rawDataOffset > 0 && numBlockInfos > 0)
        {
            var stride = (int)streams[0].Stride;
            Console.WriteLine($"=== Sample Vertex Data (first {numVertsToShow}) ===");
            Console.WriteLine($"Stride: {stride} bytes per vertex");
            Console.WriteLine();

            // Find streams by offset order - VERIFIED ORDER:
            // half4[0] = Position, half4[1] = Normal (matches PC), half4[2] = Tangent, half4[3] = Bitangent
            var half4StreamsForVerts = streams.Where(s => s.Type == 16 && s.UnitSize == 8)
                .OrderBy(s => s.BlockOffset).ToList();
            var posStream = half4StreamsForVerts.ElementAtOrDefault(0);
            var uvStream = streams.FirstOrDefault(s => s.Type == 14 && s.UnitSize == 4);
            var normalStream = half4StreamsForVerts.ElementAtOrDefault(1); // 2nd half4 stream is Normal (offset 8)

            Console.WriteLine(
                $"{"Idx",-5} {"X",-12} {"Y",-12} {"Z",-12} {"U",-10} {"V",-10} {"Nx",-10} {"Ny",-10} {"Nz",-10}");
            Console.WriteLine(new string('-', 105));

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

            Console.WriteLine();

            // Show raw hex for first vertex
            Console.WriteLine($"=== Raw Hex for Vertex 0 ({stride} bytes at 0x{rawDataOffset:X4}) ===");
            HexDump(data, rawDataOffset, stride);
        }

        return 0;
    }

    /// <summary>
    ///     Dump all half4 streams for first N vertices to identify where normals are stored.
    /// </summary>
    public static int StreamDump(string path, int blockIndex, int count)
    {
        var data = File.ReadAllBytes(path);
        var nif = NifParser.Parse(data);

        if (blockIndex < 0 || blockIndex >= nif.NumBlocks)
        {
            Console.Error.WriteLine($"Block index {blockIndex} out of range");
            return 1;
        }

        var typeName = nif.GetBlockTypeName(blockIndex);
        if (!typeName.Contains("PackedAdditionalGeometryData"))
        {
            Console.Error.WriteLine($"Block {blockIndex} is {typeName}, not BSPackedAdditionalGeometryData");
            return 1;
        }

        var offset = nif.GetBlockOffset(blockIndex);
        var size = (int)nif.BlockSizes[blockIndex];
        var packedResult = ParsePackedGeometry(data, offset, size, nif.IsBigEndian);

        if (packedResult.RawDataOffset < 0)
        {
            Console.Error.WriteLine("Failed to parse packed data");
            return 1;
        }

        Console.WriteLine($"=== All Half4 Streams for First {count} Vertices ===");
        Console.WriteLine($"Stride: {packedResult.Stride}, NumVertices: {packedResult.NumVertices}");
        Console.WriteLine();

        // Find all half4 streams
        var half4Streams = packedResult.Streams.Where(s => s.Type == 16 && s.UnitSize == 8)
            .OrderBy(s => s.BlockOffset).ToList();

        Console.WriteLine(
            $"Found {half4Streams.Count} half4 streams at offsets: {string.Join(", ", half4Streams.Select(s => s.BlockOffset))}");
        Console.WriteLine();

        // Header
        Console.Write($"{"Vtx",-4}");
        for (var s = 0; s < half4Streams.Count; s++)
            Console.Write($" | Stream{s} @{half4Streams[s].BlockOffset,-2} (x, y, z, w)".PadRight(40));
        Console.WriteLine();
        Console.WriteLine(new string('-', 4 + half4Streams.Count * 41));

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

            Console.WriteLine();
        }

        // Check which stream has values that look like normals (length ~1, values in -1..1)
        Console.WriteLine();
        Console.WriteLine("=== Stream Analysis ===");
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

        return 0;
    }

    public static int SkinPart(string path, int blockIndex)
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

        if (!typeName.Contains("SkinPartition"))
        {
            Console.Error.WriteLine($"Block {blockIndex} is {typeName}, not NiSkinPartition");
            return 1;
        }

        Console.WriteLine($"Block {blockIndex}: {typeName}");
        Console.WriteLine($"Offset: 0x{offset:X4}");
        Console.WriteLine($"Size: {size} bytes");
        Console.WriteLine($"Endian: {(nif.IsBigEndian ? "Big (Xbox 360)" : "Little (PC)")}");
        Console.WriteLine();

        var skinPart = SkinPartitionParser.Parse(data.AsSpan(offset, size), nif.IsBigEndian);

        Console.WriteLine("=== NiSkinPartition ===");
        Console.WriteLine();
        Console.WriteLine($"NumPartitions: {skinPart.NumPartitions}");
        Console.WriteLine();

        for (var p = 0; p < skinPart.NumPartitions; p++)
        {
            var part = skinPart.Partitions[p];
            Console.WriteLine($"--- Partition {p} ---");
            Console.WriteLine($"  NumVertices: {part.NumVertices}");
            Console.WriteLine($"  NumTriangles: {part.NumTriangles}");
            Console.WriteLine($"  NumBones: {part.NumBones}");
            Console.WriteLine($"  NumStrips: {part.NumStrips}");
            Console.WriteLine($"  NumWeightsPerVertex: {part.NumWeightsPerVertex}");
            Console.WriteLine(
                $"  Bones: [{string.Join(", ", part.Bones.Take(Math.Min(10, part.Bones.Length)))}{(part.Bones.Length > 10 ? "..." : "")}]");
            Console.WriteLine($"  HasVertexMap: {part.HasVertexMap}");
            Console.WriteLine(
                $"  VertexMap: [{string.Join(", ", part.VertexMap.Take(Math.Min(10, part.VertexMap.Length)))}{(part.VertexMap.Length > 10 ? "..." : "")}]");
            Console.WriteLine($"  HasVertexWeights: {part.HasVertexWeights}");
            Console.WriteLine($"  HasFaces: {part.HasFaces}");

            if (part.NumStrips > 0)
            {
                Console.WriteLine($"  NumStripsLengths: {part.StripLengths.Length}");
                Console.WriteLine($"  StripLengths: [{string.Join(", ", part.StripLengths)}]");
                Console.WriteLine($"  Total strip indices: {part.StripLengths.Sum(l => l)}");

                if (part.Strips.Length > 0 && part.Strips[0].Length > 0)
                    Console.WriteLine(
                        $"  Strip[0] first 20 indices: [{string.Join(", ", part.Strips[0].Take(Math.Min(20, part.Strips[0].Length)))}...]");
            }

            Console.WriteLine($"  HasBoneIndices: {part.HasBoneIndices}");
            if (part.Triangles.Length > 0)
                Console.WriteLine(
                    $"  Triangles[0-4]: {string.Join(", ", part.Triangles.Take(5).Select(t => $"({t.V1},{t.V2},{t.V3})"))}...");

            Console.WriteLine();
        }

        // Summary for skinned mesh reconstruction
        Console.WriteLine("=== Reconstruction Info ===");
        if (skinPart.NumPartitions > 0)
        {
            var part = skinPart.Partitions[0];
            if (part.NumStrips > 0 && part.Strips.Length > 0)
            {
                // Count triangles that can be generated from strips
                var stripTris = 0;
                foreach (var strip in part.Strips)
                    if (strip.Length >= 3)
                        stripTris += strip.Length - 2;
                Console.WriteLine($"Triangles reconstructable from strips: {stripTris}");
                Console.WriteLine($"Declared NumTriangles: {part.NumTriangles}");
            }
            else if (part.Triangles.Length > 0)
            {
                Console.WriteLine($"Direct triangles available: {part.Triangles.Length}");
            }
        }

        return 0;
    }

    /// <summary>
    ///     Comprehensive analysis of BSPackedAdditionalGeometryData streams with semantic identification.
    /// </summary>
    public static int AnalyzeStreams(string path, int blockIndex, int numVertices)
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

        if (!typeName.Contains("PackedAdditionalGeometryData"))
        {
            Console.Error.WriteLine($"Block {blockIndex} is {typeName}, not BSPackedAdditionalGeometryData");
            return 1;
        }

        Console.WriteLine("╔════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║           BSPackedAdditionalGeometryData Stream Analysis              ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine($"File: {Path.GetFileName(path)}");
        Console.WriteLine($"Block: {blockIndex} ({typeName})");
        Console.WriteLine($"Offset: 0x{offset:X4} ({offset} bytes)");
        Console.WriteLine($"Size: {size} bytes");
        Console.WriteLine($"Endianness: {(nif.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
        Console.WriteLine();

        // Parse packed geometry
        var result = ParsePackedGeometry(data, offset, size, nif.IsBigEndian);

        if (result.RawDataOffset < 0)
        {
            Console.Error.WriteLine("Failed to locate raw vertex data in packed block");
            return 1;
        }

        Console.WriteLine("═══════════════════════════════════════════════════════════════════════════");
        Console.WriteLine("                           STREAM LAYOUT                                   ");
        Console.WriteLine("═══════════════════════════════════════════════════════════════════════════");
        Console.WriteLine();
        Console.WriteLine($"NumVertices: {result.NumVertices}");
        Console.WriteLine($"Stride: {result.Stride} bytes per vertex");
        Console.WriteLine($"Raw Data Offset: 0x{result.RawDataOffset:X4}");
        Console.WriteLine($"NumStreams: {result.Streams.Count}");
        Console.WriteLine();

        // Identify half4 streams for semantic assignment
        var half4Streams = result.Streams
            .Where(s => s.Type == 16 && s.UnitSize == 8)
            .OrderBy(s => s.BlockOffset)
            .ToList();

        // Display stream table with semantic identification
        Console.WriteLine($"{"#",-3} {"Type",-6} {"Unit",-5} {"Offset",-8} {"Semantic",-20} {"Interpretation",-25}");
        Console.WriteLine(new string('─', 80));

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

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════════════════");
        Console.WriteLine("                         STRIDE LAYOUT DIAGRAM                             ");
        Console.WriteLine("═══════════════════════════════════════════════════════════════════════════");
        Console.WriteLine();

        // Visual stride diagram
        Console.Write("  0         8        16       20       24       32       40");
        Console.WriteLine();
        Console.Write("  │         │         │    │    │         │         │");
        Console.WriteLine();
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
                    PackedGeomStreamInfo.StreamSemantic.Position => 'P',
                    PackedGeomStreamInfo.StreamSemantic.Tangent => 'T',
                    PackedGeomStreamInfo.StreamSemantic.Bitangent => 'B',
                    PackedGeomStreamInfo.StreamSemantic.Normal => 'N',
                    PackedGeomStreamInfo.StreamSemantic.UV => 'U',
                    PackedGeomStreamInfo.StreamSemantic.VertexColor => 'C',
                    _ => '?'
                };
                Console.Write(c);
            }
            else
            {
                Console.Write('.');
            }
        }

        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine("  P=Position  T=Tangent  C=Color  U=UV  B=Bitangent  N=Normal");

        // Sample vertices
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════════════════");
        Console.WriteLine(
            $"                      SAMPLE VERTEX DATA ({Math.Min(numVertices, result.NumVertices)} vertices)");
        Console.WriteLine("═══════════════════════════════════════════════════════════════════════════");
        Console.WriteLine();

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
            Console.WriteLine("┌─── Position (half4 → float3) ───────────────────────────────────────────┐");
            Console.WriteLine($"│ {"Vtx",-5} {"X",-14} {"Y",-14} {"Z",-14} {"W",-10} │");
            Console.WriteLine("├─────────────────────────────────────────────────────────────────────────┤");

            for (var v = 0; v < displayCount; v++)
            {
                var vertBase = result.RawDataOffset + v * result.Stride;
                var pOff = vertBase + (int)posStream.BlockOffset;
                var x = HalfToFloat(ReadUInt16(data, pOff, nif.IsBigEndian));
                var y = HalfToFloat(ReadUInt16(data, pOff + 2, nif.IsBigEndian));
                var z = HalfToFloat(ReadUInt16(data, pOff + 4, nif.IsBigEndian));
                var w = HalfToFloat(ReadUInt16(data, pOff + 6, nif.IsBigEndian));
                Console.WriteLine($"│ {v,-5} {x,-14:F6} {y,-14:F6} {z,-14:F6} {w,-10:F4} │");
            }

            Console.WriteLine("└─────────────────────────────────────────────────────────────────────────┘");
            Console.WriteLine();
        }

        // Normal table
        if (normalStream != null)
        {
            Console.WriteLine("┌─── Normal (half4 → float3, normalized) ─────────────────────────────────┐");
            Console.WriteLine($"│ {"Vtx",-5} {"Nx",-12} {"Ny",-12} {"Nz",-12} {"W",-8} {"Length",-10} │");
            Console.WriteLine("├─────────────────────────────────────────────────────────────────────────┤");

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
                Console.WriteLine($"│ {v,-5} {nx,-12:F6} {ny,-12:F6} {nz,-12:F6} {nw,-8:F4} {len,-10:F6} │");
            }

            Console.WriteLine("└─────────────────────────────────────────────────────────────────────────┘");
            avgLen /= displayCount;
            Console.WriteLine($"  Average length: {avgLen:F4} (should be ~1.0 for normalized normals)");
            Console.WriteLine($"  Valid normals (0.9-1.1 length): {validCount}/{displayCount}");
            Console.WriteLine();
        }

        // UV table
        if (uvStream != null)
        {
            Console.WriteLine("┌─── UV (half2 → float2) ──────────────────────────────────────────────────┐");
            Console.WriteLine($"│ {"Vtx",-5} {"U",-20} {"V",-20} │");
            Console.WriteLine("├───────────────────────────────────────────────────────────────────────────┤");

            var outOfRange = 0;
            for (var v = 0; v < displayCount; v++)
            {
                var vertBase = result.RawDataOffset + v * result.Stride;
                var uvOff = vertBase + (int)uvStream.BlockOffset;
                var u = HalfToFloat(ReadUInt16(data, uvOff, nif.IsBigEndian));
                var vCoord = HalfToFloat(ReadUInt16(data, uvOff + 2, nif.IsBigEndian));
                if (u < 0 || u > 1 || vCoord < 0 || vCoord > 1) outOfRange++;
                Console.WriteLine($"│ {v,-5} {u,-20:F6} {vCoord,-20:F6} │");
            }

            Console.WriteLine("└───────────────────────────────────────────────────────────────────────────┘");
            Console.WriteLine($"  UVs outside 0-1 range: {outOfRange}/{displayCount}");
            Console.WriteLine();
        }

        // Vertex color table
        if (colorStream != null)
        {
            Console.WriteLine("┌─── Vertex Color (ubyte4 → RGBA) ─────────────────────────────────────────┐");
            Console.WriteLine($"│ {"Vtx",-5} {"R",-8} {"G",-8} {"B",-8} {"A",-8} {"Hex",-12} │");
            Console.WriteLine("├───────────────────────────────────────────────────────────────────────────┤");

            for (var v = 0; v < displayCount; v++)
            {
                var vertBase = result.RawDataOffset + v * result.Stride;
                var cOff = vertBase + (int)colorStream.BlockOffset;
                var r = data[cOff];
                var g = data[cOff + 1];
                var b = data[cOff + 2];
                var a = data[cOff + 3];
                Console.WriteLine($"│ {v,-5} {r,-8} {g,-8} {b,-8} {a,-8} #{r:X2}{g:X2}{b:X2}{a:X2,-4} │");
            }

            Console.WriteLine("└───────────────────────────────────────────────────────────────────────────┘");
            Console.WriteLine();
        }

        // Raw hex dump of first vertex
        Console.WriteLine("═══════════════════════════════════════════════════════════════════════════");
        Console.WriteLine($"                    RAW HEX: VERTEX 0 ({result.Stride} bytes)");
        Console.WriteLine("═══════════════════════════════════════════════════════════════════════════");
        Console.WriteLine();
        HexDump(data, result.RawDataOffset, result.Stride);

        return 0;
    }

    /// <summary>
    ///     Compare NiSkinPartition blocks from two NIF files (converted vs PC reference).
    /// </summary>
    public static int SkinPartCompare(string file1Path, string file2Path, int block1Index, int block2Index, int count)
    {
        var data1 = File.ReadAllBytes(file1Path);
        var data2 = File.ReadAllBytes(file2Path);
        var nif1 = NifParser.Parse(data1);
        var nif2 = NifParser.Parse(data2);

        Console.WriteLine("╔════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║              NiSkinPartition Comparison                                ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine($"File 1: {Path.GetFileName(file1Path)} (Block {block1Index})");
        Console.WriteLine($"File 2: {Path.GetFileName(file2Path)} (Block {block2Index})");
        Console.WriteLine();

        // Validate block indices
        if (block1Index >= nif1.NumBlocks)
        {
            Console.Error.WriteLine($"Block {block1Index} out of range for file 1");
            return 1;
        }

        if (block2Index >= nif2.NumBlocks)
        {
            Console.Error.WriteLine($"Block {block2Index} out of range for file 2");
            return 1;
        }

        var type1 = nif1.GetBlockTypeName(block1Index);
        var type2 = nif2.GetBlockTypeName(block2Index);

        if (!type1.Contains("SkinPartition"))
        {
            Console.Error.WriteLine($"Block {block1Index} in file 1 is {type1}, not NiSkinPartition");
            return 1;
        }

        if (!type2.Contains("SkinPartition"))
        {
            Console.Error.WriteLine($"Block {block2Index} in file 2 is {type2}, not NiSkinPartition");
            return 1;
        }

        // Parse both skin partitions
        var offset1 = nif1.GetBlockOffset(block1Index);
        var size1 = (int)nif1.BlockSizes[block1Index];
        var offset2 = nif2.GetBlockOffset(block2Index);
        var size2 = (int)nif2.BlockSizes[block2Index];

        Console.WriteLine($"File 1: Size={size1} bytes, Endian={(nif1.IsBigEndian ? "Big" : "Little")}");
        Console.WriteLine($"File 2: Size={size2} bytes, Endian={(nif2.IsBigEndian ? "Big" : "Little")}");
        Console.WriteLine();

        var skin1 = SkinPartitionParser.Parse(data1.AsSpan(offset1, size1), nif1.IsBigEndian);
        var skin2 = SkinPartitionParser.Parse(data2.AsSpan(offset2, size2), nif2.IsBigEndian);

        Console.WriteLine($"{"Property",-25} {"File 1",-20} {"File 2",-20} {"Match"}");
        Console.WriteLine(new string('─', 75));
        Console.WriteLine(
            $"{"NumPartitions",-25} {skin1.NumPartitions,-20} {skin2.NumPartitions,-20} {(skin1.NumPartitions == skin2.NumPartitions ? "✓" : "✗")}");
        Console.WriteLine();

        var numPartitions = Math.Min(skin1.NumPartitions, skin2.NumPartitions);

        for (var p = 0; p < numPartitions; p++)
        {
            var part1 = skin1.Partitions[p];
            var part2 = skin2.Partitions[p];

            Console.WriteLine($"═══ Partition {p} ═══════════════════════════════════════════════════════════");
            Console.WriteLine();
            Console.WriteLine($"{"Property",-25} {"File 1",-20} {"File 2",-20} {"Match"}");
            Console.WriteLine(new string('─', 75));
            Console.WriteLine(
                $"{"NumVertices",-25} {part1.NumVertices,-20} {part2.NumVertices,-20} {(part1.NumVertices == part2.NumVertices ? "✓" : "✗")}");
            Console.WriteLine(
                $"{"NumTriangles",-25} {part1.NumTriangles,-20} {part2.NumTriangles,-20} {(part1.NumTriangles == part2.NumTriangles ? "✓" : "✗")}");
            Console.WriteLine(
                $"{"NumBones",-25} {part1.NumBones,-20} {part2.NumBones,-20} {(part1.NumBones == part2.NumBones ? "✓" : "✗")}");
            Console.WriteLine(
                $"{"NumStrips",-25} {part1.NumStrips,-20} {part2.NumStrips,-20} {(part1.NumStrips == part2.NumStrips ? "✓" : "✗")}");
            Console.WriteLine(
                $"{"NumWeightsPerVertex",-25} {part1.NumWeightsPerVertex,-20} {part2.NumWeightsPerVertex,-20} {(part1.NumWeightsPerVertex == part2.NumWeightsPerVertex ? "✓" : "✗")}");
            Console.WriteLine(
                $"{"HasVertexMap",-25} {part1.HasVertexMap,-20} {part2.HasVertexMap,-20} {(part1.HasVertexMap == part2.HasVertexMap ? "✓" : "✗")}");
            Console.WriteLine(
                $"{"HasVertexWeights",-25} {part1.HasVertexWeights,-20} {part2.HasVertexWeights,-20} {(part1.HasVertexWeights == part2.HasVertexWeights ? "✓" : "✗")}");
            Console.WriteLine(
                $"{"HasFaces",-25} {part1.HasFaces,-20} {part2.HasFaces,-20} {(part1.HasFaces == part2.HasFaces ? "✓" : "✗")}");
            Console.WriteLine(
                $"{"HasBoneIndices",-25} {part1.HasBoneIndices,-20} {part2.HasBoneIndices,-20} {(part1.HasBoneIndices == part2.HasBoneIndices ? "✓" : "✗")}");
            Console.WriteLine();

            // Compare bones array
            var bonesMatch = part1.Bones.Length == part2.Bones.Length && part1.Bones.SequenceEqual(part2.Bones);
            Console.WriteLine($"Bones array: {(bonesMatch ? "✓ Match" : "✗ Mismatch")}");
            if (!bonesMatch && part1.Bones.Length > 0 && part2.Bones.Length > 0)
            {
                Console.WriteLine(
                    $"  File 1: [{string.Join(", ", part1.Bones.Take(10))}{(part1.Bones.Length > 10 ? "..." : "")}]");
                Console.WriteLine(
                    $"  File 2: [{string.Join(", ", part2.Bones.Take(10))}{(part2.Bones.Length > 10 ? "..." : "")}]");
            }

            // Compare vertex map
            var vmapMatch = part1.VertexMap.Length == part2.VertexMap.Length &&
                            part1.VertexMap.SequenceEqual(part2.VertexMap);
            Console.WriteLine(
                $"VertexMap: {(vmapMatch ? "✓ Match" : "✗ Mismatch")} ({part1.VertexMap.Length} vs {part2.VertexMap.Length} entries)");
            if (!vmapMatch && part1.VertexMap.Length > 0 && part2.VertexMap.Length > 0)
            {
                // Show first few mismatches
                var mismatches = 0;
                for (var i = 0; i < Math.Min(part1.VertexMap.Length, part2.VertexMap.Length) && mismatches < 5; i++)
                    if (part1.VertexMap[i] != part2.VertexMap[i])
                    {
                        Console.WriteLine($"  [VM {i}] File 1: {part1.VertexMap[i]}, File 2: {part2.VertexMap[i]}");
                        mismatches++;
                    }
            }

            // Compare weights if both have them
            if (part1.HasVertexWeights != 0 && part2.HasVertexWeights != 0)
            {
                Console.WriteLine();
                Console.WriteLine($"=== Vertex Weights (first {Math.Min(count, part1.NumVertices)} vertices) ===");

                var weightMatches = 0;
                var weightMismatches = 0;
                var displayedMismatches = 0;

                var vertsToCompare = Math.Min(count, Math.Min(part1.NumVertices, part2.NumVertices));

                for (var v = 0; v < vertsToCompare; v++)
                {
                    var allMatch = true;
                    for (var w = 0; w < Math.Min(part1.NumWeightsPerVertex, part2.NumWeightsPerVertex); w++)
                    {
                        var w1 = part1.VertexWeights[v][w];
                        var w2 = part2.VertexWeights[v][w];
                        if (Math.Abs(w1 - w2) > 0.001f)
                        {
                            allMatch = false;
                            break;
                        }
                    }

                    if (allMatch)
                    {
                        weightMatches++;
                    }
                    else
                    {
                        weightMismatches++;
                        if (displayedMismatches < 5)
                        {
                            Console.WriteLine(
                                $"  [V{v}] File 1: [{string.Join(", ", part1.VertexWeights[v].Select(f => f.ToString("F4")))}]");
                            Console.WriteLine(
                                $"       File 2: [{string.Join(", ", part2.VertexWeights[v].Select(f => f.ToString("F4")))}]");
                            displayedMismatches++;
                        }
                    }
                }

                Console.WriteLine(
                    $"  Weight matches: {weightMatches}/{vertsToCompare}, mismatches: {weightMismatches}");
            }

            // Compare bone indices if both have them
            if (part1.HasBoneIndices != 0 && part2.HasBoneIndices != 0)
            {
                Console.WriteLine();
                Console.WriteLine($"=== Bone Indices (first {Math.Min(count, part1.NumVertices)} vertices) ===");

                var idxMatches = 0;
                var idxMismatches = 0;
                var displayedMismatches = 0;

                var vertsToCompare = Math.Min(count, Math.Min(part1.NumVertices, part2.NumVertices));

                for (var v = 0; v < vertsToCompare; v++)
                {
                    var allMatch = part1.BoneIndices[v].SequenceEqual(part2.BoneIndices[v]);

                    if (allMatch)
                    {
                        idxMatches++;
                    }
                    else
                    {
                        idxMismatches++;
                        if (displayedMismatches < 5)
                        {
                            Console.WriteLine($"  [V{v}] File 1: [{string.Join(", ", part1.BoneIndices[v])}]");
                            Console.WriteLine($"       File 2: [{string.Join(", ", part2.BoneIndices[v])}]");
                            displayedMismatches++;
                        }
                    }
                }

                Console.WriteLine($"  Bone index matches: {idxMatches}/{vertsToCompare}, mismatches: {idxMismatches}");
            }

            // Compare triangles or strips
            if (part1.NumStrips > 0 || part2.NumStrips > 0)
            {
                Console.WriteLine();
                Console.WriteLine("=== Strips ===");
                var stripsMatch = part1.StripLengths.Length == part2.StripLengths.Length &&
                                  part1.StripLengths.SequenceEqual(part2.StripLengths);
                Console.WriteLine($"  StripLengths: {(stripsMatch ? "✓ Match" : "✗ Mismatch")}");
                if (!stripsMatch)
                {
                    Console.WriteLine($"    File 1: [{string.Join(", ", part1.StripLengths)}]");
                    Console.WriteLine($"    File 2: [{string.Join(", ", part2.StripLengths)}]");
                }
            }
            else if (part1.Triangles.Length > 0 || part2.Triangles.Length > 0)
            {
                Console.WriteLine();
                Console.WriteLine("=== Triangles ===");
                var triMatch = part1.Triangles.Length == part2.Triangles.Length;
                if (triMatch)
                {
                    var mismatches = 0;
                    for (var t = 0; t < part1.Triangles.Length; t++)
                        if (part1.Triangles[t] != part2.Triangles[t])
                            mismatches++;
                    Console.WriteLine($"  Triangles: {part1.Triangles.Length} total, {mismatches} mismatches");
                }
                else
                {
                    Console.WriteLine(
                        $"  Triangles count mismatch: {part1.Triangles.Length} vs {part2.Triangles.Length}");
                }
            }

            Console.WriteLine();
        }

        return 0;
    }
}