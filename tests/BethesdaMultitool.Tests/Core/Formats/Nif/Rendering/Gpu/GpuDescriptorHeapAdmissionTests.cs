using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Vortice.Direct3D12;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>Rejects invalid partition requests before any native device or descriptor allocation is required.</summary>
public sealed class GpuDescriptorHeapAdmissionTests
{
    private const DescriptorHeapType ResourceType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;

    /// <summary>Unsupported shader-visible domains fail before device access.</summary>
    [Fact]
    public void UnsupportedDomainsFailBeforeNativeAccess()
    {
        foreach (var type in new[] { DescriptorHeapType.RenderTargetView, DescriptorHeapType.DepthStencilView, (DescriptorHeapType)(-1) })
            Assert.Throws<ArgumentException>(() => new GpuDescriptorHeapAllocator12(null!, type, 4, 2));
    }

    /// <summary>Empty or indivisible frame regions fail before device access.</summary>
    [Fact]
    public void InvalidPartitionsFailBeforeNativeAccess()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GpuDescriptorHeapAllocator12(null!, ResourceType, 0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GpuDescriptorHeapAllocator12(null!, ResourceType, 4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GpuDescriptorHeapAllocator12(null!, ResourceType, 4, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GpuDescriptorHeapAllocator12(null!, ResourceType, 4, 2, 4));
        Assert.Throws<ArgumentException>(() => new GpuDescriptorHeapAllocator12(null!, ResourceType, 5, 2));
        Assert.Throws<ArgumentException>(() => new GpuDescriptorHeapAllocator12(null!, ResourceType, 2, 3));
    }

    /// <summary>A valid managed layout with a missing borrowed device fails explicitly.</summary>
    [Fact]
    public void MissingDeviceIsRejectedAfterPartitionAdmission()
    {
        var error = Assert.Throws<ArgumentNullException>(() => new GpuDescriptorHeapAllocator12(null!, ResourceType, 4, 2));
        Assert.Equal("gpu", error.ParamName);
    }
}
