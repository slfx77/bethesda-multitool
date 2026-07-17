using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

public sealed class QuestVariableBytecodeRemapperTests
{
    private const uint Quest = 0x00001000;

    [Fact]
    public void RewriteCompiledData_patches_little_endian_write_and_read_only()
    {
        var input = BuildExternalSet(isBigEndian: false, Quest, 7, integer: true);
        var expected = (byte[])input.Clone();
        expected[12] = 70;
        expected[21] = 70;

        var actual = QuestVariableBytecodeRemapper.RewriteCompiledData(
            input,
            false,
            [],
            [Quest],
            [Mapping(Quest, Quest, 7, 70)]);

        Assert.Equal(expected, actual);
        Assert.Equal(input.Length, actual.Length);
    }

    [Fact]
    public void RewriteCompiledData_patches_big_endian_write_and_read_only()
    {
        var input = BuildExternalSet(isBigEndian: true, Quest, 7, integer: true);
        var expected = (byte[])input.Clone();
        expected[13] = 70;
        expected[22] = 70;

        var actual = QuestVariableBytecodeRemapper.RewriteCompiledData(
            input,
            true,
            [],
            [Quest],
            [Mapping(Quest, Quest, 7, 70)]);

        Assert.Equal(expected, actual);
        Assert.Equal(input.Length, actual.Length);
    }

    [Fact]
    public void RewriteCompiledData_matches_exact_quest_even_when_variable_ids_collide()
    {
        const uint otherQuest = 0x00002000;
        var input = BuildExternalSet(isBigEndian: false, otherQuest, 7, integer: true);

        var actual = QuestVariableBytecodeRemapper.RewriteCompiledData(
            input,
            false,
            [],
            [otherQuest],
            [Mapping(Quest, Quest, 7, 70)]);

        Assert.Equal(input, actual);
        Assert.NotSame(input, actual);
    }

    [Fact]
    public void RewriteCompiledData_resolves_exact_source_to_master_quest_alias()
    {
        const uint prototypeQuest = 0x00101000;
        var input = BuildExternalSet(isBigEndian: false, prototypeQuest, 7, integer: true);

        var actual = QuestVariableBytecodeRemapper.RewriteCompiledData(
            input,
            false,
            [],
            [prototypeQuest],
            [Mapping(prototypeQuest, Quest, 7, 70)],
            new Dictionary<uint, uint> { [prototypeQuest] = Quest });

        Assert.Equal(70, actual[12]);
        Assert.Equal(70, actual[21]);
    }

    [Fact]
    public void RewriteCompiledData_rejects_marker_type_mismatch()
    {
        var input = BuildExternalSet(isBigEndian: false, Quest, 7, integer: false);

        var error = Assert.Throws<InvalidDataException>(() =>
            QuestVariableBytecodeRemapper.RewriteCompiledData(
                input,
                false,
                [],
                [Quest],
                [Mapping(Quest, Quest, 7, 70)]));

        Assert.Contains("Refused non-exact", error.Message);
    }

    [Fact]
    public void RewriteCompiledData_fails_closed_for_unknown_relevant_bytecode()
    {
        byte[] input = [0x01, 0x00, 0x00, 0x00];

        var error = Assert.Throws<InvalidDataException>(() =>
            QuestVariableBytecodeRemapper.RewriteCompiledData(
                input,
                false,
                [],
                [Quest],
                [Mapping(Quest, Quest, 7, 70)]));

        Assert.Contains("structural walk is incomplete or contains unknown data", error.Message);
    }

    [Fact]
    public void Apply_rewrites_scpt_info_all_pack_events_and_term_bundles()
    {
        var makeScript = () => new DialogueResultScript
        {
            CompiledData = BuildExternalSet(false, Quest, 7, integer: true),
            ReferencedObjects = [Quest]
        };
        var records = new RecordCollection
        {
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = 0x01000001,
                    CompiledData = BuildExternalSet(false, Quest, 7, integer: true),
                    ReferencedObjects = [Quest]
                }
            ],
            Dialogues =
            [
                new DialogueRecord
                {
                    FormId = 0x01000002,
                    ResultScripts = [makeScript()]
                }
            ],
            Packages =
            [
                new PackageRecord
                {
                    FormId = 0x01000003,
                    OnBegin = new PackageEventAction
                    {
                        Kind = PackageEventActionKind.OnBegin,
                        Scripts = [makeScript()]
                    },
                    OnEnd = new PackageEventAction
                    {
                        Kind = PackageEventActionKind.OnEnd,
                        Scripts = [makeScript()]
                    },
                    OnChange = new PackageEventAction
                    {
                        Kind = PackageEventActionKind.OnChange,
                        Scripts = [makeScript()]
                    }
                }
            ],
            Terminals =
            [
                new TerminalRecord
                {
                    FormId = 0x01000004,
                    MenuItems =
                    [
                        new TerminalMenuItem
                        {
                            CompiledData = BuildExternalSet(false, Quest, 7, integer: true),
                            ReferencedObjects = [Quest]
                        }
                    ]
                }
            ]
        };

        var result = QuestVariableBytecodeRemapper.Apply(
            records,
            [Mapping(Quest, Quest, 7, 70)],
            new Dictionary<uint, ParsedMainRecord>(),
            null,
            new HashSet<string>(StringComparer.Ordinal));

        Assert.Equal(6, result.ScriptsPatched);
        Assert.Equal(6, result.WriteOperandsPatched);
        Assert.Equal(6, result.ReadOperandsPatched);
        AssertPatched(records.Scripts[0].CompiledData!);
        AssertPatched(records.Dialogues[0].ResultScripts[0].CompiledData!);
        AssertPatched(records.Packages[0].OnBegin!.Scripts[0].CompiledData!);
        AssertPatched(records.Packages[0].OnEnd!.Scripts[0].CompiledData!);
        AssertPatched(records.Packages[0].OnChange!.Scripts[0].CompiledData!);
        AssertPatched(records.Terminals[0].MenuItems[0].CompiledData!);
    }

    [Fact]
    public void Apply_omits_sctx_when_collision_safe_target_name_no_longer_matches_scda()
    {
        var records = new RecordCollection
        {
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = 0x01000010,
                    SourceText = "scn Producer\nBegin GameMode\nset TargetQuest.GameScore to 1\nEnd",
                    CompiledData = BuildExternalSet(false, Quest, 7, integer: true),
                    ReferencedObjects = [Quest]
                }
            ]
        };
        var mapping = new QuestVariableRecoveryMapping(
            Quest,
            Quest,
            0x00005000,
            new ScriptVariableInfo(7, "GameScore", 1),
            new ScriptVariableInfo(70, "GameScore_DmpRecovered", 1),
            ScriptVariableDeclarationKind.Short);

        var result = QuestVariableBytecodeRemapper.Apply(
            records,
            [mapping],
            new Dictionary<uint, ParsedMainRecord>(),
            null,
            new HashSet<string>(StringComparer.Ordinal));

        AssertPatched(Assert.Single(records.Scripts).CompiledData!);
        Assert.Null(records.Scripts[0].SourceText);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.True(diagnostic.SourceTextOmitted);
        Assert.Contains("omitted stale SCTX", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_storage_only_integer_mapping_patches_marker_exactly_without_source_text()
    {
        var records = new RecordCollection
        {
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = 0x01000013,
                    CompiledData = BuildExternalSet(false, Quest, 7, integer: true),
                    ReferencedObjects = [Quest]
                }
            ]
        };
        var mapping = Mapping(Quest, Quest, 7, 70) with
        {
            DeclarationKind = ScriptVariableDeclarationKind.Integer
        };

        var result = QuestVariableBytecodeRemapper.Apply(
            records,
            [mapping],
            new Dictionary<uint, ParsedMainRecord>(),
            null,
            new HashSet<string>(StringComparer.Ordinal));

        AssertPatched(Assert.Single(records.Scripts).CompiledData!);
        Assert.Equal(1, result.ScriptsPatched);
        Assert.Equal(1, result.WriteOperandsPatched);
        Assert.Equal(1, result.ReadOperandsPatched);
    }

    [Fact]
    public void Apply_preserves_sctx_and_accepts_duplicate_mapping_for_case_only_exact_identity()
    {
        const string source = "scn Producer\nBegin GameMode\nset TargetQuest.GameScore to 1\nEnd";
        var records = new RecordCollection
        {
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = 0x01000012,
                    SourceText = source,
                    CompiledData = BuildExternalSet(false, Quest, 7, integer: true),
                    ReferencedObjects = [Quest]
                }
            ]
        };
        var canonical = new QuestVariableRecoveryMapping(
            Quest,
            Quest,
            0x00005000,
            new ScriptVariableInfo(7, "GameScore", 1),
            new ScriptVariableInfo(70, "GameScore", 1),
            ScriptVariableDeclarationKind.Short);
        var caseOnlyEquivalent = canonical with
        {
            TargetVariable = new ScriptVariableInfo(70, "gamescore", 1)
        };

        var result = QuestVariableBytecodeRemapper.Apply(
            records,
            [canonical, caseOnlyEquivalent],
            new Dictionary<uint, ParsedMainRecord>(),
            null,
            new HashSet<string>(StringComparer.Ordinal));

        var script = Assert.Single(records.Scripts);
        AssertPatched(script.CompiledData!);
        Assert.Equal(source, script.SourceText);
        Assert.False(Assert.Single(result.Diagnostics).SourceTextOmitted);
    }

    [Fact]
    public void Apply_omits_stale_sctx_when_collision_safe_name_keeps_same_numeric_index()
    {
        var originalBytes = BuildExternalSet(false, Quest, 10, integer: true);
        var records = new RecordCollection
        {
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = 0x01000011,
                    SourceText = "scn Producer\nBegin GameMode\nset TargetQuest.GameScore to 1\nEnd",
                    CompiledData = originalBytes,
                    ReferencedObjects = [Quest]
                }
            ]
        };
        var mapping = new QuestVariableRecoveryMapping(
            Quest,
            Quest,
            0x00005000,
            new ScriptVariableInfo(10, "GameScore", 1),
            new ScriptVariableInfo(10, "GameScore_DmpRecovered", 1),
            ScriptVariableDeclarationKind.Short);

        var result = QuestVariableBytecodeRemapper.Apply(
            records,
            [mapping],
            new Dictionary<uint, ParsedMainRecord>(),
            null,
            new HashSet<string>(StringComparer.Ordinal));

        var script = Assert.Single(records.Scripts);
        Assert.Equal(originalBytes, script.CompiledData);
        Assert.Null(script.SourceText);
        Assert.Equal(1, result.ScriptsPatched);
        Assert.Equal(0, result.WriteOperandsPatched);
        Assert.Equal(0, result.ReadOperandsPatched);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.True(diagnostic.SourceTextOmitted);
        Assert.Contains("numerically identical", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_leaves_master_anchored_script_and_info_bytecode_unchanged()
    {
        var scriptBytes = BuildExternalSet(false, Quest, 7, integer: true);
        var infoBytes = BuildExternalSet(false, Quest, 7, integer: true);
        var records = new RecordCollection
        {
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = 0x00003000,
                    CompiledData = scriptBytes,
                    ReferencedObjects = [Quest]
                }
            ],
            Dialogues =
            [
                new DialogueRecord
                {
                    FormId = 0x00004000,
                    ResultScripts =
                    [
                        new DialogueResultScript
                        {
                            CompiledData = infoBytes,
                            ReferencedObjects = [Quest]
                        }
                    ]
                }
            ]
        };
        var master = new Dictionary<uint, ParsedMainRecord>
        {
            [0x00003000] = MasterRecord("SCPT", 0x00003000),
            [0x00004000] = MasterRecord("INFO", 0x00004000)
        };

        var result = QuestVariableBytecodeRemapper.Apply(
            records,
            [Mapping(Quest, Quest, 7, 70)],
            master,
            null,
            new HashSet<string>(StringComparer.Ordinal));

        Assert.Equal(0, result.ScriptsPatched);
        Assert.Equal(7, records.Scripts[0].CompiledData![12]);
        Assert.Equal(7, records.Dialogues[0].ResultScripts[0].CompiledData![12]);
    }

    private static void AssertPatched(byte[] compiledData)
    {
        Assert.Equal(70, compiledData[12]);
        Assert.Equal(70, compiledData[21]);
    }

    private static QuestVariableRecoveryMapping Mapping(
        uint sourceQuest,
        uint targetQuest,
        uint sourceVariable,
        uint targetVariable)
    {
        return new QuestVariableRecoveryMapping(
            sourceQuest,
            targetQuest,
            0x00005000,
            new ScriptVariableInfo(sourceVariable, "GameScore", 1),
            new ScriptVariableInfo(targetVariable, "GameScore", 1),
            ScriptVariableDeclarationKind.Short);
    }

    private static ParsedMainRecord MasterRecord(string signature, uint formId)
    {
        return new ParsedMainRecord
        {
            Header = new MainRecordHeader { Signature = signature, FormId = formId }
        };
    }

    private static byte[] BuildExternalSet(
        bool isBigEndian,
        uint referencedQuest,
        ushort variableIndex,
        bool integer)
    {
        _ = referencedQuest; // Identity is carried by the SCRO table supplied to the walker.
        var bytes = new List<byte>();
        AppendUInt16(bytes, 0x001D, isBigEndian);
        AppendUInt16(bytes, 0, isBigEndian);
        AppendUInt16(bytes, 0x0015, isBigEndian);
        AppendUInt16(bytes, 15, isBigEndian);
        bytes.Add(0x72);
        AppendUInt16(bytes, 1, isBigEndian);
        bytes.Add(integer ? (byte)0x73 : (byte)0x66);
        AppendUInt16(bytes, variableIndex, isBigEndian);
        AppendUInt16(bytes, 7, isBigEndian);
        bytes.Add(0x20);
        bytes.Add(0x72);
        AppendUInt16(bytes, 1, isBigEndian);
        bytes.Add(integer ? (byte)0x73 : (byte)0x66);
        AppendUInt16(bytes, variableIndex, isBigEndian);
        AppendUInt16(bytes, 0xFFFF, isBigEndian);
        AppendUInt16(bytes, 0, isBigEndian);
        return [.. bytes];
    }

    private static void AppendUInt16(List<byte> bytes, ushort value, bool bigEndian)
    {
        if (bigEndian)
        {
            bytes.Add((byte)(value >> 8));
            bytes.Add((byte)value);
        }
        else
        {
            bytes.Add((byte)value);
            bytes.Add((byte)(value >> 8));
        }
    }
}
