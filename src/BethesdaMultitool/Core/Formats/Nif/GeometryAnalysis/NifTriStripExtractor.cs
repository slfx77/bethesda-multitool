// NIF converter - Triangle strip extraction and conversion
// Extracts triangle data from NiTriStripsData blocks and converts
// triangle strips to explicit triangle lists.

using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.GeometryAnalysis;

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
    internal static ushort[]? ExtractTrianglesFromTriStripsData(byte[] data, BlockInfo block, bool isBigEndian,
        uint binaryVersion)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        // Skip NiGeometryData common fields to get to strip data
        pos = SkipGeometryDataFields(data, pos, end, isBigEndian, binaryVersion);
        if (pos < 0)
        {
            return null;
        }

        // NiTriStripsData-specific fields
        return ExtractStripsSection(data, pos, end, isBigEndian, binaryVersion);
    }

    /// <summary>
    ///     Reads the stable strip-topology metadata from a NiTriStripsData block without
    ///     flattening it to render triangles. This keeps the declared strip triangle count
    ///     separate from the post-degenerate filtered triangle list.
    /// </summary>
    internal static NifTriStripSectionInfo? ReadStripSectionInfo(byte[] data, BlockInfo block, bool isBigEndian,
        uint binaryVersion)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        pos = SkipGeometryDataFields(data, pos, end, isBigEndian, binaryVersion);
        if (pos < 0)
        {
            return null;
        }

        return ExtractStripsSectionInfo(data, pos, end, isBigEndian, binaryVersion);
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
    private static int SkipGeometryDataFields(byte[] data, int pos, int end, bool isBigEndian, uint binaryVersion)
    {
        // NiGeometryData.Group ID (int) only since NIF 10.1.0.114 — absent on Oblivion's 10.1.0.106
        // architecture (rfcastlearchfront / rfcastleruinswall3way), present on 10.2.0.0 / 20.0.0.x.
        if (NifVersions.HasGeometryGroupId(binaryVersion))
        {
            pos += 4; // GroupId
        }

        if (pos + 2 > end)
        {
            return -1;
        }

        var numVerts = BinaryUtils.ReadUInt16(data, pos, isBigEndian);
        pos += 2;

        // Keep Flags + Compress Flags exist only since NIF 10.1.0.0. Oblivion's oldest 10.0.1.0 / 10.0.1.2
        // meshes have the modern base below this but not these two bytes — skipping them anyway would
        // desync the strip section and report no triangles.
        if (NifVersions.HasGeometryKeepFlags(binaryVersion))
        {
            pos += 2; // KeepFlags, CompressFlags
        }

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

        // Pre-20.2.0.7 the low 6 bits are a UV-set COUNT (retail TES4 collision NiTriStripsData
        // authors 2 sets — arringouterwall01 block 3); 20.2.0.7's BSDataFlags collapse it to a
        // single has-UV bit. Reading the count as a bit skipped nothing on multi-set blocks and
        // desynced the strip section into "no triangles".
        var numUVSets = binaryVersion >= NifVersions.Gamebryo202007
            ? bsDataFlags & 1
            : bsDataFlags & 0x3F;
        pos += numVerts * 8 * numUVSets;

        pos += 2; // ConsistencyFlags
        if (binaryVersion >= 0x14000004)
        {
            pos += 4; // AdditionalData ref (since NIF 20.0.0.4)
        }

        return pos;
    }

    /// <summary>
    ///     Parse the strips section of a NiTriStripsData block: read strip lengths,
    ///     read strip index data, and convert to explicit triangles.
    /// </summary>
    private static ushort[]? ExtractStripsSection(byte[] data, int pos, int end, bool isBigEndian,
        uint binaryVersion)
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

        // "Has Points" bool exists only since 10.0.1.3; at or below 10.0.1.2 (Oblivion 10.0.1.0 / 10.0.1.2)
        // the strip points follow the strip lengths unconditionally.
        if (NifVersions.HasStripPointsFlag(binaryVersion) && (pos + 1 > end || data[pos++] == 0))
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

    private static NifTriStripSectionInfo? ExtractStripsSectionInfo(byte[] data, int pos, int end, bool isBigEndian,
        uint binaryVersion)
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

        // "Has Points" bool exists only since 10.0.1.3 (see ExtractStripsSection).
        if (NifVersions.HasStripPointsFlag(binaryVersion) && (pos + 1 > end || data[pos++] == 0))
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
