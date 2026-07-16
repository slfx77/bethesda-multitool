using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
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
            Offset = 0x2000,
        };

        var entries = RecordCatalog.Build(
            new MasterRecordSource([]),
            new DmpRecordSource(new RecordCollection
            {
                GameSettings = [gameSetting],
                Globals = [global],
            }),
            EnabledTypes("GMST", "GLOB"));

        Assert.Equal(2, entries.Count);
        Assert.Same(gameSetting, Assert.Single(entries, entry => entry.Type == "GMST").Model);
        Assert.Same(global, Assert.Single(entries, entry => entry.Type == "GLOB").Model);
    }

    private static GameSettingRecord GameSetting(
        uint formId,
        string editorId,
        int value,
        long offset) =>
        new()
        {
            FormId = formId,
            EditorId = editorId,
            IntValue = value,
            Offset = offset,
        };

    private static ParsedMainRecord MasterRecord(string type, uint formId) =>
        new()
        {
            Header = new MainRecordHeader
            {
                Signature = type,
                DataSize = 0,
                Flags = 0,
                FormId = formId,
                Timestamp = 0,
                VcsInfo = 0,
                Version = 15,
            },
            Offset = 0,
        };

    private static HashSet<string> EnabledTypes(params string[] types) =>
        new(types, StringComparer.Ordinal);
}
