using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     v4 tests for the previously-deferred placed-ref subrecords (XLOC, XESP, XLKR, XTEL)
///     plus the v3 XCNT bug regression. Verifies byte layouts match fopdoc + the codebase's
///     parser expectations.
/// </summary>
public class PlacedRefSubrecordsTests
{
    [Fact]
    public void EncodeNewPlacedReference_XCNT_IsFourBytes()
    {
        // v3 wrote 2 bytes; parser's Simple4Byte schema requires 4. Regression test.
        var placed = new PlacedReference { FormId = 1, BaseFormId = 0xCAFE, Count = (short)42 };

        var encoded = RefrEncoder.EncodeNewPlacedReference(placed);

        var xcnt = Assert.Single(encoded.Subrecords, s => s.Signature == "XCNT");
        Assert.Equal(4, xcnt.Bytes.Length);
        Assert.Equal((short)42, BinaryPrimitives.ReadInt16LittleEndian(xcnt.Bytes.AsSpan(0, 2)));
        Assert.Equal(0, xcnt.Bytes[2]);
        Assert.Equal(0, xcnt.Bytes[3]);
    }

    [Fact]
    public void EncodeNewPlacedReference_XLOC_IsTwentyBytesWithCorrectLayout()
    {
        var placed = new PlacedReference
        {
            FormId = 1,
            BaseFormId = 0xCAFE,
            LockLevel = 50,
            LockKeyFormId = 0xABCDEF,
            LockFlags = 0x04,
            LockNumTries = 7,
            LockTimesUnlocked = 3
        };

        var encoded = RefrEncoder.EncodeNewPlacedReference(placed);

        var xloc = Assert.Single(encoded.Subrecords, s => s.Signature == "XLOC");
        Assert.Equal(20, xloc.Bytes.Length);
        Assert.Equal(50, xloc.Bytes[0]);
        Assert.Equal(0, xloc.Bytes[1]); // padding
        Assert.Equal(0xABCDEFu, BinaryPrimitives.ReadUInt32LittleEndian(xloc.Bytes.AsSpan(4, 4)));
        Assert.Equal(0x04, xloc.Bytes[8]);
        Assert.Equal(0, xloc.Bytes[9]); // padding
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(xloc.Bytes.AsSpan(12, 4)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(xloc.Bytes.AsSpan(16, 4)));
    }

    [Fact]
    public void EncodeNewPlacedReference_XLOC_PartialFieldsZeroPaddedDefaults()
    {
        // Only LockLevel set — other fields default to 0.
        var placed = new PlacedReference { FormId = 1, BaseFormId = 0xCAFE, LockLevel = 75 };
        var encoded = RefrEncoder.EncodeNewPlacedReference(placed);

        var xloc = Assert.Single(encoded.Subrecords, s => s.Signature == "XLOC");
        Assert.Equal(20, xloc.Bytes.Length);
        Assert.Equal(75, xloc.Bytes[0]);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(xloc.Bytes.AsSpan(4, 4)));
        Assert.Equal(0, xloc.Bytes[8]);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(xloc.Bytes.AsSpan(12, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(xloc.Bytes.AsSpan(16, 4)));
    }

    [Fact]
    public void EncodeNewPlacedReference_XESP_IsEightBytesWithFlags()
    {
        var placed = new PlacedReference
        {
            FormId = 1,
            BaseFormId = 0xCAFE,
            EnableParentFormId = 0x12345678,
            EnableParentFlags = 0x01
        };

        var encoded = RefrEncoder.EncodeNewPlacedReference(placed);

        var xesp = Assert.Single(encoded.Subrecords, s => s.Signature == "XESP");
        Assert.Equal(8, xesp.Bytes.Length);
        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(xesp.Bytes.AsSpan(0, 4)));
        Assert.Equal(0x01, xesp.Bytes[4]);
        Assert.Equal(0, xesp.Bytes[5]);
        Assert.Equal(0, xesp.Bytes[6]);
        Assert.Equal(0, xesp.Bytes[7]);
    }

    [Fact]
    public void EncodeNewPlacedReference_XLKR_FourByteForm_WhenNoKeyword()
    {
        var placed = new PlacedReference
        {
            FormId = 1,
            BaseFormId = 0xCAFE,
            LinkedRefFormId = 0xDEADBEEF
        };

        var encoded = RefrEncoder.EncodeNewPlacedReference(placed);

        var xlkr = Assert.Single(encoded.Subrecords, s => s.Signature == "XLKR");
        Assert.Equal(4, xlkr.Bytes.Length);
        Assert.Equal(0xDEADBEEFu, BinaryPrimitives.ReadUInt32LittleEndian(xlkr.Bytes));
    }

    [Fact]
    public void EncodeNewPlacedReference_XLKR_EightByteForm_WhenKeywordSet()
    {
        var placed = new PlacedReference
        {
            FormId = 1,
            BaseFormId = 0xCAFE,
            LinkedRefKeywordFormId = 0x11111111,
            LinkedRefFormId = 0x22222222
        };

        var encoded = RefrEncoder.EncodeNewPlacedReference(placed);

        var xlkr = Assert.Single(encoded.Subrecords, s => s.Signature == "XLKR");
        Assert.Equal(8, xlkr.Bytes.Length);
        Assert.Equal(0x11111111u, BinaryPrimitives.ReadUInt32LittleEndian(xlkr.Bytes.AsSpan(0, 4)));
        Assert.Equal(0x22222222u, BinaryPrimitives.ReadUInt32LittleEndian(xlkr.Bytes.AsSpan(4, 4)));
    }

    [Fact]
    public void EncodeNewPlacedReference_XTEL_IsThirtyTwoBytesWithFormIdAndZeros()
    {
        var placed = new PlacedReference
        {
            FormId = 1,
            BaseFormId = 0xCAFE,
            DestinationDoorFormId = 0xDEADBEEF
        };

        var encoded = RefrEncoder.EncodeNewPlacedReference(placed);

        var xtel = Assert.Single(encoded.Subrecords, s => s.Signature == "XTEL");
        Assert.Equal(32, xtel.Bytes.Length);
        Assert.Equal(0xDEADBEEFu, BinaryPrimitives.ReadUInt32LittleEndian(xtel.Bytes.AsSpan(0, 4)));
        // Bytes 4-31 should all be zero (PosRot + Flags + padding).
        for (var i = 4; i < 32; i++)
        {
            Assert.Equal(0, xtel.Bytes[i]);
        }

        Assert.Contains(encoded.Warnings, w => w.Contains("XTEL"));
    }

    [Fact]
    public void EncodeNewPlacedReference_CanonicalOrder_IsNameThenExtrasThenData()
    {
        var placed = new PlacedReference
        {
            FormId = 1,
            BaseFormId = 0xCAFE,
            X = 1, Y = 2, Z = 3,
            LockLevel = 50,
            EnableParentFormId = 0xAA,
            LinkedRefFormId = 0xBB,
            DestinationDoorFormId = 0xCC,
            OwnerFormId = 0xDD,
            EncounterZoneFormId = 0xEE,
            Count = 5,
            Scale = 2.0f
        };

        var encoded = RefrEncoder.EncodeNewPlacedReference(placed);

        // First should be NAME, last should be DATA.
        Assert.Equal("NAME", encoded.Subrecords[0].Signature);
        Assert.Equal("DATA", encoded.Subrecords[^1].Signature);

        // XSCL should come right before DATA.
        Assert.Equal("XSCL", encoded.Subrecords[^2].Signature);
    }
}
