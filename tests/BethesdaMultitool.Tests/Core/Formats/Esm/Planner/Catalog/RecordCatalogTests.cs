using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Catalog;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Catalog;

public sealed class RecordCatalogTests
{
    [Fact]
    public void Duplicate_Dmp_Overrides_Keep_First_Capture_Only()
    {
        const uint formId = 0x0013F401;
        var first = GameSetting(formId, "iMaxCharacterLevel", 30, 0x1000);
        var second = GameSetting(formId, "iMaxCharacterLevel", 30, 0x2000);
        var third = GameSetting(formId, "iMaxCharacterLevel", 30, 0x3000);

        var entries = RecordCatalog.Build(
            new MasterRecordSource([MasterRecord("GMST", formId)]),
            new DmpRecordSource(new RecordCollection { GameSettings = [first, second, third] }),
            EnabledTypes("GMST"));

        var entry = Assert.Single(entries);
        Assert.Equal(SourceKind.DmpOverride, entry.Source);
        Assert.Equal(formId, entry.MasterFormId);
        Assert.Equal(formId, entry.DmpFormId);
        Assert.Same(first, entry.Model);
    }

    [Fact]
    public void Duplicate_Dmp_New_Records_Keep_First_Capture_Only()
    {
        const uint formId = 0x00ABCDEF;
        var first = GameSetting(formId, "iPrototypeSetting", 1, 0x1000);
        var second = GameSetting(formId, "iPrototypeSetting", 2, 0x2000);

        var entries = RecordCatalog.Build(
            new MasterRecordSource([]),
            new DmpRecordSource(new RecordCollection { GameSettings = [first, second] }),
            EnabledTypes("GMST"));

        var entry = Assert.Single(entries);
        Assert.Equal(SourceKind.DmpNew, entry.Source);
        Assert.Null(entry.MasterFormId);
        Assert.Equal(formId, entry.DmpFormId);
        Assert.Same(first, entry.Model);
    }

    [Fact]
    public void Same_FormId_Across_Record_Types_Remains_Distinct()
    {
        const uint formId = 0x00ABCDEF;
        var gameSetting = GameSetting(formId, "iPrototypeSetting", 1, 0x1000);
        var global = new GlobalRecord
        {
            FormId = formId,
            EditorId = "PrototypeGlobal",
            ValueType = 'f',
            Value = 1.0f,
            Offset = 0x2000
        };

        var entries = RecordCatalog.Build(
            new MasterRecordSource([]),
            new DmpRecordSource(new RecordCollection
            {
                GameSettings = [gameSetting],
                Globals = [global]
            }),
            EnabledTypes("GMST", "GLOB"));

        Assert.Equal(2, entries.Count);
        Assert.Same(gameSetting, Assert.Single(entries, entry => entry.Type == "GMST").Model);
        Assert.Same(global, Assert.Single(entries, entry => entry.Type == "GLOB").Model);
    }

    [Fact]
    public void Master_Alias_Pairs_Prototype_FormId_With_Master_Record()
    {
        const uint sourceFormId = 0x001251C2;
        const uint masterFormId = 0x0013408C;
        var prototype = GameSetting(sourceFormId, "SharedEditorId", 1, 0x1000);

        var entries = RecordCatalog.Build(
            new MasterRecordSource([MasterRecord("GMST", masterFormId)]),
            new DmpRecordSource(new RecordCollection { GameSettings = [prototype] }),
            EnabledTypes("GMST"),
            new Dictionary<uint, uint> { [sourceFormId] = masterFormId });

        var entry = Assert.Single(entries);
        Assert.Equal(SourceKind.DmpOverride, entry.Source);
        Assert.Equal(masterFormId, entry.MasterFormId);
        Assert.Equal(sourceFormId, entry.DmpFormId);
        Assert.Same(prototype, entry.Model);
    }

    [Fact]
    public void Exact_Master_FormId_Capture_Wins_Over_Alias_Regardless_Of_Order()
    {
        const uint sourceFormId = 0x001251C2;
        const uint masterFormId = 0x0013408C;
        var alias = GameSetting(sourceFormId, "SharedEditorId", 1, 0x1000);
        var exact = GameSetting(masterFormId, "SharedEditorId", 2, 0x2000);

        var entries = RecordCatalog.Build(
            new MasterRecordSource([MasterRecord("GMST", masterFormId)]),
            new DmpRecordSource(new RecordCollection { GameSettings = [alias, exact] }),
            EnabledTypes("GMST"),
            new Dictionary<uint, uint> { [sourceFormId] = masterFormId });

        var entry = Assert.Single(entries);
        Assert.Equal(masterFormId, entry.DmpFormId);
        Assert.Same(exact, entry.Model);
    }

    [Fact]
    public void Master_Alias_Must_Preserve_Record_Type()
    {
        const uint sourceFormId = 0x001251C2;
        const uint masterFormId = 0x0013408C;
        var prototype = GameSetting(sourceFormId, "SharedEditorId", 1, 0x1000);

        var error = Assert.Throws<InvalidOperationException>(() => RecordCatalog.Build(
            new MasterRecordSource([MasterRecord("GLOB", masterFormId)]),
            new DmpRecordSource(new RecordCollection { GameSettings = [prototype] }),
            EnabledTypes("GMST", "GLOB"),
            new Dictionary<uint, uint> { [sourceFormId] = masterFormId }));

        Assert.Contains("aliases master GLOB", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_Dmp_Records_Report_A_Warning_Diagnostic_With_Differs_Metadata()
    {
        const uint formId = 0x00ABCDEF;
        var first = GameSetting(formId, "iPrototypeSetting", 1, 0x1000);
        var second = GameSetting(formId, "iPrototypeSetting", 2, 0x2000);

        var entries = RecordCatalog.Build(
            new MasterRecordSource([]),
            new DmpRecordSource(new RecordCollection { GameSettings = [first, second] }),
            EnabledTypes("GMST"),
            null,
            out _,
            out var diagnostics);

        // Winner unchanged: first capture still wins, exactly as the public overload pins.
        var entry = Assert.Single(entries);
        Assert.Same(first, entry.Model);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(PlanDiagnosticKind.Warning, diagnostic.Kind);
        Assert.Equal("Catalog", diagnostic.Phase);
        Assert.Equal("catalog.duplicate-dmp-record", diagnostic.Code);
        Assert.Equal("GMST", diagnostic.RecordType);
        Assert.Equal(formId, diagnostic.FormId);
        Assert.NotNull(diagnostic.Metadata);
        Assert.Equal("GMST", diagnostic.Metadata!["type"]);
        Assert.Equal($"0x{formId:X8}", diagnostic.Metadata["formId"]);
        // GMST is a flat scalar record, so the classifier deep-compares it exactly: these two
        // captures hold IntValue 1 vs 2, which is a real content difference. (Before the deep
        // compare landed this reported "unknown" — the corpus's 584 GMST discards were all
        // unclassifiable, which is what motivated the change.)
        Assert.Equal("true", diagnostic.Metadata["differs"]);
    }

    [Fact]
    public void Duplicate_Dmp_Record_Of_The_Same_Instance_Reports_Differs_False()
    {
        const uint formId = 0x00ABCDEF;
        var only = GameSetting(formId, "iPrototypeSetting", 1, 0x1000);

        RecordCatalog.Build(
            new MasterRecordSource([]),
            new DmpRecordSource(new RecordCollection { GameSettings = [only, only] }),
            EnabledTypes("GMST"),
            null,
            out _,
            out var diagnostics);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("catalog.duplicate-dmp-record", diagnostic.Code);
        Assert.Equal("false", diagnostic.Metadata!["differs"]);
    }

    [Fact]
    public void Duplicate_Generic_Records_With_Different_Field_Counts_Report_Differs_True()
    {
        const uint formId = 0x00ABCDEF;
        var first = new GenericEsmRecord
        {
            FormId = formId,
            RecordType = "MSTT",
            EditorId = "ProtoMovableStatic",
            Offset = 0x1000
        };
        var second = first with
        {
            Offset = 0x2000,
            Fields = new Dictionary<string, object?> { ["DATA"] = (byte)1 }
        };

        var entries = RecordCatalog.Build(
            new MasterRecordSource([]),
            new DmpRecordSource(new RecordCollection { GenericRecords = [first, second] }),
            EnabledTypes("MSTT"),
            null,
            out _,
            out var diagnostics);

        Assert.Same(first, Assert.Single(entries).Model);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("catalog.duplicate-dmp-record", diagnostic.Code);
        Assert.Equal("true", diagnostic.Metadata!["differs"]);
    }

    [Fact]
    public void Alias_Shadowed_By_Exact_Capture_Reports_A_Warning_Diagnostic()
    {
        const uint sourceFormId = 0x001251C2;
        const uint masterFormId = 0x0013408C;
        var exact = GameSetting(masterFormId, "SharedEditorId", 2, 0x1000);
        var alias = GameSetting(sourceFormId, "SharedEditorId", 1, 0x2000);

        var entries = RecordCatalog.Build(
            new MasterRecordSource([MasterRecord("GMST", masterFormId)]),
            new DmpRecordSource(new RecordCollection { GameSettings = [exact, alias] }),
            EnabledTypes("GMST"),
            new Dictionary<uint, uint> { [sourceFormId] = masterFormId },
            out var validatedAliases,
            out var diagnostics);

        // Winner unchanged: the exact capture keeps the master slot; the shadowed alias is
        // still a validated reference alias.
        var entry = Assert.Single(entries);
        Assert.Same(exact, entry.Model);
        Assert.Equal(masterFormId, validatedAliases[sourceFormId]);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(PlanDiagnosticKind.Warning, diagnostic.Kind);
        Assert.Equal("Catalog", diagnostic.Phase);
        Assert.Equal("catalog.alias-shadowed-by-exact", diagnostic.Code);
        Assert.Equal("GMST", diagnostic.RecordType);
        Assert.Equal(sourceFormId, diagnostic.FormId);
        Assert.Equal($"0x{masterFormId:X8}", diagnostic.Metadata!["masterFormId"]);
    }

    [Fact]
    public void Clean_Catalog_Produces_No_Diagnostics()
    {
        const uint formId = 0x0013F401;
        var single = GameSetting(formId, "iMaxCharacterLevel", 30, 0x1000);

        RecordCatalog.Build(
            new MasterRecordSource([MasterRecord("GMST", formId)]),
            new DmpRecordSource(new RecordCollection { GameSettings = [single] }),
            EnabledTypes("GMST"),
            null,
            out _,
            out var diagnostics);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Duplicate_GameSettings_Identical_Except_Capture_Offset_Report_Differs_False()
    {
        // The whole point of the Offset exclusion: one setting captured twice at two heap
        // addresses is one setting, not a conflict. This is the shape the corpus actually
        // produces (a repeated runtime GMST GRUP snapshot).
        const uint formId = 0x00ABCDEF;
        var first = GameSetting(formId, "iPrototypeSetting", 7, 0x1000);
        var second = GameSetting(formId, "iPrototypeSetting", 7, 0x9999);

        var diagnostic = Assert.Single(DuplicateGameSettingDiagnostics(first, second));
        Assert.Equal("false", diagnostic.Metadata!["differs"]);
    }

    [Theory]
    [InlineData("float")]
    [InlineData("int")]
    [InlineData("string")]
    [InlineData("valueType")]
    [InlineData("editorId")]
    public void Duplicate_GameSettings_Differing_In_Any_Value_Member_Report_Differs_True(string member)
    {
        const uint formId = 0x00ABCDEF;
        var first = new GameSettingRecord
        {
            FormId = formId,
            EditorId = "iPrototypeSetting",
            ValueType = GameSettingType.Integer,
            IntValue = 7,
            Offset = 0x1000
        };

        // ValueType alone is the case a naive "compare whichever value field is non-null"
        // comparer misses: Integer and Boolean both carry their value in IntValue.
        var second = member switch
        {
            "float" => first with { ValueType = GameSettingType.Float, FloatValue = 1.5f, IntValue = null },
            "int" => first with { IntValue = 8 },
            "string" => first with { ValueType = GameSettingType.String, StringValue = "x", IntValue = null },
            "valueType" => first with { ValueType = GameSettingType.Boolean },
            _ => first with { EditorId = "iOtherSetting" }
        };
        second = second with { Offset = 0x2000 };

        var diagnostic = Assert.Single(DuplicateGameSettingDiagnostics(first, second));
        Assert.Equal("true", diagnostic.Metadata!["differs"]);
    }

    [Fact]
    public void Duplicate_GameSettings_Holding_NaN_Report_Differs_False()
    {
        // Record equality routes float? through Single.Equals, which treats NaN as equal to NaN
        // (unlike operator ==). A corrupt capture read twice must not look like an endless
        // difference.
        const uint formId = 0x00ABCDEF;
        var first = new GameSettingRecord
        {
            FormId = formId,
            EditorId = "fBrokenSetting",
            ValueType = GameSettingType.Float,
            FloatValue = float.NaN,
            Offset = 0x1000
        };
        var second = first with { Offset = 0x2000 };

        var diagnostic = Assert.Single(DuplicateGameSettingDiagnostics(first, second));
        Assert.Equal("false", diagnostic.Metadata!["differs"]);
    }

    [Fact]
    public void Duplicate_Globals_Are_Deep_Compared_Like_GameSettings()
    {
        const uint formId = 0x00AB1234;
        var first = new GlobalRecord
        {
            FormId = formId, EditorId = "TestGlobal", ValueType = 'f', Value = 1f, Offset = 0x1000
        };
        var same = first with { Offset = 0x2000 };
        var different = first with { Offset = 0x3000, Value = 2f };

        var sameDiagnostic = Assert.Single(GlobalDuplicateDiagnostics(first, same));
        Assert.Equal("false", sameDiagnostic.Metadata!["differs"]);

        var differentDiagnostic = Assert.Single(GlobalDuplicateDiagnostics(first, different));
        Assert.Equal("true", differentDiagnostic.Metadata!["differs"]);
    }

    private static IReadOnlyList<PlanDiagnostic> DuplicateGameSettingDiagnostics(
        GameSettingRecord first,
        GameSettingRecord second)
    {
        RecordCatalog.Build(
            new MasterRecordSource([]),
            new DmpRecordSource(new RecordCollection { GameSettings = [first, second] }),
            EnabledTypes("GMST"),
            null,
            out _,
            out var diagnostics);
        return diagnostics;
    }

    private static IReadOnlyList<PlanDiagnostic> GlobalDuplicateDiagnostics(
        GlobalRecord first,
        GlobalRecord second)
    {
        RecordCatalog.Build(
            new MasterRecordSource([]),
            new DmpRecordSource(new RecordCollection { Globals = [first, second] }),
            EnabledTypes("GLOB"),
            null,
            out _,
            out var diagnostics);
        return diagnostics;
    }

    private static GameSettingRecord GameSetting(
        uint formId,
        string editorId,
        int value,
        long offset)
    {
        return new GameSettingRecord
        {
            FormId = formId,
            EditorId = editorId,
            // An "i"-prefixed setting really is Integer-typed; leaving ValueType at its default
            // would build a Float-typed record carrying an IntValue, which no real parse produces.
            ValueType = GameSettingType.Integer,
            IntValue = value,
            Offset = offset
        };
    }

    private static ParsedMainRecord MasterRecord(string type, uint formId)
    {
        return new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = type,
                DataSize = 0,
                Flags = 0,
                FormId = formId,
                Timestamp = 0,
                VcsInfo = 0,
                Version = 15
            },
            Offset = 0
        };
    }

    private static HashSet<string> EnabledTypes(params string[] types)
    {
        return new HashSet<string>(types, StringComparer.Ordinal);
    }
}