using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;
using static FalloutXbox360Utils.Tests.Helpers.BinaryTestWriter;
using static FalloutXbox360Utils.Tests.Helpers.SyntheticStructFactory;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime.Synthetic;

/// <summary>
///     Synthetic offset reader tests for the LAND path of
///     <see cref="RuntimeWorldReader" /> (FormType 0x44 in the PDB layout,
///     drift-aware at runtime). Pins the two anchor offsets validated in
///     Phase 6.2:
///       pParentCell @ PDB +48 → CELL FormID
///       pLoadedData @ PDB +56 → heap pointer to LoadedLandData
///     <para>
///         Runtime offsets match PDB offsets when build shift == 16 (the only
///         observed value; <see cref="FalloutXbox360Utils.Core.Formats.Esm.Runtime.RuntimeBuildOffsets.GetPdbShift" />
///         hardcodes 16). The reader's <c>WithShift(0, int.MaxValue,
///         _shift)</c> where <c>_shift = GetPdbShift - 16 = 0</c> means the
///         PDB-resolved offsets are used directly.
///     </para>
/// </summary>
public sealed class LandOffsetReaderTests
{
    private const byte LandFormType = 0x44;
    private const byte CellFormType = 0x39;

    // PDB-resolved offsets for class "TESObjectLAND".
    private const int LandStructSize = 60;
    private const int ParentCellPtrOffset = 48;
    private const int LoadedDataPtrOffset = 56;

    // LoadedLandData inner-struct offsets (from RuntimeWorldReader constants).
    private const int LoadedDataSize = 164;
    private const int LoadedDataCellXOffset = 152;
    private const int LoadedDataCellYOffset = 156;
    private const int LoadedDataBaseHeightOffset = 160;

    private const uint LandVa = 0x40100000;
    private const uint CellVa = 0x40200000;
    private const uint LoadedDataVa = 0x40300000;

    [Fact]
    public void ReadRuntimeLandData_ResolvesParentCellAndLoadedDataChain()
    {
        const uint landFormId = 0x000A0001;
        const uint cellFormId = 0x000A0099;
        const int cellX = 5;
        const int cellY = -3;
        const float baseHeight = 1024.0f;

        var landBuffer = BuildLand(landFormId, parentCellPtr: CellVa, loadedDataPtr: LoadedDataVa);
        var cellBuffer = BuildTesCell(cellFormId, isInterior: false);
        var loadedData = BuildLoadedLandData(cellX, cellY, baseHeight);

        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(landBuffer, LandVa)
            .WithPointerTarget(CellVa, cellBuffer)
            .WithPointerTarget(LoadedDataVa, loadedData);
        var reader = new RuntimeWorldReader(fixture.BuildContext());

        var land = reader.ReadRuntimeLandData(
            fixture.MakeEntry(landFormId, LandFormType, LandVa));

        Assert.NotNull(land);
        Assert.Equal(landFormId, land.FormId);
        Assert.Equal(cellFormId, land.ParentCellFormId);
        Assert.Equal(cellX, land.CellX);
        Assert.Equal(cellY, land.CellY);
        Assert.Equal(baseHeight, land.BaseHeight);
    }

    [Fact]
    public void ReadRuntimeLandData_NullLoadedDataPointer_ReturnsNull()
    {
        // Reader requires non-null pLoadedData; returns null without it.
        const uint landFormId = 0x000A0002;
        var landBuffer = BuildLand(landFormId, parentCellPtr: 0, loadedDataPtr: 0);

        var fixture = RuntimeReaderTestFixture.Default().WithStruct(landBuffer, LandVa);
        var reader = new RuntimeWorldReader(fixture.BuildContext());

        Assert.Null(reader.ReadRuntimeLandData(
            fixture.MakeEntry(landFormId, LandFormType, LandVa)));
    }

    [Fact]
    public void ReadRuntimeLandData_OutOfRangeCellCoordinates_ReturnsNull()
    {
        // Reader gates cellX/cellY to [-1000, 1000]; out-of-range → null.
        const uint landFormId = 0x000A0003;
        var landBuffer = BuildLand(landFormId, parentCellPtr: 0, loadedDataPtr: LoadedDataVa);
        var loadedData = BuildLoadedLandData(cellX: 5000 /* out of range */, cellY: 0, baseHeight: 0);

        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(landBuffer, LandVa)
            .WithPointerTarget(LoadedDataVa, loadedData);
        var reader = new RuntimeWorldReader(fixture.BuildContext());

        Assert.Null(reader.ReadRuntimeLandData(
            fixture.MakeEntry(landFormId, LandFormType, LandVa)));
    }

    [Fact]
    public void ReadRuntimeLandData_AbsentParentCell_PopulatesNullCellFormId()
    {
        // pParentCell = 0 is valid; just yields null ParentCellFormId.
        const uint landFormId = 0x000A0004;
        var landBuffer = BuildLand(landFormId, parentCellPtr: 0, loadedDataPtr: LoadedDataVa);
        var loadedData = BuildLoadedLandData(cellX: 0, cellY: 0, baseHeight: 0);

        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(landBuffer, LandVa)
            .WithPointerTarget(LoadedDataVa, loadedData);
        var reader = new RuntimeWorldReader(fixture.BuildContext());

        var land = reader.ReadRuntimeLandData(
            fixture.MakeEntry(landFormId, LandFormType, LandVa));
        Assert.NotNull(land);
        Assert.Null(land.ParentCellFormId);
    }

    private static byte[] BuildLand(uint formId, uint parentCellPtr, uint loadedDataPtr)
    {
        var buf = new byte[LandStructSize];
        WriteFormHeader(buf, 0, LandFormType, formId);
        WriteUInt32BE(buf, ParentCellPtrOffset, parentCellPtr);
        WriteUInt32BE(buf, LoadedDataPtrOffset, loadedDataPtr);
        return buf;
    }

    /// <summary>
    ///     Builds a minimal LoadedLandData buffer — 164 bytes with cellX/cellY/baseHeight
    ///     at the runtime offsets, and HeightExtents (NiPoint2) at +24 left zero
    ///     (reader normalizes invalid/missing extents to null).
    /// </summary>
    private static byte[] BuildLoadedLandData(int cellX, int cellY, float baseHeight)
    {
        var buf = new byte[LoadedDataSize];
        WriteInt32BE(buf, LoadedDataCellXOffset, cellX);
        WriteInt32BE(buf, LoadedDataCellYOffset, cellY);
        WriteFloatBE(buf, LoadedDataBaseHeightOffset, baseHeight);
        return buf;
    }

    /// <summary>
    ///     Builds a minimal 64-byte TESObjectCELL stub. FollowPointerToCellInfo
    ///     reads 53 bytes covering form header + cCellFlags @ +52.
    /// </summary>
    private static byte[] BuildTesCell(uint formId, bool isInterior)
    {
        var buf = new byte[64];
        WriteFormHeader(buf, 0, CellFormType, formId);
        buf[52] = (byte)(isInterior ? 0x01 : 0x00);
        return buf;
    }
}
