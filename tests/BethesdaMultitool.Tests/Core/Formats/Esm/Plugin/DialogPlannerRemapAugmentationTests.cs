using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     Guards the dialogue-remap regression: planner-allocated new QUST/NPC_ FormIDs must reach
///     the legacy remap DialogGrupBuilder resolves INFO QSTI/ANAM against, or proto dialogue is
///     dropped (Ulysses / Gomorrah-greeter "greets but no dialogue" under the ESM flag).
/// </summary>
public sealed class PlannerLegacyStateBridgeTests
{
    private static Func<uint, string?> TypeLookup(Dictionary<uint, string> emittedTypes)
    {
        return formId => emittedTypes.TryGetValue(formId, out var type) ? type : null;
    }

    [Fact]
    public void Merge_AddsPlannerAllocatedQuestAndNpc_WithTypes()
    {
        // Ulysses-class case: a proto quest + speaker NPC the planner emitted as new records.
        const uint questSource = 0x00133FDB, questEmitted = 0x01004482;
        const uint npcSource = 0x00118200, npcEmitted = 0x01005010;
        var sourceToEmitted = new Dictionary<uint, uint>
        {
            [questSource] = questEmitted,
            [npcSource] = npcEmitted
        };
        var emittedTypes = new Dictionary<uint, string>
        {
            [questEmitted] = "QUST",
            [npcEmitted] = "NPC_"
        };
        var sourceToAllocated = new Dictionary<uint, uint>();
        var sourceToAllocatedType = new Dictionary<uint, string>();

        PlannerLegacyStateBridge.Merge(
            sourceToEmitted, TypeLookup(emittedTypes), sourceToAllocated, sourceToAllocatedType);

        Assert.Equal(questEmitted, sourceToAllocated[questSource]);
        Assert.Equal("QUST", sourceToAllocatedType[questSource]);
        Assert.Equal(npcEmitted, sourceToAllocated[npcSource]);
        Assert.Equal("NPC_", sourceToAllocatedType[npcSource]);
    }

    [Fact]
    public void Merge_ExcludesDialAndInfo_OwnedByDialogGrupBuilder()
    {
        // The planner catalogs DIAL/INFO but never emits them (DialogGrupBuilder allocates the
        // real FormIDs). Merging the planner's orphan DIAL/INFO IDs would mis-resolve INFO PNAM
        // / topic links, so they must be excluded even when the type lookup resolves them.
        const uint dialSource = 0x00133000, dialEmitted = 0x01000900;
        const uint infoSource = 0x00133001, infoEmitted = 0x01000901;
        var sourceToEmitted = new Dictionary<uint, uint>
        {
            [dialSource] = dialEmitted,
            [infoSource] = infoEmitted
        };
        var emittedTypes = new Dictionary<uint, string>
        {
            [dialEmitted] = "DIAL",
            [infoEmitted] = "INFO"
        };
        var sourceToAllocated = new Dictionary<uint, uint>();
        var sourceToAllocatedType = new Dictionary<uint, string>();

        PlannerLegacyStateBridge.Merge(
            sourceToEmitted, TypeLookup(emittedTypes), sourceToAllocated, sourceToAllocatedType);

        Assert.Empty(sourceToAllocated);
        Assert.Empty(sourceToAllocatedType);
    }

    [Fact]
    public void Merge_SkipsAllocatedButUnemittedOrphans()
    {
        // Source was allocated a FormID the planner never actually emitted (type lookup null).
        const uint source = 0x00120000, orphanEmitted = 0x01000AAA;
        var sourceToEmitted = new Dictionary<uint, uint> { [source] = orphanEmitted };
        var sourceToAllocated = new Dictionary<uint, uint>();
        var sourceToAllocatedType = new Dictionary<uint, string>();

        PlannerLegacyStateBridge.Merge(
            sourceToEmitted, _ => null, sourceToAllocated, sourceToAllocatedType);

        Assert.Empty(sourceToAllocated);
        Assert.Empty(sourceToAllocatedType);
    }

    [Fact]
    public void Merge_DoesNotOverwriteLegacyTrackedSource()
    {
        // Legacy TrackNewRecordSourceAlias runs first for legacy-routed types; if a source is
        // already tracked, the legacy target wins (the planner did not emit it).
        const uint source = 0x00133FDB, legacyTarget = 0x01000111, plannerTarget = 0x01004482;
        var sourceToEmitted = new Dictionary<uint, uint> { [source] = plannerTarget };
        var emittedTypes = new Dictionary<uint, string> { [plannerTarget] = "QUST" };
        var sourceToAllocated = new Dictionary<uint, uint> { [source] = legacyTarget };
        var sourceToAllocatedType = new Dictionary<uint, string> { [source] = "QUST" };

        PlannerLegacyStateBridge.Merge(
            sourceToEmitted, TypeLookup(emittedTypes), sourceToAllocated, sourceToAllocatedType);

        Assert.Equal(legacyTarget, sourceToAllocated[source]);
    }

    [Fact]
    public void Merge_SkipsIdentityAndZeroSources()
    {
        const uint identity = 0x01002000, zeroTarget = 0x01002001;
        var sourceToEmitted = new Dictionary<uint, uint>
        {
            [identity] = identity, // source == emitted
            [0u] = zeroTarget // zero source
        };
        var emittedTypes = new Dictionary<uint, string>
        {
            [identity] = "WEAP",
            [zeroTarget] = "WEAP"
        };
        var sourceToAllocated = new Dictionary<uint, uint>();
        var sourceToAllocatedType = new Dictionary<uint, string>();

        PlannerLegacyStateBridge.Merge(
            sourceToEmitted, TypeLookup(emittedTypes), sourceToAllocated, sourceToAllocatedType);

        Assert.Empty(sourceToAllocated);
    }

    [Fact]
    public void RegisterEmittedNewRecords_TracksPlannerScript_ForLegacyScriValidation()
    {
        var records = new[]
        {
            new RecordPlan
            {
                Type = "SCPT",
                Disposition = RecordDisposition.New,
                FormId = 0x0100525E,
                SourceFormId = 0x000DA71A,
                References = ImmutableArray<
                    ResolvedRef>.Empty,
                ContainedBy = ImmutableArray<
                    RecordContainmentEdge>.Empty,
                Provenance = new PlanProvenance
                {
                    PolicyId = "test",
                    Reason = "test"
                }
            },
            new RecordPlan
            {
                Type = "INFO",
                Disposition = RecordDisposition.New,
                FormId = 0x01006000,
                References = ImmutableArray<
                    ResolvedRef>.Empty,
                ContainedBy = ImmutableArray<
                    RecordContainmentEdge>.Empty,
                Provenance = new PlanProvenance
                {
                    PolicyId = "test",
                    Reason = "test"
                }
            }
        };
        var tracked = new List<(string Type, uint FormId)>();

        PlannerLegacyStateBridge.RegisterEmittedNewRecords(
            records,
            new HashSet<string>(StringComparer.Ordinal),
            (type, formId) => tracked.Add((type, formId)));

        Assert.Equal(new[] { ("SCPT", 0x0100525Eu) }, tracked);
    }

    [Fact]
    public void RegisterEmittedNewRecords_ExcludesDiagnosticSkips()
    {
        var record = new RecordPlan
        {
            Type = "SCPT",
            Disposition = RecordDisposition.New,
            FormId = 0x0100525E,
            References = ImmutableArray<
                ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<
                RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance
            {
                PolicyId = "test",
                Reason = "test"
            }
        };
        var tracked = new List<(string Type, uint FormId)>();

        PlannerLegacyStateBridge.RegisterEmittedNewRecords(
            new[] { record },
            new HashSet<string>(StringComparer.Ordinal) { "SCPT" },
            (type, formId) => tracked.Add((type, formId)));

        Assert.Empty(tracked);
    }
}