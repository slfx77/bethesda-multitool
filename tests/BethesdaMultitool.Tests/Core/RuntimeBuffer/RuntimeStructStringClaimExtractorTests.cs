using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.RuntimeBuffer;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeBuffer;

public sealed class RuntimeStructStringClaimExtractorTests
{
    [Theory]
    [InlineData((byte)0x22, 20, 4, 88)] // MSTT
    [InlineData((byte)0x26, 12, 80, 92)] // FLOR
    public void ExtractClaims_RebasesObjectFieldsAndDeduplicatesPrecapturedFullName(
        byte formType,
        int interiorOffset,
        int fullNameOffset,
        int modelOffset)
    {
        const uint formId = 0x0013579B;
        const long objectVa = 0x8000;
        const long objectPrefixFileOffset = 300;
        const long tesFormFileOffset = 40;
        const uint fullNameVa = 0xA000;
        const long fullNameFileOffset = 500;
        const uint modelVa = 0xB000;
        const long modelFileOffset = 560;
        const string fullName = "Distinct runtime FULL";
        const string modelPath = "meshes/distinct/runtime_model.nif";
        var layout = PdbStructLayouts.Get(formType)!;
        Assert.Equal(interiorOffset, PdbStructLayouts.GetTesFormInteriorOffset(layout));
        var objectData = CreateObjectData(layout, formType, formId);
        WriteBsStringHeader(objectData, fullNameOffset, fullNameVa, fullName.Length);
        WriteBsStringHeader(objectData, modelOffset, modelVa, modelPath.Length);
        var data = new byte[640];
        Array.Copy(objectData, 0, data, objectPrefixFileOffset, interiorOffset);
        Array.Copy(objectData, interiorOffset, data, tesFormFileOffset,
            objectData.Length - interiorOffset);
        WriteAscii(data, fullNameFileOffset, fullName);
        WriteAscii(data, modelFileOffset, modelPath);
        var context = CreateContext(
            data,
            Region(objectVa, objectPrefixFileOffset, interiorOffset),
            Region(objectVa + interiorOffset, tesFormFileOffset, objectData.Length - interiorOffset),
            Region(fullNameVa, fullNameFileOffset, fullName.Length),
            Region(modelVa, modelFileOffset, modelPath.Length));
        var entry = new RuntimeEditorIdEntry
        {
            EditorId = $"Ownership{layout.RecordCode}",
            FormId = formId,
            FormType = formType,
            TesFormOffset = tesFormFileOffset,
            TesFormPointer = objectVa + interiorOffset,
            DisplayName = fullName,
            DisplayNameStringOffset = fullNameFileOffset
        };

        var claims = RuntimeStructStringClaimExtractor.ExtractClaims([entry], context);

        Assert.Equal(2, claims.Count);
        var fullNameClaim = Assert.Single(claims, claim => claim.StringFileOffset == fullNameFileOffset);
        Assert.Equal("cFullName", fullNameClaim.OwnerFieldOrSubrecord);
        Assert.Equal(objectPrefixFileOffset, fullNameClaim.OwnerFileOffset);
        var modelClaim = Assert.Single(claims, claim => claim.StringFileOffset == modelFileOffset);
        Assert.Equal("TESModel.cModel", modelClaim.OwnerFieldOrSubrecord);
        Assert.Equal(objectPrefixFileOffset, modelClaim.OwnerFileOffset);
    }

    [Fact]
    public void ExtractClaims_DeduplicatesPrecapturedDialoguePromptAgainstPdbField()
    {
        const byte formType = 0x46;
        const uint formId = 0x002468AC;
        const long objectVa = 0xC000;
        const long objectFileOffset = 24;
        const uint promptVa = 0xD000;
        const long promptFileOffset = 160;
        const string prompt = "Precaptured prompt";
        var layout = PdbStructLayouts.Get(formType)!;
        var objectData = CreateObjectData(layout, formType, formId);
        var promptOffset = layout.Fields.Single(field =>
            field is { Owner: "TESTopicInfo", Name: "cPrompt" }).Offset;
        WriteBsStringHeader(objectData, promptOffset, promptVa, prompt.Length);
        var data = new byte[208];
        Array.Copy(objectData, 0, data, objectFileOffset, objectData.Length);
        WriteAscii(data, promptFileOffset, prompt);
        var context = CreateContext(
            data,
            Region(objectVa, objectFileOffset, objectData.Length),
            Region(promptVa, promptFileOffset, prompt.Length));
        var entry = new RuntimeEditorIdEntry
        {
            EditorId = "OwnershipInfoTopic",
            FormId = formId,
            FormType = formType,
            TesFormOffset = objectFileOffset,
            TesFormPointer = objectVa,
            DialogueLine = prompt,
            DialogueLineStringOffset = promptFileOffset
        };

        var claims = RuntimeStructStringClaimExtractor.ExtractClaims([entry], context);

        var claim = Assert.Single(claims);
        Assert.Equal(promptFileOffset, claim.StringFileOffset);
        Assert.Equal("cPrompt", claim.OwnerFieldOrSubrecord);
    }

    [Fact]
    public void ExtractClaims_PrecapturedInteriorFullNameRebasesOwnerWhenObjectIsIncomplete()
    {
        const byte formType = 0x22; // MSTT: TESForm begins at complete-object +20.
        const uint formId = 0x0013579B;
        const long objectVa = 0xE000;
        const long objectFileOffset = 300;
        const long tesFormFileOffset = 40;
        const long fullNameFileOffset = 500;
        var data = new byte[560];
        var context = CreateContext(
            data,
            Region(objectVa, objectFileOffset, 4),
            Region(objectVa + 20, tesFormFileOffset, 16),
            Region(0xF000, fullNameFileOffset, 12));
        var entry = new RuntimeEditorIdEntry
        {
            EditorId = "IncompleteOwnershipMSTT",
            FormId = formId,
            FormType = formType,
            TesFormOffset = tesFormFileOffset,
            TesFormPointer = objectVa + 20,
            DisplayName = "Known name",
            DisplayNameStringOffset = fullNameFileOffset
        };

        var claim = Assert.Single(RuntimeStructStringClaimExtractor.ExtractClaims([entry], context));

        Assert.Equal(fullNameFileOffset, claim.StringFileOffset);
        Assert.Equal(objectFileOffset, claim.OwnerFileOffset);
    }

    [Fact]
    public void ReadBSStringTInfo_FileOffsetHeaderStitchesVaContiguousRegions()
    {
        const long headerVa = 0x10000;
        const uint stringVa = 0x11000;
        const long stringFileOffset = 160;
        const string text = "Split ownership header";
        var header = CreateBsStringHeader(stringVa, text.Length);
        var data = new byte[224];
        Array.Copy(header, 0, data, 10, 3);
        Array.Copy(header, 3, data, 96, 5);
        WriteAscii(data, stringFileOffset, text);
        var context = CreateContext(
            data,
            Region(headerVa, 10, 3),
            Region(headerVa + 3, 96, 5),
            Region(stringVa, stringFileOffset, text.Length));

        var result = context.ReadBSStringTInfo(10, 0);

        Assert.NotNull(result);
        Assert.Equal(stringFileOffset, result.Value.StringFileOffset);
        Assert.Equal(stringVa, result.Value.StringVa);
    }

    [Fact]
    public void ReadBSStringTInfo_FileOffsetHeaderFailsClosedAcrossVaGapWithFlatBait()
    {
        const long headerVa = 0x12000;
        const uint stringVa = 0x13000;
        const long stringFileOffset = 80;
        const string bait = "Flat header bait";
        var data = new byte[128];
        WriteBsStringHeader(data, 10, stringVa, bait.Length);
        WriteAscii(data, stringFileOffset, bait);
        var context = CreateContext(
            data,
            Region(headerVa, 10, 3),
            Region(headerVa + 4, 13, 5),
            Region(stringVa, stringFileOffset, bait.Length));

        var result = context.ReadBSStringTInfo(10, 0);

        Assert.Null(result);
    }

    [Fact]
    public void ReadBSStringTInfo_FileOffsetHeaderUnmappedInMappedDumpFailsClosedWithFlatBait()
    {
        const uint stringVa = 0x13500;
        const long stringFileOffset = 80;
        const string bait = "Unmapped flat header bait";
        var data = new byte[128];
        WriteBsStringHeader(data, 10, stringVa, bait.Length);
        WriteAscii(data, stringFileOffset, bait);
        var context = CreateContext(
            data,
            Region(stringVa, stringFileOffset, bait.Length));

        var result = context.ReadBSStringTInfo(10, 0);

        Assert.Null(result);
    }

    [Fact]
    public void ReadBSStringTInfo_BufferHeaderStitchesPayloadAcrossFileDiscontinuity()
    {
        const uint stringVa = 0x14000;
        const string text = "Split ownership payload";
        const int split = 6;
        var textBytes = Encoding.ASCII.GetBytes(text);
        var data = new byte[224];
        Array.Copy(textBytes, 0, data, 40, split);
        Array.Copy(textBytes, split, data, 160, textBytes.Length - split);
        var context = CreateContext(
            data,
            Region(stringVa, 40, split),
            Region(stringVa + split, 160, textBytes.Length - split));
        var header = CreateBsStringHeader(stringVa, textBytes.Length);

        var result = context.ReadBSStringTInfo(header, 0);

        Assert.NotNull(result);
        Assert.Equal(40, result.Value.StringFileOffset);
        Assert.Equal(stringVa, result.Value.StringVa);
    }

    [Fact]
    public void ReadBSStringTInfo_BufferHeaderFailsClosedAcrossPayloadVaGapWithFlatBait()
    {
        const uint stringVa = 0x15000;
        const string bait = "Flat payload bait";
        const int split = 5;
        var textBytes = Encoding.ASCII.GetBytes(bait);
        var data = new byte[128];
        Array.Copy(textBytes, 0, data, 40, textBytes.Length);
        var context = CreateContext(
            data,
            Region(stringVa, 40, split),
            Region(stringVa + split + 1, 40 + split, textBytes.Length - split));
        var header = CreateBsStringHeader(stringVa, textBytes.Length);

        var result = context.ReadBSStringTInfo(header, 0);

        Assert.Null(result);
    }

    private static byte[] CreateObjectData(PdbTypeLayout layout, byte formType, uint formId)
    {
        var data = new byte[layout.StructSize];
        var formTypeOffset = layout.Fields.Single(field =>
            field is { Owner: "TESForm", Name: "cFormType" }).Offset;
        var formIdOffset = layout.Fields.Single(field =>
            field is { Owner: "TESForm", Name: "iFormID" }).Offset;
        data[formTypeOffset] = formType;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(formIdOffset, 4), formId);
        return data;
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
        WriteBsStringHeader(header, 0, stringVa, length);
        return header;
    }

    private static void WriteBsStringHeader(byte[] data, int offset, uint stringVa, int length)
    {
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), stringVa);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset + 4, 2), checked((ushort)length));
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset + 6, 2), checked((ushort)length));
    }

    private static void WriteAscii(byte[] data, long fileOffset, string text)
    {
        Encoding.ASCII.GetBytes(text).CopyTo(data, checked((int)fileOffset));
    }
}