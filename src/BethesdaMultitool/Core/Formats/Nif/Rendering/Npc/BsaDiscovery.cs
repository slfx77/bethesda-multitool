using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using BethesdaMultitool.Core.Formats.Bsa.Ba2;
using BethesdaMultitool.Core.Formats.Bsa.Models;
using BethesdaMultitool.Core.Formats.Bsa.Parsing;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;

/// <summary>
///     Auto-detects BSA files (meshes + textures) in an ESM file's directory. Classifies each archive
///     by its <see cref="BsaFileFlags" /> content bits — the same mechanism the engine uses — rather
///     than by filename, so a mod that packs everything into one <c>&lt;Mod&gt; - Main.bsa</c> (which
///     matches neither <c>*Meshes*.bsa</c> nor <c>*Texture*.bsa</c>) is still found and contributes to
///     both the mesh set and the texture set.
/// </summary>
internal static class BsaDiscovery
{
    private const int MaximumCachedDirectories = 32;

    private static readonly StringComparer DirectoryComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly ConcurrentDictionary<string, DiscoveryCacheSlot> DirectoryCache =
        new(DirectoryComparer);

    private static readonly object CacheMaintenanceGate = new();

    internal static BsaDiscoveryResult Discover(string esmPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(esmPath));
        return dir == null ? BsaDiscoveryResult.Empty : DiscoverInDirectory(dir);
    }

    /// <summary>
    ///     Content-classifies every archive in <paramref name="dir" /> directly. Entry point for
    ///     callers that start from a directory or an archive path rather than an ESM (e.g. the
    ///     NifConverter tab's texture auto-detection).
    /// </summary>
    internal static BsaDiscoveryResult DiscoverInDirectory(string dir)
    {
        return DiscoverInDirectoryCore(dir, knownArchive: null);
    }

    /// <summary>
    ///     Content-classifies a directory while trusting content flags derived from one already-open
    ///     archive. The selected container is not parsed or streamed a second time, but the returned
    ///     result is the same full-directory shape and populates the ordinary directory cache so a
    ///     later selection of another sibling can reuse it.
    /// </summary>
    internal static BsaDiscoveryResult DiscoverInDirectoryWithKnownArchive(
        string dir,
        string selectedArchivePath,
        bool selectedHasMeshes,
        bool selectedHasTextures)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedArchivePath);
        return DiscoverInDirectoryCore(
            dir,
            new KnownArchiveClassification(
                Path.GetFullPath(selectedArchivePath),
                selectedHasMeshes,
                selectedHasTextures));
    }

    private static BsaDiscoveryResult DiscoverInDirectoryCore(
        string dir,
        KnownArchiveClassification? knownArchive)
    {
        if (!Directory.Exists(dir))
        {
            return BsaDiscoveryResult.Empty;
        }

        dir = Path.GetFullPath(dir);
        var allBsaPaths = Directory.GetFiles(dir, "*.bsa")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var allBa2Paths = Directory.GetFiles(dir, "*.ba2")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (allBsaPaths.Length == 0 && allBa2Paths.Length == 0)
        {
            // A game-install ROOT is a natural directory to hand this API (asset-donor dirs get
            // configured that way), and its archives live one level down in Data\. Falling through
            // to that matters more than it looks: a donor root that silently discovers zero
            // archives degrades every render that depended on the donor with no individual image
            // looking obviously wrong — the FO3 and FNV-final corpus donors shipped exactly this
            // way and contributed nothing.
            var dataDir = Path.Combine(dir, "Data");
            return Directory.Exists(dataDir)
                ? DiscoverInDirectoryCore(dataDir, knownArchive)
                : BsaDiscoveryResult.Empty;
        }

        var identity = allBsaPaths
            .Concat(allBa2Paths)
            .Select(static path =>
            {
                var file = new FileInfo(path);
                return new ArchiveFileIdentity(
                    Path.GetFullPath(path),
                    file.Length,
                    file.LastWriteTimeUtc.Ticks);
            })
            .ToArray();
        var cacheSlot = GetCacheSlot(dir);
        lock (cacheSlot.Gate)
        {
            if (cacheSlot.Identity is { } cachedIdentity &&
                cachedIdentity.AsSpan().SequenceEqual(identity) &&
                cacheSlot.Result is { } cachedResult)
            {
                return cachedResult;
            }

            var result = ClassifyArchives(allBsaPaths, allBa2Paths, knownArchive);
            cacheSlot.Identity = identity;
            cacheSlot.Result = result;
            return result;
        }
    }

    private static BsaDiscoveryResult ClassifyArchives(
        IReadOnlyList<string> bsaPaths,
        IReadOnlyList<string> ba2Paths,
        KnownArchiveClassification? knownArchive)
    {
        var meshes = new List<string>();
        var textures = new List<string>();
        foreach (var path in bsaPaths)
        {
            var classification = TryGetKnownClassification(path, knownArchive, out var known)
                ? known
                : ClassifyContent(path);
            AddClassification(path, classification, meshes, textures);
        }

        // BA2 (Fallout 4 / Fallout 76). DX10 archives hold textures only; GNRL archives hold
        // meshes/materials/etc. Both the texture path (NifTextureArchiveSourceFactory) and the mesh
        // path (MeshArchiveSet) are now BA2-aware, so classify by content and route accordingly.
        // DX10 → textures by definition. GNRL → scan its name-table paths for meshes\/textures\
        // prefixes (BA2 has no BSA-style content-flag bits).
        foreach (var path in ba2Paths)
        {
            if (TryGetKnownClassification(path, knownArchive, out var known))
            {
                AddClassification(path, known, meshes, textures);
                continue;
            }

            var header = Ba2Parser.TryReadHeader(path);
            if (header is null)
            {
                continue;
            }

            if (header.Type == Ba2HeaderType.Texture)
            {
                textures.Add(path);
                continue;
            }

            var (hasMeshes, hasTextures) = ClassifyBa2GeneralContent(path);
            AddClassification(path, (hasMeshes, hasTextures), meshes, textures);
        }

        if (meshes.Count == 0 && textures.Count == 0)
        {
            return BsaDiscoveryResult.Empty;
        }

        return new BsaDiscoveryResult(meshes.ToArray(), textures.ToArray(), true);
    }

    private static bool TryGetKnownClassification(
        string path,
        KnownArchiveClassification? knownArchive,
        out (bool Meshes, bool Textures) classification)
    {
        if (knownArchive is { } known &&
            DirectoryComparer.Equals(Path.GetFullPath(path), known.Path))
        {
            classification = (known.Meshes, known.Textures);
            return true;
        }

        classification = default;
        return false;
    }

    private static void AddClassification(
        string path,
        (bool Meshes, bool Textures) classification,
        List<string> meshes,
        List<string> textures)
    {
        if (classification.Meshes)
        {
            meshes.Add(path);
        }

        if (classification.Textures)
        {
            textures.Add(path);
        }
    }

    private static DiscoveryCacheSlot GetCacheSlot(string cacheKey)
    {
        if (!DirectoryCache.ContainsKey(cacheKey) && DirectoryCache.Count >= MaximumCachedDirectories)
        {
            // Discovery normally touches only one installed Data directory per open document, but
            // bound the process cache for long-running automation that walks many temporary games.
            lock (CacheMaintenanceGate)
            {
                if (DirectoryCache.Count >= MaximumCachedDirectories)
                {
                    foreach (var existing in DirectoryCache.Keys)
                    {
                        if (!DirectoryComparer.Equals(existing, cacheKey) &&
                            DirectoryCache.TryRemove(existing, out _))
                        {
                            break;
                        }
                    }
                }
            }
        }

        return DirectoryCache.GetOrAdd(cacheKey, static _ => new DiscoveryCacheSlot());
    }

    private readonly record struct ArchiveFileIdentity(
        string Path,
        long Length,
        long LastWriteTimeUtcTicks);

    private readonly record struct KnownArchiveClassification(
        string Path,
        bool Meshes,
        bool Textures);

    private sealed class DiscoveryCacheSlot
    {
        internal object Gate { get; } = new();

        internal ArchiveFileIdentity[]? Identity { get; set; }

        internal BsaDiscoveryResult? Result { get; set; }
    }

    /// <summary>
    ///     Classifies an archive by its header <see cref="BsaFileFlags" /> (cheap, header-only read).
    ///     Falls back to inspecting top-level folder names only when the flags are unset (some
    ///     hand-built archives ship with <see cref="BsaFileFlags.None" />).
    /// </summary>
    private static (bool Meshes, bool Textures) ClassifyContent(string bsaPath)
    {
        var header = BsaParser.TryReadHeader(bsaPath);
        if (header is null)
        {
            return (false, false);
        }

        var hasMeshes = header.FileFlags.HasFlag(BsaFileFlags.Meshes);
        var hasTextures = header.FileFlags.HasFlag(BsaFileFlags.Textures);
        if (hasMeshes || hasTextures || header.FileFlags != BsaFileFlags.None)
        {
            return (hasMeshes, hasTextures);
        }

        // FileFlags unset — inspect folder names (requires IncludeDirectoryNames, which every FNV/FO3
        // BSA sets). A full parse is acceptable here because this path is rare.
        try
        {
            var archive = BsaParser.Parse(bsaPath);
            foreach (var folder in archive.Folders)
            {
                if (folder.Name is not { } name)
                {
                    continue;
                }

                if (name.StartsWith("meshes", StringComparison.OrdinalIgnoreCase))
                {
                    hasMeshes = true;
                }
                else if (name.StartsWith("textures", StringComparison.OrdinalIgnoreCase))
                {
                    hasTextures = true;
                }

                if (hasMeshes && hasTextures)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            // Unreadable/corrupt archive — leave unclassified.
        }

        return (hasMeshes, hasTextures);
    }

    /// <summary>
    ///     Classifies a GNRL (general) BA2 by scanning its name-table paths for <c>meshes\</c> /
    ///     <c>textures\</c> / <c>materials\</c> prefixes. BA2 has no BSA-style content-flag bits, so this
    ///     is the only signal. A <c>materials\</c> archive (Fallout 4 / 76 ship a <c>… - Materials.ba2</c>
    ///     full of <c>.bgsm</c>/<c>.bgem</c>) counts as a TEXTURE source: a FO4/76 NIF's
    ///     BSLightingShaderProperty Name is a <c>.bgsm</c> path, and the texture resolver follows it
    ///     through the material file to the real textures (<c>NifTextureResolver.LoadFromMaterial</c>), so
    ///     the materials archive must reach the resolver's source set — without it every FO76 shape
    ///     resolves no diffuse and the whole world renders untextured. A GNRL BA2 with no usable name
    ///     table (hash-only paths) classifies as neither — it can't be path-resolved anyway.
    /// </summary>
    private static (bool Meshes, bool Textures) ClassifyBa2GeneralContent(string ba2Path)
    {
        try
        {
            // Classification needs only the trailing path table. Ba2Parser.Parse first materializes
            // every 36-byte GNRL record and every name into a retained object graph; on a current
            // Starfield install, archive discovery would do that for more than 1.6 million entries
            // even though those graphs are thrown away immediately. Seek straight to the name table
            // and stream it instead. This preserves content-based classification for oddly named mod
            // archives while keeping application startup proportional to path bytes, not record
            // allocations.
            using var stream = new FileStream(
                ba2Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            var header = Ba2Header.Read(reader);
            if (header.Type != Ba2HeaderType.General || !header.HasNameTable)
            {
                return (false, false);
            }

            stream.Seek(checked((long)header.NameTableOffset), SeekOrigin.Begin);
            var hasMeshes = false;
            var hasTextures = false;
            var nameBuffer = ArrayPool<byte>.Shared.Rent(ushort.MaxValue);
            try
            {
                for (uint index = 0; index < header.FileCount; index++)
                {
                    var length = reader.ReadUInt16();
                    var nameBytes = nameBuffer.AsSpan(0, length);
                    stream.ReadExactly(nameBytes);

                    // Prefixes are ASCII even though the rest of a BA2 path is UTF-8. Compare the
                    // raw bytes so a textures-only Starfield archive can scan hundreds of thousands
                    // of names without allocating a byte[] and string for every discarded path.
                    // "geometries\" counts as mesh content: Starfield splits vertex/index buffers
                    // out of its NIFs into hash-named blobs under that root.
                    if (HasBa2Root(nameBytes, "meshes"u8) ||
                        HasBa2Root(nameBytes, "geometries"u8))
                    {
                        hasMeshes = true;
                    }
                    else if (HasBa2Root(nameBytes, "textures"u8) ||
                             HasBa2Root(nameBytes, "materials"u8))
                    {
                        hasTextures = true;
                    }

                    if (hasMeshes && hasTextures)
                    {
                        break;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(nameBuffer);
            }

            return (hasMeshes, hasTextures);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or
                                   UnauthorizedAccessException or OverflowException)
        {
            return (false, false);
        }
    }

    private static bool HasBa2Root(ReadOnlySpan<byte> path, ReadOnlySpan<byte> root)
    {
        if (path.Length <= root.Length)
        {
            return false;
        }

        var separator = path[root.Length];
        if (separator != (byte)'/' && separator != (byte)'\\')
        {
            return false;
        }

        for (var index = 0; index < root.Length; index++)
        {
            var value = path[index];
            if (value is >= (byte)'A' and <= (byte)'Z')
            {
                value += (byte)('a' - 'A');
            }

            if (value != root[index])
            {
                return false;
            }
        }

        return true;
    }
}
