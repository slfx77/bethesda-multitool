using FalloutXbox360Utils.Core.Formats.Esm.Planner.Parity;
using FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.Parity;

/// <summary>
///     Tier 6.6 aggregate parity sweep: exercises every record type in
///     <see cref="PlannedEncoders.KnownRecordTypes" /> through the same one-record
///     <c>EmitPlan</c> → <c>PlanWriter.BuildGrupForType</c> → byte-compare-to-legacy path
///     the per-tier parity tests use, asserting that the registered encoder coverage stays
///     byte-exact (modulo entries registered in <see cref="MigrationDeltaRegistry.Default" />).
/// </summary>
/// <remarks>
///     The cell-children types (CELL/REFR/ACHR/ACRE) are skipped — they're exercised by
///     <c>PlanCellSectionBuilderParityTests</c> instead because they emit through the
///     cell-hierarchy framing, not the top-level GRUP path.
/// </remarks>
public sealed class AggregatePlannerParityTests
{
    [Fact]
    public void Every_Registered_Planner_Encoder_Is_Byte_Equal_To_Legacy()
    {
        var deltaRegistry = MigrationDeltaRegistry.Default;
        var failures = new List<string>();

        foreach (var recordType in PlannedEncoders.KnownRecordTypes())
        {
            if (SyntheticModelFactory.SkippedRecordTypes.Contains(recordType))
            {
                continue;
            }

            try
            {
                var model = SyntheticModelFactory.CreateModel(recordType);
                var legacy = SyntheticModelFactory.InvokeLegacyEncodeNew(recordType, model);
                PlannerTier1ParityHelper.AssertNewRecordParity(
                    recordType, SyntheticModelFactory.TestFormId, model, legacy);
            }
            catch (Xunit.Sdk.XunitException) when (deltaRegistry.IsTolerated(recordType, SyntheticModelFactory.TestFormId))
            {
                // Registered intentional diff for this record type — pass.
            }
            catch (Exception ex)
            {
                failures.Add($"{recordType}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.True(
            failures.Count == 0,
            "Aggregate planner parity sweep produced unregistered diffs:\n  - "
                + string.Join("\n  - ", failures));
    }

    [Fact]
    public void Aggregate_Sweep_Covers_Every_Known_Record_Type()
    {
        // Pin coverage: the harness exercises every entry in KnownRecordTypes except the
        // documented cell-children skip set. If a new encoder ships and isn't routed to
        // the cell-section harness, the aggregate sweep must include it.
        var known = PlannedEncoders.KnownRecordTypes().ToHashSet(StringComparer.Ordinal);
        var skipped = SyntheticModelFactory.SkippedRecordTypes;
        var exercised = known.Except(skipped).ToList();

        Assert.NotEmpty(exercised);
        foreach (var recordType in skipped)
        {
            Assert.True(
                known.Contains(recordType),
                $"Skip set lists '{recordType}' but no encoder is registered for it. Remove from skip set.");
        }
    }
}
