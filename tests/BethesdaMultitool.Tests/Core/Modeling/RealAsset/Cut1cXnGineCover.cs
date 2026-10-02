using System.Globalization;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Xngine;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Reads a cut-1c cover row through the XnGine <c>.3D</c> reader the way a <c>mesh</c> command reaches it (cut-1c
///     slice 5): an archive row through the production asset session and browse source (so the container facts answer
///     the game, the object id and the stored bytes), a loose row through a folder source with the shell's install
///     walk-up as <c>bmt.classic-game</c>. The payload is first verified against the manifest by
///     <see cref="Cut1cFixtureResolver" />; an ARCH3D duplicate that path lookup cannot reach (last-wins, plan D6, until
///     slice 8 names duplicates) is read from its verified bytes under <c>bmt.game</c> instead, which the result says.
/// </summary>
internal static class Cut1cXnGineCover
{
    private const string SamplePrefix = "Sample/";

    /// <summary>One read cover row.</summary>
    /// <param name="File">The manifest row.</param>
    /// <param name="Bytes">The verified payload bytes.</param>
    /// <param name="Result">The reader's document and coverage (structure already validated).</param>
    /// <param name="Route">How the row was read: <c>container</c>, <c>loose</c> or <c>shadowed-duplicate</c>.</param>
    /// <param name="PlaneHeaderLength">The manifest game's plane-header size (10 for Battlespire, else 8).</param>
    /// <param name="LegacyObjectId">The object id the legacy <c>classic mesh</c> route parses the record with.</param>
    /// <param name="LegacyLayout">The layout the legacy route parses the record with.</param>
    internal sealed record Read(
        Cut1cCoverFile File,
        byte[] Bytes,
        ModelReadResult Result,
        string Route,
        int PlaneHeaderLength,
        uint LegacyObjectId,
        XnGineMeshLayout LegacyLayout);

    /// <summary>True for a row the <c>.3D</c> reader reads: a mesh tag, not a decline control, not a <c>.3DC</c>.</summary>
    public static bool IsStaticMesh(Cut1cCoverFile file)
    {
        return !file.IsDeclinedControl && file.Tag is "v2.5" or "v2.6" or "v2.7" &&
               !file.Entry.EndsWith(".3DC", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The static <c>.3D</c> rows of the manifest as (row name, payload SHA-256).</summary>
    public static TheoryData<string, string> StaticRows()
    {
        var rows = new TheoryData<string, string>();
        foreach (var file in Cut1cCoverManifest.Files.Where(IsStaticMesh))
        {
            rows.Add(file.Name, file.Sha256);
        }

        return rows;
    }

    /// <summary>
    ///     The per-row facts the slice-5 Bucket-B tests pin on a static <c>.3D</c> row (slice-5 review receipt
    ///     <c>TestOutput/cut1c-20260928/slice56/receipts/cover_pins.json</c>, measured by <c>measure_cover_pins.py</c>
    ///     through the independent gate-1c oracle). The unfold pins discriminate plan decision D2 in both directions:
    ///     ARCH3D 409 is the row where the reader's unfold changes stored values (44), NECRISLE.ROB NCGATE01 the row
    ///     where only the legacy route unfolds (planes 9, 26 and 61); 451 takes the unfold but changes no value.
    /// </summary>
    /// <param name="UnfoldedValues">The stored corner 0-2 values the reader's packed-UV unfold changes (<c>bmt.xngine.uv-rule</c> <c>unfoldedValues</c>).</param>
    /// <param name="LegacyOnlyUnfoldedPlanes">The drawn planes whose UVs only the legacy route unfolds (the hop A3x UV exclusions), in plane order.</param>
    /// <param name="RepeatedPointPlanes">The drawn planes whose corners repeat a source point (the faces Blender omits), in plane order.</param>
    internal sealed record CoverPins(int UnfoldedValues, int[] LegacyOnlyUnfoldedPlanes, int[] RepeatedPointPlanes);

    /// <summary>The pins of every static <c>.3D</c> row, by manifest row name (see <see cref="CoverPins" />).</summary>
    public static IReadOnlyDictionary<string, CoverPins> Pins { get; } =
        new Dictionary<string, CoverPins>(StringComparer.Ordinal)
        {
            ["Daggerfall 44005"] = new(0, [], []),
            ["Daggerfall 451"] = new(0, [], []),
            ["Daggerfall 41509"] = new(0, [], []),
            ["Redguard CRAK0001.3D"] = new(0, [], []),
            ["ISLAND.ROB GR_COMP"] = new(0, [], []),
            ["Battlespire ARMOR.3D"] = new(0, [], []),
            ["3D.BS6 BARSTEP1.3D"] = new(0, [], []),
            ["3D.BSA HUTVANE.3D"] = new(0, [], []),
            ["MENU.ROB MENUA001"] = new(0, [], []),
            ["ARCH3D 5090 (index 5007)"] = new(0, [], []),
            ["ARCH3D 5090 (index 8903)"] = new(0, [], []),
            ["ARCH3D 5090 (index 9787)"] = new(0, [], []),
            ["ARCH3D 5090 (index 9824)"] = new(0, [], []),
            ["ARCH3D 5090 (index 9826)"] = new(0, [], []),
            ["ARCH3D 5090 (index 10238)"] = new(0, [], []),
            ["3D.BS6 ESPEAR.3D"] = new(0, [], []),
            ["ISLAND.ROB HBBLD01"] = new(0, [], []),
            ["3D.BSA CAT02.3D (folded fan)"] = new(0, [], []),
            ["3D.BSA BARSTEP1.3D namesake"] = new(0, [], []),
            ["ARCH3D 409 (unfold changes values)"] = new(44, [], []),
            ["3D.BSA PUZSPRT1.3D (repeated point)"] = new(0, [], [72]),
            ["NECRISLE.ROB NCGATE01"] = new(0, [9, 26, 61], [30, 49])
        };

    /// <summary>The pins of a static row; fails when the row has none, so a new cover row cannot go unpinned.</summary>
    /// <exception cref="InvalidOperationException">The row has no pins.</exception>
    public static CoverPins RequirePins(Cut1cCoverFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return Pins.TryGetValue(file.Name, out var pins)
            ? pins
            : throw new InvalidOperationException(
                $"{file}: no slice-5 pins; measure the row with measure_cover_pins.py and add them to Cut1cXnGineCover.Pins.");
    }

    /// <summary>
    ///     Reads one row (see the type summary). The calling test runs its own Bucket-B guard first (so the file that
    ///     calls the guard is the one that carries the Bucket-B trait). Skips when the primary container is not on this
    ///     machine; fails when it is present but does not reproduce the pins. Every source opened is added to
    ///     <paramref name="owned" />.
    /// </summary>
    public static async Task<Read> ReadAsync(Cut1cCoverFile file, ModelNativeDetail detail,
        List<IAsyncDisposable> owned)
    {
        var fixture = Cut1cFixtureResolver.TryResolveContainer(file, out var reason);
        Assert.SkipWhen(fixture is null && reason.StartsWith(Cut1cFixtureResolver.AbsentPrefix, StringComparison.Ordinal),
            $"{file}: {reason}. " + RealAssetPaths.SkipMessage("the cut-1c cover corpus"));
        Assert.True(fixture is not null, $"{file}: {reason}");
        var location = file.Source.StartsWith(SamplePrefix, StringComparison.Ordinal)
            ? RealAssetPaths.SampleFile(file.Source[SamplePrefix.Length..])
            : null;
        Assert.True(location is not null, $"{file}: the source {file.Source} resolved bytes but no path.");

        IAssetSource source;
        string path;
        IReadOnlyDictionary<string, string> options = new Dictionary<string, string>(StringComparer.Ordinal);
        var route = "container";
        if (string.Equals(file.Container, Cut1cCoverFile.LooseContainer, StringComparison.Ordinal))
        {
            source = new FolderAssetSource(Path.GetDirectoryName(location)!);
            path = Path.GetFileName(location);
            options = ShellOptions(location);
            route = "loose";
        }
        else
        {
            source = new BethesdaBrowseSource(
                AssetBrowseSession.TryOpenGameArchive(location) ?? AssetBrowseSession.OpenArchive(location));
            path = string.Equals(file.Container, Cut1cCoverFile.RobContainer, StringComparison.Ordinal)
                ? file.Entry + ".3D"
                : file.Entry;
            var facts = ClassicContainerFacts.TryQuery(source, new AssetReference(source.Id, path));
            if (facts?.EntryIndex != file.Index)
            {
                owned.Add(source);
                var memory = new InMemoryAssetSource("cut1c-shadowed");
                memory.Add(file.Entry, fixture.Bytes);
                source = memory;
                path = file.Entry;
                options = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [BethesdaModelRegistration.GameOption] = file.Game.ToLowerInvariant()
                };
                route = "shadowed-duplicate";
            }
        }

        owned.Add(source);
        var entry = await ClassicContainerFixture.FindEntryAsync(source, path);
        var item = new ModelSourceItem(source, entry);
        ModelReadResult result;
        await using (var input = await item.OpenReadAsync(TestContext.Current.CancellationToken))
        {
            var context = new ModelReadContext(item, input, BethesdaModelRegistration.CreateCache(), detail, options);
            result = new XnGineModelReader().Read(item, context, TestContext.Current.CancellationToken);
        }

        SceneValidation.ValidateStructure(result.Document, TestContext.Current.CancellationToken);
        var battlespire = string.Equals(file.Game, "Battlespire", StringComparison.Ordinal);
        var legacyId = file.Container switch
        {
            Cut1cCoverFile.Arch3dContainer => uint.Parse(file.Entry, CultureInfo.InvariantCulture),
            Cut1cCoverFile.LooseContainer => 0u,
            _ => (uint)file.Index!.Value
        };
        return new Read(file, fixture.Bytes, result, route, battlespire ? 10 : 8, legacyId,
            battlespire ? XnGineMeshLayout.Battlespire : XnGineMeshLayout.Daggerfall);
    }

    /// <summary>
    ///     Runs the A6 driver (<c>tools/scripts/gate1c/xngine_probe_json.py</c>) over a payload and returns its JSON, or
    ///     skips when no Python with numpy is on the PATH.
    /// </summary>
    public static JsonObject Probe(byte[] payload, int planeHeaderLength, string label)
    {
        Assert.SkipWhen(Cut1cOracleProcess.Interpreter is null,
            "hop A6 needs a Python interpreter with numpy on the PATH (python, py -3 or python3).");
        var directory = Directory.CreateTempSubdirectory("bmt-cut1c-a6-").FullName;
        try
        {
            var input = Path.Combine(directory, "record.bin");
            var output = Path.Combine(directory, "probe.json");
            File.WriteAllBytes(input, payload);
            var (exitCode, error) = Cut1cOracleProcess.Run("xngine_probe_json.py",
                [input, planeHeaderLength.ToString(CultureInfo.InvariantCulture), output],
                TestContext.Current.CancellationToken);
            Assert.True(exitCode == 0, $"{label}: xngine_probe_json.py exited {exitCode}: {error}");
            return JsonNode.Parse(File.ReadAllText(output))!.AsObject();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>The shell's option bag for an input path: the install walk-up as bmt.classic-game, or nothing.</summary>
    private static IReadOnlyDictionary<string, string> ShellOptions(string path)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ClassicGameLocator.DetectRootForFile(path) is { } detected)
        {
            options[BethesdaModelRegistration.ClassicGameOption] = detected.Profile.Game.ToString();
            options[BethesdaModelRegistration.ClassicGameEvidenceOption] = "install root " + detected.Root;
        }

        return options;
    }
}
