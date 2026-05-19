using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     SCOL (Static Collection) encoder tests: canonical layout, multiple parts, ONAM
///     reachability validation, and DATA placement-float endian correctness.
/// </summary>
public class ScolEncoderTests
{
    [Fact]
    public void Encode_OverrideAlwaysEmpty()
    {
        var scol = new StaticCollectionRecord { FormId = 0x100, EditorId = "S" };

        var encoded = new ScolEncoder().Encode(scol);

        Assert.Empty(encoded.Subrecords);
    }

    [Fact]
    public void EncodeNew_SinglePart_CanonicalOrderAndDataLayout()
    {
        var scol = new StaticCollectionRecord
        {
            FormId = 0x100,
            EditorId = "ScolOne",
            ModelPath = "meshes/scols/one.nif",
            Parts =
            {
                new StaticCollectionPart
                {
                    OnamFormId = 0x0003D377,
                    Placements =
                    {
                        new StaticCollectionPlacement(1f, 2f, 3f, 0.1f, 0.2f, 0.3f, 1.0f),
                        new StaticCollectionPlacement(10f, 20f, 30f, 1.1f, 1.2f, 1.3f, 2.5f)
                    }
                }
            }
        };

        var encoded = ScolEncoder.EncodeNew(scol, new HashSet<uint> { 0x0003D377 }, new HashSet<uint>());

        Assert.Equal(["EDID", "MODL", "ONAM", "DATA"], encoded.Subrecords.Select(s => s.Signature));

        var onam = encoded.Subrecords[2];
        Assert.Equal(4, onam.Bytes.Length);
        Assert.Equal(0x0003D377u, BinaryPrimitives.ReadUInt32LittleEndian(onam.Bytes));

        var data = encoded.Subrecords[3];
        Assert.Equal(56, data.Bytes.Length); // 2 placements * 28 bytes
        Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(0, 4)));
        Assert.Equal(2f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(4, 4)));
        Assert.Equal(3f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(8, 4)));
        Assert.Equal(0.1f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(12, 4)));
        Assert.Equal(0.2f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(16, 4)));
        Assert.Equal(0.3f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(20, 4)));
        Assert.Equal(1.0f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(24, 4)));
        // Second placement (offset 28).
        Assert.Equal(10f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(28, 4)));
        Assert.Equal(2.5f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(28 + 24, 4)));
    }

    [Fact]
    public void EncodeNew_MultipleParts_PreservesOrderAndPairsOnamWithData()
    {
        var scol = new StaticCollectionRecord
        {
            FormId = 0x100,
            EditorId = "ScolMulti",
            Parts =
            {
                new StaticCollectionPart
                {
                    OnamFormId = 0xAAAA,
                    Placements = { new StaticCollectionPlacement(1, 0, 0, 0, 0, 0, 1) }
                },
                new StaticCollectionPart
                {
                    OnamFormId = 0xBBBB,
                    Placements = { new StaticCollectionPlacement(2, 0, 0, 0, 0, 0, 1) }
                }
            }
        };

        var encoded = ScolEncoder.EncodeNew(
            scol, new HashSet<uint> { 0xAAAA, 0xBBBB }, new HashSet<uint>());

        Assert.Equal(
            ["EDID", "ONAM", "DATA", "ONAM", "DATA"],
            encoded.Subrecords.Select(s => s.Signature));
        Assert.Equal(0xAAAAu, BinaryPrimitives.ReadUInt32LittleEndian(encoded.Subrecords[1].Bytes));
        Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(encoded.Subrecords[2].Bytes.AsSpan(0, 4)));
        Assert.Equal(0xBBBBu, BinaryPrimitives.ReadUInt32LittleEndian(encoded.Subrecords[3].Bytes));
        Assert.Equal(2f, BinaryPrimitives.ReadSingleLittleEndian(encoded.Subrecords[4].Bytes.AsSpan(0, 4)));
    }

    [Fact]
    public void EncodeNew_PartWithUnreachableOnam_IsDroppedWithWarning()
    {
        var scol = new StaticCollectionRecord
        {
            FormId = 0x100,
            EditorId = "ScolMix",
            Parts =
            {
                new StaticCollectionPart
                {
                    OnamFormId = 0x1111,
                    Placements = { new StaticCollectionPlacement(0, 0, 0, 0, 0, 0, 1) }
                },
                new StaticCollectionPart
                {
                    OnamFormId = 0xDEAD, // unreachable
                    Placements = { new StaticCollectionPlacement(0, 0, 0, 0, 0, 0, 1) }
                }
            }
        };

        var encoded = ScolEncoder.EncodeNew(scol, new HashSet<uint> { 0x1111 }, new HashSet<uint>());

        Assert.Equal(["EDID", "ONAM", "DATA"], encoded.Subrecords.Select(s => s.Signature));
        Assert.Equal(0x1111u, BinaryPrimitives.ReadUInt32LittleEndian(encoded.Subrecords[1].Bytes));
        Assert.Contains(encoded.Warnings, w => w.Contains("0x0000DEAD"));
    }

    [Fact]
    public void EncodeNew_OnamReachableViaEmittedNewStats_KeepsPart()
    {
        var scol = new StaticCollectionRecord
        {
            FormId = 0x100,
            EditorId = "ScolProto",
            Parts =
            {
                new StaticCollectionPart
                {
                    OnamFormId = 0x01000800, // new-record allocator range
                    Placements = { new StaticCollectionPlacement(0, 0, 0, 0, 0, 0, 1) }
                }
            }
        };

        var encoded = ScolEncoder.EncodeNew(
            scol,
            masterFormIds: new HashSet<uint>(),
            emittedNewStats: new HashSet<uint> { 0x01000800 });

        Assert.Equal(["EDID", "ONAM", "DATA"], encoded.Subrecords.Select(s => s.Signature));
    }

    [Fact]
    public void EncodeNew_AllPartsUnreachable_ReturnsEmptySubrecordsAndWarns()
    {
        var scol = new StaticCollectionRecord
        {
            FormId = 0x0003D377,
            EditorId = "SCOLParkingLotChunk03",
            Parts =
            {
                new StaticCollectionPart
                {
                    OnamFormId = 0xDEAD,
                    Placements = { new StaticCollectionPlacement(0, 0, 0, 0, 0, 0, 1) }
                }
            }
        };

        var encoded = ScolEncoder.EncodeNew(scol, new HashSet<uint>(), new HashSet<uint>());

        Assert.Empty(encoded.Subrecords);
        Assert.NotEmpty(encoded.Warnings);
    }
}
