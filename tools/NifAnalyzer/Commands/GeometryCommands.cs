using NifAnalyzer.Models;
using NifAnalyzer.Parsers;
using static NifAnalyzer.Utils.BinaryHelpers;

namespace NifAnalyzer.Commands;

/// <summary>
/// Commands for geometry analysis: geometry, geomcompare, vertices.
/// </summary>
internal static class GeometryCommands
{
    public static int Geometry(string path, int blockIndex)
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

        Console.WriteLine($"Block {blockIndex}: {typeName}");
        Console.WriteLine($"Offset: 0x{offset:X4}");
        Console.WriteLine($"Size: {size} bytes");
        Console.WriteLine($"Endian: {(nif.IsBigEndian ? "Big" : "Little")}");
        Console.WriteLine();

        if (!typeName.Contains("TriShape") && !typeName.Contains("TriStrips"))
        {
            Console.WriteLine("Warning: Not a geometry data block");
        }

        // Parse geometry block
        var geom = GeometryParser.Parse(data.AsSpan(offset, size), nif.IsBigEndian, nif.BsVersion, typeName);

        Console.WriteLine("=== Geometry Data ===");
        Console.WriteLine();
        Console.WriteLine($"{"Field",-25} {"Offset",-10} {"Value",-20}");
        Console.WriteLine(new string('-', 60));
        Console.WriteLine($"{"GroupId",-25} 0x{geom.FieldOffsets.GetValueOrDefault("GroupId"):X4,-8} {geom.GroupId}");
        Console.WriteLine($"{"NumVertices",-25} 0x{geom.FieldOffsets.GetValueOrDefault("NumVertices"):X4,-8} {geom.NumVertices}");
        Console.WriteLine($"{"KeepFlags",-25} 0x{geom.FieldOffsets.GetValueOrDefault("KeepFlags"):X4,-8} {geom.KeepFlags}");
        Console.WriteLine($"{"CompressFlags",-25} 0x{geom.FieldOffsets.GetValueOrDefault("CompressFlags"):X4,-8} {geom.CompressFlags}");
        Console.WriteLine($"{"HasVertices",-25} 0x{geom.FieldOffsets.GetValueOrDefault("HasVertices"):X4,-8} {geom.HasVertices}");
        Console.WriteLine($"{"BsVectorFlags",-25} 0x{geom.FieldOffsets.GetValueOrDefault("BsVectorFlags"):X4,-8} 0x{geom.BsVectorFlags:X4}");
        Console.WriteLine($"{"HasNormals",-25} 0x{geom.FieldOffsets.GetValueOrDefault("HasNormals"):X4,-8} {geom.HasNormals}");
        Console.WriteLine($"{"Center",-25} 0x{geom.FieldOffsets.GetValueOrDefault("Center"):X4,-8} ({geom.TangentCenterX:F2}, {geom.TangentCenterY:F2}, {geom.TangentCenterZ:F2})");
        Console.WriteLine($"{"Radius",-25} {"",10} {geom.TangentRadius:F2}");
        Console.WriteLine($"{"HasVertexColors",-25} 0x{geom.FieldOffsets.GetValueOrDefault("HasVertexColors"):X4,-8} {geom.HasVertexColors}");
        Console.WriteLine($"{"NumUvSets",-25} {"",10} {geom.NumUvSets} (from BsVectorFlags)");
        Console.WriteLine($"{"ConsistencyFlags",-25} 0x{geom.FieldOffsets.GetValueOrDefault("ConsistencyFlags"):X4,-8} {geom.ConsistencyFlags}");
        Console.WriteLine($"{"AdditionalData",-25} 0x{geom.FieldOffsets.GetValueOrDefault("AdditionalData"):X4,-8} {geom.AdditionalData} (block ref)");

        Console.WriteLine();
        if (typeName.Contains("NiTriShapeData"))
        {
            Console.WriteLine("=== NiTriShapeData Specific ===");
            Console.WriteLine();
            Console.WriteLine($"{"NumTriangles",-25} 0x{geom.FieldOffsets.GetValueOrDefault("NumTriangles"):X4,-8} {geom.NumTriangles}");
            Console.WriteLine($"{"NumTrianglePoints",-25} 0x{geom.FieldOffsets.GetValueOrDefault("NumTrianglePoints"):X4,-8} {geom.NumTrianglePoints}");
            Console.WriteLine($"{"HasTriangles",-25} 0x{geom.FieldOffsets.GetValueOrDefault("HasTriangles"):X4,-8} {geom.HasTriangles}");
            Console.WriteLine($"{"NumMatchGroups",-25} 0x{geom.FieldOffsets.GetValueOrDefault("NumMatchGroups"):X4,-8} {geom.NumMatchGroups}");
            Console.WriteLine();
            Console.WriteLine($"Parsed size: {geom.ParsedSize} bytes");
            Console.WriteLine($"Actual block size: {size} bytes");
            Console.WriteLine($"Remaining/unaccounted bytes: {size - geom.ParsedSize}");

            // Calculate expected triangle data size
            int expectedTriDataSize = geom.NumTriangles * 6; // 3 ushorts per triangle
            Console.WriteLine();
            Console.WriteLine($"Triangle data analysis:");
            Console.WriteLine($"  NumTriangles × 6 bytes = {expectedTriDataSize} bytes");
            Console.WriteLine($"  Unaccounted bytes = {size - geom.ParsedSize} bytes");

            if (geom.HasTriangles == 0 && size - geom.ParsedSize > 0)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"*** WARNING: HasTriangles=0 but {size - geom.ParsedSize} bytes remain in block! ***");
                Console.WriteLine($"*** This suggests triangles ARE present despite HasTriangles=0 ***");
                Console.ResetColor();
            }
        }
        else if (typeName.Contains("NiTriStripsData"))
        {
            Console.WriteLine("=== NiTriStripsData Specific ===");
            Console.WriteLine();
            Console.WriteLine($"NumTriangles: {geom.NumTriangles}");
            Console.WriteLine($"NumStrips: {geom.NumStrips}");
            Console.WriteLine($"HasPoints: {geom.HasPoints}");
        }

        Console.WriteLine();
        Console.WriteLine("=== Raw Bytes at Triangle Fields ===");
        Console.WriteLine();

        // Show raw bytes from NumTriangles through end of block
        int triFieldOffset = geom.FieldOffsets.GetValueOrDefault("NumTriangles");
        int dumpLen = Math.Min(64, size - triFieldOffset);
        if (triFieldOffset > 0 && dumpLen > 0)
        {
            Console.WriteLine($"From NumTriangles field (relative offset 0x{triFieldOffset:X4}):");
            HexDump(data, offset + triFieldOffset, dumpLen);
        }

        return 0;
    }

    public static int GeomCompare(string path1, string path2, int blockIndex)
    {
        var data1 = File.ReadAllBytes(path1);
        var data2 = File.ReadAllBytes(path2);
        var nif1 = NifParser.Parse(data1);
        var nif2 = NifParser.Parse(data2);

        if (blockIndex >= nif1.NumBlocks || blockIndex >= nif2.NumBlocks)
        {
            Console.Error.WriteLine($"Block index {blockIndex} out of range");
            return 1;
        }

        int offset1 = nif1.GetBlockOffset(blockIndex);
        int offset2 = nif2.GetBlockOffset(blockIndex);

        var type1 = nif1.GetBlockTypeName(blockIndex);
        var type2 = nif2.GetBlockTypeName(blockIndex);
        var size1 = (int)nif1.BlockSizes[blockIndex];
        var size2 = (int)nif2.BlockSizes[blockIndex];

        Console.WriteLine("=== Geometry Block Comparison ===");
        Console.WriteLine();
        Console.WriteLine($"{"Property",-25} {"File 1",-30} {"File 2",-30}");
        Console.WriteLine(new string('-', 90));
        Console.WriteLine($"{"File",-25} {Path.GetFileName(path1),-30} {Path.GetFileName(path2),-30}");
        Console.WriteLine($"{"Endian",-25} {(nif1.IsBigEndian ? "Big (Xbox)" : "Little (PC)"),-30} {(nif2.IsBigEndian ? "Big (Xbox)" : "Little (PC)"),-30}");
        Console.WriteLine($"{"Block Type",-25} {type1,-30} {type2,-30}");
        Console.WriteLine($"{"Block Size",-25} {size1,-30} {size2,-30}");
        Console.WriteLine($"{"Offset",-25} 0x{offset1:X4,-28} 0x{offset2:X4,-28}");

        if (!type1.Contains("Tri") || !type2.Contains("Tri"))
        {
            Console.WriteLine("\nNot geometry blocks - cannot compare geometry data.");
            return 0;
        }

        var geom1 = GeometryParser.Parse(data1.AsSpan(offset1, size1), nif1.IsBigEndian, nif1.BsVersion, type1);
        var geom2 = GeometryParser.Parse(data2.AsSpan(offset2, size2), nif2.IsBigEndian, nif2.BsVersion, type2);

        Console.WriteLine();
        Console.WriteLine("=== Geometry Fields ===");
        Console.WriteLine();
        Console.WriteLine($"{"Field",-25} {"File 1",-30} {"File 2",-30} {"Match"}");
        Console.WriteLine(new string('-', 100));

        CompareField("NumVertices", geom1.NumVertices, geom2.NumVertices);
        CompareField("HasVertices", geom1.HasVertices, geom2.HasVertices);
        CompareField("BsVectorFlags", $"0x{geom1.BsVectorFlags:X4}", $"0x{geom2.BsVectorFlags:X4}");
        CompareField("HasNormals", geom1.HasNormals, geom2.HasNormals);
        CompareField("Center.X", geom1.TangentCenterX.ToString("F4"), geom2.TangentCenterX.ToString("F4"));
        CompareField("Center.Y", geom1.TangentCenterY.ToString("F4"), geom2.TangentCenterY.ToString("F4"));
        CompareField("Center.Z", geom1.TangentCenterZ.ToString("F4"), geom2.TangentCenterZ.ToString("F4"));
        CompareField("Radius", geom1.TangentRadius.ToString("F4"), geom2.TangentRadius.ToString("F4"));
        CompareField("HasVertexColors", geom1.HasVertexColors, geom2.HasVertexColors);
        CompareField("NumUvSets", geom1.NumUvSets, geom2.NumUvSets);
        CompareField("ConsistencyFlags", geom1.ConsistencyFlags, geom2.ConsistencyFlags);
        CompareField("AdditionalData", geom1.AdditionalData, geom2.AdditionalData);
        CompareField("NumTriangles", geom1.NumTriangles, geom2.NumTriangles);
        CompareField("NumTrianglePoints", geom1.NumTrianglePoints, geom2.NumTrianglePoints);
        CompareField("HasTriangles", geom1.HasTriangles, geom2.HasTriangles);

        // If vertices are present in both, compare first few
        if (geom1.HasVertices != 0 && geom2.HasVertices != 0)
        {
            Console.WriteLine();
            Console.WriteLine("=== Vertex Data Sample (first 5) ===");
            Console.WriteLine();

            var verts1 = ExtractVertices(data1.AsSpan(offset1, size1), nif1.IsBigEndian, geom1, Math.Min(5, (int)geom1.NumVertices));
            var verts2 = ExtractVertices(data2.AsSpan(offset2, size2), nif2.IsBigEndian, geom2, Math.Min(5, (int)geom2.NumVertices));

            Console.WriteLine($"{"Idx",-4} {"File 1 (X, Y, Z)",-40} {"File 2 (X, Y, Z)",-40}");
            Console.WriteLine(new string('-', 90));

            for (int i = 0; i < Math.Min(verts1.Count, verts2.Count); i++)
            {
                var v1 = verts1[i];
                var v2 = verts2[i];
                var match = Math.Abs(v1.X - v2.X) < 0.001f && Math.Abs(v1.Y - v2.Y) < 0.001f && Math.Abs(v1.Z - v2.Z) < 0.001f;
                Console.WriteLine($"{i,-4} ({v1.X,10:F4}, {v1.Y,10:F4}, {v1.Z,10:F4})   ({v2.X,10:F4}, {v2.Y,10:F4}, {v2.Z,10:F4}) {(match ? "✓" : "✗")}");
            }
        }

        return 0;
    }

    public static int Vertices(string path, int blockIndex, int count)
    {
        var data = File.ReadAllBytes(path);
        var nif = NifParser.Parse(data);

        if (blockIndex >= nif.NumBlocks)
        {
            Console.Error.WriteLine($"Block index {blockIndex} out of range");
            return 1;
        }

        int offset = nif.GetBlockOffset(blockIndex);
        var typeName = nif.GetBlockTypeName(blockIndex);
        var size = (int)nif.BlockSizes[blockIndex];

        Console.WriteLine($"Block {blockIndex}: {typeName}");
        Console.WriteLine($"Endian: {(nif.IsBigEndian ? "Big (Xbox 360)" : "Little (PC)")}");
        Console.WriteLine();

        if (!typeName.Contains("Tri"))
        {
            Console.WriteLine("Not a geometry block.");
            return 1;
        }

        var geom = GeometryParser.Parse(data.AsSpan(offset, size), nif.IsBigEndian, nif.BsVersion, typeName);
        count = Math.Min(count, geom.NumVertices);

        Console.WriteLine($"NumVertices: {geom.NumVertices}");
        Console.WriteLine($"HasVertices: {geom.HasVertices}");
        Console.WriteLine($"HasNormals: {geom.HasNormals}");
        Console.WriteLine($"BsVectorFlags: 0x{geom.BsVectorFlags:X4}");
        Console.WriteLine($"NumUvSets: {geom.NumUvSets}");
        Console.WriteLine();

        if (geom.HasVertices == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("HasVertices=0 - Vertices stored in BSPackedAdditionalGeometryData");
            Console.ResetColor();
            return 0;
        }

        // Extract and display vertex data
        var verts = ExtractVertices(data.AsSpan(offset, size), nif.IsBigEndian, geom, count);

        Console.WriteLine($"=== Vertices (first {count}) ===");
        Console.WriteLine();
        Console.WriteLine($"{"Idx",-5} {"X",12} {"Y",12} {"Z",12}");
        Console.WriteLine(new string('-', 50));
        for (int i = 0; i < verts.Count; i++)
        {
            Console.WriteLine($"{i,-5} {verts[i].X,12:F4} {verts[i].Y,12:F4} {verts[i].Z,12:F4}");
        }

        // Extract normals if present
        if (geom.HasNormals != 0)
        {
            var normals = ExtractNormals(data.AsSpan(offset, size), nif.IsBigEndian, geom, count);
            Console.WriteLine();
            Console.WriteLine($"=== Normals (first {count}) ===");
            Console.WriteLine();
            Console.WriteLine($"{"Idx",-5} {"X",12} {"Y",12} {"Z",12}");
            Console.WriteLine(new string('-', 50));
            for (int i = 0; i < normals.Count; i++)
            {
                Console.WriteLine($"{i,-5} {normals[i].X,12:F4} {normals[i].Y,12:F4} {normals[i].Z,12:F4}");
            }
        }

        // Extract UVs if present
        if (geom.NumUvSets > 0)
        {
            var uvs = ExtractUVs(data.AsSpan(offset, size), nif.IsBigEndian, geom, count);
            Console.WriteLine();
            Console.WriteLine($"=== UVs (first {count}) ===");
            Console.WriteLine();
            Console.WriteLine($"{"Idx",-5} {"U",12} {"V",12}");
            Console.WriteLine(new string('-', 35));
            for (int i = 0; i < uvs.Count; i++)
            {
                Console.WriteLine($"{i,-5} {uvs[i].U,12:F6} {uvs[i].V,12:F6}");
            }
        }

        return 0;
    }

    private static void CompareField<T>(string name, T val1, T val2)
    {
        var match = EqualityComparer<T>.Default.Equals(val1, val2);
        var marker = match ? "✓" : "✗";
        Console.WriteLine($"{name,-25} {val1,-30} {val2,-30} {marker}");
    }

    internal static List<(float X, float Y, float Z)> ExtractVertices(ReadOnlySpan<byte> blockData, bool bigEndian, GeometryInfo geom, int count)
    {
        var result = new List<(float, float, float)>();
        if (geom.HasVertices == 0 || !geom.FieldOffsets.TryGetValue("Vertices", out int vertOffset))
            return result;

        for (int i = 0; i < count; i++)
        {
            int pos = vertOffset + i * 12;
            float x = ReadFloat(blockData, pos, bigEndian);
            float y = ReadFloat(blockData, pos + 4, bigEndian);
            float z = ReadFloat(blockData, pos + 8, bigEndian);
            result.Add((x, y, z));
        }
        return result;
    }

    private static List<(float X, float Y, float Z)> ExtractNormals(ReadOnlySpan<byte> blockData, bool bigEndian, GeometryInfo geom, int count)
    {
        var result = new List<(float, float, float)>();
        if (geom.HasNormals == 0 || !geom.FieldOffsets.TryGetValue("Normals", out int normOffset))
            return result;

        for (int i = 0; i < count; i++)
        {
            int pos = normOffset + i * 12;
            float x = ReadFloat(blockData, pos, bigEndian);
            float y = ReadFloat(blockData, pos + 4, bigEndian);
            float z = ReadFloat(blockData, pos + 8, bigEndian);
            result.Add((x, y, z));
        }
        return result;
    }

    private static List<(float U, float V)> ExtractUVs(ReadOnlySpan<byte> blockData, bool bigEndian, GeometryInfo geom, int count)
    {
        var result = new List<(float, float)>();
        if (geom.NumUvSets == 0 || !geom.FieldOffsets.TryGetValue("UVSets", out int uvOffset))
            return result;

        for (int i = 0; i < count; i++)
        {
            int pos = uvOffset + i * 8;
            float u = ReadFloat(blockData, pos, bigEndian);
            float v = ReadFloat(blockData, pos + 4, bigEndian);
            result.Add((u, v));
        }
        return result;
    }

    /// <summary>
    /// Compare vertex colors between two geometry blocks (e.g., converted vs PC reference).
    /// </summary>
    public static int ColorCompare(string path1, string path2, int block1, int block2, int count = 20)
    {
        var data1 = File.ReadAllBytes(path1);
        var data2 = File.ReadAllBytes(path2);
        var nif1 = NifParser.Parse(data1);
        var nif2 = NifParser.Parse(data2);

        if (block1 >= nif1.NumBlocks || block2 >= nif2.NumBlocks)
        {
            Console.Error.WriteLine($"Block index out of range");
            return 1;
        }

        int offset1 = nif1.GetBlockOffset(block1);
        int offset2 = nif2.GetBlockOffset(block2);

        var type1 = nif1.GetBlockTypeName(block1);
        var type2 = nif2.GetBlockTypeName(block2);
        var size1 = (int)nif1.BlockSizes[block1];
        var size2 = (int)nif2.BlockSizes[block2];

        Console.WriteLine("=== Vertex Color Comparison ===");
        Console.WriteLine();
        Console.WriteLine($"{"Property",-20} {"File 1",-35} {"File 2",-35}");
        Console.WriteLine(new string('-', 95));
        Console.WriteLine($"{"File",-20} {Path.GetFileName(path1),-35} {Path.GetFileName(path2),-35}");
        Console.WriteLine($"{"Block",-20} {block1,-35} {block2,-35}");
        Console.WriteLine($"{"Type",-20} {type1,-35} {type2,-35}");
        Console.WriteLine($"{"Endian",-20} {(nif1.IsBigEndian ? "Big" : "Little"),-35} {(nif2.IsBigEndian ? "Big" : "Little"),-35}");

        var geom1 = GeometryParser.Parse(data1.AsSpan(offset1, size1), nif1.IsBigEndian, nif1.BsVersion, type1);
        var geom2 = GeometryParser.Parse(data2.AsSpan(offset2, size2), nif2.IsBigEndian, nif2.BsVersion, type2);

        Console.WriteLine($"{"NumVertices",-20} {geom1.NumVertices,-35} {geom2.NumVertices,-35}");
        Console.WriteLine($"{"HasVertexColors",-20} {geom1.HasVertexColors,-35} {geom2.HasVertexColors,-35}");

        if (geom1.HasVertexColors == 0 && geom2.HasVertexColors == 0)
        {
            Console.WriteLine("\nNeither block has vertex colors.");
            return 0;
        }

        count = Math.Min(count, Math.Min(geom1.NumVertices, geom2.NumVertices));

        var colors1 = ExtractVertexColors(data1.AsSpan(offset1, size1), nif1.IsBigEndian, geom1, count);
        var colors2 = ExtractVertexColors(data2.AsSpan(offset2, size2), nif2.IsBigEndian, geom2, count);

        Console.WriteLine();
        Console.WriteLine($"=== Vertex Colors (first {count}) ===");
        Console.WriteLine();
        Console.WriteLine($"{"Idx",-4} {"File 1 (R, G, B, A)",-40} {"File 2 (R, G, B, A)",-40} {"Match"}");
        Console.WriteLine(new string('-', 95));

        int matches = 0;
        for (int i = 0; i < count; i++)
        {
            var c1 = i < colors1.Count ? colors1[i] : (R: 0f, G: 0f, B: 0f, A: 0f);
            var c2 = i < colors2.Count ? colors2[i] : (R: 0f, G: 0f, B: 0f, A: 0f);

            // Check if close enough (allowing for float precision)
            var match = Math.Abs(c1.R - c2.R) < 0.01f &&
                        Math.Abs(c1.G - c2.G) < 0.01f &&
                        Math.Abs(c1.B - c2.B) < 0.01f &&
                        Math.Abs(c1.A - c2.A) < 0.01f;

            if (match) matches++;

            var marker = match ? "✓" : "✗";
            Console.WriteLine($"{i,-4} ({c1.R,6:F3}, {c1.G,6:F3}, {c1.B,6:F3}, {c1.A,6:F3})      ({c2.R,6:F3}, {c2.G,6:F3}, {c2.B,6:F3}, {c2.A,6:F3})      {marker}");
        }

        Console.WriteLine();
        Console.WriteLine($"Match rate: {matches}/{count} ({100.0 * matches / count:F1}%)");

        // Show raw bytes for first vertex color in each file
        if (geom1.HasVertexColors != 0 && geom1.FieldOffsets.TryGetValue("VertexColors", out int colorOffset1))
        {
            Console.WriteLine();
            Console.WriteLine($"=== Raw Color Bytes (File 1, offset 0x{colorOffset1:X}) ===");
            HexDump(data1, offset1 + colorOffset1, Math.Min(64, geom1.NumVertices * 16));
        }

        if (geom2.HasVertexColors != 0 && geom2.FieldOffsets.TryGetValue("VertexColors", out int colorOffset2))
        {
            Console.WriteLine();
            Console.WriteLine($"=== Raw Color Bytes (File 2, offset 0x{colorOffset2:X}) ===");
            HexDump(data2, offset2 + colorOffset2, Math.Min(64, geom2.NumVertices * 16));
        }

        return 0;
    }

    internal static List<(float R, float G, float B, float A)> ExtractVertexColors(ReadOnlySpan<byte> blockData, bool bigEndian, GeometryInfo geom, int count)
    {
        var result = new List<(float R, float G, float B, float A)>();
        if (geom.HasVertexColors == 0 || !geom.FieldOffsets.TryGetValue("VertexColors", out int colorOffset))
            return result;

        // NIF stores vertex colors as Color4 (4 floats: R, G, B, A)
        for (int i = 0; i < count; i++)
        {
            int pos = colorOffset + i * 16; // 4 floats * 4 bytes = 16 bytes per vertex
            float r = ReadFloat(blockData, pos, bigEndian);
            float g = ReadFloat(blockData, pos + 4, bigEndian);
            float b = ReadFloat(blockData, pos + 8, bigEndian);
            float a = ReadFloat(blockData, pos + 12, bigEndian);
            result.Add((r, g, b, a));
        }
        return result;
    }
}
