using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Coverage;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm;

public sealed class EsmPlacedReferenceSubrecordTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExtractRefrRecordsFromParsed_ReadsExternalEmittanceFormId_InBothEndiannesses(bool bigEndian)
    {
        const uint refrFormId = 0x00150410;
        const uint baseFormId = 0x00150420;
        const uint emittanceFormId = 0x00150430;
        var record = new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "REFR",
                FormId = refrFormId
            },
            Offset = 0x340,
            Subrecords =
            [
                MakeFormIdSubrecord("NAME", baseFormId, bigEndian),
                MakeFormIdSubrecord("XEMI", emittanceFormId, bigEndian)
            ]
        };

        var scanResult = new EsmRecordScanResult();
        EsmDataExtractor.ExtractRefrRecordsFromParsed(scanResult, [record], bigEndian);

        var refr = Assert.Single(scanResult.RefrRecords);
        Assert.Equal(baseFormId, refr.BaseFormId);
        Assert.Equal(emittanceFormId, refr.EmittanceFormId);
        Assert.Equal(bigEndian, refr.Header.IsBigEndian);
    }

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
        EsmDataExtractor.ExtractRefrRecordsFromParsed(scanResult, [record], false);

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
        EsmDataExtractor.ExtractRefrRecordsFromParsed(scanResult, [record], false);

        var refr = Assert.Single(scanResult.RefrRecords);
        Assert.Null(refr.LinkedRefKeywordFormId);
        Assert.Equal(linkedRefFormId, refr.LinkedRefFormId);
    }

    [Theory]
    [InlineData(false, 192.5f)]
    [InlineData(false, -500f)]
    [InlineData(true, -147.9012146f)]
    public void ExtractRefrRecordsFromParsed_PreservesSignedRadiusSubrecord(
        bool bigEndian,
        float authoredRadius)
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
                MakeFormIdSubrecord("NAME", baseFormId, bigEndian),
                MakeFloatSubrecord("XRDS", authoredRadius, bigEndian)
            ]
        };

        var scanResult = new EsmRecordScanResult();
        EsmDataExtractor.ExtractRefrRecordsFromParsed(scanResult, [record], bigEndian);

        var refr = Assert.Single(scanResult.RefrRecords);
        Assert.Equal(authoredRadius, refr.Radius);
    }

    [Fact]
    public void ExtractRefrRecordsFromParsed_ReadsReferenceEditorId()
    {
        const uint refrFormId = 0x00150310;
        const uint baseFormId = 0x00150320;

        var record = new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "REFR",
                DataSize = 0,
                Flags = 0,
                FormId = refrFormId
            },
            Offset = 0x300,
            Subrecords =
            [
                MakeStringSubrecord("EDID", "DoorMarkerRef"),
                MakeFormIdSubrecord("NAME", baseFormId)
            ]
        };

        var scanResult = new EsmRecordScanResult();
        EsmDataExtractor.ExtractRefrRecordsFromParsed(scanResult, [record], false);

        var refr = Assert.Single(scanResult.RefrRecords);
        Assert.Equal("DoorMarkerRef", refr.EditorId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExtractRefrRecordsFromParsed_ReadsBendableSplineParameters_InBothEndiannesses(
        bool bigEndian)
    {
        const uint refrFormId = 0x00150510;
        const uint baseFormId = 0x00106F19;
        var record = new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "REFR",
                FormId = refrFormId
            },
            Offset = 0x380,
            Subrecords =
            [
                MakeFormIdSubrecord("NAME", baseFormId, bigEndian),
                new ParsedSubrecord
                {
                    Signature = "XBSD",
                    Data = BuildBendableSplinePlacement(bigEndian, includeWindAndTrailingData: true),
                    BigEndian = bigEndian
                }
            ]
        };

        var scanResult = new EsmRecordScanResult();
        EsmDataExtractor.ExtractRefrRecordsFromParsed(scanResult, [record], bigEndian);

        var placement = Assert.Single(scanResult.RefrRecords).BendableSpline;
        Assert.NotNull(placement);
        Assert.Equal(24.5f, placement!.Slack);
        Assert.Equal(1.5f, placement.Thickness);
        Assert.Equal(new System.Numerics.Vector3(128f, 16f, 32f), placement.HalfExtents);
        Assert.Equal((byte)2, placement.WindDetachedEndRaw);
        Assert.True(placement.WindDetachedEnd is true);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, placement.TrailingData.ToArray());
    }

    [Fact]
    public void ExtractRefrRecordsFromParsed_AcceptsRequiredXbsdPrefixWithoutOptionalWindByte()
    {
        const uint refrFormId = 0x00150520;
        var record = new ParsedMainRecord
        {
            Header = new MainRecordHeader { Signature = "REFR", FormId = refrFormId },
            Subrecords =
            [
                new ParsedSubrecord
                {
                    Signature = "XBSD",
                    Data = BuildBendableSplinePlacement(false, includeWindAndTrailingData: false)
                }
            ]
        };

        var scanResult = new EsmRecordScanResult();
        EsmDataExtractor.ExtractRefrRecordsFromParsed(scanResult, [record], false);

        var placement = Assert.Single(scanResult.RefrRecords).BendableSpline;
        Assert.NotNull(placement);
        Assert.Null(placement!.WindDetachedEndRaw);
        Assert.Null(placement.WindDetachedEnd);
        Assert.Empty(placement.TrailingData);
    }

    [Fact]
    public void ExtractRefrRecordsFromParsed_IgnoresTruncatedXbsd()
    {
        var record = new ParsedMainRecord
        {
            Header = new MainRecordHeader { Signature = "REFR", FormId = 0x00150530 },
            Subrecords = [new ParsedSubrecord { Signature = "XBSD", Data = new byte[19] }]
        };

        var scanResult = new EsmRecordScanResult();
        EsmDataExtractor.ExtractRefrRecordsFromParsed(scanResult, [record], false);

        Assert.Null(Assert.Single(scanResult.RefrRecords).BendableSpline);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorldExtractor_ReadsBendableSplineParameters_InBothEndiannesses(bool bigEndian)
    {
        const uint refrFormId = 0x00150540;
        const uint baseFormId = 0x00106F19;
        var recordBytes = EsmTestRecordBuilder.BuildRecordBytes(
            refrFormId,
            "REFR",
            bigEndian,
            ("NAME", BuildUInt32(baseFormId, bigEndian)),
            ("XBSD", BuildBendableSplinePlacement(bigEndian, includeWindAndTrailingData: true)));
        var scanResult = EsmTestRecordBuilder.MakeScanResult(
        [
            new DetectedMainRecord(
                "REFR", (uint)(recordBytes.Length - 24), 0, refrFormId, 0, bigEndian)
        ]);
        using var mmf = MemoryMappedFile.CreateNew(null, recordBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, recordBytes.Length);
        accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);

        EsmWorldExtractor.ExtractRefrRecords(accessor, recordBytes.Length, scanResult);

        var refr = Assert.Single(scanResult.RefrRecords);
        Assert.Equal(baseFormId, refr.BaseFormId);
        Assert.NotNull(refr.BendableSpline);
        Assert.Equal(24.5f, refr.BendableSpline!.Slack);
        Assert.Equal(1.5f, refr.BendableSpline.Thickness);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, refr.BendableSpline.TrailingData.ToArray());
    }

    [Fact]
    public void DescriptorScanner_ReadsBendableSplineParametersFromCompletePlugin()
    {
        const uint refrFormId = 0x00150550;
        const uint baseFormId = 0x00106F19;
        var refrBytes = EsmTestRecordBuilder.BuildRecordBytes(
            refrFormId,
            "REFR",
            false,
            ("NAME", BuildUInt32(baseFormId, false)),
            ("XBSD", BuildBendableSplinePlacement(false, includeWindAndTrailingData: true)));
        var fileData = new EsmTestFileBuilder()
            .AddTopLevelGrup("REFR", refrBytes)
            .Build();

        var scanResult = EsmDescriptorScanner.Scan(fileData).ScanResult;

        var refr = Assert.Single(scanResult.RefrRecords);
        Assert.Equal(baseFormId, refr.BaseFormId);
        Assert.NotNull(refr.BendableSpline);
        Assert.Equal(24.5f, refr.BendableSpline!.Slack);
        Assert.Equal(1.5f, refr.BendableSpline.Thickness);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, refr.BendableSpline.TrailingData.ToArray());
    }

    private static ParsedSubrecord MakeFormIdSubrecord(string signature, uint formId, bool bigEndian = false)
    {
        return new ParsedSubrecord
        {
            Signature = signature,
            Data = BuildUInt32(formId, bigEndian),
            BigEndian = bigEndian
        };
    }

    private static byte[] BuildUInt32(uint value, bool bigEndian)
    {
        var data = new byte[4];
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(data, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data, value);
        }

        return data;
    }

    private static byte[] BuildBendableSplinePlacement(
        bool bigEndian,
        bool includeWindAndTrailingData)
    {
        var data = new byte[includeWindAndTrailingData ? 24 : 20];
        WriteFloat(data, 0, 24.5f, bigEndian);
        WriteFloat(data, 4, 1.5f, bigEndian);
        WriteFloat(data, 8, 128f, bigEndian);
        WriteFloat(data, 12, 16f, bigEndian);
        WriteFloat(data, 16, 32f, bigEndian);
        if (includeWindAndTrailingData)
        {
            data[20] = 2;
            data[21] = 0xAA;
            data[22] = 0xBB;
            data[23] = 0xCC;
        }

        return data;
    }

    private static void WriteFloat(byte[] data, int offset, float value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(offset, 4), value);
        }
        else
        {
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset, 4), value);
        }
    }

    private static ParsedSubrecord MakeFloatSubrecord(
        string signature,
        float value,
        bool bigEndian = false)
    {
        Span<byte> data = stackalloc byte[4];
        if (bigEndian)
        {
            BinaryPrimitives.WriteSingleBigEndian(data, value);
        }
        else
        {
            BinaryPrimitives.WriteSingleLittleEndian(data, value);
        }

        return new ParsedSubrecord
        {
            Signature = signature,
            Data = data.ToArray(),
            BigEndian = bigEndian
        };
    }

    private static ParsedSubrecord MakeStringSubrecord(string signature, string value)
    {
        var data = Encoding.UTF8.GetBytes(value + '\0');
        return new ParsedSubrecord
        {
            Signature = signature,
            Data = data,
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
        BinaryPrimitives.WriteUInt32LittleEndian(data[..4], parentFormId);
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
        BinaryPrimitives.WriteUInt32LittleEndian(data[..4], keywordFormId);
        BinaryPrimitives.WriteUInt32LittleEndian(data[4..8], linkedRefFormId);
        return new ParsedSubrecord
        {
            Signature = "XLKR",
            Data = data.ToArray(),
            BigEndian = false
        };
    }

    [Fact]
    public void ExtractRefrRecordsFromParsed_Reads32ByteXtelPosRotAndFlags()
    {
        // Phase 4.2c: regression coverage for the full 32-byte XTEL layout
        // (DoorFormID@0 + PosX/Y/Z@4-15 + RotX/Y/Z@16-27 + Flags@28).
        const uint refrFormId = 0x00150210;
        const uint baseFormId = 0x00150220;
        const uint destinationDoorFormId = 0x00150280;

        var record = new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "REFR",
                DataSize = 0,
                Flags = 0,
                FormId = refrFormId
            },
            Offset = 0x300,
            Subrecords =
            [
                MakeFormIdSubrecord("NAME", baseFormId),
                MakeFullXtelSubrecord(
                    destinationDoorFormId,
                    100.5f, 200.25f, 50.125f,
                    0.1f, 0.2f, 0.3f,
                    0x01)
            ]
        };

        var scanResult = new EsmRecordScanResult();
        EsmDataExtractor.ExtractRefrRecordsFromParsed(scanResult, [record], false);

        var refr = Assert.Single(scanResult.RefrRecords);
        Assert.Equal(destinationDoorFormId, refr.DestinationDoorFormId);
        Assert.NotNull(refr.TeleportPosRot);
        Assert.Equal(100.5f, refr.TeleportPosRot!.X);
        Assert.Equal(200.25f, refr.TeleportPosRot.Y);
        Assert.Equal(50.125f, refr.TeleportPosRot.Z);
        Assert.Equal(0.1f, refr.TeleportPosRot.RotX);
        Assert.Equal(0.2f, refr.TeleportPosRot.RotY);
        Assert.Equal(0.3f, refr.TeleportPosRot.RotZ);
        Assert.Equal((byte)0x01, refr.TeleportFlags);
    }

    [Fact]
    public void ExtractRefrRecordsFromParsed_Reads4ByteXtelLeavesPosRotNull()
    {
        // Legacy 4-byte XTEL: only the door FormID, no PosRot or Flags.
        // Phase 4.2c gates the PosRot read on `DataLength >= 28` so this stays null.
        const uint refrFormId = 0x00150310;
        const uint baseFormId = 0x00150320;
        const uint destinationDoorFormId = 0x00150380;

        var record = new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "REFR",
                DataSize = 0,
                Flags = 0,
                FormId = refrFormId
            },
            Offset = 0x400,
            Subrecords =
            [
                MakeFormIdSubrecord("NAME", baseFormId),
                MakeFormIdSubrecord("XTEL", destinationDoorFormId) // 4 bytes only
            ]
        };

        var scanResult = new EsmRecordScanResult();
        EsmDataExtractor.ExtractRefrRecordsFromParsed(scanResult, [record], false);

        var refr = Assert.Single(scanResult.RefrRecords);
        Assert.Equal(destinationDoorFormId, refr.DestinationDoorFormId);
        Assert.Null(refr.TeleportPosRot);
        Assert.Null(refr.TeleportFlags);
    }

    private static ParsedSubrecord MakeFullXtelSubrecord(
        uint destinationDoorFormId,
        float posX, float posY, float posZ,
        float rotX, float rotY, float rotZ,
        byte flags)
    {
        Span<byte> data = stackalloc byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(data[..4], destinationDoorFormId);
        BinaryPrimitives.WriteSingleLittleEndian(data[4..8], posX);
        BinaryPrimitives.WriteSingleLittleEndian(data[8..12], posY);
        BinaryPrimitives.WriteSingleLittleEndian(data[12..16], posZ);
        BinaryPrimitives.WriteSingleLittleEndian(data[16..20], rotX);
        BinaryPrimitives.WriteSingleLittleEndian(data[20..24], rotY);
        BinaryPrimitives.WriteSingleLittleEndian(data[24..28], rotZ);
        data[28] = flags;
        // bytes 29-31 are padding (zero)
        return new ParsedSubrecord
        {
            Signature = "XTEL",
            Data = data.ToArray(),
            BigEndian = false
        };
    }
}
