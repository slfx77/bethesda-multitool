using FalloutXbox360Utils.Core.Formats.Esm.Plugin;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

public class FormIdAllocatorTests
{
    [Fact]
    public void Allocate_FirstCall_ReturnsBaseLocalIdWithPluginIndex()
    {
        var allocator = new FormIdAllocator(baseLocalId: 0x800);

        var formId = allocator.Allocate();

        Assert.Equal(0x01000800u, formId);
    }

    [Fact]
    public void Allocate_IncrementsLocalId()
    {
        var allocator = new FormIdAllocator(baseLocalId: 0x800);

        var first = allocator.Allocate();
        var second = allocator.Allocate();
        var third = allocator.Allocate();

        Assert.Equal(0x01000800u, first);
        Assert.Equal(0x01000801u, second);
        Assert.Equal(0x01000802u, third);
    }

    [Fact]
    public void HasAllocations_TrueAfterFirstAllocate()
    {
        var allocator = new FormIdAllocator();

        Assert.False(allocator.HasAllocations);
        allocator.Allocate();
        Assert.True(allocator.HasAllocations);
    }

    [Fact]
    public void NextObjectId_TracksHighWaterMark()
    {
        var allocator = new FormIdAllocator(baseLocalId: 0x800);
        Assert.Equal(0x800u, allocator.NextObjectId);

        allocator.Allocate(); // 0x800
        Assert.Equal(0x801u, allocator.NextObjectId);

        allocator.Allocate(); // 0x801
        allocator.Allocate(); // 0x802
        Assert.Equal(0x803u, allocator.NextObjectId);
    }

    [Fact]
    public void Constructor_RejectsBaseAbove24BitLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FormIdAllocator(baseLocalId: 0x01000000));
    }

    [Fact]
    public void DefaultBase_IsGeckConvention()
    {
        Assert.Equal(0x800u, FormIdAllocator.DefaultBaseLocalId);
        Assert.Equal((byte)0x01, FormIdAllocator.PluginIndex);
    }
}
