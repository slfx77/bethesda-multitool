using BethesdaMultitool.Core.Resources;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Resources;

/// <summary>
///     Locks the machine-scaled budget tiers (<see cref="AdaptiveMemoryDefaults" />): the shipped
///     mid-tier sizes stay exactly what the viewer always used (128 MB ring, 256 MB decoded mesh
///     cache), small machines shrink, big machines grow, and the tier boundaries don't drift.
/// </summary>
public sealed class AdaptiveMemoryDefaultsTests
{
    [Theory]
    [InlineData(8_000, 64)]    // 8 GB laptop → halved
    [InlineData(11_999, 64)]
    [InlineData(12_000, 128)]  // mid tier = the long-shipped default
    [InlineData(16_000, 128)]
    [InlineData(23_999, 128)]
    [InlineData(24_000, 256)]  // big-RAM workstation → doubled
    [InlineData(64_000, 256)]
    public void RingBufferMegabytes_TiersBySystemMemory(long systemMb, int expectedMb) =>
        Assert.Equal(expectedMb, AdaptiveMemoryDefaults.RingBufferMegabytes(systemMb));

    [Theory]
    [InlineData(8_000, 128)]
    [InlineData(12_000, 256)]  // mid tier = the long-shipped default
    [InlineData(23_999, 256)]
    [InlineData(24_000, 512)]
    [InlineData(47_999, 512)]
    [InlineData(48_000, 1024)]
    [InlineData(128_000, 1024)]
    public void DecodedMeshCacheMegabytes_TiersBySystemMemory(long systemMb, int expectedMb) =>
        Assert.Equal(expectedMb, AdaptiveMemoryDefaults.DecodedMeshCacheMegabytes(systemMb));

    [Fact]
    public void SystemMemoryMb_ReportsAPlausiblePhysicalSize()
    {
        // The GC's TotalAvailableMemoryBytes view: positive, and at least container-ish sized
        // (any machine/CI runner running this suite has well over 1 GB visible).
        Assert.True(AdaptiveMemoryDefaults.SystemMemoryMb > 1024,
            $"system memory reported as {AdaptiveMemoryDefaults.SystemMemoryMb} MB");
    }
}
