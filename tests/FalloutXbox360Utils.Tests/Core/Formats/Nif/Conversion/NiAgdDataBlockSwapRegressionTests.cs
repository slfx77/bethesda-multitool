using FalloutXbox360Utils.Core.Formats.Nif;
using FalloutXbox360Utils.Core.Formats.Nif.Conversion;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Conversion;

/// <summary>
///     Regression for the <c>NiAGDDataBlock.Data</c> byte-swap gap. The schema declares the
///     field as a 2-D <c>byte</c> array (<c>byte[Num Data][Block Size]</c>) so the converter
///     used to walk it byte-by-byte without any endian swap. The buffer actually holds
///     packed multi-byte channel data (typically 4-byte floats for vertex positions /
///     normals on LOD meshes), so PC reads garbage after a BE→LE conversion. A full
///     parity sweep across 14,854 Xbox/PC NIF pairs in <c>Sample/Meshes</c> showed 2,282
///     same-structure files diverging entirely inside <c>NiAdditionalGeometryData</c> blocks.
///     The fix swaps each 4-byte unit in the data buffer when <c>Block Size</c> is a
///     multiple of 4. These tests pin the result by converting a real Xbox LOD mesh and
///     comparing every byte against its PC vanilla counterpart.
/// </summary>
[Trait("Category", BucketBTestGuard.Category)]
public sealed class NiAgdDataBlockSwapRegressionTests
{
    private const string XboxStripLod =
        @"Sample\Meshes\meshes_360_final\meshes\landscape\lod\thestripworldnew\thestripworldnew.level4.x12.y-8.nif";

    private const string PcStripLod =
        @"Sample\Meshes\meshes_pc\meshes\landscape\lod\thestripworldnew\thestripworldnew.level4.x12.y-8.nif";

    [Fact]
    public void Convert_StripLodNif_ProducesByteIdenticalPcOutput()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var xboxPath = SampleFileFixture.FindSamplePath(XboxStripLod);
        var pcPath = SampleFileFixture.FindSamplePath(PcStripLod);
        Assert.SkipWhen(xboxPath is null || pcPath is null,
            "Sample LOD NIF pair not available — needs Sample/Meshes/{meshes_360_final,meshes_pc}");

        var xboxBytes = File.ReadAllBytes(xboxPath!);
        var pcBytes = File.ReadAllBytes(pcPath!);

        var result = NifConverter.Convert(xboxBytes);
        Assert.True(result.Success, $"NifConverter failed: {result.ErrorMessage}");
        Assert.NotNull(result.OutputData);

        // Strongest possible assertion: byte-identical to PC vanilla. This LOD NIF
        // contains a NiAdditionalGeometryData block, so any regression in the Data swap
        // will surface as a non-zero diff count.
        Assert.Equal(pcBytes.Length, result.OutputData!.Length);
        var firstDiff = FirstDiffOffset(result.OutputData, pcBytes);
        Assert.True(firstDiff < 0,
            $"Converted bytes diverge from PC vanilla at offset 0x{firstDiff:X}");
    }

    [Fact]
    public void Convert_StripLodNif_DataBufferIsNotByteEqualToXboxSource()
    {
        // Negative test: if the swap somehow regressed to a no-op, the converted bytes
        // for the NiAdditionalGeometryData block would be byte-identical to the Xbox
        // source. They must NOT be — the whole point is the buffer gets swapped.
        var xboxPath = SampleFileFixture.FindSamplePath(XboxStripLod);
        Assert.SkipWhen(xboxPath is null, "Sample LOD NIF not available");

        var xboxBytes = File.ReadAllBytes(xboxPath!);
        var result = NifConverter.Convert(xboxBytes);
        Assert.True(result.Success);

        var info = NifParser.Parse(result.OutputData!);
        Assert.NotNull(info);
        var agdBlock = info!.Blocks.First(b => b.TypeName == "NiAdditionalGeometryData");

        // Compare buffer-level: at least one byte inside the AGD block must differ from
        // the Xbox source bytes at the same offset.
        var anyByteSwapped = false;
        for (var i = agdBlock.DataOffset; i < agdBlock.DataOffset + agdBlock.Size && i < xboxBytes.Length; i++)
        {
            if (result.OutputData![i] != xboxBytes[i])
            {
                anyByteSwapped = true;
                break;
            }
        }

        Assert.True(anyByteSwapped,
            "NiAdditionalGeometryData block in converted output matches the Xbox source byte-for-byte; the data-buffer swap regressed to a no-op.");
    }

    private static int FirstDiffOffset(byte[] a, byte[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        for (var i = 0; i < len; i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }

        return -1;
    }
}
