using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Output;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     Tests for <see cref="Tes4HeaderBuilder" /> — verifies the synthesized plugin TES4 has
///     the master dependency, optional metadata subrecords, and parses correctly through
///     <see cref="EsmParser.ParseFileHeader" />.
/// </summary>
public class Tes4HeaderBuilderTests
{
    [Fact]
    public void Build_StartsWithTes4Signature()
    {
        var bytes = BuildSimple();
        Assert.Equal((byte)'T', bytes[0]);
        Assert.Equal((byte)'E', bytes[1]);
        Assert.Equal((byte)'S', bytes[2]);
        Assert.Equal((byte)'4', bytes[3]);
    }

    [Fact]
    public void Build_ParsesIntoFileHeader_WithExpectedFields()
    {
        var options = new PluginBuildOptions
        {
            MasterFileName = "FalloutNV.esm",
            MasterFileSize = 100,
            Author = "DMP-ESP test",
            Description = "Synthetic TES4 for unit test."
        };

        var bytes = Tes4HeaderBuilder.Build(options, 42, 0x800);
        var fileHeader = EsmParser.ParseFileHeader(bytes);

        Assert.NotNull(fileHeader);
        Assert.False(fileHeader!.IsBigEndian);
        Assert.Equal(Tes4HeaderBuilder.HedrVersion, fileHeader.Version);
        Assert.Equal(0x800u, fileHeader.NextObjectId);
        Assert.Equal("DMP-ESP test", fileHeader.Author);
        Assert.Equal("Synthetic TES4 for unit test.", fileHeader.Description);
        Assert.Single(fileHeader.Masters);
        Assert.Equal("FalloutNV.esm", fileHeader.Masters[0]);
    }

    [Fact]
    public void ParseFileHeader_DeclaredDataSizeExceedsBuffer_ClampsInsteadOfThrowing()
    {
        // EsmLoadOrderResolver / GameDetector pass only an ~8 KB prefix of a plugin to sniff masters +
        // version. Fallout 76's TES4 header is larger than that (many masters + a big ONAM overridden-form
        // list), so the record's declared DataSize exceeds the prefix — which used to throw
        // ArgumentOutOfRangeException on the header Slice and break FO76 load-order resolution. Simulate it
        // by inflating the DataSize field past the buffer; ParseFileHeader must clamp and still parse the
        // early subrecords (HEDR version + masters) that sit before the truncated tail.
        var bytes = Tes4HeaderBuilder.Build(
            new PluginBuildOptions { MasterFileName = "SeventySix.esm", MasterFileSize = 100 }, 0, 0x800);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 5_000_000); // record DataSize >> buffer

        var header = EsmParser.ParseFileHeader(bytes); // must not throw

        Assert.NotNull(header);
        Assert.Equal(Tes4HeaderBuilder.HedrVersion, header!.Version);
        Assert.Contains("SeventySix.esm", header.Masters);
    }

    [Fact]
    public void Build_OmitsAuthorAndDescription_WhenNullOrEmpty()
    {
        var bytes = Tes4HeaderBuilder.Build(
            new PluginBuildOptions
                { MasterFileName = "FalloutNV.esm", MasterFileSize = 100, Author = null, Description = "" },
            0,
            0x800);

        var header = EsmParser.ParseFileHeader(bytes);
        Assert.NotNull(header);
        Assert.Null(header!.Author);
        Assert.Null(header.Description);
        Assert.Contains("FalloutNV.esm", header.Masters);
    }

    [Fact]
    public void Build_FlagsClear_WhenNoNavmAugmentation()
    {
        // The ESM/master flag is tied to navmesh augmentation: with augmentation OFF the
        // plugin carries no navmesh edits, so it stays a plain ESP (deferred load, no
        // eager-init race). This is the playability config.
        var bytes = Tes4HeaderBuilder.Build(
            new PluginBuildOptions
            {
                MasterFileName = "FalloutNV.esm",
                MasterFileSize = 100,
                EmitMasterCellNavmAugmentation = false
            },
            0,
            0x800);

        // Header flags live at bytes 8–11 (little-endian uint32).
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        Assert.Equal(0u, flags & 0x00000001u); // master flag clear
    }

    [Fact]
    public void Build_FlagsSet_WhenNavmAugmentationEnabled()
    {
        // With master-cell navmesh augmentation ON the plugin emits NAVM/NAVI edits, which
        // FNV requires to ride on an ESM-flagged plugin or the NavMeshInfoMap desyncs.
        var bytes = Tes4HeaderBuilder.Build(
            new PluginBuildOptions
            {
                MasterFileName = "FalloutNV.esm",
                MasterFileSize = 100,
                EmitMasterCellNavmAugmentation = true
            },
            0,
            0x800);

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        Assert.Equal(0x00000001u, flags & 0x00000001u); // master flag set
    }

    [Fact]
    public void Build_HeaderVersion_Is0x000F()
    {
        var bytes = BuildSimple();
        // Version field lives at bytes 22–23 (little-endian uint16).
        var version = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22, 2));
        Assert.Equal(Tes4HeaderBuilder.RecordVersion, version);
    }

    [Fact]
    public void Build_DataAfterMast_IsEightZeroBytes()
    {
        // Per fopdoc, FNV's TES4 DATA after MAST is "always 0, probably vestigial".
        // Earlier code wrote master file size (non-canonical, FNVEdit warns).
        var bytes = Tes4HeaderBuilder.Build(
            new PluginBuildOptions { MasterFileName = "FalloutNV.esm", MasterFileSize = 12_345_678 },
            0,
            0x800);

        // Find the MAST subrecord and verify the DATA payload immediately after it.
        var mastIndex = FindSubrecordIndex(bytes, "MAST");
        Assert.True(mastIndex > 0, "MAST subrecord not found.");

        var mastLen = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(mastIndex + 4, 2));
        var dataIndex = mastIndex + 6 + mastLen;

        Assert.Equal((byte)'D', bytes[dataIndex]);
        Assert.Equal((byte)'A', bytes[dataIndex + 1]);
        Assert.Equal((byte)'T', bytes[dataIndex + 2]);
        Assert.Equal((byte)'A', bytes[dataIndex + 3]);

        var dataLen = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(dataIndex + 4, 2));
        Assert.Equal(8, dataLen);

        var payload = bytes.AsSpan(dataIndex + 6, 8);
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(0, payload[i]);
        }
    }

    private static int FindSubrecordIndex(byte[] bytes, string sig)
    {
        // TES4 subrecords start after the 24-byte record header.
        for (var i = 24; i + 6 <= bytes.Length;)
        {
            if (bytes[i] == sig[0] && bytes[i + 1] == sig[1] && bytes[i + 2] == sig[2] && bytes[i + 3] == sig[3])
            {
                return i;
            }

            var len = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 4, 2));
            i += 6 + len;
        }

        return -1;
    }

    private static byte[] BuildSimple()
    {
        return Tes4HeaderBuilder.Build(
            new PluginBuildOptions { MasterFileName = "FalloutNV.esm", MasterFileSize = 100 },
            0,
            0x800);
    }
}
