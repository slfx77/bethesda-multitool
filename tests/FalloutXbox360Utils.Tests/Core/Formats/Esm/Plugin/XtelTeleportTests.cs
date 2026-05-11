using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     v5 tests for XTEL teleport position/rotation/flags emission. Verifies that when the
///     PlacedReference model carries TeleportPosRot/TeleportFlags (populated from PC ESM
///     XTEL bytes), the encoder emits them at the correct offsets in the 32-byte XTEL.
/// </summary>
public class XtelTeleportTests
{
    [Fact]
    public void EncodeNewPlacedReference_WithTeleportPosRot_PopulatesAllSixFloats()
    {
        var placed = new PlacedReference
        {
            FormId = 1,
            BaseFormId = 0xCAFE,
            DestinationDoorFormId = 0xDEADBEEF,
            TeleportPosRot = new PositionSubrecord(
                X: 100.0f, Y: -200.5f, Z: 50.25f,
                RotX: 0.5f, RotY: 1.5f, RotZ: 3.14f,
                Offset: 0, IsBigEndian: false),
            TeleportFlags = 0x01
        };

        var encoded = RefrEncoder.EncodeNewPlacedReference(placed);

        var xtel = Assert.Single(encoded.Subrecords, s => s.Signature == "XTEL");
        Assert.Equal(32, xtel.Bytes.Length);
        Assert.Equal(0xDEADBEEFu, BinaryPrimitives.ReadUInt32LittleEndian(xtel.Bytes.AsSpan(0, 4)));
        Assert.Equal(100.0f, BinaryPrimitives.ReadSingleLittleEndian(xtel.Bytes.AsSpan(4, 4)));
        Assert.Equal(-200.5f, BinaryPrimitives.ReadSingleLittleEndian(xtel.Bytes.AsSpan(8, 4)));
        Assert.Equal(50.25f, BinaryPrimitives.ReadSingleLittleEndian(xtel.Bytes.AsSpan(12, 4)));
        Assert.Equal(0.5f, BinaryPrimitives.ReadSingleLittleEndian(xtel.Bytes.AsSpan(16, 4)));
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(xtel.Bytes.AsSpan(20, 4)));
        Assert.Equal(3.14f, BinaryPrimitives.ReadSingleLittleEndian(xtel.Bytes.AsSpan(24, 4)));
        Assert.Equal((byte)0x01, xtel.Bytes[28]);

        // Verify no warning about missing PosRot.
        Assert.DoesNotContain(encoded.Warnings,
            w => w.Contains("teleport position not available"));
    }

    [Fact]
    public void EncodeNewPlacedReference_NoTeleportPosRot_StillEmitsXtelWithZerosAndWarning()
    {
        var placed = new PlacedReference
        {
            FormId = 1,
            BaseFormId = 0xCAFE,
            DestinationDoorFormId = 0xDEADBEEF
            // TeleportPosRot and TeleportFlags both null
        };

        var encoded = RefrEncoder.EncodeNewPlacedReference(placed);

        var xtel = Assert.Single(encoded.Subrecords, s => s.Signature == "XTEL");
        Assert.Equal(32, xtel.Bytes.Length);
        Assert.Equal(0xDEADBEEFu, BinaryPrimitives.ReadUInt32LittleEndian(xtel.Bytes.AsSpan(0, 4)));
        // PosRot and Flags should be zero.
        for (var i = 4; i < 32; i++)
        {
            Assert.Equal(0, xtel.Bytes[i]);
        }

        Assert.Contains(encoded.Warnings,
            w => w.Contains("teleport position not available"));
    }
}
