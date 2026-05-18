namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     Shared asset path policy for collection, indexing, resolution, and record-field
///     rewrites. Keep path classification here so the packer and rewrite pass agree on
///     what a valid Data-relative asset path means.
/// </summary>
internal static class AssetPathRules
{
    public static readonly HashSet<string> AssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nif", ".dds", ".ddx", ".kf", ".wav", ".lip", ".egm", ".egt",
        ".xwm", ".ogg", ".bik", ".psa", ".tri", ".xma", ".mp3"
    };

    public static readonly Dictionary<string, string> ExtensionToPrefix =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".nif"] = "meshes\\",
            [".kf"] = "meshes\\",
            [".egm"] = "meshes\\",
            [".egt"] = "meshes\\",
            [".tri"] = "meshes\\",
            [".psa"] = "meshes\\",
            [".dds"] = "textures\\",
            [".ddx"] = "textures\\",
            [".wav"] = "sound\\",
            [".lip"] = "sound\\",
            [".ogg"] = "sound\\",
            [".xwm"] = "sound\\",
            [".bik"] = "video\\"
        };

    public static readonly Dictionary<string, string[]> ExtensionSwaps =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".wav"] = [".ogg", ".xwm", ".xma", ".mp3"],
            [".ogg"] = [".wav", ".xwm", ".xma", ".mp3"],
            [".xwm"] = [".wav", ".ogg", ".xma", ".mp3"],
            [".xma"] = [".wav", ".ogg", ".xwm", ".mp3"],
            [".mp3"] = [".wav", ".ogg", ".xwm", ".xma"],
            [".ddx"] = [".dds"],
            [".dds"] = [".ddx"]
        };

    public static readonly string[] PathLikePropertyTokens =
    [
        "Path", "FileName", "Texture", "Model", "Icon", "Mesh"
    ];

    public static readonly string[] DmpScanStrictPrefixes =
    [
        "meshes\\", "textures\\", "sound\\", "music\\", "video\\",
        "data\\meshes\\", "data\\textures\\", "data\\sound\\", "data\\music\\", "data\\video\\"
    ];

    public static string? TryNormalizeRequestPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var lower = raw.Trim().Replace('/', '\\').ToLowerInvariant();
        if (lower.StartsWith("data\\", StringComparison.Ordinal))
        {
            lower = lower[5..];
        }

        var ext = Path.GetExtension(lower);
        if (string.IsNullOrEmpty(ext) || !AssetExtensions.Contains(ext))
        {
            return null;
        }

        if (!ExtensionToPrefix.TryGetValue(ext, out var expectedPrefix))
        {
            return null;
        }

        var prefixIdx = lower.IndexOf(expectedPrefix, StringComparison.Ordinal);
        if (prefixIdx >= 0)
        {
            lower = lower[prefixIdx..];
        }
        else
        {
            while (lower.Length > 0 && lower[0] == '\\')
            {
                lower = lower[1..];
            }

            if (lower.Length == 0 || !lower.Contains('\\'))
            {
                return null;
            }

            lower = expectedPrefix + lower;
        }

        return lower;
    }

    public static string NormalizeDataRelativePath(string raw)
    {
        var trimmed = raw.Trim().Replace('/', '\\');

        var firstNonSeparator = 0;
        while (firstNonSeparator < trimmed.Length && trimmed[firstNonSeparator] == '\\')
        {
            firstNonSeparator++;
        }

        if (firstNonSeparator > 0)
        {
            trimmed = trimmed[firstNonSeparator..];
        }

        if (trimmed.StartsWith("data\\", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[5..];
        }

        return trimmed.ToLowerInvariant();
    }

    public static bool IsEngineGlobalCharacterAsset(string normalizedPath)
    {
        if (!normalizedPath.StartsWith("meshes\\characters\\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var ext = Path.GetExtension(normalizedPath);
        if (ext.Equals(".kf", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!ext.Equals(".nif", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fileName = Path.GetFileNameWithoutExtension(normalizedPath);
        if (!fileName.StartsWith("skeleton", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return normalizedPath.StartsWith("meshes\\characters\\_male\\", StringComparison.OrdinalIgnoreCase)
               || normalizedPath.StartsWith("meshes\\characters\\_female\\", StringComparison.OrdinalIgnoreCase)
               || normalizedPath.StartsWith("meshes\\characters\\_1stperson\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Do not pack pre-baked LOD assets pulled from a prototype DMP / Xbox 360 BSA.
    ///     Covers both meshes and textures because all LOD assets are baked from one
    ///     specific build's terrain mesh layout and worldspace bounds:
    ///     <list type="bullet">
    ///         <item><description>
    ///             <c>meshes\landscape\lod\&lt;ws&gt;\(blocks|stinger)\*.nif</c> — LOD block
    ///             meshes referencing STAT/SCOL base records at the prototype's FormIDs.
    ///             Loading on top of PC final's terrain produces a scene-graph that
    ///             references geometry/IDs that don't fit, then crashes during
    ///             <c>BGSDistantObjectBlock::ApplyObjectsAlphaState</c> (type-3 LOD-object
    ///             content comes up null).
    ///         </description></item>
    ///         <item><description>
    ///             <c>textures\landscape\lod\&lt;ws&gt;\(diffuse|normals)\*.dds</c> — per-block
    ///             LOD terrain textures. Coords are encoded in the filename and must match
    ///             the LOD mesh's expected grid. Mixing prototype LOD textures with PC
    ///             final's LOD meshes (or vice versa) produces orphaned references that
    ///             flood the engine's asset pipeline with "Could not get file" lookups.
    ///         </description></item>
    ///     </list>
    ///     The fix is to never repack these files — the engine falls back to master's
    ///     matching-terrain LOD instead.
    /// </summary>
    public static bool IsTerrainBoundLodAsset(string normalizedPath)
    {
        // LOD object meshes: meshes\landscape\lod\<ws>\(blocks|stinger)\*.nif
        if (normalizedPath.StartsWith("meshes\\landscape\\lod\\", StringComparison.OrdinalIgnoreCase)
            && normalizedPath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)
            && (normalizedPath.Contains("\\blocks\\", StringComparison.OrdinalIgnoreCase)
                || normalizedPath.Contains("\\stinger\\", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        // LOD terrain textures: textures\landscape\lod\<ws>\*.dds
        if (normalizedPath.StartsWith("textures\\landscape\\lod\\", StringComparison.OrdinalIgnoreCase)
            && normalizedPath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return false;
    }

    public static string ComputeLooseBasename(string fileNameWithExtension)
    {
        var withoutExt = Path.GetFileNameWithoutExtension(fileNameWithExtension);
        if (string.IsNullOrEmpty(withoutExt))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[withoutExt.Length];
        var write = 0;
        foreach (var ch in withoutExt)
        {
            if (ch is ' ' or '_' or '-' or '\'')
            {
                continue;
            }

            buffer[write++] = char.ToLowerInvariant(ch);
        }

        return write == 0 ? string.Empty : new string(buffer[..write]);
    }

    public static string ComputeLooseBasenameWithoutNvAffix(string fileNameWithExtension)
    {
        var loose = ComputeLooseBasename(fileNameWithExtension);
        if (loose.Length < 7)
        {
            return string.Empty;
        }

        var start = 0;
        var end = loose.Length;
        const int minStemAfterStrip = 5;
        var stripped = false;

        if (end - start >= 2 + minStemAfterStrip
            && loose[start] == 'n' && loose[start + 1] == 'v')
        {
            start += 2;
            stripped = true;
        }

        if (end - start >= 2 + minStemAfterStrip
            && loose[end - 2] == 'n' && loose[end - 1] == 'v')
        {
            end -= 2;
            stripped = true;
        }

        return stripped ? loose[start..end] : string.Empty;
    }

    public static bool TryGetExtensionPrefix(string extension, out string prefix)
    {
        return ExtensionToPrefix.TryGetValue(extension, out prefix!);
    }

    public static IEnumerable<string> EnumerateExtensionSwaps(string normalizedPath)
    {
        var ext = Path.GetExtension(normalizedPath);
        if (string.IsNullOrEmpty(ext) || !ExtensionSwaps.TryGetValue(ext, out var swaps))
        {
            yield break;
        }

        var stem = normalizedPath[..^ext.Length];
        foreach (var swap in swaps)
        {
            yield return stem + swap;
        }
    }

    public static bool ExtensionsAreCompatible(string requestedExt, string candidatePath)
    {
        var candidateExt = Path.GetExtension(candidatePath);
        if (string.Equals(requestedExt, candidateExt, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (ExtensionSwaps.TryGetValue(requestedExt, out var swaps))
        {
            foreach (var swap in swaps)
            {
                if (string.Equals(swap, candidateExt, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static string DenormalizeForField(string normalizedNewPath, string originalRawPath)
    {
        if (string.IsNullOrEmpty(normalizedNewPath))
        {
            return normalizedNewPath;
        }

        var lowerRaw = (originalRawPath ?? string.Empty).ToLowerInvariant().Replace('/', '\\');
        var hadDataPrefix = lowerRaw.StartsWith("data\\", StringComparison.Ordinal);
        if (hadDataPrefix)
        {
            lowerRaw = lowerRaw[5..];
        }

        var origExt = Path.GetExtension(lowerRaw);
        var newExt = Path.GetExtension(normalizedNewPath);

        var originalHadTypePrefix = false;
        if (TryGetExtensionPrefix(origExt, out var origPrefix))
        {
            originalHadTypePrefix = lowerRaw.StartsWith(origPrefix, StringComparison.Ordinal);
        }

        if (!TryGetExtensionPrefix(newExt, out var newPrefix))
        {
            return normalizedNewPath;
        }

        var withoutPrefix = normalizedNewPath.StartsWith(newPrefix, StringComparison.Ordinal)
            ? normalizedNewPath[newPrefix.Length..]
            : normalizedNewPath;

        if (originalHadTypePrefix)
        {
            return hadDataPrefix
                ? "Data\\" + newPrefix + withoutPrefix
                : newPrefix + withoutPrefix;
        }

        return hadDataPrefix
            ? "Data\\" + withoutPrefix
            : withoutPrefix;
    }
}
