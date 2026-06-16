using Xunit;
using static FalloutXbox360Utils.Tests.Helpers.EsmTestFileBuilder;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     Regression for the FO3 terrain bug: exterior-cell grid coordinates must be read from XCLC
///     whether it is 8 bytes (Fallout 3: X, Y) or 12 bytes (Fallout NV: X, Y, forceHideLand). Gating
///     on a 12-byte XCLC silently dropped the grid on every FO3 exterior cell, which then filtered all
///     of its terrain out of the world viewer (and stranded persistent refs into synthetic tiles).
/// </summary>
public class CellXclcGridParsingTests
{
    [Theory]
    [InlineData(8)]   // Fallout 3
    [InlineData(12)]  // Fallout NV
    public void ExteriorCell_ReadsGridFromXclc_RegardlessOfTrailingFlagsField(int xclcByteCount)
    {
        var result = new Helpers.EsmTestFileBuilder()
            .AddWorldspace(new WorldspaceData
            {
                FormId = 0x100,
                EditorId = "TestWorld",
                ExteriorCells =
                {
                    new CellData { FormId = 0x200, GridX = 5, GridY = -7, XclcByteCount = xclcByteCount }
                }
            })
            .BuildAndAnalyze();

        var ws = Assert.Single(result.Collection.Worldspaces);
        var cell = ws.Cells.Single(c => c.FormId == 0x200);
        Assert.Equal(5, cell.GridX);
        Assert.Equal(-7, cell.GridY);
    }
}
