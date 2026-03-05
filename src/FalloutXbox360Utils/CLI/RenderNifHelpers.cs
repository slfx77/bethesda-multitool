using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using FalloutXbox360Utils.Core.Formats.Bsa;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     Helper methods and data types for NIF rendering: BSA filtering, texture resolution,
///     ESM cross-referencing, and output generation.
/// </summary>
internal static class RenderNifHelpers
{
    private static readonly JsonSerializerOptions JsonOptions = RenderIndexJsonContext.Default.Options;

    internal static List<BsaFileRecord> CollectNifFiles(BsaArchive archive, string? filter)
    {
        var nifFiles = new List<BsaFileRecord>();

        foreach (var folder in archive.Folders)
        {
            foreach (var file in folder.Files)
            {
                var fullPath = file.FullPath;
                if (!fullPath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Skip LOD and marker meshes
                var fileName = Path.GetFileName(fullPath);
                if (fileName.StartsWith("marker", StringComparison.OrdinalIgnoreCase) ||
                    fileName.EndsWith("_far.nif", StringComparison.OrdinalIgnoreCase) ||
                    fileName.EndsWith("_lod.nif", StringComparison.OrdinalIgnoreCase) ||
                    fullPath.Contains("\\lod\\", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Apply folder filter
                if (filter != null && !fullPath.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                nifFiles.Add(file);
            }
        }

        return nifFiles;
    }

    /// <summary>Convert BSA path to base filename: meshes\foo\bar.nif → meshes__foo__bar</summary>
    internal static string BsaPathToBaseName(string bsaPath)
    {
        var baseName = bsaPath
            .Replace('\\', '_')
            .Replace('/', '_');
        return Path.GetFileNameWithoutExtension(baseName);
    }

    internal static bool ValidateTextureBsas(string[] textureBsaPaths)
    {
        foreach (var texBsa in textureBsaPaths)
        {
            if (!File.Exists(texBsa))
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Textures BSA not found: {0}", texBsa);
                return false;
            }
        }

        return true;
    }

    internal static NifTextureResolver? CreateTextureResolver(string[] textureBsaPaths)
    {
        if (textureBsaPaths.Length == 0)
        {
            return null;
        }

        foreach (var texBsa in textureBsaPaths)
        {
            AnsiConsole.MarkupLine("Loading textures BSA: [cyan]{0}[/]", Path.GetFileName(texBsa));
        }

        return new NifTextureResolver(textureBsaPaths);
    }

    internal static EsmModelCrossReference? LoadEsmCrossReference(string? esmPath)
    {
        if (esmPath == null)
        {
            return null;
        }

        if (!File.Exists(esmPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] ESM file not found: {0}", esmPath);
            return null;
        }

        var esm = EsmFileLoader.Load(esmPath, false);
        if (esm == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Failed to load ESM file");
            return null;
        }

        AnsiConsole.MarkupLine("Building ESM cross-reference: [cyan]{0}[/]", Path.GetFileName(esmPath));
        var crossRef = EsmModelCrossReference.Build(esm.Data, esm.IsBigEndian);
        AnsiConsole.MarkupLine("Indexed [green]{0}[/] base records, [green]{1}[/] placed references",
            crossRef.BaseRecordCount, crossRef.RefCount);
        return crossRef;
    }

    internal static void EnrichWithCrossReference(SpriteIndexEntry entry, EsmModelCrossReference? crossRef,
        string modelPath)
    {
        var xref = crossRef?.Lookup(modelPath);
        if (xref == null)
        {
            return;
        }

        if (xref.BaseRecords.Count > 0)
        {
            entry.BaseRecords = new Dictionary<string, BaseRecordValue>();
            foreach (var br in xref.BaseRecords)
            {
                entry.BaseRecords[br.FormId.ToString("X8")] = new BaseRecordValue
                {
                    EditorId = br.EditorId,
                    Type = br.RecordType
                };
            }
        }

        if (xref.Refs.Count > 0)
        {
            entry.Refs = new Dictionary<string, string?>();
            foreach (var r in xref.Refs)
            {
                entry.Refs[r.FormId.ToString("X8")] = r.EditorId;
            }
        }
    }

    internal static void WriteIndexAndSummary(string outputDir,
        ConcurrentDictionary<string, SpriteIndexEntry> index,
        ProcessingStats stats, NifTextureResolver? textureResolver, CancellationToken ct)
    {
        // Write index file
        var indexPath = Path.Combine(outputDir, "sprite-index.json");
        var sortedIndex = new SortedDictionary<string, SpriteIndexEntry>(index);
        var json = JsonSerializer.Serialize(sortedIndex, JsonOptions);
        File.WriteAllText(indexPath, json);

        // Summary
        var pngSuffix = stats.PngCount != stats.Rendered ? $" ({stats.PngCount} PNGs)" : "";
        AnsiConsole.MarkupLine("\nRendered: [green]{0}[/]{1}  Skipped: [yellow]{2}[/]  Failed: [red]{3}[/]",
            stats.Rendered, pngSuffix, stats.Skipped, stats.Failed);

        if (textureResolver != null)
        {
            var textured = index.Values.Count(e => e.HasTexture);
            AnsiConsole.MarkupLine("Textured: [cyan]{0}[/]  Texture cache: [green]{1}[/] hits, [yellow]{2}[/] misses",
                textured, textureResolver.CacheHits, textureResolver.CacheMisses);
        }

        AnsiConsole.MarkupLine("Index written to: [cyan]{0}[/]", indexPath);
    }
}

internal sealed class NifRenderSettings
{
    public required string Path { get; init; }
    public required string OutputDir { get; init; }
    public required RenderParams Render { get; init; }
    public string? BsaPath { get; init; }
    public int Parallelism { get; init; }
    public string[] TexturesBsaPaths { get; init; } = [];
    public string? EsmPath { get; init; }
    public bool Isometric { get; init; }
    public float ElevationDeg { get; init; } = 30f;
    public bool ElevationOverridden { get; init; }
    public bool SideProfile { get; init; }
    public bool Trimetric { get; init; }
    public int? FixedSize { get; init; }
    public bool ForceGpu { get; init; }
    public bool ForceCpu { get; init; }
}

internal sealed record RenderParams(float PixelsPerUnit, int MinSize, int MaxSize);

internal sealed class ProcessingStats
{
    public int Failed;
    public int PngCount;
    public int Rendered;
    public int Skipped;
}

internal sealed class SpriteIndexEntry
{
    public required string File { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required float BoundsWidth { get; init; }
    public required float BoundsHeight { get; init; }
    public bool HasTexture { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, BaseRecordValue>? BaseRecords { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string?>? Refs { get; set; }
}

internal sealed class BaseRecordValue
{
    public string? EditorId { get; init; }
    public required string Type { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SortedDictionary<string, SpriteIndexEntry>))]
[JsonSerializable(typeof(Dictionary<string, BaseRecordValue>))]
[JsonSerializable(typeof(Dictionary<string, string?>))]
internal sealed partial class RenderIndexJsonContext : JsonSerializerContext;
