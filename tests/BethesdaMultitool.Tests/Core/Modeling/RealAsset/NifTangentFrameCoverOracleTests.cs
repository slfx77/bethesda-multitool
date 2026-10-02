using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using BethesdaMultitool.Tests.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.RealAsset.NifModelOracleSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     The reader's typed tangents over the whole cut-1a cover, judged against each primitive's own UV frame
///     (<see cref="NifUvTangentFrame" />: area-weighted dP/du and dP/dv of UV set 0, projected into the normal's plane),
///     never against a stored tangent array: TANGENT.xyz must run along +dP/du and w must be glTF's handedness
///     sign(dot(cross(N, dP/du), -dP/dv)) at the rates the independent measurement reports
///     (TestOutput/nif-tangent-frame-20260928/MEASURE.md, run1: a Python decoder that reads the stored arrays itself and
///     never the reader's output).
/// </summary>
/// <remarks>
///     <para>
///         Populations (per the measurement's families, one primitive per geometry block, declined controls and .kf
///         streams skipped): PC inline arrays in little-endian files, measured 294,725 of 294,992 scored vertices along
///         +dP/du (99.91%) and w agreeing on 282,602 (95.80%; 99.94% of 275,854 without
///         <c>scol/scolbld06georgetown01.nif</c>, whose constant-handed SCOL shapes hold 12,226 of the 12,390 misses);
///         console packed layouts, 196,987 of 197,111 along (99.94%) and w on 196,882 (99.88%); inline arrays in
///         big-endian files, 543 of 543 along and w on 538. The floors below sit a little under those rates because this
///         transcription can differ from the measurement at a threshold (float summation order) and cannot see a zero
///         stored dP/dv array (6 PC vertices the measurement excluded, which the reader types with w undetermined, +1).
///         Each family's scored count must lie within 2% of the measured one, so the census is the measured population
///         (the reader emits primitives for placed shapes only, the measurement walked every shape block).
///     </para>
///     <para>
///         What discriminates: the along-U floors are what the reader's mapping before 2026-09-28 fails (a frame rotated
///         90 degrees in the tangent plane scored 0.12% PC and 0.09% console along +dP/du); and one control on the w
///         floors, a constant w = +1, agrees on at most 70% (about 37% of the scored vertices are mirrored), so the w
///         floors cannot be met by ignoring the UVs.
///     </para>
///     <para>
///         Two further checks restate the floors rather than control them, and are kept only as consistency checks.
///         The DirectX sense (-w) agrees exactly where w disagrees, because the typed w and the judge's w are both +1 or
///         -1; so it is asserted as that identity (every scored typed w is +1 or -1), not as a rate that could fail on
///         its own. glTF's rebuilt bitangent cross(N, T) * w, used as a tangent, runs along +dP/du on at most 1%, which
///         the along-U floor nearly implies: a unit bitangent in the tangent plane has |cos(B, dP/du)| =
///         |sin(angle(T, dP/du))|, below 0.714 whenever cos(T, dP/du) exceeds 0.7, so it can fail only through the
///         small part of T that leaves the tangent plane.
///     </para>
///     <para>
///         The exception MEASURE.md leaves to the owner: terrain LOD (shader flags 2 bit 2) and seven FO3 SCOL files
///         store a constant-handed frame, so w from the stored arrays misses the UV handedness there; the cover carries
///         <c>scolbld06georgetown01.nif</c>, <c>dcworld06.level8.x17.y-2.nif</c> and the X360
///         <c>wastelandnv.level4.x16.y-36.nif</c> of that class, which the overall PC rate absorbs. If the owner rules
///         for the UV-derived w there, raise the PC floor to the without-georgetown one.
///     </para>
/// </remarks>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class NifTangentFrameCoverOracleTests
{
    /// <summary>The measured scored-vertex counts (MEASURE.md, run1), the population check's reference.</summary>
    private static readonly IReadOnlyDictionary<string, long> MeasuredScored = new Dictionary<string, long>
    {
        [InlineLittleEndian] = 294_992,
        [ConsolePacked] = 197_111,
        [InlineBigEndian] = 543
    };

    private const string InlineLittleEndian = "PC inline arrays (little-endian files)";
    private const string ConsolePacked = "console packed layouts";
    private const string InlineBigEndian = "inline arrays in big-endian files";
    private const string GeorgetownScol = "scolbld06georgetown01.nif";

    [Fact]
    public void Tangents_RunAlongDpDu_WithGltfHandedness_AtTheMeasuredRates()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipWhen(Cut1aCoverManifest.Files.Count == 0,
            $"The cut-1a cover manifest ({Cut1aCoverManifest.RelativePath}) cannot be located.");
        var tallies = new Dictionary<string, Tally>
        {
            [InlineLittleEndian] = new(),
            [ConsolePacked] = new(),
            [InlineBigEndian] = new()
        };
        var withoutGeorgetown = new Tally();
        var unresolved = new List<string>();
        foreach (var file in Cut1aCoverManifest.Files)
        {
            if (file.IsDeclinedControl || file.IsAnimationStream)
            {
                continue;
            }

            var fixture = Cut1aFixtureResolver.TryResolve(file, out var tried);
            if (fixture is null)
            {
                unresolved.Add($"{file} (tried {string.Join("; ", tried)})");
                continue;
            }

            var expectation = Cut1aProbeExpectations.Require(file);
            if (expectation["declined"] is not null)
            {
                continue;
            }

            var document = Read(fixture.Bytes, file, options: ConsoleReadOptions(file, expectation)).Document;
            var georgetown = file.Entry.EndsWith(GeorgetownScol, StringComparison.OrdinalIgnoreCase);
            foreach (var (primitive, payload) in PrimitivesByGeometryBlock(document).Values)
            {
                if (primitive.Tangents is null)
                {
                    continue;
                }

                var family = Family(payload, file);
                var secondary = family == InlineLittleEndian && !georgetown ? withoutGeorgetown : null;
                Score(primitive, file.Entry, tallies[family], secondary);
            }
        }

        Assert.SkipWhen(unresolved.Count > 0,
            $"{unresolved.Count} cover file(s) did not resolve, so the census would not be the measured population: " +
            $"{string.Join(" | ", unresolved.Take(5))}. {RealAssetPaths.SkipMessage("the cut-1a cover corpus")}");
        var report = Report(tallies, withoutGeorgetown);
        TestContext.Current.TestOutputHelper?.WriteLine(report);

        foreach (var (family, tally) in tallies)
        {
            var measured = MeasuredScored[family];
            Assert.True(tally.Scored >= measured * 0.98 && tally.Scored <= measured * 1.02,
                $"{family}: {tally.Scored} scored vertices against the measured {measured}.{Environment.NewLine}{report}");

            // Restatements of the floors, not controls (see the remarks): the rebuilt bitangent is nearly implied by the
            // along-U floor, and the DirectX sense is exactly the w misses when every typed w is +1 or -1.
            Assert.True(tally.Rate(tally.RebuiltBitangentAlongU) <= 0.01,
                $"{family}: glTF's rebuilt bitangent runs along +dP/du on more than 1% of the scored vertices (a " +
                $"restatement of the along-U floor).{Environment.NewLine}{report}");
            Assert.True(tally.DirectXSenseAgrees == tally.Scored - tally.WAgrees,
                $"{family}: the DirectX-sense count {tally.DirectXSenseAgrees} is not the {tally.Scored - tally.WAgrees} " +
                $"w misses, so some scored typed w is neither +1 nor -1.{Environment.NewLine}{report}");
        }

        AssertAtLeast(tallies[InlineLittleEndian], t => t.AlongU, 0.9985, "PC inline: TANGENT along +dP/du", report);
        AssertAtLeast(tallies[InlineLittleEndian], t => t.WAgrees, 0.955, "PC inline: w = glTF handedness", report);
        AssertAtLeast(withoutGeorgetown, t => t.WAgrees, 0.9985,
            $"PC inline without {GeorgetownScol}: w = glTF handedness", report);
        AssertAtLeast(tallies[ConsolePacked], t => t.AlongU, 0.9985, "console: TANGENT along +dP/du", report);
        AssertAtLeast(tallies[ConsolePacked], t => t.WAgrees, 0.998, "console: w = glTF handedness", report);
        AssertAtLeast(tallies[InlineBigEndian], t => t.AlongU, 0.99, "inline big-endian: TANGENT along +dP/du", report);
        AssertAtLeast(tallies[InlineBigEndian], t => t.WAgrees, 0.985, "inline big-endian: w = glTF handedness", report);

        // Control on the w floors: a constant w = +1, which ignores the UVs, must not reach them.
        foreach (var (label, tally) in new[] { ("PC inline without the SCOL", withoutGeorgetown),
                     ("console", tallies[ConsolePacked]) })
        {
            Assert.True(tally.Rate(tally.ConstantPositiveAgrees) <= 0.70,
                $"{label}: a constant w = +1 agrees too often to be told apart.{Environment.NewLine}{report}");
        }
    }

    /// <summary>The measurement family of a primitive: its packed facts, else its file's byte order.</summary>
    private static string Family(JsonObject payload, Cut1aCoverFile file)
    {
        if (payload["packed"] is JsonObject)
        {
            return ConsolePacked;
        }

        return file.IsBigEndian ? InlineBigEndian : InlineLittleEndian;
    }

    /// <summary>Scores one primitive's valid vertices into the family tally (and the without-SCOL tally).</summary>
    private static void Score(ScenePrimitive primitive, string entry, Tally family, Tally? secondary)
    {
        var frame = NifUvTangentFrame.Compute(primitive.Vertices, primitive.Indices);
        var tangents = primitive.Tangents!.Values;
        var targets = secondary is null ? new[] { family } : new[] { family, secondary };
        foreach (var tally in targets)
        {
            tally.Vertices += tangents.Count;
        }

        for (var i = 0; i < tangents.Count; i++)
        {
            var t = tangents[i];
            var xyz = new Vector3(t.X, t.Y, t.Z);
            var excluded = Exclusion(frame.Classes[i], xyz);
            foreach (var tally in targets)
            {
                if (excluded is not null)
                {
                    tally.Excluded[excluded] = tally.Excluded.GetValueOrDefault(excluded) + 1;
                    continue;
                }

                var w = frame.GltfW(i);
                tally.Scored++;
                tally.AlongU += frame.CosineToU(i, xyz) > NifUvTangentFrame.Align ? 1 : 0;
                tally.WAgrees += (int)t.W == w ? 1 : 0;
                tally.DirectXSenseAgrees += (int)t.W == -w ? 1 : 0;
                tally.ConstantPositiveAgrees += w == 1 ? 1 : 0;
                tally.RebuiltBitangentAlongU +=
                    frame.CosineToU(i, frame.GltfBitangent(i, t)) > NifUvTangentFrame.Align ? 1 : 0;
                if ((int)t.W != w)
                {
                    tally.WMissesByFile[entry] = tally.WMissesByFile.GetValueOrDefault(entry) + 1;
                }
            }
        }
    }

    /// <summary>Why a vertex is not scored (the judge's class, or a zero or non-finite typed direction), or null.</summary>
    private static string? Exclusion(NifUvTangentFrame.VertexClass vertexClass, Vector3 tangent)
    {
        if (vertexClass != NifUvTangentFrame.VertexClass.Valid)
        {
            return vertexClass.ToString();
        }

        var length = tangent.Length();
        return float.IsFinite(length) && length > NifUvTangentFrame.ZeroVector ? null : "ZeroTangent";
    }

    private static void AssertAtLeast(Tally tally, Func<Tally, long> count, double floor, string what, string report)
    {
        var rate = tally.Rate(count(tally));
        Assert.True(rate >= floor,
            string.Create(CultureInfo.InvariantCulture,
                $"{what}: {count(tally)} of {tally.Scored} ({rate:P3}) is below the floor {floor:P2}.") +
            Environment.NewLine + report);
    }

    private static string Report(IReadOnlyDictionary<string, Tally> tallies, Tally withoutGeorgetown)
    {
        var text = new StringBuilder();
        foreach (var (family, tally) in tallies.Append(
                     new KeyValuePair<string, Tally>($"PC inline without {GeorgetownScol}", withoutGeorgetown)))
        {
            text.Append(CultureInfo.InvariantCulture,
                $"{family}: vertices {tally.Vertices}, scored {tally.Scored}, along +dP/du {tally.AlongU} " +
                $"({tally.Rate(tally.AlongU):P3}), w = glTF {tally.WAgrees} ({tally.Rate(tally.WAgrees):P3}), " +
                $"DirectX sense {tally.DirectXSenseAgrees}, constant +1 {tally.ConstantPositiveAgrees}, rebuilt " +
                $"bitangent along +dP/du {tally.RebuiltBitangentAlongU}; excluded ");
            text.AppendJoin(", ", tally.Excluded.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Key} {p.Value}")));
            text.Append("; most w misses: ");
            text.AppendJoin(", ", tally.WMissesByFile.OrderByDescending(p => p.Value).Take(5)
                .Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Key} {p.Value}")));
            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>Vertex counts of one population.</summary>
    private sealed class Tally
    {
        public long Vertices { get; set; }

        public long Scored { get; set; }

        public long AlongU { get; set; }

        public long WAgrees { get; set; }

        public long DirectXSenseAgrees { get; set; }

        public long ConstantPositiveAgrees { get; set; }

        public long RebuiltBitangentAlongU { get; set; }

        public Dictionary<string, long> Excluded { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, long> WMissesByFile { get; } = new(StringComparer.Ordinal);

        public double Rate(long count)
        {
            return Scored == 0 ? 0 : (double)count / Scored;
        }
    }
}
