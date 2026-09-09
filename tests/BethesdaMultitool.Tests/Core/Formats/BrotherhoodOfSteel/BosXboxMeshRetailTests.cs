using System.Numerics;
using BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.BrotherhoodOfSteel;

/// <summary>
///     Opt-in checks (<c>RUN_BUCKET_B=1</c>) of the Xbox mesh sections against the shipped disc,
///     with the PS2 twins of the same clumps as the control.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class BosXboxMeshRetailTests
{
    /// <summary>The clumps the mesh census covers, relative to <c>extracted\resx</c>.</summary>
    private static readonly string[] Clumps =
    [
        "armor.clp",
        "global.clp",
        "cain.clp",
        "robtur.clp",
        "sfx.clp",
        Path.Combine("c1", "BAR", "BAR.clp")
    ];

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

    /// <summary>Walks one clump and hands back every section that parses as a mesh, with its tag.</summary>
    private static List<(uint Tag, BosXboxMesh Mesh)> Meshes(string resx, string relative, out int sectionCount)
    {
        var bytes = File.ReadAllBytes(Path.Combine(resx, relative));
        var clump = BosClumpFile.Parse(bytes, relative);
        sectionCount = clump.Sections.Count;

        var meshes = new List<(uint, BosXboxMesh)>();
        foreach (var section in clump.Sections)
        {
            var payload = BosClumpFile.Read(bytes, section);
            if (BosXboxMesh.TryParse(payload, $"{relative}#{section.Tag:X8}", out var mesh, out _))
            {
                meshes.Add((section.Tag, mesh));
            }
        }

        return meshes;
    }

    [Fact]
    public void TheSixClumpsYieldExactlyThreeHundredAndThirteenTilingMeshes()
    {
        // ⚑ Per-clump counts, so a change that moves meshes between files cannot hide behind the
        // total: armor 92 of 184 sections, global 153 of 153, cain 2 of 5, robtur 2 of 8,
        // sfx 2 of 3,170, BAR 62 of 236.
        var expected = new Dictionary<string, (int Sections, int Meshes)>(StringComparer.Ordinal)
        {
            ["armor.clp"] = (184, 92),
            ["global.clp"] = (153, 153),
            ["cain.clp"] = (5, 2),
            ["robtur.clp"] = (8, 2),
            ["sfx.clp"] = (3170, 2),
            [Path.Combine("c1", "BAR", "BAR.clp")] = (236, 62)
        };

        var resx = RequireResx();
        var total = 0;
        foreach (var relative in Clumps)
        {
            var meshes = Meshes(resx, relative, out var sections);
            Assert.Equal(expected[relative], (sections, meshes.Count));
            total += meshes.Count;
        }

        Assert.Equal(313, total);
    }

    [Fact]
    public void EveryStrideIsOneOfTheTwoTheShaderDeclarationsUse()
    {
        // 175 skin/flat at 32 bytes, 138 halo at 38. ⚠ NOTHING on the disc uses the declared
        // 16-byte world layout, so this pins the absence too — if a future walk started admitting
        // junk sections, the stride histogram is where it would show first.
        var resx = RequireResx();
        var strides = new Dictionary<int, int>();
        foreach (var relative in Clumps)
        {
            foreach (var (_, mesh) in Meshes(resx, relative, out _))
            {
                strides[mesh.Stride] = strides.GetValueOrDefault(mesh.Stride) + 1;
            }
        }

        Assert.Equal(new Dictionary<int, int> { [32] = 175, [38] = 138 }, strides);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "S1244",
        Justification = "Counts exact zero normals separately from authored nonzero vectors.")]
    public void TheDecodedVerticesAreUnitNormalsUnitIntervalUvsAndPairedBoneWeights()
    {
        var resx = RequireResx();
        var vertices = 0;
        var zeroNormals = 0;
        var offUnit = 0;
        var offUv = 0;
        var smoothNormals = 0;
        var offUnitSmoothNormals = 0;

        foreach (var relative in Clumps)
        {
            foreach (var (_, mesh) in Meshes(resx, relative, out _))
            {
                foreach (var vertex in mesh.Vertices)
                {
                    vertices++;
                    var length = vertex.Normal.Length();
                    if (length == 0)
                    {
                        zeroNormals++;
                    }
                    else if (Math.Abs(length - 1f) > 1e-3f)
                    {
                        offUnit++;
                    }

                    if (vertex.TexCoord.X is < 0f or > 1f || vertex.TexCoord.Y is < 0f or > 1f)
                    {
                        offUv++;
                    }

                    if (!mesh.HasSmoothNormal)
                    {
                        continue;
                    }

                    smoothNormals++;
                    if (Math.Abs(vertex.SmoothNormal.Length() - 1f) > 1e-3f)
                    {
                        offUnitSmoothNormals++;
                    }
                }
            }
        }

        // The parser already refuses a vertex whose weights do not sum to 1 or whose lanes are not
        // palette slots × 4, so reaching 143,439 vertices at all is that half of the claim.
        Assert.Equal(143439, vertices);
        Assert.Equal(6, zeroNormals);
        Assert.Equal(0, offUnit);
        Assert.Equal(0, offUv);
        Assert.Equal(59848, smoothNormals);
        Assert.Equal(98, offUnitSmoothNormals);
    }

    [Fact]
    public void TheHaloLayoutsExtraVectorIsParallelToTheNormalSoItCannotBeATangent()
    {
        // ⛔ THE REFUTATION of the name this field carried until 2026-09-08. A tangent is
        // perpendicular to its normal by construction, so a population sitting at a median dot of
        // 0.996 cannot be one. This test could have failed in the obvious way: had the vectors
        // really been tangents the perpendicular bucket would hold ~59,000 and the parallel bucket
        // ~0, which is the exact opposite of what the disc stores.
        var resx = RequireResx();
        var dots = new List<double>();
        foreach (var relative in Clumps)
        {
            foreach (var (_, mesh) in Meshes(resx, relative, out _))
            {
                if (!mesh.HasSmoothNormal)
                {
                    continue;
                }

                foreach (var vertex in mesh.Vertices)
                {
                    var n = vertex.Normal.Length();
                    var e = vertex.SmoothNormal.Length();
                    if (n > 1e-6f && e > 1e-6f)
                    {
                        dots.Add(Vector3.Dot(vertex.Normal, vertex.SmoothNormal) / (n * (double)e));
                    }
                }
            }
        }

        dots.Sort();
        Assert.Equal(59750, dots.Count);
        Assert.Equal(30407, dots.Count(d => d > 0.99));
        Assert.Equal(198, dots.Count(d => Math.Abs(d) < 0.05));
        Assert.True(dots[dots.Count / 2] > 0.99, $"median dot was {dots[dots.Count / 2]}");
    }

    [Fact]
    public void TheHaloLayoutsExtraVectorIsConstantAcrossVerticesThatShareAPositionButSplitTheirNormals()
    {
        // ⚑ THE POSITIVE reading: a per-POSITION smoothed normal. At a smoothing split the hard
        // normals differ by definition, and a tangent — split for the same reason the UVs are —
        // would differ with them. The extra vector does not: it is identical across 11,399 of the
        // 11,408 split groups. Not trivial, either: only 1 of the 138 halo meshes uses a single
        // extra value throughout, which the distinct-value count below pins.
        var resx = RequireResx();
        var splitGroups = 0;
        var extraConstantInSplitGroup = 0;
        var meshesWithOneExtraValue = 0;
        var haloMeshes = 0;

        foreach (var relative in Clumps)
        {
            foreach (var (_, mesh) in Meshes(resx, relative, out _))
            {
                if (!mesh.HasSmoothNormal)
                {
                    continue;
                }

                haloMeshes++;
                var byPosition = new Dictionary<Vector3, List<BosXboxMeshVertex>>();
                var extras = new HashSet<Vector3>();
                foreach (var vertex in mesh.Vertices)
                {
                    if (!byPosition.TryGetValue(vertex.Position, out var bucket))
                    {
                        bucket = [];
                        byPosition[vertex.Position] = bucket;
                    }

                    bucket.Add(vertex);
                    extras.Add(vertex.SmoothNormal);
                }

                if (extras.Count == 1)
                {
                    meshesWithOneExtraValue++;
                }

                foreach (var bucket in byPosition.Values)
                {
                    if (bucket.Count < 2 || bucket.Select(v => v.Normal).Distinct().Count() < 2)
                    {
                        continue;
                    }

                    splitGroups++;
                    if (bucket.Select(v => v.SmoothNormal).Distinct().Count() == 1)
                    {
                        extraConstantInSplitGroup++;
                    }
                }
            }
        }

        Assert.Equal(138, haloMeshes);
        Assert.Equal(1, meshesWithOneExtraValue);
        Assert.Equal(11408, splitGroups);
        Assert.Equal(11399, extraConstantInSplitGroup);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "S1244",
        Justification = "The retail census distinguishes an exactly zero ground plane from other authored bounds.")]
    public void MeshesSitOnTheGroundPlaneInZAndOnlyInZ()
    {
        // ⚑ HALF THE Z-UP EVIDENCE, and the half a comment overstated until 2026-09-08: the claim
        // is a minimum of >= 0 (nothing below the floor), NOT a minimum of exactly 0 — that
        // stricter reading holds on only 2 of 92 and 38 of 153, which this test pins so the two
        // can never be confused again. X and Y are the discriminating control drawn from the same
        // meshes: both score 0, so the axis is not simply "whichever we looked at".
        var resx = RequireResx();
        var counts = new Dictionary<string, int>();

        void Bump(string key)
        {
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        foreach (var relative in new[] { "armor.clp", "global.clp" })
        {
            var clump = Path.GetFileNameWithoutExtension(relative);
            foreach (var (_, mesh) in Meshes(resx, relative, out _))
            {
                Bump($"{clump}.meshes");
                var min = new[]
                {
                    mesh.Vertices.Min(v => v.Position.X),
                    mesh.Vertices.Min(v => v.Position.Y),
                    mesh.Vertices.Min(v => v.Position.Z)
                };

                for (var axis = 0; axis < 3; axis++)
                {
                    var name = "XYZ"[axis];
                    if (min[axis] >= 0)
                    {
                        Bump($"{clump}.{name}>=0");
                    }

                    if (min[axis] == 0)
                    {
                        Bump($"{clump}.{name}==0");
                    }
                }
            }
        }

        Assert.Equal(92, counts["armor.meshes"]);
        Assert.Equal(153, counts["global.meshes"]);
        Assert.Equal(75, counts["armor.Z>=0"]);
        Assert.Equal(72, counts["global.Z>=0"]);
        Assert.Equal(2, counts["armor.Z==0"]);
        Assert.Equal(38, counts["global.Z==0"]);
        Assert.Equal(0, counts.GetValueOrDefault("armor.X>=0"));
        Assert.Equal(0, counts.GetValueOrDefault("armor.Y>=0"));
        Assert.Equal(0, counts.GetValueOrDefault("global.X>=0"));
        Assert.Equal(0, counts.GetValueOrDefault("global.Y>=0"));
    }

    [Fact]
    public void TheBonePaletteIsExactlyAsLongAsTheHighestBoneSlotTheVerticesUse()
    {
        // ⚑ THE PROBE that settles +0x24 as a bone palette: max slot + 1 == the palette length, on
        // every mesh. Reading the FLOAT4 as four weights instead makes this impossible — those
        // "weights" sum to 1, 5, 9, 13 … because two of the lanes are indices times four.
        var resx = RequireResx();
        var meshes = 0;
        var matching = 0;
        foreach (var relative in Clumps)
        {
            foreach (var (_, mesh) in Meshes(resx, relative, out _))
            {
                meshes++;
                var highest = 0;
                foreach (var vertex in mesh.Vertices)
                {
                    highest = Math.Max(highest, Math.Max(vertex.BoneA, vertex.BoneB));
                }

                if (highest + 1 == mesh.BonePalette.Count)
                {
                    matching++;
                }
            }
        }

        Assert.Equal(313, meshes);
        Assert.Equal(313, matching);
    }

    [Fact]
    public void TheRenderFlagsStrideBitAgreesWithTheStrideTheBufferLengthsForce()
    {
        // ⚑⚑ A SECOND, INDEPENDENT reading of the stride, from a field the buffer arithmetic never
        // touches: FUN_00077EA0 hands SetStreamSource `0x20 + ((flags & 0x20) ? 6 : 0)`. The derived
        // stride comes from vbLength / vertexCount; the flag comes from a byte at +0x10. They agree
        // on every shipped mesh. ⚑ THE DISCRIMINATION is the cross-tab: an unrelated byte would
        // populate all four cells, and a byte that were merely *usually* right would populate three.
        // Two cells are populated and two are empty.
        var resx = RequireResx();
        var flagSetStride38 = 0;
        var flagClearStride32 = 0;
        var flagSetStride32 = 0;
        var flagClearStride38 = 0;
        var meshes = 0;
        foreach (var relative in Clumps)
        {
            foreach (var (_, mesh) in Meshes(resx, relative, out _))
            {
                meshes++;
                switch (mesh.DeclaresHaloStride, mesh.Stride)
                {
                    case (true, 38):
                        flagSetStride38++;
                        break;
                    case (false, 32):
                        flagClearStride32++;
                        break;
                    case (true, 32):
                        flagSetStride32++;
                        break;
                    case (false, 38):
                        flagClearStride38++;
                        break;
                }
            }
        }

        Assert.Equal(313, meshes);
        Assert.Equal(138, flagSetStride38);
        Assert.Equal(175, flagClearStride32);
        Assert.Equal(0, flagSetStride32);
        Assert.Equal(0, flagClearStride38);
    }

    [Fact]
    public void ThereAreTwoSixtyFourEntryBoneTablesAndTheSecondOneIsPopulatedOnFiveMeshes()
    {
        // ⛔ THE CORRECTION of 2026-09-08. The old reading was "a 128-byte palette terminated by
        // 0xFF", which merges two tables the draw path walks separately (0x40 iterations from +0x24,
        // then 0x40 from +0x64, rebinding only where the two differ).
        // ⚑ THE CONTROL that +0x24 is the table the vertices index: maxSlot + 1 matches its run on
        // 313/313 and the second table's on 0/313.
        // ⚑ THE CONTROL that 0xFF is a per-entry SKIP and not a terminator: on all five meshes whose
        // second table is populated, live entries resume AFTER an unused one. A terminator reading
        // would drop them. (The first table never exercises this — 0 of 313 — which is why retail
        // behaviour does not change.)
        var resx = RequireResx();
        var meshes = 0;
        var matchesFirstTable = 0;
        var matchesSecondTable = 0;
        var firstTableHasAHole = 0;
        var secondTablePopulated = 0;
        var secondTableHasAHole = 0;
        var populatedTags = new List<string>();
        foreach (var relative in Clumps)
        {
            foreach (var (tag, mesh) in Meshes(resx, relative, out _))
            {
                meshes++;
                Assert.Equal(64, mesh.BonePaletteEntries.Count);
                Assert.Equal(64, mesh.SecondBonePaletteEntries.Count);

                var highest = 0;
                foreach (var vertex in mesh.Vertices)
                {
                    highest = Math.Max(highest, Math.Max(vertex.BoneA, vertex.BoneB));
                }

                if (highest + 1 == Run(mesh.BonePaletteEntries))
                {
                    matchesFirstTable++;
                }

                if (highest + 1 == Run(mesh.SecondBonePaletteEntries))
                {
                    matchesSecondTable++;
                }

                if (HasAHole(mesh.BonePaletteEntries))
                {
                    firstTableHasAHole++;
                }

                if (mesh.SecondBonePaletteEntries.Any(entry => entry != BosXboxMesh.BonePaletteUnused))
                {
                    secondTablePopulated++;
                    populatedTags.Add($"{Path.GetFileName(relative)}#{tag:X8}");
                    if (HasAHole(mesh.SecondBonePaletteEntries))
                    {
                        secondTableHasAHole++;
                    }
                }
            }
        }

        Assert.Equal(313, meshes);
        Assert.Equal(313, matchesFirstTable);
        Assert.Equal(0, matchesSecondTable);
        Assert.Equal(0, firstTableHasAHole);
        Assert.Equal(5, secondTablePopulated);
        Assert.Equal(5, secondTableHasAHole);
        Assert.Equal(
            new[]
            {
                "BAR.clp#158280B5", "BAR.clp#15C54C4E", "BAR.clp#98545F16", "BAR.clp#FE6439F1", "cain.clp#8CAB3206"
            },
            populatedTags.Order(StringComparer.Ordinal).ToArray());

        static int Run(IReadOnlyList<byte> table)
        {
            var length = 0;
            while (length < table.Count && table[length] != BosXboxMesh.BonePaletteUnused)
            {
                length++;
            }

            return length;
        }

        static bool HasAHole(IReadOnlyList<byte> table)
        {
            for (var i = Run(table); i < table.Count; i++)
            {
                if (table[i] != BosXboxMesh.BonePaletteUnused)
                {
                    return true;
                }
            }

            return false;
        }
    }

    [Fact]
    public void TheIndicesAreStripsNotListsAndTheStripsExpandToRealTriangles()
    {
        // A triangle list needs the count divisible by three: 217 of the 313 counts are not, which
        // is what rules the reading out. ⛔ An earlier note claiming "count ≡ 1 or 2 mod 3" is
        // refuted by the 96 that are ≡ 0.
        var resx = RequireResx();
        var byResidue = new int[3];
        var stripPositions = 0;
        var triangles = 0;
        foreach (var relative in Clumps)
        {
            foreach (var (_, mesh) in Meshes(resx, relative, out _))
            {
                byResidue[mesh.StripIndices.Count % 3]++;
                stripPositions += mesh.StripIndices.Count - 2;
                triangles += mesh.TriangleCount;
            }
        }

        Assert.Equal(new[] { 96, 137, 80 }, byResidue);
        Assert.Equal(267943, stripPositions);
        Assert.Equal(152310, triangles);
    }

    [Fact]
    public void TheXboxClumpsThatHoldNoGeometryYieldNoMeshesEither()
    {
        // ⛔ THE SAME-PLATFORM CONTROL, drawn from the affected population: the Xbox disc's own
        // texture, HUD, inventory, streamed-audio and sound clumps. If the walk were loose it would
        // claim some of these 453 sections; it claims none. The three player-character clumps are
        // the positive half of the same control — they DO hold geometry and yield 4 meshes.
        var resx = RequireResx();
        var negative = new[]
        {
            "global_x.clp", "hud.clp", "inventry.clp", "inv_swap.clp", "cains.clp",
            Path.Combine("c1", "BAR", "BAR_T.clp"), Path.Combine("c1", "BAR", "BAR_S.clp")
        };

        var sections = 0;
        var claimed = 0;
        foreach (var relative in negative)
        {
            claimed += Meshes(resx, relative, out var count).Count;
            sections += count;
        }

        Assert.Equal(453, sections);
        Assert.Equal(0, claimed);

        var characters = 0;
        var characterSections = 0;
        foreach (var relative in new[] { "cyrus.clp", "nadia.clp", "patty.clp" })
        {
            characters += Meshes(resx, relative, out var count).Count;
            characterSections += count;
        }

        Assert.Equal(8, characterSections);
        Assert.Equal(4, characters);
    }

    [Fact]
    public void ThePlayStation2TwinsOfTheSameClumpsYieldNoMeshesAtAll()
    {
        // ⛔ THE CONTROL. The PS2 release stores VIF packets in the same container, so if the walk
        // were loose enough to accept arbitrary bytes it would claim some of these 3,888 sections.
        BucketBTestGuard.SkipUnlessEnabled();
        var iso = RealAssetPaths.Consoles.BrotherhoodOfSteelIso();
        Assert.SkipWhen(iso is null, RealAssetPaths.SkipMessage("Fallout: Brotherhood of Steel disc image"));

        using var disc = ArchiveReader.Open(iso);
        var wanted = new[] { "ARMOR.CLP", "GLOBAL.CLP", "SFX.CLP", "BAR.CLP" };
        var sections = 0;
        var meshes = 0;
        var files = 0;

        foreach (var entry in disc.ListFiles())
        {
            if (!wanted.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var bytes = disc.ReadFile(entry.FullPath);
            if (bytes is null || !BosClumpFile.TryParse(bytes, entry.Name, out var clump, out _))
            {
                continue;
            }

            files++;
            foreach (var section in clump.Sections)
            {
                sections++;
                if (BosXboxMesh.IsMesh(BosClumpFile.Read(bytes, section)))
                {
                    meshes++;
                }
            }
        }

        Assert.Equal(4, files);
        Assert.Equal(3888, sections);
        Assert.Equal(0, meshes);
    }

    [Fact]
    public void TheSixLargestArmourMeshesExportAsGlb()
    {
        var resx = RequireResx();
        var directory = Environment.GetEnvironmentVariable("BOS_XBOX_GLB_DIR");
        if (string.IsNullOrEmpty(directory))
        {
            directory = Path.Combine(Path.GetTempPath(), "bos-xbox-mesh-glb");
        }

        Directory.CreateDirectory(directory);

        var largest = Meshes(resx, "armor.clp", out _)
            .OrderByDescending(m => m.Mesh.Vertices.Count)
            .Take(6)
            .ToList();
        Assert.Equal(6, largest.Count);

        foreach (var (tag, mesh) in largest)
        {
            var path = Path.Combine(directory, $"armor_{tag:X8}.glb");
            BosXboxMeshGlbExporter.Write(mesh, path);

            // SharpGLTF validates on write, so a mesh that decoded to nonsense throws before here.
            var glb = File.ReadAllBytes(path);
            Assert.Equal("glTF"u8.ToArray(), glb.Take(4).ToArray());
            Assert.True(glb.Length > 4096, $"{path} is only {glb.Length} bytes");
            Assert.True(mesh.TriangleCount > 100, $"{path} has only {mesh.TriangleCount} triangles");
        }
    }
}