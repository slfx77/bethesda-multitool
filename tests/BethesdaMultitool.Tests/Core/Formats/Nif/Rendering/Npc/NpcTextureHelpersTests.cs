using BethesdaMultitool.CLI.Rendering.Npc;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class NpcTextureHelpersTests
{
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
