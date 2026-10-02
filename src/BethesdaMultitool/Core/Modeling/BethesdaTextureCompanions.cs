using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.Vfs;
using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling;

/// <summary>
///     BMT's texture companion resolver (plan section 4, "Resolution"): an <see cref="IAssetSource" /> over a Bethesda
///     virtual file system (<see cref="GameFileSystem.OpenDataFolder" /> per data root: loose files shadow the archives,
///     archives in the engine's order) whose <see cref="ResolveAsync" /> is the <see cref="ModelCompanionResolver" /> the
///     model workflow hands to the Shared operations.
/// </summary>
/// <remarks>
///     <para>
///         Resolution normalizes the authored path with <see cref="NifTexturePathUtility.Normalize" /> (a lookup key only;
///         the reader keeps the authored string), looks it up with <see cref="IGameFileSystem.TryStat" /> without reading
///         it, and, when a <c>.dds</c> path is absent everywhere, falls back to the same path with <c>.ddx</c> (the Xbox
///         engine's spelling). It returns at most one occurrence: the layered file system resolves one winner. The
///         occurrence id binds the reference to that path and layer, and one path in one layer always gets the same
///         occurrence, so two authored spellings of one file (<c>a.dds</c> falling back to <c>a.ddx</c>, and
///         <c>a.ddx</c>) share one reference and one read.
///     </para>
///     <para>
///         Bytes are read only when an occurrence is opened, bounded by the texture limit, and an open refuses bytes
///         supplied by any layer other than the one resolution named, so the provenance is always the layer that
///         supplied the bytes. Nothing is retained between resolution and opening: a resolved texture that is never
///         opened (refused by a budget, ambiguous, or superseded) costs no memory.
///     </para>
///     <para>
///         Ownership: the caller owns this source and keeps it alive until the operation and every returned handle are
///         finished (<see cref="ModelCompanionResolver" />'s contract).
///     </para>
/// </remarks>
public sealed class BethesdaTextureCompanions : IAssetSource
{
    /// <summary>The default largest texture the resolver reads.</summary>
    public const long DefaultMaximumTextureBytes = 256L * 1024 * 1024;

    private readonly IGameFileSystem _fileSystem;
    private readonly ConcurrentDictionary<string, (string Occurrence, string Path)> _byPathAndLayer =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string Path, string Layer)> _issued = new(StringComparer.Ordinal);
    private readonly long _maximumTextureBytes;
    private readonly bool _ownsFileSystem;
    private long _nextOccurrence;

    /// <summary>Creates a resolver over an already opened file system.</summary>
    /// <param name="fileSystem">The layered game file system to resolve against.</param>
    /// <param name="displayName">The user-facing source name.</param>
    /// <param name="ownsFileSystem">Whether disposing this source disposes the file system.</param>
    /// <param name="maximumTextureBytes">The largest texture read (bounded before allocation).</param>
    public BethesdaTextureCompanions(IGameFileSystem fileSystem, string displayName, bool ownsFileSystem = true,
        long maximumTextureBytes = DefaultMaximumTextureBytes)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumTextureBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumTextureBytes, int.MaxValue);
        _fileSystem = fileSystem;
        _ownsFileSystem = ownsFileSystem;
        _maximumTextureBytes = maximumTextureBytes;
        DisplayName = displayName;
        Id = "bmt-textures-" + Guid.NewGuid().ToString("N");
    }

    /// <summary>The data roots this source was opened over, in precedence order (empty for an explicit file system).</summary>
    public IReadOnlyList<string> DataRoots { get; private init; } = [];

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <inheritdoc />
    public StringComparer PathComparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>
    ///     Opens one or more Bethesda Data folders, earlier roots first; each root mounts its loose tree over its archives.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">A root does not exist.</exception>
    public static BethesdaTextureCompanions OpenDataRoots(IReadOnlyList<string> dataRoots,
        long maximumTextureBytes = DefaultMaximumTextureBytes)
    {
        ArgumentNullException.ThrowIfNull(dataRoots);
        if (dataRoots.Count == 0)
        {
            throw new ArgumentException("At least one data root is required.", nameof(dataRoots));
        }

        var roots = new List<string>(dataRoots.Count);
        foreach (var root in dataRoots)
        {
            var full = Path.GetFullPath(root);
            if (!Directory.Exists(full))
            {
                throw new DirectoryNotFoundException($"The data root does not exist: {root}");
            }

            roots.Add(full);
        }

        var layers = new List<IGameFileSystem>(roots.Count);
        try
        {
            foreach (var root in roots)
            {
                layers.Add(GameFileSystem.OpenDataFolder(root));
            }
        }
        catch
        {
            foreach (var layer in layers)
            {
                layer.Dispose();
            }

            throw;
        }

        IGameFileSystem fileSystem = layers.Count == 1 ? layers[0] : new LayeredGameFileSystem(layers);
        return new BethesdaTextureCompanions(fileSystem, string.Join(" > ", roots), true, maximumTextureBytes)
        {
            DataRoots = roots.AsReadOnly()
        };
    }

    /// <summary>
    ///     Infers a Data root by walking up from a model file (or from a directory itself) to the first folder holding a
    ///     <c>textures</c> folder, as the legacy NIF export does (<c>NifExportPathResolver.TryDetectDataRoot</c>).
    /// </summary>
    public static bool TryInferDataRoot(string inputPath, out string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        var full = Path.GetFullPath(inputPath);
        var current = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (Directory.Exists(Path.Combine(current, "textures")))
            {
                dataRoot = current;
                return true;
            }

            current = Path.GetDirectoryName(current);
        }

        dataRoot = string.Empty;
        return false;
    }

    /// <summary>
    ///     Resolves an authored texture path (the <see cref="ModelCompanionResolver" /> contract): zero or one occurrence
    ///     of this source. Names that cannot form a virtual path resolve to nothing rather than throwing.
    /// </summary>
    public ValueTask<IReadOnlyList<ModelSourceItem>> ResolveAsync(ModelSourceItem owner, string name,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(name))
        {
            return ValueTask.FromResult<IReadOnlyList<ModelSourceItem>>([]);
        }

        foreach (var candidate in Candidates(name))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path;
            try
            {
                path = AssetPath.Normalize(candidate);
            }
            catch (ArgumentException)
            {
                return ValueTask.FromResult<IReadOnlyList<ModelSourceItem>>([]);
            }

            if (_fileSystem.TryStat(path) is not { } stat)
            {
                continue;
            }

            var issued = _byPathAndLayer.GetOrAdd(path + "|" + stat.Source, _ =>
                (Interlocked.Increment(ref _nextOccurrence).ToString(CultureInfo.InvariantCulture), path));
            var reference = new AssetReference(Id, issued.Path, issued.Occurrence);
            _issued.TryAdd(issued.Occurrence, (reference.Path, stat.Source));
            var entry = new AssetEntry(reference, stat.Size, Provenance: stat.Source);
            return ValueTask.FromResult<IReadOnlyList<ModelSourceItem>>([new ModelSourceItem(this, entry)]);
        }

        return ValueTask.FromResult<IReadOnlyList<ModelSourceItem>>([]);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AssetEntry> EnumerateAsync(string? prefix = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        foreach (var entry in _fileSystem.EnumerateFiles(prefix))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new AssetEntry(new AssetReference(Id, entry.Path), entry.Size, Provenance: entry.Source);
        }
    }

    /// <inheritdoc />
    public ValueTask<Stream> OpenReadAsync(AssetReference reference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(reference.SourceId, Id, StringComparison.Ordinal))
        {
            throw new ArgumentException("The asset belongs to a different source.", nameof(reference));
        }

        if (reference.OccurrenceId is { } occurrence)
        {
            if (!_issued.TryGetValue(occurrence, out var issued) ||
                !PathComparer.Equals(issued.Path, reference.Path))
            {
                throw new ArgumentException("The occurrence was not issued by this source.", nameof(reference));
            }

            var read = Read(reference.Path);
            if (!string.Equals(read.Entry.Source, issued.Layer, StringComparison.Ordinal))
            {
                throw new IOException(
                    $"The texture '{reference.Path}' resolved from '{issued.Layer}' but reads from '{read.Entry.Source}'.");
            }

            return ValueTask.FromResult<Stream>(new MemoryStream(read.Data, false));
        }

        return ValueTask.FromResult<Stream>(new MemoryStream(Read(reference.Path).Data, false));
    }

    /// <summary>Forgets the issued occurrences and, when owned, releases the file system.</summary>
    public ValueTask DisposeAsync()
    {
        _byPathAndLayer.Clear();
        _issued.Clear();
        if (_ownsFileSystem)
        {
            _fileSystem.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Reads one texture within the limit.</summary>
    /// <exception cref="FileNotFoundException">The texture is absent, unreadable or larger than the limit.</exception>
    private GameFileReadResult Read(string path)
    {
        return _fileSystem.TryReadAllBytesBounded(path, _maximumTextureBytes)
               ?? throw new FileNotFoundException(string.Create(CultureInfo.InvariantCulture,
                   $"The texture could not be read within the {_maximumTextureBytes}-byte limit (absent, unreadable or larger)."),
                   path);
    }

    /// <summary>The lookup key, then its <c>.ddx</c> fallback when the key names a <c>.dds</c>.</summary>
    private static IEnumerable<string> Candidates(string name)
    {
        var key = NifTexturePathUtility.Normalize(name);
        yield return key;
        if (key.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            yield return key[..^4] + ".ddx";
        }
    }
}
