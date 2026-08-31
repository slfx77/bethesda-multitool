using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Tests.Helpers;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.BinaryTestWriter;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime;

/// <summary>
///     End-to-end coverage for specialized-reader struct reads that cross VA-adjacent minidump
///     regions stored at unrelated file offsets. A flat read from the first region sees only the
///     prefix plus zero-filled bait; the asserted fields live in the second physical fragment.
/// </summary>
public sealed class RuntimeSpecializedReaderVaRunTests
{
    private const uint ObjectVa = 0x40001000;

    [Fact]
    public void ReadRuntimeAmmo_StitchesScalarAndModelHeaderFromSecondFileFragment()
    {
        const uint formId = 0x00123456;
        const int expectedValue = 137;
        const string expectedModelPath = "meshes\\split\\ammo_round.nif";
        const uint modelStringVa = 0x40003000;
        const int firstFileOffset = 32;
        const int secondFileOffset = 400;
        const int stringFileOffset = 800;
        const int splitOffset = 64;

        // The synthetic contexts used by the existing item-reader tests select the +16 runtime
        // layout. Value is at +140 and the model BSStringT header at +80, both after this split.
        var layouts = new RuntimeItemLayouts(16);
        var logical = SyntheticStructFactory.BuildAmmo(
            formId,
            ammoDataOffset: 184,
            value: expectedValue,
            bufferSize: layouts.AmmoStructSize);
        WriteBsStringHeader(logical, layouts.WeapModelPathOffset, modelStringVa, expectedModelPath.Length);

        var file = new byte[900];
        CopySplit(logical, splitOffset, file, firstFileOffset, secondFileOffset);
        Encoding.ASCII.GetBytes(expectedModelPath).CopyTo(file, stringFileOffset);
        var context = CreateContext(
            file,
            Region(ObjectVa, firstFileOffset, splitOffset),
            Region(ObjectVa + splitOffset, secondFileOffset, logical.Length - splitOffset),
            Region(modelStringVa, stringFileOffset, expectedModelPath.Length));
        var entry = MakeEntry("SplitAmmo", formId, 0x29, firstFileOffset, ObjectVa);

        var result = new RuntimeItemReader(context).ReadRuntimeAmmo(entry);

        Assert.NotNull(result);
        Assert.Equal((uint)expectedValue, result.Value);
        Assert.Equal(expectedModelPath, result.ModelPath);
        Assert.Equal(firstFileOffset, result.Offset);
    }

    [Fact]
    public void ReadRuntimeDialogueInfoFromVA_StitchesFieldsFromSecondFileFragment()
    {
        const uint formId = 0x00234567;
        const int firstFileOffset = 24;
        const int secondFileOffset = 320;
        const int splitOffset = 44;
        const ushort expectedInfoIndex = 0x1234;
        const uint expectedFileOffset = 0x0010ABCD;
        var logical = new byte[96];
        WriteFormHeader(logical, 0x46, formId);
        WriteUInt16BE(logical, 48, expectedInfoIndex);
        logical[50] = 1; // bSaidOnce
        logical[51] = 5; // TOPIC_INFO_DATA.type
        logical[52] = 2; // nextSpeaker
        logical[53] = 0xA5;
        logical[54] = 0x02;
        WriteUInt32BE(logical, 84, 4); // difficulty
        WriteUInt32BE(logical, 92, expectedFileOffset);

        var file = new byte[500];
        CopySplit(logical, splitOffset, file, firstFileOffset, secondFileOffset);
        var context = CreateContext(
            file,
            Region(ObjectVa, firstFileOffset, splitOffset),
            Region(ObjectVa + splitOffset, secondFileOffset, logical.Length - splitOffset));

        var result = new RuntimeDialogueReader(context).ReadRuntimeDialogueInfoFromVA(ObjectVa);

        Assert.NotNull(result);
        Assert.Equal(formId, result.FormId);
        Assert.Equal(expectedInfoIndex, result.InfoIndex);
        Assert.Equal((byte)5, result.TopicType);
        Assert.Equal((byte)2, result.NextSpeaker);
        Assert.Equal((byte)0xA5, result.InfoFlags);
        Assert.Equal((byte)0x02, result.InfoFlagsExt);
        Assert.Equal(4u, result.Difficulty);
        Assert.True(result.SaidOnce);
        Assert.Equal(expectedFileOffset, result.TesFileOffset);
        Assert.Equal(firstFileOffset, result.DumpOffset);
    }

    [Fact]
    public void ReadRuntimeLandData_StitchesLoadedDataBaseHeightFromSecondFileFragment()
    {
        const uint landVa = ObjectVa;
        const uint loadedDataVa = 0x40002000;
        const uint formId = 0x00345678;
        const int landFileOffset = 24;
        const int loadedFirstFileOffset = 200;
        const int loadedSecondFileOffset = 800;
        const int loadedSplitOffset = 160;
        const float expectedBaseHeight = 824.5f;
        var land = new byte[60];
        WriteFormHeader(land, 0x44, formId);
        WriteUInt32BE(land, 56, loadedDataVa);
        var loadedData = new byte[164];
        WriteInt32BE(loadedData, 152, 7);
        WriteInt32BE(loadedData, 156, -9);
        WriteFloatBE(loadedData, 160, expectedBaseHeight);

        var file = new byte[900];
        land.CopyTo(file, landFileOffset);
        CopySplit(
            loadedData,
            loadedSplitOffset,
            file,
            loadedFirstFileOffset,
            loadedSecondFileOffset);
        var context = CreateContext(
            file,
            Region(landVa, landFileOffset, land.Length),
            Region(loadedDataVa, loadedFirstFileOffset, loadedSplitOffset),
            Region(
                loadedDataVa + loadedSplitOffset,
                loadedSecondFileOffset,
                loadedData.Length - loadedSplitOffset));
        var entry = MakeEntry("SplitLand", formId, 0x44, landFileOffset, landVa);

        var result = new RuntimeWorldReader(context).ReadRuntimeLandData(entry);

        Assert.NotNull(result);
        Assert.Equal(formId, result.FormId);
        Assert.Equal(7, result.CellX);
        Assert.Equal(-9, result.CellY);
        Assert.Equal(expectedBaseHeight, result.BaseHeight);
        Assert.Equal(landFileOffset, result.LandOffset);
        Assert.Equal(loadedFirstFileOffset, result.LoadedDataOffset);
    }

    [Fact]
    public void AmmoDataProbe_StitchesSampleBeforeScoringSecondFragment()
    {
        const uint formId = 0x00456789;
        const int firstFileOffset = 40;
        const int secondFileOffset = 360;
        const int splitOffset = 64;
        const int expectedAmmoDataOffset = 188;
        var logical = SyntheticStructFactory.BuildAmmo(
            formId,
            expectedAmmoDataOffset,
            speed: 1500f,
            flags: 2,
            bufferSize: 224);
        var file = new byte[600];
        CopySplit(logical, splitOffset, file, firstFileOffset, secondFileOffset);
        var context = CreateContext(
            file,
            Region(ObjectVa, firstFileOffset, splitOffset),
            Region(ObjectVa + splitOffset, secondFileOffset, logical.Length - splitOffset));
        RuntimeEditorIdEntry[] entries =
        [
            MakeEntry("SplitProbeAmmo", formId, 0x29, firstFileOffset, ObjectVa)
        ];

        var result = RuntimeAmmoDataProbe.Probe(context, entries);

        Assert.NotNull(result);
        Assert.Equal(expectedAmmoDataOffset, result.Winner.Layout);
        Assert.Equal(1, result.WinnerScore);
        Assert.Equal(0, result.RunnerUpScore);
        Assert.Equal(1, result.SampleCount);
    }

    [Fact]
    public void ReadRuntimeAvif_StitchesAllBufferedStringHeadersBeyondTesFormHeader()
    {
        const uint formId = 0x0056789A;
        const uint fullNameVa = 0x40003000;
        const uint iconVa = 0x40003100;
        const uint abbreviationVa = 0x40003200;
        const string fullName = "Guns";
        const string icon = "interface\\icons\\guns.dds";
        const string abbreviation = "GUN";
        const int firstFileOffset = 32;
        const int secondFileOffset = 300;
        const int splitOffset = 40;
        const int fullNameFileOffset = 500;
        const int iconFileOffset = 540;
        const int abbreviationFileOffset = 580;
        var logical = new byte[84];
        WriteFormHeader(logical, 0x59, formId);
        WriteBsStringHeader(logical, 44, fullNameVa, fullName.Length);
        WriteBsStringHeader(logical, 64, iconVa, icon.Length);
        WriteBsStringHeader(logical, 76, abbreviationVa, abbreviation.Length);

        var file = new byte[640];
        CopySplit(logical, splitOffset, file, firstFileOffset, secondFileOffset);
        Encoding.ASCII.GetBytes(fullName).CopyTo(file, fullNameFileOffset);
        Encoding.ASCII.GetBytes(icon).CopyTo(file, iconFileOffset);
        Encoding.ASCII.GetBytes(abbreviation).CopyTo(file, abbreviationFileOffset);
        var context = CreateContext(
            file,
            Region(ObjectVa, firstFileOffset, splitOffset),
            Region(ObjectVa + splitOffset, secondFileOffset, logical.Length - splitOffset),
            Region(fullNameVa, fullNameFileOffset, fullName.Length),
            Region(iconVa, iconFileOffset, icon.Length),
            Region(abbreviationVa, abbreviationFileOffset, abbreviation.Length));
        var entry = MakeEntry("Guns", formId, 0x59, firstFileOffset, ObjectVa);

        var result = new RuntimeActorReader(context).ReadRuntimeAvif(entry);

        Assert.NotNull(result);
        Assert.Equal(fullName, result.FullName);
        Assert.Equal(icon, result.Icon);
        Assert.Equal(abbreviation, result.Abbreviation);
    }

    private static RuntimeEditorIdEntry MakeEntry(
        string editorId,
        uint formId,
        byte formType,
        long fileOffset,
        long va)
    {
        return new RuntimeEditorIdEntry
        {
            EditorId = editorId,
            FormId = formId,
            FormType = formType,
            TesFormOffset = fileOffset,
            TesFormPointer = va
        };
    }

    private static RuntimeMemoryContext CreateContext(
        byte[] file,
        params MinidumpMemoryRegion[] regions)
    {
        return new RuntimeMemoryContext(
            new ByteArrayMemoryAccessor(file),
            file.Length,
            new MinidumpInfo
            {
                IsValid = true,
                ProcessorArchitecture = 0x03,
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

    private static void CopySplit(
        byte[] logical,
        int splitOffset,
        byte[] file,
        int firstFileOffset,
        int secondFileOffset)
    {
        Array.Copy(logical, 0, file, firstFileOffset, splitOffset);
        Array.Copy(
            logical,
            splitOffset,
            file,
            secondFileOffset,
            logical.Length - splitOffset);
    }

    private static void WriteFormHeader(byte[] data, byte formType, uint formId)
    {
        WriteUInt32BE(data, 0, 0x82010000);
        data[4] = formType;
        WriteUInt32BE(data, 12, formId);
    }

    private static void WriteBsStringHeader(byte[] data, int offset, uint stringVa, int length)
    {
        WriteUInt32BE(data, offset, stringVa);
        WriteUInt16BE(data, offset + 4, checked((ushort)length));
        WriteUInt16BE(data, offset + 6, checked((ushort)length));
    }
}
