using System.Text.Json;

namespace Xbox360MemoryCarver.Core.Carving;

/// <summary>
///     Entry in the carving manifest.
/// </summary>
public class CarveEntry
{
    public string FileType { get; set; } = "";
    public long Offset { get; set; }
    public int SizeInDump { get; set; }
    public int SizeOutput { get; set; }
    public string Filename { get; set; } = "";

    /// <summary>
    ///     Original file path from the game data (e.g., "textures\architecture\anvil\anvildoor01.ddx").
    ///     Only populated for files where the path could be extracted from memory.
    /// </summary>
    public string? OriginalPath { get; set; }

    public bool IsCompressed { get; set; }
    public string? ContentType { get; set; }
    public bool IsPartial { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
///     Manages the carving manifest and serialization.
/// </summary>
public static class CarveManifest
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    ///     Save the manifest to a JSON file.
    /// </summary>
    public static async Task SaveAsync(string outputPath, IEnumerable<CarveEntry> entries)
    {
        var manifestPath = Path.Combine(outputPath, "manifest.json");
        var json = JsonSerializer.Serialize(entries.ToList(), JsonOptions);
        await File.WriteAllTextAsync(manifestPath, json);
    }

    /// <summary>
    ///     Load a manifest from a JSON file.
    /// </summary>
    public static async Task<List<CarveEntry>> LoadAsync(string manifestPath)
    {
        var json = await File.ReadAllTextAsync(manifestPath);
        return JsonSerializer.Deserialize<List<CarveEntry>>(json) ?? [];
    }
}
