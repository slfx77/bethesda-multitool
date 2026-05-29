using System.Collections.Immutable;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Parity;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.Parity;

public sealed class MigrationDeltaRegistryTests
{
    [Fact]
    public void Default_Registry_Is_Empty_At_Tier_6_6_Baseline()
    {
        // Tier 6.6 baseline mirrors migration-deltas.md: "Open deltas: _None recorded yet_".
        // When a real delta lands the markdown grows a DELTA-NNN section and this assertion
        // gets updated alongside it.
        Assert.Empty(MigrationDeltaRegistry.Default.Deltas);
    }

    [Fact]
    public void IsTolerated_Returns_False_On_Empty_Registry()
    {
        Assert.False(MigrationDeltaRegistry.Default.IsTolerated("SCPT"));
        Assert.False(MigrationDeltaRegistry.Default.IsTolerated("SCPT", 0x0014DA58u));
    }

    [Fact]
    public void IsTolerated_Returns_False_For_Empty_Record_Type()
    {
        Assert.False(MigrationDeltaRegistry.Default.IsTolerated(""));
    }

    [Fact]
    public void IsTolerated_Matches_When_Record_Type_Listed_Without_Scope()
    {
        var registry = MakeRegistry(new MigrationDelta
        {
            Id = "DELTA-TEST",
            Tier = "6.6",
            RecordTypes = ImmutableHashSet.Create("SCPT", "INFO"),
            Reason = "Test entry",
            ApprovalDate = new DateOnly(2026, 5, 29),
        });

        Assert.True(registry.IsTolerated("SCPT"));
        Assert.True(registry.IsTolerated("INFO", 0x000A0001u));
        Assert.False(registry.IsTolerated("WEAP"));
    }

    [Fact]
    public void IsTolerated_Respects_Form_Id_Scope_Predicate()
    {
        var registry = MakeRegistry(new MigrationDelta
        {
            Id = "DELTA-TEST",
            Tier = "6.6",
            RecordTypes = ImmutableHashSet.Create("SCPT"),
            Reason = "Only the proto VCG tutorial script",
            ApprovalDate = new DateOnly(2026, 5, 29),
            FormIdScope = id => id == 0x0014DA58u,
        });

        Assert.True(registry.IsTolerated("SCPT", 0x0014DA58u));
        Assert.False(registry.IsTolerated("SCPT", 0x0014DA59u));
        // No FormID supplied → can't satisfy a scoped predicate.
        Assert.False(registry.IsTolerated("SCPT"));
    }

    private static MigrationDeltaRegistry MakeRegistry(params MigrationDelta[] deltas) =>
        new(ImmutableArray.Create(deltas));
}
