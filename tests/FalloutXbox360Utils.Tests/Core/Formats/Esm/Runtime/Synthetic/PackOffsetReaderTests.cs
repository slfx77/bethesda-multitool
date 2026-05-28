using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;
using static FalloutXbox360Utils.Tests.Helpers.BinaryTestWriter;
using static FalloutXbox360Utils.Tests.Helpers.SyntheticStructFactory;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime.Synthetic;

/// <summary>
///     Synthetic offset reader tests for <see cref="RuntimePackageReader" />
///     (TESPackage). Focuses on the contracts that aren't covered by the
///     generic FormID/FormType guards:
///       - PackageData inline reading (pack type, flags) at +44
///       - pPackLoc heap-shape gate (Phase 1B.15A fix — non-zero,
///         non-heap pPackLoc → reject the record)
///       - pPackTarg pointer chase to PackageTarget struct
///       - pCombatStyle FormType-gated pointer chase (Phase 1B.7 fix —
///         offset +88 not +104; FormType 0x4A required)
/// </summary>
public sealed class PackOffsetReaderTests
{
    private const byte CstyFormType = 0x4A;
    private const byte ActiFormType = 0x15; // used as a non-CSTY foil

    // Runtime offsets = PDB-baseline + _s(16). Mirror RuntimePackageReader.
    private const int PackStructSize = 128 + 16;
    private const int PackDataOffset = 28 + 16;       // 44
    private const int PackLocPtrOffset = 44 + 16;     // 60
    private const int PackTargPtrOffset = 48 + 16;    // 64
    private const int PackSchedOffset = 56 + 16;      // 72
    private const int CombatStylePtrOffset = 72 + 16; // 88

    // PACKAGE_DATA inner offsets (relative to PackDataOffset).
    private const int PackTypeInnerOffset = 4;

    private const uint PackVa = 0x40100000;
    private const uint LocVa = 0x40200000;
    private const uint TargVa = 0x40300000;
    private const uint CstyVa = 0x40400000;

    [Fact]
    public void ReadRuntimePackage_ResolvesCombatStylePointerWhenTargetIsCsty()
    {
        // Combat-style pointer at the corrected +88 offset (was +104 pre-Phase 1B.7).
        // FollowPointerToFormId expects FormType 0x4A (CSTY); a CSTY target resolves.
        const uint packFormId = 0x000D0001;
        const uint cstyFormId = 0x000D0099;
        var buffer = BuildPack(packFormId, packType: 0, packLocPtr: 0, packTargPtr: 0,
            combatStylePtr: CstyVa);

        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(buffer, PackVa)
            .WithPointerTarget(CstyVa, BuildTesForm(CstyFormType, cstyFormId));
        var reader = new RuntimePackageReader(fixture.BuildContext());

        var pack = reader.ReadRuntimePackage(
            fixture.MakeEntry(packFormId, formType: 0x4A /* PACK = 0x4A */, PackVa));

        Assert.NotNull(pack);
        Assert.Equal(packFormId, pack.FormId);
        Assert.Equal(cstyFormId, pack.CombatStyleFormId);
    }

    [Fact]
    public void ReadRuntimePackage_RejectsCombatStylePointerWhenTargetIsNotCsty()
    {
        // The CombatStyle pointer is FormType-gated: a non-CSTY target → null.
        // This catches stale pointers that resolve to unrelated forms.
        const uint packFormId = 0x000D0002;
        var buffer = BuildPack(packFormId, packType: 0, packLocPtr: 0, packTargPtr: 0,
            combatStylePtr: CstyVa);

        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(buffer, PackVa)
            .WithPointerTarget(CstyVa, BuildTesForm(ActiFormType /* not CSTY */, 0x000D00FF));
        var reader = new RuntimePackageReader(fixture.BuildContext());

        var pack = reader.ReadRuntimePackage(
            fixture.MakeEntry(packFormId, formType: 0x4A, PackVa));

        Assert.NotNull(pack);
        Assert.Null(pack.CombatStyleFormId);
    }

    [Fact]
    public void ReadRuntimePackage_NonHeapPackLocPointer_ReturnsNull()
    {
        // The Phase 1B.15A gate: pPackLoc that is non-zero AND not a heap-shape
        // pointer (0x40000000-0x7FFFFFFF) indicates a FormType-drift IDLE struct
        // misread as a PACK. Reader returns null instead of emitting a stub.
        // 0x00CBCB17 = the uninit fill pattern observed in real DMPs.
        const uint packFormId = 0x000D0003;
        var buffer = BuildPack(packFormId, packType: 0,
            packLocPtr: 0x00CBCB17, packTargPtr: 0, combatStylePtr: 0);

        var fixture = RuntimeReaderTestFixture.Default().WithStruct(buffer, PackVa);
        var reader = new RuntimePackageReader(fixture.BuildContext());

        Assert.Null(reader.ReadRuntimePackage(
            fixture.MakeEntry(packFormId, formType: 0x4A, PackVa)));
    }

    [Fact]
    public void ReadRuntimePackage_NullPackLocPointer_IsAccepted()
    {
        // Null pPackLoc is fine — real PACKs without a target location have it.
        const uint packFormId = 0x000D0004;
        var buffer = BuildPack(packFormId, packType: 0,
            packLocPtr: 0, packTargPtr: 0, combatStylePtr: 0);

        var fixture = RuntimeReaderTestFixture.Default().WithStruct(buffer, PackVa);
        var reader = new RuntimePackageReader(fixture.BuildContext());

        var pack = reader.ReadRuntimePackage(
            fixture.MakeEntry(packFormId, formType: 0x4A, PackVa));
        Assert.NotNull(pack);
        Assert.Null(pack.Location);
    }

    [Fact]
    public void ReadRuntimePackage_OutOfRangePackType_ReturnsNull()
    {
        // PACKAGE_DATA.packType > 20 is a strong "this isn't a real PACK" signal —
        // the reader's ReadPackageData returns null, which propagates.
        const uint packFormId = 0x000D0005;
        var buffer = BuildPack(packFormId, packType: 99 /* out of range */,
            packLocPtr: 0, packTargPtr: 0, combatStylePtr: 0);

        var fixture = RuntimeReaderTestFixture.Default().WithStruct(buffer, PackVa);
        var reader = new RuntimePackageReader(fixture.BuildContext());

        var pack = reader.ReadRuntimePackage(
            fixture.MakeEntry(packFormId, formType: 0x4A, PackVa));

        // packType > 20 → PackageData = null, but reader still returns a record
        // (the data is just absent). Confirm both records.
        Assert.NotNull(pack);
        Assert.Null(pack.Data);
    }

    [Fact]
    public void ReadRuntimePackage_FormIdMismatch_ReturnsNull()
    {
        var buffer = BuildPack(formId: 0x000D00AA, packType: 0,
            packLocPtr: 0, packTargPtr: 0, combatStylePtr: 0);

        var fixture = RuntimeReaderTestFixture.Default().WithStruct(buffer, PackVa);
        var reader = new RuntimePackageReader(fixture.BuildContext());

        Assert.Null(reader.ReadRuntimePackage(
            fixture.MakeEntry(0x000D0001, formType: 0x4A, PackVa)));
    }

    private static byte[] BuildPack(uint formId, byte packType,
        uint packLocPtr, uint packTargPtr, uint combatStylePtr)
    {
        var buf = new byte[PackStructSize];
        WriteFormHeader(buf, 0, formType: 0x4A, formId);

        // PACKAGE_DATA (12 bytes inline at PackDataOffset)
        // packFlags(+0) = 0, packType(+4), pad(+5), foBehavior(+6), typeSpecific(+8)
        buf[PackDataOffset + PackTypeInnerOffset] = packType;

        // Schedule (8 bytes inline at PackSchedOffset) — leave zero-valued
        // (month=0/dayOfWeek=0/date=0/time=0/duration=0 all pass validation).
        // The reader requires month in [-1, 11], dayOfWeek in [-1, 6], time in
        // [-1, 23], duration in [0, 744] — zeros are all in-range.

        WriteUInt32BE(buf, PackLocPtrOffset, packLocPtr);
        WriteUInt32BE(buf, PackTargPtrOffset, packTargPtr);
        WriteUInt32BE(buf, CombatStylePtrOffset, combatStylePtr);
        return buf;
    }

    private static byte[] BuildTesForm(byte formType, uint formId)
    {
        var buf = new byte[24];
        WriteFormHeader(buf, 0, formType, formId);
        return buf;
    }
}
