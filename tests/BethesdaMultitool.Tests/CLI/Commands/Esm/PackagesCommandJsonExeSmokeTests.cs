using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.CLI.Commands.Esm;

/// <summary>
///     Runs <c>esm packages</c> through the SHIPPED exe (see <see cref="CliExeRunner" />) on a synthetic
///     FalloutNV.esm. The JSON mode used to throw <c>Reflection-based serialization has been disabled</c> in
///     the trimmed exe while passing in-process, and it also mixed progress text into stdout; only a run
///     under the exe's own runtimeconfig can see either failure.
/// </summary>
public sealed class PackagesCommandJsonExeSmokeTests
{
    internal const uint SmokeTravelPackageFormId = 0x000FE923;
    internal const uint SmokeBracketPackageFormId = 0x000FE924;
    internal const uint SmokeStatFormId = 0x00001234;

    [Fact]
    public async Task JsonFormat_RunsUnderShippedRuntimeConfig_AndStdoutIsOneJsonObject()
    {
        CliExeRunner.AssertShippedJsonReflectionIsDisabled();
        using var directory = CliExeRunner.CreateTempDirectory();
        var plugin = CliExeRunner.WriteFalloutNvEsm(directory, BuildSmokePlugin());

        var result = await CliExeRunner.RunAsync(
            ["--plain", "esm", "packages", plugin, "-l", "1", "-f", "json"],
            TestContext.Current.CancellationToken,
            workingDirectory: directory.Path);

        Assert.True(result.ExitCode == 0, result.Describe());
        Assert.DoesNotContain("Reflection-based serialization", result.StandardError, StringComparison.Ordinal);
        Assert.StartsWith("{", result.StandardOutput.TrimStart(), StringComparison.Ordinal);

        // JsonDocument.Parse rejects leading progress text and any second top-level value, so a successful
        // parse is the "exactly one JSON object on stdout" assertion.
        using var json = JsonDocument.Parse(result.StandardOutput);
        var root = json.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal("bethesda-multitool/esm-packages", root.GetProperty("schema").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("toolVersion").GetString()));
        Assert.Equal(Path.GetFullPath(plugin), root.GetProperty("source").GetString());
        Assert.Equal("FalloutNewVegas", root.GetProperty("game").GetString());
        Assert.Equal(2, root.GetProperty("totalPackages").GetInt32());
        Assert.Equal(2, root.GetProperty("matchedPackages").GetInt32());
        Assert.Equal(1, root.GetProperty("shownPackages").GetInt32());
        Assert.True(root.GetProperty("truncated").GetBoolean());

        var package = Assert.Single(root.GetProperty("packages").EnumerateArray());
        Assert.Equal("0x000FE923", package.GetProperty("formId").GetString());
        Assert.Equal("SmokeTravel", package.GetProperty("editorId").GetString());
        Assert.Equal("Travel", package.GetProperty("type").GetString());
        Assert.Equal(8, package.GetProperty("schedule").GetProperty("time").GetInt32());
        Assert.Equal(4, package.GetProperty("schedule").GetProperty("durationHours").GetInt32());

        var location = package.GetProperty("location");
        Assert.Equal(4, location.GetProperty("type").GetInt32());
        Assert.Equal("0x00001234", location.GetProperty("union").GetString());
        Assert.Equal("SmokeStat", location.GetProperty("unionEditorId").GetString());
        Assert.Equal(256, location.GetProperty("radius").GetInt32());

        var target = package.GetProperty("target");
        Assert.Equal(2, target.GetProperty("type").GetInt32());
        Assert.Equal("0x00000012", target.GetProperty("formIdOrType").GetString());
        Assert.Equal(750, target.GetProperty("countDistance").GetInt32());
        Assert.Equal(JsonValueKind.Null, target.GetProperty("acquireRadius").ValueKind);
        Assert.Equal("0x7FC00000", target.GetProperty("acquireRadiusRawBits").GetString());

        var condition = Assert.Single(package.GetProperty("conditionsRaw").EnumerateArray());
        Assert.Equal(0x48, condition.GetProperty("functionIndex").GetInt32());
        Assert.Equal("0x00001234", condition.GetProperty("parameter1").GetString());
        Assert.Equal("0x3F800000", condition.GetProperty("comparisonRawBits").GetString());
    }

    [Fact]
    public async Task JsonFormat_MissingInput_ExitsOneWithNothingOnStdout()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var missing = Path.Combine(directory.Path, "Missing.esm");

        var result = await CliExeRunner.RunAsync(
            ["--plain", "esm", "packages", missing, "-f", "json"],
            TestContext.Current.CancellationToken,
            workingDirectory: directory.Path);

        Assert.True(result.ExitCode == 1, result.Describe());
        Assert.True(result.StandardOutput.Length == 0, result.Describe());
        Assert.Contains("File not found", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownFormat_ExitsTwo()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var missing = Path.Combine(directory.Path, "Missing.esm");

        var result = await CliExeRunner.RunAsync(
            ["--plain", "esm", "packages", missing, "-f", "xml"],
            TestContext.Current.CancellationToken,
            workingDirectory: directory.Path);

        Assert.True(result.ExitCode == 2, result.Describe());
        Assert.Contains("--format", result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Text mode renders data through a Spectre table, whose cells are parsed as markup. An EditorID of
    ///     <c>[Q]</c> used to abort the command with "Could not find color or style 'Q'"; escaped, it prints
    ///     literally.
    /// </summary>
    [Fact]
    public async Task TextFormat_BracketedEditorId_RendersLiterally()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var plugin = CliExeRunner.WriteFalloutNvEsm(directory, BuildSmokePlugin());

        var result = await CliExeRunner.RunAsync(
            ["--plain", "esm", "packages", plugin, "-t", "Find"],
            TestContext.Current.CancellationToken,
            workingDirectory: directory.Path);

        Assert.True(result.ExitCode == 0, result.Describe());
        Assert.Contains("Total packages: 2", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[Q]", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A little-endian FalloutNV plugin with one STAT and two PACKs, shared by the esm exe smoke tests:
    ///     <list type="bullet">
    ///         <item>
    ///             PACK 0x000FE923 <c>SmokeTravel</c>: Travel, 8 AM for 4 hours, near Object ID 0x00001234
    ///             (r=256), target Object Type 18 with count 750 and a NaN acquire radius (bits 0x7FC00000),
    ///             one GetIsID(0x00001234) == 1 condition.
    ///         </item>
    ///         <item>PACK 0x000FE924 <c>[Q]</c>: Find, no schedule/location/target — a markup-hostile EditorID.</item>
    ///         <item>STAT 0x00001234 <c>SmokeStat</c>: the named location target.</item>
    ///     </list>
    /// </summary>
    internal static EsmTestFileBuilder BuildSmokePlugin()
    {
        var pkdt = new byte[12];
        pkdt[4] = 6; // Travel

        var psdt = new byte[8];
        psdt[0] = 0xFF; // month: any
        psdt[1] = 0xFF; // day of week: any
        psdt[2] = 0; // date: any
        psdt[3] = 8; // 8 AM
        BinaryPrimitives.WriteInt32LittleEndian(psdt.AsSpan(4), 4);

        var pldt = new byte[12];
        pldt[0] = 4; // Object ID
        BinaryPrimitives.WriteUInt32LittleEndian(pldt.AsSpan(4), SmokeStatFormId);
        BinaryPrimitives.WriteInt32LittleEndian(pldt.AsSpan(8), 256);

        var ptdt = new byte[16];
        ptdt[0] = 2; // Object Type
        BinaryPrimitives.WriteUInt32LittleEndian(ptdt.AsSpan(4), 18);
        BinaryPrimitives.WriteInt32LittleEndian(ptdt.AsSpan(8), 750);
        BinaryPrimitives.WriteUInt32LittleEndian(ptdt.AsSpan(12), 0x7FC00000);

        var ctda = new byte[28];
        ctda[0] = 0x00; // ==, AND
        BinaryPrimitives.WriteSingleLittleEndian(ctda.AsSpan(4), 1.0f);
        BinaryPrimitives.WriteUInt16LittleEndian(ctda.AsSpan(8), 0x48); // GetIsID
        BinaryPrimitives.WriteUInt32LittleEndian(ctda.AsSpan(12), SmokeStatFormId);

        var findPkdt = new byte[12];
        findPkdt[4] = 0; // Find

        return new EsmTestFileBuilder()
            .AddTopLevelGrup("STAT",
                EsmTestFileBuilder.BuildRecord("STAT", SmokeStatFormId, 0, ("EDID", NullTerminated("SmokeStat"))))
            .AddTopLevelGrup("PACK",
                EsmTestFileBuilder.BuildRecord("PACK", SmokeTravelPackageFormId, 0,
                    ("EDID", NullTerminated("SmokeTravel")),
                    ("PKDT", pkdt),
                    ("PSDT", psdt),
                    ("PLDT", pldt),
                    ("PTDT", ptdt),
                    ("CTDA", ctda)),
                EsmTestFileBuilder.BuildRecord("PACK", SmokeBracketPackageFormId, 0,
                    ("EDID", NullTerminated("[Q]")),
                    ("PKDT", findPkdt)));
    }

    private static byte[] NullTerminated(string value)
    {
        return Encoding.ASCII.GetBytes(value + "\0");
    }
}
