using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Models;

/// <summary>
///     A record recovered from a dump can exist twice — once as embedded ESM bytes, once as a live
///     heap struct, or twice as two heap snapshots — and the two fail in different places. These pin
///     the rules for combining them: fill what is missing, never overwrite what is there, and never
///     guess at a scalar whose zero could be real.
/// </summary>
public sealed class RecordModelUnionTests
{
    [Fact]
    public void Fill_TakesNullMembersFromTheOtherCapture()
    {
        var primary = new GlobalRecord { FormId = 7, EditorId = null };
        var secondary = new GlobalRecord { FormId = 7, EditorId = "TimeScale" };

        var merged = Assert.IsType<GlobalRecord>(RecordModelUnion.Fill(primary, secondary));

        Assert.Equal("TimeScale", merged.EditorId);
    }

    [Fact]
    public void Fill_NeverOverwritesAValueThePrimaryHad()
    {
        var primary = new GlobalRecord { FormId = 7, EditorId = "TimeScale" };
        var secondary = new GlobalRecord { FormId = 7, EditorId = "SomethingElse" };

        var merged = Assert.IsType<GlobalRecord>(RecordModelUnion.Fill(primary, secondary));

        Assert.Same(primary, merged); // nothing to fill, so no copy is even made
        Assert.Equal("TimeScale", merged.EditorId);
    }

    [Fact]
    public void Fill_LeavesNonNullableScalarsAlone()
    {
        // A zero here is a legitimate value, not "unset". Filling it would let a second capture
        // silently overwrite a real zero — which is exactly why the hand-written mergers encode
        // per-field `!= 0` rules instead of a general one.
        var primary = new GlobalRecord { FormId = 7, EditorId = "X", Value = 0f };
        var secondary = new GlobalRecord { FormId = 7, EditorId = "X", Value = 42f };

        var merged = Assert.IsType<GlobalRecord>(RecordModelUnion.Fill(primary, secondary));

        Assert.Equal(0f, merged.Value);
    }

    [Fact]
    public void Fill_TreatsAnEmptyCollectionAsUnset()
    {
        var primary = new GenericEsmRecord { FormId = 7, RecordType = "MSTT" };
        var secondary = new GenericEsmRecord
        {
            FormId = 7,
            RecordType = "MSTT",
            Fields = new Dictionary<string, object?> { ["DATA"] = (byte)1 }
        };

        var merged = Assert.IsType<GenericEsmRecord>(RecordModelUnion.Fill(primary, secondary));

        Assert.Single(merged.Fields);
    }

    [Fact]
    public void Fill_KeepsAPopulatedCollectionOverAnEmptyOne()
    {
        var primary = new GenericEsmRecord
        {
            FormId = 7,
            RecordType = "MSTT",
            Fields = new Dictionary<string, object?> { ["DATA"] = (byte)1 }
        };
        var secondary = new GenericEsmRecord { FormId = 7, RecordType = "MSTT" };

        var merged = Assert.IsType<GenericEsmRecord>(RecordModelUnion.Fill(primary, secondary));

        Assert.Single(merged.Fields);
    }

    [Fact]
    public void Fill_TakesACaptureOffsetOnlyWhenThePrimaryHasNone()
    {
        var primary = new GlobalRecord { FormId = 7, EditorId = "X", Offset = 0 };
        var secondary = new GlobalRecord { FormId = 7, EditorId = "X", Offset = 0x2000 };

        var merged = Assert.IsType<GlobalRecord>(RecordModelUnion.Fill(primary, secondary));
        Assert.Equal(0x2000, merged.Offset);

        // ...and never replaces one it already has.
        var kept = Assert.IsType<GlobalRecord>(RecordModelUnion.Fill(merged, primary with { Offset = 0x9000 }));
        Assert.Equal(0x2000, kept.Offset);
    }

    [Fact]
    public void Fill_OrsIsBigEndianRatherThanFillingIt()
    {
        var primary = new GlobalRecord { FormId = 7, EditorId = "X", IsBigEndian = false };
        var secondary = new GlobalRecord { FormId = 7, EditorId = "X", IsBigEndian = true };

        Assert.True(Assert.IsType<GlobalRecord>(RecordModelUnion.Fill(primary, secondary)).IsBigEndian);
    }

    [Fact]
    public void Fill_RefusesMismatchedTypes()
    {
        var primary = new GlobalRecord { FormId = 7 };
        var secondary = new GenericEsmRecord { FormId = 7, RecordType = "MSTT" };

        Assert.Same(primary, RecordModelUnion.Fill(primary, secondary));
    }

    [Fact]
    public void Score_RanksAResolvedEditorIdAboveAnAnonymousCapture()
    {
        var anonymous = new GenericEsmRecord { FormId = 7, RecordType = "MSTT" };
        var named = anonymous with { EditorId = "ProtoMovableStatic" };

        Assert.True(RecordModelUnion.Score(named) > RecordModelUnion.Score(anonymous));
    }

    [Fact]
    public void Score_CountsEveryPopulatedMember()
    {
        var sparse = new GenericEsmRecord { FormId = 7, RecordType = "MSTT", EditorId = "X" };
        var rich = sparse with { FullName = "Movable", ModelPath = @"clutter\thing.nif" };

        Assert.True(RecordModelUnion.Score(rich) > RecordModelUnion.Score(sparse));
    }
}
