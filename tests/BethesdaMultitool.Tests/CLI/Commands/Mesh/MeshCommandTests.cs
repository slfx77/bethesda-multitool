using System.CommandLine;
using System.Text;
using System.Text.Json;
using BethesdaMultitool.CLI.Commands.Mesh;
using BethesdaMultitool.Tests.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models.Export;
using Xunit;

namespace BethesdaMultitool.Tests.CLI.Commands.Mesh;

/// <summary>
///     The <c>mesh</c> shell over the Shared operations (plan section 6, slice 4), on synthetic NIF files in a private
///     temporary directory. Only the GLB writer is exercised: a <c>.blend</c> conversion would launch an installed
///     Blender, which the default suite must never do. The memory gate is injected with an ample sample: the real
///     process gate admits against the machine's free memory, which would make these results depend on the host.
/// </summary>
public sealed class MeshCommandTests : IDisposable
{
    private static readonly float[] QuadVertices = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 1f, 1f, 0.5f];
    private static readonly float[] QuadNormals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f];
    private static readonly ushort[] QuadTriangles = [0, 1, 2, 1, 3, 2];

    private readonly string _directory = Directory.CreateTempSubdirectory("bmt-mesh-command-").FullName;

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

    /// <summary>Info on a NIF succeeds and names the reader's evidence; the same bytes truncated to garbage do not.</summary>
    [Fact]
    public async Task InfoOnANifSucceedsAndItsControlFails()
    {
        var model = WriteNif("model.nif");
        var (exit, text, _) = await InfoAsync(model, null, json: false);
        Assert.Equal(0, exit);
        Assert.Contains("NIF 20.2.0.7, user 11, BS 34, little-endian", text, StringComparison.Ordinal);

        var garbage = Path.Combine(_directory, "notes.nif");
        await File.WriteAllTextAsync(garbage, "not a model", TestContext.Current.CancellationToken);
        var (garbageExit, _, _) = await InfoAsync(garbage, null, json: false);
        Assert.NotEqual(0, garbageExit);
    }

    /// <summary>The JSON form is valid JSON carrying the same source format.</summary>
    [Fact]
    public async Task InfoJsonIsWellFormed()
    {
        var model = WriteNif("model.nif");
        var output = new MemoryStream();
        var exit = await MeshCommand.ExecuteInfoAsync(model, null, "fnv", true, false, new StringWriter(), output,
            new StringWriter(), TestContext.Current.CancellationToken, AmpleMemory());
        Assert.Equal(0, exit);
        using var document = JsonDocument.Parse(output.ToArray());
        Assert.Contains("bmt.nif", Encoding.UTF8.GetString(output.ToArray()), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }

    /// <summary>Convert writes a GLB, skips it on a second run, and replaces it with --overwrite.</summary>
    [Fact]
    public async Task ConvertWritesThenSkipsThenOverwrites()
    {
        var model = WriteNif("model.nif");
        var outputDirectory = Path.Combine(_directory, "out");
        var glb = Path.Combine(outputDirectory, "model.glb");

        var first = await ConvertAsync(model, outputDirectory, overwrite: false);
        Assert.Equal(0, first.Exit);
        Assert.True(File.Exists(glb), first.Text);
        var bytes = await File.ReadAllBytesAsync(glb, TestContext.Current.CancellationToken);
        Assert.Equal("glTF"u8.ToArray(), bytes[..4]);

        File.SetLastWriteTimeUtc(glb, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var second = await ConvertAsync(model, outputDirectory, overwrite: false);
        Assert.Equal(0, second.Exit);
        Assert.Equal(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(glb));

        var third = await ConvertAsync(model, outputDirectory, overwrite: true);
        Assert.Equal(0, third.Exit);
        Assert.NotEqual(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(glb));
    }

    /// <summary>A directory batch converts its model and does not fail on a neighboring non-model file.</summary>
    [Fact]
    public async Task DirectoryBatchIgnoresANonModelNeighbor()
    {
        var input = Path.Combine(_directory, "in");
        Directory.CreateDirectory(input);
        await File.WriteAllBytesAsync(Path.Combine(input, "model.nif"), Fixture(), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(input, "readme.txt"), "text", TestContext.Current.CancellationToken);
        var outputDirectory = Path.Combine(_directory, "out");

        var result = await ConvertAsync(input, outputDirectory, overwrite: false);

        Assert.Equal(0, result.Exit);
        Assert.True(File.Exists(Path.Combine(outputDirectory, "model.glb")), result.Text);
        Assert.False(File.Exists(Path.Combine(outputDirectory, "readme.glb")));
    }

    /// <summary>Usage mistakes exit 1 with a message: an archive without --entry, --entry on a loose file, a missing file.</summary>
    [Fact]
    public async Task UsageMistakesExitOneWithAMessage()
    {
        var archive = Path.Combine(_directory, "Meshes.bsa");
        await File.WriteAllBytesAsync(archive, [], TestContext.Current.CancellationToken);
        var (archiveExit, _, archiveError) = await InfoAsync(archive, null, json: false);
        Assert.Equal(1, archiveExit);
        Assert.Contains("--entry", archiveError, StringComparison.Ordinal);

        var model = WriteNif("model.nif");
        var (looseExit, _, looseError) = await InfoAsync(model, "meshes/model.nif", json: false);
        Assert.Equal(1, looseExit);
        Assert.Contains("--entry", looseError, StringComparison.Ordinal);

        var (missingExit, _, _) = await InfoAsync(Path.Combine(_directory, "missing.nif"), null, json: false);
        Assert.Equal(1, missingExit);
    }

    /// <summary>
    ///     An explicit --data-root that does not exist exits 1 naming it, for both subcommands; control: the same model
    ///     without the option succeeds (no data root is inferred from the private temporary directory).
    /// </summary>
    [Fact]
    public async Task MissingDataRoot_ExitsOneWithAMessage()
    {
        var model = WriteNif("model.nif");
        var missing = Path.Combine(_directory, "no-such-data");
        var error = new StringWriter();

        var info = await MeshCommand.ExecuteInfoAsync(model, null, "fnv", false, false, new StringWriter(),
            new MemoryStream(), error, TestContext.Current.CancellationToken, AmpleMemory(), [missing]);
        var convert = await MeshCommand.ExecuteConvertAsync(model, Path.Combine(_directory, "out"), null, "fnv", "glb",
            1, false, new StringWriter(), error, TestContext.Current.CancellationToken, AmpleMemory(), [missing]);
        var (control, _, _) = await InfoAsync(model, null, json: false);

        Assert.Equal(1, info);
        Assert.Equal(1, convert);
        Assert.Contains("no-such-data", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, control);
    }

    /// <summary>
    ///     A parse error inside <c>mesh</c> (a missing argument, a format outside glb/blend) exits 2, the model shells'
    ///     usage code. Controls: a valid parse keeps its own code, and a parse error in another command keeps 1.
    /// </summary>
    [Fact]
    public void UsageErrors_InsideMesh_ExitTwo()
    {
        var root = new RootCommand();
        root.Subcommands.Add(MeshCommand.Create());
        var other = new Command("other");
        other.Arguments.Add(new Argument<string>("value"));
        root.Subcommands.Add(other);

        Assert.Equal(2, MeshCommand.MapUsageExitCode(root.Parse(["mesh", "convert"]), 1));
        Assert.Equal(2, MeshCommand.MapUsageExitCode(root.Parse(["mesh", "convert", "a.nif", "out", "--format", "fbx"]), 1));
        Assert.Equal(0, MeshCommand.MapUsageExitCode(root.Parse(["mesh", "info", "a.nif"]), 0));
        Assert.Equal(1, MeshCommand.MapUsageExitCode(root.Parse(["other"]), 1));
    }

    /// <summary>A gate whose every sample reports 64 GiB available, so admission never waits on the host.</summary>
    private static ModelMemoryGate AmpleMemory()
    {
        return new ModelMemoryGate(() => new ModelMemorySample(64L * 1024 * 1024 * 1024));
    }

    private static byte[] Fixture()
    {
        return NifModelTestSupport.SingleTriShape(
            new NifTestGeometryStreams { Vertices = QuadVertices, Normals = QuadNormals }, QuadTriangles);
    }

    private string WriteNif(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, Fixture());
        return path;
    }

    private static async Task<(int Exit, string Text, string Error)> InfoAsync(string input, string? entry, bool json)
    {
        var text = new StringWriter();
        var error = new StringWriter();
        var exit = await MeshCommand.ExecuteInfoAsync(input, entry, "fnv", json, false, text, new MemoryStream(), error,
            TestContext.Current.CancellationToken, AmpleMemory());
        return (exit, text.ToString(), error.ToString());
    }

    private static async Task<(int Exit, string Text)> ConvertAsync(string input, string output, bool overwrite)
    {
        var text = new StringWriter();
        var error = new StringWriter();
        var exit = await MeshCommand.ExecuteConvertAsync(input, output, null, "fnv", "glb", 1, overwrite, text, error,
            TestContext.Current.CancellationToken, AmpleMemory());
        return (exit, text + Environment.NewLine + error);
    }
}
