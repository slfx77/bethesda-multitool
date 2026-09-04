using BethesdaMultitool.CLI.Rendering.Npc;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class NpcTextureHelpersTests
{
    [Fact]
    public void BuildNpcGeneratedTextureKeys_ReturnsCompleteVariantScopedSet()
    {
        var appearance = new NpcAppearance
        {
            NpcFormId = 0x00ABCDEF,
            RenderVariantLabel = "npc_plus_race"
        };

        Assert.Collection(
            NpcTextureHelpers.BuildNpcGeneratedTextureKeys(appearance),
            key => Assert.Equal(@"facegen_egt\00ABCDEF_npc_plus_race.dds", key),
            key => Assert.Equal(@"body_egt\00ABCDEF_npc_plus_race_ears.dds", key),
            key => Assert.Equal(@"body_egt\00ABCDEF_npc_plus_race_upperbody.dds", key),
            key => Assert.Equal(@"body_egt\00ABCDEF_npc_plus_race_lefthand.dds", key),
            key => Assert.Equal(@"body_egt\00ABCDEF_npc_plus_race_righthand.dds", key));
    }

    [Theory]
    [InlineData(@"textures\characters\imperial\female\UpperBodyFemale.dds")]
    [InlineData(@"textures\characters\orc\male\UpperBodyMale.dds")]
    [InlineData(@"textures/characters/argonian/female/HandFemale.dds")]
    [InlineData(@"textures\characters\_male\upperbody.dds")]
    public void IsEquipmentSkinSubmesh_RecognizesRaceNestedBodyTextures(string path)
    {
        Assert.True(NpcTextureHelpers.IsEquipmentSkinSubmesh(path));
    }

    [Theory]
    [InlineData(@"textures\characters\hair\OrcMaleStubs.dds")]
    [InlineData(@"textures\characters\imperial\female\HeadHuman.dds")]
    [InlineData(@"textures\characters\imperial\eyes\Eye.dds")]
    [InlineData(@"textures\characters\_female\underwear.dds")]
    [InlineData(@"textures\armor\iron\f\Cuirass.dds")]
    public void IsEquipmentSkinSubmesh_DoesNotReplaceNonSkinEquipmentTextures(string path)
    {
        Assert.False(NpcTextureHelpers.IsEquipmentSkinSubmesh(path));
    }
}
