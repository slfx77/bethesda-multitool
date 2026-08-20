using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Planner.Disposition;
using BethesdaMultitool.Core.Formats.Esm.Planner.Disposition.Policies;
using BethesdaMultitool.Core.Formats.Esm.Planner.References;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin.Pipeline;

public sealed class NewWorldspaceFormIdReservationPlannerTests
{
    [Fact]
    public void Reserve_PreservesEligibleSlotsInDmpWorldspaceOrder()
    {
        const uint masterWorldspace = 0x0000003C;
        const uint aliasedWorldspace = 0x00F0FF01;
        const uint noChildWorldspace = 0x00F0FF02;
        var candidateIds = Enumerable.Range(0, 8)
            .Select(index => 0x00F00001u + (uint)index)
            .ToArray();
        var worldspaces = new List<WorldspaceRecord>
        {
            NewWorldspace(candidateIds[0]),
            NewWorldspace(masterWorldspace),
            NewWorldspace(candidateIds[1]),
            NewWorldspace(aliasedWorldspace),
            NewWorldspace(candidateIds[2]),
            NewWorldspace(noChildWorldspace)
        };
        worldspaces.AddRange(candidateIds.Skip(3).Select(NewWorldspace));
        var cells = candidateIds
            .Select((worldspaceFormId, index) => NewCell(0x00E00001u + (uint)index, worldspaceFormId))
            .Append(NewCell(0x00E0FF01, masterWorldspace))
            .Append(NewCell(0x00E0FF02, aliasedWorldspace))
            .ToList();
        var records = new RecordCollection { Worldspaces = worldspaces, Cells = cells };
        var aliases = new Dictionary<uint, uint> { [aliasedWorldspace] = masterWorldspace };
        var allocator = new FormIdAllocator();

        var reservations = NewWorldspaceFormIdReservationPlanner.Reserve(
            records,
            new NewVsOverrideClassifier([masterWorldspace]),
            allocator,
            aliases);

        Assert.Equal(candidateIds, reservations.Select(static reservation => reservation.SourceFormId));
        Assert.Equal(
            Enumerable.Range(0, 8).Select(index => 0x01000800u + (uint)index),
            reservations.Select(static reservation => reservation.FormId));
        Assert.All(reservations, reservation =>
        {
            Assert.Equal("WRLD", reservation.RecordType);
            Assert.Equal(NewWorldspaceFormIdReservationPlanner.PolicyId, reservation.PolicyId);
        });
        Assert.Equal(0x808u, allocator.NextObjectId);
        Assert.Equal(masterWorldspace, aliases[aliasedWorldspace]);
        Assert.Single(aliases);
    }

    [Fact]
    public void ReservedLegacySlot_IsDistinctFromLivePlannerWorldspaceAllocation()
    {
        const uint worldspaceSource = 0x00F00001;
        const uint cellSource = 0x00E00001;
        var records = new RecordCollection
        {
            Worldspaces = [NewWorldspace(worldspaceSource)],
            Cells = [NewCell(cellSource, worldspaceSource)]
        };
        var allocator = new FormIdAllocator();
        var reservations = NewWorldspaceFormIdReservationPlanner.Reserve(
            records,
            new NewVsOverrideClassifier([]),
            allocator,
            new Dictionary<uint, uint>());
        var masterIndex = MasterRecordIndex.Build([], []);
        var planner = new EsmPlanner(
            new DispositionEngine([new DefaultDispositionPolicy()]),
            allocator,
            new ReferenceResolver([], new DegradationPolicy()));

        var plan = planner.Build(
            [],
            records,
            new HashSet<string> { "CELL" },
            new HashSet<uint>(),
            null,
            new Dictionary<uint, PcEsmCellContext>(),
            new Dictionary<uint, ParsedMainRecord>(),
            allocator,
            masterRefFormIds: new HashSet<uint>(),
            cellVerdictInputs: new CellVerdictInputs { MasterIndex = masterIndex });
        plan = plan with
        {
            FormIdReservations = reservations.AddRange(plan.FormIdReservations)
        };

        var reservation = Assert.Single(plan.FormIdReservations);
        var liveWorldspace = Assert.Single(plan.WorldspacesByFormId.Values);
        Assert.Equal(0x01000800u, reservation.FormId);
        Assert.NotEqual(reservation.FormId, liveWorldspace.WorldspaceFormId);
        Assert.Equal(liveWorldspace.WorldspaceFormId, plan.SourceToEmittedFormId[worldspaceSource]);
        Assert.Contains(liveWorldspace.WorldspaceFormId, plan.EmittedFormIds);
        Assert.DoesNotContain(reservation.FormId, plan.EmittedFormIds);
        Assert.Equal(0x803u, plan.Meta.NextObjectId);
        Assert.DoesNotContain(reservation.SourceFormId, plan.SourceToEmittedFormId
            .Where(pair => pair.Value == reservation.FormId)
            .Select(pair => pair.Key));
    }

    private static WorldspaceRecord NewWorldspace(uint formId)
    {
        return new WorldspaceRecord { FormId = formId, EditorId = $"World{formId:X8}" };
    }

    private static CellRecord NewCell(uint formId, uint worldspaceFormId)
    {
        return new CellRecord
        {
            FormId = formId,
            EditorId = $"Cell{formId:X8}",
            GridX = 0,
            GridY = 0,
            WorldspaceFormId = worldspaceFormId
        };
    }
}