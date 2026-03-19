// NIF converter - Triangle strip extraction and conversion
// Extracts triangle data from NiTriStripsData blocks and converts
// triangle strips to explicit triangle lists.

using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Geometry;

internal readonly record struct NifTriStripSectionInfo(
    int DeclaredTriangleCount,
    int StripCount,
    ushort[] StripLengths,
    int CandidateTriangleWindowCount,
    int DegenerateTriangleCount,
    int ExtractedTriangleCount);

/// <summary>
///     Extracts and converts triangle strip data from NIF geometry blocks.
///     Handles NiTriStripsData parsing, strip-to-triangle conversion,
///     and geometry data field skipping for navigation to strip sections.
/// </summary>
internal static class NifTriStripExtractor
{
    /// <summary>
    ///     Extract triangles from a NiTriStripsData block by parsing past
    ///     common geometry fields and reading the strip data.
    /// </summary>
    internal static ushort[]? ExtractTrianglesFromTriStripsData(byte[] data, BlockInfo block, bool isBigEndian)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        // Skip NiGeometryData common fields to get to strip data
        pos = SkipGeometryDataFields(data, pos, end, isBigEndian);
        if (pos < 0)
        {
            return null;
        }

        // NiTriStripsData-specific fields
        return ExtractStripsSection(data, pos, end, isBigEndian);
    }

    /// <summary>
    ///     Reads the stable strip-topology metadata from a NiTriStripsData block without
    ///     flattening it to render triangles. This keeps the declared strip triangle count
    ///     separate from the post-degenerate filtered triangle list.
    /// </summary>
    internal static NifTriStripSectionInfo? ReadStripSectionInfo(byte[] data, BlockInfo block, bool isBigEndian)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        pos = SkipGeometryDataFields(data, pos, end, isBigEndian);
        if (pos < 0)
        {
            return null;
        }

        return ExtractStripsSectionInfo(data, pos, end, isBigEndian);
    }

    /// <summary>
    ///     Convert triangle strips to explicit triangles.
    /// </summary>
    internal static ushort[] ConvertStripsToTriangles(List<ushort[]> strips)
    {
        var triangles = new List<ushort>();

        foreach (var strip in strips)
        {
            if (strip.Length < 3)
            {
                continue;
            }

            for (var i = 0; i < strip.Length - 2; i++)
            {
                // Skip degenerate triangles
                if (strip[i] == strip[i + 1] || strip[i + 1] == strip[i + 2] || strip[i] == strip[i + 2])
                {
                    continue;
                }

                // Alternate winding order
                if ((i & 1) == 0)
                {
                    triangles.Add(strip[i]);
                    triangles.Add(strip[i + 1]);
                    triangles.Add(strip[i + 2]);
                }
                else
                {
                    triangles.Add(strip[i]);
                    triangles.Add(strip[i + 2]);
                    triangles.Add(strip[i + 1]);
                }
            }
        }

        return [.. triangles];
    }

    /// <summary>
    ///     Skip past NiGeometryData common fields (vertices, normals, colors, UVs, etc.)
    ///     to reach the NiTriStripsData-specific strip section.
    /// </summary>
    private static int SkipGeometryDataFields(byte[] data, int pos, int end, bool isBigEndian)
    {
        pos += 4; // GroupId

        if (pos + 2 > end)
        {
            return -1;
        }

        var numVerts = BinaryUtils.ReadUInt16(data, pos, isBigEndian);
        pos += 2;

        pos += 2; // KeepFlags, CompressFlags

        if (pos + 1 > end)
        {
            return -1;
        }

        var hasVertices = data[pos++];
        if (hasVertices != 0)
        {
            pos += numVerts * 12;
        }

        if (pos + 2 > end)
        {
            return -1;
        }

        var bsDataFlags = BinaryUtils.ReadUInt16(data, pos, isBigEndian);
        pos += 2;

        if (pos + 1 > end)
        {
            return -1;
        }

        var hasNormals = data[pos++];
        if (hasNormals != 0)
        {
            pos += numVerts * 12;
            if ((bsDataFlags & 4096) != 0)
            {
                pos += numVerts * 24;
            }
        }

        pos += 16; // BoundingSphere

        if (pos + 1 > end)
        {
            return -1;
        }

        var hasVertexColors = data[pos++];
        if (hasVertexColors != 0)
        {
            pos += numVerts * 16;
        }

        var numUVSets = bsDataFlags & 1;
        if (numUVSets != 0)
        {
            pos += numVerts * 8;
        }

        pos += 2; // ConsistencyFlags
        pos += 4; // AdditionalData ref

        return pos;
    }

    /// <summary>
    ///     Parse the strips section of a NiTriStripsData block: read strip lengths,
    ///     read strip index data, and convert to explicit triangles.
    /// </summary>
    private static ushort[]? ExtractStripsSection(byte[] data, int pos, int end, bool isBigEndian)
    {
        if (pos + 2 > end)
        {
            return null;
        }

        pos += 2; // NumTriangles

        if (pos + 2 > end)
        {
            return null;
        }

        var numStrips = BinaryUtils.ReadUInt16(data, pos, isBigEndian);
        pos += 2;

        if (numStrips == 0)
        {
            return null;
        }

        // Read strip lengths
        var stripLengths = new ushort[numStrips];
        for (var i = 0; i < numStrips; i++)
        {
            if (pos + 2 > end)
            {
                return null;
            }

            stripLengths[i] = BinaryUtils.ReadUInt16(data, pos, isBigEndian);
            pos += 2;
        }

        if (pos + 1 > end)
        {
            return null;
        }

        var hasPoints = data[pos++];
        if (hasPoints == 0)
        {
            return null;
        }

        // Read all strip indices
        var allStrips = new List<ushort[]>();
        for (var i = 0; i < numStrips; i++)
        {
            var stripLen = stripLengths[i];
            if (pos + stripLen * 2 > end)
            {
                return null;
            }

            var strip = new ushort[stripLen];
            for (var j = 0; j < stripLen; j++)
            {
                strip[j] = BinaryUtils.ReadUInt16(data, pos, isBigEndian);
                pos += 2;
            }

            allStrips.Add(strip);
        }

        return ConvertStripsToTriangles(allStrips);
    }

    private static NifTriStripSectionInfo? ExtractStripsSectionInfo(byte[] data, int pos, int end, bool isBigEndian)
    {
        if (pos + 2 > end)
        {
            return null;
        }

        var declaredTriangleCount = BinaryUtils.ReadUInt16(data, pos, isBigEndian);
        pos += 2;

        if (pos + 2 > end)
        {
            return null;
        }

        var numStrips = BinaryUtils.ReadUInt16(data, pos, isBigEndian);
        pos += 2;

        if (numStrips == 0)
        {
            return new NifTriStripSectionInfo(
                declaredTriangleCount,
                0,
                [],
                0,
                0,
                0);
        }

        var stripLengths = new ushort[numStrips];
        for (var i = 0; i < numStrips; i++)
        {
            if (pos + 2 > end)
            {
                return null;
            }

            stripLengths[i] = BinaryUtils.ReadUInt16(data, pos, isBigEndian);
            pos += 2;
        }

        if (pos + 1 > end)
        {
            return null;
        }

        var hasPoints = data[pos++];
        if (hasPoints == 0)
        {
            return null;
        }

        var candidateTriangleWindowCount = 0;
        var degenerateTriangleCount = 0;

        for (var i = 0; i < numStrips; i++)
        {
            var stripLength = stripLengths[i];
            if (pos + stripLength * 2 > end)
            {
                return null;
            }

            if (stripLength >= 3)
            {
                candidateTriangleWindowCount += stripLength - 2;
            }

            ushort? a = null;
            ushort? b = null;
            for (var j = 0; j < stripLength; j++)
            {
                var c = BinaryUtils.ReadUInt16(data, pos, isBigEndian);
                pos += 2;

                if (a.HasValue && b.HasValue && IsDegenerateTriangle(a.Value, b.Value, c))
                {
                    degenerateTriangleCount++;
                }

                a = b;
                b = c;
            }
        }

        return new NifTriStripSectionInfo(
            declaredTriangleCount,
            numStrips,
            stripLengths,
            candidateTriangleWindowCount,
            degenerateTriangleCount,
            candidateTriangleWindowCount - degenerateTriangleCount);
    }

    private static bool IsDegenerateTriangle(ushort a, ushort b, ushort c)
    {
        return a == b || b == c || a == c;
    }
}
