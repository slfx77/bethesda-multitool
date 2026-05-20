using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Records;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Records;

public class EsmWorldExtractorLandDiagnosticsTests
{
    [Fact]
    public void ExtractLandFromBuffer_ReportsTerrainVisualSubrecords()
    {
        var data = BuildLandRecordData(
            ("VCLR", new byte[9]),
            ("VTEX", BuildVtex(0x10, 0x20, 0x30)),
            ("BTXT", BuildTextureLayer(0x00123456, quadrant: 2, platformFlag: 7, layer: 0)),
            ("ATXT", BuildTextureLayer(0x00654321, quadrant: 3, platformFlag: 9, layer: 4)),
            ("VTXT", BuildVtxt((12, 0.25f), (288, 1.0f))));
        var header = new DetectedMainRecord("LAND", (uint)data.Length, 0, 0x000ABCDE, 0x1000, false);

        var land = EsmWorldExtractor.ExtractLandFromBuffer(data, data.Length, header);

        Assert.NotNull(land);
        Assert.Null(land.Heightmap);
        Assert.Equal(9, land.VclrByteCount);
        Assert.Equal(3, land.VtexCount);
        Assert.Equal(1, land.BtxtCount);
        Assert.Equal(1, land.AtxtCount);
        Assert.Equal(1, land.VtxtCount);
        Assert.Equal(16, land.VtxtByteCount);
        Assert.Equal(2, land.TextureLayers.Count);

        Assert.NotNull(land.VisualData);
        Assert.Equal(9, land.VisualData.VertexColors!.Length);
        Assert.NotNull(land.VisualData.TextureIndices);
        Assert.Equal(new uint[] { 0x10u, 0x20u, 0x30u }, land.VisualData.TextureIndices);
        Assert.Equal(2, land.VisualData.TextureLayers.Count);
        Assert.Equal(LandTextureLayerKind.Base, land.VisualData.TextureLayers[0].Kind);
        Assert.Equal(7, land.VisualData.TextureLayers[0].PlatformFlag);
        Assert.Equal(LandTextureLayerKind.Alpha, land.VisualData.TextureLayers[1].Kind);
        Assert.Equal(9, land.VisualData.TextureLayers[1].PlatformFlag);
        Assert.Equal(4, land.VisualData.TextureLayers[1].Layer);
        Assert.Equal(2, land.VisualData.TextureLayers[1].BlendEntries.Count);
        Assert.Equal(12, land.VisualData.TextureLayers[1].BlendEntries[0].Position);
        Assert.Equal(0.25f, land.VisualData.TextureLayers[1].BlendEntries[0].Opacity);
        Assert.Equal(288, land.VisualData.TextureLayers[1].BlendEntries[1].Position);
        Assert.Equal(0, land.VisualData.UnattachedVtxtCount);
    }

    [Fact]
    public void ExtractLandFromBuffer_VtxtWithoutAtxt_IsDiagnosticOnly()
    {
        var data = BuildLandRecordData(
            ("VTXT", BuildVtxt((5, 0.75f))),
            ("BTXT", BuildTextureLayer(0x00123456, quadrant: 0, platformFlag: 0, layer: 0)),
            ("VTXT", BuildVtxt((6, 0.5f))));
        var header = new DetectedMainRecord("LAND", (uint)data.Length, 0, 0x000ABCDE, 0x1000, false);

        var land = EsmWorldExtractor.ExtractLandFromBuffer(data, data.Length, header);

        Assert.NotNull(land);
        Assert.NotNull(land.VisualData);
        Assert.Equal(2, land.VtxtCount);
        Assert.Equal(2, land.VisualData.UnattachedVtxtCount);
        Assert.Empty(land.VisualData.TextureLayers[0].BlendEntries);
    }

    [Fact]
    public void ExtractedLandRecord_BestCell_PrefersParentCellCoordinates()
    {
        var land = new ExtractedLandRecord
        {
            Header = new DetectedMainRecord("LAND", 0, 0, 0x000ABCDE, 0x1000, false),
            CellX = -2,
            CellY = 4,
            RuntimeCellX = 10,
            RuntimeCellY = 11
        };

        Assert.Equal(-2, land.BestCellX);
        Assert.Equal(4, land.BestCellY);
    }

    [Fact]
    public void EnrichLandRecordsWithCellWorldspaces_PreservesExistingLandWorldspace()
    {
        var scanResult = new EsmRecordScanResult
        {
            LandRecords =
            [
                new ExtractedLandRecord
                {
                    Header = new DetectedMainRecord("LAND", 0, 0, 0x000ABCDE, 0x1000, false),
                    ParentCellFormId = 0x00006000,
                    WorldspaceFormId = 0x00001000
                }
            ]
        };
        var cells = new[]
        {
            new CellRecord
            {
                FormId = 0x00006000,
                GridX = 2,
                GridY = 3,
                WorldspaceFormId = 0x00002000
            }
        };

        EsmLandEnricher.EnrichLandRecordsWithCellWorldspaces(scanResult, cells);

        var land = Assert.Single(scanResult.LandRecords);
        Assert.Equal(0x00001000u, land.WorldspaceFormId);
        Assert.Equal(2, land.CellX);
        Assert.Equal(3, land.CellY);
    }

    [Fact]
    public void EnrichLandRecordsWithCellWorldspaces_AuthorityParentOverridesExistingLandWorldspace()
    {
        var scanResult = new EsmRecordScanResult
        {
            LandRecords =
            [
                new ExtractedLandRecord
                {
                    Header = new DetectedMainRecord("LAND", 0, 0, 0x000ABCDE, 0x1000, false),
                    ParentCellFormId = 0x00006000,
                    WorldspaceFormId = 0x00001000
                }
            ]
        };
        var cells = new[]
        {
            new CellRecord
            {
                FormId = 0x00006000,
                GridX = -18,
                GridY = 0,
                WorldspaceFormId = 0x00002000,
                WorldspaceAssignmentSource = "Authority"
            }
        };

        EsmLandEnricher.EnrichLandRecordsWithCellWorldspaces(scanResult, cells);

        var land = Assert.Single(scanResult.LandRecords);
        Assert.Equal(0x00002000u, land.WorldspaceFormId);
        Assert.Equal(-18, land.CellX);
        Assert.Equal(0, land.CellY);
    }

    [Fact]
    public void EnrichLandRecordsWithCellWorldspaces_PrefersResolvedDuplicateParentCell()
    {
        var scanResult = new EsmRecordScanResult
        {
            LandRecords =
            [
                new ExtractedLandRecord
                {
                    Header = new DetectedMainRecord("LAND", 0, 0, 0x000ABCDE, 0x1000, false),
                    ParentCellFormId = 0x00006000
                }
            ]
        };
        var cells = new[]
        {
            new CellRecord
            {
                FormId = 0x00006000,
                IsUnresolvedBucket = true
            },
            new CellRecord
            {
                FormId = 0x00006000,
                GridX = 3,
                GridY = -2,
                WorldspaceFormId = 0x00002000
            }
        };

        EsmLandEnricher.EnrichLandRecordsWithCellWorldspaces(scanResult, cells);

        var land = Assert.Single(scanResult.LandRecords);
        Assert.Equal(0x00002000u, land.WorldspaceFormId);
        Assert.Equal(3, land.CellX);
        Assert.Equal(-2, land.CellY);
    }

    private static byte[] BuildTextureLayer(uint textureFormId, byte quadrant, byte platformFlag, ushort layer)
    {
        var data = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(data, textureFormId);
        data[4] = quadrant;
        data[5] = platformFlag;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), layer);
        return data;
    }

    private static byte[] BuildVtex(params uint[] formIds)
    {
        var data = new byte[formIds.Length * 4];
        for (var i = 0; i < formIds.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i * 4), formIds[i]);
        }

        return data;
    }

    private static byte[] BuildVtxt(params (ushort Position, float Opacity)[] entries)
    {
        var data = new byte[entries.Length * 8];
        for (var i = 0; i < entries.Length; i++)
        {
            var offset = i * 8;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), entries[i].Position);
            data[offset + 2] = 0xAA;
            data[offset + 3] = 0xBB;
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset + 4), entries[i].Opacity);
        }

        return data;
    }

    private static byte[] BuildLandRecordData(params (string Signature, byte[] Data)[] subrecords)
    {
        using var stream = new MemoryStream();
        foreach (var (signature, data) in subrecords)
        {
            var sigBytes = System.Text.Encoding.ASCII.GetBytes(signature);
            stream.Write(sigBytes);
            var length = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(length, checked((ushort)data.Length));
            stream.Write(length);
            stream.Write(data);
        }

        return stream.ToArray();
    }
}
