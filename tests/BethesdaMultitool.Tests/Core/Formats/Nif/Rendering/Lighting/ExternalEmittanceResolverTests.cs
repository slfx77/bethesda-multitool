using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Lighting;

public sealed class ExternalEmittanceResolverTests
{
    [Fact]
    public void BuildIndex_DecodesRegionAndLittleEndianPackedLightRgb()
    {
        var index = ExternalEmittanceResolver.BuildIndex(
            [
                new RegionRecord
                {
                    FormId = 0x100,
                    EmittanceColorR = 255,
                    EmittanceColorG = 128,
                    EmittanceColorB = 0
                }
            ],
            [new LightRecord { FormId = 0x200, Color = 0x7f3f1fu }]);

        Assert.Equal(new Vector3(1f, 128f / 255f, 0f), index[0x100]);
        Assert.Equal(new Vector3(31f / 255f, 63f / 255f, 127f / 255f), index[0x200]);
    }

    [Fact]
    public void Modulation_UsesIndependentClampedLightingInfluence()
    {
        var color = new Vector3(0.2f, 0.4f, 0.8f);

        Assert.Equal(Vector3.One, ExternalEmittanceResolver.Modulation(color, 0f));
        Assert.Equal(color, ExternalEmittanceResolver.Modulation(color, 1f));
        Assert.Equal(new Vector3(0.6f, 0.7f, 0.9f),
            ExternalEmittanceResolver.Modulation(color, 0.5f));
        Assert.Equal(color, ExternalEmittanceResolver.Modulation(color, 2f));
    }
}