using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

public sealed class QuestVariableProducerGateTests
{
    private const uint Quest = 0x00001000;
    private const uint TargetScript = 0x00002000;
    private const uint SourceScript = 0x01000001;

    [Fact]
    public void Apply_keeps_fresh_local_dependency_when_combined_info_result_script_writes_it()
    {
        var mapping = Mapping(7, 70);
        var augmentation = Augmentation(70);
        var records = new RecordCollection
        {
            Dialogues =
            [
                new DialogueRecord
                {
                    FormId = 0x01000002,
                    EditorId = "InfoOnlyProducer",
                    Conditions = [QuestVariable(70)],
                    ResultScripts = [CompiledScript(7)]
                }
            ]
        };

        var dialogueLedger = DialogueProducerLedger.FromCombinedOutput(
            CombinedOutput(records.Dialogues),
            [mapping],
            null);
        var result = QuestVariableProducerGate.Apply(
            records,
            [augmentation],
            [mapping],
            dialogueLedger.Evidence,
            new Dictionary<uint, ParsedMainRecord>(),
            null);

        var evidence = Assert.Single(dialogueLedger.Evidence);
        Assert.Equal("INFO", evidence.Owner.RecordType);
        Assert.Single(records.Dialogues);
        Assert.Equal(0, result.SuppressedInfoCount);
        Assert.Equal(augmentation, Assert.Single(result.ScriptVariableAugmentations));
        Assert.Equal(mapping, Assert.Single(result.VariableRecoveryMappings));
        Assert.Equal(evidence.Owner, Assert.Single(Assert.Single(result.ProducerRequirements).CandidateOwners));

        QuestVariableProducerGate.VerifyPlannerState(
            null,
            result.ProducerRequirements);
        QuestVariableProducerGate.VerifyFinalOutput(
            result.ProducerRequirements,
            PlanProducerEmissionLedger.Empty,
            dialogueLedger);
    }

    [Fact]
    public void Combined_dialogue_ledger_ignores_overlays_so_no_op_suppression_cannot_disturb_evidence()
    {
        // A1 (no-op overlay suppression) removes SharedInfoOverlays entries at plan time.
        // Producer evidence must be sourced from NewInfos ONLY, so an overlay that carries a
        // writer result script contributes nothing — with or without suppression.
        var mapping = Mapping(7, 70);
        var writerInfo = new DialogueRecord
        {
            FormId = 0x01000002,
            EditorId = "InfoOnlyProducer",
            Conditions = [QuestVariable(70)],
            ResultScripts = [CompiledScript(7)]
        };
        var overlayPrototype = new DialogueRecord
        {
            FormId = 0x00100050,
            EditorId = "OverlayWriterNeverCounts",
            ResultScripts = [CompiledScript(7)]
        };
        var overlay = new SharedDialogueInfoOverlay(
            overlayPrototype,
            new MasterDialogueInfo(
                new ParsedMainRecord
                {
                    Header = new MainRecordHeader { Signature = "INFO", FormId = 0x00100050 },
                    Offset = 0
                },
                0x000000D4,
                0,
                new HashSet<uint>()));
        var planWithOverlay = CombinedOutput([writerInfo]) with { SharedInfoOverlays = [overlay] };
        var planWithoutOverlay = CombinedOutput([writerInfo]);

        var evidenceWithOverlay = DialogueProducerLedger.FromCombinedOutput(
            planWithOverlay, [mapping], null).Evidence;
        var evidenceWithoutOverlay = DialogueProducerLedger.FromCombinedOutput(
            planWithoutOverlay, [mapping], null).Evidence;

        Assert.Single(evidenceWithOverlay);
        Assert.Single(evidenceWithoutOverlay);
        Assert.Equal(
            evidenceWithoutOverlay.Single().Owner,
            evidenceWithOverlay.Single().Owner);
    }

    [Fact]
    public void VerifyFinalOutput_rejects_info_candidate_that_was_not_emitted()
    {
        var mapping = Mapping(7, 70);
        var records = new RecordCollection
        {
            Dialogues =
            [
                new DialogueRecord
                {
                    FormId = 0x01000002,
                    EditorId = "OrphanProducer",
                    Conditions = [QuestVariable(70)],
                    ResultScripts = [CompiledScript(7)]
                }
            ]
        };
        var candidateLedger = DialogueProducerLedger.FromCombinedOutput(
            CombinedOutput(records.Dialogues),
            [mapping],
            null);
        var gate = QuestVariableProducerGate.Apply(
            records,
            [Augmentation(70)],
            [mapping],
            candidateLedger.Evidence,
            new Dictionary<uint, ParsedMainRecord>(),
            null);

        var error = Assert.Throws<InvalidOperationException>(() =>
            QuestVariableProducerGate.VerifyFinalOutput(
                gate.ProducerRequirements,
                PlanProducerEmissionLedger.Empty,
                DialogueProducerLedger.Empty));

        Assert.Contains("dialogue emission ledger", error.Message, StringComparison.Ordinal);
        Assert.Contains("ResultScripts[0]", error.Message, StringComparison.Ordinal);

        var candidate = Assert.Single(candidateLedger.Evidence);
        var wrongBundleLedger = new DialogueProducerLedger(
            [candidate with { Owner = candidate.Owner with { ScriptPath = "OrphanProducer/ResultScripts[1]" } }]);
        Assert.Throws<InvalidOperationException>(() =>
            QuestVariableProducerGate.VerifyFinalOutput(
                gate.ProducerRequirements,
                PlanProducerEmissionLedger.Empty,
                wrongBundleLedger));
    }

    [Fact]
    public void PlanWriter_records_exact_pack_script_owner_after_record_serialization()
    {
        var plan = PlanWithPackProducer(false);
        var writer = new PlanWriter(PlannedEncoders.BuildRegistry());
        var owner = new QuestVariableProducerOwner(
            "PACK",
            0x01000020,
            "LateDecliningProducer/OnBegin/script[0]");
        var requirement = new QuestVariableProducerRequirement(Augmentation(70), [owner]);

        var grup = writer.BuildGrupForType("PACK", plan, new PluginBuildOptions());

        Assert.NotEmpty(grup);
        Assert.Contains(owner, writer.ProducerLedger.Owners);
        QuestVariableProducerGate.VerifyFinalOutput(
            [requirement],
            writer.ProducerLedger,
            DialogueProducerLedger.Empty);

        var wrongPath = requirement with
        {
            CandidateOwners = [owner with { ScriptPath = "LateDecliningProducer/OnEnd/script[0]" }]
        };
        Assert.Throws<InvalidOperationException>(() =>
            QuestVariableProducerGate.VerifyFinalOutput(
                [wrongPath],
                writer.ProducerLedger,
                DialogueProducerLedger.Empty));
    }

    [Fact]
    public void VerifyFinalOutput_rejects_pack_that_late_declines_serialization()
    {
        var plan = PlanWithPackProducer(true);
        var writer = new PlanWriter(PlannedEncoders.BuildRegistry());
        var owner = new QuestVariableProducerOwner(
            "PACK",
            0x01000020,
            "LateDecliningProducer/OnBegin/script[0]");
        var requirement = new QuestVariableProducerRequirement(Augmentation(70), [owner]);

        // The producer is still present in the immutable plan, but PACK's atomic inline-script
        // gate declines the record after planning because the executable bundle is incomplete.
        QuestVariableProducerGate.VerifyFinalPlan(plan, [requirement]);
        var grup = writer.BuildGrupForType("PACK", plan, new PluginBuildOptions());

        Assert.Empty(grup);
        Assert.Empty(writer.ProducerLedger.Owners);
        var error = Assert.Throws<InvalidOperationException>(() =>
            QuestVariableProducerGate.VerifyFinalOutput(
                [requirement],
                writer.ProducerLedger,
                DialogueProducerLedger.Empty));
        Assert.Contains("actual PlanWriter/dialogue emission ledgers", error.Message, StringComparison.Ordinal);
        Assert.Contains("LateDecliningProducer/OnBegin/script[0]", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Combined_dialogue_ledger_rejects_unsafe_info_bundle_as_producer_evidence()
    {
        var mapping = Mapping(7, 70);
        var records = new RecordCollection
        {
            Dialogues =
            [
                new DialogueRecord
                {
                    FormId = 0x01000002,
                    EditorId = "UnsafeInfoProducer",
                    Conditions = [QuestVariable(70)],
                    ResultScripts =
                    [
                        CompiledScript(7),
                        new DialogueResultScript
                        {
                            IsIncompleteExecutableBundle = true
                        }
                    ]
                }
            ]
        };
        var ledger = DialogueProducerLedger.FromCombinedOutput(
            CombinedOutput(records.Dialogues),
            [mapping],
            null);

        var gate = QuestVariableProducerGate.Apply(
            records,
            [Augmentation(70)],
            [mapping],
            ledger.Evidence,
            new Dictionary<uint, ParsedMainRecord>(),
            null);

        Assert.Empty(ledger.Evidence);
        Assert.Empty(records.Dialogues);
        Assert.Empty(gate.ScriptVariableAugmentations);
        Assert.Empty(gate.ProducerRequirements);
    }

    [Fact]
    public void Apply_keeps_fresh_local_dependency_with_planner_owned_scpt_writer()
    {
        var mapping = Mapping(7, 70);
        var augmentation = Augmentation(70);
        var records = RecordsWithScptWriter(mapping.SourceVariable.Index, mapping.TargetVariable.Index);

        var evidence = FindEvidence(records, [mapping], "SCPT");
        var result = QuestVariableProducerGate.Apply(
            records,
            [augmentation],
            [mapping],
            evidence,
            new Dictionary<uint, ParsedMainRecord>(),
            null);

        var producer = Assert.Single(evidence);
        Assert.Equal("SCPT", producer.Owner.RecordType);
        Assert.Equal(SourceScript, producer.Owner.SourceFormId);
        Assert.Single(records.Dialogues);
        Assert.Equal(0, result.SuppressedInfoCount);
        Assert.Equal(augmentation, Assert.Single(result.ScriptVariableAugmentations));
        Assert.Equal(mapping, Assert.Single(result.VariableRecoveryMappings));
        Assert.Equal(SourceScript,
            Assert.Single(Assert.Single(result.ProducerRequirements).CandidateOwners).SourceFormId);
    }

    [Fact]
    public void Apply_does_not_require_producer_for_exact_existing_retail_local_remap()
    {
        var mapping = Mapping(7, 31);
        var records = new RecordCollection
        {
            Dialogues =
            [
                new DialogueRecord
                {
                    FormId = 0x01000003,
                    Conditions = [QuestVariable(31)]
                }
            ]
        };

        var result = QuestVariableProducerGate.Apply(
            records,
            [],
            [mapping],
            [],
            new Dictionary<uint, ParsedMainRecord>(),
            null);

        Assert.Single(records.Dialogues);
        Assert.Equal(mapping, Assert.Single(result.VariableRecoveryMappings));
        Assert.Empty(result.ProducerRequirements);
    }

    [Fact]
    public void Apply_gates_only_fresh_mapping_when_exact_and_fresh_dependencies_are_mixed()
    {
        var exact = Mapping(7, 31, "RetailState");
        var fresh = Mapping(8, 70, "PrototypeState");
        var records = new RecordCollection
        {
            Dialogues =
            [
                new DialogueRecord
                {
                    FormId = 0x01000004,
                    EditorId = "ExactRetailLocal",
                    Conditions = [QuestVariable(31)]
                },
                new DialogueRecord
                {
                    FormId = 0x01000005,
                    EditorId = "UnwrittenFreshLocal",
                    Conditions = [QuestVariable(70)]
                }
            ]
        };

        var result = QuestVariableProducerGate.Apply(
            records,
            [Augmentation(70, "PrototypeState")],
            [exact, fresh],
            [],
            new Dictionary<uint, ParsedMainRecord>(),
            null);

        Assert.Equal(0x01000004u, Assert.Single(records.Dialogues).FormId);
        Assert.Equal(1, result.SuppressedInfoCount);
        Assert.Equal(exact, Assert.Single(result.VariableRecoveryMappings));
        Assert.Empty(result.ScriptVariableAugmentations);
    }

    [Fact]
    public void Apply_never_suppresses_master_anchored_info()
    {
        const uint masterInfo = 0x00003000;
        var mapping = Mapping(7, 70);
        var records = new RecordCollection
        {
            Dialogues =
            [
                new DialogueRecord
                {
                    FormId = masterInfo,
                    Conditions = [QuestVariable(70)]
                }
            ]
        };
        var masterRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [masterInfo] = new()
            {
                Header = new MainRecordHeader { Signature = "INFO", FormId = masterInfo }
            }
        };

        var result = QuestVariableProducerGate.Apply(
            records,
            [Augmentation(70)],
            [mapping],
            [],
            masterRecords,
            null);

        Assert.Single(records.Dialogues);
        Assert.Equal(0, result.SuppressedInfoCount);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Apply_reaches_fixed_point_when_suppressed_pack_was_another_locals_only_producer()
    {
        var mappingA = Mapping(7, 70, "StateA");
        var mappingB = Mapping(8, 71, "StateB");
        var records = new RecordCollection
        {
            Dialogues =
            [
                new DialogueRecord
                {
                    FormId = 0x01000010,
                    Conditions = [QuestVariable(70)]
                }
            ],
            Packages =
            [
                new PackageRecord
                {
                    FormId = 0x01000011,
                    EditorId = "ProducerThatWillBeSuppressed",
                    Conditions = [QuestVariable(71)],
                    OnBegin = new PackageEventAction
                    {
                        Kind = PackageEventActionKind.OnBegin,
                        Scripts = [CompiledScript(7)]
                    }
                }
            ]
        };

        var evidence = FindEvidence(records, [mappingA, mappingB], "PACK");
        var result = QuestVariableProducerGate.Apply(
            records,
            [Augmentation(70, "StateA"), Augmentation(71, "StateB")],
            [mappingA, mappingB],
            evidence,
            new Dictionary<uint, ParsedMainRecord>(),
            null);

        Assert.Single(evidence);
        Assert.Empty(records.Packages);
        Assert.Empty(records.Dialogues);
        Assert.Equal(1, result.SuppressedPackageCount);
        Assert.Equal(1, result.SuppressedInfoCount);
        Assert.Empty(result.ScriptVariableAugmentations);
        Assert.Empty(result.ProducerRequirements);
    }

    [Fact]
    public void Apply_tracks_terminal_menu_producers_by_item_and_normalizes_paths_after_removal()
    {
        var mappingA = Mapping(7, 70, "StateA");
        var mappingB = Mapping(8, 71, "StateB");
        var mappingC = Mapping(9, 72, "StateC");
        var terminal = new TerminalRecord
        {
            FormId = 0x01000030,
            EditorId = "IndexedTerminalProducer",
            MenuItems =
            [
                new TerminalMenuItem
                {
                    Text = "Removed producer A",
                    Conditions = [QuestVariable(72)],
                    CompiledData = BuildExternalSet(7),
                    ReferencedObjects = [Quest]
                },
                new TerminalMenuItem
                {
                    Text = "Surviving producer B",
                    CompiledData = BuildExternalSet(8),
                    ReferencedObjects = [Quest]
                }
            ]
        };
        var records = new RecordCollection
        {
            Terminals = [terminal],
            Dialogues =
            [
                new DialogueRecord
                {
                    FormId = 0x01000031,
                    EditorId = "ConsumesA",
                    Conditions = [QuestVariable(70)]
                },
                new DialogueRecord
                {
                    FormId = 0x01000032,
                    EditorId = "ConsumesB",
                    Conditions = [QuestVariable(71)]
                }
            ]
        };
        var evidence = FindEvidence(records, [mappingA, mappingB, mappingC], "TERM");

        var result = QuestVariableProducerGate.Apply(
            records,
            [Augmentation(70, "StateA"), Augmentation(71, "StateB"), Augmentation(72, "StateC")],
            [mappingA, mappingB, mappingC],
            evidence,
            new Dictionary<uint, ParsedMainRecord>(),
            null);

        Assert.Equal("Surviving producer B", Assert.Single(Assert.Single(records.Terminals).MenuItems).Text);
        Assert.Equal(0x01000032u, Assert.Single(records.Dialogues).FormId);
        Assert.Equal(1, result.SuppressedTerminalMenuItemCount);
        Assert.Equal(1, result.SuppressedInfoCount);
        Assert.Equal(Augmentation(71, "StateB"), Assert.Single(result.ScriptVariableAugmentations));
        var requirement = Assert.Single(result.ProducerRequirements);
        var owner = Assert.Single(requirement.CandidateOwners);
        Assert.Equal("TERM", owner.RecordType);
        Assert.Equal("IndexedTerminalProducer/menu[0]", owner.ScriptPath);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "quest-variable.menu-item-suppressed-no-emitted-producer");

        var writer = new PlanWriter(PlannedEncoders.BuildRegistry());
        var grup = writer.BuildGrupForType(
            "TERM",
            PlanWithTerminalProducer(Assert.Single(records.Terminals)),
            new PluginBuildOptions());
        Assert.NotEmpty(grup);
        Assert.Contains(owner, writer.ProducerLedger.Owners);
        QuestVariableProducerGate.VerifyFinalOutput(
            result.ProducerRequirements,
            writer.ProducerLedger,
            DialogueProducerLedger.Empty);
    }

    [Fact]
    public void Producer_scan_rejects_incomplete_terminal_bundle_before_it_can_support_consumer()
    {
        var mapping = Mapping(7, 70);
        var records = new RecordCollection
        {
            Terminals =
            [
                new TerminalRecord
                {
                    FormId = 0x01000040,
                    EditorId = "IncompleteTerminalProducer",
                    MenuItems =
                    [
                        new TerminalMenuItem
                        {
                            CompiledData = BuildExternalSet(7),
                            ReferencedObjects = [Quest],
                            IsIncompleteExecutableBundle = true
                        }
                    ]
                }
            ],
            Dialogues =
            [
                new DialogueRecord
                {
                    FormId = 0x01000041,
                    Conditions = [QuestVariable(70)]
                }
            ]
        };

        var evidence = FindEvidence(records, [mapping], "TERM");
        var result = QuestVariableProducerGate.Apply(
            records,
            [Augmentation(70)],
            [mapping],
            evidence,
            new Dictionary<uint, ParsedMainRecord>(),
            null);

        Assert.Empty(evidence);
        Assert.Empty(records.Dialogues);
        Assert.Empty(result.ScriptVariableAugmentations);
        Assert.Empty(result.ProducerRequirements);
        Assert.Equal(1, result.SuppressedInfoCount);
    }

    [Fact]
    public void VerifyFinalPlan_requires_a_candidate_owner_to_survive_as_emitted_record()
    {
        var mapping = Mapping(7, 70);
        var records = RecordsWithScptWriter(7, 70);
        var gate = QuestVariableProducerGate.Apply(
            records,
            [Augmentation(70)],
            [mapping],
            FindEvidence(records, [mapping], "SCPT"),
            new Dictionary<uint, ParsedMainRecord>(),
            null);
        var requirement = Assert.Single(gate.ProducerRequirements);

        QuestVariableProducerGate.VerifyFinalPlan(
            PlanWithProducer(SourceScript),
            [requirement]);

        var error = Assert.Throws<InvalidOperationException>(() =>
            QuestVariableProducerGate.VerifyFinalPlan(EmptyPlan(), [requirement]));
        Assert.Contains("lost every proven producer", error.Message, StringComparison.Ordinal);

        var noPlanError = Assert.Throws<InvalidOperationException>(() =>
            QuestVariableProducerGate.VerifyFinalPlan(null, [requirement]));
        Assert.Contains("requires a final EmitPlan", noPlanError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Producer_scan_excludes_structural_writer_not_owned_by_planner()
    {
        var mapping = Mapping(7, 70);
        var records = RecordsWithScptWriter(7, 70);

        var evidence = FindEvidence(records, [mapping], "PACK");

        Assert.Empty(evidence);
    }

    private static IReadOnlyList<QuestVariableProducerEvidence> FindEvidence(
        RecordCollection records,
        IReadOnlyList<QuestVariableRecoveryMapping> mappings,
        params string[] plannerTypes)
    {
        return QuestVariableBytecodeRemapper.FindEmissionEligibleProducerWrites(
            records,
            mappings,
            new Dictionary<uint, ParsedMainRecord>(),
            null,
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(plannerTypes, StringComparer.Ordinal));
    }

    private static DialogueCombinePlan CombinedOutput(IReadOnlyList<DialogueRecord> infos)
    {
        return new DialogueCombinePlan(
            [],
            infos,
            [],
            new HashSet<uint>(),
            0,
            0,
            0,
            []);
    }

    private static RecordCollection RecordsWithScptWriter(uint sourceVariable, uint targetVariable)
    {
        return new RecordCollection
        {
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = SourceScript,
                    EditorId = "PrototypeQuestProducer",
                    CompiledData = BuildExternalSet(checked((ushort)sourceVariable)),
                    ReferencedObjects = [Quest]
                }
            ],
            Dialogues =
            [
                new DialogueRecord
                {
                    FormId = 0x01000002,
                    Conditions = [QuestVariable(targetVariable)]
                }
            ]
        };
    }

    private static DialogueResultScript CompiledScript(uint sourceVariable)
    {
        return new DialogueResultScript
        {
            CompiledData = BuildExternalSet(checked((ushort)sourceVariable)),
            ReferencedObjects = [Quest]
        };
    }

    private static DialogueCondition QuestVariable(uint targetVariable)
    {
        return new DialogueCondition
        {
            FunctionIndex = QuestVariableConditionSanitizer.GetQuestVariableFunctionIndex,
            Parameter1 = Quest,
            Parameter2 = targetVariable
        };
    }

    private static QuestVariableRecoveryMapping Mapping(
        uint sourceVariable,
        uint targetVariable,
        string name = "RecoveredState")
    {
        return new QuestVariableRecoveryMapping(
            Quest,
            Quest,
            TargetScript,
            new ScriptVariableInfo(sourceVariable, name, 1),
            new ScriptVariableInfo(targetVariable, name, 1),
            ScriptVariableDeclarationKind.Short);
    }

    private static ScriptVariableAugmentation Augmentation(
        uint targetVariable,
        string name = "RecoveredState")
    {
        return new ScriptVariableAugmentation(
            TargetScript,
            new ScriptVariableInfo(targetVariable, name, 1),
            ScriptVariableDeclarationKind.Short);
    }

    private static EmitPlan PlanWithProducer(uint sourceFormId)
    {
        var emittedFormId = 0x01000800u;
        var record = new RecordPlan
        {
            Type = "SCPT",
            Disposition = RecordDisposition.New,
            FormId = emittedFormId,
            SourceFormId = sourceFormId,
            Model = new ScriptRecord { FormId = sourceFormId },
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test producer" }
        };
        return new EmitPlan
        {
            Records = [record],
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty.Add(sourceFormId, emittedFormId),
            EmittedFormIds = ImmutableHashSet.Create(emittedFormId),
            RecordIndexByEmittedFormId = ImmutableDictionary<uint, int>.Empty.Add(emittedFormId, 0),
            Diagnostics = ImmutableArray<PlanDiagnostic>.Empty,
            Meta = new PlanMetadata
            {
                NextObjectId = 0x801,
                PlannerCoverage = ImmutableHashSet.Create("SCPT")
            }
        };
    }

    private static EmitPlan PlanWithPackProducer(bool incompleteExecutableBundle)
    {
        const uint sourceFormId = 0x01000020;
        const uint emittedFormId = 0x01000800;
        var package = new PackageRecord
        {
            FormId = sourceFormId,
            EditorId = "LateDecliningProducer",
            OnBegin = new PackageEventAction
            {
                Kind = PackageEventActionKind.OnBegin,
                Scripts =
                [
                    new DialogueResultScript
                    {
                        CompiledData = BuildExternalSet(7),
                        ReferencedObjects = [Quest],
                        IsIncompleteExecutableBundle = incompleteExecutableBundle
                    }
                ]
            }
        };
        var record = new RecordPlan
        {
            Type = "PACK",
            Disposition = RecordDisposition.New,
            FormId = emittedFormId,
            SourceFormId = sourceFormId,
            Model = package,
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test producer" }
        };
        return new EmitPlan
        {
            Records = [record],
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty.Add(sourceFormId, emittedFormId),
            EmittedFormIds = ImmutableHashSet.Create(emittedFormId, Quest),
            ValidPackageFormIds = ImmutableHashSet.Create(emittedFormId),
            RecordIndexByEmittedFormId = ImmutableDictionary<uint, int>.Empty.Add(emittedFormId, 0),
            Diagnostics = ImmutableArray<PlanDiagnostic>.Empty,
            Meta = new PlanMetadata
            {
                NextObjectId = 0x801,
                PlannerCoverage = ImmutableHashSet.Create("PACK")
            }
        };
    }

    private static EmitPlan PlanWithTerminalProducer(TerminalRecord terminal)
    {
        const uint emittedFormId = 0x01000800;
        var record = new RecordPlan
        {
            Type = "TERM",
            Disposition = RecordDisposition.New,
            FormId = emittedFormId,
            SourceFormId = terminal.FormId,
            Model = terminal,
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test producer" }
        };
        return new EmitPlan
        {
            Records = [record],
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty
                .Add(terminal.FormId, emittedFormId),
            EmittedFormIds = ImmutableHashSet.Create(emittedFormId, Quest),
            RecordIndexByEmittedFormId = ImmutableDictionary<uint, int>.Empty.Add(emittedFormId, 0),
            Diagnostics = ImmutableArray<PlanDiagnostic>.Empty,
            Meta = new PlanMetadata
            {
                NextObjectId = 0x801,
                PlannerCoverage = ImmutableHashSet.Create("TERM")
            }
        };
    }

    private static EmitPlan EmptyPlan()
    {
        return new EmitPlan
        {
            Records = ImmutableArray<RecordPlan>.Empty,
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty,
            EmittedFormIds = ImmutableHashSet<uint>.Empty,
            RecordIndexByEmittedFormId = ImmutableDictionary<uint, int>.Empty,
            Diagnostics = ImmutableArray<PlanDiagnostic>.Empty,
            Meta = new PlanMetadata
            {
                NextObjectId = 0x800,
                PlannerCoverage = ImmutableHashSet<string>.Empty
            }
        };
    }

    private static byte[] BuildExternalSet(ushort variableIndex)
    {
        var bytes = new List<byte>();
        AppendUInt16(bytes, 0x001D);
        AppendUInt16(bytes, 0);
        AppendUInt16(bytes, 0x0015);
        AppendUInt16(bytes, 15);
        bytes.Add(0x72);
        AppendUInt16(bytes, 1);
        bytes.Add(0x73);
        AppendUInt16(bytes, variableIndex);
        AppendUInt16(bytes, 7);
        bytes.Add(0x20);
        bytes.Add(0x72);
        AppendUInt16(bytes, 1);
        bytes.Add(0x73);
        AppendUInt16(bytes, variableIndex);
        AppendUInt16(bytes, 0xFFFF);
        AppendUInt16(bytes, 0);
        return [.. bytes];
    }

    private static void AppendUInt16(List<byte> bytes, ushort value)
    {
        bytes.Add((byte)value);
        bytes.Add((byte)(value >> 8));
    }
}