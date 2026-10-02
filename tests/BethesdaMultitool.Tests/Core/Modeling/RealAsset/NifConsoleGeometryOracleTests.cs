using System.Globalization;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.RealAsset.NifModelOracleSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Hop A3 (plan section 6, slices 10 and 12) over every big-endian model of the cut-1a cover manifest: the console
///     file, read under the platform of its manifest source, is compared shape by shape against the PC file of the
///     same Data-relative path (<see cref="Cut1aConsoleTwins" />: the Steam Final build's Data folder, then the
///     installed Steam Fallout New Vegas Data folder, the twin pinned by SHA-256 and size so a wrong file is rejected).
///     A file the probe tagged with BSPackedAdditionalGeometryData goes through
///     <see cref="NifPackedGeometryComparison.Compare" /> (the format's own precision for the packed shapes, bit for bit
///     for the inline shapes beside them); a console file with inline streams only is compared bit for bit through
///     <see cref="NifPackedGeometryComparison.CompareInline" /> (positions, normals, UVs, tangents, colors, indices and
///     influences), which the measurement licenses: read with the probe's array readers, every inline console array of
///     the 17 such manifest files equals the PC one exactly (27 shapes; 2026-09-24).
/// </summary>
/// <remarks>
///     <para>
///         Rows skipped with a reason: declined controls and <c>.kf</c> streams (hop A2's rows; none is big-endian in
///         the manifest today), a console file without a PC twin (the <c>missing</c> map of the twins table names it;
///         empty today: all 95 have one), an absent build (the resolver's and the twin reader's skips), and a file
///         where neither side yields a primitive (8 of the 17 inline files: skeletons, particle systems and cameras,
///         which have no NiTri* geometry to compare).
///     </para>
///     <para>
///         Controls: for a packed file, <see cref="NifPackedGeometryComparison.AssertMovedVertexControl" /> (one half
///         ulp fails exactness, two leave the tolerance) and, where the decoded colors can tell the consoles apart, the
///         other platform's byte order fails the color comparison (<see cref="NifPackedGeometryComparison.CountColorDisagreements" />;
///         measured 2026-09-24 over the manifest's packed files: 97 shapes in 23 files carry such colors, and every one
///         reproduces the PC colors under its manifest platform's order and not under the other, so the control runs
///         on those 23 files and reports itself inapplicable on the 55 whose colors are grey or absent); for an inline
///         file, one reader vertex moved by a single float ulp is detected by the same bit-for-bit comparison.
///     </para>
/// </remarks>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class NifConsoleGeometryOracleTests
{
    [Theory]
    [MemberData(nameof(Cut1aCoverManifest.BigEndianRows), MemberType = typeof(Cut1aCoverManifest))]
    public void ConsoleGeometry_ReproducesThePcTwin(string entry, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var file = Cut1aCoverManifest.Require(sha256);
        Assert.Equal(entry, file.Entry);
        Assert.True(file.IsBigEndian, $"{file}: the big-endian rows listed a little-endian key.");
        var expectation = Cut1aProbeExpectations.Require(file);
        SkipUnlessComparableModel(file, expectation);
        var twin = Cut1aConsoleTwins.Require(file);
        var consoleBytes = Cut1aFixtureResolver.Require(file);
        Assert.Equal(sha256, Cut1aFixtureResolver.Sha256(consoleBytes));
        var pcBytes = Cut1aConsoleTwins.ReadPcBytes(twin);
        var options = ConsoleReadOptions(file, expectation)!;
        var platform = options[BethesdaModelRegistration.PlatformOption];
        Assert.Equal(twin.Platform, platform);

        var console = Read(consoleBytes, file, options: options).Document;
        var pc = NifModelTestSupport.Read(pcBytes, path: twin.PcPath).Document;
        SceneValidation.ValidateStructure(console);
        SceneValidation.ValidateStructure(pc);
        var consoleShapes = NifPackedGeometryComparison.Shapes(console);
        var pcShapes = NifPackedGeometryComparison.Shapes(pc);
        Assert.SkipWhen(consoleShapes.Count == 0 && pcShapes.Count == 0,
            $"{file}: neither the console file nor its PC twin {twin.PcPath} yields a primitive (a skeleton, particle " +
            $"or camera file); nothing to compare.");
        Assert.Equal(pcShapes.Count, consoleShapes.Count);

        if (HasTag(expectation, PackedGeometryTag))
        {
            ComparePacked(file, consoleBytes, pcBytes, console, pc, platform);
        }
        else
        {
            CompareInline(file, consoleShapes, pcShapes, platform);
        }
    }

    /// <summary>A packed file: the shared comparison, the moved-vertex control and the wrong-platform color control.</summary>
    private static void ComparePacked(Cut1aCoverFile file, byte[] consoleBytes, byte[] pcBytes, ModelDocument console,
        ModelDocument pc, string platform)
    {
        var result = NifPackedGeometryComparison.Compare(console, pc, pcBytes, file.Entry, platform);
        TestContext.Current.TestOutputHelper?.WriteLine(result.Summary(file.Entry, platform));

        // Control 1: one matched packed position component moved by one half ulp fails the exactness rule, by two the
        // one-ulp tolerance.
        NifPackedGeometryComparison.AssertMovedVertexControl(result, file.Entry);

        // Control 2: where some decoded color spans at least two bytes, the other platform's order disagrees with the
        // PC colors; a file whose colors are absent, grey or spread by a single byte (order-sensitive to the reader,
        // invisible to the one-byte tolerance) cannot tell the orders apart and says so.
        if (result.OrderDiscriminating > 0)
        {
            var other = new Dictionary<string, string>
            {
                [BethesdaModelRegistration.PlatformOption] = platform == NifPackedPlatformOption.Ps3Value
                    ? NifPackedPlatformOption.X360Value
                    : NifPackedPlatformOption.Ps3Value
            };
            var misread = Read(consoleBytes, file, options: other).Document;
            var disagreeing = NifPackedGeometryComparison.CountColorDisagreements(misread, pc);
            Assert.True(disagreeing > 0, $"{file}: the wrong platform's color order was not detected");
            TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{file}: the other platform's color order disagrees with the PC colors on {disagreeing} vertices."));
        }
        else
        {
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{file}: no decoded vertex color spans two bytes; the wrong-order control has nothing to detect on this file.");
        }
    }

    /// <summary>An inline file: every shape bit for bit against the twin's, and the one-ulp control.</summary>
    private static void CompareInline(Cut1aCoverFile file, List<(ScenePrimitive Primitive, JsonObject Payload)> consoleShapes,
        List<(ScenePrimitive Primitive, JsonObject Payload)> pcShapes, string platform)
    {
        var compared = 0;
        var components = 0;
        (ScenePrimitive Primitive, ScenePrimitive Reference)? first = null;
        for (var ordinal = 0; ordinal < consoleShapes.Count; ordinal++)
        {
            var (primitive, payload) = consoleShapes[ordinal];
            var (reference, pcPayload) = pcShapes[ordinal];
            Assert.Null(payload["packed"]);
            var where = $"{file} shape {ordinal} (console block {payload["geometryBlock"]}, PC block {pcPayload["geometryBlock"]})";
            components += NifPackedGeometryComparison.CompareInline(primitive, reference, where);
            first ??= (primitive, reference);
            compared++;
        }

        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{file} ({platform}, inline): {compared} shape(s), {components} components equal the PC twin bit for bit."));

        // Control: moving one reader vertex by a single float ulp is detected by the same comparison.
        var (controlPrimitive, controlReference) = first!.Value;
        var moved = controlPrimitive.Vertices.Select(v => v.Position).ToList();
        moved[^1] = moved[^1] with { X = MathF.BitIncrement(moved[^1].X) };
        Assert.NotNull(FirstMismatch(controlReference.Vertices.Select(v => v.Position).ToList(), moved, "control"));
    }
}
