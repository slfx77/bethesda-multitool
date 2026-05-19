using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     Tests for <see cref="RefrEncoder.EncodeNewPlacedReference" /> — the v3 path that emits
///     a complete REFR record (not just a DATA-only override). Verifies NAME is present,
///     optional subrecords appear when set, and v3-deferred subrecords surface as warnings.
/// </summary>
public class NewRefEncoderTests
{
    [Fact]
    public void EncodeNewPlacedReference_AlwaysEmitsNameAndData()
    {
        var placed = new PlacedReference { FormId = 1, BaseFormId = 0xCAFE, X = 1, Y = 2, Z = 3 };

        var encoded = RefrEncoder.EncodeNewPlacedReference(placed);

        Assert.Contains(encoded.Subrecords, s => s.Signature == "NAME");
        Assert.Contains(encoded.Subrecords, s => s.Signature == "DATA");
    }

    [Fact]
    public void EncodeNewPlacedReference_NameContainsBaseFormId()
    {
        var placed = new PlacedReference { FormId = 1, BaseFormId = 0x12345678 };
        var encoded = RefrEncoder.EncodeNewPlacedReference(placed);

        var name = Assert.Single(encoded.Subrecords, s => s.Signature == "NAME");
        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(name.Bytes));
    }

    [Fact]
    public void EncodeNewPlacedReference_DefaultScale_OmitsXscl()
    {
        var placed = new PlacedReference { FormId = 1, Scale = 1.0f };
        var encoded = RefrEncoder.EncodeNewPlacedReference(placed);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "XSCL");
    }

    [Fact]
    public void EncodeNewPlacedReference_NonDefaultScale_EmitsXscl()
    {
        var placed = new PlacedReference { FormId = 1, Scale = 2.0f };
        var encoded = RefrEncoder.EncodeNewPlacedReference(placed);
        Assert.Contains(encoded.Subrecords, s => s.Signature == "XSCL");
    }

    [Fact]
    public void EncodeNewPlacedReference_OwnerSet_EmitsXown()
    {
        var placed = new PlacedReference { FormId = 1, OwnerFormId = 0xABCD };
        var encoded = RefrEncoder.EncodeNewPlacedReference(placed);

        var xown = Assert.Single(encoded.Subrecords, s => s.Signature == "XOWN");
        Assert.Equal(0xABCDu, BinaryPrimitives.ReadUInt32LittleEndian(xown.Bytes));
    }

    [Fact]
    public void EncodeNewPlacedReference_LockState_NowEmitsXloc()
    {
        // v4 closed the v3 deferred-XLOC gap. Lock state now emits XLOC bytes (see
        // PlacedRefSubrecordsTests for byte-layout assertions).
        var placed = new PlacedReference { FormId = 1, LockLevel = 50 };
        var encoded = RefrEncoder.EncodeNewPlacedReference(placed);

        Assert.Contains(encoded.Subrecords, s => s.Signature == "XLOC");
    }

    [Fact]
    public void EncodeNewPlacedReference_TeleportDoor_NowEmitsXtelWithWarning()
    {
        // v4 emits XTEL with the FormID + zero PosRot/Flags (model lacks teleport position).
        // Warning surfaces the partial coverage.
        var placed = new PlacedReference { FormId = 1, DestinationDoorFormId = 0xDEAD };
        var encoded = RefrEncoder.EncodeNewPlacedReference(placed);

        Assert.Contains(encoded.Subrecords, s => s.Signature == "XTEL");
        Assert.Contains(encoded.Warnings, w => w.Contains("XTEL"));
    }
}
