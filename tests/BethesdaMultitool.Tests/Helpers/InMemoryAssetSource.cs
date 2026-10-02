using System.Runtime.CompilerServices;
using Slfx77.Multitool.Core.Assets;

namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     A read-only <see cref="IAssetSource" /> over byte arrays held in memory, for model-reader tests that need a real
///     <c>ModelSourceItem</c> without touching the file system. Paths are normalized by <see cref="AssetReference" />
///     and compared ordinally; an unknown path or a foreign source id is refused rather than resolved by name.
/// </summary>
internal sealed class InMemoryAssetSource : IAssetSource
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    /// <summary>Creates an empty source.</summary>
    public InMemoryAssetSource(string id = "memory")
    {
        Id = id;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public string DisplayName => Id;

    /// <inheritdoc />
    public StringComparer PathComparer => StringComparer.Ordinal;

    /// <summary>Adds (or replaces) a file and returns its entry.</summary>
    public AssetEntry Add(string path, byte[] bytes)
    {
        var reference = new AssetReference(Id, path);
        _files[reference.Path] = bytes;
        return new AssetEntry(reference, bytes.Length);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AssetEntry> EnumerateAsync(string? prefix = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        foreach (var (path, bytes) in _files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (prefix is null || path.StartsWith(prefix, StringComparison.Ordinal))
            {
                yield return new AssetEntry(new AssetReference(Id, path), bytes.Length);
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<Stream> OpenReadAsync(AssetReference reference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(reference.SourceId, Id, StringComparison.Ordinal) ||
            !_files.TryGetValue(reference.Path, out var bytes))
        {
            throw new FileNotFoundException("The in-memory source has no such asset.", reference.Path);
        }

        return ValueTask.FromResult<Stream>(new MemoryStream(bytes, false));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
