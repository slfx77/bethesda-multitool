namespace BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;

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
        ".xwm", ".ogg", ".bik", ".psa", ".tri", ".xma", ".mp3",
        // SpeedTree trees ship under trees\*.spt (NOT meshes\). Without this the index skipped every
        // .spt, so DataFolderResolver returned "missing" for TREE refs and the viewer rendered no trees.
        ".spt"
    };

    /// <summary>
    ///     Extensions the asset INDEX must see but the asset PACKER must not touch. Kept out of
    ///     <see cref="AssetExtensions" /> deliberately.
    ///     <para>
    ///         Starfield's <c>geometries\*.mesh</c> blobs are content-addressed — the path IS a hash of
    ///         the bytes — so they must be readable (the renderer resolves them per BSGeometry block)
    ///         but must never be fed to the collector/renamer, which would rewrite paths that the
    ///         hashes have to match. Without indexing them the viewer resolves every Starfield model to
    ///         "no geometry"; with them in the packer's set, a rename pass would corrupt them.
    ///     </para>
    /// </summary>
    public static readonly HashSet<string> IndexOnlyExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mesh" };

    /// <summary>
    ///     True when <paramref name="extension" /> should appear in a read-side asset index (the union
    ///     of <see cref="AssetExtensions" /> and <see cref="IndexOnlyExtensions" />). Packing and
    ///     renaming keep using <see cref="AssetExtensions" /> alone.
    /// </summary>
    public static bool IsIndexableAsset(string extension) =>
        AssetExtensions.Contains(extension) || IndexOnlyExtensions.Contains(extension);

    public static readonly Dictionary<string, string> ExtensionToPrefix =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".nif"] = "meshes\\",
            [".kf"] = "meshes\\",
            [".egm"] = "meshes\\",
            [".egt"] = "meshes\\",
            [".tri"] = "meshes\\",
            [".psa"] = "meshes\\",
            [".spt"] = "trees\\",
            [".dds"] = "textures\\",
            [".ddx"] = "textures\\",
            [".wav"] = "sound\\",
            [".lip"] = "sound\\",
            [".ogg"] = "sound\\",
            [".xwm"] = "sound\\",
            [".xma"] = "sound\\",
            [".mp3"] = "sound\\",
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

    /// <summary>
    ///     Every Data subtree an asset path can be rooted at, derived from
    ///     <see cref="ExtensionToPrefix" /> plus <c>music\</c> — which no extension maps to,
    ///     because <c>.mp3</c> and <c>.wav</c> both appear under <c>Sound\</c> AND <c>Music\</c>
    ///     and only the owning field can say which.
    /// </summary>
    private static readonly string[] CategoryRoots =
        [.. ExtensionToPrefix.Values.Append("music\\").Distinct(StringComparer.Ordinal)];

    /// <summary>Identifies which Data subtree a normalized path is rooted at.</summary>
    public static bool TryGetCategoryRoot(string normalizedPath, out string root)
    {
        foreach (var candidate in CategoryRoots)
        {
            if (normalizedPath.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
            {
                root = candidate;
                return true;
            }
        }

        root = string.Empty;
        return false;
    }

    /// <summary>
    ///     True when two normalized paths live under the same Data subtree (or both under
    ///     none). Rewriting a field across roots changes what the field means, so callers use
    ///     this to decline such matches.
    /// </summary>
    public static bool SharesCategoryRoot(string a, string b)
    {
        var hasA = TryGetCategoryRoot(a, out var rootA);
        var hasB = TryGetCategoryRoot(b, out var rootB);
        return hasA == hasB && string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Extensions the FNV engine cannot read from inside a BSA, so they have to ship as
    ///     loose files next to the archives.
    ///     <para>
    ///         MP3 is the whole list. Bethesda's own data is the proof, and it is an FO3→FNV
    ///         engine difference: Fallout 3's <c>Fallout - Sound.bsa</c> archives 66 <c>.mp3</c>,
    ///         while FNV's archives ZERO — the same radio songs were re-encoded to <c>.ogg</c>
    ///         for the FNV archive, and all 199 <c>Data\Music\</c> tracks were left loose. The
    ///         GECK wiki states it outright ("MP3 files will not work in Fallout: New Vegas when
    ///         placed inside BSA files. Use OGG/Vorbis instead.").
    ///     </para>
    ///     <para>
    ///         Note this is narrower than the streaming-audio rule that also applies: <c>.wav</c>
    ///         and <c>.ogg</c> work from a BSA but only an UNCOMPRESSED one, which
    ///         <see cref="Bsa.BsaWriter.CreateWithAutoFlags" /> already guarantees for audio
    ///         buckets. MP3 fails regardless of compression, hence loose delivery.
    ///     </para>
    /// </summary>
    private static readonly string[] LooseOnlyExtensions = [".mp3"];

    /// <summary>
    ///     True when an asset must be delivered loose rather than packed into a BSA.
    /// </summary>
    public static bool RequiresLooseDelivery(string path) =>
        LooseOnlyExtensions.Contains(
            Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static readonly string[] PathLikePropertyTokens =
    [
        "Path", "FileName", "Texture", "Model", "Icon", "Mesh"
    ];

    public static readonly string[] DmpScanStrictPrefixes =
    [
        "meshes\\", "textures\\", "sound\\", "music\\", "video\\",
        "data\\meshes\\", "data\\textures\\", "data\\sound\\", "data\\music\\", "data\\video\\"
    ];

    /// <summary>
    ///     Normalizes a raw asset path to a Data-relative, lowercased path rooted at its
    ///     expected category prefix (meshes\, textures\, …), or returns null if the path
    ///     has no recognized asset extension.
    /// </summary>
    /// <param name="rootHint">
    ///     The Data subtree the owning field is interpreted against, when the extension alone
    ///     cannot say. FNV uses <c>.mp3</c> and <c>.wav</c> under BOTH <c>Sound\</c> (SOUN FNAM,
    ///     including the <c>songs\radio\*</c> family) and <c>Music\</c> (MUSC FNAM), so deriving
    ///     the root from the extension mis-roots one of them whichever way the map points.
    /// </param>
    public static string? TryNormalizeRequestPath(string? raw, string? rootHint = null)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var lower = raw.Trim().Replace('/', '\\').ToLowerInvariant();

        // Prototype captures can carry a developer's absolute path (MUSC FNAM values like
        // "D:\Data\Music\endgame\endgame_02.mp3"). Root it at the Data folder so it resolves
        // like any other reference; without this the engine appends the whole drive path to
        // Data\Music\ and the track is silent.
        var dataSegment = lower.LastIndexOf("\\data\\", StringComparison.Ordinal);
        if (dataSegment >= 0)
        {
            lower = lower[(dataSegment + 6)..];
        }
        else if (lower.Length > 2 && lower[1] == ':' && lower[2] == '\\')
        {
            lower = lower[3..];
        }

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

        if (rootHint is not null)
        {
            expectedPrefix = rootHint;
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

    /// <summary>
    ///     Lowercases a path and strips any leading separators and a leading "data\" prefix,
    ///     yielding a plain Data-relative path without re-rooting it to a category prefix.
    /// </summary>
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

    /// <summary>
    ///     True for engine-global character assets shared across all actors (any .kf under
    ///     meshes\characters\, or a skeleton*.nif under the _male/_female/_1stperson folders).
    /// </summary>
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

    /// <summary>
    ///     Computes a loose match key for a filename: the extension-less name lowercased with
    ///     spaces, underscores, hyphens, and apostrophes removed.
    /// </summary>
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

    /// <summary>
    ///     Like <see cref="ComputeLooseBasename" /> but also strips a leading or trailing "nv"
    ///     affix; returns empty if no affix was present so it can't false-match an un-affixed name.
    /// </summary>
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

    /// <summary>
    ///     Gets the expected Data-folder category prefix (e.g. "meshes\", "textures\") for a
    ///     file extension, returning false for unrecognized extensions.
    /// </summary>
    public static bool TryGetExtensionPrefix(string extension, out string prefix)
    {
        return ExtensionToPrefix.TryGetValue(extension, out prefix!);
    }

    /// <summary>
    ///     Yields the same path with each interchangeable-format extension substituted
    ///     (e.g. .wav to .ogg/.xwm, .ddx to .dds), for fallback asset lookups.
    /// </summary>
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

    /// <summary>
    ///     True if the candidate path's extension matches the requested extension or is one of
    ///     its interchangeable-format swaps.
    /// </summary>
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

    /// <summary>
    ///     Converts a normalized asset path back into the shape expected by a record field,
    ///     restoring the "data\" prefix and category-prefix presence to match the original raw value.
    /// </summary>
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

        var originalHadTypePrefix = TryGetCategoryRoot(lowerRaw, out _);

        // Strip the root the path ACTUALLY has, not the one its extension implies. Those two
        // disagree whenever a container appears under more than one subtree — a MUSC `.mp3`
        // resolved under `music\` used to keep that root here (because `.mp3` maps to
        // `sound\`), so the record gained a `music\` prefix retail never has.
        if (!TryGetCategoryRoot(normalizedNewPath, out var newRoot))
        {
            return normalizedNewPath;
        }

        var withoutPrefix = normalizedNewPath[newRoot.Length..];

        if (originalHadTypePrefix)
        {
            return hadDataPrefix
                ? "Data\\" + newRoot + withoutPrefix
                : newRoot + withoutPrefix;
        }

        return hadDataPrefix
            ? "Data\\" + withoutPrefix
            : withoutPrefix;
    }
}
