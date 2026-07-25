using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Conversion;

/// <summary>
///     Round-trip gate for <see cref="BigEndianNifBuilder" />: the production parser (which
///     handles retail Xbox 360 NIFs and is therefore the reference) must accept the synthetic
///     big-endian fixture and read back exactly the values the builder claims to have written.
///     If these fail, fix the FIXTURE — the conversion regression tests in this directory are
///     only meaningful once this gate is green.
/// </summary>
public sealed class BigEndianNifBuilderTests
{
    [Fact]
    public void Parse_ReportsBigEndianFnvHeaderIdentity()
    {
        var info = Assert.IsType<NifInfo>(NifParser.Parse(BigEndianNifBuilder.Build()));

        Assert.True(info.IsBigEndian);
        Assert.Equal(0x14020007u, info.BinaryVersion);
        Assert.Equal(11u, info.UserVersion);
        Assert.Equal(34u, info.BsVersion);
        Assert.Equal(["SynthRoot", "SynthShape"], info.Strings);
    }

    [Fact]
    public void Parse_ReportsExpectedBlockListAndTightLayout()
    {
        var data = BigEndianNifBuilder.Build();
        var info = Assert.IsType<NifInfo>(NifParser.Parse(data));

        Assert.Equal(7, info.BlockCount);
        Assert.Equal(
            [
                "NiNode", "NiTriShape", "NiTriShapeData", "NiAlphaProperty",
                "BSShaderNoLightingProperty", "BSDismemberSkinInstance", "NiAdditionalGeometryData"
            ],
            info.Blocks.Select(b => b.TypeName));

        // Blocks are contiguous and the footer (num roots + one root index) ends the file —
        // any block whose hand-computed size disagrees with its written bytes shows up here.
        for (var i = 1; i < info.Blocks.Count; i++)
        {
            Assert.Equal(info.Blocks[i - 1].DataOffset + info.Blocks[i - 1].Size, info.Blocks[i].DataOffset);
        }

        var lastBlock = info.Blocks[^1];
        Assert.Equal(data.Length, lastBlock.DataOffset + lastBlock.Size + 8);
    }

    [Fact]
    public void Parse_AlphaPropertyFieldsReadBackAtDocumentedOffsets()
    {
        var data = BigEndianNifBuilder.Build(0x12EC, 80);
        var info = Assert.IsType<NifInfo>(NifParser.Parse(data));
        var alphaBlock = info.Blocks[BigEndianNifBuilder.NiAlphaPropertyBlockIndex];

        Assert.Equal("NiAlphaProperty", alphaBlock.TypeName);
        Assert.Equal(0x12EC, BinaryPrimitives.ReadUInt16BigEndian(
            data.AsSpan(alphaBlock.DataOffset + BigEndianNifBuilder.AlphaFlagsOffsetInBlock)));
        Assert.Equal(80, data[alphaBlock.DataOffset + BigEndianNifBuilder.AlphaThresholdOffsetInBlock]);
    }

    [Fact]
    public void Parse_DismemberPartitionsReadBackWithNativePartFlagsAndBigEndianBodyParts()
    {
        var data = BigEndianNifBuilder.Build();
        var info = Assert.IsType<NifInfo>(NifParser.Parse(data));
        var dismemberBlock = info.Blocks[BigEndianNifBuilder.DismemberBlockIndex];
        Assert.Equal("BSDismemberSkinInstance", dismemberBlock.TypeName);

        var partitionsPos = dismemberBlock.DataOffset + BigEndianNifBuilder.DismemberPartitionsOffsetInBlock;
        Assert.Equal((uint)BigEndianNifBuilder.DefaultPartitions.Length,
            BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(partitionsPos - 4)));

        for (var i = 0; i < BigEndianNifBuilder.DefaultPartitions.Length; i++)
        {
            var (expectedFlag, expectedBodyPart) = BigEndianNifBuilder.DefaultPartitions[i];
            // PartFlag is PC-native (little-endian) on disk even in the BE file; BodyPart is BE.
            Assert.Equal(expectedFlag,
                BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(partitionsPos + i * 4)));
            Assert.Equal(expectedBodyPart,
                BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(partitionsPos + i * 4 + 2)));
        }
    }

    [Fact]
    public void Parse_AgdPayloadReadsBackAtDocumentedOffset()
    {
        var payload = BigEndianNifBuilder.DefaultAgdPayload();
        var data = BigEndianNifBuilder.Build(agdPayload: payload);
        var info = Assert.IsType<NifInfo>(NifParser.Parse(data));
        var agdBlock = info.Blocks[BigEndianNifBuilder.AdditionalGeometryDataBlockIndex];

        Assert.Equal("NiAdditionalGeometryData", agdBlock.TypeName);
        Assert.Equal(BigEndianNifBuilder.AgdPayloadOffsetInBlock + payload.Length, agdBlock.Size);

        var payloadPos = agdBlock.DataOffset + BigEndianNifBuilder.AgdPayloadOffsetInBlock;
        // Block Size and Num Data sit directly before Data Sizes[0] + the payload.
        Assert.Equal((uint)payload.Length,
            BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(payloadPos - 20)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(payloadPos - 8)));
        Assert.Equal(payload, data.AsSpan(payloadPos, payload.Length).ToArray());
    }

    [Fact]
    public void Convert_SyntheticFixture_SucceedsInPlaceAndReparsesAsLittleEndian()
    {
        var data = BigEndianNifBuilder.Build();

        var result = NifConverter.Convert(data);

        Assert.True(result.Success, result.ErrorMessage);
        var converted = Assert.IsType<byte[]>(result.OutputData);
        Assert.Equal(data.Length, converted.Length); // no expansions/strips → in-place conversion

        var info = Assert.IsType<NifInfo>(NifParser.Parse(converted));
        Assert.False(info.IsBigEndian);
        Assert.Equal(7, info.BlockCount);
    }
}