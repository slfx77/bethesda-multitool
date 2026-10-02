using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Redguard;
using BethesdaMultitool.Core.Modeling.Units;
using BethesdaMultitool.Core.Modeling.Xngine;
using BethesdaMultitool.Tests.Core.Modeling.Redguard;
using BethesdaMultitool.Tests.Core.Modeling.Xngine;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Export;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Bucket B for cut-1c slice 6 (plan section 8, row 6; section 9, hop A7): the Redguard <c>.3DC</c> reader against
///     the independent gate-1c oracle <c>tools/scripts/gate1c/rg3dc_probe.py</c> (through its driver
///     <c>rg3dc_probe_json.py</c>, ONE process for the whole directory) on every one of the 147 retail files, the A7
///     controls (a flipped narrow delta bit is detected; accumulating the deltas frame to frame drifts on every narrow
///     file of three or more frames), the probe's one recognition per file, the six cover rows through both writers'
///     plans, and the DOGA001 unit control.
/// </summary>
/// <remarks>
///     Every count pinned here was measured by the read-only receipts beside the slice-6 staging
///     (<c>TestOutput/cut1c-20260928/slice56/receipts/measure_slice6.json</c> and <c>measure_slice6_extra.json</c>), built
///     on the oracle alone: 147 files (37 wide, 110 narrow), 9,190 frames, 26 inside the 64 KiB probe prefix, 41 n-gons
///     whose pose-union corners differ from the keyframe's own (45 n-gons some pose keeps differently), 109 of the 109
///     narrow files of three or more frames drifting under accumulation, a static <c>.3D</c> walk of the same bytes
///     reading wrong points on 146 files (the trap; GOLMA001's header +48 happens to name its keyframe), and 7 points
///     no plane corner names in 4 files (LASRA001 1; SKELA001, SKELA002 and SKELA004 2 each; slice-6 review receipt
///     <c>measure_review6_fixes.json</c>).
/// </remarks>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class Cut1cRedguard3DcTests : IDisposable
{
    private const string CyrsaSha256 = "8b04fbdb95c0b0e1bb79d1d37f6745fd1e485de750affd1720cd539ace09862f";

    private readonly string _directory = Directory.CreateTempSubdirectory("bmt-cut1c-a7-").FullName;

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>
    ///     The six <c>.3DC</c> cover rows: name, frames, wide, whether the tiling leaves an unaccounted region, and whether
    ///     Shared's GLB writer refuses the document for its four MiB fidelity-carrier budget. Only BMANA001 is refused:
    ///     measured 2026-09-28, 2,622 of its 2,819 GLB fidelity rows are the absolute-to-relative and rounding rows the
    ///     writer emits per morph target per primitive (69 targets x 19 primitives), and the writer reserves at least 1 KiB
    ///     per row. Shared has no summarized carrier yet ("explicit summarized fidelity is required"); the ask is SA-K4 in
    ///     the mailbox. When it ships, this row must convert, and the pin below fails until the flag is cleared.
    /// </summary>
    public static TheoryData<string, int, bool, bool, bool> CoverRows()
    {
        return new TheoryData<string, int, bool, bool, bool>
        {
            { "BMANA001.3DC", 70, true, true, true },
            { "CVFTL001.3DC", 13, false, true, false },
            { "DOGA001.3DC", 38, false, true, false },
            { "BLOBA001.3DC", 2, false, true, false },
            { "BEAMA001.3DC", 4, false, false, false },
            { "CV_SKUL3.3DC", 45, true, true, false }
        };
    }

    /// <summary>
    ///     Hop A7 on all 147 retail files: the payload digest, the frame table (fourth dwords included), the preamble, the
    ///     unaccounted region, every frame's normal and plane-data digests, the plane list, the stored-UV stream, the
    ///     pose-union triangles per plane, the n-gons whose union differs from the keyframe, and every pose rebuilt as
    ///     integers through <c>PointIndices</c> (keyframe positions, plus narrow deltas or wide absolute positions)
    ///     against the oracle's per-frame digests; the points no plane corner names, whose stored values in every frame
    ///     the <c>bmt.redguard.3dc.unreferenced-points</c> row must carry (against the oracle's keyframe and rebuilt
    ///     poses; slice-6 review finding 2) and the point blocks' coverage must name; plus the census size, the clip's
    ///     end, and the probe (exactly one recognition, Confirmed on the 26 files inside the prefix). Control: a narrow
    ///     file read with its deltas accumulated frame to frame disagrees with the oracle on 109 of 109 narrow files of
    ///     three or more frames.
    /// </summary>
    [Fact]
    public void A7_EveryRetailFrameStackAgreesWithTheIndependentProbe()
    {
        var art = RequireRedguard3dart();
        var oracle = RunA7(art);
        var names = Directory.GetFiles(art, "*.3DC").Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.Equal(147, names.Count);
        Assert.Equal(147, oracle.Count);

        var registry = BethesdaModelRegistration.CreateReaders();
        var mismatches = new List<string>();
        int wide = 0, frames = 0, unionDiffers = 0, poseDependent = 0, confirmed = 0, drifting = 0, narrowThree = 0,
            trapped = 0, unreferencedFiles = 0, unreferencedPoints = 0;
        foreach (var name in names)
        {
            var bytes = File.ReadAllBytes(Path.Combine(art, name!));
            var expected = oracle[name!]!.AsObject();
            Assert.Null(expected["error"]);
            var frameCount = expected["frame_count"]!.GetValue<int>();
            var isWide = expected["width"]!.GetValue<string>() == "wide";
            wide += isWide ? 1 : 0;
            frames += frameCount;
            poseDependent += expected["pose_dependent_ngons"]!.AsArray().Count;
            trapped += expected["walk_as_3d_trap"]!["wrong_points"]!.GetValue<int>() > 0 ? 1 : 0;

            var selection = registry.Probe(Redguard3DcModelTestSupport.Candidate(bytes));
            var match = Assert.Single(selection.Matches, m => m.Result.Kind != ModelProbeKind.NotAModel);
            Check(mismatches, match.FormatId == "bmt.redguard.3dc", name, "recognized by " + match.FormatId);
            confirmed += match.Result.Confidence == ModelProbeConfidence.Confirmed ? 1 : 0;

            var result = Redguard3DcModelTestSupport.Read(bytes, detail: ModelNativeDetail.Full, path: "3dart/" + name);
            var document = result.Document;
            Compare(mismatches, name!, expected, document, result.Coverage, frameCount, isWide);
            unionDiffers += expected["union_differs_from_keyframe"]!.AsArray().Count;
            var unreferencedRows = XnGineModelTestSupport.Rows(document, Redguard3DcModelNativeState.UnreferencedPointsKind);
            unreferencedFiles += unreferencedRows.Count;
            unreferencedPoints += unreferencedRows.Sum(row => JsonNode.Parse(row.PayloadJson)!["count"]!.GetValue<int>());

            if (!isWide && frameCount >= 3)
            {
                narrowThree++;
                var accumulated = Poses(document, frameCount, isWide, accumulate: true);
                var digests = expected["pose_sha256"]!.AsArray().Select(d => d!.GetValue<string>()).ToArray();
                drifting += accumulated.Select(PoseDigest).Where((digest, f) => digest != digests[f]).Any() ? 1 : 0;
            }
        }

        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches.Take(40)));
        Assert.Equal(37, wide);
        Assert.Equal(9_190, frames);
        Assert.Equal(41, unionDiffers);
        Assert.Equal(45, poseDependent);
        Assert.Equal(26, confirmed);
        Assert.Equal(146, trapped);
        Assert.Equal(109, narrowThree);
        Assert.Equal(109, drifting);
        Assert.Equal(4, unreferencedFiles);
        Assert.Equal(7, unreferencedPoints);
    }

    /// <summary>
    ///     The A7 control: one bit flipped in DOGA001's frame 5 deltas (the low bit of the first referenced point's dx) is
    ///     detected by exactly that frame's digest; every other frame still agrees.
    /// </summary>
    [Fact]
    public void A7Control_AFlippedNarrowDeltaBitIsDetected()
    {
        var art = RequireRedguard3dart();
        var path = Path.Combine(art, "DOGA001.3DC");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("Redguard DOGA001.3DC"));
        var expected = RunA7(path)["DOGA001.3DC"]!.AsObject();
        Assert.Equal("narrow", expected["width"]!.GetValue<string>());
        var frameCount = expected["frame_count"]!.GetValue<int>();
        var digests = expected["pose_sha256"]!.AsArray().Select(d => d!.GetValue<string>()).ToArray();
        var point = expected["referenced_points"]!.AsArray()[0]!.GetValue<int>();
        var offset = expected["table"]!.AsArray()[5]!.AsArray()[0]!.GetValue<int>() + 6 * point;

        var intact = Redguard3DcModelTestSupport.Read(File.ReadAllBytes(path)).Document;
        Assert.Equal(digests, Poses(intact, frameCount, false, false).Select(PoseDigest).ToArray());

        var flipped = File.ReadAllBytes(path);
        flipped[offset] ^= 1;
        var document = Redguard3DcModelTestSupport.Read(flipped).Document;
        var actual = Poses(document, frameCount, false, false).Select(PoseDigest).ToArray();
        Assert.Equal(new[] { 5 }, Enumerable.Range(0, frameCount).Where(f => actual[f] != digests[f]).ToArray());
    }

    /// <summary>
    ///     The six cover rows: the payload SHA-256 pinned by the manifest, the frame count and width, the unaccounted
    ///     element exactly where the tiling leaves a region (BEAMA001 has none), a GLB that converts (BMANA001: the pinned
    ///     Shared carrier-budget refusal instead), Blender carrying the
    ///     clip natively, the clip ending at N/15 s, and the <c>.3D</c> reader refusing the same bytes (the trap control
    ///     <c>3dc-offered-to-3d-reader</c>: a <c>.3DC</c> walked as a static mesh would read frame 1's points or none).
    /// </summary>
    [Theory]
    [MemberData(nameof(CoverRows))]
    public async Task CoverRows_ReadConvertAndCarryTheClip(string name, int frames, bool wide, bool unaccounted,
        bool glbCarrierBudgetRefused)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var file = Cut1cCoverManifest.Require(name);
        var bytes = Cut1cFixtureResolver.Require(file);

        var result = Redguard3DcModelTestSupport.Read(bytes, path: "3dart/" + file.Entry);
        var document = result.Document;

        Assert.Equal(file.Sha256, document.SourceProvenance!.Sha256);
        var header = XnGineModelTestSupport.Payload(document, Redguard3DcModelNativeState.HeaderKind);
        Assert.Equal(frames, header["frameCount"]!.GetValue<int>());
        Assert.Equal(wide ? "wide" : "narrow", header["width"]!.GetValue<string>());
        Assert.Equal(unaccounted, result.Coverage.Elements.Any(e => e.Identity == Redguard3DcModelCoverage.UnaccountedElement));
        Assert.All(document.Meshes[0].Primitives, p => Assert.Equal(frames - 1, p.MorphTargets.Count));

        var track = Assert.Single(Assert.Single(document.Animations).MorphTracks);
        Assert.Equal(SceneInterpolation.Step, track.Interpolation);
        Assert.Equal(frames / 15f, track.Times[^1]);

        var (_, written, refusal) = await Redguard3DcModelTestSupport.WriteGlbAsync(document, _directory,
            Path.GetFileNameWithoutExtension(name));
        if (glbCarrierBudgetRefused)
        {
            // A Shared writer limit, not a reader loss: the Blender package below still carries the clip.
            Assert.NotNull(refusal);
            Assert.Contains("four MiB carrier budget", refusal, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(refusal);
            Assert.Equal(ModelItemOutcome.Converted, written!.Outcome);
        }

        var clip = Assert.Single(XnGineModelTestSupport.BlenderRows(document),
            r => r.Target.Kind == SceneElementKind.Animation && r.FeatureId == "clip");
        Assert.Equal("animation-native-curves", clip.ReasonCode);

        Assert.Throws<NotSupportedException>(() => XnGineModelTestSupport.Read(bytes));
    }

    /// <summary>
    ///     The registry on every manifest row, from the bytes the manifest pins (plan section 6.1, "exactly one reader
    ///     recognizes each file"): a <c>.3DC</c> row by <c>bmt.redguard.3dc</c> alone, a static row by <c>bmt.xngine.3d</c>
    ///     alone, the 3dfx row by <c>bmt.xngine.3d</c> alone (Unsupported), the MZ stray and the empty segment by no reader.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cut1cCoverManifest.Rows), MemberType = typeof(Cut1cCoverManifest))]
    public void Registry_RecognizesEveryManifestRowAtMostOnce(string name, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var file = Cut1cCoverManifest.Require(name);
        Assert.Equal(sha256, file.Sha256);
        var bytes = Cut1cFixtureResolver.Require(file);

        var selection = BethesdaModelRegistration.CreateReaders().Probe(Redguard3DcModelTestSupport.Candidate(bytes));
        var matches = selection.Matches.Where(m => m.Result.Kind != ModelProbeKind.NotAModel).ToList();

        if (file.Entry.EndsWith(".3DC", StringComparison.OrdinalIgnoreCase) && file.Tag is "v2.6" or "v2.7")
        {
            Assert.Equal("bmt.redguard.3dc", Assert.Single(matches).FormatId);
        }
        else if (Cut1cXnGineCover.IsStaticMesh(file) || file.Tag is "v4.0" or "v5.0")
        {
            Assert.Equal("bmt.xngine.3d", Assert.Single(matches).FormatId);
        }
        else
        {
            Assert.Empty(matches);
        }
    }

    /// <summary>
    ///     The section 6.3 unit control: DOGA001's keyframe spans 30,060 native units in Y and 40,706 in Z, 1.4677734375 m
    ///     and 1.98759765625 m at the actor row's 1/20480, and the human actor CYRSA001 spans 32,766 in Y, 1.5999 m. Both
    ///     documents take the actor row and carry the actor-scale diagnostic. Control: the Daggerfall factor would make
    ///     the dog 2.94 m tall.
    /// </summary>
    [Fact]
    public void Doga001_UnitControl_PinsTheActorRow()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var dog = Redguard3DcModelTestSupport.Read(Cut1cFixtureResolver.Require(Cut1cCoverManifest.Require("DOGA001.3DC")),
            path: "3dart/DOGA001.3DC").Document;
        var art = RequireRedguard3dart();
        var cyrsaPath = Path.Combine(art, "CYRSA001.3DC");
        Assert.SkipWhen(!File.Exists(cyrsaPath), RealAssetPaths.SkipMessage("Redguard CYRSA001.3DC"));
        var cyrsaBytes = File.ReadAllBytes(cyrsaPath);
        Assert.Equal(CyrsaSha256, Convert.ToHexStringLower(SHA256.HashData(cyrsaBytes)));
        var human = Redguard3DcModelTestSupport.Read(cyrsaBytes, path: "3dart/CYRSA001.3DC").Document;

        foreach (var document in new[] { dog, human })
        {
            Assert.Equal(ClassicModelUnits.RedguardMetersPerUnit, document.Units!.MetersPerUnit);
            Assert.Equal(SceneValueProvenance.Assumed, document.Units.Provenance);
            Assert.Contains(document.Diagnostics, d => d.Code == ClassicModelUnits.ActorScaleDiagnostic);
        }

        var dogExtent = Extent(dog);
        Assert.Equal((10_785, 30_060, 40_706), dogExtent);
        Assert.Equal(1.4677734375, dogExtent.Y * dog.Units!.MetersPerUnit, 1e-12);
        Assert.Equal(1.98759765625, dogExtent.Z * dog.Units.MetersPerUnit, 1e-12);
        Assert.Equal(32_766, Extent(human).Y);
        Assert.Equal(1.59990234375, Extent(human).Y * human.Units!.MetersPerUnit, 1e-12);
        Assert.True(Math.Abs(dogExtent.Y * ClassicModelUnits.DaggerfallMetersPerUnit - 1.4677734375) > 1);
    }

    // ---- Helpers.

    private static string RequireRedguard3dart()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.Redguard();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Redguard"));
        var art = Path.Combine(root, "3dart");
        Assert.SkipWhen(!Directory.Exists(art), RealAssetPaths.SkipMessage("Redguard 3dart"));
        return art;
    }

    /// <summary>Runs the A7 driver over a file or directory (one process) and returns its per-file extractions.</summary>
    private JsonObject RunA7(string input)
    {
        Assert.SkipWhen(Cut1cOracleProcess.Interpreter is null,
            "hop A7 needs a Python interpreter with numpy on the PATH (python, py -3 or python3).");
        var output = Path.Combine(_directory, "a7-" + Guid.NewGuid().ToString("N") + ".json");
        var (exitCode, error) = Cut1cOracleProcess.Run("rg3dc_probe_json.py", [input, output],
            TestContext.Current.CancellationToken);
        Assert.True(exitCode == 0, $"rg3dc_probe_json.py exited {exitCode}: {error}");
        return JsonNode.Parse(File.ReadAllText(output))!["files"]!.AsObject();
    }

    /// <summary>Records a mismatch instead of stopping at the first file.</summary>
    private static void Check(List<string> mismatches, bool condition, string? name, string what)
    {
        if (!condition)
        {
            mismatches.Add($"{name}: {what}");
        }
    }

    /// <summary>One file's field-by-field comparison with its oracle extraction (see the A7 test's summary).</summary>
    private static void Compare(List<string> mismatches, string name, JsonObject expected, ModelDocument document,
        ModelSourceCoverage coverage, int frameCount, bool wide)
    {
        Check(mismatches, document.SourceProvenance!.Sha256 == expected["payload_sha256"]!.GetValue<string>(), name,
            "payload SHA-256");

        var header = XnGineModelTestSupport.Payload(document, Redguard3DcModelNativeState.HeaderKind);
        Check(mismatches, header["frameCount"]!.GetValue<int>() == frameCount, name, "frame count");
        Check(mismatches, header["frameRecordDwords"]!.GetValue<int>() == expected["record_dwords"]!.GetValue<int>(),
            name, "record width");
        Check(mismatches, header["planeListEnd"]!.GetValue<int>() == expected["plane_list_end"]!.GetValue<int>(), name,
            "plane-list end");
        Check(mismatches, header["plus44"]!.GetValue<int>() == expected["header"]!["h44"]!.GetValue<int>(), name, "+44");

        var preamble = XnGineModelTestSupport.Payload(document, Redguard3DcModelNativeState.PreambleKind)["dwords"]!
            .AsArray().Select(d => d!.GetValue<int>());
        Check(mismatches, preamble.SequenceEqual(Ints(expected["preamble"])), name, "preamble");

        var table = expected["table"]!.AsArray();
        var rows = XnGineModelTestSupport.Payload(document, Redguard3DcModelNativeState.FramesKind)["frames"]!.AsArray();
        var normals = Strings(expected["normals_sha256"]);
        var planeData = Strings(expected["plane_data_sha256"]);
        Check(mismatches, rows.Count == frameCount, name, "frame rows");
        for (var f = 0; f < Math.Min(rows.Count, frameCount); f++)
        {
            var record = Ints(table[f]).ToArray();
            var row = rows[f]!;
            var fourth = row["fourthDword"]?.GetValue<int>();
            Check(mismatches, row["pointOffset"]!.GetValue<int>() == record[0] &&
                              row["normalOffset"]!.GetValue<int>() == record[1] &&
                              row["planeDataOffset"]!.GetValue<int>() == record[2] &&
                              fourth == (record.Length == 4 ? record[3] : (int?)null), name, $"frame {f} record");
            Check(mismatches, row["normalsSha256"]!.GetValue<string>() == normals[f] &&
                              row["planeDataSha256"]!.GetValue<string>() == planeData[f], name, $"frame {f} blocks");
        }

        var gap = expected["gap"];
        var unaccounted = XnGineModelTestSupport.Rows(document, Redguard3DcModelNativeState.UnaccountedKind);
        if (gap is null)
        {
            Check(mismatches, unaccounted.Count == 0, name, "no unaccounted region");
        }
        else
        {
            var payload = JsonNode.Parse(Assert.Single(unaccounted).PayloadJson)!;
            Check(mismatches, payload["start"]!.GetValue<int>() == gap[0]!.GetValue<int>() &&
                              payload["length"]!.GetValue<int>() == gap[1]!.GetValue<int>(), name, "unaccounted region");
        }

        var planes = XnGineModelTestSupport.Payload(document, XnGineModelNativeState.PlanesKind)["planes"]!.AsArray();
        var oraclePlanes = expected["planes"]!.AsArray();
        Check(mismatches, planes.Count == oraclePlanes.Count, name, "plane count");
        var readerPlanes = XnGineLegacyComparison.ReaderPlanes(document);
        for (var k = 0; k < Math.Min(planes.Count, oraclePlanes.Count); k++)
        {
            var plane = planes[k]!;
            var oraclePlane = oraclePlanes[k]!;
            Check(mismatches, plane["pointCount"]!.GetValue<int>() == oraclePlane["count"]!.GetValue<int>() &&
                              plane["unknown1"]!.GetValue<int>() == oraclePlane["unknown1"]!.GetValue<int>() &&
                              plane["textureKey"]!.GetValue<uint>() == oraclePlane["key"]!.GetValue<uint>() &&
                              plane["headerTail"]!.GetValue<string>() == oraclePlane["tail"]!.GetValue<string>(),
                name, $"plane {k} header");
            var triangles = readerPlanes.TryGetValue(k, out var drawn)
                ? ReferenceTriangles(drawn).ToArray()
                : Array.Empty<(int, int, int)>();
            var oracleTriangles = oraclePlane["triangles"]!.AsArray()
                .Select(t => Ints(t).ToArray()).Select(t => (t[0], t[1], t[2])).ToArray();
            Check(mismatches, triangles.SequenceEqual(oracleTriangles), name, $"plane {k} triangles");
        }

        var uv = UvStream(document);
        Check(mismatches, uv.Rows == expected["uv_stream"]!["rows"]!.GetValue<int>() &&
                          uv.Sha256 == expected["uv_stream"]!["sha256"]!.GetValue<string>(), name, "stored-UV stream");

        var rule = XnGineModelTestSupport.Payload(document, XnGineModelNativeState.UvRuleKind);
        Check(mismatches, Ints(rule["poseDependentNgonOrdinals"]).SequenceEqual(Ints(expected["union_differs_from_keyframe"])),
            name, "pose-dependent n-gons");

        var poses = Poses(document, frameCount, wide, accumulate: false);
        var referenced = Ints(expected["referenced_points"]).ToArray();
        Check(mismatches, poses[0].Keys.SequenceEqual(referenced), name, "referenced points");
        Check(mismatches, expected["point_count"]!.GetValue<int>() - referenced.Length ==
                          expected["unreferenced_points"]!.GetValue<int>(), name, "unreferenced points");
        var digests = Strings(expected["pose_sha256"]);
        for (var f = 0; f < frameCount; f++)
        {
            Check(mismatches, PoseDigest(poses[f]) == digests[f], name, $"pose {f}");
        }

        CompareUnreferenced(mismatches, name, expected, document, coverage, frameCount, wide);

        var census = 3 + (expected["record_dwords"]!.GetValue<int>() == 4 ? 1 : 0) + oraclePlanes.Count +
                     3 * frameCount + (gap is null ? 0 : 1);
        Check(mismatches, coverage.TotalCount == census, name, $"census {coverage.TotalCount} against {census}");
        Check(mismatches, coverage.Classifications.Where(c => c.ElementIdentity.StartsWith("plane:", StringComparison.Ordinal))
            .All(c => c.Kind == ModelSourceCoverageKind.Typed), name, "every plane typed");

        var track = document.Animations.SingleOrDefault()?.MorphTracks.SingleOrDefault();
        Check(mismatches, frameCount < 2 ? track is null : track is { Interpolation: SceneInterpolation.Step } &&
                                                          track.Times[^1] == frameCount / 15f &&
                                                          track.TargetCount == frameCount - 1, name, "clip");
    }

    /// <summary>
    ///     Slice-6 review finding 2: the points no plane corner names against the <c>bmt.redguard.3dc.unreferenced-points</c>
    ///     row (absent exactly when the oracle lists none): each point's keyframe triple against the oracle's keyframe,
    ///     each later value against the oracle's rebuilt pose (a wide value is the pose, a narrow one the delta from the
    ///     keyframe), the raw content against the row's own digest; and every point block's Typed reason naming the row
    ///     exactly when there are such points.
    /// </summary>
    private static void CompareUnreferenced(List<string> mismatches, string name, JsonObject expected,
        ModelDocument document, ModelSourceCoverage coverage, int frameCount, bool wide)
    {
        var oracle = expected["unreferenced_poses"]!.AsArray();
        var rows = XnGineModelTestSupport.Rows(document, Redguard3DcModelNativeState.UnreferencedPointsKind);
        if (oracle.Count == 0)
        {
            Check(mismatches, rows.Count == 0, name, "unreferenced-points row present only in the reader");
        }
        else if (rows.Count != 1)
        {
            Check(mismatches, false, name, "unreferenced-points row missing");
        }
        else
        {
            var payload = JsonNode.Parse(rows[0].PayloadJson)!;
            var listed = payload["points"]!.AsArray();
            Check(mismatches, listed.Count == oracle.Count, name, "unreferenced point count");
            for (var i = 0; i < Math.Min(listed.Count, oracle.Count); i++)
            {
                var mine = listed[i]!;
                var point = oracle[i]!["point"]!.GetValue<int>();
                var poses = oracle[i]!["poses"]!.AsArray().Select(pose => Ints(pose).ToArray()).ToArray();
                var keyframe = Ints(mine["keyframe"]).ToArray();
                Check(mismatches, mine["point"]!.GetValue<int>() == point && keyframe.SequenceEqual(poses[0]), name,
                    $"unreferenced point {point} keyframe");
                var later = mine["later"]!.AsArray();
                Check(mismatches, later.Count == frameCount - 1, name, $"unreferenced point {point} frame count");
                for (var f = 1; f < Math.Min(frameCount, later.Count + 1); f++)
                {
                    var stored = Ints(later[f - 1]).ToArray();
                    var pose = wide ? stored : stored.Zip(keyframe, static (delta, key) => delta + key).ToArray();
                    Check(mismatches, pose.SequenceEqual(poses[f]), name, $"unreferenced point {point} frame {f}");
                }
            }

            Check(mismatches, Convert.ToHexStringLower(SHA256.HashData(rows[0].CopyRawContent())) ==
                              payload["sha256"]!.GetValue<string>(), name, "unreferenced raw content");
        }

        var reason = oracle.Count == 0 ? null : Redguard3DcModelCoverage.UnreferencedPointsReason(oracle.Count);
        Check(mismatches, coverage.Classifications
            .Where(c => c.ElementIdentity.EndsWith(":points", StringComparison.Ordinal))
            .All(c => c.Kind == ModelSourceCoverageKind.Typed && c.Reason == reason), name, "point blocks' reason");
    }

    /// <summary>
    ///     Every pose rebuilt as integers per source point from the document alone: the keyframe from the vertex positions
    ///     (x, -y, z) undone, a later frame from the wide absolute positions, or from the narrow position deltas added to
    ///     the keyframe (or, with <paramref name="accumulate" />, the control's wrong reading, added to the previous frame).
    ///     Vertices sharing a source point must agree in every pose.
    /// </summary>
    private static SortedDictionary<int, (int X, int Y, int Z)>[] Poses(ModelDocument document, int frames, bool wide,
        bool accumulate)
    {
        var poses = Enumerable.Range(0, frames).Select(_ => new SortedDictionary<int, (int X, int Y, int Z)>()).ToArray();
        foreach (var primitive in document.Meshes[0].Primitives)
        {
            var points = primitive.PointIndices!.Values;
            for (var i = 0; i < points.Count; i++)
            {
                var keyframe = Redguard3DcModelTestSupport.Integer(primitive.Vertices[i].Position);
                var previous = keyframe;
                Put(poses[0], points[i], keyframe);
                for (var t = 0; t < frames - 1; t++)
                {
                    var target = primitive.MorphTargets[t];
                    (int X, int Y, int Z) pose;
                    if (wide)
                    {
                        pose = Redguard3DcModelTestSupport.Integer(target.AbsolutePositions![i]);
                    }
                    else
                    {
                        var delta = Redguard3DcModelTestSupport.Integer(target.PositionDeltas[i]);
                        var baseline = accumulate ? previous : keyframe;
                        pose = (baseline.X + delta.X, baseline.Y + delta.Y, baseline.Z + delta.Z);
                    }

                    previous = pose;
                    Put(poses[t + 1], points[i], pose);
                }
            }
        }

        return poses;
    }

    private static void Put(SortedDictionary<int, (int X, int Y, int Z)> pose, int point, (int X, int Y, int Z) value)
    {
        if (pose.TryGetValue(point, out var existing))
        {
            Assert.Equal(existing, value);
        }
        else
        {
            pose[point] = value;
        }
    }

    /// <summary>The oracle's pose digest: SHA-256 over (point, x, y, z) little-endian int32, points ascending.</summary>
    private static string PoseDigest(SortedDictionary<int, (int X, int Y, int Z)> pose)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var row = new byte[16];
        foreach (var (point, value) in pose)
        {
            BinaryPrimitives.WriteInt32LittleEndian(row, point);
            BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(4), value.X);
            BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(8), value.Y);
            BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(12), value.Z);
            hash.AppendData(row);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    ///     A reader plane's triangles back in the reference orientation and source corners: the document writes each
    ///     reference triangle (A, B, C) as face-local (A, C, B), and face vertex j carries source corner
    ///     <see cref="XnGineModelGeometry.CornerOfVertex" />.
    /// </summary>
    private static IEnumerable<(int, int, int)> ReferenceTriangles(XnGineLegacyComparison.ReaderPlane plane)
    {
        var n = plane.Vertices.Count;
        return plane.Triangles.Select(t => (XnGineModelGeometry.CornerOfVertex(n, t.A),
            XnGineModelGeometry.CornerOfVertex(n, t.C), XnGineModelGeometry.CornerOfVertex(n, t.B)));
    }

    /// <summary>
    ///     The stored-UV stream rebuilt from the document in the oracle's order and encoding (<c>uv_field_stream</c>): per
    ///     (plane, source corner) ascending, int32 plane, corner and point, then int16 u and v, little-endian.
    /// </summary>
    private static (int Rows, string Sha256) UvStream(ModelDocument document)
    {
        var rows = new List<(int Plane, int Corner, int Point, short U, short V)>();
        foreach (var primitive in document.Meshes[0].Primitives)
        {
            var faces = primitive.Faces!;
            var points = primitive.PointIndices!.Values;
            var uv = XnGineModelTestSupport.StoredUv(primitive);
            var ordinals = XnGineModelTestSupport.PlaneOrdinals(primitive);
            var cursor = 0;
            for (var f = 0; f < faces.FaceCount; f++)
            {
                var n = faces.FaceSizes[f];
                for (var j = 0; j < n; j++)
                {
                    var vertex = faces.CornerIndices[cursor + j];
                    rows.Add((ordinals[f], XnGineModelGeometry.CornerOfVertex(n, j), points[vertex], uv[cursor + j].U,
                        uv[cursor + j].V));
                }

                cursor += n;
            }
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var bytes = new byte[16];
        foreach (var row in rows.OrderBy(r => r.Plane).ThenBy(r => r.Corner))
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes, row.Plane);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), row.Corner);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), row.Point);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(12), row.U);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(14), row.V);
            hash.AppendData(bytes);
        }

        return (rows.Count, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    /// <summary>The keyframe's extent per axis over the referenced points, in native units.</summary>
    private static (int X, int Y, int Z) Extent(ModelDocument document)
    {
        var points = Poses(document, 1, false, false)[0].Values.ToList();
        return (points.Max(p => p.X) - points.Min(p => p.X), points.Max(p => p.Y) - points.Min(p => p.Y),
            points.Max(p => p.Z) - points.Min(p => p.Z));
    }

    private static IEnumerable<int> Ints(JsonNode? array)
    {
        return array!.AsArray().Select(value => value!.GetValue<int>());
    }

    private static string[] Strings(JsonNode? array)
    {
        return array!.AsArray().Select(value => value!.GetValue<string>()).ToArray();
    }
}
