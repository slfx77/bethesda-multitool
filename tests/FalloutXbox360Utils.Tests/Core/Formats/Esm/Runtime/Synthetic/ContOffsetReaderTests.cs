using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;
using static FalloutXbox360Utils.Tests.Helpers.BinaryTestWriter;
using static FalloutXbox360Utils.Tests.Helpers.SyntheticStructFactory;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime.Synthetic;

/// <summary>
///     Synthetic offset reader tests for <see cref="RuntimeContainerReader" />
///     (FormType 0x1B, TESObjectCONT). Pins the contents-list traversal offsets
///     (ContentsData @ +68, ContentsNext @ +72) + script pointer @ +124 + weight
///     @ +136 + flags @ +168, all PDB-baseline-relative shifted by +16. Phase
///     1B.12 anchors validated all 3 pointer offsets at 100% across snippets.
/// </summary>
public sealed class ContOffsetReaderTests
{
    private const byte ContFormType = 0x1B;
    private const byte ScriptFormType = 0x11;
    private const byte AmmoFormType = 0x29;

    // Runtime offsets = PDB-baseline + _s(16).
    private const int ContStructSize = 156 + 16;
    private const int ContentsDataOffset = 52 + 16;     // 68
    private const int ContentsNextOffset = 56 + 16;     // 72
    private const int ScriptPtrOffset = 108 + 16;       // 124
    private const int WeightOffset = 120 + 16;          // 136
    private const int FlagsOffset = 152 + 16;           // 168

    private const uint ContVa = 0x40100000;
    private const uint ScriptVa = 0x40200000;
    private const uint ContObjVa = 0x40300000;
    private const uint ItemVa = 0x40400000;

    [Fact]
    public void ReadRuntimeContainer_ResolvesScriptPointerToFormId()
    {
        const uint contFormId = 0x000B0001;
        const uint scriptFormId = 0x000B0099;
        var buffer = BuildCont(contFormId, scriptPtr: ScriptVa, flags: 0, weight: 0);

        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(buffer, ContVa)
            .WithPointerTarget(ScriptVa, BuildTesForm(ScriptFormType, scriptFormId));
        var reader = new RuntimeContainerReader(fixture.BuildContext());

        var cont = reader.ReadRuntimeContainer(
            fixture.MakeEntry(contFormId, ContFormType, ContVa));

        Assert.NotNull(cont);
        Assert.Equal(contFormId, cont.FormId);
        Assert.Equal(scriptFormId, cont.Script);
    }

    [Fact]
    public void ReadRuntimeContainer_ReadsFlagsAndWeight()
    {
        const uint contFormId = 0x000B0002;
        var buffer = BuildCont(contFormId, scriptPtr: 0, flags: 0x03, weight: 25.5f);

        var fixture = RuntimeReaderTestFixture.Default().WithStruct(buffer, ContVa);
        var reader = new RuntimeContainerReader(fixture.BuildContext());

        var cont = reader.ReadRuntimeContainer(
            fixture.MakeEntry(contFormId, ContFormType, ContVa));

        Assert.NotNull(cont);
        Assert.Equal((byte)0x03, cont.Flags);
        Assert.Equal(25.5f, cont.Weight);
    }

    [Fact]
    public void ReadRuntimeContainer_ResolvesInlineContainerObject()
    {
        // Inline first item at ContentsDataOffset: a ContainerObject pointer
        // (8 bytes: count int32 BE + pItem TESForm*). pItem follows to an
        // AMMO with FormID 0x12345.
        const uint contFormId = 0x000B0003;
        const uint itemFormId = 0x00012345;
        const int itemCount = 7;

        var buffer = BuildCont(contFormId, scriptPtr: 0, flags: 0, weight: 0,
            contentsDataPtr: ContObjVa);

        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(buffer, ContVa)
            .WithPointerTarget(ContObjVa, BuildContainerObject(count: itemCount, itemPtr: ItemVa))
            .WithPointerTarget(ItemVa, BuildTesForm(AmmoFormType, itemFormId));
        var reader = new RuntimeContainerReader(fixture.BuildContext());

        var cont = reader.ReadRuntimeContainer(
            fixture.MakeEntry(contFormId, ContFormType, ContVa));

        Assert.NotNull(cont);
        Assert.Single(cont.Contents);
        Assert.Equal(itemFormId, cont.Contents[0].ItemFormId);
        Assert.Equal(itemCount, cont.Contents[0].Count);
    }

    [Fact]
    public void ReadRuntimeContainer_FormIdMismatch_ReturnsNull()
    {
        var buffer = BuildCont(formId: 0x000B00AA, scriptPtr: 0, flags: 0, weight: 0);

        var fixture = RuntimeReaderTestFixture.Default().WithStruct(buffer, ContVa);
        var reader = new RuntimeContainerReader(fixture.BuildContext());

        Assert.Null(reader.ReadRuntimeContainer(
            fixture.MakeEntry(0x000B0001, ContFormType, ContVa)));
    }

    [Fact]
    public void ReadRuntimeContainer_WrongFormType_ReturnsNull()
    {
        var buffer = BuildCont(0x000B0004, scriptPtr: 0, flags: 0, weight: 0);

        var fixture = RuntimeReaderTestFixture.Default().WithStruct(buffer, ContVa);
        var reader = new RuntimeContainerReader(fixture.BuildContext());

        Assert.Null(reader.ReadRuntimeContainer(
            fixture.MakeEntry(0x000B0004, formType: 0x28 /* WEAP, not CONT */, ContVa)));
    }

    private static byte[] BuildCont(uint formId, uint scriptPtr, byte flags, float weight,
        uint contentsDataPtr = 0)
    {
        var buf = new byte[ContStructSize];
        WriteFormHeader(buf, 0, ContFormType, formId);
        WriteUInt32BE(buf, ContentsDataOffset, contentsDataPtr);
        WriteUInt32BE(buf, ContentsNextOffset, 0); // empty chain
        WriteFloatBE(buf, WeightOffset, weight);
        WriteUInt32BE(buf, ScriptPtrOffset, scriptPtr);
        buf[FlagsOffset] = flags;
        return buf;
    }

    /// <summary>
    ///     Builds an 8-byte ContainerObject: count(int32 BE) + pItem(TESForm*).
    /// </summary>
    private static byte[] BuildContainerObject(int count, uint itemPtr)
    {
        var buf = new byte[8];
        WriteInt32BE(buf, 0, count);
        WriteUInt32BE(buf, 4, itemPtr);
        return buf;
    }

    private static byte[] BuildTesForm(byte formType, uint formId)
    {
        var buf = new byte[24];
        WriteFormHeader(buf, 0, formType, formId);
        return buf;
    }
}
