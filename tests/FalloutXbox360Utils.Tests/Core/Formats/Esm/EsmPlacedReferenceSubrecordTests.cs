using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm;

public sealed class EsmPlacedReferenceSubrecordTests
{
    [Fact]
    public void ExtractRefrRecordsFromParsed_ReadsLockAndEightByteLinkedRefVariant()
    {
        const uint refrFormId = 0x00150010;
        const uint baseFormId = 0x00150020;
        const uint ownerFormId = 0x00150030;
        const uint encounterZoneFormId = 0x00150035;
        const uint keyFormId = 0x00150040;
        const uint enableParentFormId = 0x00150050;
        const uint linkedRefKeywordFormId = 0x00150060;
        const uint linkedRefFormId = 0x00150070;
        const uint destinationDoorFormId = 0x00150080;

        var record = new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "REFR",
                DataSize = 0,
                Flags = 0,
                FormId = refrFormId
            },
            Offset = 0x200,
            Subrecords =
            [
                MakeFormIdSubrecord("NAME", baseFormId),
                MakeFormIdSubrecord("XOWN", ownerFormId),
                MakeFormIdSubrecord("XEZN", encounterZoneFormId),
                MakeLockSubrecord(60, keyFormId, 0x03, 5, 2),
                MakeFormIdSubrecord("XTEL", destinationDoorFormId),
                MakeEnableParentSubrecord(enableParentFormId, 0x01),
                MakeEightByteLinkedRefSubrecord(linkedRefKeywordFormId, linkedRefFormId)
            ]
        };

        var scanResult = new EsmRecordScanResult();
        EsmDataExtractor.ExtractRefrRecordsFromParsed(scanResult, [record], bigEndian: false);

        var refr = Assert.Single(scanResult.RefrRecords);
        Assert.Equal(baseFormId, refr.BaseFormId);
        Assert.Equal(ownerFormId, refr.OwnerFormId);
        Assert.Equal(encounterZoneFormId, refr.EncounterZoneFormId);
        Assert.Equal((byte)60, refr.LockLevel);
        Assert.Equal(keyFormId, refr.LockKeyFormId);
        Assert.Equal((byte)0x03, refr.LockFlags);
        Assert.Equal(5u, refr.LockNumTries);
        Assert.Equal(2u, refr.LockTimesUnlocked);
        Assert.Equal(destinationDoorFormId, refr.DestinationDoorFormId);
        Assert.Equal(enableParentFormId, refr.EnableParentFormId);
        Assert.Equal((byte)0x01, refr.EnableParentFlags);
        Assert.Equal(linkedRefKeywordFormId, refr.LinkedRefKeywordFormId);
        Assert.Equal(linkedRefFormId, refr.LinkedRefFormId);
    }

    [Fact]
    public void ExtractRefrRecordsFromParsed_ReadsFourByteLinkedRefVariantWithoutKeyword()
    {
        const uint refrFormId = 0x00150110;
        const uint baseFormId = 0x00150120;
        const uint linkedRefFormId = 0x00150130;

        var record = new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "REFR",
                DataSize = 0,
                Flags = 0,
                FormId = refrFormId
            },
            Offset = 0x280,
            Subrecords =
            [
                MakeFormIdSubrecord("NAME", baseFormId),
                MakeFormIdSubrecord("XLKR", linkedRefFormId)
            ]
        };

        var scanResult = new EsmRecordScanResult();
        EsmDataExtractor.ExtractRefrRecordsFromParsed(scanResult, [record], bigEndian: false);

        var refr = Assert.Single(scanResult.RefrRecords);
        Assert.Null(refr.LinkedRefKeywordFormId);
        Assert.Equal(linkedRefFormId, refr.LinkedRefFormId);
    }

    [Fact]
    public void ExtractRefrRecordsFromParsed_ReadsRadiusSubrecord()
    {
        const uint refrFormId = 0x00150210;
        const uint baseFormId = 0x00150220;

        var record = new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "REFR",
                DataSize = 0,
                Flags = 0,
                FormId = refrFormId
            },
            Offset = 0x2C0,
            Subrecords =
            [
                MakeFormIdSubrecord("NAME", baseFormId),
                MakeFloatSubrecord("XRDS", 192.5f)
            ]
        };

        var scanResult = new EsmRecordScanResult();
        EsmDataExtractor.ExtractRefrRecordsFromParsed(scanResult, [record], bigEndian: false);

        var refr = Assert.Single(scanResult.RefrRecords);
        Assert.Equal(192.5f, refr.Radius);
    }

    private static ParsedSubrecord MakeFormIdSubrecord(string signature, uint formId)
    {
        Span<byte> data = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, formId);
        return new ParsedSubrecord
        {
            Signature = signature,
            Data = data.ToArray(),
            BigEndian = false
        };
    }

    private static ParsedSubrecord MakeFloatSubrecord(string signature, float value)
    {
        Span<byte> data = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(data, value);
        return new ParsedSubrecord
        {
            Signature = signature,
            Data = data.ToArray(),
            BigEndian = false
        };
    }

    private static ParsedSubrecord MakeLockSubrecord(
        byte level,
        uint keyFormId,
        byte flags,
        uint numTries,
        uint timesUnlocked)
    {
        Span<byte> data = stackalloc byte[20];
        data[0] = level;
        BinaryPrimitives.WriteUInt32LittleEndian(data[4..8], keyFormId);
        data[8] = flags;
        BinaryPrimitives.WriteUInt32LittleEndian(data[12..16], numTries);
        BinaryPrimitives.WriteUInt32LittleEndian(data[16..20], timesUnlocked);
        return new ParsedSubrecord
        {
            Signature = "XLOC",
            Data = data.ToArray(),
            BigEndian = false
        };
    }

    private static ParsedSubrecord MakeEnableParentSubrecord(uint parentFormId, byte flags)
    {
        Span<byte> data = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(data[0..4], parentFormId);
        data[4] = flags;
        return new ParsedSubrecord
        {
            Signature = "XESP",
            Data = data.ToArray(),
            BigEndian = false
        };
    }

    private static ParsedSubrecord MakeEightByteLinkedRefSubrecord(uint keywordFormId, uint linkedRefFormId)
    {
        Span<byte> data = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(data[0..4], keywordFormId);
        BinaryPrimitives.WriteUInt32LittleEndian(data[4..8], linkedRefFormId);
        return new ParsedSubrecord
        {
            Signature = "XLKR",
            Data = data.ToArray(),
            BigEndian = false
        };
    }
}
