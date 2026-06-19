using FalloutXbox360Utils.Core.Formats.Nif.Conversion;
using FalloutXbox360Utils.Core.Formats.Nif.Schema;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif;

/// <summary>
///     Regression test for the schema converter's measure walk of <c>NiTriStripsData</c>. Oblivion
///     (20.0.0.4) NIFs have no per-block size array, so the parser recovers block boundaries by
///     field-walking each block. The jagged <c>Points</c> array (length = "Num Strips", width =
///     "Strip Lengths") needs the per-strip lengths cached during the array walk; that caching was
///     capped at 100 entries, so a mesh with &gt;100 triangle strips (common in architecture / fort
///     pieces) skipped its strip-point data — under-measuring the block and desyncing every block after
///     it, which dropped whole mesh pieces. This locks the &gt;100-strip case to a correct full measure.
/// </summary>
public sealed class NifTriStripsDataMeasureTests
{
    // Oblivion: version 20.0.0.4, user version 11, BS version 11.
    private const uint OblivionVersion = 0x14000004;

    [Theory]
    [InlineData(50)] // <= 100 strips: always measured correctly
    [InlineData(150)] // > 100 strips: the regressing case (Points were skipped before the cap was raised)
    [InlineData(2000)] // far above any heuristic cap
    public void MeasureBlock_NiTriStripsData_MeasuresPointsRegardlessOfStripCount(int numStrips)
    {
        const int stripLen = 4;
        var block = BuildNiTriStripsData(numStrips, stripLen);

        var converter = new NifSchemaConverter(
            NifSchema.LoadEmbedded(), OblivionVersion, 11, 11, true);
        var (size, _) = converter.MeasureBlock(block, 0, block.Length, "NiTriStripsData");

        // The block is built to be exactly one NiTriStripsData (header + strips + points), so a correct
        // measure consumes all of it. Before the fix, >100-strip blocks stopped short by the points bytes.
        Assert.Equal(block.Length, size);
    }

    [Fact]
    public void MeasureBlock_StripPointBytes_ScaleWithStripCount()
    {
        var converter = new NifSchemaConverter(
            NifSchema.LoadEmbedded(), OblivionVersion, 11, 11, true);

        var (size50, _) = converter.MeasureBlock(BuildNiTriStripsData(50, 4), 0, int.MaxValue, "NiTriStripsData");
        var (size150, _) = converter.MeasureBlock(BuildNiTriStripsData(150, 4), 0, int.MaxValue, "NiTriStripsData");

        // 100 extra strips × (2 bytes strip-length entry + 4 points × 2 bytes) = 1000 bytes. Before the
        // fix the >100 block skipped its points entirely, making this difference negative.
        Assert.Equal(1000, size150 - size50);
    }

    /// <summary>
    ///     Builds one little-endian Oblivion <c>NiTriStripsData</c> block: a minimal NiGeometryData header
    ///     (0 vertices, no normals/colors/UV) followed by <paramref name="numStrips" /> strips of
    ///     <paramref name="stripLen" /> points each. The buffer length equals the block's exact byte size.
    /// </summary>
    private static byte[] BuildNiTriStripsData(int numStrips, int stripLen)
    {
        var b = new List<byte>();

        void U8(byte v)
        {
            b.Add(v);
        }

        void U16(ushort v)
        {
            b.AddRange(BitConverter.GetBytes(v));
        }

        void U32(uint v)
        {
            b.AddRange(BitConverter.GetBytes(v));
        }

        // --- NiGeometryData ---
        U32(0); // Group ID (int, since 10.1.0.114) — always zero
        U16(0); // Num Vertices = 0 (so Vertices/Normals/Colors/UV arrays are all empty)
        U8(0); // Keep Flags
        U8(0); // Compress Flags
        U8(1); // Has Vertices (no vertices follow since Num Vertices = 0)
        U16(0); // Data Flags (0 UV sets, no tangents)
        U8(0); // Has Normals = 0
        b.AddRange(new byte[16]); // Bounding Sphere (NiBound: Center vec3 + Radius float)
        U8(0); // Has Vertex Colors = 0
        U16(0); // Consistency Flags
        U32(0); // Additional Data (Ref, since 20.0.0.4)
        // --- NiTriBasedGeomData ---
        U16(0); // Num Triangles
        // --- NiTriStripsData ---
        U16((ushort)numStrips);
        for (var i = 0; i < numStrips; i++)
        {
            U16((ushort)stripLen); // Strip Lengths
        }

        U8(1); // Has Points
        for (var i = 0; i < numStrips * stripLen; i++)
        {
            U16(0); // Points (jagged: Num Strips × Strip Lengths)
        }

        return b.ToArray();
    }
}