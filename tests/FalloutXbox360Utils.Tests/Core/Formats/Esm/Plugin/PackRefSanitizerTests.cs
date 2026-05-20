using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.AI;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     PLDT/PLD2 + PTDT/PTD2 dangling-FormID sanitizer tests for PackEncoder.EncodeNew.
///     Dangling Package Location FormIDs trigger the engine errors "Unable to find Package
///     Location Reference" and "AI: is assigned a reference location that doesnt exist for
///     a package" — the NPC's AI then falls through to default idle. Remap when possible,
///     otherwise rewrite the Type byte to a no-FormID-needed variant (NearCurrentLocation
///     for PLDT, Object Type for PTDT) so the package still loads cleanly.
/// </summary>
public class PackRefSanitizerTests
{
    private const byte PlocNearReference = 0;
    private const byte PlocNearCurrent = 2;
    private const byte PtdtSpecificReference = 0;
    private const byte PtdtObjectType = 2;

    [Fact]
    public void EncodeNew_keeps_PLDT_when_no_validFormIds_supplied()
    {
        var pack = MakePack(loc: new PackageLocation
        {
            Type = PlocNearReference, Union = 0x000CDA76u, Radius = 100
        });

        var encoded = PackEncoder.EncodeNew(pack);

        var pldt = Assert.Single(encoded.Subrecords, s => s.Signature == "PLDT");
        Assert.Equal(PlocNearReference, pldt.Bytes[0]);
        Assert.Equal(0x000CDA76u, BinaryPrimitives.ReadUInt32LittleEndian(pldt.Bytes.AsSpan(4, 4)));
    }

    [Fact]
    public void EncodeNew_keeps_PLDT_when_Union_FormID_is_valid()
    {
        var pack = MakePack(loc: new PackageLocation
        {
            Type = PlocNearReference, Union = 0x000ED239u, Radius = 100
        });
        var valid = new HashSet<uint> { 0x000ED239u };

        var encoded = PackEncoder.EncodeNew(pack, validFormIds: valid);

        var pldt = Assert.Single(encoded.Subrecords, s => s.Signature == "PLDT");
        Assert.Equal(PlocNearReference, pldt.Bytes[0]);
        Assert.Equal(0x000ED239u, BinaryPrimitives.ReadUInt32LittleEndian(pldt.Bytes.AsSpan(4, 4)));
    }

    [Fact]
    public void EncodeNew_falls_back_to_NearCurrent_when_PLDT_Union_dangles_no_remap()
    {
        // 0x00122985 is one of the actual dangling refs from the live error log.
        var pack = MakePack(loc: new PackageLocation
        {
            Type = PlocNearReference, Union = 0x00122985u, Radius = 100
        });
        var valid = new HashSet<uint> { 0x00000001u };

        var encoded = PackEncoder.EncodeNew(pack, validFormIds: valid);

        var pldt = Assert.Single(encoded.Subrecords, s => s.Signature == "PLDT");
        Assert.Equal(PlocNearCurrent, pldt.Bytes[0]);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(pldt.Bytes.AsSpan(4, 4)));
        Assert.Contains(encoded.Warnings, w => w.Contains("PLDT") && w.Contains("fallback"));
    }

    [Fact]
    public void EncodeNew_remaps_PLDT_Union_when_dangling_ref_is_in_remap_table()
    {
        var pack = MakePack(loc: new PackageLocation
        {
            Type = PlocNearReference, Union = 0x01999AAAu, Radius = 100
        });
        var valid = new HashSet<uint> { 0x01000123u };
        var remap = new Dictionary<uint, uint> { [0x01999AAAu] = 0x01000123u };

        var encoded = PackEncoder.EncodeNew(pack, validFormIds: valid, remapTable: remap);

        var pldt = Assert.Single(encoded.Subrecords, s => s.Signature == "PLDT");
        Assert.Equal(PlocNearReference, pldt.Bytes[0]);   // Type preserved
        Assert.Equal(0x01000123u, BinaryPrimitives.ReadUInt32LittleEndian(pldt.Bytes.AsSpan(4, 4)));
        Assert.Contains(encoded.Warnings, w => w.Contains("PLDT") && w.Contains("remapped"));
    }

    [Fact]
    public void EncodeNew_does_not_touch_PLDT_when_Type_is_ObjectType_enum()
    {
        // Type 5 (ObjectType) has Union = form-type enum, NOT a FormID. Don't validate.
        var pack = MakePack(loc: new PackageLocation { Type = 5, Union = 42u, Radius = 0 });
        var valid = new HashSet<uint>();    // empty — Union is not a FormID anyway

        var encoded = PackEncoder.EncodeNew(pack, validFormIds: valid);

        var pldt = Assert.Single(encoded.Subrecords, s => s.Signature == "PLDT");
        Assert.Equal(5, pldt.Bytes[0]);
        Assert.Equal(42u, BinaryPrimitives.ReadUInt32LittleEndian(pldt.Bytes.AsSpan(4, 4)));
    }

    [Fact]
    public void EncodeNew_falls_back_PTDT_to_ObjectType_when_FormIdOrType_dangles()
    {
        var pack = MakePack(target: new PackageTarget
        {
            Type = PtdtSpecificReference, FormIdOrType = 0x000DEAD1u
        });
        var valid = new HashSet<uint> { 0x00000001u };

        var encoded = PackEncoder.EncodeNew(pack, validFormIds: valid);

        var ptdt = Assert.Single(encoded.Subrecords, s => s.Signature == "PTDT");
        Assert.Equal(PtdtObjectType, ptdt.Bytes[0]);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(ptdt.Bytes.AsSpan(4, 4)));
        Assert.Contains(encoded.Warnings, w => w.Contains("PTDT") && w.Contains("fallback"));
    }

    [Fact]
    public void EncodeNew_keeps_PTDT_when_Type_is_ObjectType_enum()
    {
        // PTDT Type 2 = ObjectType enum, FormIdOrType is a Form-type code, not a FormID.
        var pack = MakePack(target: new PackageTarget { Type = PtdtObjectType, FormIdOrType = 41u });
        var valid = new HashSet<uint>();

        var encoded = PackEncoder.EncodeNew(pack, validFormIds: valid);

        var ptdt = Assert.Single(encoded.Subrecords, s => s.Signature == "PTDT");
        Assert.Equal(PtdtObjectType, ptdt.Bytes[0]);
        Assert.Equal(41u, BinaryPrimitives.ReadUInt32LittleEndian(ptdt.Bytes.AsSpan(4, 4)));
    }

    [Fact]
    public void EncodeNew_PLD2_and_PTD2_share_the_same_sanitization_path()
    {
        var pack = new PackageRecord
        {
            FormId = 0x01000A00,
            EditorId = "TestPack",
            Data = new PackageData(),
            Location2 = new PackageLocation { Type = PlocNearReference, Union = 0xBADBADu, Radius = 0 },
            Target2 = new PackageTarget { Type = PtdtSpecificReference, FormIdOrType = 0xBADBADu }
        };
        var valid = new HashSet<uint>();

        var encoded = PackEncoder.EncodeNew(pack, validFormIds: valid);

        var pld2 = Assert.Single(encoded.Subrecords, s => s.Signature == "PLD2");
        Assert.Equal(PlocNearCurrent, pld2.Bytes[0]);
        var ptd2 = Assert.Single(encoded.Subrecords, s => s.Signature == "PTD2");
        Assert.Equal(PtdtObjectType, ptd2.Bytes[0]);
    }

    private static PackageRecord MakePack(PackageLocation? loc = null, PackageTarget? target = null)
    {
        return new PackageRecord
        {
            FormId = 0x01000A00,
            EditorId = "TestPack",
            Data = new PackageData(),    // empty PKDT
            Location = loc,
            Target = target
        };
    }
}
