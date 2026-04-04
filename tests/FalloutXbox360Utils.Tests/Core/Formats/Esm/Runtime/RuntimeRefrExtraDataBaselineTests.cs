using System.Globalization;
using System.Text.Json;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime;

public sealed class RuntimeRefrExtraDataBaselineTests
{
    private static readonly string SnippetDir = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestData", "Dmp");

    private static readonly JsonSerializerOptions BaselineJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    ///     Maps baseline samplePath values to snippet names used by DmpSnippetReader.
    /// </summary>
    private static readonly Dictionary<string, string> SamplePathToSnippet = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"Sample\MemoryDump\Fallout_Debug.xex.dmp"] = "debug_dump",
        [@"Sample\MemoryDump\Fallout_Release_Beta.xex.dmp"] = "release_dump",
        [@"Sample\MemoryDump\Fallout_Release_Beta.xex4.dmp"] = "xex4_dump",
        [@"Sample\MemoryDump\Fallout_Release_Beta.xex44.dmp"] = "xex44_dump",
        [@"Sample\MemoryDump\Fallout_Release_MemDebug.xex.dmp"] = "memdebug_dump"
    };

    [Fact]
    public void RuntimeRefrExtraBaselines_HaveValidSchema()
    {
        using var doc = LoadBaselinesDocument();
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);

        var labels = new HashSet<string>(StringComparer.Ordinal);
        var samplePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in doc.RootElement.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Object, row.ValueKind);

            Assert.True(row.TryGetProperty("label", out var labelElement));
            Assert.True(row.TryGetProperty("family", out var familyElement));
            Assert.True(row.TryGetProperty("samplePath", out var samplePathElement));
            Assert.True(row.TryGetProperty("sampleLimit", out var sampleLimitElement));
            Assert.True(row.TryGetProperty("isEarlyBuild", out var isEarlyBuildElement));
            Assert.True(row.TryGetProperty("sampleCount", out var sampleCountElement));
            Assert.True(row.TryGetProperty("validRefrCount", out var validRefrCountElement));
            Assert.True(row.TryGetProperty("refsWithExtraData", out var refsWithExtraDataElement));
            Assert.True(row.TryGetProperty("visitedNodeCount", out var visitedNodeCountElement));
            Assert.True(row.TryGetProperty("ownershipCount", out var ownershipCountElement));
            Assert.True(row.TryGetProperty("lockCount", out var lockCountElement));
            Assert.True(row.TryGetProperty("teleportCount", out var teleportCountElement));
            Assert.True(row.TryGetProperty("mapMarkerCount", out var mapMarkerCountElement));
            Assert.True(row.TryGetProperty("enableParentCount", out var enableParentCountElement));
            Assert.True(row.TryGetProperty("linkedRefCount", out var linkedRefCountElement));
            Assert.True(row.TryGetProperty("encounterZoneCount", out var encounterZoneCountElement));
            Assert.True(row.TryGetProperty("startingPositionCount", out var startingPositionCountElement));
            Assert.True(row.TryGetProperty("startingWorldOrCellCount", out var startingWorldOrCellCountElement));
            Assert.True(row.TryGetProperty("packageStartLocationCount", out var packageStartLocationCountElement));
            Assert.True(row.TryGetProperty("merchantContainerCount", out var merchantContainerCountElement));
            Assert.True(row.TryGetProperty("leveledCreatureCount", out var leveledCreatureCountElement));
            Assert.True(row.TryGetProperty("radiusCount", out var radiusCountElement));
            Assert.True(row.TryGetProperty("countCount", out var countCountElement));
            Assert.True(row.TryGetProperty("editorIdCount", out var editorIdCountElement));
            Assert.True(row.TryGetProperty("typeCounts", out var typeCountsElement));
            Assert.True(row.TryGetProperty("notes", out var notesElement));

            var label = Assert.IsType<string>(labelElement.GetString());
            Assert.NotEmpty(label);
            Assert.True(labels.Add(label), $"Duplicate baseline label: {label}");

            Assert.False(string.IsNullOrWhiteSpace(familyElement.GetString()));

            var samplePath = Assert.IsType<string>(samplePathElement.GetString());
            Assert.NotEmpty(samplePath);
            Assert.True(samplePaths.Add(samplePath), $"Duplicate baseline samplePath: {samplePath}");

            Assert.True(sampleLimitElement.GetInt32() > 0);
            Assert.True(isEarlyBuildElement.ValueKind is JsonValueKind.True or JsonValueKind.False);
            Assert.True(sampleCountElement.GetInt32() >= 0);
            Assert.True(validRefrCountElement.GetInt32() >= 0);
            Assert.True(refsWithExtraDataElement.GetInt32() >= 0);
            Assert.True(visitedNodeCountElement.GetInt32() >= 0);
            Assert.True(ownershipCountElement.GetInt32() >= 0);
            Assert.True(lockCountElement.GetInt32() >= 0);
            Assert.True(teleportCountElement.GetInt32() >= 0);
            Assert.True(mapMarkerCountElement.GetInt32() >= 0);
            Assert.True(enableParentCountElement.GetInt32() >= 0);
            Assert.True(linkedRefCountElement.GetInt32() >= 0);
            Assert.True(encounterZoneCountElement.GetInt32() >= 0);
            Assert.True(startingPositionCountElement.GetInt32() >= 0);
            Assert.True(startingWorldOrCellCountElement.GetInt32() >= 0);
            Assert.True(packageStartLocationCountElement.GetInt32() >= 0);
            Assert.True(merchantContainerCountElement.GetInt32() >= 0);
            Assert.True(leveledCreatureCountElement.GetInt32() >= 0);
            Assert.True(radiusCountElement.GetInt32() >= 0);
            Assert.True(countCountElement.GetInt32() >= 0);
            Assert.True(editorIdCountElement.GetInt32() >= 0);
            Assert.Equal(JsonValueKind.Object, typeCountsElement.ValueKind);
            Assert.False(string.IsNullOrWhiteSpace(notesElement.GetString()));

            foreach (var typeCount in typeCountsElement.EnumerateObject())
            {
                Assert.True(byte.TryParse(typeCount.Name, out _), $"Invalid type-count key '{typeCount.Name}'.");
                Assert.True(typeCount.Value.GetInt32() >= 0);
            }
        }
    }

    [Fact]
    public async Task RuntimeRefrExtraBaselines_MatchObservedDumpResults()
    {
        var baselines = LoadBaselines();
        Assert.NotEmpty(baselines);

        foreach (var baseline in baselines)
        {
            Assert.True(SamplePathToSnippet.TryGetValue(baseline.SamplePath, out var snippetName),
                $"No snippet mapping for baseline '{baseline.Label}': {baseline.SamplePath}");

            var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);
            var observed = ObserveSnippet(snippet, baseline.SampleLimit);
            var mismatch = BuildMismatchMessage(baseline, observed);

            Assert.True(observed.IsEarlyBuild == baseline.IsEarlyBuild, mismatch);
            Assert.True(observed.SampleCount == baseline.SampleCount, mismatch);
            Assert.True(observed.ValidRefrCount == baseline.ValidRefrCount, mismatch);
            Assert.True(observed.RefsWithExtraData == baseline.RefsWithExtraData, mismatch);
            Assert.True(observed.VisitedNodeCount == baseline.VisitedNodeCount, mismatch);
            Assert.True(observed.OwnershipCount == baseline.OwnershipCount, mismatch);
            Assert.True(observed.LockCount == baseline.LockCount, mismatch);
            Assert.True(observed.TeleportCount == baseline.TeleportCount, mismatch);
            Assert.True(observed.MapMarkerCount == baseline.MapMarkerCount, mismatch);
            Assert.True(observed.EnableParentCount == baseline.EnableParentCount, mismatch);
            Assert.True(observed.LinkedRefCount == baseline.LinkedRefCount, mismatch);
            Assert.True(observed.EncounterZoneCount == baseline.EncounterZoneCount, mismatch);
            Assert.True(observed.StartingPositionCount == baseline.StartingPositionCount, mismatch);
            Assert.True(observed.StartingWorldOrCellCount == baseline.StartingWorldOrCellCount, mismatch);
            Assert.True(observed.PackageStartLocationCount == baseline.PackageStartLocationCount, mismatch);
            Assert.True(observed.MerchantContainerCount == baseline.MerchantContainerCount, mismatch);
            Assert.True(observed.LeveledCreatureCount == baseline.LeveledCreatureCount, mismatch);
            Assert.True(observed.RadiusCount == baseline.RadiusCount, mismatch);
            Assert.True(observed.CountCount == baseline.CountCount, mismatch);
            Assert.True(observed.EditorIdCount == baseline.EditorIdCount, mismatch);
            Assert.True(TypeCountsEqual(observed.TypeCounts, baseline.TypeCounts), mismatch);
        }
    }

    private static RuntimeRefrExtraObservation ObserveSnippet(DmpSnippetReader snippet, int sampleLimit)
    {
        var reader = RuntimeStructReader.CreateWithAutoDetect(
            snippet.Accessor,
            snippet.FileSize,
            snippet.MinidumpInfo,
            snippet.RuntimeRefrFormEntries);

        var census = reader.BuildRuntimeRefrExtraDataCensus(
            snippet.RuntimeRefrFormEntries,
            sampleLimit);

        return new RuntimeRefrExtraObservation(
            reader.IsEarlyBuild,
            census.SampleCount,
            census.ValidRefrCount,
            census.RefsWithExtraData,
            census.VisitedNodeCount,
            census.OwnershipCount,
            census.LockCount,
            census.TeleportCount,
            census.MapMarkerCount,
            census.EnableParentCount,
            census.LinkedRefCount,
            census.EncounterZoneCount,
            census.StartingPositionCount,
            census.StartingWorldOrCellCount,
            census.PackageStartLocationCount,
            census.MerchantContainerCount,
            census.LeveledCreatureCount,
            census.RadiusCount,
            census.CountCount,
            census.EditorIdCount,
            census.TypeCounts);
    }

    private static bool TypeCountsEqual(IReadOnlyDictionary<byte, int> observed,
        IReadOnlyDictionary<string, int> baseline)
    {
        if (observed.Count != baseline.Count)
        {
            return false;
        }

        foreach (var (type, count) in observed)
        {
            if (!baseline.TryGetValue(type.ToString(CultureInfo.InvariantCulture), out var expected) || expected != count)
            {
                return false;
            }
        }

        return true;
    }

    private static string BuildMismatchMessage(
        RuntimeRefrExtraBaselineRow baseline,
        RuntimeRefrExtraObservation observed)
    {
        return
            $"Baseline '{baseline.Label}' mismatch. " +
            $"Expected early={baseline.IsEarlyBuild}, sampleCount={baseline.SampleCount}, valid={baseline.ValidRefrCount}, " +
            $"withExtra={baseline.RefsWithExtraData}, nodes={baseline.VisitedNodeCount}, ownership={baseline.OwnershipCount}, " +
            $"lock={baseline.LockCount}, teleport={baseline.TeleportCount}, marker={baseline.MapMarkerCount}, " +
            $"enableParent={baseline.EnableParentCount}, linkedRef={baseline.LinkedRefCount}, encounterZone={baseline.EncounterZoneCount}, " +
            $"startingPosition={baseline.StartingPositionCount}, startingWorldOrCell={baseline.StartingWorldOrCellCount}, packageStart={baseline.PackageStartLocationCount}, " +
            $"merchantContainer={baseline.MerchantContainerCount}, leveledCreature={baseline.LeveledCreatureCount}, radius={baseline.RadiusCount}, " +
            $"count={baseline.CountCount}, editorId={baseline.EditorIdCount}, " +
            $"typeCounts={JsonSerializer.Serialize(baseline.TypeCounts)}. " +
            $"Observed early={observed.IsEarlyBuild}, sampleCount={observed.SampleCount}, valid={observed.ValidRefrCount}, " +
            $"withExtra={observed.RefsWithExtraData}, nodes={observed.VisitedNodeCount}, ownership={observed.OwnershipCount}, " +
            $"lock={observed.LockCount}, teleport={observed.TeleportCount}, marker={observed.MapMarkerCount}, " +
            $"enableParent={observed.EnableParentCount}, linkedRef={observed.LinkedRefCount}, encounterZone={observed.EncounterZoneCount}, " +
            $"startingPosition={observed.StartingPositionCount}, startingWorldOrCell={observed.StartingWorldOrCellCount}, packageStart={observed.PackageStartLocationCount}, " +
            $"merchantContainer={observed.MerchantContainerCount}, leveledCreature={observed.LeveledCreatureCount}, radius={observed.RadiusCount}, " +
            $"count={observed.CountCount}, editorId={observed.EditorIdCount}, " +
            $"typeCounts={JsonSerializer.Serialize(observed.TypeCounts.OrderBy(pair => pair.Key).ToDictionary(pair => pair.Key.ToString(CultureInfo.InvariantCulture), pair => pair.Value))}.";
    }

    private static List<RuntimeRefrExtraBaselineRow> LoadBaselines()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "docs", "runtime-refr-extra-baselines.json");
        Assert.True(File.Exists(path), $"REFR extra-data baseline file not found: {path}");

        var baselines = JsonSerializer.Deserialize<List<RuntimeRefrExtraBaselineRow>>(
            File.ReadAllText(path),
            BaselineJsonOptions);
        Assert.NotNull(baselines);
        return baselines!;
    }

    private static JsonDocument LoadBaselinesDocument()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "docs", "runtime-refr-extra-baselines.json");
        Assert.True(File.Exists(path), $"REFR extra-data baseline file not found: {path}");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(dir, "Xbox360MemoryCarver.slnx")) ||
                File.Exists(Path.Combine(dir, "docs", "runtime-refr-extra-baselines.json")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir)!;
        }

        throw new DirectoryNotFoundException("Could not locate repo root from test base directory.");
    }

    private sealed record RuntimeRefrExtraBaselineRow(
        string Label,
        string Family,
        string SamplePath,
        int SampleLimit,
        bool IsEarlyBuild,
        int SampleCount,
        int ValidRefrCount,
        int RefsWithExtraData,
        int VisitedNodeCount,
        int OwnershipCount,
        int LockCount,
        int TeleportCount,
        int MapMarkerCount,
        int EnableParentCount,
        int LinkedRefCount,
        int EncounterZoneCount,
        int StartingPositionCount,
        int StartingWorldOrCellCount,
        int PackageStartLocationCount,
        int MerchantContainerCount,
        int LeveledCreatureCount,
        int RadiusCount,
        int CountCount,
        int EditorIdCount,
        Dictionary<string, int> TypeCounts,
        string Notes);

    private sealed record RuntimeRefrExtraObservation(
        bool IsEarlyBuild,
        int SampleCount,
        int ValidRefrCount,
        int RefsWithExtraData,
        int VisitedNodeCount,
        int OwnershipCount,
        int LockCount,
        int TeleportCount,
        int MapMarkerCount,
        int EnableParentCount,
        int LinkedRefCount,
        int EncounterZoneCount,
        int StartingPositionCount,
        int StartingWorldOrCellCount,
        int PackageStartLocationCount,
        int MerchantContainerCount,
        int LeveledCreatureCount,
        int RadiusCount,
        int CountCount,
        int EditorIdCount,
        IReadOnlyDictionary<byte, int> TypeCounts);
}