using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.Utils;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Records;

public sealed class EsmEditorIdExtractorTests
{
    [Theory]
    [InlineData((byte)0x22, 20)] // MSTT: BGSMovableStatic::TESForm
    [InlineData((byte)0x26, 12)] // FLOR: TESFlora::TESForm
    [InlineData((byte)0x41, 0)]  // WRLD: TESForm-first control
    public void TesFormInteriorOffset_ComesFromObjectRelativePdbLayout(byte formType, int expected)
    {
        var layout = PdbStructLayouts.Get(formType);

        Assert.NotNull(layout);
        Assert.Equal(expected, PdbStructLayouts.GetTesFormInteriorOffset(layout!));
    }

    [Fact]
    public void ReadDisplayName_MsttRebasesTesFormPointerToObjectFullName()
    {
        const long objectVa = 0x40001000;
        const long objectFileOffset = 16;
        const uint fullNameVa = 0x40002000;
        const long fullNameFileOffset = 160;
        const uint decoyVa = 0x40003000;
        const long decoyFileOffset = 208;
        const string expected = "Movable Static Name";
        const string decoy = "TESForm-relative decoy";
        var data = new byte[256];
        WriteBsStringHeader(data, checked((int)objectFileOffset + 4), fullNameVa, expected.Length);
        WriteBsStringHeader(data, checked((int)objectFileOffset + 24), decoyVa, decoy.Length);
        WriteAscii(data, fullNameFileOffset, expected);
        WriteAscii(data, decoyFileOffset, decoy);
        var context = CreateContext(
            data,
            Region(objectVa, objectFileOffset, 64),
            Region(Xbox360MemoryUtils.VaToLong(fullNameVa), fullNameFileOffset, expected.Length),
            Region(Xbox360MemoryUtils.VaToLong(decoyVa), decoyFileOffset, decoy.Length));

        var result = EsmEditorIdExtractor.ReadDisplayName(context, 0x22, objectVa + 20);

        Assert.NotNull(result);
        Assert.Equal(expected, result.Value.Text);
        Assert.Equal(fullNameFileOffset, result.Value.StringFileOffset);
    }

    [Fact]
    public void ReadDisplayName_FlorDoesNotPromoteModelPathAsFullName()
    {
        const long objectVa = 0x41001000;
        const long objectFileOffset = 16;
        const uint fullNameVa = 0x41002000;
        const long fullNameFileOffset = 176;
        const uint modelVa = 0x41003000;
        const long modelFileOffset = 224;
        const string expected = "Broc Flower";
        const string modelPath = "meshes/plants/brocflower.nif";
        var data = new byte[288];
        WriteBsStringHeader(data, checked((int)objectFileOffset + 80), fullNameVa, expected.Length);
        WriteBsStringHeader(data, checked((int)objectFileOffset + 92), modelVa, modelPath.Length);
        WriteAscii(data, fullNameFileOffset, expected);
        WriteAscii(data, modelFileOffset, modelPath);
        var context = CreateContext(
            data,
            Region(objectVa, objectFileOffset, 112),
            Region(Xbox360MemoryUtils.VaToLong(fullNameVa), fullNameFileOffset, expected.Length),
            Region(Xbox360MemoryUtils.VaToLong(modelVa), modelFileOffset, modelPath.Length));

        var result = EsmEditorIdExtractor.ReadDisplayName(context, 0x26, objectVa + 12);

        Assert.NotNull(result);
        Assert.Equal(expected, result.Value.Text);
        Assert.NotEqual(modelPath, result.Value.Text);
        Assert.Equal(fullNameFileOffset, result.Value.StringFileOffset);
    }

    [Fact]
    public void WithDisplayName_PopulatesTextAndPayloadFileOffset()
    {
        var entry = new RuntimeEditorIdEntry
        {
            EditorId = "OffsetBearingMstt",
            FormId = 0x00112233,
            FormType = 0x22
        };
        var read = new EsmEditorIdStringReader.ReadResult(
            "Offset-bearing name", 0x3456);

        var result = EsmEditorIdExtractor.WithDisplayName(entry, read);

        Assert.Equal(read.Text, result.DisplayName);
        Assert.Equal(read.StringFileOffset, result.DisplayNameStringOffset);
    }

    [Fact]
    public void ReadDisplayName_TesFormFirstSupportsSignExtendedModuleVa()
    {
        var objectVa = Xbox360MemoryUtils.VaToLong(0x82001000);
        const long objectFileOffset = 16;
        const uint fullNameVa = 0x82002000;
        const long fullNameFileOffset = 128;
        const string expected = "Service Rifle";
        var data = new byte[176];
        WriteBsStringHeader(data, checked((int)objectFileOffset + 68), fullNameVa, expected.Length);
        WriteAscii(data, fullNameFileOffset, expected);
        var context = CreateContext(
            data,
            Region(objectVa, objectFileOffset, 80),
            Region(Xbox360MemoryUtils.VaToLong(fullNameVa), fullNameFileOffset, expected.Length));

        var result = EsmEditorIdExtractor.ReadDisplayName(context, 0x28, objectVa);

        Assert.NotNull(result);
        Assert.Equal(expected, result.Value.Text);
        Assert.Equal(fullNameFileOffset, result.Value.StringFileOffset);
    }

    [Fact]
    public void ReadBsStringTAtVa_StitchesHeaderAcrossNonContiguousFileOffsets()
    {
        const long objectVa = 0x42001000;
        const int fieldOffset = 4;
        const uint stringVa = 0x42002000;
        const long stringFileOffset = 160;
        const string expected = "Split header";
        var header = CreateBsStringHeader(stringVa, expected.Length);
        var data = new byte[208];
        Array.Copy(header, 0, data, 14, 3);
        Array.Copy(header, 3, data, 96, 5);
        WriteAscii(data, stringFileOffset, expected);
        var context = CreateContext(
            data,
            Region(objectVa, 10, fieldOffset + 3),
            Region(objectVa + fieldOffset + 3, 96, 5),
            Region(Xbox360MemoryUtils.VaToLong(stringVa), stringFileOffset, expected.Length));

        var result = EsmEditorIdStringReader.ReadBsStringTAtVa(context, objectVa, fieldOffset);

        Assert.NotNull(result);
        Assert.Equal(expected, result.Value.Text);
        Assert.Equal(stringFileOffset, result.Value.StringFileOffset);
    }

    [Fact]
    public void ReadBsStringTAtVa_StitchesPayloadAcrossNonContiguousFileOffsets()
    {
        const long objectVa = 0x43001000;
        const uint stringVa = 0x43002000;
        const string expected = "Split payload text";
        const int split = 5;
        var textBytes = Encoding.ASCII.GetBytes(expected);
        var data = new byte[208];
        WriteBsStringHeader(data, 10, stringVa, textBytes.Length);
        Array.Copy(textBytes, 0, data, 80, split);
        Array.Copy(textBytes, split, data, 144, textBytes.Length - split);
        var context = CreateContext(
            data,
            Region(objectVa, 10, 8),
            Region(Xbox360MemoryUtils.VaToLong(stringVa), 80, split),
            Region(Xbox360MemoryUtils.VaToLong(stringVa) + split, 144, textBytes.Length - split));

        var result = EsmEditorIdStringReader.ReadBsStringTAtVa(context, objectVa, 0);

        Assert.NotNull(result);
        Assert.Equal(expected, result.Value.Text);
        Assert.Equal(80, result.Value.StringFileOffset);
    }

    [Fact]
    public void ReadBsStringTAtVa_FailsClosedAcrossHeaderVaGapWithFlatBait()
    {
        const long objectVa = 0x44001000;
        const uint stringVa = 0x44002000;
        const long stringFileOffset = 80;
        const string bait = "Flat header bait";
        var data = new byte[128];
        WriteBsStringHeader(data, 10, stringVa, bait.Length);
        WriteAscii(data, stringFileOffset, bait);
        var context = CreateContext(
            data,
            Region(objectVa, 10, 3),
            Region(objectVa + 4, 13, 5),
            Region(Xbox360MemoryUtils.VaToLong(stringVa), stringFileOffset, bait.Length));

        var result = EsmEditorIdStringReader.ReadBsStringTAtVa(context, objectVa, 0);

        Assert.Null(result);
    }

    [Fact]
    public void ReadBsStringTAtVa_FailsClosedAcrossPayloadVaGapWithFlatBait()
    {
        const long objectVa = 0x45001000;
        const uint stringVa = 0x45002000;
        const string bait = "Flat payload bait";
        const int split = 5;
        var textBytes = Encoding.ASCII.GetBytes(bait);
        var data = new byte[128];
        WriteBsStringHeader(data, 10, stringVa, textBytes.Length);
        Array.Copy(textBytes, 0, data, 80, textBytes.Length);
        var context = CreateContext(
            data,
            Region(objectVa, 10, 8),
            Region(Xbox360MemoryUtils.VaToLong(stringVa), 80, split),
            Region(Xbox360MemoryUtils.VaToLong(stringVa) + split + 1, 80 + split, textBytes.Length - split));

        var result = EsmEditorIdStringReader.ReadBsStringTAtVa(context, objectVa, 0);

        Assert.Null(result);
    }

    [Fact]
    public void ReadFromTesFormEntry_MapsFileOffsetWhenRetainedPointerIsUnavailable()
    {
        const long tesFormVa = 0x46001000;
        const long tesFormFileOffset = 24;
        const uint stringVa = 0x46002000;
        const long stringFileOffset = 96;
        const string expected = "Mapped fallback";
        var data = new byte[144];
        WriteBsStringHeader(data, checked((int)tesFormFileOffset + 44), stringVa, expected.Length);
        WriteAscii(data, stringFileOffset, expected);
        var context = CreateContext(
            data,
            Region(tesFormVa, tesFormFileOffset, 64),
            Region(Xbox360MemoryUtils.VaToLong(stringVa), stringFileOffset, expected.Length));
        var entry = new RuntimeEditorIdEntry
        {
            EditorId = "FallbackInfoTopic",
            FormId = 0x00123456,
            FormType = 0x46,
            TesFormOffset = tesFormFileOffset
        };

        var result = EsmEditorIdStringReader.ReadFromTesFormEntry(context, entry, 44);

        Assert.NotNull(result);
        Assert.Equal(expected, result.Value.Text);
        Assert.Equal(stringFileOffset, result.Value.StringFileOffset);
    }

    [Fact]
    public void ExtractDialogueLines_PopulatesDialogueTextAndPayloadFileOffset()
    {
        const long tesFormVa = 0x47001000;
        const long tesFormFileOffset = 24;
        const uint stringVa = 0x47002000;
        const long stringFileOffset = 112;
        const string expected = "Five-topic prompt";
        var data = new byte[160];
        WriteBsStringHeader(data, checked((int)tesFormFileOffset + 44), stringVa, expected.Length);
        WriteAscii(data, stringFileOffset, expected);
        var context = CreateContext(
            data,
            Region(tesFormVa, tesFormFileOffset, 64),
            Region(Xbox360MemoryUtils.VaToLong(stringVa), stringFileOffset, expected.Length));
        var scanResult = new EsmRecordScanResult();
        for (var i = 0; i < 5; i++)
        {
            scanResult.RuntimeEditorIds.Add(new RuntimeEditorIdEntry
            {
                EditorId = $"SyntheticTopic{i}",
                FormId = checked((uint)(0x00123000 + i)),
                FormType = 0x46,
                TesFormOffset = tesFormFileOffset,
                TesFormPointer = tesFormVa
            });
        }

        EditorIdLookupTables.ExtractDialogueLinesForInfoEntries(
            context, scanResult, 0, Logger.Instance);

        Assert.All(scanResult.RuntimeEditorIds, entry =>
        {
            Assert.Equal(expected, entry.DialogueLine);
            Assert.Equal(stringFileOffset, entry.DialogueLineStringOffset);
        });
    }

    private static RuntimeMemoryContext CreateContext(
        byte[] data,
        params MinidumpMemoryRegion[] regions)
    {
        return new RuntimeMemoryContext(
            new ByteArrayMemoryAccessor(data),
            data.Length,
            new MinidumpInfo
            {
                IsValid = true,
                MemoryRegions = [.. regions]
            });
    }

    private static MinidumpMemoryRegion Region(long va, long fileOffset, int size)
    {
        return new MinidumpMemoryRegion
        {
            VirtualAddress = va,
            FileOffset = fileOffset,
            Size = size
        };
    }

    private static byte[] CreateBsStringHeader(uint stringVa, int length)
    {
        var header = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, stringVa);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), checked((ushort)length));
        return header;
    }

    private static void WriteBsStringHeader(byte[] data, int fileOffset, uint stringVa, int length)
    {
        CreateBsStringHeader(stringVa, length).CopyTo(data, fileOffset);
    }

    private static void WriteAscii(byte[] data, long fileOffset, string text)
    {
        Encoding.ASCII.GetBytes(text).CopyTo(data, checked((int)fileOffset));
    }
}
