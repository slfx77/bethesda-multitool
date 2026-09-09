using System.Numerics;
using BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.BrotherhoodOfSteel;

/// <summary>
///     Opt-in checks (<c>RUN_BUCKET_B=1</c>) of the <c>.CUT</c> cutscene scripts: the loose Xbox
///     files, and the same scripts sitting hash-addressed inside <c>sfx.clp</c> on BOTH discs.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class BosCutsceneRetailTests
{
    /// <summary>The two loose files that do NOT tile: an older layout that appears in no clump.</summary>
    private static readonly string[] NotInAnyClump = ["Scene_startTown.cut", "scene_townkill.cut"];

    private static string RequireResx()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var extracted = RealAssetPaths.Consoles.BrotherhoodOfSteelXboxExtracted();
        Assert.SkipWhen(extracted is null,
            RealAssetPaths.SkipMessage("the extracted Fallout: Brotherhood of Steel Xbox disc"));
        var resx = Path.Combine(extracted, "resx");
        Assert.SkipWhen(!Directory.Exists(resx), RealAssetPaths.SkipMessage("the Xbox disc's resx directory"));
        return resx;
    }

    private static List<string> LooseFiles(string resx)
    {
        return [.. Directory.GetFiles(resx, "*.cut").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)];
    }

    [Fact]
    public void TwentyNineOfTheThirtyOneLooseFilesTileExactly()
    {
        var resx = RequireResx();
        var files = LooseFiles(resx);
        Assert.Equal(31, files.Count);

        var tiled = new List<string>();
        var refused = new List<string>();
        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            if (BosCutscene.TryParse(File.ReadAllBytes(path), name, out _, out _))
            {
                tiled.Add(name);
            }
            else
            {
                refused.Add(name);
            }
        }

        Assert.Equal(29, tiled.Count);
        Assert.Equal(NotInAnyClump.OrderBy(n => n, StringComparer.OrdinalIgnoreCase),
            refused.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheLooseFilesAreByteIdenticalToTheSectionsInsideBothDiscsSoundClump()
    {
        var resx = RequireResx();
        var iso = RealAssetPaths.Consoles.BrotherhoodOfSteelIso();
        Assert.SkipWhen(iso is null, RealAssetPaths.SkipMessage("Fallout: Brotherhood of Steel disc image"));

        var loose = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in LooseFiles(resx))
        {
            var bytes = File.ReadAllBytes(path);
            if (BosCutscene.IsCutscene(bytes))
            {
                loose[Path.GetFileName(path)] = bytes;
            }
        }

        Assert.Equal(29, loose.Count);

        var xbox = File.ReadAllBytes(Path.Combine(resx, "sfx.clp"));
        using var disc = ArchiveReader.Open(iso);
        var entry = disc.ListFiles().Single(e => string.Equals(e.Name, "SFX.CLP", StringComparison.OrdinalIgnoreCase));
        var ps2 = disc.ReadFile(entry.FullPath);
        Assert.NotNull(ps2);

        foreach (var (label, clumpBytes, expectedSections) in
                 new[] { ("Xbox sfx.clp", xbox, 3170), ("PS2 SFX.CLP", ps2, 3178) })
        {
            var clump = BosClumpFile.Parse(clumpBytes, label);
            Assert.Equal(expectedSections, clump.Sections.Count);

            // ⚑ The key is the LOWERCASED file name: 29 of 29 resolve that way and 8 of 29 with the
            // name's own case, so the spelling is not a free choice.
            var identical = 0;
            foreach (var (name, bytes) in loose)
            {
                Assert.True(clump.TryFind(name.ToLowerInvariant(), out var section),
                    $"{label} has no section for {name}");
                if (BosClumpFile.Read(clumpBytes, section).SequenceEqual(bytes))
                {
                    identical++;
                }
            }

            Assert.Equal(29, identical);

            // ⛔ THE CONTROL: of the clump's thousands of sections, exactly the 29 tile as a
            // cutscene, so the gate is not admitting arbitrary bytes.
            var tiling = clump.Sections.Count(s => BosCutscene.IsCutscene(BosClumpFile.Read(clumpBytes, s)));
            Assert.Equal(29, tiling);
        }
    }

    [Fact]
    public void TheRecordsReadAsAScriptAndMostlyNameTheirOwnObjectTable()
    {
        // Census over the 29 tiling files. Each figure is a separate way the reading could break:
        // a wrong record stride would scatter the target field, and a wrong object stride would
        // stop the actor ids resolving.
        var resx = RequireResx();
        var records = 0;
        var scene = 0;
        var camera = 0;
        var actor = 0;
        var actorResolved = 0;
        var ends = 0;
        var files = 0;
        var sorted = 0;
        var named = 0;

        foreach (var path in LooseFiles(resx))
        {
            if (!BosCutscene.TryParse(File.ReadAllBytes(path), Path.GetFileName(path), out var cut, out _))
            {
                continue;
            }

            files++;
            sorted += cut.IsTimeSorted ? 1 : 0;
            var ids = cut.Objects.Select(o => o.EntityId).ToHashSet();

            foreach (var record in cut.Records)
            {
                records++;
                named += record.CommandName.StartsWith("cmd", StringComparison.Ordinal) ? 0 : 1;
                switch (record.Kind)
                {
                    case BosCutsceneTargetKind.Scene:
                        scene++;
                        ends += record.Scene == BosSceneCommand.End ? 1 : 0;
                        break;
                    case BosCutsceneTargetKind.Camera:
                        camera++;
                        break;
                    default:
                        actor++;
                        actorResolved += ids.Contains(record.Target) ? 1 : 0;
                        break;
                }
            }
        }

        Assert.Equal(29, files);
        Assert.Equal(1629, records);
        Assert.Equal(374, scene);
        Assert.Equal(612, camera);
        Assert.Equal(643, actor);

        // ⚑ 636 of the 643 actor records name an id that is in their own file's object table.
        Assert.Equal(636, actorResolved);

        // ⚑ Exactly one End per file — the command that tears the player down.
        Assert.Equal(29, ends);

        // ⚠ Three files are not time-sorted, though the engine's seek helpers assume they are.
        Assert.Equal(26, sorted);

        // ⚑ Every one of the 1,629 records carries a code the game's own code names — including the
        // camera's 7/9/10, which are not in the record switch at all but in the interpolation block
        // at the end of 0x0006CB80. The raw fall-back is still there (the synthetic vectors exercise
        // it); the shipped scripts simply never need it.
        Assert.Equal(1629, named);
    }

    [Fact]
    public void OneShippedScriptDumpsAsReadableText()
    {
        var resx = RequireResx();
        var cut = BosCutscene.Parse(File.ReadAllBytes(Path.Combine(resx, "res_1_door.cut")), "res_1_door.cut");
        var text = cut.Describe();

        // res_1_door.cut: 5 objects, 15 records — a camera move ending in the scene's End.
        Assert.Equal(5, cut.Objects.Count);
        Assert.Equal(15, cut.Records.Count);
        Assert.Contains("object 0   id 0x9293682B", text, StringComparison.Ordinal);
        Assert.Contains("KeyPosition", text, StringComparison.Ordinal);
        Assert.Contains("SetOrientation", text, StringComparison.Ordinal);
        Assert.Contains("End", text, StringComparison.Ordinal);

        var directory = Environment.GetEnvironmentVariable("BOS_XBOX_GLB_DIR");
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "res_1_door.cut.txt"), text);
        }
    }

    [Fact]
    public void TheCameraKeysThirdComponentIsTheTightVerticalOne()
    {
        // ⚑ THE OTHER HALF of the Z-up evidence in BosXboxMeshGlbExporter, pinned here because it
        // lives in this format. A camera travels far horizontally and little vertically, so the
        // vertical lane is the one with the small span — components 1 and 2 span 6,288 and 6,350
        // units, component 3 only 1,084. ⚠ The exporter's comment quoted "129 … 357" for that lane
        // until 2026-09-08; no subset of these records produces it, and this test now fixes the
        // real numbers. It could fail on any edit that changed the parameter order or the record
        // stride, which would scramble which lane is tight.
        var resx = RequireResx();
        var lanes = new List<int>[] { [], [], [] };
        var setPosition = 0;
        var keyPosition = 0;

        foreach (var path in LooseFiles(resx))
        {
            if (!BosCutscene.TryParse(File.ReadAllBytes(path), Path.GetFileName(path), out var cut, out _))
            {
                continue;
            }

            foreach (var record in cut.Records)
            {
                if (record.Camera is not (BosCameraCommand.SetPosition or BosCameraCommand.KeyPosition))
                {
                    continue;
                }

                if (record.Camera == BosCameraCommand.SetPosition)
                {
                    setPosition++;
                }
                else
                {
                    keyPosition++;
                }

                lanes[0].Add(record.P0);
                lanes[1].Add(record.P1);
                lanes[2].Add(record.P2);
            }
        }

        Assert.Equal(48, setPosition);
        Assert.Equal(257, keyPosition);
        Assert.Equal(305, lanes[0].Count);

        Assert.Equal(-2991, lanes[0].Min());
        Assert.Equal(3297, lanes[0].Max());
        Assert.Equal(-3505, lanes[1].Min());
        Assert.Equal(2845, lanes[1].Max());
        Assert.Equal(-100, lanes[2].Min());
        Assert.Equal(984, lanes[2].Max());
    }

    [Fact]
    public void ObjectStartPositionsPutTheActorsOnTheFloorInTheThirdComponent()
    {
        // ⚑ A THIRD Z-up population, independent of both the mesh bounds and the camera keys:
        // scene actors are placed by hand in the editor and they stand on the ground. 177 of the
        // 263 object start positions sit within 2 units of zero in the third component against
        // 1 of 263 in each of the other two, which is the discrimination — had the vertical lane
        // been the first or the second, those counts would have swapped.
        var resx = RequireResx();
        var near = new int[3];
        var min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
        var max = new[] { float.MinValue, float.MinValue, float.MinValue };
        var objects = 0;

        foreach (var path in LooseFiles(resx))
        {
            if (!BosCutscene.TryParse(File.ReadAllBytes(path), Path.GetFileName(path), out var cut, out _))
            {
                continue;
            }

            foreach (var entry in cut.Objects)
            {
                objects++;
                var lanes = new[] { entry.StartPosition.X, entry.StartPosition.Y, entry.StartPosition.Z };
                for (var axis = 0; axis < 3; axis++)
                {
                    if (Math.Abs(lanes[axis]) < 2f)
                    {
                        near[axis]++;
                    }

                    min[axis] = Math.Min(min[axis], lanes[axis]);
                    max[axis] = Math.Max(max[axis], lanes[axis]);
                }
            }
        }

        Assert.Equal(263, objects);
        Assert.Equal(1, near[0]);
        Assert.Equal(1, near[1]);
        Assert.Equal(177, near[2]);

        // The vertical lane's whole range is two orders of magnitude tighter than the other two.
        Assert.True(max[2] - min[2] < 200f, $"third-component span was {max[2] - min[2]}");
        Assert.True(max[0] - min[0] > 5000f, $"first-component span was {max[0] - min[0]}");
        Assert.True(max[1] - min[1] > 5000f, $"second-component span was {max[1] - min[1]}");
    }

    [Fact]
    public void AnObjectsSecondPositionIsNotAlwaysACopyOfItsStart()
    {
        // ⚠ The doc on BosCutsceneObject.SecondPosition claimed "equal on every shipped entry"
        // until 2026-09-08. It is equal on 225 of the 263 objects but DIFFERS on 38, so the field
        // carries information and must not be dropped as a duplicate. This test fails the moment
        // either count moves.
        var resx = RequireResx();
        var equal = 0;
        var differing = 0;
        var differingAndZero = 0;
        var filesWithADifference = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in LooseFiles(resx))
        {
            var name = Path.GetFileName(path);
            if (!BosCutscene.TryParse(File.ReadAllBytes(path), name, out var cut, out _))
            {
                continue;
            }

            foreach (var entry in cut.Objects)
            {
                if (entry.SecondPosition == entry.StartPosition)
                {
                    equal++;
                    continue;
                }

                differing++;
                filesWithADifference.Add(name);
                if (entry.SecondPosition == Vector3.Zero)
                {
                    differingAndZero++;
                }
            }
        }

        Assert.Equal(225, equal);
        Assert.Equal(38, differing);

        // ⚑ None of the 38 is a cleared field — each is a distinct non-zero point.
        Assert.Equal(0, differingAndZero);
        Assert.Equal(
            new[] { "res1_piss.cut", "Scene_Mov1A.cut", "Scene_Mov1B.cut", "Scene_Mov1E.cut", "Scene_Mov3K.cut" }
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase),
            filesWithADifference);
    }

    [Fact]
    public void EveryNamedCommandCodeAppearsWithTheCountTheDiscCarries()
    {
        var resx = RequireResx();
        var histogram = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var path in LooseFiles(resx))
        {
            if (!BosCutscene.TryParse(File.ReadAllBytes(path), Path.GetFileName(path), out var cut, out _))
            {
                continue;
            }

            foreach (var record in cut.Records)
            {
                var key = $"{record.Kind}.{record.CommandName}";
                histogram[key] = histogram.GetValueOrDefault(key) + 1;
            }
        }

        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Actor.MoveWalk"] = 145,
            ["Actor.PlayAnimation"] = 194,
            ["Actor.MoveRun"] = 66,
            ["Actor.Die"] = 10,
            ["Actor.SetTarget"] = 18,
            ["Actor.CutAnimation"] = 61,
            ["Actor.Effect"] = 149,
            ["Camera.SetPosition"] = 48,
            ["Camera.KeyPosition"] = 257,
            ["Camera.SetOrientation"] = 48,
            ["Camera.KeyOrientation"] = 254,
            ["Camera.SetFarClip"] = 5,
            ["Scene.End"] = 29,
            ["Scene.FadeIn"] = 26,
            ["Scene.FadeOut"] = 33,
            ["Scene.SetBrightnessLimit"] = 20,
            ["Scene.SetTimeScale"] = 33,
            ["Scene.SetCameraShake"] = 17,
            ["Scene.RunCutScript"] = 216
        };

        Assert.Equal(expected, histogram);
    }
}