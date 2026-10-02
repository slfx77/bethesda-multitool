using System.Globalization;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Schema;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.RealAsset.NifModelOracleSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Hop A3 (plan section 6, slices 3, 7 and 11) over every little-endian model of the cut-1a cover manifest: for
///     every submesh the existing extractor returns in bind pose (<see cref="NifGeometryExtractor.Extract" /> with
///     <c>bindPoseOnly</c>, which applies no node transform and no skinning), the reader's primitive for the same
///     geometry block (<see cref="RenderableSubmesh.SourceBlockIndex" />) must match bit for bit: positions, authored
///     normals, UV set 0 and triangles. The reader shares no code with the oracle (<see cref="NifStripTriangulator" />
///     re-implements the strip rule), so agreement is evidence, not a tautology; both share NifParser, so a parser
///     error is not caught here (design section 7.2).
/// </summary>
/// <remarks>
///     <para>
///         The oracle's documented differences are handled explicitly, never by loosening the comparison. It returns no
///         submesh for hidden shapes, shapes under a RootCollisionNode, particle emitter volumes, gore and editor-helper
///         names, BSShader-era shapes without a texture-bearing property, inactive NiSwitchNode children, embedded
///         "_lod" shapes beside non-LOD siblings, unsupported refraction helpers, and BSSegmentedTriShape (not in its
///         shape table); NiVisController-hidden subtrees are excluded only when a caller passes them, and none is passed
///         here. Such reader-only geometry is counted, not compared. It recomputes normals a block does not store, so
///         normals are compared only where the reader says Authored. It bakes a NiTexturingProperty texture transform
///         into the UVs, so UVs are compared only for shapes without one. It keeps list triangles that repeat an index,
///         which the reader drops and counts, so its list is filtered by that rule here and the drop count must equal
///         what the filter removed.
///     </para>
///     <para>
///         Rows skipped with a reason: big-endian files (this oracle reads little-endian streams only; the 95 console
///         files are compared against the PC file of the same path by <see cref="NifConsoleGeometryOracleTests" />),
///         <c>.kf</c> streams and declined controls (hop A2 asserts the declines), and files where neither side yields
///         geometry or the oracle yields only empty submeshes (nothing to compare; the counts are stated). Each file is
///         resolved through <see cref="Cut1aFixtureResolver" />; the pinned SHA-256 proves every copy is the same bytes.
///     </para>
/// </remarks>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class NifModelGeometryOracleTests
{
    [Theory]
    [MemberData(nameof(Cut1aCoverManifest.Rows), MemberType = typeof(Cut1aCoverManifest))]
    public void Geometry_MatchesTheBindPoseExtractor_BitForBit(string entry, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var file = Cut1aCoverManifest.Require(sha256);
        Assert.Equal(entry, file.Entry);
        var expectation = Cut1aProbeExpectations.Require(file);
        SkipUnlessComparableModel(file, expectation);
        SkipIfBigEndian(file, expectation, nameof(NifConsoleGeometryOracleTests));
        var bytes = Cut1aFixtureResolver.Require(file);
        Assert.Equal(sha256, Cut1aFixtureResolver.Sha256(bytes));

        var document = Read(bytes, file).Document;
        SceneValidation.ValidateStructure(document);
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(bytes));
        Assert.False(nif.IsBigEndian);
        var submeshes = NifGeometryExtractor.Extract(bytes, nif, bindPoseOnly: true)?.Submeshes
                        ?? new List<RenderableSubmesh>();
        var decoder = new NifBlockDecoder(NifSchema.LoadEmbedded(), nif, bytes);
        var primitives = PrimitivesByGeometryBlock(document);
        Assert.SkipWhen(submeshes.Count == 0 && primitives.Count == 0,
            $"{file}: neither the oracle nor the reader yields geometry; nothing to compare.");

        var compared = 0;
        var uvsSkipped = 0;
        (RenderableSubmesh Submesh, ScenePrimitive Primitive)? first = null;
        foreach (var submesh in submeshes)
        {
            var block = submesh.SourceBlockIndex;
            var expectedTriangles = WithoutRepeatedIndices(submesh.Triangles, out var repeated);
            if (!primitives.TryGetValue(block, out var pair))
            {
                Assert.True(expectedTriangles.Count == 0,
                    $"{file}: the oracle extracts block {block} but the reader emitted no primitive for it.");
                continue;
            }

            var (primitive, payload) = pair;
            var where = $"{file} block {block}";
            Assert.Null(FirstMismatch(submesh.Positions, primitive.Vertices.Select(v => v.Position).ToList(), where));
            if (primitive.NormalProvenance?.Kind == SceneNormalProvenanceKind.Authored)
            {
                Assert.NotNull(submesh.Normals);
                Assert.Null(FirstMismatch(submesh.Normals, primitive.Vertices.Select(v => v.Normal).ToList(), where));
            }

            if (HasTexturingProperty(decoder, nif, block))
            {
                uvsSkipped++;
            }
            else if (submesh.UVs is null)
            {
                Assert.Equal(0, Int(payload["uvSets"]));
            }
            else
            {
                Assert.Null(FirstUvMismatch(submesh.UVs, primitive, where));
            }

            Assert.Equal(expectedTriangles, primitive.Indices);
            if (string.Equals(Text(payload["triangles"]!["form"]), "list", StringComparison.Ordinal))
            {
                Assert.Equal(repeated, Int(payload["triangles"]!["droppedRepeatedIndex"]));
            }

            first ??= (submesh, primitive);
            compared++;
        }

        Assert.SkipWhen(compared == 0,
            $"{file}: the oracle's {submeshes.Count} submesh(es) are all empty and the reader's {primitives.Count} " +
            "primitive(s) are reader-only geometry; nothing to compare.");
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{file}: {compared} geometry block(s) compared ({uvsSkipped} without UVs: texture transform), " +
            $"{primitives.Count - compared} reader-only; oracle {submeshes.Count} submesh(es)."));

        // Control: moving one reader vertex by a single ulp is detected by the same comparison.
        var (controlSubmesh, controlPrimitive) = first!.Value;
        var moved = controlPrimitive.Vertices.Select(v => v.Position).ToList();
        moved[^1] = moved[^1] with { X = MathF.BitIncrement(moved[^1].X) };
        Assert.NotNull(FirstMismatch(controlSubmesh.Positions, moved, "control"));
    }
}
