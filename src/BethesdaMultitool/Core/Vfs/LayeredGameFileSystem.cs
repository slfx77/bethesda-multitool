namespace BethesdaMultitool.Core.Vfs;

/// <summary>
///     Ordered first-hit-wins layering of <see cref="IGameFileSystem" /> instances — the
///     engine-faithful composition: loose files first (runtime override rules), then BSAs in
///     alphabetical filename order (the FNV <c>SArchiveList</c> convention already mirrored by
///     <c>DataFolderIndex</c>), then BA2s. Build one via
///     <see cref="GameFileSystem.OpenDataFolder" />. Reads inherit each layer's lock-free
///     concurrency; the layer list is immutable after construction.
/// </summary>
public sealed class LayeredGameFileSystem : IGameFileSystem
{
    private readonly bool _ownsLayers;

    /// <summary>Creates a layered VFS over the given layers.</summary>
    /// <param name="layers">Resolution order: earlier layers shadow later ones.</param>
    /// <param name="ownsLayers">When true (default), disposing this disposes every layer.</param>
    public LayeredGameFileSystem(IReadOnlyList<IGameFileSystem> layers, bool ownsLayers = true)
    {
        Layers = layers;
        _ownsLayers = ownsLayers;
        Label = $"layered[{string.Join(" > ", layers.Select(l => Path.GetFileName(l.Label.TrimEnd('\\', '/'))))}]";
    }

    /// <summary>The layers in resolution order (diagnostics / format-specific escape hatch).</summary>
    public IReadOnlyList<IGameFileSystem> Layers { get; }

    public string Label { get; }

    public bool Exists(string path)
    {
        return Layers.Any(l => l.Exists(path));
    }

    public GameFileEntry? TryStat(string path)
    {
        return Layers.Select(l => l.TryStat(path)).FirstOrDefault(e => e is not null);
    }

    public byte[]? TryReadAllBytes(string path)
    {
        foreach (var layer in Layers)
        {
            // Try-read-then-fall-through: a layer that has the path but cannot extract it
            // (corrupt entry, locked loose file) returns null and the next layer's copy wins —
            // the same per-source tolerance as the hand-rolled chains this replaces. For
            // readable files this is still first-hit-wins; note Exists(path) can therefore be
            // true while the read comes from (or falls through past) a different layer than
            // TryStat reports.
            if (layer.TryReadAllBytes(path) is { } bytes)
            {
                return bytes;
            }
        }

        return null;
    }

    /// <summary>Enumerates the union; a path appearing in multiple layers yields only the winning copy.</summary>
    public IEnumerable<GameFileEntry> EnumerateFiles(string? prefix = null)
    {
        var seen = new HashSet<string>(VfsPath.Comparer);
        foreach (var layer in Layers)
        {
            foreach (var entry in layer.EnumerateFiles(prefix))
            {
                if (seen.Add(entry.Path))
                {
                    yield return entry;
                }
            }
        }
    }

    public void Dispose()
    {
        if (!_ownsLayers)
        {
            return;
        }

        foreach (var layer in Layers)
        {
            layer.Dispose();
        }
    }

    /// <summary>
    ///     Reads the first available candidate while preserving layer precedence: each layer is
    ///     tried against the candidates, in caller order, before resolution moves to the next layer.
    ///     This is distinct from calling <see cref="TryReadAllBytes(string)" /> once per candidate,
    ///     which is path-major and can let a later archive beat an earlier layer merely because it
    ///     contains the caller's preferred spelling.
    /// </summary>
    public byte[]? TryReadFirstAvailable(IReadOnlyList<string> candidatePaths)
    {
        foreach (var layer in Layers)
        {
            foreach (var path in candidatePaths)
            {
                if (layer.TryReadAllBytes(path) is { } bytes)
                {
                    return bytes;
                }
            }
        }

        return null;
    }
}
