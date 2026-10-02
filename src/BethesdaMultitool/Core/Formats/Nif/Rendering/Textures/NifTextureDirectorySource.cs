using BethesdaMultitool.Core.Formats.Dds;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

/// <summary>Loose-file texture source with unique case-insensitive lookup beneath an explicit local root.</summary>
/// <remarks>Only the requested path's immediate parent directories are enumerated; no recursive index or cache is
/// retained. Existing symlinks/junctions are followed, so the root is a lookup scope, not a filesystem sandbox.</remarks>
internal sealed class NifTextureDirectorySource : INifTextureSource
{
    private const int DefaultMaximumEntryVisits = 65_536;
    private const int MaximumPathCharacters = 32_767;
    private readonly string _rootPath;
    private readonly int _maximumEntryVisits;

    /// <summary>Retains the explicit root and a finite per-request sibling-enumeration budget.</summary>
    /// <param name="rootPath">Existing game-data root, including legitimate linked directories.</param>
    /// <param name="maximumEntryVisits">Total directory entries examined across path segments and DDS/DDX fallback.</param>
    internal NifTextureDirectorySource(string rootPath, int maximumEntryVisits = DefaultMaximumEntryVisits)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumEntryVisits);
        _rootPath = Path.GetFullPath(rootPath);
        _maximumEntryVisits = maximumEntryVisits;
    }

    /// <inheritdoc />
    public DecodedTexture? TryLoad(string path)
    {
        var remaining = _maximumEntryVisits;
        var primary = ResolveLocalPath(path, ref remaining, out var refused);
        if (refused)
        {
            return null;
        }
        var texture = TryDecodeFile(primary);
        if (texture is not null)
        {
            return texture;
        }
        var alternate = AlternatePath(path);
        return alternate is null ? null : TryDecodeFile(ResolveLocalPath(alternate, ref remaining, out _));
    }

    /// <inheritdoc />
    public bool Exists(string path)
    {
        var remaining = _maximumEntryVisits;
        if (ResolveLocalPath(path, ref remaining, out var refused) is not null)
        {
            return true;
        }
        if (refused)
        {
            return false;
        }
        var alternate = AlternatePath(path);
        return alternate is not null && ResolveLocalPath(alternate, ref remaining, out _) is not null;
    }

    /// <inheritdoc />
    public byte[]? TryLoadRaw(string path)
    {
        var remaining = _maximumEntryVisits;
        var primary = ResolveLocalPath(path, ref remaining, out _);
        if (primary is null)
        {
            return null;
        }
        try
        {
            return File.ReadAllBytes(primary);
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public bool TryGetAssetMetadata(string path, out NifTextureSourceAssetMetadata metadata)
    {
        metadata = default;
        var remaining = _maximumEntryVisits;
        var primary = ResolveLocalPath(path, ref remaining, out _);
        if (primary is null)
        {
            return false;
        }
        try
        {
            var file = new FileInfo(primary);
            if (!file.Exists)
            {
                return false;
            }
            metadata = new NifTextureSourceAssetMetadata(
                file.FullName, file.Length, file.LastWriteTimeUtc.Ticks);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // No directory index, handles or decoded textures are retained.
    }

    /// <summary>Preserves existing decoded-load fallback; raw reads and metadata remain exact-extension operations.</summary>
    /// <param name="path">The validated root-relative request.</param>
    /// <returns>The other DDS/DDX spelling, or null for other extensions.</returns>
    private static string? AlternatePath(string path)
    {
        if (path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            return Path.ChangeExtension(path, ".ddx");
        }
        return path.EndsWith(".ddx", StringComparison.OrdinalIgnoreCase) ? Path.ChangeExtension(path, ".dds") : null;
    }

    /// <summary>Uses the existing format decoder and preserves unreadable/undecodable candidate fallback.</summary>
    /// <param name="path">One uniquely resolved physical file, or null for a miss.</param>
    /// <returns>The decoded texture, or null so a supported alternate can be tried.</returns>
    private static DecodedTexture? TryDecodeFile(string? path)
    {
        if (path is null)
        {
            return null;
        }
        try
        {
            return NifTextureLoader.DecodeTextureData(File.ReadAllBytes(path));
        }
        catch
        {
            // Preserve the existing decoder/read failure behavior.
            return null;
        }
    }

    /// <summary>Resolves exactly one case-insensitive child at each requested segment without a stale index.</summary>
    /// <param name="path">Root-relative game path; both separators and current-directory segments are accepted.</param>
    /// <param name="remaining">Shared finite entry-visit budget for this request and its optional extension fallback.</param>
    /// <param name="refused">True for invalid scope, ambiguity, enumeration failure or budget exhaustion; no fallback elects a replacement.</param>
    /// <returns>The original physical spelling, or null for an absent/refused path.</returns>
    private string? ResolveLocalPath(string path, ref int remaining, out bool refused)
    {
        refused = true;
        if (string.IsNullOrEmpty(path) || path.Length > MaximumPathCharacters || path.Contains('\0'))
        {
            return null;
        }
        var relative = path.Replace('\\', '/');
        if (relative[0] == '/' || Path.IsPathRooted(relative) ||
            relative.Length >= 2 && char.IsAsciiLetter(relative[0]) && relative[1] == ':')
        {
            return null;
        }
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(static segment => segment == ".."))
        {
            return null;
        }
        var current = _rootPath;
        var hasName = false;
        try
        {
            foreach (var segment in segments)
            {
                if (segment == ".")
                {
                    continue;
                }
                hasName = true;
                if (!Directory.Exists(current))
                {
                    refused = false;
                    return null;
                }
                string? match = null;
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    if (remaining == 0)
                    {
                        return null;
                    }
                    remaining--;
                    if (!string.Equals(Path.GetFileName(entry), segment, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (match is not null)
                    {
                        return null;
                    }
                    match = entry;
                }
                if (match is null)
                {
                    refused = false;
                    return null;
                }
                current = match;
            }
            refused = !hasName;
            return hasName && File.Exists(current) ? current : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
