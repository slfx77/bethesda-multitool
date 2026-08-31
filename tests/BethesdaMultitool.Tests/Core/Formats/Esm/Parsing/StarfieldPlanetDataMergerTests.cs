using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using Xunit;
using static BethesdaMultitool.Tests.Core.Formats.Esm.Parsing.StarfieldPlanetDataTestData;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class StarfieldPlanetDataMergerTests
{
    [Fact]
    public void Merge_FoldsOrderedDeltasAndRetainsAuthoredListOrder()
    {
        var a = new StarfieldPlanetWorldspaceEntry(1d, 10d, 0x100);
        var b = new StarfieldPlanetWorldspaceEntry(2d, 20d, 0x200);
        var c = new StarfieldPlanetWorldspaceEntry(3d, 30d, 0x300);
        var d = new StarfieldPlanetWorldspaceEntry(4d, 40d, 0x400);

        var result = StarfieldPlanetDataMerger.Merge(
        [
            Master(a, b, c),
            Override(
                new(b, StarfieldPlanetWorldspaceOperation.Removed),
                new(d, StarfieldPlanetWorldspaceOperation.Added)),
            Override(
                new(a, StarfieldPlanetWorldspaceOperation.Removed),
                new(b, StarfieldPlanetWorldspaceOperation.Added))
        ]);

        Assert.True(result.IsResolved, result.FailureDetail);
        Assert.Equal(StarfieldPlanetDataMergeStatus.Resolved, result.Status);
        Assert.Equal([c, d, b], result.EffectiveWorldspaces);
    }

    [Fact]
    public void Merge_RemoveThenAddSameTuple_IsValidAndMovesItToAuthoredTail()
    {
        var earth = new StarfieldPlanetWorldspaceEntry(29.7604d, -95.3698d, 0x100);
        var moon = new StarfieldPlanetWorldspaceEntry(0d, 0d, 0x200);

        var result = StarfieldPlanetDataMerger.Merge(
        [
            Master(earth, moon),
            Override(
                new(earth, StarfieldPlanetWorldspaceOperation.Removed),
                new(earth, StarfieldPlanetWorldspaceOperation.Added))
        ]);

        Assert.True(result.IsResolved, result.FailureDetail);
        Assert.Equal([moon, earth], result.EffectiveWorldspaces);
    }

    [Fact]
    public void Merge_UsesExactCoordinateBitsAndWorldspaceFormIdAsIdentity()
    {
        var positiveZero = new StarfieldPlanetWorldspaceEntry(0d, 1d, 0x100);
        var negativeZero = new StarfieldPlanetWorldspaceEntry(
            BitConverter.Int64BitsToDouble(unchecked((long)0x8000000000000000UL)),
            1d,
            0x100);
        var differentWorldspace = new StarfieldPlanetWorldspaceEntry(0d, 1d, 0x200);

        var result = StarfieldPlanetDataMerger.Merge(
        [
            Master(positiveZero),
            Override(
                new(negativeZero, StarfieldPlanetWorldspaceOperation.Added),
                new(differentWorldspace, StarfieldPlanetWorldspaceOperation.Added))
        ]);

        Assert.NotEqual(positiveZero.LatitudeRawBits, negativeZero.LatitudeRawBits);
        Assert.True(result.IsResolved, result.FailureDetail);
        Assert.Equal([positiveZero, negativeZero, differentWorldspace], result.EffectiveWorldspaces);

        var removeOne = StarfieldPlanetDataMerger.Merge(
        [
            Master(positiveZero, differentWorldspace),
            Override(new StarfieldPlanetWorldspaceDelta(positiveZero, StarfieldPlanetWorldspaceOperation.Removed))
        ]);
        Assert.True(removeOne.IsResolved, removeOne.FailureDetail);
        Assert.Equal([differentWorldspace], removeOne.EffectiveWorldspaces);
    }

    [Fact]
    public void Merge_FailsClosedOnDeltaWithoutBaseOrMissingMaster()
    {
        var entry = new StarfieldPlanetWorldspaceEntry(1d, 2d, 3);

        AssertFailure(
            StarfieldPlanetDataMerger.Merge(
            [
                Override(new StarfieldPlanetWorldspaceDelta(entry, StarfieldPlanetWorldspaceOperation.Added))
            ]),
            StarfieldPlanetDataMergeStatus.DeltaWithoutBase,
            0);
        AssertFailure(
            StarfieldPlanetDataMerger.Merge([]),
            StarfieldPlanetDataMergeStatus.MissingMaster,
            null);
    }

    [Fact]
    public void Merge_FailsClosedOnUnmatchedRemoval()
    {
        var existing = new StarfieldPlanetWorldspaceEntry(1d, 2d, 3);
        var absent = new StarfieldPlanetWorldspaceEntry(4d, 5d, 6);

        var result = StarfieldPlanetDataMerger.Merge(
        [
            Master(existing),
            Override(new StarfieldPlanetWorldspaceDelta(absent, StarfieldPlanetWorldspaceOperation.Removed))
        ]);

        AssertFailure(result, StarfieldPlanetDataMergeStatus.UnmatchedRemoval, 1, 0);
    }

    [Fact]
    public void Merge_FailsClosedOnConflictingExactAddition()
    {
        var existing = new StarfieldPlanetWorldspaceEntry(1d, 2d, 3);

        var result = StarfieldPlanetDataMerger.Merge(
        [
            Master(existing),
            Override(new StarfieldPlanetWorldspaceDelta(existing, StarfieldPlanetWorldspaceOperation.Added))
        ]);

        AssertFailure(result, StarfieldPlanetDataMergeStatus.ConflictingAddition, 1, 0);
    }

    [Fact]
    public void Merge_FailsClosedOnDuplicateMasterRecordOrDuplicateMasterTuple()
    {
        var entry = new StarfieldPlanetWorldspaceEntry(1d, 2d, 3);

        AssertFailure(
            StarfieldPlanetDataMerger.Merge([Master(entry), Master(entry)]),
            StarfieldPlanetDataMergeStatus.DuplicateMasterAmbiguity,
            1);
        AssertFailure(
            StarfieldPlanetDataMerger.Merge([Master(entry, entry)]),
            StarfieldPlanetDataMergeStatus.DuplicateMasterTupleAmbiguity,
            0,
            1);
    }

    [Fact]
    public void Merge_FailsClosedOnDecoderFailureAndManualRecordInconsistency()
    {
        var entry = new StarfieldPlanetWorldspaceEntry(1d, 2d, 3);
        Assert.False(StarfieldPlanetDataDecoder.TryDecode(
            [0x01], false, out var malformed, out _));

        AssertFailure(
            StarfieldPlanetDataMerger.Merge([Master(entry), malformed]),
            StarfieldPlanetDataMergeStatus.MalformedRecord,
            1);

        var inconsistent = Master(entry) with
        {
            PayloadKind = StarfieldPlanetDataPayloadKind.Override
        };
        AssertFailure(
            StarfieldPlanetDataMerger.Merge([inconsistent]),
            StarfieldPlanetDataMergeStatus.MalformedRecord,
            0);

        var valid = Master(entry);
        var nonFiniteBody = valid.Body! with
        {
            Atmosphere = valid.Body!.Atmosphere with { UnknownFloat1 = float.NaN }
        };
        AssertFailure(
            StarfieldPlanetDataMerger.Merge([valid with { Body = nonFiniteBody }]),
            StarfieldPlanetDataMergeStatus.MalformedRecord,
            0);
    }

    [Fact]
    public void Merge_FailsClosedOnManuallyInjectedUnknownOperation()
    {
        var entry = new StarfieldPlanetWorldspaceEntry(1d, 2d, 3);
        var invalidOverride = Override() with
        {
            WorldspaceOverrides =
            [
                new StarfieldPlanetWorldspaceDelta(
                    entry,
                    (StarfieldPlanetWorldspaceOperation)0xFF)
            ]
        };

        AssertFailure(
            StarfieldPlanetDataMerger.Merge([Master(), invalidOverride]),
            StarfieldPlanetDataMergeStatus.MalformedRecord,
            1,
            0);
    }

    private static StarfieldPlanetDataRecord Master(
        params StarfieldPlanetWorldspaceEntry[] entries)
    {
        Assert.True(StarfieldPlanetDataDecoder.TryDecode(
            ValidMasterData(entries), false, out var record, out var error), error);
        return record;
    }

    private static StarfieldPlanetDataRecord Override(
        params StarfieldPlanetWorldspaceDelta[] deltas)
    {
        Assert.True(StarfieldPlanetDataDecoder.TryDecode(
            ValidOverrideData(deltas), false, out var record, out var error), error);
        return record;
    }

    private static void AssertFailure(
        StarfieldPlanetDataMergeResult result,
        StarfieldPlanetDataMergeStatus expectedStatus,
        int? expectedRecordIndex,
        int? expectedDeltaIndex = null)
    {
        Assert.False(result.IsResolved);
        Assert.Equal(expectedStatus, result.Status);
        Assert.Null(result.EffectiveWorldspaces);
        Assert.Equal(expectedRecordIndex, result.FailureRecordIndex);
        Assert.Equal(expectedDeltaIndex, result.FailureDeltaIndex);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureDetail));
    }
}
