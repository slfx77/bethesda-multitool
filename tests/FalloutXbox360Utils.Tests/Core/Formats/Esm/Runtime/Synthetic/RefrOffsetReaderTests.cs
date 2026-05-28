using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;
using static FalloutXbox360Utils.Tests.Helpers.BinaryTestWriter;
using static FalloutXbox360Utils.Tests.Helpers.SyntheticStructFactory;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime.Synthetic;

/// <summary>
///     Synthetic offset reader tests for <see cref="RuntimeRefrReader" />
///     (REFR/ACHR/ACRE — FormType 0x3A-0x3C). Covers both TESChildCell layout
///     variants: 8-byte (vtable + data) and 4-byte (vtable only, all subsequent
///     fields shift -4).
///     <para>
///         Replaces the REFR section of the deleted
///         <c>RuntimeOffsetCrossReferenceTests</c> harness.
///     </para>
/// </summary>
public sealed class RefrOffsetReaderTests
{
    private const byte ActiFormType = 0x15; // ACTI — generic activator base
    private const byte CellFormType = 0x39;

    // 8-byte TESChildCell variant ("Final"): struct size 120, offsets at PDB locations.
    private const int FinalRefrStructSize = 120;
    private const int FinalBaseObjectPtrOffset = 48;
    private const int FinalAngleXOffset = 52;
    private const int FinalAngleYOffset = 56;
    private const int FinalAngleZOffset = 60;
    private const int FinalLocationXOffset = 64;
    private const int FinalLocationYOffset = 68;
    private const int FinalLocationZOffset = 72;
    private const int FinalRefScaleOffset = 76;
    private const int FinalParentCellPtrOffset = 80;
    private const int FinalExtraListHeadOffset = 88;

    // 4-byte TESChildCell variant ("Early"): all OBJ_REFR / extra-list offsets -4.
    private const int EarlyRefrStructSize = 116;
    private const int EarlyShift = -4;

    private const uint RefrVa = 0x40100000;
    private const uint BaseObjVa = 0x40200000;
    private const uint CellVa = 0x40300000;

    [Theory]
    [InlineData((byte)0x3A)] // REFR
    [InlineData((byte)0x3B)] // ACHR
    [InlineData((byte)0x3C)] // ACRE
    public void ReadRuntimeRefr_Final8ByteVariant_ResolvesAllFields(byte formType)
    {
        const uint refrFormId = 0x01000001;
        const uint baseFormId = 0x0001A001;
        const uint cellFormId = 0x0000000F;

        var buffer = BuildRefrFinal(refrFormId, baseObjectPtr: BaseObjVa, parentCellPtr: CellVa,
            locX: 100.5f, locY: 200.25f, locZ: -50.0f,
            rotX: 0.5f, rotY: 1.0f, rotZ: -0.25f,
            scale: 1.5f, flags: 0);

        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(buffer, RefrVa)
            .WithPointerTarget(BaseObjVa, BuildTesForm(ActiFormType, baseFormId))
            .WithPointerTarget(CellVa, BuildTesCell(cellFormId, isInterior: false));

        var reader = new RuntimeRefrReader(fixture.BuildContext(), useProtoOffsets: false);
        var refr = reader.ReadRuntimeRefr(fixture.MakeEntry(refrFormId, formType, RefrVa));

        Assert.NotNull(refr);
        Assert.Equal(refrFormId, refr.Header.FormId);
        Assert.Equal(baseFormId, refr.BaseFormId);
        Assert.Equal(cellFormId, refr.ParentCellFormId);
        Assert.False(refr.ParentCellIsInterior);
        Assert.Equal(100.5f, refr.Position.X);
        Assert.Equal(200.25f, refr.Position.Y);
        Assert.Equal(-50.0f, refr.Position.Z);
        Assert.Equal(0.5f, refr.Position.RotX);
        Assert.Equal(1.0f, refr.Position.RotY);
        Assert.Equal(-0.25f, refr.Position.RotZ);
        Assert.Equal(1.5f, refr.Scale);
    }

    [Fact]
    public void ReadRuntimeRefr_Early4ByteVariant_ResolvesAllFields()
    {
        // Early-variant offsets shift by -4. Same logical layout, different on-disk
        // offsets. Production discovers via RuntimeRefrReader.ProbeIsEarlyBuild.
        const uint refrFormId = 0x01000002;
        const uint baseFormId = 0x0001A002;

        var buffer = BuildRefrEarly(refrFormId, baseObjectPtr: BaseObjVa, parentCellPtr: 0,
            locX: 10.0f, locY: 20.0f, locZ: 30.0f, rotX: 0, rotY: 0, rotZ: 0, scale: 1.0f, flags: 0);

        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(buffer, RefrVa)
            .WithPointerTarget(BaseObjVa, BuildTesForm(ActiFormType, baseFormId));

        var reader = new RuntimeRefrReader(fixture.BuildContext(), useProtoOffsets: true);
        var refr = reader.ReadRuntimeRefr(fixture.MakeEntry(refrFormId, formType: 0x3A, RefrVa));

        Assert.NotNull(refr);
        Assert.Equal(baseFormId, refr.BaseFormId);
        Assert.Equal(10.0f, refr.Position.X);
        Assert.Equal(20.0f, refr.Position.Y);
        Assert.Equal(30.0f, refr.Position.Z);
    }

    [Fact]
    public void ReadRuntimeRefr_NullBaseObjectPointer_ReturnsNull()
    {
        // Reader requires a non-null base object — returns null otherwise.
        const uint refrFormId = 0x01000003;
        var buffer = BuildRefrFinal(refrFormId, baseObjectPtr: 0, parentCellPtr: 0,
            locX: 0, locY: 0, locZ: 0, rotX: 0, rotY: 0, rotZ: 0, scale: 1.0f, flags: 0);

        var fixture = RuntimeReaderTestFixture.Default().WithStruct(buffer, RefrVa);
        var reader = new RuntimeRefrReader(fixture.BuildContext());

        Assert.Null(reader.ReadRuntimeRefr(fixture.MakeEntry(refrFormId, 0x3A, RefrVa)));
    }

    [Fact]
    public void ReadRuntimeRefr_DeletedFlag_ReturnsNull()
    {
        // 0x20 deleted flag in TESForm.flags causes the reader to skip the record.
        const uint refrFormId = 0x01000004;
        const uint baseFormId = 0x0001A004;
        var buffer = BuildRefrFinal(refrFormId, baseObjectPtr: BaseObjVa, parentCellPtr: 0,
            locX: 0, locY: 0, locZ: 0, rotX: 0, rotY: 0, rotZ: 0, scale: 1.0f,
            flags: 0x20 /* DELETED */);

        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(buffer, RefrVa)
            .WithPointerTarget(BaseObjVa, BuildTesForm(ActiFormType, baseFormId));
        var reader = new RuntimeRefrReader(fixture.BuildContext());

        Assert.Null(reader.ReadRuntimeRefr(fixture.MakeEntry(refrFormId, 0x3A, RefrVa)));
    }

    [Fact]
    public void ReadRuntimeRefr_OutOfRangeLocation_ReturnsNull()
    {
        // Reader bails on |loc.X| > 500_000 — catches garbage offset reads where
        // a non-position field is being misinterpreted as a position float.
        const uint refrFormId = 0x01000005;
        const uint baseFormId = 0x0001A005;
        var buffer = BuildRefrFinal(refrFormId, baseObjectPtr: BaseObjVa, parentCellPtr: 0,
            locX: 1_000_000.0f /* out of range */, locY: 0, locZ: 0,
            rotX: 0, rotY: 0, rotZ: 0, scale: 1.0f, flags: 0);

        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(buffer, RefrVa)
            .WithPointerTarget(BaseObjVa, BuildTesForm(ActiFormType, baseFormId));
        var reader = new RuntimeRefrReader(fixture.BuildContext());

        Assert.Null(reader.ReadRuntimeRefr(fixture.MakeEntry(refrFormId, 0x3A, RefrVa)));
    }

    [Fact]
    public void ReadRuntimeRefr_OutOfRangeScale_DefaultsToOne()
    {
        // Reader gates scale to (0, 100]; out-of-range values clamp to 1.0.
        const uint refrFormId = 0x01000006;
        const uint baseFormId = 0x0001A006;
        var buffer = BuildRefrFinal(refrFormId, baseObjectPtr: BaseObjVa, parentCellPtr: 0,
            locX: 0, locY: 0, locZ: 0, rotX: 0, rotY: 0, rotZ: 0,
            scale: 500.0f /* out of range — clamps to 1.0 */, flags: 0);

        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(buffer, RefrVa)
            .WithPointerTarget(BaseObjVa, BuildTesForm(ActiFormType, baseFormId));
        var reader = new RuntimeRefrReader(fixture.BuildContext());

        var refr = reader.ReadRuntimeRefr(fixture.MakeEntry(refrFormId, 0x3A, RefrVa));
        Assert.NotNull(refr);
        Assert.Equal(1.0f, refr.Scale);
    }

    [Fact]
    public void ReadRuntimeRefr_FormTypeOutsideRefrRange_ReturnsNull()
    {
        // Reader's FormType guard: must be 0x3A-0x3C. The header validation reads
        // formType from the buffer (not the entry), so a mismatched buffer bytes
        // produce null.
        const uint refrFormId = 0x01000007;
        var buffer = BuildRefrFinal(refrFormId, baseObjectPtr: BaseObjVa, parentCellPtr: 0,
            locX: 0, locY: 0, locZ: 0, rotX: 0, rotY: 0, rotZ: 0, scale: 1.0f, flags: 0);
        // Patch FormType to 0x28 (WEAP) in the header — readers expects 0x3A-0x3C.
        buffer[4] = 0x28;

        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(buffer, RefrVa)
            .WithPointerTarget(BaseObjVa, BuildTesForm(ActiFormType, 0x0001A007));
        var reader = new RuntimeRefrReader(fixture.BuildContext());

        Assert.Null(reader.ReadRuntimeRefr(fixture.MakeEntry(refrFormId, 0x3A, RefrVa)));
    }

    // =========================================================================
    // Synthetic struct builders (REFR/ACHR/ACRE in two layout variants).
    // Field offsets mirror the production constants in RuntimeRefrReader.
    // =========================================================================

    private static byte[] BuildRefrFinal(uint formId, uint baseObjectPtr, uint parentCellPtr,
        float locX, float locY, float locZ,
        float rotX, float rotY, float rotZ,
        float scale, uint flags)
    {
        var buf = new byte[FinalRefrStructSize];
        WriteFormHeader(buf, 0, formType: 0x3A, formId);
        WriteUInt32BE(buf, 8, flags); // FormFlags
        WriteUInt32BE(buf, FinalBaseObjectPtrOffset, baseObjectPtr);
        WriteFloatBE(buf, FinalAngleXOffset, rotX);
        WriteFloatBE(buf, FinalAngleYOffset, rotY);
        WriteFloatBE(buf, FinalAngleZOffset, rotZ);
        WriteFloatBE(buf, FinalLocationXOffset, locX);
        WriteFloatBE(buf, FinalLocationYOffset, locY);
        WriteFloatBE(buf, FinalLocationZOffset, locZ);
        WriteFloatBE(buf, FinalRefScaleOffset, scale);
        WriteUInt32BE(buf, FinalParentCellPtrOffset, parentCellPtr);
        // ExtraListHead @ +88 left zero (empty list — OK).
        return buf;
    }

    private static byte[] BuildRefrEarly(uint formId, uint baseObjectPtr, uint parentCellPtr,
        float locX, float locY, float locZ,
        float rotX, float rotY, float rotZ,
        float scale, uint flags)
    {
        var buf = new byte[EarlyRefrStructSize];
        WriteFormHeader(buf, 0, formType: 0x3A, formId);
        WriteUInt32BE(buf, 8, flags);
        WriteUInt32BE(buf, FinalBaseObjectPtrOffset + EarlyShift, baseObjectPtr);
        WriteFloatBE(buf, FinalAngleXOffset + EarlyShift, rotX);
        WriteFloatBE(buf, FinalAngleYOffset + EarlyShift, rotY);
        WriteFloatBE(buf, FinalAngleZOffset + EarlyShift, rotZ);
        WriteFloatBE(buf, FinalLocationXOffset + EarlyShift, locX);
        WriteFloatBE(buf, FinalLocationYOffset + EarlyShift, locY);
        WriteFloatBE(buf, FinalLocationZOffset + EarlyShift, locZ);
        WriteFloatBE(buf, FinalRefScaleOffset + EarlyShift, scale);
        WriteUInt32BE(buf, FinalParentCellPtrOffset + EarlyShift, parentCellPtr);
        return buf;
    }

    private static byte[] BuildTesForm(byte formType, uint formId)
    {
        var buf = new byte[24];
        WriteFormHeader(buf, 0, formType, formId);
        return buf;
    }

    /// <summary>
    ///     Builds a minimal TESObjectCELL target — 64 bytes covering the form
    ///     header and the cCellFlags byte at +52 (bit 0 = IsInterior). The
    ///     production reader reads 53 bytes from a cell pointer to get both
    ///     FormID and the interior flag.
    /// </summary>
    private static byte[] BuildTesCell(uint formId, bool isInterior)
    {
        var buf = new byte[64];
        WriteFormHeader(buf, 0, CellFormType, formId);
        buf[52] = (byte)(isInterior ? 0x01 : 0x00);
        return buf;
    }
}
