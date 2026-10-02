using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Presentation;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Presentation;

/// <summary>
///     The typed PACK location/target layer (PLDT/PLD2, PTDT/PTD2) that <c>show</c>, the GUI record browser
///     and the PACK presentation profile all format through. A package union is a FormID only on the arms
///     <c>PackageReferenceIntegrity</c> names — location 0/1/4, target 0/1 — and every other arm is an
///     enum value or unused. Formatting every non-zero union as a FormID printed an object type of 18 as
///     HorseMarker, because FalloutNV.esm really does own the low FormIDs of the engine markers.
/// </summary>
public sealed class PackageUnionPresentationTests
{
    // The FormIDs FalloutNV.esm assigns to these engine records. A non-FormID union that happens to equal
    // one of them must never be printed as the record.
    private const uint DoorMarker = 0x00000001;
    private const uint TravelMarker = 0x00000002;
    private const uint NorthMarker = 0x00000003;
    private const uint Player = 0x00000007;
    private const uint HorseMarker = 0x00000012;
    private const uint PrimmGenericHouse01 = 0x0009A285;

    private static readonly FormIdResolver Resolver = new(
        new Dictionary<uint, string>
        {
            [DoorMarker] = "DoorMarker",
            [TravelMarker] = "TravelMarker",
            [NorthMarker] = "NorthMarker",
            [Player] = "Player",
            [HorseMarker] = "HorseMarker",
            [PrimmGenericHouse01] = "PrimmGenericHouse01"
        },
        []);

    // ================================================================
    // Target (PTDT/PTD2)
    // ================================================================

    [Fact]
    public void FormatPackageTarget_ObjectTypeArm_IsNotResolvedAsFormId()
    {
        // Target type 2 carries xEdit's wbObjectTypeEnum, not a FormID; 18 is the most common non-zero
        // object type in the retail FNV PTDT/PTD2 census (58 subrecords). Resolved as a FormID it read as
        // "Object Type: HorseMarker".
        var target = new PackageTarget { Type = 2, FormIdOrType = 18, CountDistance = 1, AcquireRadius = 0f };

        var text = RecordDetailHelpers.FormatPackageTarget(target, Resolver);

        Assert.Equal($"Object Type: 18, count 1, radius {0f:F1}", text);
        Assert.DoesNotContain("HorseMarker", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatPackageTarget_ObjectTypeArmAtZero_PrintsTheEnumValue()
    {
        // Zero is enum value 0 ("None" in wbObjectTypeEnum), not an absent FormID.
        var target = new PackageTarget { Type = 2, FormIdOrType = 0, CountDistance = 0, AcquireRadius = 0f };

        Assert.Equal($"Object Type: 0, count 0, radius {0f:F1}",
            RecordDetailHelpers.FormatPackageTarget(target, Resolver));
    }

    [Fact]
    public void FormatPackageTarget_LinkedReferenceArmWithAValue_PrintsItRaw()
    {
        // Linked Reference (type 3) has an unused union arm in FNV. A stray value equal to the Player's
        // FormID must not be shown as the Player.
        var target = new PackageTarget { Type = 3, FormIdOrType = Player, CountDistance = 0, AcquireRadius = 0f };

        var text = RecordDetailHelpers.FormatPackageTarget(target, Resolver);

        Assert.Equal($"Linked Reference: raw 0x00000007, count 0, radius {0f:F1}", text);
        Assert.DoesNotContain("Player", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatPackageTarget_UnknownType_PrintsTheUnionRaw()
    {
        var target = new PackageTarget { Type = 4, FormIdOrType = TravelMarker, CountDistance = 0, AcquireRadius = 0f };

        Assert.Equal($"Unknown (4): raw 0x00000002, count 0, radius {0f:F1}",
            RecordDetailHelpers.FormatPackageTarget(target, Resolver));
    }

    [Fact]
    public void FormatPackageTarget_FormIdArms_KeepTheirResolvedText()
    {
        // Guard: the FormID arms print exactly what they printed before the arm gate existed. PlayerRef
        // (0x14) is not a record, so it stays hex; count 750 is PACK 0x000E62E1's retail value.
        var specific = new PackageTarget { Type = 0, FormIdOrType = 0x14, CountDistance = 750, AcquireRadius = 0f };
        var objectId = new PackageTarget
        {
            Type = 1, FormIdOrType = HorseMarker, CountDistance = 2, AcquireRadius = 256f
        };
        var empty = new PackageTarget { Type = 0, FormIdOrType = 0, CountDistance = 0, AcquireRadius = 0f };

        Assert.Equal($"Specific Reference: 0x00000014, count 750, radius {0f:F1}",
            RecordDetailHelpers.FormatPackageTarget(specific, Resolver));
        Assert.Equal($"Object ID: HorseMarker, count 2, radius {256f:F1}",
            RecordDetailHelpers.FormatPackageTarget(objectId, Resolver));
        Assert.Equal($"Specific Reference: (none), count 0, radius {0f:F1}",
            RecordDetailHelpers.FormatPackageTarget(empty, Resolver));
    }

    // ================================================================
    // Location (PLDT/PLD2)
    // ================================================================

    [Fact]
    public void FormatPackageLocation_InCellArm_StillResolvesTheCell()
    {
        // Guard: PACK 0x000FE923's retail PLDT (type 1, PrimmGenericHouse01, radius 0) reads as before.
        var location = new PackageLocation { Type = 1, Union = PrimmGenericHouse01, Radius = 0 };

        Assert.Equal("Type 1, PrimmGenericHouse01, radius 0",
            RecordDetailHelpers.FormatPackageLocation(location, Resolver));
    }

    [Fact]
    public void FormatPackageLocation_ReferenceAndObjectIdArms_KeepTheirResolvedText()
    {
        // Guard: a FormID arm is resolved whatever its value, low FormIDs included.
        var nearReference = new PackageLocation { Type = 0, Union = DoorMarker, Radius = 256 };
        var objectId = new PackageLocation { Type = 4, Union = 0x00ABCDEF, Radius = 0 };
        var empty = new PackageLocation { Type = 1, Union = 0, Radius = 0 };

        Assert.Equal("Type 0, DoorMarker, radius 256",
            RecordDetailHelpers.FormatPackageLocation(nearReference, Resolver));
        Assert.Equal("Type 4, 0x00ABCDEF, radius 0",
            RecordDetailHelpers.FormatPackageLocation(objectId, Resolver));
        Assert.Equal("Type 1, (none), radius 0",
            RecordDetailHelpers.FormatPackageLocation(empty, Resolver));
    }

    [Fact]
    public void FormatPackageLocation_ObjectTypeArm_IsNotResolvedAsFormId()
    {
        var location = new PackageLocation { Type = 5, Union = 18, Radius = 512 };

        var text = RecordDetailHelpers.FormatPackageLocation(location, Resolver);

        Assert.Equal("Type 5, object type 18, radius 512", text);
        Assert.DoesNotContain("HorseMarker", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData((byte)2, TravelMarker, "TravelMarker")]
    [InlineData((byte)3, NorthMarker, "NorthMarker")]
    [InlineData((byte)6, Player, "Player")]
    [InlineData((byte)7, DoorMarker, "DoorMarker")]
    public void FormatPackageLocation_UnusedArmWithAValue_PrintsItRaw(byte type, uint union, string wrongLabel)
    {
        // Near current/editor location, near linked reference and at package location carry no FormID.
        var location = new PackageLocation { Type = type, Union = union, Radius = 0 };

        var text = RecordDetailHelpers.FormatPackageLocation(location, Resolver);

        Assert.Equal($"Type {type}, raw 0x{union:X8}, radius 0", text);
        Assert.DoesNotContain(wrongLabel, text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatPackageLocation_UnusedArmAtZero_KeepsNone()
    {
        // Guard: every retail unused arm is zero, and those lines read as they always did.
        var location = new PackageLocation { Type = 3, Union = 0, Radius = 0 };

        Assert.Equal("Type 3, (none), radius 0", RecordDetailHelpers.FormatPackageLocation(location, Resolver));
    }

    [Fact]
    public void FormatPackageLocation_UnknownType_PrintsTheUnionRaw()
    {
        var location = new PackageLocation { Type = 9, Union = DoorMarker, Radius = 0 };

        Assert.Equal("Type 9, raw 0x00000001, radius 0", RecordDetailHelpers.FormatPackageLocation(location, Resolver));
    }

    [Theory]
    [InlineData((byte)0, "Near Reference")]
    [InlineData((byte)1, "In Cell")]
    [InlineData((byte)2, "Near Current Location")]
    [InlineData((byte)3, "Near Editor Location")]
    [InlineData((byte)4, "Object ID")]
    [InlineData((byte)5, "Object Type")]
    [InlineData((byte)6, "Near Linked Reference")]
    [InlineData((byte)7, "At Package Location")]
    [InlineData((byte)8, "Unknown (8)")]
    [InlineData((byte)12, "Unknown (12)")]
    public void PackageLocationTypeName_FollowsTheFnvPldtEnum(byte type, string expected)
    {
        // xEdit wbDefinitionsFNV.pas, PLDT/PLD2 'Type'. There is no type 12 in FNV.
        Assert.Equal(expected, new PackageLocation { Type = type }.TypeName);
    }

    // ================================================================
    // Typed parsers feeding the formatters
    // ================================================================

    [Theory]
    // PACK 0x000FE923's PLDT in the 2022 Steam master (little-endian) ...
    [InlineData(false, new byte[] { 0x01, 0x00, 0x00, 0x00, 0x85, 0xA2, 0x09, 0x00, 0x00, 0x00, 0x00, 0x00 })]
    // ... and in the July 2010 Xbox 360 master: the type byte is NOT swapped, the union is.
    [InlineData(true, new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00, 0x09, 0xA2, 0x85, 0x00, 0x00, 0x00, 0x00 })]
    public void ParsePackageLocation_RetailInCellBytes_DecodeTheSameCellInBothByteOrders(
        bool bigEndian, byte[] pldt)
    {
        var location = AiRecordHandler.ParsePackageLocation(pldt, bigEndian);

        Assert.Equal(1, location.Type);
        Assert.Equal("In Cell", location.TypeName);
        Assert.Equal(PrimmGenericHouse01, location.Union);
        Assert.Equal(0, location.Radius);
        Assert.Equal("Type 1, PrimmGenericHouse01, radius 0",
            RecordDetailHelpers.FormatPackageLocation(location, Resolver));
    }

    [Theory]
    // The first twelve bytes of PACK 0x000E62E1's PTDT (Specific Reference, PlayerRef 0x14, count 750),
    // little- and big-endian. xEdit requires only Type, Target and Count / Distance, so twelve is legal.
    [InlineData(false, new byte[] { 0x00, 0x00, 0x00, 0x00, 0x14, 0x00, 0x00, 0x00, 0xEE, 0x02, 0x00, 0x00 })]
    [InlineData(true, new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x14, 0x00, 0x00, 0x02, 0xEE })]
    public void ParsePackageTarget_TwelveByteForm_DecodesWithoutTheOptionalFloat(bool bigEndian, byte[] ptdt)
    {
        var target = AiRecordHandler.ParsePackageTarget(ptdt, bigEndian);

        Assert.Equal(0, target.Type);
        Assert.Equal(0x00000014u, target.FormIdOrType);
        Assert.Equal(750, target.CountDistance);
        Assert.Equal(0f, target.AcquireRadius);
    }

    [Fact]
    public void ParsePackageTarget_SixteenByteBigEndianForm_StillReadsTheFloat()
    {
        // Object Type 18, count 3, acquire radius 128.0 (0x43000000), big-endian.
        byte[] ptdt =
        [
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x12, 0x00, 0x00, 0x00, 0x03, 0x43, 0x00, 0x00, 0x00
        ];

        var target = AiRecordHandler.ParsePackageTarget(ptdt, true);

        Assert.Equal(2, target.Type);
        Assert.Equal(18u, target.FormIdOrType);
        Assert.Equal(3, target.CountDistance);
        Assert.Equal(128f, target.AcquireRadius);
        Assert.Equal($"Object Type: 18, count 3, radius {128f:F1}",
            RecordDetailHelpers.FormatPackageTarget(target, Resolver));
    }

    // ================================================================
    // The PACK detail model show and the GUI render
    // ================================================================

    [Fact]
    public void BuildPackage_ObjectTypeTarget_ShowsTheEnumValueAndKeepsTheCell()
    {
        var package = new PackageRecord
        {
            FormId = 0x00ABC123,
            EditorId = "SyntheticObjectTypePackage",
            Location = new PackageLocation { Type = 1, Union = PrimmGenericHouse01, Radius = 0 },
            Target = new PackageTarget { Type = 2, FormIdOrType = 18, CountDistance = 1, AcquireRadius = 0f }
        };

        var model = RecordDetailBuilders.BuildPackage(package, Resolver);

        Assert.Equal("Type 1, PrimmGenericHouse01, radius 0", Primary(model, "Location"));
        Assert.Equal($"Object Type: 18, count 1, radius {0f:F1}", Primary(model, "Target"));
    }

    private static string? Primary(RecordDetailModel model, string sectionTitle)
    {
        var section = Assert.Single(model.Sections, candidate => candidate.Title == sectionTitle);
        return Assert.Single(section.Entries, entry => entry.Label == "Primary").Value;
    }
}
