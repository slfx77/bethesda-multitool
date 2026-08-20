using System.Text.Json;

namespace BethesdaMultitool.Core.WorldData;

/// <summary>
///     Identifies the companion JSON files owned by the 2D tiled-map exporter. A filename convention
///     alone is not ownership: an unrelated same-stem JSON must never be deleted.
/// </summary>
internal static class WorldMapExportManifestOwnership
{
    internal const string FormatId = "BethesdaMultitool.WorldMapTileManifest";
    internal const int CurrentVersion = 1;

    internal static void EnsureExistingCompanionIsOwned(
        string manifestPath,
        string outputBaseName,
        string tileExtension)
    {
        if (!File.Exists(manifestPath)) return;

        string json;
        try
        {
            json = File.ReadAllText(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"Could not verify ownership of the existing map-export companion '{manifestPath}'.",
                ex);
        }

        if (!IsOwnedManifest(json, outputBaseName, tileExtension))
        {
            throw new InvalidDataException(
                $"Refusing to replace unrelated same-name JSON '{manifestPath}'. Choose another PNG name.");
        }
    }

    internal static void EnsureSelectedPathIsNotAnOwnedTile(string outputPath)
    {
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath);
        var extension = Path.GetExtension(fullPath);
        var selectedName = Path.GetFileNameWithoutExtension(fullPath);
        if (string.IsNullOrEmpty(directory) ||
            !TryGetOwnerBaseName(selectedName, out var ownerBaseName))
        {
            return;
        }

        var ownerManifest = Path.Combine(directory, $"{ownerBaseName}_manifest.json");
        if (!File.Exists(ownerManifest)) return;

        string json;
        try
        {
            json = File.ReadAllText(ownerManifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"Could not verify whether '{outputPath}' belongs to an existing tiled export.",
                ex);
        }

        if (IsOwnedManifest(json, ownerBaseName, extension))
        {
            throw new InvalidDataException(
                $"'{outputPath}' is a tile owned by '{ownerManifest}'. Choose a base PNG name instead.");
        }
    }

    /// <summary>
    ///     Accepts marker-bearing manifests and the exact legacy shape emitted before the marker was
    ///     introduced. Legacy ownership still requires every tile filename to match its physical row/col.
    /// </summary>
    internal static bool IsOwnedManifest(
        string json,
        string outputBaseName,
        string tileExtension)
    {
        if (string.IsNullOrWhiteSpace(json) ||
            string.IsNullOrWhiteSpace(outputBaseName) ||
            string.IsNullOrWhiteSpace(tileExtension))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("tiles", out var tiles) ||
                tiles.ValueKind != JsonValueKind.Array ||
                tiles.GetArrayLength() == 0)
            {
                return false;
            }

            var hasFormatMarker = root.TryGetProperty("format", out var format);
            if (hasFormatMarker &&
                (!string.Equals(format.GetString(), FormatId, StringComparison.Ordinal) ||
                 !root.TryGetProperty("version", out var version) ||
                 version.ValueKind != JsonValueKind.Number ||
                 !version.TryGetInt32(out var versionNumber) ||
                 versionNumber != CurrentVersion))
            {
                return false;
            }

            if (!hasFormatMarker &&
                (!root.TryGetProperty("layer", out var layer) || layer.ValueKind != JsonValueKind.String ||
                 !HasInteger(root, "pixelsPerCell") ||
                 !HasInteger(root, "tilesWide") ||
                 !HasInteger(root, "tilesTall") ||
                 !HasInteger(root, "gridX0") ||
                 !HasInteger(root, "gridX1") ||
                 !HasInteger(root, "gridY0") ||
                 !HasInteger(root, "gridY1")))
            {
                return false;
            }

            foreach (var tile in tiles.EnumerateArray())
            {
                if (tile.ValueKind != JsonValueKind.Object ||
                    !tile.TryGetProperty("row", out var rowElement) ||
                    !tile.TryGetProperty("col", out var columnElement) ||
                    !tile.TryGetProperty("file", out var fileElement) ||
                    rowElement.ValueKind != JsonValueKind.Number ||
                    columnElement.ValueKind != JsonValueKind.Number ||
                    fileElement.ValueKind != JsonValueKind.String ||
                    !rowElement.TryGetInt32(out var row) || row < 0 ||
                    !columnElement.TryGetInt32(out var column) || column < 0)
                {
                    return false;
                }

                var expected = $"{outputBaseName}_r{row}_c{column}{tileExtension}";
                if (!string.Equals(
                        Path.GetFileName(fileElement.GetString()),
                        expected,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryGetOwnerBaseName(string selectedName, out string ownerBaseName)
    {
        ownerBaseName = string.Empty;
        var columnMarker = selectedName.LastIndexOf("_c", StringComparison.OrdinalIgnoreCase);
        if (columnMarker < 0 ||
            !IsNonNegativeInteger(selectedName.AsSpan(columnMarker + 2)))
        {
            return false;
        }

        var rowMarker = selectedName.LastIndexOf(
            "_r",
            columnMarker - 1,
            StringComparison.OrdinalIgnoreCase);
        if (rowMarker <= 0 ||
            !IsNonNegativeInteger(selectedName.AsSpan(rowMarker + 2, columnMarker - rowMarker - 2)))
        {
            return false;
        }

        ownerBaseName = selectedName[..rowMarker];
        return true;
    }

    private static bool HasInteger(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out _);
    }

    private static bool IsNonNegativeInteger(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty) return false;
        foreach (var character in value)
        {
            if (character is < '0' or > '9') return false;
        }

        return true;
    }
}
