using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Nif;
using Slfx77.Multitool.Core.Models;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     The console platform option (slice 10): absent, X360 stands in as Assumed with the evidence that no byte of
///     the file discriminates the consoles; <c>x360</c> and <c>ps3</c> (any case) are declared with a measured
///     (ReverseEngineered) color byte order; anything else is refused. The workflow's option bag carries the value
///     under <c>bmt.platform</c> only when one is given.
/// </summary>
public class NifPackedPlatformOptionTests
{
    [Fact]
    public void Absent_IsX360Assumed_WithTheEvidence()
    {
        var selection = NifPackedPlatformOption.Resolve(new Dictionary<string, string>());

        Assert.Equal(NifPackedPlatform.X360, selection.Platform);
        Assert.True(selection.IsAssumed);
        Assert.Equal("x360", selection.OptionValue);
        Assert.Equal("A,R,G,B", selection.ColorByteOrder);
        Assert.Equal(SceneValueProvenance.Assumed, selection.ColorByteOrderProvenance);
        Assert.Contains("bmt.platform not set", selection.Source);
    }

    /// <summary>Control: the PS3 rows differ from the default in platform, byte order and provenance.</summary>
    [Theory]
    [InlineData("ps3", NifPackedPlatform.Ps3, "A,G,B,R")]
    [InlineData("PS3", NifPackedPlatform.Ps3, "A,G,B,R")]
    [InlineData("x360", NifPackedPlatform.X360, "A,R,G,B")]
    [InlineData(" X360 ", NifPackedPlatform.X360, "A,R,G,B")]
    internal void Declared_IsNotAssumed_AndNamesTheOption(string value, NifPackedPlatform platform, string order)
    {
        var options = new Dictionary<string, string> { [BethesdaModelRegistration.PlatformOption] = value };

        var selection = NifPackedPlatformOption.Resolve(options);

        Assert.Equal(platform, selection.Platform);
        Assert.False(selection.IsAssumed);
        Assert.Equal(order, selection.ColorByteOrder);
        Assert.Equal(SceneValueProvenance.ReverseEngineered, selection.ColorByteOrderProvenance);
        Assert.Equal("bmt.platform=" + selection.OptionValue, selection.Source);
    }

    [Theory]
    [InlineData("wii")]
    [InlineData("xbox360")]
    [InlineData("pc")]
    public void OtherValues_AreRefused(string value)
    {
        var options = new Dictionary<string, string> { [BethesdaModelRegistration.PlatformOption] = value };

        var error = Assert.Throws<ArgumentException>(() => NifPackedPlatformOption.Resolve(options));

        Assert.Contains(value, error.Message);
        Assert.Contains("x360 or ps3", error.Message);
    }

    /// <summary>Control: without a platform the bag has no platform key, so the reader's default stays in force.</summary>
    [Fact]
    public void WorkflowOptions_CarryThePlatform_OnlyWhenGiven()
    {
        var with = BethesdaModelWorkflow.CreateAppOptions("fnv", platform: "ps3");
        Assert.Equal("ps3", with[BethesdaModelRegistration.PlatformOption]);
        Assert.Equal("fnv", with[BethesdaModelRegistration.GameOption]);

        var without = BethesdaModelWorkflow.CreateAppOptions("fnv");
        Assert.False(without.ContainsKey(BethesdaModelRegistration.PlatformOption));

        var blank = BethesdaModelWorkflow.CreateAppOptions(null, platform: " ");
        Assert.Empty(blank);
    }
}
