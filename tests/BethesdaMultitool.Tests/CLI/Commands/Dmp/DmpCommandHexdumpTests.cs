using BethesdaMultitool.CLI.Commands.Dmp;
using Xunit;

namespace BethesdaMultitool.Tests.CLI.Commands.Dmp;

/// <summary>
///     Pins the fix for `dmp hexdump` silently printing nothing on a bare offset past EOF
///     (seek beyond file + Read returning 0 used to exit 0 with no output). Bare offsets
///     are classified against the dump length; out-of-range values that look like virtual
///     addresses earn a "retry with the 'va:' prefix" hint.
/// </summary>
public sealed class DmpCommandHexdumpTests
{
    private const long FileLength = 0x1000;

    [Theory]
    [InlineData(0L)]
    [InlineData(0x800L)]
    [InlineData(FileLength - 1)]
    public void ClassifyBareOffset_WithinFile_IsInRange(long offset)
    {
        var result = DmpCommand.ClassifyBareOffset(offset, FileLength, resolvesAsVirtualAddress: false);

        Assert.Equal(DmpCommand.BareOffsetClassification.InRange, result);
    }

    [Fact]
    public void ClassifyBareOffset_WithinFile_StaysInRangeEvenIfItResolvesAsVa()
    {
        // A small offset can coincide with a captured VA; an in-file offset must still dump.
        var result = DmpCommand.ClassifyBareOffset(0x200, FileLength, resolvesAsVirtualAddress: true);

        Assert.Equal(DmpCommand.BareOffsetClassification.InRange, result);
    }

    [Theory]
    [InlineData(FileLength)] // first byte past EOF
    [InlineData(FileLength + 1)]
    [InlineData(0x7FFFFFFFL)] // large, but below the VA heuristic threshold
    public void ClassifyBareOffset_PastEof_IsOutOfRange(long offset)
    {
        var result = DmpCommand.ClassifyBareOffset(offset, FileLength, resolvesAsVirtualAddress: false);

        Assert.Equal(DmpCommand.BareOffsetClassification.OutOfRange, result);
    }

    [Theory]
    [InlineData(0x80000000L)] // threshold itself
    [InlineData(0x82041204L)] // typical Xbox 360 module VA
    public void ClassifyBareOffset_PastEofAboveVaThreshold_SuggestsVaPrefix(long offset)
    {
        var result = DmpCommand.ClassifyBareOffset(offset, FileLength, resolvesAsVirtualAddress: false);

        Assert.Equal(DmpCommand.BareOffsetClassification.OutOfRangeLikelyVirtualAddress, result);
    }

    [Fact]
    public void ClassifyBareOffset_PastEofResolvableThroughRegionTables_SuggestsVaPrefix()
    {
        // Below 0x80000000 but the module/region tables resolve it (e.g. a heap VA).
        var result = DmpCommand.ClassifyBareOffset(
            0x40001000, FileLength, resolvesAsVirtualAddress: true);

        Assert.Equal(DmpCommand.BareOffsetClassification.OutOfRangeLikelyVirtualAddress, result);
    }

    [Fact]
    public void ClassifyBareOffset_NegativeOffset_IsOutOfRange()
    {
        // "FFFFFFFFFFFFFFFF" parses to -1 via long hex parsing.
        var result = DmpCommand.ClassifyBareOffset(-1, FileLength, resolvesAsVirtualAddress: false);

        Assert.Equal(DmpCommand.BareOffsetClassification.OutOfRange, result);
    }
}
