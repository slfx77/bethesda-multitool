using System.Numerics;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifMaterialFixtures;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     The eleven blend pairs measured in loose FNV NIFs (design section 6.2) read through the real reader and evaluated
///     with Shared's <see cref="SceneBlendEquation.Evaluate" />, against an analytic Direct3D blend written here
///     independently of the reader's factor table. The control swaps the two factors: a reader that did so fails every
///     asymmetric pair.
/// </summary>
public class NifModelBlendPairTests
{
    private const int One = 0;
    private const int Zero = 1;
    private const int SrcColor = 2;
    private const int InvSrcAlpha = 7;
    private const int SrcAlpha = 6;

    private static readonly Vector4 Source = new(0.8f, 0.4f, 0.2f, 0.6f);
    private static readonly Vector4 Destination = new(0.1f, 0.3f, 0.5f, 0.9f);

    /// <summary>The measured pairs (design section 6.2), source then destination AlphaFunction.</summary>
    public static TheoryData<int, int> MeasuredPairs => new()
    {
        { SrcAlpha, InvSrcAlpha },
        { SrcAlpha, One },
        { Zero, SrcColor },
        { One, One },
        { One, Zero },
        { SrcColor, One },
        { Zero, InvSrcAlpha },
        { SrcColor, SrcColor },
        { SrcAlpha, SrcColor },
        { SrcColor, Zero },
        { Zero, Zero }
    };

    [Theory]
    [MemberData(nameof(MeasuredPairs))]
    public void BlendPair_EvaluatesToTheAnalyticDirect3DResult(int source, int destination)
    {
        var bytes = Shape(34, [FirstExtraBlock],
            b => AddAlpha(b, NifTestBlockLayouts.AlphaFlags(true, source, destination), 0));
        var document = Read(bytes).Document;
        var blend = Assert.IsType<SceneBlendState>(MaterialOf(document).RenderState?.Blend);

        Assert.True(blend.Enabled);
        Assert.Equal(blend.ColorEquation, blend.AlphaEquation);
        Assert.Equal(SceneBlendOperation.Add, blend.ColorEquation.Operation);
        Assert.True(blend.ColorEquation.Clamp);
        var expected = Analytic(source, destination);
        AssertClose(expected, blend.Evaluate(Source, Destination));
        SceneValidation.ValidateStructure(document);

        // Control: the same factors exchanged. Every pair whose factors differ must then miss the analytic result.
        if (source != destination)
        {
            var swapped = new SceneBlendEquation(blend.ColorEquation.DestinationFactor,
                blend.ColorEquation.SourceFactor, SceneBlendOperation.Add, true);
            Assert.True(MaxDifference(expected, swapped.Evaluate(Source, Destination)) > 1e-3,
                $"swapping {source}/{destination} did not change the result");
        }
    }

    /// <summary>Alpha Blend (bit 0) clear keeps the equations but disables them; control: set, it blends.</summary>
    [Fact]
    public void BlendBitClear_DisablesTheRetainedEquations()
    {
        var disabled = Shape(34, [FirstExtraBlock],
            b => AddAlpha(b, NifTestBlockLayouts.AlphaFlags(false, SrcAlpha, InvSrcAlpha), 0));
        var enabled = Shape(34, [FirstExtraBlock],
            b => AddAlpha(b, NifTestBlockLayouts.AlphaFlags(true, SrcAlpha, InvSrcAlpha), 0));

        var off = MaterialOf(Read(disabled).Document).RenderState!;
        var on = MaterialOf(Read(enabled).Document).RenderState!;

        Assert.False(off.Blend!.Enabled);
        Assert.Equal(Source, off.Blend.Evaluate(Source, Destination));
        Assert.Null(off.DrawOrder);
        Assert.NotEqual(Source, on.Blend!.Evaluate(Source, Destination));
        Assert.Equal(SceneDrawSort.BackToFront, on.DrawOrder!.Sort);
    }

    /// <summary>No Sorter (bit 13) keeps authored order; control: clear, it sorts back to front.</summary>
    [Fact]
    public void NoSorter_KeepsAuthoredOrder()
    {
        var bytes = Shape(34, [FirstExtraBlock],
            b => AddAlpha(b, NifTestBlockLayouts.AlphaFlags(true, SrcAlpha, InvSrcAlpha, noSorter: true), 0));

        var order = MaterialOf(Read(bytes).Document).RenderState!.DrawOrder!;

        Assert.Equal(0, order.Priority);
        Assert.Equal(SceneDrawSort.Authored, order.Sort);
    }

    /// <summary>out = clamp(S * f(src) + D * f(dst)) per component, with Direct3D's factor definitions.</summary>
    private static Vector4 Analytic(int source, int destination)
    {
        var result = new float[4];
        for (var c = 0; c < 4; c++)
        {
            var value = Get(Source, c) * Factor(source, c) + Get(Destination, c) * Factor(destination, c);
            result[c] = (float)Math.Clamp(value, 0d, 1d);
        }

        return new Vector4(result[0], result[1], result[2], result[3]);
    }

    private static double Factor(int function, int component)
    {
        double s = Get(Source, component);
        double d = Get(Destination, component);
        double sa = Source.W;
        return function switch
        {
            One => 1,
            Zero => 0,
            SrcColor => s,
            3 => 1 - s,
            4 => d,
            5 => 1 - d,
            SrcAlpha => sa,
            InvSrcAlpha => 1 - sa,
            _ => throw new ArgumentOutOfRangeException(nameof(function))
        };
    }

    private static double Get(Vector4 value, int component)
    {
        return component switch
        {
            0 => value.X,
            1 => value.Y,
            2 => value.Z,
            _ => value.W
        };
    }

    private static void AssertClose(Vector4 expected, Vector4 actual)
    {
        Assert.True(MaxDifference(expected, actual) <= 1e-6, $"expected {expected}, got {actual}");
    }

    private static double MaxDifference(Vector4 a, Vector4 b)
    {
        var d = Vector4.Abs(a - b);
        return Math.Max(Math.Max(d.X, d.Y), Math.Max(d.Z, d.W));
    }
}
