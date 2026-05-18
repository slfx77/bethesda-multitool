using System.Collections;
using System.Reflection;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Reporting;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     Walks a <see cref="RecordCollection" /> and a raw DMP file to gather every
///     referenced asset path (.nif/.dds/.wav/.kf/.egm/.egt/...) that the plugin needs
///     in order to render and play correctly.
///     Output paths are normalized to lowercase + backslash separators, with no
///     leading separator and no "Data\" prefix. This matches BSA-internal convention.
/// </summary>
internal static class AssetPathCollector
{
    /// <summary>
    ///     Collect every asset path referenced by the converted plugin's record collection
    ///     and (optionally) the raw bytes of the source DMP.
    /// </summary>
    public static HashSet<string> Collect(
        RecordCollection records,
        string? dmpFilePath,
        IConversionProgressSink sink)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var beforeRecords = paths.Count;
        ScanRecords(records, paths, null);
        sink.Info("AssetCollect", $"ESP records contributed {paths.Count - beforeRecords} unique asset paths");

        if (dmpFilePath is not null && File.Exists(dmpFilePath))
        {
            var beforeDmp = paths.Count;
            ScanDmpFile(dmpFilePath, paths);
            sink.Info("AssetCollect",
                $"DMP string scan contributed {paths.Count - beforeDmp} additional asset paths");
        }

        var beforeSiblings = paths.Count;
        DeriveNifSiblings(paths);
        sink.Info("AssetCollect",
            $"NIF sibling derivation added {paths.Count - beforeSiblings} EGM/EGT/TRI paths");

        return paths;
    }

    /// <summary>
    ///     Record-only collection that also returns the source-object + property for each
    ///     path discovered. Used by <see cref="AssetPathRewriter" /> to mutate the original
    ///     field when fuzzy resolution decides to remap to a differently-named asset.
    ///     DMP raw-byte scanning is skipped here because DMP-derived paths have no
    ///     record/property anchor — they can't be rewritten.
    /// </summary>
    internal static IReadOnlyList<AssetPathReference> CollectRecordSources(RecordCollection records)
    {
        var sources = new List<AssetPathReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ScanRecords(records, seen, sources);
        return sources;
    }

    // ====================================================================================
    // Record scanning (reflection)
    // ====================================================================================

    /// <summary>
    ///     Walks every public list property on the RecordCollection. For each record in each
    ///     list, scans the record's path-shaped string properties for values that look like
    ///     asset paths (have a known extension). When <paramref name="sources" /> is non-null,
    ///     each path is also recorded as an <see cref="AssetPathReference" /> so a later
    ///     pass can mutate the originating record field.
    /// </summary>
    private static void ScanRecords(
        RecordCollection records,
        HashSet<string> paths,
        List<AssetPathReference>? sources)
    {
        // 1) Explicit dictionary: FormID → model path. Cheapest collection pass.
        //    These paths can't be source-tracked (the dictionary is a projection, not the
        //    canonical owner) — for rewrite purposes the record fields are scanned below.
        foreach (var modelPath in records.ModelPathIndex.Values)
        {
            TryAddPath(modelPath, paths);
        }

        // 2) Reflection over every IEnumerable property on RecordCollection. Skip dictionaries
        //    and primitives. For each enumerated record, scan its path-like string properties.
        foreach (var collectionProp in typeof(RecordCollection).GetProperties(
                     BindingFlags.Public | BindingFlags.Instance))
        {
            if (!typeof(IEnumerable).IsAssignableFrom(collectionProp.PropertyType) ||
                collectionProp.PropertyType == typeof(string))
            {
                continue;
            }

            // Skip the dictionaries — already handled above (ModelPathIndex) or not relevant.
            if (collectionProp.PropertyType.IsGenericType &&
                collectionProp.PropertyType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                continue;
            }

            var enumerable = collectionProp.GetValue(records) as IEnumerable;
            if (enumerable is null)
            {
                continue;
            }

            foreach (var record in enumerable)
            {
                if (record is null)
                {
                    continue;
                }

                ScanRecordObject(record, paths, sources, 0);
            }
        }
    }

    /// <summary>
    ///     Recursively scan a record's string properties (and nested record-like sub-objects)
    ///     for asset paths. Depth-limited to avoid pathological graphs.
    /// </summary>
    private static void ScanRecordObject(
        object record,
        HashSet<string> paths,
        List<AssetPathReference>? sources,
        int depth)
    {
        if (depth > 3)
        {
            return;
        }

        var type = record.GetType();
        var propertyAccessors = TypePathAccessorCache.GetOrAdd(type);

        foreach (var (prop, isPathLike) in propertyAccessors)
        {
            object? value;
            try
            {
                value = prop.GetValue(record);
            }
            catch
            {
                continue;
            }

            if (value is null)
            {
                continue;
            }

            switch (value)
            {
                case string s when isPathLike:
                    if (TryAddPath(s, paths) && sources is not null)
                    {
                        sources.Add(BuildReference(record, prop, s));
                    }

                    break;
                case IEnumerable en when prop.PropertyType != typeof(string):
                    foreach (var item in en)
                    {
                        if (item is null)
                        {
                            continue;
                        }

                        if (item is string itemStr && isPathLike)
                        {
                            // String inside an IEnumerable (e.g. List<string>) — we can detect
                            // the path but can't rewrite it from the list (no settable index).
                            // Skip source-tracking but still gather it for packing.
                            TryAddPath(itemStr, paths);
                        }
                        else if (!item.GetType().IsPrimitive && item is not string)
                        {
                            ScanRecordObject(item, paths, sources, depth + 1);
                        }
                    }

                    break;
                default:
                    // Non-string, non-IEnumerable: if it's a complex type owned by the same
                    // record assembly, descend into it (e.g., a VatsAttackData sub-record).
                    if (!value.GetType().IsPrimitive &&
                        value.GetType().Namespace?.StartsWith(
                            "FalloutXbox360Utils.Core.Formats.Esm.Models",
                            StringComparison.Ordinal) == true)
                    {
                        ScanRecordObject(value, paths, sources, depth + 1);
                    }

                    break;
            }
        }
    }

    /// <summary>
    ///     Build an <see cref="AssetPathReference" /> for a path discovered on a record
    ///     field. Captures the owner object + property + raw value + normalized value.
    /// </summary>
    private static AssetPathReference BuildReference(
        object owner,
        PropertyInfo property,
        string rawPath)
    {
        var normalized = TryNormalizeRequestPath(rawPath) ?? rawPath;
        return new AssetPathReference
        {
            Owner = owner,
            Property = property,
            OriginalRawPath = rawPath,
            NormalizedPath = normalized
        };
    }

    // ====================================================================================
    // DMP raw-string scanning
    // ====================================================================================

    /// <summary>
    ///     Scans the raw bytes of a DMP file for null-terminated ASCII/Latin1 strings
    ///     ending in a known asset extension. Memory-mapped, single pass.
    /// </summary>
    private static void ScanDmpFile(string dmpFilePath, HashSet<string> paths)
    {
        const int MaxLen = 260; // Windows MAX_PATH
        const int ChunkSize = 4 * 1024 * 1024;

        using var stream = File.OpenRead(dmpFilePath);
        var buffer = new byte[ChunkSize + MaxLen];
        var leftover = 0;
        long fileOffset = 0;

        while (true)
        {
            var read = stream.Read(buffer.AsSpan(leftover, ChunkSize));
            if (read == 0 && leftover == 0)
            {
                break;
            }

            var spanLen = leftover + read;
            ScanBufferForStrings(buffer.AsSpan(0, spanLen), paths, read == 0);
            fileOffset += read;

            if (read == 0)
            {
                break;
            }

            // Carry over the tail in case a string straddles the chunk boundary.
            var tail = Math.Min(MaxLen, spanLen);
            Buffer.BlockCopy(buffer, spanLen - tail, buffer, 0, tail);
            leftover = tail;
        }
    }

    /// <summary>
    ///     Walks a buffer and emits every null-terminated printable-ASCII string that ends
    ///     in a known asset extension. Adds matches to <paramref name="paths" />.
    /// </summary>
    private static void ScanBufferForStrings(ReadOnlySpan<byte> buffer, HashSet<string> paths, bool isLastChunk)
    {
        var start = -1;
        for (var i = 0; i < buffer.Length; i++)
        {
            var b = buffer[i];
            if (IsPrintableAsciiPathByte(b))
            {
                if (start < 0)
                {
                    start = i;
                }
            }
            else
            {
                if (start >= 0)
                {
                    var len = i - start;
                    // Only treat as a candidate at a null terminator (consistent with C-string convention)
                    // OR at the end of the buffer when it's the last chunk.
                    if ((b == 0 || (isLastChunk && i == buffer.Length - 1)) && len is >= 6 and <= 260)
                    {
                        TryRecordCandidate(buffer.Slice(start, len), paths);
                    }
                }

                start = -1;
            }
        }
    }

    /// <summary>
    ///     Canonical Data\ subdirectories. A DMP-scanned candidate MUST start with one of
    ///     these (case-insensitively, after stripping <c>data\</c>) — otherwise it's almost
    ///     certainly garbage (single-letter register-save bytes preceding a real path).
    ///     Unlike record-field paths, DMP strings are NOT prefix-inferred because there's
    ///     no way to distinguish an unprefixed real path from byte noise.
    /// </summary>
    private static void TryRecordCandidate(ReadOnlySpan<byte> bytes, HashSet<string> paths)
    {
        // Quick check: must contain a '.' followed by a known extension before the end.
        // Cheaper than allocating a string for every non-path candidate.
        var dotIndex = bytes.LastIndexOf((byte)'.');
        if (dotIndex < 0 || dotIndex >= bytes.Length - 2)
        {
            return;
        }

        var extLen = bytes.Length - dotIndex;
        if (extLen > 6)
        {
            return; // longest known extension is 4 chars + dot
        }

        Span<char> extBuf = stackalloc char[extLen];
        for (var i = 0; i < extLen; i++)
        {
            extBuf[i] = (char)bytes[dotIndex + i];
        }

        if (!AssetPathRules.AssetExtensions.Contains(new string(extBuf)))
        {
            return;
        }

        // DMP-scan guard: only accept candidates that START with a known Data\ subdir.
        // This rejects garbage-prefixed paths like "%characters\..." or "zcharacters\..."
        // that come from register-save bytes living adjacent to a real path string in
        // memory. Record-field paths take a different code path that infers the prefix.
        var firstSlash = bytes.IndexOf((byte)'\\');
        if (firstSlash < 0)
        {
            // forward slash also acceptable (will be normalized later).
            firstSlash = bytes.IndexOf((byte)'/');
            if (firstSlash < 0)
            {
                return;
            }
        }

        if (firstSlash < 4 || firstSlash > 14)
        {
            // Plausible first-segment range: "data" (4) through "lodsettings" (11) plus slop.
            return;
        }

        Span<char> firstSeg = stackalloc char[firstSlash + 1];
        for (var i = 0; i <= firstSlash; i++)
        {
            firstSeg[i] = char.ToLowerInvariant((char)bytes[i]);
        }

        var firstSegStr = new string(firstSeg);
        var hasKnownPrefix = false;
        foreach (var prefix in AssetPathRules.DmpScanStrictPrefixes)
        {
            // Match against just the first segment (everything up to and including the first '\').
            // If the path's first segment matches "<prefix>" (e.g., "meshes\"), accept.
            if (firstSegStr.Equals(prefix, StringComparison.Ordinal) ||
                (prefix.StartsWith("data\\", StringComparison.Ordinal) &&
                 firstSegStr.Equals("data\\", StringComparison.Ordinal)))
            {
                hasKnownPrefix = true;
                break;
            }
        }

        if (!hasKnownPrefix)
        {
            return;
        }

        // Looks like a path. Decode and normalize.
        Span<char> pathBuf = stackalloc char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            pathBuf[i] = (char)bytes[i];
        }

        TryAddPath(new string(pathBuf), paths);
    }

    private static bool IsPrintableAsciiPathByte(byte b)
    {
        // 0x20–0x7E except '\0'. Path chars include letters, digits, '\', '/', '.', '_', '-', ' ', etc.
        return b is >= 0x20 and <= 0x7E;
    }

    // ====================================================================================
    // NIF sibling derivation
    // ====================================================================================

    /// <summary>
    ///     For every facegen-eligible .nif path in the set, add the matching .egm, .egt,
    ///     and .tri siblings in the same directory. FNV loads these automatically only for
    ///     head/face meshes — adding them for every NIF (weapons, ammo, statics, etc.)
    ///     floods the missing-asset list with thousands of bogus entries that no asset on
    ///     disk would ever satisfy.
    /// </summary>
    private static void DeriveNifSiblings(HashSet<string> paths)
    {
        // Snapshot the current set — we're going to add to it.
        var nifPaths = paths
            .Where(p => p.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)
                        && IsFaceGenEligibleNif(p))
            .ToList();

        foreach (var nif in nifPaths)
        {
            var prefix = nif[..^4];
            paths.Add(prefix + ".egm");
            paths.Add(prefix + ".egt");
            paths.Add(prefix + ".tri");
        }
    }

    /// <summary>
    ///     Heuristic: a .nif path is "facegen-eligible" (has .egm/.egt/.tri siblings) when
    ///     it lives under one of the *specific* directories the engine loads facegen data
    ///     for. Inspecting FNV PC Data the actual locations are limited to:
    ///     <list type="bullet">
    ///         <item>
    ///             <description><c>meshes\characters\head\</c> — race head meshes (headhuman, headold, eyelefthuman, …)</description>
    ///         </item>
    ///         <item>
    ///             <description><c>meshes\armor\headgear\…\</c> — hat/helmet face-morph data</description>
    ///         </item>
    ///         <item>
    ///             <description><c>meshes\dlc*\armor\</c> — DLC headgear variants</description>
    ///         </item>
    ///     </list>
    ///     Notably <c>meshes\creatures\</c> never has facegen siblings (brahmin skeletons,
    ///     gecko skeletons, etc. are 4-legged body meshes, no face), and generic body NIFs
    ///     under <c>meshes\characters\</c> don't either. Earlier passes that auto-derived
    ///     siblings for everything under <c>characters\</c> / <c>creatures\</c> flooded the
    ///     missing-asset audit with thousands of bogus entries.
    /// </summary>
    private static bool IsFaceGenEligibleNif(string normalizedPath)
    {
        return normalizedPath.Contains("\\head\\", StringComparison.OrdinalIgnoreCase)
               || normalizedPath.Contains("\\headgear\\", StringComparison.OrdinalIgnoreCase)
               || normalizedPath.Contains("\\facegen\\", StringComparison.OrdinalIgnoreCase);
    }

    // ====================================================================================
    // Path normalization
    // ====================================================================================

    /// <summary>
    ///     Add a candidate path to the set after normalization. Returns true if added,
    ///     false if filtered out (wrong extension, basename-only, no inferable prefix, …).
    /// </summary>
    private static bool TryAddPath(string? raw, HashSet<string> paths)
    {
        var normalized = TryNormalizeRequestPath(raw);
        return normalized is not null
               && !IsEngineGlobalCharacterAsset(normalized)
               && !AssetPathRules.IsTerrainBoundLodAsset(normalized)
               && paths.Add(normalized);
    }

    /// <summary>
    ///     Do not pack engine-global human skeleton or KF animation files from a DMP.
    ///     These paths are shared by the player and all human actors; when an output BSA
    ///     supplies Xbox/prototype bytes at the vanilla path, the PC animation graph can
    ///     fall back to a bind/A pose for every human actor.
    /// </summary>
    internal static bool IsEngineGlobalCharacterAsset(string normalizedPath)
    {
        return AssetPathRules.IsEngineGlobalCharacterAsset(normalizedPath);
    }

    /// <summary>
    ///     Normalize a raw path string captured from a record field or DMP byte scan into
    ///     a runtime-queryable BSA-internal path (full path relative to <c>Data\</c>).
    ///     The asset type is decided by the file extension (<c>.nif</c> → <c>meshes\</c>,
    ///     <c>.dds</c> → <c>textures\</c>, …). Two cases:
    ///     <list type="number">
    ///         <item>
    ///             <description>
    ///                 <b>String already contains its expected prefix</b> — common in DMP raw-byte scans, where the
    ///                 runtime cached the full path. Anything before the expected prefix is garbage (printf format bytes,
    ///                 register saves bleeding into the string) and gets trimmed.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <b>String lacks the expected prefix</b> — common in record fields like
    ///                 <c>WeaponRecord.ModelPath</c> that store paths relative to the asset-type folder. Prepend the expected
    ///                 prefix.
    ///             </description>
    ///         </item>
    ///     </list>
    ///     Basename-only paths (no directory component) are rejected as too ambiguous.
    /// </summary>
    /// <returns>Canonical normalized path, or null if the input can't be made into a valid asset reference.</returns>
    internal static string? TryNormalizeRequestPath(string? raw)
    {
        return AssetPathRules.TryNormalizeRequestPath(raw);
    }

    /// <summary>
    ///     Normalize a path that we KNOW is already fully qualified (BSA-internal record
    ///     paths, loose-file paths relative to Data\). No prefix inference; just lowercase
    ///     + backslash + strip leading separators + drop optional <c>data\</c>.
    /// </summary>
    internal static string NormalizePath(string raw)
    {
        return AssetPathRules.NormalizeDataRelativePath(raw);
    }

    /// <summary>
    ///     Per-type cache of (property, isPathLike) tuples. Avoids re-running the
    ///     name-token check on every record instance.
    /// </summary>
    private static class TypePathAccessorCache
    {
        private static readonly Dictionary<Type, (PropertyInfo Prop, bool IsPathLike)[]> Cache = new();

        public static (PropertyInfo Prop, bool IsPathLike)[] GetOrAdd(Type type)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(type, out var existing))
                {
                    return existing;
                }

                var properties = type
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanRead)
                    .Select(p => (Prop: p, IsPathLike: IsPathLikeProperty(p.Name)))
                    .ToArray();

                Cache[type] = properties;
                return properties;
            }
        }

        private static bool IsPathLikeProperty(string name)
        {
            foreach (var token in AssetPathRules.PathLikePropertyTokens)
            {
                if (name.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
