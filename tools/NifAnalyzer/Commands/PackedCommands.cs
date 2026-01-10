using NifAnalyzer.Models;
using NifAnalyzer.Parsers;
using static NifAnalyzer.Utils.BinaryHelpers;

namespace NifAnalyzer.Commands;

/// <summary>
/// Commands for packed data and skin partition analysis: packed, skinpart, normalcompare.
/// </summary>
internal static class PackedCommands
{
    /// <summary>
    /// Compare normals from Xbox 360 packed data against PC reference normals.
    /// </summary>
    public static int NormalCompare(string xboxPath, string pcPath, int packedBlockIndex, int pcGeomBlockIndex, int count)
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
            Console.Error.WriteLine($"Xbox block {packedBlockIndex} is {xboxTypeName}, not BSPackedAdditionalGeometryData");
            return 1;
        }

        // Parse Xbox packed data
        int xboxOffset = xboxNif.GetBlockOffset(packedBlockIndex);
        int xboxSize = (int)xboxNif.BlockSizes[packedBlockIndex];
        var packedResult = ParsePackedGeometry(xboxData, xboxOffset, xboxSize, xboxNif.IsBigEndian);

        if (packedResult.RawDataOffset < 0)
        {
            Console.Error.WriteLine("Failed to parse Xbox packed data");
            return 1;
        }

        // Parse PC geometry data
        int pcOffset = pcNif.GetBlockOffset(pcGeomBlockIndex);
        int pcSize = (int)pcNif.BlockSizes[pcGeomBlockIndex];
        var pcGeom = GeometryParser.Parse(pcData.AsSpan(pcOffset, pcSize), pcNif.IsBigEndian, pcNif.BsVersion, pcTypeName);

        Console.WriteLine($"Xbox: {packedResult.NumVertices} vertices, stride {packedResult.Stride}");
        Console.WriteLine($"PC:   {pcGeom.NumVertices} vertices, HasNormals={pcGeom.HasNormals}");
        Console.WriteLine();

        // Find normal stream in packed data
        var half4Streams = packedResult.Streams.Where(s => s.Type == 16 && s.UnitSize == 8)
            .OrderBy(s => s.BlockOffset).ToList();

        if (half4Streams.Count < 2)
        {
            Console.Error.WriteLine("Could not find normal stream in packed data");
            return 1;
        }

        var normalStream = half4Streams[1]; // Second half4 stream is normals
        Console.WriteLine($"Xbox normal stream: type={normalStream.Type}, offset={normalStream.BlockOffset}, unitSize={normalStream.UnitSize}");
        Console.WriteLine();

        // Extract Xbox normals
        var xboxNormals = new List<(float X, float Y, float Z)>();
        for (int v = 0; v < packedResult.NumVertices && v < count; v++)
        {
            var vertexBase = packedResult.RawDataOffset + v * packedResult.Stride;
            var nOff = vertexBase + (int)normalStream.BlockOffset;

            float nx = HalfToFloat(ReadUInt16(xboxData, nOff, true));
            float ny = HalfToFloat(ReadUInt16(xboxData, nOff + 2, true));
            float nz = HalfToFloat(ReadUInt16(xboxData, nOff + 4, true));
            xboxNormals.Add((nx, ny, nz));
        }

        // Extract PC normals
        var pcNormals = ExtractPcNormals(pcData.AsSpan(pcOffset, pcSize), pcNif.IsBigEndian, pcGeom, count);

        // Show raw bytes for first few normals
        Console.WriteLine("=== Raw Normal Bytes (first 5 vertices) ===");
        Console.WriteLine();
        Console.WriteLine($"{"Vtx",-4} {"Xbox Bytes",-26} {"Xbox (Nx, Ny, Nz)",-35} {"PC (Nx, Ny, Nz)",-35}");
        Console.WriteLine(new string('-', 110));

        int displayCount = Math.Min(5, Math.Min(xboxNormals.Count, pcNormals.Count));
        for (int v = 0; v < displayCount; v++)
        {
            var vertexBase = packedResult.RawDataOffset + v * packedResult.Stride;
            var nOff = vertexBase + (int)normalStream.BlockOffset;

            // Raw bytes
            string hexBytes = $"{xboxData[nOff]:X2} {xboxData[nOff + 1]:X2} {xboxData[nOff + 2]:X2} {xboxData[nOff + 3]:X2} " +
                             $"{xboxData[nOff + 4]:X2} {xboxData[nOff + 5]:X2}";

            var xn = xboxNormals[v];
            var pn = pcNormals[v];

            Console.WriteLine($"{v,-4} {hexBytes,-26} ({xn.X,8:F4}, {xn.Y,8:F4}, {xn.Z,8:F4})   ({pn.X,8:F4}, {pn.Y,8:F4}, {pn.Z,8:F4})");
        }

        Console.WriteLine();

        // Full comparison with statistics
        Console.WriteLine($"=== Normal Comparison (first {count}) ===");
        Console.WriteLine();
        Console.WriteLine($"{"Vtx",-5} {"Xbox (Nx, Ny, Nz)",-35} {"PC (Nx, Ny, Nz)",-35} {"Delta",-10} {"Match"}");
        Console.WriteLine(new string('-', 100));

        int matchCount = 0;
        float maxDelta = 0;
        int maxDeltaVert = 0;

        for (int i = 0; i < Math.Min(xboxNormals.Count, pcNormals.Count); i++)
        {
            var xn = xboxNormals[i];
            var pn = pcNormals[i];

            float delta = MathF.Sqrt((xn.X - pn.X) * (xn.X - pn.X) +
                                     (xn.Y - pn.Y) * (xn.Y - pn.Y) +
                                     (xn.Z - pn.Z) * (xn.Z - pn.Z));
            bool match = delta < 0.02f;
            if (match) matchCount++;
            if (delta > maxDelta) { maxDelta = delta; maxDeltaVert = i; }

            if (i < 20 || !match) // Show first 20 or any mismatches
            {
                Console.WriteLine($"{i,-5} ({xn.X,8:F4}, {xn.Y,8:F4}, {xn.Z,8:F4})   ({pn.X,8:F4}, {pn.Y,8:F4}, {pn.Z,8:F4})   {delta,8:F4}   {(match ? "✓" : "✗")}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"=== Summary ===");
        Console.WriteLine($"Compared: {Math.Min(xboxNormals.Count, pcNormals.Count)} normals");
        Console.WriteLine($"Matches (delta < 0.02): {matchCount} ({100.0 * matchCount / Math.Min(xboxNormals.Count, pcNormals.Count):F1}%)");
        Console.WriteLine($"Max delta: {maxDelta:F4} at vertex {maxDeltaVert}");

        // Check if normals look like they need remapping
        Console.WriteLine();
        Console.WriteLine("=== Checking for potential issues ===");

        // Check if Xbox normals are normalized
        float avgLen = 0;
        for (int i = 0; i < xboxNormals.Count; i++)
        {
            var n = xboxNormals[i];
            avgLen += MathF.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
        }
        avgLen /= xboxNormals.Count;
        Console.WriteLine($"Xbox normal avg length: {avgLen:F4} (should be ~1.0 for normalized)");

        // Check if values look like valid normals (-1 to 1 range)
        int outOfRange = 0;
        for (int i = 0; i < xboxNormals.Count; i++)
        {
            var n = xboxNormals[i];
            if (MathF.Abs(n.X) > 1.1f || MathF.Abs(n.Y) > 1.1f || MathF.Abs(n.Z) > 1.1f)
                outOfRange++;
        }
        Console.WriteLine($"Xbox normals out of [-1,1] range: {outOfRange}");

        return 0;
    }

    private static (int NumVertices, int Stride, int RawDataOffset, List<PackedGeomStreamInfo> Streams) ParsePackedGeometry(
        byte[] data, int offset, int size, bool bigEndian)
    {
        var pos = offset;
        var numVertices = ReadUInt16(data, pos, bigEndian);
        pos += 2;
        var numBlockInfos = ReadUInt32(data, pos, bigEndian);
        pos += 4;

        var streams = new List<PackedGeomStreamInfo>();
        for (int i = 0; i < numBlockInfos; i++)
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

        int rawDataOffset = -1;
        for (int b = 0; b < numDataBlocks; b++)
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

    private static List<(float X, float Y, float Z)> ExtractPcNormals(ReadOnlySpan<byte> blockData, bool bigEndian, GeometryInfo geom, int count)
    {
        var normals = new List<(float X, float Y, float Z)>();
        if (geom.HasNormals == 0) return normals;

        // Find normals offset: after vertices
        int normalsOffset = geom.FieldOffsets.GetValueOrDefault("Vertices") + (int)geom.NumVertices * 12;
        count = Math.Min(count, geom.NumVertices);

        for (int i = 0; i < count; i++)
        {
            int off = normalsOffset + i * 12;
            float x = ReadFloat(blockData.ToArray(), off, bigEndian);
            float y = ReadFloat(blockData.ToArray(), off + 4, bigEndian);
            float z = ReadFloat(blockData.ToArray(), off + 8, bigEndian);
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

        int offset = nif.GetBlockOffset(blockIndex);
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

        Console.WriteLine($"=== BSPackedAdditionalGeometryData ===");
        Console.WriteLine($"NumVertices: {numVertices}");
        Console.WriteLine($"NumBlockInfos (streams): {numBlockInfos}");
        Console.WriteLine();

        // Parse stream infos (25 bytes each)
        var streams = new PackedGeomStreamInfo[numBlockInfos];
        Console.WriteLine("=== Data Streams ===");
        Console.WriteLine($"{"#",-3} {"Type",-6} {"Unit",-5} {"Total",-8} {"Stride",-7} {"BlkIdx",-7} {"Offset",-7} {"Interpretation",-20}");
        Console.WriteLine(new string('-', 75));

        for (int i = 0; i < numBlockInfos; i++)
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

            Console.WriteLine($"{i,-3} {streams[i].Type,-6} {streams[i].UnitSize,-5} {streams[i].TotalSize,-8} {streams[i].Stride,-7} {streams[i].BlockIndex,-7} {streams[i].BlockOffset,-7} {streams[i].GetInterpretation(),-20}");
        }

        Console.WriteLine();

        // NumDataBlocks
        var numDataBlocks = ReadUInt32(data, pos, nif.IsBigEndian);
        pos += 4;
        Console.WriteLine($"=== Data Blocks ({numDataBlocks}) ===");

        int rawDataOffset = -1;
        int rawDataSize = 0;

        for (int b = 0; b < numDataBlocks; b++)
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
            for (int ib = 0; ib < numInnerBlocks; ib++)
            {
                var blkOff = ReadUInt32(data, pos, nif.IsBigEndian);
                Console.WriteLine($"  blockOffset[{ib}]: {blkOff}");
                pos += 4;
            }

            var numData = ReadUInt32(data, pos, nif.IsBigEndian);
            pos += 4;
            Console.WriteLine($"  numData: {numData}");

            // Data sizes
            for (int d = 0; d < numData; d++)
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

            // Find position stream (type 16 with lowest offset)
            var posStream = streams.Where(s => s.Type == 16 && s.UnitSize == 8)
                .OrderBy(s => s.BlockOffset).FirstOrDefault();
            var uvStream = streams.FirstOrDefault(s => s.Type == 14 && s.UnitSize == 4);
            var normalStream = streams.Where(s => s.Type == 16 && s.UnitSize == 8)
                .OrderBy(s => s.BlockOffset).Skip(1).FirstOrDefault();

            Console.WriteLine($"{"Idx",-5} {"X",-12} {"Y",-12} {"Z",-12} {"U",-10} {"V",-10} {"Nx",-10} {"Ny",-10} {"Nz",-10}");
            Console.WriteLine(new string('-', 105));

            for (int v = 0; v < Math.Min(numVertsToShow, numVertices); v++)
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

                Console.WriteLine($"{v,-5} {px,-12:F4} {py,-12:F4} {pz,-12:F4} {u,-10:F4} {uv,-10:F4} {nx,-10:F4} {ny,-10:F4} {nz,-10:F4}");
            }

            Console.WriteLine();

            // Show raw hex for first vertex
            Console.WriteLine($"=== Raw Hex for Vertex 0 ({stride} bytes at 0x{rawDataOffset:X4}) ===");
            HexDump(data, rawDataOffset, stride);
        }

        return 0;
    }

    /// <summary>
    /// Dump all half4 streams for first N vertices to identify where normals are stored.
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

        int offset = nif.GetBlockOffset(blockIndex);
        int size = (int)nif.BlockSizes[blockIndex];
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

        Console.WriteLine($"Found {half4Streams.Count} half4 streams at offsets: {string.Join(", ", half4Streams.Select(s => s.BlockOffset))}");
        Console.WriteLine();

        // Header
        Console.Write($"{"Vtx",-4}");
        for (int s = 0; s < half4Streams.Count; s++)
        {
            Console.Write($" | Stream{s} @{half4Streams[s].BlockOffset,-2} (x, y, z, w)".PadRight(40));
        }
        Console.WriteLine();
        Console.WriteLine(new string('-', 4 + half4Streams.Count * 41));

        count = Math.Min(count, packedResult.NumVertices);
        for (int v = 0; v < count; v++)
        {
            Console.Write($"{v,-4}");
            var vertexBase = packedResult.RawDataOffset + v * packedResult.Stride;

            for (int s = 0; s < half4Streams.Count; s++)
            {
                var streamOff = vertexBase + (int)half4Streams[s].BlockOffset;
                float x = HalfToFloat(ReadUInt16(data, streamOff, true));
                float y = HalfToFloat(ReadUInt16(data, streamOff + 2, true));
                float z = HalfToFloat(ReadUInt16(data, streamOff + 4, true));
                float w = HalfToFloat(ReadUInt16(data, streamOff + 6, true));
                float len = MathF.Sqrt(x * x + y * y + z * z);
                Console.Write($" | ({x,7:F3},{y,7:F3},{z,7:F3},{w,6:F2}) len={len:F2}");
            }
            Console.WriteLine();
        }

        // Check which stream has values that look like normals (length ~1, values in -1..1)
        Console.WriteLine();
        Console.WriteLine("=== Stream Analysis ===");
        for (int s = 0; s < half4Streams.Count; s++)
        {
            float avgLen = 0;
            int inRange = 0;
            for (int v = 0; v < Math.Min(100, packedResult.NumVertices); v++)
            {
                var vertexBase = packedResult.RawDataOffset + v * packedResult.Stride;
                var streamOff = vertexBase + (int)half4Streams[s].BlockOffset;
                float x = HalfToFloat(ReadUInt16(data, streamOff, true));
                float y = HalfToFloat(ReadUInt16(data, streamOff + 2, true));
                float z = HalfToFloat(ReadUInt16(data, streamOff + 4, true));
                float len = MathF.Sqrt(x * x + y * y + z * z);
                avgLen += len;
                if (MathF.Abs(x) <= 1.1f && MathF.Abs(y) <= 1.1f && MathF.Abs(z) <= 1.1f)
                    inRange++;
            }
            avgLen /= Math.Min(100, packedResult.NumVertices);
            Console.WriteLine($"Stream {s} @offset {half4Streams[s].BlockOffset}: avgLen={avgLen:F3}, inRange(-1..1)={inRange}%");
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

        int offset = nif.GetBlockOffset(blockIndex);
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

        for (int p = 0; p < skinPart.NumPartitions; p++)
        {
            var part = skinPart.Partitions[p];
            Console.WriteLine($"--- Partition {p} ---");
            Console.WriteLine($"  NumVertices: {part.NumVertices}");
            Console.WriteLine($"  NumTriangles: {part.NumTriangles}");
            Console.WriteLine($"  NumBones: {part.NumBones}");
            Console.WriteLine($"  NumStrips: {part.NumStrips}");
            Console.WriteLine($"  NumWeightsPerVertex: {part.NumWeightsPerVertex}");
            Console.WriteLine($"  Bones: [{string.Join(", ", part.Bones.Take(Math.Min(10, part.Bones.Length)))}{(part.Bones.Length > 10 ? "..." : "")}]");
            Console.WriteLine($"  HasVertexMap: {part.HasVertexMap}");
            Console.WriteLine($"  VertexMap: [{string.Join(", ", part.VertexMap.Take(Math.Min(10, part.VertexMap.Length)))}{(part.VertexMap.Length > 10 ? "..." : "")}]");
            Console.WriteLine($"  HasVertexWeights: {part.HasVertexWeights}");
            Console.WriteLine($"  HasFaces: {part.HasFaces}");

            if (part.NumStrips > 0)
            {
                Console.WriteLine($"  NumStripsLengths: {part.StripLengths.Length}");
                Console.WriteLine($"  StripLengths: [{string.Join(", ", part.StripLengths)}]");
                Console.WriteLine($"  Total strip indices: {part.StripLengths.Sum(l => (int)l)}");

                if (part.Strips.Length > 0 && part.Strips[0].Length > 0)
                {
                    Console.WriteLine($"  Strip[0] first 20 indices: [{string.Join(", ", part.Strips[0].Take(Math.Min(20, part.Strips[0].Length)))}...]");
                }
            }

            Console.WriteLine($"  HasBoneIndices: {part.HasBoneIndices}");
            if (part.Triangles.Length > 0)
            {
                Console.WriteLine($"  Triangles[0-4]: {string.Join(", ", part.Triangles.Take(5).Select(t => $"({t.V1},{t.V2},{t.V3})"))}...");
            }

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
                int stripTris = 0;
                foreach (var strip in part.Strips)
                {
                    if (strip.Length >= 3)
                        stripTris += strip.Length - 2;
                }
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
}
