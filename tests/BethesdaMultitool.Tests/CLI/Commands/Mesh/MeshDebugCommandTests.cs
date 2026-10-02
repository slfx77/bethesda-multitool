using BethesdaMultitool.Core.Modeling.Shadowkey;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using BethesdaMultitool.CLI.Commands.Mesh;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Export;
using Slfx77.Multitool.Core.Models.Sources;
using Slfx77.Multitool.Media.Blender.Package;
using Xunit;

namespace BethesdaMultitool.Tests.CLI.Commands.Mesh;

/// <summary>
///     The <c>mesh</c> debug commands (plan section 6, slice 9; Shared adoption at 6c94992) on synthetic NIF files in a
///     private temporary directory: <c>dump</c> (the Shared schema-version-1 envelope, deterministic and sensitive),
///     <c>validate</c>, <c>fidelity</c>, <c>formats</c> and <c>package</c>. Nothing here launches Blender: <c>fidelity</c>
///     runs the GLB writer, and <c>package</c> uses the Shared package-only preflight, which never locates a tool. The
///     memory gate is injected with an ample sample so results never depend on the host's free memory.
/// </summary>
public sealed class MeshDebugCommandTests : IDisposable
{
    private static readonly float[] QuadVertices = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 1f, 1f, 0.5f];
    private static readonly float[] MovedVertices = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 1f, 1f, 0.25f];
    private static readonly float[] QuadNormals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f];
    private static readonly ushort[] QuadTriangles = [0, 1, 2, 1, 3, 2];

    private readonly string _directory = Directory.CreateTempSubdirectory("bmt-mesh-debug-").FullName;

    /// <summary>Removes the temporary directory.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is harmless; the test result stands.
        }
    }

    /// <summary>
    ///     A dump is the Shared envelope: <c>schemaVersion</c> 1 and a <c>document</c> whose format is bmt.nif and whose
    ///     one primitive carries the quad's four vertices. Native rows are retained in both modes with the same count, and
    ///     <c>--native</c> is what puts raw bytes on them: zero rows carry raw content without it, at least one with it.
    ///     Control: a command that ignored <c>--native</c> would report equal raw-content counts and equal lengths.
    /// </summary>
    [Fact]
    public async Task Dump_WritesTheSharedEnvelope_AndNativeRawContentFollowsTheOption()
    {
        var path = WriteNif("model.nif", Quad(QuadVertices));

        var metadata = await DumpAsync(path);
        var full = await DumpAsync(path, true);

        Assert.Equal(0, metadata.Exit);
        Assert.Equal(0, full.Exit);
        Assert.Equal(string.Empty, metadata.Error);
        using var metadataJson = JsonDocument.Parse(metadata.Json);
        using var fullJson = JsonDocument.Parse(full.Json);
        Assert.Equal(1, metadataJson.RootElement.GetProperty("schemaVersion").GetInt32());
        var document = metadataJson.RootElement.GetProperty("document");
        Assert.Equal("bmt.nif", document.GetProperty("sourceFormat").GetString());
        Assert.Equal(4, Primitive(metadataJson).GetProperty("vertices").GetArrayLength());

        var metadataRows = document.GetProperty("nativeStates").EnumerateArray().ToList();
        var fullRows = fullJson.RootElement.GetProperty("document").GetProperty("nativeStates").EnumerateArray().ToList();
        Assert.NotEmpty(metadataRows);
        Assert.Equal(metadataRows.Count, fullRows.Count);
        Assert.Contains(metadataRows, row => row.GetProperty("kind").GetString() == "bmt.nif.block");
        Assert.Equal(0, metadataRows.Count(row => row.GetProperty("hasRawContent").GetBoolean()));
        Assert.True(fullRows.Count(row => row.GetProperty("hasRawContent").GetBoolean()) > 0,
            "--native must retain raw bytes on at least one native row");
        Assert.True(full.Json.Length > metadata.Json.Length, "--native must lengthen the dump with the raw bytes");
    }

    /// <summary>
    ///     Two dumps of one file are byte-identical, and moving one vertex coordinate (z of vertex 3, 0.5 to 0.25) changes
    ///     the output and that vertex's position. Control: the unchanged file dumps identically twice, so the difference
    ///     is the vertex, and a dump that hashed or omitted geometry would leave the position unchanged.
    /// </summary>
    [Fact]
    public async Task Dump_IsDeterministic_AndOneMovedVertexChangesItsPosition()
    {
        var path = WriteNif("model.nif", Quad(QuadVertices));

        var first = await DumpAsync(path);
        var second = await DumpAsync(path);
        await File.WriteAllBytesAsync(path, Quad(MovedVertices), TestContext.Current.CancellationToken);
        var moved = await DumpAsync(path);

        Assert.Equal(0, first.Exit);
        Assert.Equal(0, second.Exit);
        Assert.Equal(0, moved.Exit);
        Assert.Equal(first.Json, second.Json);
        Assert.NotEqual(first.Json, moved.Json);
        using var firstJson = JsonDocument.Parse(first.Json);
        using var movedJson = JsonDocument.Parse(moved.Json);
        var before = Position(firstJson, 3);
        var after = Position(movedJson, 3);
        Assert.Equal(new[] { 1f, 1f, 0.5f }, before);
        Assert.Equal(new[] { 1f, 1f, 0.25f }, after);
        Assert.Equal(Position(firstJson, 0), Position(movedJson, 0));
    }

    /// <summary>
    ///     A material alpha of 0.5 instead of 0.75 changes the material's base color alpha. Control: the 0.75 fixture
    ///     dumps identically twice, so the difference is the alpha.
    /// </summary>
    [Fact]
    public async Task Dump_AChangedMaterialAlphaChangesTheBaseColor()
    {
        var path = WriteNif("material.nif", MaterialFixture(0.75f));

        var first = await DumpAsync(path);
        var again = await DumpAsync(path);
        await File.WriteAllBytesAsync(path, MaterialFixture(0.5f), TestContext.Current.CancellationToken);
        var faded = await DumpAsync(path);

        Assert.Equal(0, first.Exit);
        Assert.Equal(0, faded.Exit);
        Assert.Equal(first.Json, again.Json);
        using var firstJson = JsonDocument.Parse(first.Json);
        using var fadedJson = JsonDocument.Parse(faded.Json);
        Assert.Equal(0.75f, BaseColorAlpha(firstJson));
        Assert.Equal(0.5f, BaseColorAlpha(fadedJson));
    }

    /// <summary>
    ///     Bytes no reader recognizes are refused with exit 1, the Shared reason on standard error and nothing on standard
    ///     output; the NIF control dumps with exit 0 and an empty error channel.
    /// </summary>
    [Fact]
    public async Task Dump_RefusesAnUnrecognizedInput()
    {
        var notes = Path.Combine(_directory, "notes.nif");
        await File.WriteAllTextAsync(notes, "not a model", TestContext.Current.CancellationToken);
        var model = WriteNif("model.nif", Quad(QuadVertices));

        var refused = await DumpAsync(notes);
        var control = await DumpAsync(model);

        Assert.Equal(1, refused.Exit);
        Assert.Contains("No registered reader recognized", refused.Error, StringComparison.Ordinal);
        Assert.Empty(refused.Json);
        Assert.Equal(0, control.Exit);
        Assert.Equal(string.Empty, control.Error);
    }

    /// <summary>
    ///     <c>--output</c> publishes the same bytes a standard-output dump streams; a second run without
    ///     <c>--overwrite</c> refuses (exit 1, message naming the flag) and leaves the file untouched; with
    ///     <c>--overwrite</c> it replaces a damaged file; and no staging file is left behind. Control: a refusal that
    ///     still wrote would change the damaged file's bytes before the overwrite run.
    /// </summary>
    [Fact]
    public async Task Dump_OutputFile_MatchesStandardOutput_RefusesAnExistingFile_AndOverwritesOnRequest()
    {
        var model = WriteNif("model.nif", Quad(QuadVertices));
        var output = Path.Combine(_directory, "out");
        var json = Path.Combine(output, "model.json");
        var token = TestContext.Current.CancellationToken;

        var streamed = await DumpAsync(model);
        var first = await DumpToFileAsync(model, json, false);
        Assert.Equal(0, first.Exit);
        Assert.Equal(string.Empty, first.Error);
        Assert.Empty(first.Json);
        Assert.Equal(streamed.Json, await File.ReadAllBytesAsync(json, token));

        await File.WriteAllTextAsync(json, "damaged", token);
        var second = await DumpToFileAsync(model, json, false);
        Assert.Equal(1, second.Exit);
        Assert.Contains("--overwrite", second.Error, StringComparison.Ordinal);
        Assert.Equal("damaged", await File.ReadAllTextAsync(json, token));

        var third = await DumpToFileAsync(model, json, true);
        Assert.Equal(0, third.Exit);
        Assert.Equal(streamed.Json, await File.ReadAllBytesAsync(json, token));
        Assert.Equal(new[] { json }, Directory.GetFiles(output));
    }

    /// <summary>
    ///     Validation succeeds on a good fixture without planning any writer, and fails with exit 1 and a reason when the
    ///     root's child reference points past the last block. Control: the same builder with a valid reference passes.
    /// </summary>
    [Fact]
    public async Task Validate_PassesAGoodFixtureAndFailsABrokenChildReference()
    {
        var good = WriteNif("good.nif", ChildFixture(1));
        var broken = WriteNif("broken.nif", ChildFixture(7));

        var (goodExit, goodText) = await ValidateAsync(good);
        var (brokenExit, brokenText) = await ValidateAsync(broken);

        Assert.Equal(0, goodExit);
        Assert.Contains("fidelity: none registered or selected", goodText, StringComparison.Ordinal);
        Assert.DoesNotContain("status:", goodText, StringComparison.Ordinal);
        Assert.Equal(1, brokenExit);
        Assert.Contains("status: Failed", brokenText, StringComparison.Ordinal);
        Assert.Contains("reason: ", brokenText, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A textured fixture whose DDS resolves through <c>--data-root</c>: <c>mesh info</c> plans the GLB writer and leaves
    ///     its pixel rows pending, while <c>mesh fidelity --format glb</c> resolves every row (none pending) and exits 0.
    ///     Control: a fidelity command that did not request resolution would report the same pending count as info.
    /// </summary>
    [Fact]
    public async Task Fidelity_ResolvesTheGlbRowsThatInfoLeavesPending()
    {
        var data = Path.Combine(_directory, "data");
        Directory.CreateDirectory(Path.Combine(data, "textures", "t"));
        await File.WriteAllBytesAsync(Path.Combine(data, "textures", "t", "base.dds"),
            SyntheticDds.Dxt1Single(SyntheticDds.OpaqueDxt1Block), TestContext.Current.CancellationToken);
        var model = WriteNif("textured.nif", TexturedFixture(@"textures\t\base.dds"));

        var infoJson = new MemoryStream();
        await MeshCommand.ExecuteInfoAsync(model, null, "fnv", true, false, new StringWriter(), infoJson,
            new StringWriter(), TestContext.Current.CancellationToken, AmpleMemory(), [data]);
        var fidelityJson = new MemoryStream();
        var fidelityError = new StringWriter();
        var exit = await MeshDebugCommands.ExecuteFidelityAsync(model, null, "fnv", "glb", 1, true, new StringWriter(),
            fidelityJson, fidelityError, TestContext.Current.CancellationToken, AmpleMemory(), [data]);

        using var info = JsonDocument.Parse(infoJson.ToArray());
        var infoGlb = info.RootElement.GetProperty("writers").EnumerateArray()
            .Single(writer => writer.GetProperty("format").GetString() == "glb");
        Assert.True(infoGlb.GetProperty("fidelity").GetProperty("pending").GetInt32() > 0,
            "mesh info must leave the GLB pixel preparation pending");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, fidelityError.ToString());
        using var fidelity = JsonDocument.Parse(fidelityJson.ToArray());
        var glb = Assert.Single(fidelity.RootElement.GetProperty("writers").EnumerateArray());
        Assert.Equal("glb", glb.GetProperty("format").GetString());
        Assert.Equal(JsonValueKind.Null, glb.GetProperty("failure").ValueKind);
        Assert.Equal(0, glb.GetProperty("fidelity").GetProperty("pending").GetInt32());
    }

    /// <summary>
    ///     Cut 2, BC7/BC6H DDS through the real command surface. A typed BC7 texture (DXGI 98) resolved through
    ///     <c>--data-root</c> flows to Shared's decoders: <c>mesh fidelity --format glb</c> resolves its pixels row
    ///     Converted with no writer failure. A BC6H texture (DXGI 95), which Shared's inspection refuses, gets the
    ///     honest rows instead: <c>mesh info</c> plans a Degraded <c>image.unsupported</c> pixels row naming Shared's
    ///     reason, and resolution reports that reason as the GLB writer's failure rather than inventing pixels. Each
    ///     format is the other's control through an identical harness.
    /// </summary>
    [Theory]
    [InlineData(98u, true)]
    [InlineData(95u, false)]
    public async Task Fidelity_Bc7FlowsToShared_AndBc6hKeepsHonestRows(uint dxgiFormat, bool supported)
    {
        var data = Path.Combine(_directory, "data");
        Directory.CreateDirectory(Path.Combine(data, "textures", "t"));
        await File.WriteAllBytesAsync(Path.Combine(data, "textures", "t", "base.dds"),
            SyntheticDds.Dx10(dxgiFormat, 4, 4, new byte[16]), TestContext.Current.CancellationToken);
        var model = WriteNif("textured.nif", TexturedFixture(@"textures\t\base.dds"));

        var infoJson = new MemoryStream();
        var infoExit = await MeshCommand.ExecuteInfoAsync(model, null, "fnv", true, true, new StringWriter(),
            infoJson, new StringWriter(), TestContext.Current.CancellationToken, AmpleMemory(), [data]);
        var fidelityJson = new MemoryStream();
        var fidelityError = new StringWriter();
        var fidelityExit = await MeshDebugCommands.ExecuteFidelityAsync(model, null, "fnv", "glb", 1, true,
            new StringWriter(), fidelityJson, fidelityError, TestContext.Current.CancellationToken, AmpleMemory(),
            [data]);

        Assert.Equal(0, infoExit);

        // Shared's fidelity result exits 1 when a writer fails (the BC6H plan throws inside the GLB writer and is
        // reported as the per-writer failure); the JSON is still written first, so the rows below stay checkable.
        Assert.Equal(supported ? 0 : 1, fidelityExit);
        Assert.Equal(string.Empty, fidelityError.ToString());
        using var info = JsonDocument.Parse(infoJson.ToArray());
        var infoGlb = info.RootElement.GetProperty("writers").EnumerateArray()
            .Single(writer => writer.GetProperty("format").GetString() == "glb");
        var pixels = Assert.Single(infoGlb.GetProperty("fidelity").GetProperty("rows").EnumerateArray(),
            row => row.GetProperty("featureId").GetString() == "pixels" &&
                   row.GetProperty("target").GetProperty("kind").GetString() == "Image");
        using var fidelity = JsonDocument.Parse(fidelityJson.ToArray());
        var glb = Assert.Single(fidelity.RootElement.GetProperty("writers").EnumerateArray());
        if (supported)
        {
            Assert.Equal("image.pixel-preparation-pending", pixels.GetProperty("reasonCode").GetString());
            Assert.Equal(JsonValueKind.Null, glb.GetProperty("failure").ValueKind);
            Assert.Equal(0, glb.GetProperty("fidelity").GetProperty("pending").GetInt32());
            var resolved = Assert.Single(glb.GetProperty("fidelity").GetProperty("rows").EnumerateArray(),
                row => row.GetProperty("featureId").GetString() == "pixels" &&
                       row.GetProperty("target").GetProperty("kind").GetString() == "Image");
            Assert.Equal("Converted", resolved.GetProperty("outcome").GetString());
        }
        else
        {
            Assert.Equal("Degraded", pixels.GetProperty("outcome").GetString());
            Assert.Equal("image.unsupported", pixels.GetProperty("reasonCode").GetString());
            Assert.Contains("DXGI", pixels.GetProperty("description").GetString(), StringComparison.Ordinal);
            Assert.Contains("not an implemented", glb.GetProperty("failure").GetString(),
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     <c>mesh formats</c> lists bmt.nif with its variants and default units, and its JSON carries the same format id;
    ///     since cut 2 it lists bmt.starfield.mesh second, since cut-1c slice 5 bmt.xngine.3d after it, with its Unknown
    ///     default units (the guard row), and since slice 6 bmt.redguard.3dc last, with the Redguard actor row.
    ///     Control: a reader without format metadata prints "metadata: unavailable" and none of these lines.
    /// </summary>
    [Fact]
    public async Task Formats_ListsTheNifReaderWithItsVariantsAndUnits()
    {
        var text = new StringWriter();
        var textExit = await MeshDebugCommands.ExecuteFormatsAsync(false, text, new MemoryStream(), new StringWriter(),
            TestContext.Current.CancellationToken);
        var json = new MemoryStream();
        var jsonExit = await MeshDebugCommands.ExecuteFormatsAsync(true, new StringWriter(), json, new StringWriter(),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, textExit);
        Assert.Equal(0, jsonExit);
        var output = text.ToString();
        var fnv = GameProfiles.For(BethesdaGame.FalloutNewVegas).Units.MetersPerUnit;
        Assert.Contains("reader: bmt.nif", output, StringComparison.Ordinal);
        Assert.Contains("  metadata: available", output, StringComparison.Ordinal);
        Assert.Contains("  variant: NIF 20.2.0.7, user 11, BS 34, big-endian", output, StringComparison.Ordinal);
        Assert.Contains("  units: " + fnv.ToString("R", CultureInfo.InvariantCulture) + " meters per source unit",
            output, StringComparison.Ordinal);
        Assert.Contains("  unitProvenance: Assumed", output, StringComparison.Ordinal);
        Assert.Contains("reader: bmt.starfield.mesh", output, StringComparison.Ordinal);
        Assert.Contains("  variant: Starfield .mesh v2 with meshlet tail", output, StringComparison.Ordinal);
        Assert.Contains("  variant: Starfield .mesh v2 without meshlet tail", output, StringComparison.Ordinal);
        Assert.Contains("  units: 1 meters per source unit", output, StringComparison.Ordinal);

        Assert.Contains("reader: bmt.xngine.3d", output, StringComparison.Ordinal);
        Assert.Contains("  variant: XnGine v2.7 mesh, 10-byte plane headers", output, StringComparison.Ordinal);
        Assert.Contains("reader: bmt.redguard.3dc", output, StringComparison.Ordinal);
        Assert.Contains("  variant: Redguard .3DC v2.6 frame stack, 3-dword frame records", output, StringComparison.Ordinal);
        Assert.Contains("reader: bmt.shadowkey.mesh", output, StringComparison.Ordinal);
        Assert.Contains("  variant: " + ShadowkeyModelFormatMetadata.MultiSkinVariant, output, StringComparison.Ordinal);
        Assert.Contains("reader: bmt.shadowkey.zone", output, StringComparison.Ordinal);
        Assert.Contains("  variant: " + ShadowkeyModelFormatMetadata.UncountedSkyVariant, output, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(json.ToArray());
        var readers = document.RootElement.GetProperty("readers").EnumerateArray().ToArray();
        Assert.Equal(
            new[]
            {
                "bmt.nif", "bmt.starfield.mesh", "bmt.xngine.3d", "bmt.redguard.3dc", "bmt.shadowkey.mesh",
                "bmt.shadowkey.zone"
            },
            readers.Select(r => r.GetProperty("formatId").GetString()).ToArray());
        Assert.Equal(ShadowkeyModelFormatMetadata.MeshVariants,
            readers[4].GetProperty("metadata").GetProperty("supportedVariants").EnumerateArray()
                .Select(variant => variant.GetString()!).ToArray());
        Assert.Equal(ShadowkeyModelFormatMetadata.ZoneVariants,
            readers[5].GetProperty("metadata").GetProperty("supportedVariants").EnumerateArray()
                .Select(variant => variant.GetString()!).ToArray());
        var reader = readers[0];
        Assert.Equal("bmt.nif", reader.GetProperty("formatId").GetString());
        var starfield = readers[1];
        Assert.True(starfield.GetProperty("metadataAvailable").GetBoolean());
        Assert.Equal(
            new[]
            {
                "Starfield .mesh v2 with meshlet tail", "Starfield .mesh v2 without meshlet tail",
                "Starfield .mesh v1 (no retail sample)", "Starfield .mesh v0, no LOD section (no retail sample)"
            },
            starfield.GetProperty("metadata").GetProperty("supportedVariants").EnumerateArray()
                .Select(variant => variant.GetString()).ToArray());
        Assert.True(reader.GetProperty("metadataAvailable").GetBoolean());
        Assert.Contains("NIF 20.2.0.7, user 11, BS 14, little-endian",
            reader.GetProperty("metadata").GetProperty("supportedVariants").EnumerateArray()
                .Select(variant => variant.GetString()));
        Assert.Equal("glb,blend", string.Join(",", document.RootElement.GetProperty("writers").EnumerateArray()
            .Select(writer => writer.GetProperty("format").GetString())));
    }

    /// <summary>
    ///     The variants are exactly the probe's evidence for the ten scene-graph keys, since cut-1b slice 10 the
    ///     twenty-four .kf stream keys (every BS version a .kf is read at, both byte orders, with the .kf suffix) and
    ///     since cut 2 the two little-endian 20.0.0.4 .kf keys (user 10 and 11 at BS 11), and the default units are the
    ///     resolver's no-game answer. Controls: a scene graph at an animation-only BS version (BS 24) and a 20.0.0.4
    ///     scene graph probe Unsupported and their evidence is not listed; no rule keeps the retired 'later-cut(1b)'
    ///     handling.
    /// </summary>
    [Fact]
    public void FormatMetadata_VariantsAreTheKeysTheProbeSupports()
    {
        var metadata = NifModelFormatMetadata.Description;

        foreach (var bs in NifTestFileBuilder.CutOneABsVersions)
        {
            foreach (var bigEndian in new[] { false, true })
            {
                var probe = NifModelTestSupport.Probe(NifModelTestSupport.SingleTriShape(Streams(QuadVertices),
                    QuadTriangles, bigEndian, bs));
                Assert.Equal(ModelProbeKind.Supported, probe.Kind);
                Assert.Contains(probe.Evidence!.Description, metadata.SupportedVariants);
            }
        }

        foreach (var bs in NifModelProbe.AnimationStreamBsVersions)
        {
            foreach (var bigEndian in new[] { false, true })
            {
                var builder = new NifTestFileBuilder(bigEndian, bs);
                builder.AddBlock("NiControllerSequence", w => w.U32(0));
                var probe = NifModelTestSupport.Probe(builder.Build());
                Assert.Equal(ModelProbeKind.Supported, probe.Kind);
                Assert.Contains(probe.Evidence!.Description, metadata.SupportedVariants);
            }
        }

        foreach (var userVersion in NifModelProbe.LegacyKfUserVersions)
        {
            var probe = NifModelTestSupport.Probe(NifKf2004Fixtures.Build(userVersion: userVersion));
            Assert.Equal(ModelProbeKind.Supported, probe.Kind);
            Assert.Contains(probe.Evidence!.Description, metadata.SupportedVariants);
        }

        Assert.Equal((NifTestFileBuilder.CutOneABsVersions.Length + NifModelProbe.AnimationStreamBsVersions.Count) * 2 +
                     NifModelProbe.LegacyKfUserVersions.Count,
            metadata.SupportedVariants.Count);
        var deferred = NifModelTestSupport.Probe(NifModelTestSupport.SingleTriShape(Streams(QuadVertices),
            QuadTriangles, false, 24));
        Assert.Equal(ModelProbeKind.Unsupported, deferred.Kind);
        Assert.DoesNotContain(deferred.Evidence!.Description, metadata.SupportedVariants);
        var legacySceneGraph = NifModelTestSupport.Probe(NifKf2004Fixtures.BuildSceneGraph());
        Assert.Equal(ModelProbeKind.Unsupported, legacySceneGraph.Kind);
        Assert.DoesNotContain(legacySceneGraph.Evidence!.Description, metadata.SupportedVariants);
        Assert.Contains(metadata.AdmissionRules, rule => rule.Id == "kf-stream");
        Assert.Contains(metadata.AdmissionRules, rule => rule.Id == "kf-stream-20004");
        Assert.DoesNotContain(metadata.AdmissionRules,
            rule => rule.Handling.Contains("later-cut(1b)", StringComparison.Ordinal));
        Assert.Equal(GameProfiles.For(BethesdaGame.FalloutNewVegas).Units.MetersPerUnit,
            metadata.DefaultUnits.MetersPerUnit);
        Assert.Equal(SceneValueProvenance.Assumed, metadata.DefaultUnits.Provenance);
        Assert.Contains(metadata.AdmissionRules, rule => rule.Id == "ddx-gate");
    }

    /// <summary>
    ///     <c>mesh package</c> publishes the Blender package (its first entry is the manifest naming bmt.nif, followed by
    ///     the primitive streams) through the Shared operation without running Blender and reports Converted; a second run
    ///     without <c>--overwrite</c> is Skipped with exit 0 (the Shared request's semantics) and leaves the zip untouched;
    ///     with <c>--overwrite</c> it replaces a damaged file, and no staging file is left behind. No tool settings are
    ///     consulted: the package-only preflight cannot be failed by a host Blender.
    /// </summary>
    [Fact]
    public async Task Package_WritesWithoutBlender_SkipsAnExistingZip_AndOverwritesOnRequest()
    {
        var model = WriteNif("model.nif", Quad(QuadVertices));
        var output = Path.Combine(_directory, "out");
        var zip = Path.Combine(output, "model.zip");
        var token = TestContext.Current.CancellationToken;

        var first = await PackageAsync(model, zip, false);
        Assert.Equal(0, first.Exit);
        Assert.Equal(string.Empty, first.Error);
        Assert.Contains("outcome: Converted", first.Text, StringComparison.Ordinal);
        // The Shared text formatter escapes backslashes in every field, so the path reads with doubled separators.
        Assert.Contains("output: " + zip.Replace("\\", "\\\\", StringComparison.Ordinal), first.Text, StringComparison.Ordinal);
        Assert.Contains("Blender package only; Blender was not run.", first.Text, StringComparison.Ordinal);
        Assert.Contains("plannedFidelity: ", first.Text, StringComparison.Ordinal);
        var bytes = await File.ReadAllBytesAsync(zip, token);
        AssertPackage(bytes);
        Assert.Contains("bytesWritten: " + bytes.Length.ToString(CultureInfo.InvariantCulture), first.Text,
            StringComparison.Ordinal);

        var second = await PackageAsync(model, zip, false);
        Assert.Equal(0, second.Exit);
        Assert.Contains("outcome: Skipped", second.Text, StringComparison.Ordinal);
        Assert.Contains("reason: The planned output already exists.", second.Text, StringComparison.Ordinal);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(zip, token));

        await File.WriteAllTextAsync(zip, "not a package", token);
        var third = await PackageAsync(model, zip, true);
        Assert.Equal(0, third.Exit);
        Assert.Contains("outcome: Converted", third.Text, StringComparison.Ordinal);
        AssertPackage(await File.ReadAllBytesAsync(zip, token));
        Assert.Equal(new[] { zip }, Directory.GetFiles(output));
    }

    /// <summary>
    ///     Bytes no reader recognizes are reported as NotAModel with exit 1 through the Shared formatter and publish no
    ///     zip; the NIF control publishes with exit 0.
    /// </summary>
    [Fact]
    public async Task Package_RefusesAnUnrecognizedInput()
    {
        var notes = Path.Combine(_directory, "notes.nif");
        await File.WriteAllTextAsync(notes, "not a model", TestContext.Current.CancellationToken);
        var model = WriteNif("model.nif", Quad(QuadVertices));
        var refusedZip = Path.Combine(_directory, "notes.zip");
        var controlZip = Path.Combine(_directory, "model.zip");

        var refused = await PackageAsync(notes, refusedZip, false);
        var control = await PackageAsync(model, controlZip, false);

        Assert.Equal(1, refused.Exit);
        Assert.Contains("outcome: NotAModel", refused.Text, StringComparison.Ordinal);
        Assert.False(File.Exists(refusedZip));
        Assert.Equal(0, control.Exit);
        Assert.True(File.Exists(controlZip));
    }

    /// <summary>A gate whose every sample reports 64 GiB available, so admission never waits on the host.</summary>
    private static ModelMemoryGate AmpleMemory()
    {
        return new ModelMemoryGate(() => new ModelMemorySample(64L * 1024 * 1024 * 1024));
    }

    /// <summary>Asserts a package: the manifest first, naming bmt.nif, then at least one primitive stream.</summary>
    private static void AssertPackage(byte[] bytes)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes, false), ZipArchiveMode.Read);
        Assert.Equal(BlenderPackageWriter.ManifestEntryName, archive.Entries[0].FullName);
        using (var manifestStream = archive.Entries[0].Open())
        {
            using var manifest = JsonDocument.Parse(manifestStream);
            Assert.Equal("bmt.nif", manifest.RootElement.GetProperty("sourceFormat").GetString());
        }

        Assert.Contains(archive.Entries, entry => entry.FullName.StartsWith("streams/", StringComparison.Ordinal));
    }

    /// <summary>The first primitive of the first mesh of a dump.</summary>
    private static JsonElement Primitive(JsonDocument dump)
    {
        return dump.RootElement.GetProperty("document").GetProperty("meshes")[0].GetProperty("primitives")[0];
    }

    /// <summary>One vertex's position (x, y, z) in the first primitive of a dump.</summary>
    private static float[] Position(JsonDocument dump, int vertex)
    {
        return Primitive(dump).GetProperty("vertices")[vertex].GetProperty("position").EnumerateArray()
            .Select(component => component.GetSingle()).ToArray();
    }

    /// <summary>The alpha (fourth component) of the first material's base color in a dump.</summary>
    private static float BaseColorAlpha(JsonDocument dump)
    {
        return dump.RootElement.GetProperty("document").GetProperty("materials")[0].GetProperty("baseColor")[3]
            .GetSingle();
    }

    /// <summary>The quad's streams with the given positions.</summary>
    private static NifTestGeometryStreams Streams(float[] vertices)
    {
        return new NifTestGeometryStreams { Vertices = vertices, Normals = QuadNormals };
    }

    /// <summary>0 NiNode "Root" [1], 1 NiTriShape "Shape", 2 NiTriShapeData with the given positions.</summary>
    private static byte[] Quad(float[] vertices)
    {
        return NifModelTestSupport.SingleTriShape(Streams(vertices), QuadTriangles);
    }

    /// <summary>0 NiNode "Root" [child], 1 NiTriShape "Shape" (data 2), 2 NiTriShapeData.</summary>
    private static byte[] ChildFixture(int child)
    {
        var builder = new NifTestFileBuilder(false, 34);
        NifModelTestSupport.AddNode(builder, builder.AddString("Root"), [child]);
        NifModelTestSupport.AddTriShape(builder, builder.AddString("Shape"), 2);
        NifModelTestSupport.AddTriShapeData(builder, Streams(QuadVertices), QuadTriangles);
        return builder.Build();
    }

    /// <summary>
    ///     A BSShaderPPLightingProperty over a one-path texture set on the quad. The flags are the ones the GLB writer can
    ///     lower: SF1 bit 31 (depth test) and bit 3 (vertex alpha), SF2 bit 0 (depth write) and bit 5 (vertex colors), so
    ///     the reader's assumed vertex-color use is AmbientDiffuse with alpha for opacity; with vertex colors ignored the GLB
    ///     plan would carry a blocking vertex-color limitation and resolution would fail for a reason unrelated to pixels.
    /// </summary>
    private static byte[] TexturedFixture(string path)
    {
        return NifMaterialFixtures.Shape(34, [NifMaterialFixtures.FirstExtraBlock], builder =>
        {
            builder.AddBlock("BSShaderPPLightingProperty", w => NifTestBlockLayouts.PerPixelLightingProperty(w, 34,
                0x80000008u, 0x21u, 1f, 3, NifMaterialFixtures.FirstExtraBlock + 1));
            builder.AddBlock("BSShaderTextureSet", w => NifTestBlockLayouts.TextureSet(w, path));
        });
    }

    /// <summary>The material fixture: an NiMaterialProperty with the given alpha on the shape.</summary>
    private static byte[] MaterialFixture(float alpha)
    {
        return NifMaterialFixtures.Shape(34, [NifMaterialFixtures.FirstExtraBlock],
            builder => NifMaterialFixtures.AddMaterial(builder, alpha: alpha));
    }

    /// <summary>Writes a fixture into the temporary directory and returns its path.</summary>
    private string WriteNif(string name, byte[] bytes)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Runs <c>mesh dump</c> to a memory stream with FNV units and the ample memory gate.</summary>
    private static async Task<(int Exit, byte[] Json, string Error)> DumpAsync(string path, bool includeNative = false)
    {
        var json = new MemoryStream();
        var error = new StringWriter();
        var exit = await MeshDebugCommands.ExecuteDumpAsync(path, null, "fnv", includeNative, json, error,
            TestContext.Current.CancellationToken, AmpleMemory());
        return (exit, json.ToArray(), error.ToString());
    }

    /// <summary>Runs <c>mesh dump --output</c> with FNV units and the ample memory gate; the stream stays empty.</summary>
    private static async Task<(int Exit, byte[] Json, string Error)> DumpToFileAsync(string path, string output,
        bool overwrite)
    {
        var json = new MemoryStream();
        var error = new StringWriter();
        var exit = await MeshDebugCommands.ExecuteDumpAsync(path, null, "fnv", false, json, error,
            TestContext.Current.CancellationToken, AmpleMemory(), outputPath: output, overwrite: overwrite);
        return (exit, json.ToArray(), error.ToString());
    }

    /// <summary>Runs <c>mesh validate</c> as text with FNV units and the ample memory gate.</summary>
    private static async Task<(int Exit, string Text)> ValidateAsync(string path)
    {
        var text = new StringWriter();
        var exit = await MeshDebugCommands.ExecuteValidateAsync(path, null, "fnv", false, text, new MemoryStream(),
            new StringWriter(), TestContext.Current.CancellationToken, AmpleMemory());
        return (exit, text.ToString());
    }

    /// <summary>Runs <c>mesh package</c> with FNV units and the ample memory gate; no tool settings exist to consult.</summary>
    private static async Task<(int Exit, string Text, string Error)> PackageAsync(string input, string output,
        bool overwrite)
    {
        var text = new StringWriter();
        var error = new StringWriter();
        var exit = await MeshDebugCommands.ExecutePackageAsync(input, output, null, "fnv", 1, overwrite, text, error,
            TestContext.Current.CancellationToken, AmpleMemory());
        return (exit, text.ToString(), error.ToString());
    }
}
