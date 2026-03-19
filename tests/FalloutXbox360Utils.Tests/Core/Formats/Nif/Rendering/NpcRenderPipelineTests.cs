using FalloutXbox360Utils.CLI.Rendering.Npc;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class NpcRenderPipelineTests
{
    [Fact]
    public void BuildTextureComparisonVariants_ProducesNpcOnlyAndNpcPlusRaceVariants()
    {
        var appearance = new NpcAppearance
        {
            NpcFormId = 0x112640,
            EditorId = "VStreetDennisCrocker",
            FaceGenTextureCoeffs = [0.5f],
            NpcFaceGenTextureCoeffs = [0.5f],
            RaceFaceGenTextureCoeffs = [0.25f]
        };

        var variants = NpcRenderPipeline.BuildTextureComparisonVariants([appearance]);

        Assert.Collection(
            variants,
            npcOnly =>
            {
                Assert.Equal("npc_only", npcOnly.RenderVariantLabel);
                Assert.Equal([0.5f], npcOnly.FaceGenTextureCoeffs!);
                Assert.Equal([0.5f], npcOnly.NpcFaceGenTextureCoeffs!);
                Assert.Equal([0.25f], npcOnly.RaceFaceGenTextureCoeffs!);
            },
            npcPlusRace =>
            {
                Assert.Equal("npc_plus_race", npcPlusRace.RenderVariantLabel);
                Assert.Equal([0.75f], npcPlusRace.FaceGenTextureCoeffs!);
                Assert.Equal([0.5f], npcPlusRace.NpcFaceGenTextureCoeffs!);
                Assert.Equal([0.25f], npcPlusRace.RaceFaceGenTextureCoeffs!);
            });
    }
}