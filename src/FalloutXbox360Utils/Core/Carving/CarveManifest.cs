using System.Text.Json;
using FalloutXbox360Utils.Core.Json;

namespace FalloutXbox360Utils.Core.Carving;

/// <summary>
///     Manages the carving manifest and serialization.
///     Uses source-generated JSON serialization for trim compatibility.
/// </summary>
public static class CarveManifest
{
    /// <summary>
    ///     Save the manifest to a JSON file.
    /// </summary>
    public static async Task SaveAsync(string outputPath, IEnumerable<CarveEntry> entries)
    {
        var manifestPath = Path.Combine(outputPath, "manifest.json");
        var json = JsonSerializer.Serialize(entries.ToList(), CarverJsonContext.Default.ListCarveEntry);
        await File.WriteAllTextAsync(manifestPath, json);
    }

    /// <summary>
    ///     Load a manifest from a JSON file.
    /// </summary>
    public static async Task<List<CarveEntry>> LoadAsync(string manifestPath)
    {
        var json = await File.ReadAllTextAsync(manifestPath);
        return JsonSerializer.Deserialize(json, CarverJsonContext.Default.ListCarveEntry) ?? [];
    }
}
