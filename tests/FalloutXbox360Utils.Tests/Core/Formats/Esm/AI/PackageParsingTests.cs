using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.AI;

/// <summary>
///     Tests for AI package parsing: PKDT, PSDT, PTDT subrecord parsing and flag decoding.
/// </summary>
public sealed class PackageParsingTests
{
    // ================================================================
    // PKDT - Package Data parsing
    // ================================================================

    [Fact]
    public void ParsePackageData_Xbox360BigEndian_DecodesCorrectly()
    {
        // PDB PACKAGE_DATA layout: [0-3]=iPackFlags(uint32), [4]=cPackType, [5]=unused,
        // [6-7]=iFOBehaviorFlags(uint16), [8-9]=iPackageSpecificFlags(uint16), [10-11]=unknown
        var data = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0), 0x00000207); // iPackFlags = 0x0207
        data[4] = 12; // cPackType = Sandbox
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(6), 0x0003); // FOBehavior = Hellos + Random Conversations
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(8), 0x007E); // TypeSpecific = all Sandbox flags

        var result = AiRecordHandler.ParsePackageData(data, isBigEndian: true);

        Assert.Equal(12, result.Type);
        Assert.Equal("Sandbox", result.TypeName);
        Assert.Equal(0x0207u, result.GeneralFlags);
        Assert.Equal(0x0003, result.FalloutBehaviorFlags);
        Assert.Equal(0x007E, result.TypeSpecificFlags);
    }

    [Fact]
    public void ParsePackageData_PcLittleEndian_DecodesCorrectly()
    {
        // Same logical values in PC LE byte order
        var data = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0), 0x00000207); // iPackFlags = 0x0207
        data[4] = 12; // cPackType = Sandbox
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), 0x0003); // FOBehavior
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), 0x007E); // TypeSpecific

        var result = AiRecordHandler.ParsePackageData(data, isBigEndian: false);

        Assert.Equal(12, result.Type);
        Assert.Equal("Sandbox", result.TypeName);
        Assert.Equal(0x0207u, result.GeneralFlags);
        Assert.Equal(0x0003, result.FalloutBehaviorFlags);
        Assert.Equal(0x007E, result.TypeSpecificFlags);
    }

    [Fact]
    public void ParsePackageData_Xbox360AndPc_ProduceSameResult()
    {
        // Build Xbox BE version: iPackFlags=0x00002001, cPackType=Wander(5)
        var xboxData = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(xboxData.AsSpan(0), 0x00002001); // Offers Services + Always Run
        xboxData[4] = 5; // cPackType = Wander
        BinaryPrimitives.WriteUInt16BigEndian(xboxData.AsSpan(6), 0x01FF); // All FO behaviors
        BinaryPrimitives.WriteUInt16BigEndian(xboxData.AsSpan(8), 0x0001); // Location Is Linked Ref

        // Build PC LE version (same logical values)
        var pcData = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(pcData.AsSpan(0), 0x00002001);
        pcData[4] = 5; // cPackType = Wander
        BinaryPrimitives.WriteUInt16LittleEndian(pcData.AsSpan(6), 0x01FF);
        BinaryPrimitives.WriteUInt16LittleEndian(pcData.AsSpan(8), 0x0001);

        var xboxResult = AiRecordHandler.ParsePackageData(xboxData, isBigEndian: true);
        var pcResult = AiRecordHandler.ParsePackageData(pcData, isBigEndian: false);

        Assert.Equal(xboxResult.Type, pcResult.Type);
        Assert.Equal(xboxResult.GeneralFlags, pcResult.GeneralFlags);
        Assert.Equal(xboxResult.FalloutBehaviorFlags, pcResult.FalloutBehaviorFlags);
        Assert.Equal(xboxResult.TypeSpecificFlags, pcResult.TypeSpecificFlags);
    }

    // ================================================================
    // PSDT - Package Schedule parsing
    // ================================================================

    [Fact]
    public void ParsePackageSchedule_EveryDay8AM_ProducesCorrectSummary()
    {
        // month=-1, dow=-1, date=0, time=8, duration=8 hours
        var data = new byte[8];
        data[0] = 0xFF; // Month = -1 (Any)
        data[1] = 0xFF; // DayOfWeek = -1 (Any)
        data[2] = 0; // Date = 0 (Any)
        data[3] = 8; // Time = 8 (8 AM)
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 8); // Duration = 8 hours

        var result = AiRecordHandler.ParsePackageSchedule(data, isBigEndian: false);

        Assert.Equal(-1, result.Month);
        Assert.Equal(-1, result.DayOfWeek);
        Assert.Equal(0, result.Date);
        Assert.Equal(8, result.Time);
        Assert.Equal(8, result.Duration);
        Assert.Equal("Every day, 8:00 AM for 8 hours", result.Summary);
    }

    [Fact]
    public void ParsePackageSchedule_SpecificDay_ProducesCorrectSummary()
    {
        // month=5 (June), dow=-1, date=15, time=12 (noon), duration=2 hours
        var data = new byte[8];
        data[0] = 5; // Month = June
        data[1] = 0xFF; // DayOfWeek = -1
        data[2] = 15; // Date = 15
        data[3] = 12; // Time = 12 (noon)
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 2); // Duration = 2 hours

        var result = AiRecordHandler.ParsePackageSchedule(data, isBigEndian: false);

        Assert.Equal("June", result.MonthName);
        Assert.Equal("June 15, 12:00 PM for 2 hours", result.Summary);
    }

    [Fact]
    public void ParsePackageSchedule_Xbox360BigEndian_Duration()
    {
        var data = new byte[8];
        data[0] = 0xFF; // Any month
        data[1] = 0xFF; // Any day
        data[2] = 0; // Any date
        data[3] = 20; // 8 PM
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(4), 6); // Duration = 6 hours

        var result = AiRecordHandler.ParsePackageSchedule(data, isBigEndian: true);

        Assert.Equal(6, result.Duration);
        Assert.Equal("Every day, 8:00 PM for 6 hours", result.Summary);
    }

    // ================================================================
    // PTDT - Package Target parsing
    // ================================================================

    [Fact]
    public void ParsePackageTarget_SpecificReference_DecodesCorrectly()
    {
        var data = new byte[16];
        data[0] = 0; // Type = Specific Reference
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 0x0012ABCD); // FormID
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), 5); // CountDistance
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(12), 100.0f); // AcquireRadius

        var result = AiRecordHandler.ParsePackageTarget(data, isBigEndian: false);

        Assert.Equal(0, result.Type);
        Assert.Equal("Specific Reference", result.TypeName);
        Assert.Equal(0x0012ABCDu, result.FormIdOrType);
        Assert.Equal(5, result.CountDistance);
        Assert.Equal(100.0f, result.AcquireRadius);
    }

    [Fact]
    public void ParsePackageTarget_ObjectType_DecodesCorrectly()
    {
        var data = new byte[16];
        data[0] = 2; // Type = Object Type
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 42); // Object type enum
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), 1);

        var result = AiRecordHandler.ParsePackageTarget(data, isBigEndian: false);

        Assert.Equal(2, result.Type);
        Assert.Equal("Object Type", result.TypeName);
        Assert.Equal(42u, result.FormIdOrType);
    }

    // ================================================================
    // Package type names
    // ================================================================

    [Theory]
    [InlineData(0, "Find")]
    [InlineData(1, "Follow")]
    [InlineData(2, "Escort")]
    [InlineData(3, "Eat")]
    [InlineData(4, "Sleep")]
    [InlineData(5, "Wander")]
    [InlineData(6, "Travel")]
    [InlineData(7, "Accompany")]
    [InlineData(8, "Use Item At")]
    [InlineData(9, "Ambush")]
    [InlineData(10, "Flee Not Combat")]
    [InlineData(12, "Sandbox")]
    [InlineData(13, "Patrol")]
    [InlineData(14, "Guard")]
    [InlineData(15, "Dialogue")]
    [InlineData(16, "Use Weapon")]
    public void PackageTypeName_AllKnownValues_ReturnExpectedName(byte typeCode, string expectedName)
    {
        var data = new PackageData { Type = typeCode };
        Assert.Equal(expectedName, data.TypeName);
    }

    [Fact]
    public void PackageTypeName_UnknownValue_ReturnsUnknownWithCode()
    {
        var data = new PackageData { Type = 99 };
        Assert.Equal("Unknown (99)", data.TypeName);
    }

    // ================================================================
    // Flag decoding
    // ================================================================

    [Fact]
    public void FlagRegistry_PackageGeneralFlags_DecodesKnownBits()
    {
        // 0x00000007 = Offers Services | Must Reach Location | Must Complete
        var result = FlagRegistry.DecodeFlagNames(0x00000007, FlagRegistry.PackageGeneralFlags);
        Assert.Contains("Offers Services", result);
        Assert.Contains("Must Reach Location", result);
        Assert.Contains("Must Complete", result);
    }

    [Fact]
    public void FlagRegistry_PackageFOBehaviorFlags_DecodesAllNineBits()
    {
        // 0x01FF = all 9 FO behavior bits set
        var result = FlagRegistry.DecodeFlagNames(0x01FF, FlagRegistry.PackageFOBehaviorFlags);
        Assert.Contains("Hellos to Player", result);
        Assert.Contains("Random Conversations", result);
        Assert.Contains("Observe Combat", result);
        Assert.Contains("Greet Corpse", result);
        Assert.Contains("React to Player Actions", result);
        Assert.Contains("Friendly Fire Comments", result);
        Assert.Contains("Aggro Radius Behavior", result);
        Assert.Contains("Idle Chatter", result);
        Assert.Contains("Avoid Radiation", result);
    }

    [Fact]
    public void FlagRegistry_PackageTypeSpecificFlags_DecodesSandboxFlags()
    {
        // 0x007E = all Sandbox allow flags (bits 1-6)
        var result = FlagRegistry.DecodeFlagNames(0x007E, FlagRegistry.PackageTypeSpecificFlags);
        Assert.Contains("Allow Eating", result);
        Assert.Contains("Allow Sleeping", result);
        Assert.Contains("Allow Conversation", result);
        Assert.Contains("Allow Idle Markers", result);
        Assert.Contains("Allow Furniture", result);
        Assert.Contains("Allow Wandering", result);
    }

    // ================================================================
    // PKPT - Patrol Data parsing
    // ================================================================

    [Fact]
    public void ParsePatrolData_RepeatableOnly_DecodesCorrectly()
    {
        var data = new byte[] { 1, 0 };
        var (repeatable, linkedRef) = AiRecordHandler.ParsePatrolData(data);
        Assert.True(repeatable);
        Assert.False(linkedRef);
    }

    [Fact]
    public void ParsePatrolData_BothSet_DecodesCorrectly()
    {
        var data = new byte[] { 1, 1 };
        var (repeatable, linkedRef) = AiRecordHandler.ParsePatrolData(data);
        Assert.True(repeatable);
        Assert.True(linkedRef);
    }

    [Fact]
    public void ParsePatrolData_NeitherSet_DecodesCorrectly()
    {
        var data = new byte[] { 0, 0 };
        var (repeatable, linkedRef) = AiRecordHandler.ParsePatrolData(data);
        Assert.False(repeatable);
        Assert.False(linkedRef);
    }

    [Fact]
    public void ParsePatrolData_NonZeroRepeatable_TreatsAsTrueNotJustOne()
    {
        // Any non-zero value should be treated as true
        var data = new byte[] { 0xFF, 0 };
        var (repeatable, linkedRef) = AiRecordHandler.ParsePatrolData(data);
        Assert.True(repeatable);
        Assert.False(linkedRef);
    }
}
