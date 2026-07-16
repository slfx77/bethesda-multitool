using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class MoonGmstRegistryTests
{
    [Fact]
    public void BuildGameSettingIndex_PreservesCelestialOverridesCaseInsensitively()
    {
        var settings = new List<GameSettingRecord>
        {
            FloatSetting(0x101, "fMasserSpeed", 0.42f),
            FloatSetting(0x102, "fMasserZOffset", 31f),
            FloatSetting(0x103, "fMasserAngleFadeStart", 38f),
            FloatSetting(0x104, "fMasserAngleFadeEnd", 17f),
            FloatSetting(0x105, "fSecundaSpeed", 0.33f),
            FloatSetting(0x106, "fSecundaZOffset", 49f),
            FloatSetting(0x107, "fSecundaAngleFadeStart", 36f),
            FloatSetting(0x108, "fSecundaAngleFadeEnd", 19f),
        };

        var index = GameSettingRegistry.BuildIndex(settings);

        Assert.Equal(settings.Count, index.Count);
        Assert.Equal(0.42f, index["FMASSERSPEED"].FloatValue);
        Assert.Equal(17f, index["fmasseranglefadeend"].FloatValue);
        Assert.Equal(0.33f, index["FSECUNDASPEED"].FloatValue);
        Assert.Equal(19f, index["fsecundaanglefadeend"].FloatValue);
    }

    [Fact]
    public void BuildGameSettingIndex_LoadOrderOverrideWinsBeforeLookup()
    {
        const uint masserSpeedFormId = 0x0002D4E7;
        var master = new RecordCollection
        {
            GameSettings = [FloatSetting(masserSpeedFormId, "fMasserSpeed", 0.25f)]
        };
        var plugin = new RecordCollection
        {
            GameSettings = [FloatSetting(masserSpeedFormId, "fMasserSpeed", 0.42f)]
        };

        var merged = master.MergeWith(plugin);
        var index = GameSettingRegistry.BuildIndex(merged.GameSettings);

        Assert.Single(merged.GameSettings);
        Assert.Equal(0.42f, index["FMASSERSPEED"].FloatValue);
    }

    private static GameSettingRecord FloatSetting(uint formId, string editorId, float value) => new()
    {
        FormId = formId,
        EditorId = editorId,
        ValueType = GameSettingType.Float,
        FloatValue = value,
    };
}
