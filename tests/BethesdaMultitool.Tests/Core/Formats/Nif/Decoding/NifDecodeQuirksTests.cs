using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Schema;
using BethesdaMultitool.Tests.Helpers;
using Xunit;
using static BethesdaMultitool.Tests.Core.Formats.Nif.Decoding.NifDecodingTestSupport;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Decoding;

/// <summary>
///     Pins every entry of <see cref="NifDecodeQuirks" /> with a value. None of these quirks changes a field's width,
///     so the per-block size check cannot see any of them: each test includes the negative case where the bytes are
///     laid out the other way, decodes COMPLETELY, and still yields the wrong value.
/// </summary>
public class NifDecodeQuirksTests
{
    // -- Quirk 1: BSPartFlag is little-endian inside big-endian files --

    private static readonly (long PartFlag, long BodyPart)[] AuthoredPartitions = [(0x0001L, 300L), (0x0100L, 230L)];

    private static NifTestFileBuilder Dismember(bool bigEndian, bool partFlagLittleEndian)
    {
        var builder = new NifTestFileBuilder(bigEndian, 34);
        builder.AddBlock("NiNode", w => NifTestBlockLayouts.Node(w, 34, -1, []));
        builder.AddBlock("BSDismemberSkinInstance", w =>
        {
            w.Ref(-1).Ref(-1).Ref(0); // NiSkinInstance: Data, Skin Partition, Skeleton Root (Ptr NiNode)
            w.U32(1).Ref(0); // Num Bones, Bones
            w.U32(2); // Num Partitions
            foreach (var (partFlag, bodyPart) in new (ushort, ushort)[] { (0x0001, 300), (0x0100, 230) })
            {
                if (partFlagLittleEndian)
                {
                    w.U16Le(partFlag);
                }
                else
                {
                    w.U16Be(partFlag);
                }

                w.U16(bodyPart); // Body Part stays in body order
            }
        });
        return builder;
    }

    private static (long PartFlag, long BodyPart)[] Partitions(NifDecodedBlock block)
    {
        return block.Root.Get<NifArrayValue>("Partitions").Items.Cast<NifStructValue>()
            .Select(p => (p.Get<NifIntegerValue>("Part Flag").Value, p.Get<NifIntegerValue>("Body Part").Value))
            .ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BSPartFlag_IsLittleEndianInBothByteOrders(bool bigEndian)
    {
        var block = DecodeStrict(Dismember(bigEndian, true), 1);

        Assert.True(block.IsComplete);
        Assert.Equal(AuthoredPartitions, Partitions(block));
        Assert.Equal("BSPartFlag", block.Root.Get<NifArrayValue>("Partitions").Items.Cast<NifStructValue>()
            .First().Get<NifIntegerValue>("Part Flag").TypeName);
    }

    /// <summary>
    ///     Negative control: a big-endian file whose BSPartFlag is written big-endian (what a naive Xbox writer would
    ///     do) PASSES the size check and decodes completely, yet PF_EDITOR_VISIBLE and PF_START_NET_BONESET swap. The
    ///     size check is not a value falsifier; only the value assertion catches this.
    /// </summary>
    [Fact]
    public void BSPartFlag_ByteSwapped_PassesTheSizeCheckButFailsTheValue()
    {
        var block = DecodeStrict(Dismember(true, false), 1);

        Assert.True(block.IsComplete);
        Assert.Equal(block.Size, block.ConsumedBytes);
        var partitions = Partitions(block);
        Assert.NotEqual(AuthoredPartitions, partitions);
        Assert.Equal(new (long, long)[] { (0x0100L, 300L), (0x0001L, 230L) }, partitions);
    }

    // -- Quirk 2: single-byte-field fixed-size structs are one big-endian unit --

    private static NifTestFileBuilder Palette(bool bigEndian, bool unitOrder)
    {
        var builder = new NifTestFileBuilder(bigEndian, 34);
        builder.AddBlock("NiPalette", w =>
        {
            w.U8(1); // Has Alpha
            w.U32(16); // Num Entries: selects the 16-entry Palette declaration
            for (var i = 0; i < 16; i++)
            {
                var (r, g, b, a) = Color(i);
                if (unitOrder && bigEndian)
                {
                    w.U8(a).U8(b).U8(g).U8(r); // the little-endian r,g,b,a unit, stored big-endian
                }
                else
                {
                    w.U8(r).U8(g).U8(b).U8(a);
                }
            }
        });
        return builder;
    }

    private static (byte R, byte G, byte B, byte A) Color(int i)
    {
        return ((byte)i, (byte)(0x10 + i), (byte)(0x20 + i), (byte)(0x40 + i));
    }

    private static (long, long, long, long)[] Channels(NifDecodedBlock block)
    {
        return block.Root.Get<NifArrayValue>("Palette").Items.Cast<NifStructValue>()
            .Select(c => (c.Get<NifIntegerValue>("r").Value, c.Get<NifIntegerValue>("g").Value,
                c.Get<NifIntegerValue>("b").Value, c.Get<NifIntegerValue>("a").Value))
            .ToArray();
    }

    private static (long, long, long, long)[] ExpectedChannels()
    {
        return Enumerable.Range(0, 16).Select(i =>
        {
            var (r, g, b, a) = Color(i);
            return ((long)r, (long)g, (long)b, (long)a);
        }).ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ByteColor4_IsReadAsOneUnitThenSplit(bool bigEndian)
    {
        var block = DecodeStrict(Palette(bigEndian, true), 0);

        Assert.True(block.IsComplete);
        Assert.Equal(ExpectedChannels(), Channels(block));
    }

    [Fact]
    public void ByteColor4_PerFieldBytesInABigEndianFile_PassTheSizeCheckButReverseTheChannels()
    {
        var block = DecodeStrict(Palette(true, false), 0);

        Assert.True(block.IsComplete);
        var channels = Channels(block);
        Assert.NotEqual(ExpectedChannels(), channels);
        var (r, g, b, a) = Color(3);
        Assert.Equal(((long)a, (long)b, (long)g, (long)r), channels[3]);
    }

    // -- Quirk 3: NiAGDDataBlock.Data is Block Size bytes, not Num Data x Block Size --

    private static readonly byte[] AgdPayload = [.. Enumerable.Range(0, 32).Select(i => (byte)(0xA0 + i))];

    private static NifTestFileBuilder PackedGeometry(bool bigEndian, byte[] payload)
    {
        var builder = new NifTestFileBuilder(bigEndian, 34);
        builder.AddBlock("BSPackedAdditionalGeometryData", w =>
        {
            w.U16(2); // Num Vertices
            w.U32(2); // Num Block Infos
            w.U32(7).U32(12).U32(24).U32(16).U32(0).U32(0).U8(2); // NiAGDDataStream: position channel
            w.U32(1).U32(4).U32(8).U32(16).U32(0).U32(12).U8(2); // NiAGDDataStream: color channel
            w.U32(1); // Num Blocks
            w.Bool(true); // NiAGDDataBlocks.Has Data
            w.U32(32); // NiAGDDataBlock: Block Size
            w.U32(1).U32(0); // Num Blocks, Block Offsets
            w.U32(2).U32(24).U32(8); // Num Data, Data Sizes
            w.Bytes(payload); // Data
            w.U32(7).U32(32); // Shader Index, Total Size (arg 1 only)
        });
        return builder;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AgdData_IsBlockSizeBytes_FollowedByShaderIndexAndTotalSize(bool bigEndian)
    {
        var block = DecodeStrict(PackedGeometry(bigEndian, AgdPayload), 0);

        Assert.True(block.IsComplete);
        Assert.Equal(2, block.Root.Get<NifArrayValue>("Block Infos").Count);
        var blocks = Assert.IsType<NifStructValue>(Assert.Single(block.Root.Get<NifArrayValue>("Blocks").Items));
        var data = blocks.Get<NifStructValue>("Data Block");
        AssertInteger(data, "Num Data", 2);
        Assert.Equal(AgdPayload, data.Get<NifByteArrayValue>("Data").Bytes.ToArray());
        AssertInteger(data, "Shader Index", 7);
        AssertInteger(data, "Total Size", 32);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AgdData_LaidOutAsNifXmlLiterallyDeclares_IsRejected(bool bigEndian)
    {
        byte[] literal = [.. AgdPayload, .. AgdPayload]; // Num Data (2) x Block Size (32)

        var failure = Assert.Throws<NifDecodeException>(() => DecodeStrict(PackedGeometry(bigEndian, literal), 0));

        Assert.Equal(NifDecodeFailureKind.Size, failure.Failure.Kind);
    }

    // -- Quirk 4: HavokFilter's byte order depends on its site --

    private const string HavokSchemaXml = """
        <niftoolsxml version="0.10.0.0">
          <basic name="byte" integral="true" size="1" />
          <basic name="ushort" integral="true" size="2" />
          <basic name="uint" integral="true" size="4" />
          <struct name="HavokFilter" size="4">
            <field name="Layer" type="byte" />
            <field name="Flags" type="byte" />
            <field name="Group" type="ushort" />
          </struct>
          <struct name="bhkRigidBodyCInfo2010">
            <field name="Havok Filter" type="HavokFilter" />
            <field name="Mass" type="uint" />
          </struct>
          <niobject name="NiObject" abstract="true" />
          <niobject name="TestFilterBlock" inherit="NiObject">
            <field name="Havok Filter" type="HavokFilter" />
            <field name="Rigid Body Info" type="bhkRigidBodyCInfo2010" />
          </niobject>
        </niftoolsxml>
        """;

    // Layer 5, Flags 0x80, Group 0x1234: the little-endian unit is 05 80 34 12 (uint 0x12348005).
    private static readonly byte[] FilterLittleEndianUnit = [0x05, 0x80, 0x34, 0x12];
    private static readonly byte[] FilterBigEndianUnit = [0x12, 0x34, 0x80, 0x05];

    private static NifTestFileBuilder FilterBlock(bool bigEndian, byte[] streamSite, byte[] cinfoSite)
    {
        var builder = new NifTestFileBuilder(bigEndian, 34);
        builder.AddBlock("TestFilterBlock", w =>
        {
            w.Bytes(streamSite);
            w.Bytes(cinfoSite).U32(1000);
        });
        return builder;
    }

    private static (long Layer, long Flags, long Group) Filter(NifStructValue filter)
    {
        return (filter.Get<NifIntegerValue>("Layer").Value, filter.Get<NifIntegerValue>("Flags").Value,
            filter.Get<NifIntegerValue>("Group").Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HavokFilter_IsABigEndianUnitAtStreamSites_AndRawLittleEndianInsideRigidBodyCInfo(bool bigEndian)
    {
        var schema = SchemaFromXml(HavokSchemaXml);
        var streamSite = bigEndian ? FilterBigEndianUnit : FilterLittleEndianUnit;
        var block = DecodeStrict(FilterBlock(bigEndian, streamSite, FilterLittleEndianUnit), 0, schema);

        Assert.True(block.IsComplete);
        Assert.Equal((5L, 0x80L, 0x1234L), Filter(block.Root.Get<NifStructValue>("Havok Filter")));
        var info = block.Root.Get<NifStructValue>("Rigid Body Info");
        Assert.Equal((5L, 0x80L, 0x1234L), Filter(info.Get<NifStructValue>("Havok Filter")));
        AssertInteger(info, "Mass", 1000);
    }

    [Fact]
    public void HavokFilter_WrongSiteConvention_PassesTheSizeCheckButMisreadsTheFilter()
    {
        var schema = SchemaFromXml(HavokSchemaXml);
        var block = DecodeStrict(FilterBlock(true, FilterLittleEndianUnit, FilterBigEndianUnit), 0, schema);

        Assert.True(block.IsComplete);
        Assert.NotEqual((5L, 0x80L, 0x1234L), Filter(block.Root.Get<NifStructValue>("Havok Filter")));
        var info = block.Root.Get<NifStructValue>("Rigid Body Info");
        Assert.NotEqual((5L, 0x80L, 0x1234L), Filter(info.Get<NifStructValue>("Havok Filter")));
    }

    // -- The quirk predicates themselves --

    [Fact]
    public void QuirkPredicates_SelectExactlyTheCitedCases()
    {
        var schema = NifSchema.LoadEmbedded();

        Assert.True(NifDecodeQuirks.IsLittleEndianInBigEndianFiles("BSPartFlag"));
        Assert.False(NifDecodeQuirks.IsLittleEndianInBigEndianFiles("BSDismemberBodyPartType"));

        Assert.True(NifDecodeQuirks.IsBigEndianUnitStruct(schema, schema.Structs["ByteColor4"]));
        Assert.True(NifDecodeQuirks.IsBigEndianUnitStruct(schema, schema.Structs["ByteColor4BGRA"]));
        Assert.False(NifDecodeQuirks.IsBigEndianUnitStruct(schema, schema.Structs["ByteColor3"])); // size 3
        Assert.False(NifDecodeQuirks.IsBigEndianUnitStruct(schema, schema.Structs["BodyPartList"])); // 2 x ushort
        Assert.False(NifDecodeQuirks.IsBigEndianUnitStruct(schema, schema.Structs["HalfTexCoord"])); // 2 x hfloat
        Assert.False(NifDecodeQuirks.IsBigEndianUnitStruct(schema, schema.Structs["HavokFilter"])); // ushort Group

        Assert.True(NifDecodeQuirks.IsAgdPayload("NiAGDDataBlock", "Data"));
        Assert.False(NifDecodeQuirks.IsAgdPayload("NiAGDDataBlock", "Data Sizes"));
        Assert.False(NifDecodeQuirks.IsAgdPayload("ByteArray", "Data"));

        Assert.True(NifDecodeQuirks.IsHavokFilterRawLittleEndian("bhkRigidBodyCInfo550_660"));
        Assert.True(NifDecodeQuirks.IsHavokFilterRawLittleEndian("bhkRigidBodyCInfo2010"));
        Assert.False(NifDecodeQuirks.IsHavokFilterRawLittleEndian("bhkRigidBody"));
        Assert.False(NifDecodeQuirks.IsHavokFilterRawLittleEndian("bhkNiTriStripsShape"));
    }
}
