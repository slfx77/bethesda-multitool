using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Conversion;

/// <summary>
///     Regression for the July-prototype physics bug (props launched when touched, ragdolls
///     undraggable). Xbox 360 NIFs serialize the bhkWorldObject-level <c>Havok Filter</c> as a
///     single big-endian uint32; the schema's per-field walk (byte Layer, byte Flags, ushort
///     Group) instead left Layer/Flags at 0 and byte-swapped garbage into Group on every
///     collision object — ragdoll bones lost FOL_BIPED and their per-bone part numbers,
///     clutter lost FOL_CLUTTER. The fix whole-uint32-swaps HavokFilter at NiStream sites
///     (and passes the raw-LE copy inside bhkRigidBodyCInfo through verbatim). These tests
///     convert real July-prototype meshes and assert the engine-visible filter values.
///     bhkWorldObject layout: Shape (Ref, 4 bytes) then Havok Filter (4 bytes) at +0x04.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public sealed class HavokFilterEndianRegressionTests
{
    private const string JulyMeshesBsa =
        @"Sample\Full_Builds\Fallout New Vegas (July 21, 2010)\FalloutNV\Data\Fallout - Meshes.bsa";

    private const int HavokFilterOffsetInBlock = 4;

    private const byte FolStatic = 1;
    private const byte FolBiped = 8;
    private const byte FolClutter = 10;

    [Fact]
    public void Convert_JulyTinCan_RigidBodyKeepsClutterLayerAndZeroGroup()
    {
        var filters = ConvertAndReadFilters("tincan01.nif", "bhkRigidBody");
        if (filters is null)
        {
            return;
        }

        Assert.NotEmpty(filters);
        foreach (var filter in filters)
        {
            Assert.Equal(FolClutter, (byte)(filter & 0xFF));
            Assert.Equal(0, (int)(filter >> 16)); // Group must be 0, not swap garbage
        }
    }

    [Fact]
    public void Convert_JulySkeleton_RagdollBodiesKeepBipedLayerAndPartNumbers()
    {
        var filters = ConvertAndReadFilters(@"characters\_male\skeleton.nif", "bhkRigidBody");
        if (filters is null)
        {
            return;
        }

        Assert.NotEmpty(filters);
        var sawNonZeroFlags = false;
        foreach (var filter in filters)
        {
            Assert.Equal(FolBiped, (byte)(filter & 0xFF));
            Assert.Equal(0, (int)(filter >> 16));
            sawNonZeroFlags |= (filter & 0xFF00) != 0;
        }

        // Ragdoll bones carry per-bone part numbers in the flags byte (e.g. Pelvis = 2,
        // L Thigh = 8); losing them all to zero is exactly the regression.
        Assert.True(sawNonZeroFlags, "Expected at least one ragdoll body with a non-zero part number");
    }

    [Fact]
    public void Convert_JulyStatic_RigidBodyKeepsStaticLayer()
    {
        var filters = ConvertAndReadFilters("vendingmachine01.nif", "bhkRigidBody");
        if (filters is null)
        {
            return;
        }

        Assert.NotEmpty(filters);
        Assert.All(filters, f => Assert.Equal(FolStatic, (byte)(f & 0xFF)));
    }

    /// <summary>
    ///     Extracts the first BSA entry whose path ends with <paramref name="nifSuffix" />,
    ///     converts it Xbox→PC, and returns the little-endian Havok Filter dword of every
    ///     block of <paramref name="blockType" />. Null when Bucket B is disabled or the
    ///     sample archive is absent.
    /// </summary>
    private static List<uint>? ConvertAndReadFilters(string nifSuffix, string blockType)
    {
        BucketBTestGuard.SkipUnlessEnabled();

        var bsaPath = Path.Combine(SourceContract.RepoRoot, JulyMeshesBsa);
        if (!File.Exists(bsaPath))
        {
            return null;
        }

        using var extractor = new BsaExtractor(bsaPath);
        var entry = extractor.Archive.Folders
            .SelectMany(f => f.Files)
            .FirstOrDefault(f => f.FullPath.EndsWith(nifSuffix, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return null;
        }

        var nifBytes = extractor.ExtractFile(entry);
        var result = NifConverter.Convert(nifBytes);
        Assert.True(result.Success, $"Conversion of {entry.FullPath} failed");
        var converted = result.OutputData!;

        var info = NifParser.Parse(converted);
        Assert.NotNull(info);
        Assert.False(info!.IsBigEndian, "Converted NIF should be little-endian");

        var filters = new List<uint>();
        foreach (var block in info.Blocks.Where(b => b.TypeName == blockType))
        {
            var pos = block.DataOffset + HavokFilterOffsetInBlock;
            filters.Add(BitConverter.ToUInt32(converted, pos));
        }

        return filters;
    }
}
