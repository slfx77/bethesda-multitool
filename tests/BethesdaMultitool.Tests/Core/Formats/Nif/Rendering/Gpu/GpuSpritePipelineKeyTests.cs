using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using Vortice.Direct3D12;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>Checks sprite pipeline deduplication against the production native blend interpretation.</summary>
public sealed class GpuSpritePipelineKeyTests
{
    /// <summary>Preserves every byte's native factor in either blend position, including the unknown-value fallback.</summary>
    [Fact]
    public void EveryBlendBytePreservesItsNativeFactorInEitherPosition()
    {
        for (var value = 0; value <= byte.MaxValue; value++)
        {
            var raw = (byte)value;
            var source = GpuSpritePipelineKey.Create(NifAlphaRenderMode.Blend, raw, 7, false, false);
            var destination = GpuSpritePipelineKey.Create(NifAlphaRenderMode.Blend, 6, raw, false, false);

            Assert.True(source.Blended);
            Assert.True(destination.Blended);
            Assert.InRange(source.SourceBlend, (byte)0, (byte)10);
            Assert.InRange(destination.DestinationBlend, (byte)0, (byte)10);
            Assert.Equal(NifD3D12BlendMapper.ResolveBlendFactor(raw),
                NifD3D12BlendMapper.ResolveBlendFactor(source.SourceBlend));
            Assert.Equal(NifD3D12BlendMapper.ResolveBlendFactor(raw),
                NifD3D12BlendMapper.ResolveBlendFactor(destination.DestinationBlend));
            Assert.Equal((byte)7, source.DestinationBlend);
            Assert.Equal((byte)6, destination.SourceBlend);
            if (value <= 10)
            {
                Assert.Equal(raw, source.SourceBlend);
                Assert.Equal(raw, destination.DestinationBlend);
            }
            else
            {
                Assert.Equal((byte)6, source.SourceBlend);
                Assert.Equal((byte)6, destination.DestinationBlend);
            }
        }
    }

    /// <summary>Assigns exactly one slot to each native blend/cull/shader state, covering all raw blend pairs.</summary>
    [Fact]
    public void NativeStatesAndSlotsFormABijectionAcrossAllBlendBytePairs()
    {
        bool[] options = [false, true];
        var slotsByState = new Dictionary<(bool Blended, Blend? Source, Blend? Destination,
            CullMode Cull, bool ClassicSkin), int>();
        var statesBySlot = new Dictionary<int, (bool Blended, Blend? Source, Blend? Destination,
            CullMode Cull, bool ClassicSkin)>();

        foreach (var doubleSided in options)
        {
            foreach (var classicSkin in options)
            {
                var cull = doubleSided ? CullMode.None : CullMode.Back;
                var opaque = GpuSpritePipelineKey.Create(NifAlphaRenderMode.Opaque, 0, 0,
                    doubleSided, classicSkin);
                Assert.False(opaque.Blended);
                Assert.Equal(doubleSided, opaque.DoubleSided);
                Assert.Equal(classicSkin, opaque.ClassicSkin);
                Assert.InRange(opaque.Slot, 0, GpuSpritePipelineKey.Capacity - 1);
                var opaqueState = (false, (Blend?)null, (Blend?)null, cull, classicSkin);
                Assert.True(slotsByState.TryAdd(opaqueState, opaque.Slot));
                Assert.True(statesBySlot.TryAdd(opaque.Slot, opaqueState));

                for (byte source = 0; source <= 10; source++)
                {
                    for (byte destination = 0; destination <= 10; destination++)
                    {
                        var key = GpuSpritePipelineKey.Create(NifAlphaRenderMode.Blend, source, destination,
                            doubleSided, classicSkin);
                        var state = (true, (Blend?)NifD3D12BlendMapper.ResolveBlendFactor(source),
                            (Blend?)NifD3D12BlendMapper.ResolveBlendFactor(destination), cull, classicSkin);
                        Assert.True(key.Blended);
                        Assert.Equal(doubleSided, key.DoubleSided);
                        Assert.Equal(classicSkin, key.ClassicSkin);
                        Assert.InRange(key.Slot, 0, GpuSpritePipelineKey.Capacity - 1);
                        Assert.True(slotsByState.TryAdd(state, key.Slot));
                        Assert.True(statesBySlot.TryAdd(key.Slot, state));
                    }
                }
            }
        }

        Assert.Equal(488, slotsByState.Count);
        Assert.Equal(GpuSpritePipelineKey.Capacity, statesBySlot.Count);
        for (var source = 0; source <= byte.MaxValue; source++)
        {
            for (var destination = 0; destination <= byte.MaxValue; destination++)
            {
                foreach (var doubleSided in options)
                {
                    foreach (var classicSkin in options)
                    {
                        var key = GpuSpritePipelineKey.Create(NifAlphaRenderMode.Blend, (byte)source,
                            (byte)destination, doubleSided, classicSkin);
                        var state = (true, (Blend?)NifD3D12BlendMapper.ResolveBlendFactor((byte)source),
                            (Blend?)NifD3D12BlendMapper.ResolveBlendFactor((byte)destination),
                            doubleSided ? CullMode.None : CullMode.Back, classicSkin);
                        Assert.Equal(slotsByState[state], key.Slot);
                        Assert.Equal(state, statesBySlot[key.Slot]);
                    }
                }
            }
        }
    }

    /// <summary>Shares opaque native state for cutout and shader-driven coverage while preserving cull and shader choices.</summary>
    [Fact]
    public void NonBlendedModesIgnoreAllBlendBytesAndShareOnlyEquivalentNativeState()
    {
        NifAlphaRenderMode[] modes =
            [NifAlphaRenderMode.Opaque, NifAlphaRenderMode.Cutout, NifAlphaRenderMode.AlphaToCoverage];
        bool[] options = [false, true];
        foreach (var doubleSided in options)
        {
            foreach (var classicSkin in options)
            {
                var expected = GpuSpritePipelineKey.Create(NifAlphaRenderMode.Opaque, 0, 0,
                    doubleSided, classicSkin);
                foreach (var mode in modes)
                {
                    for (var source = 0; source <= byte.MaxValue; source++)
                    {
                        for (var destination = 0; destination <= byte.MaxValue; destination++)
                        {
                            var key = GpuSpritePipelineKey.Create(mode, (byte)source, (byte)destination,
                                doubleSided, classicSkin);
                            Assert.False(key.Blended);
                            Assert.Equal((byte)0, key.SourceBlend);
                            Assert.Equal((byte)0, key.DestinationBlend);
                            Assert.Equal(doubleSided, key.DoubleSided);
                            Assert.Equal(classicSkin, key.ClassicSkin);
                            Assert.Equal(expected.Slot, key.Slot);
                        }
                    }
                }
            }
        }
    }
}
