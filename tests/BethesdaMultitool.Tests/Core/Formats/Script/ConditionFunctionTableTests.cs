using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Script;

/// <summary>
///     The game-keyed condition-function tables (S1): Oblivion's engine-extracted command table and
///     the shared numeric-vs-FormID classifier lifted from <c>DialogueConditionDisplayFormatter</c>.
///     Oblivion conditions used to render through the FNV table — wrong names past index 0x171 and
///     wrong param typing past raw id 31 (the numberings diverge at 32: TES4 Birthsign vs FNV FormType).
/// </summary>
public class ConditionFunctionTableTests
{
    [Fact]
    public void FalloutTable_BacksTheSharedFacade()
    {
        var set = ScriptFunctionTables.For(BethesdaGame.FalloutNewVegas);
        Assert.Same(ScriptFunctionTable.Get(0x102F), set.Get(0x102F)); // GetItemCount
        Assert.Equal(ScriptFunctionTable.GetName(0x1030), set.GetName(0x1030));

        // FO3 keeps the pre-existing FNV script-opcode compatibility surface, but its raw CTDA
        // lookup is a separately extracted retail map with FO3-owned definition objects.
        var fallout3 = ScriptFunctionTables.For(BethesdaGame.Fallout3);
        Assert.Equal(BethesdaGame.Fallout3, fallout3.Game);
        Assert.Equal(BethesdaGame.Fallout3, ConditionFunctionTable.For(BethesdaGame.Fallout3).Game);
        Assert.NotSame(set, fallout3);
        Assert.Same(set.Get(0x102F), fallout3.Get(0x102F));
        Assert.Equal(set.GetConditionFunction(0x02F)?.Name,
            fallout3.GetConditionFunction(0x02F)?.Name);
        Assert.NotSame(set.GetConditionFunction(0x02F), fallout3.GetConditionFunction(0x02F));
        var unknown = ScriptFunctionTables.For(BethesdaGame.Unknown);
        Assert.Equal(BethesdaGame.Unknown, unknown.Game);
        Assert.Null(unknown.Get(0x102F));
    }

    [Fact]
    public void FalloutNewVegasTable_UsesExactRetailConditionCallbackSubset()
    {
        var set = ScriptFunctionTables.For(BethesdaGame.FalloutNewVegas);
        var conditions = ConditionFunctionTable.For(BethesdaGame.FalloutNewVegas);

        Assert.Equal(205, ScriptFunctionTable.ConsoleFunctionCount);
        Assert.Equal(625, ScriptFunctionTable.EngineSlotCount);
        Assert.Equal(624, ScriptFunctionTable.GameCommandCount);
        Assert.Equal(829, ScriptFunctionTable.FunctionCount);
        Assert.Equal(829, ScriptFunctionTable.All.Count);
        Assert.Equal(250, ScriptFunctionTable.ConditionFunctionCount);
        Assert.Equal(250, ScriptFunctionTable.ConditionFunctions.Count);
        Assert.Equal(250, ScriptFunctionTable.All.Values.Count(
            item => item.IsConditionFunction is true));
        Assert.Equal(579, ScriptFunctionTable.All.Values.Count(
            item => item.IsConditionFunction is false));
        Assert.DoesNotContain(ScriptFunctionTable.All.Values,
            item => item.IsConditionFunction is null);

        for (var rawIndex = 0; rawIndex < ScriptFunctionTable.GameCommandCount; rawIndex++)
        {
            Assert.NotNull(set.Get((ushort)(0x1000 + rawIndex)));
        }

        foreach (var (rawIndex, definition) in ScriptFunctionTable.ConditionFunctions)
        {
            Assert.Same(set.Get((ushort)(0x1000 + rawIndex)), definition);
        }

        var getDistance = set.Get(0x1001);
        Assert.NotNull(getDistance);
        Assert.Equal("GetDistance", getDistance!.Name);
        Assert.Same(getDistance, ScriptFunctionTable.ConditionFunctions[0x0001]);
        Assert.Same(getDistance, set.GetConditionFunction(0x0001));
        Assert.Same(getDistance, conditions.Get(0x0001));

        // These remain valid script commands, but their retail pConditionFunction is null.
        Assert.Equal("UnusedFunction0", set.Get(0x1000)?.Name);
        Assert.Equal("AddItem", set.Get(0x1002)?.Name);
        Assert.Null(set.GetConditionFunction(0x0000));
        Assert.Null(set.GetConditionFunction(0x0002));

        // A script opcode accidentally supplied as a raw CTDA index, and corrupt high values,
        // must not alias a command through the exact map.
        Assert.Null(set.GetConditionFunction(0x1001));
        Assert.Null(set.GetConditionFunction(0xFFFF));
        Assert.Equal("Func 0xFFFF", conditions.GetName(0xFFFF));
    }

    [Fact]
    public void FalloutNewVegasGeneratedTable_PinsCallbackProvenanceAndGeneratorContract()
    {
        var generated = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Esm", "Script",
            "ScriptFunctionTable.Generated.cs");
        Assert.Contains(
            "EXE SHA-256: " +
            "A43DFE9025A0FACD0EF862E89A83C05C11A5CB3A9FE53EFEEC18C0278F75F0A6",
            generated, StringComparison.Ordinal);
        Assert.Contains(
            "PDB SHA-256: " +
            "EC702DC52F42A9C14037B800871D727AF2B54ED191D68AE5FCDE3BFCE65E6F00",
            generated, StringComparison.Ordinal);
        Assert.Contains("624 game commands; 250 non-null retail condition callbacks",
            generated, StringComparison.Ordinal);
        Assert.Contains("pConditionFunction is the pointer at +0x20",
            generated, StringComparison.Ordinal);

        var extractor = SourceContract.ReadSource("tools", "extract_script_functions.py");
        Assert.Contains("condition_pointer = pe.read_u32(offset + 32)",
            extractor, StringComparison.Ordinal);
        Assert.Contains("EXPECTED_CONDITION_COUNT = 250", extractor, StringComparison.Ordinal);
        Assert.Contains("if actual != expected:", extractor, StringComparison.Ordinal);
        Assert.Contains("unmapped condition callback", extractor, StringComparison.Ordinal);
        Assert.Contains("if args.verify_only:", extractor, StringComparison.Ordinal);
    }

    [Fact]
    public void Fallout3Table_UsesExactRetailConditionCallbackMap()
    {
        var scripts = ScriptFunctionTables.For(BethesdaGame.Fallout3);
        var conditions = ConditionFunctionTable.For(BethesdaGame.Fallout3);
        var fnv = ScriptFunctionTables.For(BethesdaGame.FalloutNewVegas);

        Assert.Equal(BethesdaGame.Fallout3, scripts.Game);
        Assert.Equal(568, Fallout3ConditionFunctionTable.LocalGameCommandCount);
        Assert.Equal(1, Fallout3ConditionFunctionTable.LocalSentinelSlotCount);
        Assert.Equal(237, Fallout3ConditionFunctionTable.ConditionFunctionCount);
        Assert.Equal(7, Fallout3ConditionFunctionTable.ExcludedFoseConditionCount);
        Assert.Equal(237, Fallout3ConditionFunctionTable.ConditionFunctions.Count);
        Assert.All(Fallout3ConditionFunctionTable.ConditionFunctions.Values,
            definition => Assert.True(definition.IsConditionFunction is true));

        var getDistance = scripts.GetConditionFunction(0x0001);
        Assert.NotNull(getDistance);
        Assert.Equal("GetDistance", getDistance!.Name);
        Assert.Same(getDistance, conditions.Get(0x0001));
        Assert.NotSame(fnv.GetConditionFunction(0x0001), getDistance);

        // The script-opcode compatibility table remains separate. Neither a script-only FO3 row nor
        // a later FNV condition callback can enter FO3's explicit raw-index map through projection.
        Assert.Equal("AddItem", scripts.Get(0x1002)?.Name);
        Assert.Null(scripts.GetConditionFunction(0x0002));
        Assert.Equal("GetObjectiveCompleted", scripts.Get(0x11A4)?.Name);
        Assert.Null(scripts.GetConditionFunction(0x01A4));
        Assert.Null(scripts.GetConditionFunction(0x01A5));
        Assert.Null(scripts.GetConditionFunction(0x023D));

        var fo3HasPerk = scripts.GetConditionFunction(0x01C1);
        Assert.NotNull(fo3HasPerk);
        Assert.Equal("HasPerk", fo3HasPerk!.Name);
        var fo3PerkParam = Assert.Single(fo3HasPerk.Params);
        Assert.Equal("Perk", fo3PerkParam.Name);
        Assert.Equal(ScriptParamType.Perk, fo3PerkParam.Type);
        Assert.False(fo3PerkParam.Optional);
        Assert.Equal(2, fnv.GetConditionFunction(0x01C1)?.Params.Length);
        Assert.True(conditions.TryClassifyParam(0x01C1, 0, out var perkKind));
        Assert.Equal(ConditionParamKind.FormId, perkKind);
        Assert.False(conditions.TryClassifyParam(0x01C1, 1, out _));

        Assert.Equal("HasLoaded3D", conditions.GetName(0x022E));
        Assert.Null(conditions.Get(0x022F));
        Assert.Null(conditions.Get(0x1001));
        Assert.Null(conditions.Get(0xFFFF));
    }

    [Fact]
    public void Fallout3GeneratedTable_PinsRetailOracleAndExtractionContract()
    {
        var generated = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Esm", "Script",
            "Fallout3ConditionFunctionTable.Generated.cs");
        Assert.Contains("Fallout3.exe 1.7.0.4; 16,855,040 bytes", generated,
            StringComparison.Ordinal);
        Assert.Contains(
            "C3F97C2255FA041A851C17CF372D69AAADD8694E2DC4230BA556001BBFBD2F3E",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            "CodeView: Fallout.pdb; GUID fa958b2a-dde8-42d1-b407-b864abf11685; age 2",
            generated,
            StringComparison.Ordinal);
        Assert.Contains("568 sequential game commands", generated, StringComparison.Ordinal);
        Assert.Contains("+0x20 callback pointer is non-null for exactly 237 rows", generated,
            StringComparison.Ordinal);
        Assert.Contains("+0x20 pConditionFunction field name/layout is FNV-PDB-corroborated, not FO3-PDB-proven",
            generated,
            StringComparison.Ordinal);
        Assert.Contains("comes directly from that FO3 array", generated,
            StringComparison.Ordinal);
        Assert.Contains("not generated by subtracting rows from or copying definitions out of the FNV table",
            generated,
            StringComparison.Ordinal);
        Assert.Contains("FNV-PDB-pinned enum numbering", generated, StringComparison.Ordinal);
        Assert.Contains(
            "xEdit source commit: e0e529a2d473756520f2d41f72c24dea0cf5ee0d",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            "EF6F8DF070B5E7C7B4A551AD2A633A329DA9BEEFE72A995DACA61F8404A16A96",
            generated,
            StringComparison.Ordinal);
        Assert.Contains("Seven separately labeled FOSE rows are validated but excluded",
            generated,
            StringComparison.Ordinal);
        Assert.Contains("normal builds/runtime have no dependency on it", generated,
            StringComparison.Ordinal);

        var extractor = SourceContract.ReadSource(
            "tools", "extract_fo3_condition_functions.py");
        Assert.Contains("EXPECTED_GAME_COMMAND_COUNT = 568", extractor,
            StringComparison.Ordinal);
        Assert.Contains("EXPECTED_CONDITION_COUNT = 237", extractor,
            StringComparison.Ordinal);
        Assert.Contains("candidates != [EXPECTED_GAME_ARRAY_FILE_OFFSET]", extractor,
            StringComparison.Ordinal);
        Assert.Contains("parameter_section.name != \".data\"", extractor,
            StringComparison.Ordinal);
        Assert.Contains("condition_pointer = image.read_u32(offset + 0x20)", extractor,
            StringComparison.Ordinal);
        Assert.Contains("resolved_condition[0].name != \".text\"", extractor,
            StringComparison.Ordinal);
        Assert.Contains("zero_fields = (0x0C, 0x10, 0x14, 0x18, 0x1C, 0x20, 0x24)",
            extractor,
            StringComparison.Ordinal);
        Assert.Contains("fose_entries != EXPECTED_FOSE_FUNCTIONS", extractor,
            StringComparison.Ordinal);
        Assert.Contains("validate_xedit_crosscheck(conditions, xedit_base)", extractor,
            StringComparison.Ordinal);
        Assert.Contains("if args.verify_only:", extractor, StringComparison.Ordinal);
    }

    [Fact]
    public void Fallout4Table_PreservesFullEngineTableAndAuthoritativeConditionSubset()
    {
        var set = ScriptFunctionTables.For(BethesdaGame.Fallout4);

        Assert.Equal(BethesdaGame.Fallout4, set.Game);
        Assert.Equal(819, Fallout4ScriptFunctionTable.EngineSlotCount);
        Assert.Equal(810, Fallout4ScriptFunctionTable.NamedFunctionCount);
        Assert.Equal(810, Fallout4ScriptFunctionTable.Functions.Count);
        Assert.Equal(479, Fallout4ScriptFunctionTable.ConditionFunctionCount);
        Assert.Equal(479, Fallout4ScriptFunctionTable.ConditionFunctions.Count);
        Assert.Equal(479, Fallout4ScriptFunctionTable.ConditionParamKinds.Count);
        Assert.Equal(63, Fallout4ScriptFunctionTable.TypeOverrideEligibleFunctionCount);
        Assert.Equal(64, Fallout4ScriptFunctionTable.TypeOverrideEligibleSlotCount);
        Assert.Equal(63, Fallout4ScriptFunctionTable.ConditionTypeOverrideEligibility.Count);
        Assert.Equal(64, Fallout4ScriptFunctionTable.ConditionTypeOverrideEligibility.Values
            .Sum(parameters => parameters.Count(item => item)));
        Assert.Equal(479, Fallout4ScriptFunctionTable.Functions.Values.Count(item => item.IsConditionFunction is true));
        Assert.Equal(331, Fallout4ScriptFunctionTable.Functions.Values.Count(item => item.IsConditionFunction is false));
        Assert.DoesNotContain(Fallout4ScriptFunctionTable.Functions.Values, item => item.IsConditionFunction is null);

        var getItemCount = set.Get(0x102F);
        Assert.NotNull(getItemCount);
        Assert.Equal("GetItemCount", getItemCount!.Name);
        Assert.Equal(Fallout4ScriptParamType.InvObjectOrFormList, getItemCount.Params[0].Fallout4Type);
        Assert.Same(getItemCount, Fallout4ScriptFunctionTable.ConditionFunctions[0x02F]);
        Assert.Same(getItemCount, set.GetConditionFunction(0x02F));
        Assert.Contains((ushort)0x02F, Fallout4ScriptFunctionTable.ConditionParamKinds.Keys);
        Assert.DoesNotContain((ushort)0x102F, Fallout4ScriptFunctionTable.ConditionParamKinds.Keys);

        // AddItem is a real FO4 script command at opcode 0x1002, but raw condition index 2 is
        // absent. The script-command table keeps it while the CTDA lookup rejects it.
        Assert.Equal("AddItem", set.Get(0x1002)?.Name);
        Assert.Null(set.GetConditionFunction(0x002));

        Assert.Equal(Fallout4ScriptParamType.EventFunction,
            set.Get(0x1240)!.Params[0].Fallout4Type);
        Assert.Equal(46, (ushort)Fallout4ScriptParamType.EventFunction);
        Assert.Equal(46, (ushort)ScriptParamType.VoiceType);
        Assert.NotEqual(nameof(Fallout4ScriptParamType.EventFunction), nameof(ScriptParamType.VoiceType));
    }

    [Fact]
    public void RawConditionIndices_DoNotAliasScriptOpcodesOrEachOther()
    {
        var scriptCommand = new ScriptFunctionDef("ScriptCommand", "", false, []);
        var lowCondition = new ScriptFunctionDef("IsTeamLeader", "", false, []);
        var highCondition = new ScriptFunctionDef("PlayerHasQuest", "", false, []);
        var set = new ScriptFunctionSet(
            game: BethesdaGame.Fallout76,
            functions: new Dictionary<ushort, ScriptFunctionDef>
            {
                [0x138C] = scriptCommand
            },
            conditionFunctionsByIndex: new Dictionary<ushort, ScriptFunctionDef>
            {
                [908] = lowCondition,
                [5004] = highCondition
            },
            conditionParamKindsByIndex: new Dictionary<ushort, ConditionParamKind?[]>
            {
                [908] = [],
                [5004] = [ConditionParamKind.FormId]
            });

        Assert.Same(scriptCommand, set.Get(0x138C));
        Assert.Same(lowCondition, set.GetConditionFunction(908));
        Assert.Same(highCondition, set.GetConditionFunction(5004));
        Assert.False(set.TryGetConditionParamKind(908, 0, out _));
        Assert.True(set.TryGetConditionParamKind(5004, 0, out var highKind));
        Assert.Equal(ConditionParamKind.FormId, highKind);
    }

    [Fact]
    public void ExplicitEmptyConditionMap_IsAuthoritative()
    {
        var projectedCommand = new ScriptFunctionDef("ProjectedCommand", "", false, []);
        var functions = new Dictionary<ushort, ScriptFunctionDef>
        {
            [0x102F] = projectedCommand
        };
        var explicitEmpty = new ScriptFunctionSet(
            game: BethesdaGame.Starfield,
            functions: functions,
            conditionFunctionsByIndex: new Dictionary<ushort, ScriptFunctionDef>());
        var legacyNull = new ScriptFunctionSet(
            game: BethesdaGame.FalloutNewVegas,
            functions: functions,
            useLegacyConditionOpcodeProjection: true);
        var modernNull = new ScriptFunctionSet(
            game: BethesdaGame.Fallout76,
            functions: functions);

        Assert.Null(explicitEmpty.GetConditionFunction(0x02F));
        Assert.Same(projectedCommand, legacyNull.GetConditionFunction(0x02F));
        Assert.Null(modernNull.GetConditionFunction(0x02F));
    }

    [Fact]
    public void ExplicitEmptyConditionMetadata_SuppressesDefinitionFallback()
    {
        var condition = new ScriptFunctionDef(
            "FormCondition", "", false,
            [new ScriptFunctionParamDef("Object", ScriptParamType.Form, false)]);
        var set = new ScriptFunctionSet(
            game: BethesdaGame.Starfield,
            functions: new Dictionary<ushort, ScriptFunctionDef>(),
            conditionFunctionsByIndex: new Dictionary<ushort, ScriptFunctionDef> { [0x02F] = condition },
            conditionParamKindsByIndex: new Dictionary<ushort, ConditionParamKind?[]>());

        Assert.True(set.HasAuthoritativeConditionParamKinds);
        Assert.False(set.TryGetConditionParamKind(0x02F, 0, out _));
    }

    [Fact]
    public void LegacyConditionProjection_FailsClosedAboveClassicIndexRange()
    {
        var set = new ScriptFunctionSet(
            game: BethesdaGame.FalloutNewVegas,
            functions: new Dictionary<ushort, ScriptFunctionDef>
            {
                [0x1000] = new("GetWantBlocking", "", true, [])
            },
            useLegacyConditionOpcodeProjection: true);

        Assert.Null(set.GetConditionFunction(0x1000));
        Assert.Null(set.GetConditionFunction(5000));
    }

    [Fact]
    public void Fallout4Table_NullParameterPointersAreExplicitlyUnresolved()
    {
        var set = ScriptFunctionTables.For(BethesdaGame.Fallout4);
        var unresolved = Fallout4ScriptFunctionTable.Functions.Values
            .Where(item => item.HasUnresolvedParameters)
            .ToArray();

        Assert.Equal(Fallout4ScriptFunctionTable.UnresolvedParameterFunctionCount, unresolved.Length);
        Assert.Collection(unresolved.OrderBy(item => item.Name),
            item =>
            {
                Assert.Equal("TeachWord", item.Name);
                Assert.Empty(item.Params);
            },
            item =>
            {
                Assert.Equal("UnlockWord", item.Name);
                Assert.Empty(item.Params);
            });
        Assert.Empty(set.Get(0x1245)!.Params);
        Assert.Empty(set.Get(0x1246)!.Params);
    }

    [Fact]
    public void Fallout4ConditionKinds_UseCtdaMetadataNotScriptParameterPosition()
    {
        var table = ConditionFunctionTable.For(BethesdaGame.Fallout4);

        // FO4 ActorValue is an AVIF FormID in CTDA storage, unlike TES4's numeric AV index.
        Assert.True(table.TryClassifyParam(0x00E, 0, out var actorValue));
        Assert.Equal(ConditionParamKind.FormId, actorValue);

        // The script compiler exposes GetEventData as three parameters. CTDA packs function/member
        // into param1 and stores event data (a FormID) in param2, so raw script positions are not an oracle.
        var engineGetEventData = ScriptFunctionTables.For(BethesdaGame.Fallout4).Get(0x1240);
        Assert.Equal(3, engineGetEventData!.Params.Length);
        Assert.True(table.TryClassifyParam(0x240, 0, out var eventFunction));
        Assert.Equal(ConditionParamKind.Numeric, eventFunction);
        Assert.True(table.TryClassifyParam(0x240, 1, out var eventData));
        Assert.Equal(ConditionParamKind.FormId, eventData);
        var condition = new DialogueCondition
        {
            FunctionIndex = 0x240,
            Parameter1 = 0x00010002,
            Parameter2 = 0x00123456
        };
        Assert.False(DialogueConditionDisplayFormatter.IsFormReference(
            condition, 0, BethesdaGame.Fallout4));
        Assert.True(DialogueConditionDisplayFormatter.IsFormReference(
            condition, 1, BethesdaGame.Fallout4));

        // xEdit declares one functional ParamType3 (GetPlayerControlsDisabled), but FO4's physical
        // trailing CTDA field is selected by Run On. This table deliberately types only param1/2.
        Assert.Equal(1, Fallout4ScriptFunctionTable.XEditDeclaredThirdParameterCount);
        Assert.False(table.TryClassifyParam(0x062, 2, out _));
    }

    [Fact]
    public void Fallout4TypeOverrides_UseExactEligibilityPriorityAndRunOnException()
    {
        var table = ConditionFunctionTable.For(BethesdaGame.Fallout4);

        // The context-free API deliberately exposes the declared base kind.
        Assert.True(table.TryClassifyParam(0x001, 0, out var declared));
        Assert.Equal(ConditionParamKind.FormId, declared); // GetDistance(ptReference)

        Assert.True(table.TryClassifyParam(0x001, 0, 0x02, 0, out var alias));
        Assert.Equal(ConditionParamKind.Numeric, alias);
        Assert.True(table.TryClassifyParam(0x001, 0, 0x08, 0, out var packdata));
        Assert.Equal(ConditionParamKind.Numeric, packdata);

        // Param2 eligibility is independent: GetFactionRankDifference's Faction stays a FormID,
        // while its Actor slot is replaced by the alias id.
        Assert.True(table.TryClassifyParam(0x03C, 0, 0x02, 0, out var faction));
        Assert.Equal(ConditionParamKind.FormId, faction);
        Assert.True(table.TryClassifyParam(0x03C, 1, 0x02, 0, out var actorAlias));
        Assert.Equal(ConditionParamKind.Numeric, actorAlias);

        // A FormID kind outside xEdit's Reference/Actor/Package gate is never overridden.
        Assert.True(table.TryClassifyParam(0x048, 0, 0x0A, 0, out var baseObject));
        Assert.Equal(ConditionParamKind.FormId, baseObject); // GetIsID(ptBaseObject)

        // GetIsCurrentPackage is the live FO4 exception: with Run On Quest Alias, bit 0x02
        // describes physical Param3, so param1 remains the Package FormID. Alias has priority;
        // when both bits are present xEdit also skips the packdata override.
        Assert.True(table.TryClassifyParam(0x0A1, 0, 0x02, 5, out var package));
        Assert.Equal(ConditionParamKind.FormId, package);
        Assert.True(table.TryClassifyParam(0x0A1, 0, 0x0A, 5, out var aliasWins));
        Assert.Equal(ConditionParamKind.FormId, aliasWins);
        Assert.True(table.TryClassifyParam(0x0A1, 0, 0x08, 5, out var packageData));
        Assert.Equal(ConditionParamKind.Numeric, packageData);
        Assert.True(table.TryClassifyParam(0x0A1, 0, 0x02, 0, out var ordinaryAlias));
        Assert.Equal(ConditionParamKind.Numeric, ordinaryAlias);
        Assert.False(table.TryClassifyParam(0x0A1, 0, 0x02, null, out _));
    }

    [Fact]
    public void Fallout4Formatter_UsesConditionTypeAndRunOnContext()
    {
        static string Resolve(uint formId) => $"FORM_{formId}";

        var ordinary = new DialogueCondition
        {
            FunctionIndex = 0x001,
            Parameter1 = 7
        };
        Assert.Contains("GetDistance(FORM_7)", DialogueConditionDisplayFormatter.FormatCondition(
            ordinary, Resolve, game: BethesdaGame.Fallout4));

        var alias = ordinary with { Type = 0x02 };
        Assert.Contains("GetDistance(7)", DialogueConditionDisplayFormatter.FormatCondition(
            alias, Resolve, game: BethesdaGame.Fallout4));

        var exception = new DialogueCondition
        {
            Type = 0x02,
            FunctionIndex = 0x0A1,
            Parameter1 = 42,
            RunOn = 5
        };
        var formatted = DialogueConditionDisplayFormatter.FormatCondition(
            exception, Resolve, game: BethesdaGame.Fallout4);
        Assert.Contains("GetIsCurrentPackage(FORM_42)", formatted);
        Assert.Contains("Run On: Quest Alias", formatted);

        var reference = ordinary with { RunOn = 2, Reference = 0x1234 };
        Assert.Contains("Ref: FORM_4660 (0x00001234)",
            DialogueConditionDisplayFormatter.FormatCondition(
                reference, Resolve, game: BethesdaGame.Fallout4));

        var ignoredReferenceStorage = ordinary with { RunOn = 5, Reference = 0x1234 };
        Assert.DoesNotContain("Ref:", DialogueConditionDisplayFormatter.FormatCondition(
            ignoredReferenceStorage, Resolve, game: BethesdaGame.Fallout4));

        var fnvException = ordinary with { FunctionIndex = 0x006A, RunOn = 2, Reference = 0x1234 };
        Assert.DoesNotContain("Ref:", DialogueConditionDisplayFormatter.FormatCondition(
            fnvException, Resolve, game: BethesdaGame.FalloutNewVegas));
        Assert.Contains("Ref:", DialogueConditionDisplayFormatter.FormatCondition(
            fnvException, Resolve, game: BethesdaGame.Fallout3));
    }

    [Theory]
    [InlineData(BethesdaGame.Skyrim, 5u, "Quest Alias")]
    [InlineData(BethesdaGame.Skyrim, 6u, "Package Data")]
    [InlineData(BethesdaGame.Skyrim, 7u, "Event Data")]
    [InlineData(BethesdaGame.Fallout4, 5u, "Quest Alias")]
    [InlineData(BethesdaGame.Fallout4, 6u, "Package Data")]
    [InlineData(BethesdaGame.Fallout4, 7u, "Event Data")]
    [InlineData(BethesdaGame.Fallout4, 8u, "Command Target")]
    [InlineData(BethesdaGame.Fallout4, 9u, "Event Camera Ref")]
    [InlineData(BethesdaGame.Fallout4, 10u, "My Killer")]
    [InlineData(BethesdaGame.Fallout76, 5u, "Quest Alias")]
    [InlineData(BethesdaGame.Fallout76, 6u, "Package Data")]
    [InlineData(BethesdaGame.Fallout76, 7u, "Event Data")]
    [InlineData(BethesdaGame.Fallout76, 8u, "Command Target")]
    [InlineData(BethesdaGame.Fallout76, 9u, "Event Camera Ref")]
    [InlineData(BethesdaGame.Fallout76, 10u, "My Killer")]
    [InlineData(BethesdaGame.Fallout76, 11u, "Active Players")]
    [InlineData(BethesdaGame.Fallout76, 12u, "Potential Players")]
    [InlineData(BethesdaGame.Fallout76, 13u, "Player Teammates")]
    [InlineData(BethesdaGame.Fallout76, 14u, "Target List")]
    [InlineData(BethesdaGame.Fallout76, 15u, "Instance Owner")]
    [InlineData(BethesdaGame.Starfield, 2u, "Reference")]
    [InlineData(BethesdaGame.Starfield, 5u, "Quest Alias")]
    [InlineData(BethesdaGame.Starfield, 6u, "Package Data")]
    [InlineData(BethesdaGame.Starfield, 7u, "Event Data")]
    [InlineData(BethesdaGame.Starfield, 8u, "Command Target")]
    [InlineData(BethesdaGame.Starfield, 9u, "Event Camera Ref")]
    [InlineData(BethesdaGame.Starfield, 10u, "My Killer")]
    [InlineData(BethesdaGame.Starfield, 11u, "Self Packin")]
    [InlineData(BethesdaGame.Starfield, 12u, "Target Packin")]
    [InlineData(BethesdaGame.Starfield, 13u, "My Ship")]
    [InlineData(BethesdaGame.Starfield, 14u, "Player Home Ship")]
    [InlineData(BethesdaGame.Starfield, 15u, "Player")]
    [InlineData(BethesdaGame.Starfield, 16u, "Unknown (16)")]
    public void Formatter_UsesGameSpecificModernRunOnNames(BethesdaGame game, uint runOn, string expected)
    {
        var condition = new DialogueCondition { FunctionIndex = 1, RunOn = runOn };

        var formatted = DialogueConditionDisplayFormatter.FormatCondition(
            condition, formId => $"FORM_{formId}", game: game);

        Assert.Contains($"Run On: {expected}", formatted);
    }

    [Fact]
    public void StarfieldFormatter_KeepsDefaultSubjectImplicit()
    {
        var condition = new DialogueCondition { FunctionIndex = 1, RunOn = 0 };

        Assert.Equal("Subject", DialogueConditionRunOnPolicy.Format(
            condition,
            BethesdaGame.Starfield));
        Assert.False(DialogueConditionRunOnPolicy.ShouldDisplay(
            condition,
            BethesdaGame.Starfield));
        Assert.DoesNotContain("Run On:", DialogueConditionDisplayFormatter.FormatCondition(
            condition,
            formId => $"FORM_{formId}",
            game: BethesdaGame.Starfield));
    }

    [Theory]
    [InlineData(BethesdaGame.Oblivion)]
    [InlineData(BethesdaGame.Fallout3)]
    [InlineData(BethesdaGame.FalloutNewVegas)]
    [InlineData(BethesdaGame.Unknown)]
    public void Formatter_DoesNotBorrowModernRunOnNamesForLegacyOrUnknownGames(BethesdaGame game)
    {
        var condition = new DialogueCondition { FunctionIndex = 1, RunOn = 5 };

        var formatted = DialogueConditionDisplayFormatter.FormatCondition(
            condition, formId => $"FORM_{formId}", game: game);

        Assert.Contains("Run On: Unknown (5)", formatted);
    }

    [Theory]
    [InlineData(BethesdaGame.Unknown)]
    [InlineData(BethesdaGame.Morrowind)]
    [InlineData(BethesdaGame.Oblivion)]
    public void Formatter_FailsClosedForUnsupportedOrdinaryRunOnDomains(BethesdaGame game)
    {
        var condition = new DialogueCondition { FunctionIndex = 1, RunOn = 2 };

        var formatted = DialogueConditionDisplayFormatter.FormatCondition(
            condition, formId => $"FORM_{formId}", game: game);

        Assert.Contains("Run On: Unknown (2)", formatted);
    }

    [Theory]
    [InlineData(0x006A, 0u, "Idle")]
    [InlineData(0x006A, 2u, "Left Arm")]
    [InlineData(0x006A, 20u, "Whole Body")]
    [InlineData(0x006A, 8u, "Unknown (8)")]
    [InlineData(0x011D, 0u, "Idle")]
    [InlineData(0x011D, 2u, "Left Arm")]
    [InlineData(0x011D, 20u, "Whole Body")]
    [InlineData(0x011D, 8u, "Unknown (8)")]
    public void Formatter_UsesFnvAnimationBodySelectorInsteadOfRunOnNames(
        int functionIndex, uint selector, string expected)
    {
        var condition = new DialogueCondition
        {
            FunctionIndex = (ushort)functionIndex,
            RunOn = selector
        };

        var formatted = DialogueConditionDisplayFormatter.FormatCondition(
            condition, formId => $"FORM_{formId}", game: BethesdaGame.FalloutNewVegas);

        Assert.Contains($"Run On: {expected}", formatted);
    }

    [Fact]
    public void Formatter_KeepsFnvAnimationBodySelectorExceptionGameAndFunctionScoped()
    {
        var exceptionalIndex = new DialogueCondition { FunctionIndex = 0x006A, RunOn = 2 };
        var fallout3 = DialogueConditionDisplayFormatter.FormatCondition(
            exceptionalIndex, formId => $"FORM_{formId}", game: BethesdaGame.Fallout3);
        Assert.Contains("Run On: Reference", fallout3);

        var ordinaryFnv = exceptionalIndex with { FunctionIndex = 1, RunOn = 0 };
        var fnv = DialogueConditionDisplayFormatter.FormatCondition(
            ordinaryFnv, formId => $"FORM_{formId}", game: BethesdaGame.FalloutNewVegas);
        Assert.DoesNotContain("Run On:", fnv);
    }

    [Theory]
    [InlineData(BethesdaGame.Oblivion)]
    [InlineData(BethesdaGame.Fallout3)]
    [InlineData(BethesdaGame.FalloutNewVegas)]
    public void Formatter_MigratesLegacyTypeTargetBitForClassicGames(BethesdaGame game)
    {
        var condition = new DialogueCondition { Type = 0x02, FunctionIndex = 1, RunOn = 0 };

        var formatted = DialogueConditionDisplayFormatter.FormatCondition(
            condition, formId => $"FORM_{formId}", game: game);

        Assert.Contains("Run On: Target", formatted);
    }

    [Fact]
    public void Formatter_DoesNotTreatModernAliasBitAsLegacyTarget()
    {
        var condition = new DialogueCondition { Type = 0x02, FunctionIndex = 1, RunOn = 0 };

        var formatted = DialogueConditionDisplayFormatter.FormatCondition(
            condition, formId => $"FORM_{formId}", game: BethesdaGame.Fallout4);

        Assert.DoesNotContain("Run On:", formatted);
    }

    [Theory]
    [InlineData(BethesdaGame.Skyrim, 5u, -1, "Quest Alias: -1")]
    [InlineData(BethesdaGame.Fallout4, 7u, 0x3152, "Event Data: 12626")]
    [InlineData(BethesdaGame.Fallout76, 5u, -42, "Quest Alias: -42")]
    [InlineData(BethesdaGame.Starfield, 5u, -1, "Quest Alias: -1")]
    [InlineData(BethesdaGame.Starfield, 7u, 0x3152, "Event Data: 12626")]
    public void Formatter_ShowsRunOnSelectedModernParameter3(
        BethesdaGame game,
        uint runOn,
        int parameter3,
        string expected)
    {
        var condition = new DialogueCondition
        {
            FunctionIndex = 1,
            RunOn = runOn,
            Parameter3 = parameter3
        };

        var formatted = DialogueConditionDisplayFormatter.FormatCondition(
            condition, formId => $"FORM_{formId}", game: game);

        Assert.Contains(expected, formatted);
    }

    [Fact]
    public void Formatter_KeepsOtherParameter3RawAndSuppressesOnlyItsDefaultNoise()
    {
        var raw = new DialogueCondition { FunctionIndex = 1, RunOn = 2, Parameter3 = -42 };
        var formatted = DialogueConditionDisplayFormatter.FormatCondition(
            raw, formId => $"FORM_{formId}", game: BethesdaGame.Unknown);
        Assert.Contains("Parameter #3: -42", formatted);

        var defaultValue = raw with { Parameter3 = -1 };
        Assert.DoesNotContain("Parameter #3:", DialogueConditionDisplayFormatter.FormatCondition(
            defaultValue, formId => $"FORM_{formId}", game: BethesdaGame.Fallout4));
        Assert.DoesNotContain("Parameter #3:", DialogueConditionDisplayFormatter.FormatCondition(
            defaultValue, formId => $"FORM_{formId}", game: BethesdaGame.Starfield));

        var absent = raw with { Parameter3 = null };
        Assert.DoesNotContain("Parameter #3:", DialogueConditionDisplayFormatter.FormatCondition(
            absent, formId => $"FORM_{formId}", game: BethesdaGame.Fallout4));
    }

    [Fact]
    public void Formatter_PrefersPresentCisStrings_AndEscapesThemDeterministically()
    {
        static string Resolve(uint formId) => $"FORM_{formId}";

        var escaped = new DialogueCondition
        {
            FunctionIndex = 0x294, // FO4 GetVMScriptVariable(string, string)
            Parameter1 = 0xDEADBEEF,
            Parameter2 = 0,
            Parameter1String = "A\\B\"C\r\n\t\b\f\u0001",
            Parameter2String = string.Empty,
            ComparisonValue = 1
        };
        Assert.Equal(
            "GetVMScriptVariable(\"A\\\\B\\\"C\\r\\n\\t\\b\\f\\u0001\", \"\") == 1",
            DialogueConditionDisplayFormatter.FormatCondition(
                escaped, Resolve, game: BethesdaGame.Fallout4));

        // CIS presence wins over both a nonzero, FormID-shaped placeholder and the table's
        // declared kind. Empty is a present string, not an absent parameter.
        var empty = new DialogueCondition
        {
            FunctionIndex = 0x001, // GetDistance normally takes a FormID.
            Parameter1 = 0x00123456,
            Parameter1String = string.Empty,
            ComparisonValue = 1
        };
        Assert.Equal("GetDistance(\"\") == 1", DialogueConditionDisplayFormatter.FormatCondition(
            empty,
            _ => throw new InvalidOperationException("A CIS placeholder must not be resolved as a FormID."),
            game: BethesdaGame.Fallout4));
        Assert.False(DialogueConditionDisplayFormatter.IsFormReference(
            empty, 0, BethesdaGame.Fallout4));
    }

    [Fact]
    public void Formatter_ResolvesUseGlobalComparisonInsteadOfFormattingFormIdBitsAsFloat()
    {
        const uint globalFormId = 0x00123456;
        var condition = new DialogueCondition
        {
            Type = 0x04,
            FunctionIndex = 0x00E,
            ComparisonValue = BitConverter.UInt32BitsToSingle(globalFormId)
        };

        Assert.True(condition.UsesGlobalComparison);
        Assert.Equal(globalFormId, condition.ComparisonGlobalFormId);
        Assert.Equal(
            "GetValue == GLOB GameHour (0x00123456)",
            DialogueConditionDisplayFormatter.FormatCondition(
                condition,
                id => $"FORM_{id:X8}",
                id => id == globalFormId ? "GameHour" : $"FORM_{id:X8}",
                BethesdaGame.Fallout4));
    }

    [Theory]
    [InlineData(BethesdaGame.Morrowind)]
    [InlineData(BethesdaGame.Unknown)]
    public void UnsupportedTables_FailClosedInsteadOfInventingFnvCommands(BethesdaGame game)
    {
        var scripts = ScriptFunctionTables.For(game);
        var conditions = ConditionFunctionTable.For(game);

        Assert.Equal(game, scripts.Game);
        Assert.Null(scripts.Get(0x102F));
        Assert.Equal("UnknownFunc_0x102F", scripts.GetName(0x102F));
        Assert.Null(conditions.Get(0x02F));
        Assert.Equal("Func 0x002F", conditions.GetName(0x02F));
        Assert.False(conditions.TryClassifyParam(0x02F, 0, out _));
    }

    [Fact]
    public void Fallout76Table_PreservesRawIndicesWithoutInventingScriptOpcodes()
    {
        var scripts = ScriptFunctionTables.For(BethesdaGame.Fallout76);
        var conditions = ConditionFunctionTable.For(BethesdaGame.Fallout76);

        Assert.Equal(BethesdaGame.Fallout76, scripts.Game);
        Assert.Equal(638, Fallout76ConditionFunctionTable.ConditionFunctionCount);
        Assert.Equal(64, Fallout76ConditionFunctionTable.ParameterTypeCount);
        Assert.Equal(62, Fallout76ConditionFunctionTable.UsedParameterTypeCount);
        Assert.Equal(49, Fallout76ConditionFunctionTable.HighRawIndexCount);
        Assert.Equal(8, Fallout76ConditionFunctionTable.LegacyOrCollisionPairCount);
        Assert.Equal(12004, Fallout76ConditionFunctionTable.MaximumRawIndex);
        Assert.Equal(638, Fallout76ConditionFunctionTable.ConditionFunctions.Count);
        Assert.Equal(638, Fallout76ConditionFunctionTable.ConditionParamKinds.Count);
        Assert.Equal(68, Fallout76ConditionFunctionTable.ConditionTypeOverrideEligibility.Count);
        Assert.Equal(69, Fallout76ConditionFunctionTable.ConditionTypeOverrideEligibility.Values.Sum(
            slots => slots.Count(eligible => eligible)));

        var low = conditions.Get(908);
        var high = conditions.Get(5004);
        Assert.Equal("IsTeamLeader", low?.Name);
        Assert.Equal("PlayerHasQuest", high?.Name);
        Assert.NotSame(low, high);
        Assert.Equal("IsInAirOrFloating", conditions.GetName(5000));
        Assert.Equal("GetEquippedWeaponHealthPercent", conditions.GetName(12004));
        Assert.Null(conditions.Get(4096));
        Assert.Null(conditions.Get(ushort.MaxValue));

        // Both 908 and 5004 collapse to 0x138C under the old bitwise-OR projection. The condition
        // table keeps them distinct, while the unrelated script-opcode domain remains empty.
        Assert.Null(scripts.Get(0x138C));
        Assert.Null(scripts.Get(0x238C));
        Assert.Equal("UnknownFunc_0x138C", scripts.GetName(0x138C));
    }

    [Fact]
    public void Fallout76Classifier_UsesXEditBaseKindsAndExactTypeOverrides()
    {
        var table = ConditionFunctionTable.For(BethesdaGame.Fallout76);

        Assert.True(table.TryClassifyParam(0x0001, 0, out var distance));
        Assert.Equal(ConditionParamKind.FormId, distance);
        Assert.True(table.TryClassifyParam(0x000E, 0, out var actorValue));
        Assert.Equal(ConditionParamKind.Numeric, actorValue);
        Assert.True(table.TryClassifyParam(893, 0, out var attackData));
        Assert.Equal(ConditionParamKind.Numeric, attackData);
        Assert.True(table.TryClassifyParam(904, 0, out var constructible));
        Assert.Equal(ConditionParamKind.FormId, constructible);
        Assert.True(table.TryClassifyParam(5004, 0, out var quest));
        Assert.Equal(ConditionParamKind.FormId, quest);
        Assert.False(table.TryClassifyParam(5000, 0, out _));

        Assert.True(table.TryClassifyParam(0x0001, 0, 0x02, 0, out var alias));
        Assert.Equal(ConditionParamKind.Numeric, alias);
        Assert.True(table.TryClassifyParam(0x0001, 0, 0x08, 0, out var packdata));
        Assert.Equal(ConditionParamKind.Numeric, packdata);

        // BaseObject remains a FormID even when both modern Type bits are set.
        Assert.True(table.TryClassifyParam(0x0048, 0, 0x0A, 0, out var baseObject));
        Assert.Equal(ConditionParamKind.FormId, baseObject);

        // GetFactionRankDifference: p1 Faction is ineligible, while p2 Actor is eligible.
        Assert.True(table.TryClassifyParam(0x003C, 0, 0x02, 0, out var faction));
        Assert.Equal(ConditionParamKind.FormId, faction);
        Assert.True(table.TryClassifyParam(0x003C, 1, 0x02, 0, out var actorAlias));
        Assert.Equal(ConditionParamKind.Numeric, actorAlias);

        // FO76 shares xEdit's FO4 exception: with Run On=Quest Alias the physical Param3 owns the
        // alias and GetIsCurrentPackage param1 stays a Package FormID.
        Assert.True(table.TryClassifyParam(0x00A1, 0, 0x02, 5, out var package));
        Assert.Equal(ConditionParamKind.FormId, package);
        Assert.True(table.TryClassifyParam(0x00A1, 0, 0x02, 0, out var ordinaryAlias));
        Assert.Equal(ConditionParamKind.Numeric, ordinaryAlias);
        Assert.False(table.TryClassifyParam(0x00A1, 0, 0x02, null, out _));
    }

    [Fact]
    public void Fallout76GeneratedTable_PinsCommunityProvenanceAndCollisionBoundary()
    {
        var generated = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Esm", "Script",
            "Fallout76ConditionFunctionTable.Generated.cs");
        Assert.Contains(
            "xEdit wbDefinitionsFO76.pas SHA-256: " +
            "6DBB57FEF040413E4A2D4E5C2FB98E880D959F68A7ECF83CC922686A9A5887F9",
            generated,
            StringComparison.Ordinal);
        Assert.Contains("xEdit source under MPL-2.0", generated, StringComparison.Ordinal);
        Assert.Contains("no engine command/callback identity is claimed", generated,
            StringComparison.Ordinal);
        Assert.Contains("Eight low/high pairs collide", generated, StringComparison.Ordinal);
        Assert.Contains("not a Fallout 76 script-opcode or Papyrus command table", generated,
            StringComparison.Ordinal);

        var extractor = SourceContract.ReadSource("tools", "extract_fo76_condition_functions.py");
        Assert.Contains("EXPECTED_LEGACY_OR_COLLISIONS", extractor, StringComparison.Ordinal);
        Assert.Contains("TYPE_OVERRIDE_ELIGIBLE_SLOT_COUNT = 69", extractor,
            StringComparison.Ordinal);
        Assert.Contains("if args.verify_only:", extractor, StringComparison.Ordinal);
    }

    [Fact]
    public void StarfieldTable_IsConditionOnlyAndPinsItsCommunityContract()
    {
        var scripts = ScriptFunctionTables.For(BethesdaGame.Starfield);
        var conditions = ConditionFunctionTable.For(BethesdaGame.Starfield);

        Assert.Equal(BethesdaGame.Starfield, scripts.Game);
        Assert.Equal(610, StarfieldConditionFunctionTable.ConditionFunctionCount);
        Assert.Equal(67, StarfieldConditionFunctionTable.ParameterTypeCount);
        Assert.Equal(966, StarfieldConditionFunctionTable.MaximumRawIndex);
        Assert.Equal(610, StarfieldConditionFunctionTable.ConditionFunctions.Count);
        Assert.Equal(610, StarfieldConditionFunctionTable.ConditionParamKinds.Count);
        Assert.Equal(82, StarfieldConditionFunctionTable.ConditionTypeOverrideEligibility.Count);
        Assert.Equal(83, StarfieldConditionFunctionTable.ConditionTypeOverrideEligibility.Values.Sum(
            slots => slots.Count(eligible => eligible)));

        Assert.Equal("GetDistance", conditions.GetName(1));
        Assert.Equal("GetActionDataForm", conditions.GetName(819));
        Assert.Equal("IsInsidePrimitiveTopAndBottom", conditions.GetName(904));
        // Keep the xEdit spelling: the retail executable's Gameplay spelling is useful lexical
        // evidence, but raw 961 is unobserved and the string alone does not prove index identity.
        Assert.Equal("GetGamePlayOptionCurrentValue", conditions.GetName(961));
        Assert.Equal("AreVehiclesUnlocked", conditions.GetName(966));
        Assert.Null(conditions.Get(967));
        Assert.Null(conditions.Get(ushort.MaxValue));

        // The xEdit list is condition metadata, not proof of Starfield bytecode/Papyrus opcodes.
        Assert.Null(scripts.Get(0x1001));
        Assert.Null(scripts.Get(0x13C6));
        Assert.Equal("UnknownFunc_0x1001", scripts.GetName(0x1001));
    }

    [Fact]
    public void StarfieldClassifier_FollowsConcreteUnionArmsAndModernTypeOverrides()
    {
        var table = ConditionFunctionTable.For(BethesdaGame.Starfield);

        Assert.True(table.TryClassifyParam(1, 0, out var distance));
        Assert.Equal(ConditionParamKind.FormId, distance);

        // Unlike Skyrim/FO4/FO76's AV-index metadata, SF1's union stores ptActorValue as AVIF.
        Assert.True(table.TryClassifyParam(14, 0, out var actorValue));
        Assert.Equal(ConditionParamKind.FormId, actorValue);
        // ptForm sits under xEdit's broad "Misc" comment but its concrete union arm is wbFormID.
        Assert.True(table.TryClassifyParam(819, 0, out var form));
        Assert.Equal(ConditionParamKind.FormId, form);

        Assert.True(table.TryClassifyParam(407, 0, out var vatsSelector));
        Assert.Equal(ConditionParamKind.Numeric, vatsSelector);
        Assert.True(table.TryClassifyParam(407, 1, out var vatsValue));
        Assert.Equal(ConditionParamKind.Numeric, vatsValue);
        Assert.True(table.TryClassifyParam(576, 0, out var eventCode));
        Assert.Equal(ConditionParamKind.Numeric, eventCode);
        Assert.True(table.TryClassifyParam(576, 1, out var eventData));
        Assert.Equal(ConditionParamKind.FormId, eventData);
        Assert.False(table.TryClassifyParam(966, 0, out _));

        Assert.True(table.TryClassifyParam(1, 0, 0x02, 14, out var alias));
        Assert.Equal(ConditionParamKind.Numeric, alias);
        Assert.True(table.TryClassifyParam(1, 0, 0x08, 0, out var packdata));
        Assert.Equal(ConditionParamKind.Numeric, packdata);

        Assert.True(table.TryClassifyParam(0x00A1, 0, 0x02, 5, out var package));
        Assert.Equal(ConditionParamKind.FormId, package);
        Assert.True(table.TryClassifyParam(0x00A1, 0, 0x02, 0, out var ordinaryAlias));
        Assert.Equal(ConditionParamKind.Numeric, ordinaryAlias);
        Assert.False(table.TryClassifyParam(0x00A1, 0, 0x02, null, out _));
    }

    [Fact]
    public void StarfieldGeneratedTable_PinsCommunityProvenanceAndUnionSemantics()
    {
        var generated = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Esm", "Script",
            "StarfieldConditionFunctionTable.Generated.cs");
        Assert.Contains(
            "xEdit wbDefinitionsSF1.pas SHA-256: " +
            "8736162FCE44C970CFA3DDAC945A739530169390C4FDABAFC0209B36B247A576",
            generated,
            StringComparison.Ordinal);
        Assert.Contains("xEdit source under MPL-2.0", generated, StringComparison.Ordinal);
        Assert.Contains("no engine command/callback identity is claimed", generated,
            StringComparison.Ordinal);
        Assert.Contains("Retail layout/usage cross-check: Steam build 23518663", generated,
            StringComparison.Ordinal);
        Assert.Contains(
            "7E9ADB1414A8E1B325E5E1F097B9B17B78DEB7EEBEDA37A333351A43A60F9D28",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            "1DABED00C3F4282DD3BB54D2E9601E40B577D8742D078B7CCEF203ADBFEF0DA7",
            generated,
            StringComparison.Ordinal);
        Assert.Contains("HEDR 0.96; its 96,486 CTDAs", generated, StringComparison.Ordinal);
        Assert.Contains("all 124,096 CTDAs in the pinned 14-master",
            generated,
            StringComparison.Ordinal);
        Assert.Contains("corpus, are 32 bytes", generated, StringComparison.Ordinal);
        Assert.Contains("does not prove the 300 unobserved rows", generated,
            StringComparison.Ordinal);
        Assert.Contains("No matching PDB/map was installed", generated, StringComparison.Ordinal);
        Assert.Contains("conservatively retains xEdit's", generated, StringComparison.Ordinal);
        Assert.Contains("not a Starfield script-opcode or Papyrus command table", generated,
            StringComparison.Ordinal);

        var extractor = SourceContract.ReadSource(
            "tools", "extract_starfield_condition_functions.py");
        Assert.Contains("wbFormID('Form')", extractor, StringComparison.Ordinal);
        Assert.Contains("Actor Value', [AVIF]", extractor, StringComparison.Ordinal);
        Assert.Contains("TYPE_OVERRIDE_ELIGIBLE_SLOT_COUNT = 83", extractor,
            StringComparison.Ordinal);
        Assert.Contains("EXPECTED_RETAIL_STEAM_BUILD_ID = \"23518663\"", extractor,
            StringComparison.Ordinal);
        Assert.Contains("EXPECTED_RETAIL_BASE_CENSUS", extractor, StringComparison.Ordinal);
        Assert.Contains("EXPECTED_RETAIL_CORPUS_CENSUS", extractor, StringComparison.Ordinal);
        Assert.Contains("scan_retail_ctda", extractor, StringComparison.Ordinal);
        Assert.Contains("\"--verify-retail\"", extractor, StringComparison.Ordinal);
        Assert.Contains("if args.verify_only or args.verify_retail is not None:", extractor,
            StringComparison.Ordinal);

        var runOnPolicy = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Esm", "Script", "Conditions",
            "DialogueConditionRunOnPolicy.cs");
        var displayFormatter = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "EsmView",
            "DialogueConditionDisplayFormatter.cs");
        foreach (var presentationSource in new[] { runOnPolicy, displayFormatter })
        {
            Assert.Contains(
                "e0e529a2d473756520f2d41f72c24dea0cf5ee0d",
                presentationSource,
                StringComparison.Ordinal);
            Assert.Contains(
                "8736162FCE44C970CFA3DDAC945A739530169390C4FDABAFC0209B36B247A576",
                presentationSource,
                StringComparison.Ordinal);
            Assert.Contains("MPL-2.0", presentationSource, StringComparison.Ordinal);
            Assert.Contains("not these labels", presentationSource, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SkyrimTable_SeparatesPinnedLeEngineRowsFromLaterCommunityRows()
    {
        var scripts = ScriptFunctionTables.For(BethesdaGame.Skyrim);
        var conditions = ConditionFunctionTable.For(BethesdaGame.Skyrim);

        Assert.Equal(BethesdaGame.Skyrim, scripts.Game);
        Assert.Equal(727, SkyrimConditionFunctionTable.LocalEngineCommandCount);
        Assert.Equal(1, SkyrimConditionFunctionTable.LocalEngineSentinelSlotCount);
        Assert.Equal(391, SkyrimConditionFunctionTable.LocalEngineConditionFunctionCount);
        Assert.Equal(379, SkyrimConditionFunctionTable.LocalEngineUniqueConditionHandlerCount);
        Assert.Equal(387, SkyrimConditionFunctionTable.MapConditionSymbolCount);
        Assert.Equal(385, SkyrimConditionFunctionTable.LocalEngineExactDisplayNameCount);
        Assert.Equal(6, SkyrimConditionFunctionTable.LocalEngineAliasDisplayNameCount);
        Assert.Equal(6, SkyrimConditionFunctionTable.XEditPostArtifactConditionCount);
        Assert.Equal(5, SkyrimConditionFunctionTable.SkseExtensionConditionCount);
        Assert.Equal(402, SkyrimConditionFunctionTable.ConditionFunctionCount);
        Assert.Equal(402, SkyrimConditionFunctionTable.ConditionFunctions.Count);
        Assert.Equal(402, SkyrimConditionFunctionTable.ConditionParamKinds.Count);
        Assert.Equal(49, SkyrimConditionFunctionTable.ConditionTypeOverrideEligibility.Count);
        Assert.Equal(50, SkyrimConditionFunctionTable.ConditionTypeOverrideEligibility.Values.Sum(
            slots => slots.Count(eligible => eligible)));

        Assert.Equal("GetDistance", conditions.GetName(0x0001));
        Assert.Equal("IsRidingMount", conditions.GetName(0x0147));
        Assert.Equal("IsOnFlyingMount", conditions.GetName(0x02DA));
        Assert.Equal("GetActorWarmth", conditions.GetName(0x02DF));
        Assert.Equal("GetSKSEVersion", conditions.GetName(0x0400));
        Assert.Equal("ClearInvalidRegistrations", conditions.GetName(0x0404));
        Assert.Null(conditions.Get(0x02D7));
        Assert.Null(conditions.Get(0xFFFF));

        // The generated source is deliberately condition-only. Community CTDA rows must not become
        // an invented Skyrim bytecode/Papyrus command table through 0x1000 arithmetic.
        Assert.Null(scripts.Get(0x1001));
        Assert.Null(scripts.Get(0x12DA));
        Assert.Equal("UnknownFunc_0x1001", scripts.GetName(0x1001));
    }

    [Fact]
    public void SkyrimClassifier_AppliesExactBaseKindsAndTypeOverrideEligibility()
    {
        var table = ConditionFunctionTable.For(BethesdaGame.Skyrim);

        Assert.True(table.TryClassifyParam(0x0001, 0, out var distance));
        Assert.Equal(ConditionParamKind.FormId, distance);
        Assert.True(table.TryClassifyParam(0x000E, 0, out var actorValue));
        Assert.Equal(ConditionParamKind.Numeric, actorValue);

        Assert.True(table.TryClassifyParam(0x0001, 0, 0x02, 0, out var alias));
        Assert.Equal(ConditionParamKind.Numeric, alias);
        Assert.True(table.TryClassifyParam(0x0001, 0, 0x08, 0, out var packdata));
        Assert.Equal(ConditionParamKind.Numeric, packdata);
        Assert.True(table.TryClassifyParam(0x0001, 0, 0x0A, 0, out var aliasWins));
        Assert.Equal(ConditionParamKind.Numeric, aliasWins);

        // BaseObject is a FormID but is not one of xEdit's three overrideable base kinds.
        Assert.True(table.TryClassifyParam(0x0048, 0, 0x0A, 0, out var baseObject));
        Assert.Equal(ConditionParamKind.FormId, baseObject);

        // Unlike FO4, TES5 has no GetIsCurrentPackage/Run-On-Quest-Alias exception: the package
        // slot itself becomes an alias id under Type bit 0x02.
        Assert.True(table.TryClassifyParam(0x00A1, 0, 0x02, 5, out var packageAlias));
        Assert.Equal(ConditionParamKind.Numeric, packageAlias);
    }

    [Theory]
    [InlineData(0u, ConditionParamKind.FormId)]
    [InlineData(1u, ConditionParamKind.FormId)]
    [InlineData(2u, ConditionParamKind.FormId)]
    [InlineData(3u, ConditionParamKind.FormId)]
    [InlineData(4u, ConditionParamKind.Numeric)]
    [InlineData(5u, ConditionParamKind.Numeric)]
    [InlineData(9u, ConditionParamKind.FormId)]
    [InlineData(10u, ConditionParamKind.FormId)]
    [InlineData(18u, ConditionParamKind.Numeric)]
    [InlineData(20u, ConditionParamKind.Numeric)]
    public void SkyrimGetVatsValue_Param2DependsOnParam1(
        uint selector,
        ConditionParamKind expected)
    {
        var table = ConditionFunctionTable.For(BethesdaGame.Skyrim);

        Assert.True(table.TryClassifyParam(
            SkyrimConditionFunctionTable.VatsValueFunctionIndex,
            1,
            conditionType: 0,
            runOn: 0,
            parameter1Value: selector,
            out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SkyrimGetVatsValue_MissingOrInvalidSelectorFailsClosed()
    {
        var table = ConditionFunctionTable.For(BethesdaGame.Skyrim);

        Assert.False(table.TryClassifyParam(
            SkyrimConditionFunctionTable.VatsValueFunctionIndex, 1, 0, 0, out _));
        Assert.False(table.TryClassifyParam(
            SkyrimConditionFunctionTable.VatsValueFunctionIndex,
            1,
            0,
            0,
            parameter1Value: 21,
            out _));

        var weapon = new DialogueCondition
        {
            FunctionIndex = SkyrimConditionFunctionTable.VatsValueFunctionIndex,
            Parameter1 = 0,
            Parameter2 = 0x00123456
        };
        Assert.True(DialogueConditionDisplayFormatter.IsFormReference(
            weapon, 1, BethesdaGame.Skyrim));
        Assert.Contains(
            "GetVATSValue(IronSword)",
            DialogueConditionDisplayFormatter.FormatCondition(
                weapon,
                id => id == 0x00123456 ? "IronSword" : $"0x{id:X8}",
                game: BethesdaGame.Skyrim),
            StringComparison.Ordinal);
    }

    [Fact]
    public void SkyrimGeneratedTable_PinsEngineMapAndCommunityProvenance()
    {
        var generated = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Esm", "Script",
            "SkyrimConditionFunctionTable.Generated.cs");
        Assert.Contains(
            "Skyrim LE TESV.exe SHA-256: " +
            "311E71737B597DDC02A8D26D83BB5B0B2896C9041A69F580E1B4DE875C4BB8BD",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            "Skyrim LE TESV.map SHA-256: " +
            "FED7F0B964EA752FE677F4C413C37C97B5CAD21541755C69C701A66423B288B2",
            generated,
            StringComparison.Ordinal);
        Assert.Contains(
            "xEdit wbDefinitionsTES5.pas SHA-256: " +
            "621697E36E806C6308B11E3FE125C0BBB8CE783BCC7704DBD05A7B1BF9E40390",
            generated,
            StringComparison.Ordinal);
        Assert.Contains("six untagged xEdit rows 730..735", generated, StringComparison.Ordinal);
        Assert.Contains("five rows explicitly marked Added by SKSE", generated, StringComparison.Ordinal);
        Assert.Contains("not a Skyrim script-opcode or Papyrus command table", generated,
            StringComparison.Ordinal);

        var extractor = SourceContract.ReadSource("tools", "extract_skyrim_condition_functions.py");
        Assert.Contains("condition_pointer", extractor, StringComparison.Ordinal);
        Assert.Contains("engine_handler_addresses != map_handler_addresses", extractor,
            StringComparison.Ordinal);
        Assert.Contains("EXPECTED_ENGINE_NAME_ALIASES", extractor, StringComparison.Ordinal);
        Assert.Contains("if args.verify_only:", extractor, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownFunctionsAndParameters_FailClosedAsRaw()
    {
        var fallout = ConditionFunctionTable.For(BethesdaGame.FalloutNewVegas);

        Assert.False(fallout.TryClassifyParam(0xFFFF, 0, out _));
        Assert.False(fallout.TryClassifyParam(0x02F, 10, out _));
        Assert.Equal(ConditionParamKind.Numeric, fallout.ClassifyParam(0xFFFF, 0));
    }

    [Theory]
    [InlineData(0x1001, "GetDistance", true)]
    [InlineData(0x1048, "GetIsID", true)]
    [InlineData(0x1029, "GetClothingValue", true)]
    public void OblivionTable_CarriesEngineCommandDefinitions(ushort opcode, string name, bool isRef)
    {
        var set = ScriptFunctionTables.For(BethesdaGame.Oblivion);
        var def = set.Get(opcode);
        Assert.NotNull(def);
        Assert.Equal(name, def!.Name);
        Assert.Equal(isRef, def.IsReferenceFunction);
    }

    [Fact]
    public void OblivionTable_UsesExactRawConditionSubsetAndKeepsKeySpacesSeparate()
    {
        var set = ScriptFunctionTables.For(BethesdaGame.Oblivion);
        var conditions = ConditionFunctionTable.For(BethesdaGame.Oblivion);

        Assert.Equal(501, OblivionScriptFunctionTable.RetailCommandCount);
        Assert.Equal(31, OblivionScriptFunctionTable.XObseCommandCount);
        Assert.Equal(532, OblivionScriptFunctionTable.FunctionCount);
        Assert.Equal(532, OblivionScriptFunctionTable.Functions.Count);
        Assert.Equal(169, OblivionScriptFunctionTable.EngineConditionFunctionCount);
        Assert.Equal(31, OblivionScriptFunctionTable.XObseExtensionCount);
        Assert.Equal(200, OblivionScriptFunctionTable.ConditionFunctionCount);
        Assert.Equal(200, OblivionScriptFunctionTable.ConditionFunctions.Count);
        Assert.Equal(169, OblivionScriptFunctionTable.ConditionFunctions.Keys.Count(
            index => index <= 0x0171));
        Assert.Equal(31, OblivionScriptFunctionTable.ConditionFunctions.Keys.Count(
            index => index > 0x0171));
        Assert.Equal(200, OblivionScriptFunctionTable.Functions.Values.Count(
            item => item.IsConditionFunction is true));
        Assert.Equal(332, OblivionScriptFunctionTable.Functions.Values.Count(
            item => item.IsConditionFunction is false));
        Assert.DoesNotContain(OblivionScriptFunctionTable.Functions.Values,
            item => item.IsConditionFunction is null);
        foreach (var (rawIndex, definition) in OblivionScriptFunctionTable.ConditionFunctions)
        {
            Assert.Same(OblivionScriptFunctionTable.Functions[(ushort)(0x1000 + rawIndex)],
                definition);
        }

        // GetDistance is both a game command and an engine-backed CTDA function. The two
        // independently keyed maps intentionally reuse the exact same definition object.
        var getDistance = set.Get(0x1001);
        Assert.NotNull(getDistance);
        Assert.Equal("GetDistance", getDistance!.Name);
        Assert.Same(getDistance, set.GetConditionFunction(0x001));
        Assert.Same(getDistance, OblivionScriptFunctionTable.ConditionFunctions[0x001]);

        // These are real script commands whose CommandInfo.eval pointer is null.
        Assert.Equal("MessageBox", set.Get(0x1000)?.Name);
        Assert.Equal("AddItem", set.Get(0x1002)?.Name);
        Assert.Null(set.GetConditionFunction(0x000));
        Assert.Null(set.GetConditionFunction(0x002));

        // HasSpell is one of the 31 rows xEdit labels Added by (x)OBSE. It remains in the wider
        // opcode table for extension compatibility, but is not attributed to retail CommandInfo.
        var hasSpellCommand = set.Get(0x1462);
        Assert.NotNull(hasSpellCommand);
        var hasSpell = set.GetConditionFunction(0x462);
        Assert.NotNull(hasSpell);
        Assert.Equal("HasSpell", hasSpell!.Name);
        Assert.Equal(ObScriptParamType.SpellItem, hasSpell.Params[0].ObType);
        Assert.Same(hasSpellCommand, hasSpell);
        Assert.Same(hasSpell, conditions.Get(0x462));

        Assert.Null(set.GetConditionFunction(0xFFFF));
        Assert.Equal("Func 0xFFFF", conditions.GetName(0xFFFF));
    }

    [Fact]
    public void OblivionGeneratedTable_PinsEngineAndCommunityProvenance()
    {
        var generated = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Esm", "Script",
            "OblivionScriptFunctionTable.Generated.cs");
        Assert.Contains(
            "Official retail Oblivion.exe SHA-256: " +
            "74DB80316ADE529DF4D70942FCF8D9E40660C9CA69683E595F0448FEBEB018EA",
            generated, StringComparison.Ordinal);
        Assert.Contains(
            "xEdit source commit: e0e529a2d473756520f2d41f72c24dea0cf5ee0d",
            generated, StringComparison.Ordinal);
        Assert.Contains(
            "xEdit wbDefinitionsTES4.pas SHA-256: " +
            "D461214EDBD7648FB9960826902403BA5E70798B3C56FF046D3AC7C10AF8372A",
            generated, StringComparison.Ordinal);
        Assert.Contains("Community provenance (MPL-2.0): 31 xOBSE command/condition definitions",
            generated, StringComparison.Ordinal);
        Assert.Contains("retained in the opcode table for xOBSE ecosystem compatibility",
            generated, StringComparison.Ordinal);
        Assert.Contains("The raw-index map is exactly 200 rows: 169 retail engine + 31 xOBSE.",
            generated, StringComparison.Ordinal);

        var extractor = SourceContract.ReadSource("tools", "extract_tes4_script_functions.py");
        Assert.Contains("eval_va = pe.read_ptr(pos + 0x20)", extractor, StringComparison.Ordinal);
        Assert.Contains("if mode == \"--verify-only\":", extractor, StringComparison.Ordinal);
        Assert.Contains("if actual != expected:", extractor, StringComparison.Ordinal);
    }

    [Fact]
    public void OblivionAndFalloutTables_DivergeWhereTheEnginesDo()
    {
        var oblivion = ScriptFunctionTables.For(BethesdaGame.Oblivion);
        var fallout = ScriptFunctionTables.For(BethesdaGame.FalloutNewVegas);

        // The tables genuinely diverge (FNV kept many legacy TES4 names at the same indices, but
        // rewrote/added others) — count the disagreements over TES4's whole opcode range.
        var diverging = 0;
        for (ushort op = 0x1000; op <= 0x1171; op++)
        {
            var obName = oblivion.Get(op)?.Name;
            if (obName is not null && fallout.Get(op)?.Name is { } fnvName &&
                !string.Equals(obName, fnvName, StringComparison.OrdinalIgnoreCase))
            {
                diverging++;
            }
        }

        // 49 divergences as of the FNV Aug-2010 / Oblivion retail extractions.
        Assert.True(diverging >= 40, $"expected substantial divergence, found {diverging} differing names");

        // xOBSE's extension command is not present in FNV's table at the TES4/xOBSE opcode.
        Assert.Equal("HasSpell", oblivion.Get(0x1462)?.Name);
        Assert.Same(oblivion.Get(0x1462), oblivion.GetConditionFunction(0x462));
        Assert.Equal("UnknownFunc_0x1462", fallout.GetName(0x1462));
    }

    [Fact]
    public void OblivionClassifier_UsesTes4Numbering()
    {
        var table = ConditionFunctionTable.For(BethesdaGame.Oblivion);

        // GetIsID: param1 is a base-object FormID.
        Assert.Equal(ConditionParamKind.FormId, table.ClassifyParam(0x048, 0));
        // GetActorValue: param1 is an AV index — numeric.
        Assert.Equal(ConditionParamKind.Numeric, table.ClassifyParam(0x00E, 0));
        // GetStageDone: (Quest FormID, Stage number).
        Assert.Equal(ConditionParamKind.FormId, table.ClassifyParam(0x03B, 0));
        Assert.Equal(ConditionParamKind.Numeric, table.ClassifyParam(0x03B, 1));
    }

    [Fact]
    public void FalloutClassifier_UsesNumericActorValueEnumWithoutFormIdResolution()
    {
        var fallout3 = ConditionFunctionTable.For(BethesdaGame.Fallout3);
        var fnv = ConditionFunctionTable.For(BethesdaGame.FalloutNewVegas);
        Assert.Equal(ConditionParamKind.Numeric, fallout3.ClassifyParam(0x00E, 0));
        Assert.Equal(ConditionParamKind.Numeric, fnv.ClassifyParam(0x00E, 0));

        var condition = new DialogueCondition { FunctionIndex = 0x00E, Parameter1 = 5 };
        var formatted = DialogueConditionDisplayFormatter.FormatCondition(
            condition,
            _ => throw new InvalidOperationException("ActorValue must not be resolved as a FormID."),
            game: BethesdaGame.FalloutNewVegas);

        Assert.Contains("GetActorValue(5)", formatted, StringComparison.Ordinal);
        Assert.False(DialogueConditionDisplayFormatter.IsFormReference(
            condition, 0, BethesdaGame.FalloutNewVegas));
    }

    [Fact]
    public void FalloutClassifier_MatchesTheFormatterForRepresentativeConditions()
    {
        // The shared classifier must agree with IsFormReference for representative FNV slots.
        var samples = new (ushort FunctionIndex, int ParamIndex, bool ExpectFormId)[]
        {
            (0x02F, 0, true), // GetItemCount(ObjectID)
            (0x00E, 0, false), // GetActorValue — ActorValue enum index
            (0x04F, 0, true), // GetQuestVariable(Quest, var) — quest is a FormID
            (0x04F, 1, false), // …the variable index is numeric (ScriptVar)
            (0x00A, 0, false), // GetStartingPos(Axis)
            (0x046, 0, false) // GetIsSex(Sex)
        };

        foreach (var (functionIndex, paramIndex, expectFormId) in samples)
        {
            var condition = new DialogueCondition
            {
                FunctionIndex = functionIndex,
                Parameter1 = 0x1234,
                Parameter2 = 0x5678
            };
            var actual = DialogueConditionDisplayFormatter.IsFormReference(condition, paramIndex);
            Assert.True(expectFormId == actual,
                $"0x{functionIndex:X3}[{paramIndex}]: expected FormId={expectFormId}, got {actual}");
        }
    }
}
