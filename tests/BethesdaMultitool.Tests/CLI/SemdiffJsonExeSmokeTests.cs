using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.CLI;

/// <summary>
///     Runs <c>esm semdiff --format json</c> through the SHIPPED exe (see <see cref="CliExeRunner" />) on two
///     synthetic FalloutNV.esm files. The trimmed exe disables reflection-based JSON, which the test host
///     allows, so only a run under the exe's own runtimeconfig can show that the document is written without
///     it; the run also proves stdout carries nothing but that one document (no banner, no warnings).
/// </summary>
public sealed class SemdiffJsonExeSmokeTests
{
    private const uint ActorFormId = 0x000E739E;
    private const uint ReusedFormId = 0x01011E59;
    private const uint ScriptFormId = 0x00005000;

    private const string SourceA = "scn SmokeScript\r\n\tset sText to \"a\\b\"\r\nEnd";
    private const string SourceB = "scn SmokeScript\r\n\tset sText to \"c\\d\"\r\nEnd";

    [Fact]
    public async Task SemdiffJson_UnderShippedRuntimeConfig_IsValidJson()
    {
        CliExeRunner.AssertShippedJsonReflectionIsDisabled();
        using var directory = CliExeRunner.CreateTempDirectory();
        var fileA = WritePlugin(directory, "a", BuildPlugin(false));
        var fileB = WritePlugin(directory, "b", BuildPlugin(true));

        var result = await CliExeRunner.RunAsync(
            ["--plain", "esm", "semdiff", fileA, fileB, "--format", "json"],
            TestContext.Current.CancellationToken,
            workingDirectory: directory.Path);

        Assert.True(result.ExitCode == 0, result.Describe());
        Assert.DoesNotContain("Reflection-based serialization", result.StandardError, StringComparison.Ordinal);
        Assert.StartsWith("{", result.StandardOutput.TrimStart(), StringComparison.Ordinal);

        // JsonDocument.Parse rejects leading text and a second top-level value, so a successful parse is the
        // "exactly one JSON object on stdout" assertion.
        using var json = JsonDocument.Parse(result.StandardOutput);
        var root = json.RootElement;
        Assert.Equal("bethesda-multitool/esm-semdiff", root.GetProperty("schema").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("toolVersion").GetString()));
        Assert.Equal("FalloutNewVegas", root.GetProperty("files").GetProperty("a").GetProperty("game").GetString());
        Assert.Equal(3, root.GetProperty("summary").GetProperty("withDifferences").GetInt32());

        var records = root.GetProperty("records").EnumerateArray()
            .ToDictionary(r => r.GetProperty("formId").GetString()!);
        Assert.Equal(3, records.Count);

        var script = records[$"0x{ScriptFormId:X8}"];
        Assert.Equal("different", script.GetProperty("status").GetString());
        var source = Assert.Single(Assert.Single(script.GetProperty("subrecords").EnumerateArray())
            .GetProperty("fields").EnumerateArray());
        Assert.Equal(SourceA, source.GetProperty("a").GetString());
        Assert.Equal(SourceB, source.GetProperty("b").GetString());

        var actor = records[$"0x{ActorFormId:X8}"];
        var added = Assert.Single(actor.GetProperty("header").GetProperty("flagsAdded").EnumerateArray());
        Assert.Equal("Initially Disabled", added.GetProperty("name").GetString());

        var reused = records[$"0x{ReusedFormId:X8}"];
        Assert.Equal("signatureMismatch", reused.GetProperty("status").GetString());
        Assert.Equal("refused", reused.GetProperty("comparison").GetString());
        Assert.Equal("QUST", reused.GetProperty("a").GetProperty("signature").GetString());
        Assert.Equal("REFR", reused.GetProperty("b").GetProperty("signature").GetString());
        Assert.Equal(0, reused.GetProperty("subrecords").GetArrayLength());
    }

    [Fact]
    public async Task SemdiffTreeFormat_IsAParseError()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var fileA = WritePlugin(directory, "a", BuildPlugin(false));
        var fileB = WritePlugin(directory, "b", BuildPlugin(true));

        var result = await CliExeRunner.RunAsync(
            ["--plain", "esm", "semdiff", fileA, fileB, "--format", "tree"],
            TestContext.Current.CancellationToken,
            workingDirectory: directory.Path);

        // 'tree' was never implemented and used to print the table with exit 0.
        Assert.True(result.ExitCode != 0, result.Describe());
        Assert.Contains("tree", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Semantic ESM Diff", result.StandardOutput, StringComparison.Ordinal);
    }

    private static string WritePlugin(CliExeRunner.TempDirectory directory, string build,
        EsmTestFileBuilder builder)
    {
        var buildDirectory = Path.Combine(directory.Path, build);
        Directory.CreateDirectory(buildDirectory);
        var path = Path.Combine(buildDirectory, "FalloutNV.esm");
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    /// <summary>
    ///     One side of the pair. File A: ACHR 0x000E739E with flags 0x400 (Persistent), QUST 0x01011E59
    ///     <c>NVDLC03X13VR</c>, SCPT 0x00005000 with <see cref="SourceA" />. File B: the ACHR with 0xC00
    ///     (Initially Disabled added), a REFR reusing 0x01011E59, and the SCPT with <see cref="SourceB" />.
    /// </summary>
    private static EsmTestFileBuilder BuildPlugin(bool retail)
    {
        var name = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(name, 0x00123456);
        var position = new byte[24];

        var questData = new byte[8];
        questData[0] = 0x01;
        questData[1] = 0x3F;
        BinaryPrimitives.WriteSingleLittleEndian(questData.AsSpan(4), 5.0f);

        var builder = new EsmTestFileBuilder()
            .AddTopLevelGrup("SCPT",
                EsmTestFileBuilder.BuildRecord("SCPT", ScriptFormId, 0,
                    ("EDID", NullTerminated("SmokeScript")),
                    ("SCTX", Encoding.ASCII.GetBytes(retail ? SourceB : SourceA))))
            .AddTopLevelGrup("ACHR",
                EsmTestFileBuilder.BuildRecord("ACHR", ActorFormId, retail ? 0x00000C00u : 0x00000400u,
                    ("NAME", name), ("DATA", position)));

        return retail
            ? builder.AddTopLevelGrup("REFR",
                EsmTestFileBuilder.BuildRecord("REFR", ReusedFormId, 0, ("NAME", name), ("DATA", position)))
            : builder.AddTopLevelGrup("QUST",
                EsmTestFileBuilder.BuildRecord("QUST", ReusedFormId, 0,
                    ("EDID", NullTerminated("NVDLC03X13VR")), ("DATA", questData)));
    }

    private static byte[] NullTerminated(string value)
    {
        return Encoding.ASCII.GetBytes(value + "\0");
    }
}
