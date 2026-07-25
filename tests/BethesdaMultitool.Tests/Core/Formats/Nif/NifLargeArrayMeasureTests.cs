using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Formats.Nif.Schema;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif;

/// <summary>
///     Regression for the schema converter's measure-walk array bound. Oblivion (20.0.0.4) NIFs carry no
///     per-block size array, so block boundaries are recovered by field-walking each block. A flat array
///     used to be skipped entirely when its element count exceeded a magic 100 000 cap — but a large
///     shape's tangent <c>NiBinaryExtraData</c> (e.g. ICAUTower01: 144 256 bytes) legitimately exceeds
///     that, and skipping it under-measured the block, desyncing every block after it (Oblivion's
///     no-block-size legacy path) and dropping ~half the mesh's pieces. The bound is now "no more elements
///     than bytes remaining in the buffer", so any in-buffer array is measured while garbage counts are
///     still rejected. Mirrors <see cref="NifTriStripsDataMeasureTests" /> (which covers the jagged path).
/// </summary>
public sealed class NifLargeArrayMeasureTests
{
    // Oblivion: version 20.0.0.4, user version 11, BS version 11.
    private const uint OblivionVersion = 0x14000004;

    [Theory]
    [InlineData(50_000)] // under the old cap — always measured
    [InlineData(144_256)] // the ICAUTower01 tangent-blob size — the regressing case (was skipped)
    [InlineData(500_000)] // well above the old cap
    public void MeasureBlock_NiBinaryExtraData_MeasuresLargeDataArray(int byteSize)
    {
        var block = BuildNiBinaryExtraData(byteSize);

        var converter = new NifSchemaConverter(NifSchema.LoadEmbedded(), OblivionVersion, 11, 11, true);
        var (size, _) = converter.MeasureBlock(block, 0, block.Length, "NiBinaryExtraData");

        // A correct measure consumes the whole block (name + byte-size field + Data[byteSize]). Before the
        // fix the >100 000-byte case skipped Data entirely, measuring only the ~21-byte header.
        Assert.Equal(block.Length, size);
    }

    /// <summary>
    ///     Builds one little-endian Oblivion <c>NiBinaryExtraData</c> block: <c>NiExtraData.Name</c> as an
    ///     inline length-prefixed string (versions &lt; 20.1.0.1), then the <c>Byte Size</c> field and its
    ///     <c>Data</c> byte array. The buffer length equals the block's exact byte size.
    /// </summary>
    private static byte[] BuildNiBinaryExtraData(int byteSize)
    {
        var b = new List<byte>();

        void U32(uint v)
        {
            b.AddRange(BitConverter.GetBytes(v));
        }

        var name = "Tangent space"u8.ToArray();
        U32((uint)name.Length);
        b.AddRange(name);
        U32((uint)byteSize);
        b.AddRange(new byte[byteSize]);

        return b.ToArray();
    }
}