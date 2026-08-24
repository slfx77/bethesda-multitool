using BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Terrain;

/// <summary>
///     Pins the terrain texture-set warm budget. The bulk warm at <c>TerrainRenderer12.LoadData</c>
///     is resident and scales with gridSize² × cells, so the worldspace that broke it is the one
///     pairing the biggest grid with the most cells: Fallout 76's Appalachia (129-grid, 41,219
///     gridded cells) wanted ~42 GB and exhausted memory before the first frame — the reported
///     "switching to 3D view freezes the application". These assert the pathological case is
///     refused while the 33-grid worldspaces the warm was profiled against still take it.
/// </summary>
public sealed class TerrainTextureSetWarmPolicyTests
{
    private const int FalloutGrid = 33;
    private const int BtdGrid = 129; // Fallout 76 / Starfield native BTD terrain
    private const int AppalachiaCells = 41_219;

    [Fact]
    public void Appalachia_IsRefused_AndWouldHaveCostTensOfGigabytes()
    {
        Assert.False(TerrainTextureSetWarmPolicy.ShouldWarm(BtdGrid, AppalachiaCells));

        // The number that made this a hang rather than a slow load: ~1 MB per cell, ~40 GB total.
        var perCell = TerrainTextureSetWarmPolicy.EstimateCellBytes(BtdGrid);
        Assert.InRange(perCell, 1_000_000, 1_100_000);
        Assert.True(
            TerrainTextureSetWarmPolicy.EstimateWarmBytes(BtdGrid, AppalachiaCells) > 40L * 1024 * 1024 * 1024,
            "Appalachia's full warm should price above 40 GB");
    }

    [Theory]
    [InlineData(1_000)] // a small exterior
    [InlineData(6_000)] // FNV's largest worldspace, the profile the warm was sized against
    public void FalloutSizedWorldspaces_StillWarm(int cellCount)
    {
        Assert.True(TerrainTextureSetWarmPolicy.ShouldWarm(FalloutGrid, cellCount));
    }

    [Fact]
    public void FalloutCell_CostsAboutSixtyEightKilobytes()
    {
        // 33² vertices × 4 slot-vectors × 16 B. The 129-grid cell is ~15× this, which is the whole
        // reason a per-cell cost that was fine at 33 is not fine at 129.
        Assert.Equal(69_696, TerrainTextureSetWarmPolicy.EstimateCellBytes(FalloutGrid));
    }

    [Fact]
    public void DegenerateInputs_DoNotWarm()
    {
        Assert.False(TerrainTextureSetWarmPolicy.ShouldWarm(FalloutGrid, 0));
        Assert.False(TerrainTextureSetWarmPolicy.ShouldWarm(0, 1_000));
    }
}
