using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     v4 tests for <see cref="CellEncoder" /> exterior-cell support — XCLC emission,
///     interior bit cleared, canonical order.
/// </summary>
public class CellEncoderExteriorTests
{
    [Fact]
    public void Encode_ExteriorCell_KeepsInteriorBitClear()
    {
        // v4 respects the model's IsInterior. For exterior cells the model carries Flags
        // with bit 0 cleared; the encoder preserves that.
        var cell = new CellRecord
        {
            FormId = 0xC0,
            EditorId = "WastelandTile",
            Flags = 0x20, // public-place, exterior (bit 0 clear)
            GridX = 5,
            GridY = -3
        };

        var encoded = new CellEncoder().Encode(cell);

        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA");
        Assert.Single(data.Bytes);
        Assert.Equal(0x20, data.Bytes[0]); // public-place preserved, interior bit clear
    }

    [Fact]
    public void Encode_ExteriorCell_EmitsXclcWithGridCoords()
    {
        var cell = new CellRecord
        {
            FormId = 0xC0,
            EditorId = "WastelandTile",
            Flags = 0x00, // exterior
            GridX = 5,
            GridY = -3
        };

        var encoded = new CellEncoder().Encode(cell);

        var xclc = Assert.Single(encoded.Subrecords, s => s.Signature == "XCLC");
        Assert.Equal(12, xclc.Bytes.Length);
        Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(xclc.Bytes.AsSpan(0, 4)));
        Assert.Equal(-3, BinaryPrimitives.ReadInt32LittleEndian(xclc.Bytes.AsSpan(4, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(xclc.Bytes.AsSpan(8, 4))); // ForceHideLand
    }

    [Fact]
    public void Encode_InteriorCell_OmitsXclc()
    {
        var cell = new CellRecord { FormId = 0x42, EditorId = "Vault", Flags = 0x01 };
        var encoded = new CellEncoder().Encode(cell);

        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "XCLC");
    }

    [Fact]
    public void Encode_ExteriorCell_MissingGridCoords_StillEmitsXclcWithZeroCoordsAndWarning()
    {
        var cell = new CellRecord
        {
            FormId = 0xC0,
            EditorId = "Wasteland",
            Flags = 0x00, // exterior
            GridX = null,
            GridY = null
        };

        var encoded = new CellEncoder().Encode(cell);

        var xclc = Assert.Single(encoded.Subrecords, s => s.Signature == "XCLC");
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(xclc.Bytes.AsSpan(0, 4)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(xclc.Bytes.AsSpan(4, 4)));
        Assert.Contains(encoded.Warnings, w => w.Contains("grid coords"));
    }

    [Fact]
    public void Encode_CanonicalOrder_EdidBeforeFullBeforeDataBeforeXclc()
    {
        var cell = new CellRecord
        {
            FormId = 0xC0,
            EditorId = "Edid",
            FullName = "Display",
            Flags = 0x02,
            GridX = 1,
            GridY = 2,
            WaterHeight = -50f,
            Heightmap = new LandHeightmap
            {
                HeightOffset = 0f,
                HeightDeltas = new sbyte[33 * 33]
            }
        };

        var encoded = new CellEncoder().Encode(cell);

        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        var edidIndex = sigs.IndexOf("EDID");
        var fullIndex = sigs.IndexOf("FULL");
        var dataIndex = sigs.IndexOf("DATA");
        var xclcIndex = sigs.IndexOf("XCLC");
        var xclwIndex = sigs.IndexOf("XCLW");

        Assert.True(edidIndex < fullIndex);
        Assert.True(fullIndex < dataIndex);
        Assert.True(dataIndex < xclcIndex);
        Assert.True(xclcIndex < xclwIndex);
    }
}
