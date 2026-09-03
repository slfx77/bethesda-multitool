using BethesdaMultitool.CLI.Rendering.Npc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class NpcAppearanceSkeletonSelectionTests
{
    private const string HumanoidSkeleton = @"meshes\characters\_Male\skeleton.nif";
    private const string BeastSkeleton = @"meshes\characters\_Male\skeletonbeast.nif";

    [Theory]
    [InlineData("Argonian", true, @"Characters\Argonian\Tail.NIF", BeastSkeleton)]
    [InlineData("Khajiit", false, @"Characters\Khajiit\KhajiitTail.NIF", BeastSkeleton)]
    [InlineData("Imperial", false, null, HumanoidSkeleton)]
    public void OblivionFactory_SelectsSkeletonFromResolvedRaceTail_InBothBuildPaths(
        string raceEditorId,
        bool isFemale,
        string? tailPath,
        string expectedSkeleton)
    {
        const uint raceFormId = 0x1234;
        const uint npcFormId = 0x5678;
        var index = new NpcAppearanceIndex { Game = BethesdaGame.Oblivion };
        index.Races[raceFormId] = new RaceScanEntry
        {
            EditorId = raceEditorId,
            MaleTailPath = tailPath,
            FemaleTailPath = tailPath
        };

        var factory = new NpcAppearanceFactory(index);
        var scannedAppearance = factory.Build(
            npcFormId,
            new NpcScanEntry
            {
                RaceFormId = raceFormId,
                IsFemale = isFemale
            },
            "Oblivion.esm");
        var runtimeAppearance = factory.BuildFromDmpRecord(
            new NpcRecord
            {
                FormId = npcFormId,
                Race = raceFormId,
                Stats = new ActorBaseSubrecord(
                    isFemale ? 1u : 0u,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    false)
            },
            "Oblivion.esm");

        Assert.Equal(expectedSkeleton, scannedAppearance.SkeletonNifPath);
        Assert.Equal(expectedSkeleton, runtimeAppearance.SkeletonNifPath);
    }

    [Theory]
    [InlineData(HumanoidSkeleton)]
    [InlineData(BeastSkeleton)]
    public void AdjacentAnimationPath_DoesNotDependOnCanonicalSkeletonFileName(string skeletonPath)
    {
        Assert.Equal(
            @"meshes\characters\_Male\",
            NpcSkeletonLoader.ResolveSkeletonDirectory(skeletonPath));
        Assert.Equal(
            @"meshes\characters\_Male\idle.kf",
            NpcSkeletonLoader.ResolveAnimationAssetPath(skeletonPath, "idle.kf"));
    }
}
