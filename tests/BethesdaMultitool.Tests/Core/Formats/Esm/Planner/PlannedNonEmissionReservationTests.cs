using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Disposition;
using BethesdaMultitool.Core.Formats.Esm.Planner.Disposition.Policies;
using BethesdaMultitool.Core.Formats.Esm.Planner.References;
using BethesdaMultitool.Core.Formats.Esm.Planner.References.Walkers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner;

public sealed class PlannedNonEmissionReservationTests
{
    [Fact]
    public void NewAvifReservations_PreserveLaterAllocationOrdinalsWithoutBecomingLive()
    {
        const uint containerSource = 0x00F00020;
        var avifs = Enumerable.Range(0, 13)
            .Select(index => new ActorValueInfoRecord
            {
                FormId = 0x00F00001u + (uint)index,
                EditorId = $"ProtoActorValue{index}"
            })
            .ToList();
        var records = new RecordCollection
        {
            ActorValueInfos = avifs,
            Containers = [new ContainerRecord { FormId = containerSource, EditorId = "AfterAvif" }]
        };

        var plan = BuildPlanner().Build(
            [], records, new HashSet<string> { "AVIF", "CONT" }, new HashSet<uint>(), null);

        Assert.Equal(13, plan.FormIdReservations.Length);
        Assert.Equal(
            Enumerable.Range(0, 13).Select(index => 0x01000800u + (uint)index),
            plan.FormIdReservations.Select(static reservation => reservation.FormId));
        Assert.All(plan.FormIdReservations, reservation =>
        {
            Assert.Equal("AVIF", reservation.RecordType);
            Assert.DoesNotContain(reservation.SourceFormId, plan.SourceToEmittedFormId.Keys);
            Assert.DoesNotContain(reservation.FormId, plan.EmittedFormIds);
        });

        var container = Assert.Single(plan.Records);
        Assert.Equal("CONT", container.Type);
        Assert.Equal(0x0100080Du, container.FormId);
        Assert.Equal(container.FormId, plan.SourceToEmittedFormId[containerSource]);
        Assert.Equal(0x80Eu, plan.Meta.NextObjectId);
    }

    [Fact]
    public void ScriptReferenceToReservedAvif_IsUnresolvedAndSuppressesDependentScript()
    {
        const uint avifSource = 0x00ABCDEF;
        const uint scriptSource = 0x00ABCDE0;
        var records = new RecordCollection
        {
            ActorValueInfos =
            [
                new ActorValueInfoRecord { FormId = avifSource, EditorId = "ProtoActorValue" }
            ],
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = scriptSource,
                    EditorId = "ReferencesProtoActorValue",
                    ReferencedObjects = [avifSource]
                }
            ]
        };

        var plan = BuildPlanner(true).Build(
            [], records, new HashSet<string> { "AVIF", "SCPT" }, new HashSet<uint>(), null);

        var reservation = Assert.Single(plan.FormIdReservations);
        Assert.Equal(avifSource, reservation.SourceFormId);
        Assert.Empty(plan.Records);
        Assert.DoesNotContain(avifSource, plan.SourceToEmittedFormId.Keys);
        Assert.DoesNotContain(scriptSource, plan.SourceToEmittedFormId.Keys);
        Assert.DoesNotContain(reservation.FormId, plan.EmittedFormIds);
        Assert.Contains(plan.Diagnostics, diagnostic =>
            diagnostic.Code == "script.suppress-unsafe-reference-table"
            && diagnostic.Message.Contains($"0x{avifSource:X8}", StringComparison.Ordinal));
    }

    [Fact]
    public void ReservationFailsFastWhenAnotherNewTypeSharesTheSourceAllocationKey()
    {
        const uint sharedSource = 0x00F00001;
        var records = new RecordCollection
        {
            ActorValueInfos =
            [
                new ActorValueInfoRecord { FormId = sharedSource, EditorId = "ProtoActorValue" }
            ],
            Containers =
            [
                new ContainerRecord { FormId = sharedSource, EditorId = "CollidingContainer" }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            BuildPlanner().Build(
                [], records, new HashSet<string> { "AVIF", "CONT" }, new HashSet<uint>(), null));

        Assert.Contains("AVIF", exception.Message, StringComparison.Ordinal);
        Assert.Contains("CONT", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"0x{sharedSource:X8}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("same source-keyed allocation", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReservationFailsFastWhenAvifSourceIsIndependentlyMasterLive()
    {
        const uint avifSource = 0x00001234;
        const uint scriptSource = 0x00ABCDE0;
        var records = new RecordCollection
        {
            ActorValueInfos =
            [
                new ActorValueInfoRecord { FormId = avifSource, EditorId = "ProtoActorValue" }
            ],
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = scriptSource,
                    EditorId = "WouldBindToRawSourceFallback",
                    ReferencedObjects = [avifSource]
                }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            BuildPlanner(true).Build(
                [],
                records,
                new HashSet<string> { "AVIF", "SCPT" },
                new HashSet<uint> { avifSource },
                null));

        Assert.Contains("AVIF", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"0x{avifSource:X8}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("independently live", exception.Message, StringComparison.Ordinal);
        Assert.Contains("raw source identity", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NewScolWithOnlyUnreachableParts_IsReservedBeforeWriterDispatch()
    {
        const uint scolSource = 0x00F10000;
        var records = new RecordCollection
        {
            StaticCollections =
            [
                new StaticCollectionRecord
                {
                    FormId = scolSource,
                    EditorId = "UnrenderableCollection",
                    Parts =
                    [
                        new StaticCollectionPart { OnamFormId = 0x00DEAD00 }
                    ]
                }
            ]
        };

        var plan = BuildPlanner().Build(
            [], records, new HashSet<string> { "SCOL" }, new HashSet<uint>(), null);

        var reservation = Assert.Single(plan.FormIdReservations);
        Assert.Equal("SCOL", reservation.RecordType);
        Assert.Equal(scolSource, reservation.SourceFormId);
        Assert.Equal(0x01000800u, reservation.FormId);
        Assert.Empty(plan.Records);
        Assert.DoesNotContain(scolSource, plan.SourceToEmittedFormId.Keys);
        Assert.DoesNotContain(reservation.FormId, plan.EmittedFormIds);
        Assert.Contains(plan.Diagnostics, diagnostic =>
            diagnostic.Code == "allocation.reserve.scol-no-renderable-content");
    }

    [Fact]
    public void NewScolWithMasterReachablePart_RemainsLive()
    {
        const uint masterStat = 0x00001234;
        const uint scolSource = 0x00F10000;
        var records = new RecordCollection
        {
            StaticCollections =
            [
                new StaticCollectionRecord
                {
                    FormId = scolSource,
                    EditorId = "RenderableCollection",
                    Parts =
                    [
                        new StaticCollectionPart { OnamFormId = masterStat }
                    ]
                }
            ]
        };

        var plan = BuildPlanner().Build(
            [], records, new HashSet<string> { "SCOL" }, new HashSet<uint> { masterStat }, null);

        var scol = Assert.Single(plan.Records);
        Assert.Equal("SCOL", scol.Type);
        Assert.Empty(plan.FormIdReservations);
        Assert.Equal(scol.FormId, plan.SourceToEmittedFormId[scolSource]);
        Assert.Contains(scol.FormId, plan.EmittedFormIds);
    }

    [Fact]
    public void NewScolPartTargetingNewStat_ResolvesThroughSourceAliasAndEmits()
    {
        const uint statSource = 0x00F20000;
        const uint scolSource = 0x00F10000;
        var records = new RecordCollection
        {
            Statics = [new StaticRecord { FormId = statSource, EditorId = "ProtoPart" }],
            StaticCollections =
            [
                new StaticCollectionRecord
                {
                    FormId = scolSource,
                    EditorId = "CollectionOfProtoPart",
                    Parts =
                    [
                        new StaticCollectionPart { OnamFormId = statSource }
                    ]
                }
            ]
        };

        var plan = BuildPlanner().Build(
            [], records, new HashSet<string> { "SCOL", "STAT" }, new HashSet<uint>(), null);

        Assert.Empty(plan.FormIdReservations);
        Assert.Contains(plan.Records, record => record.Type == "SCOL");
        Assert.Contains(plan.Records, record => record.Type == "STAT");
        Assert.NotEqual(statSource, plan.SourceToEmittedFormId[statSource]);
        Assert.NotEmpty(new PlanWriter(PlannedEncoders.BuildRegistry()).BuildGrupForType(
            "SCOL", plan, new PluginBuildOptions { CompressRecords = false }));
    }

    private static EsmPlanner BuildPlanner(bool includeScriptWalker = false)
    {
        var disposition = new DispositionEngine(
        [
            new ScriptDispositionPolicy(),
            new DefaultDispositionPolicy()
        ]);
        var degradation = new DegradationPolicy();
        degradation.SetDefaultForType("SCPT", DanglingAction.DropSubrecord);
        var walkers = includeScriptWalker
            ? new IRecordReferenceWalker[] { new ScriptReferenceWalker() }
            : [];
        return new EsmPlanner(
            disposition,
            new FormIdAllocator(),
            new ReferenceResolver(walkers, degradation));
    }
}