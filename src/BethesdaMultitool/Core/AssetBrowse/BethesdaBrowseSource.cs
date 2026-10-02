using System.Runtime.CompilerServices;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Vfs;
using Slfx77.Multitool.Core.Assets;

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>
///     Adapts the Bethesda virtual filesystem to a leased shared browser source. It also answers the classic container
///     facts the XnGine model readers ask of their item's source (<see cref="IClassicContainerFactsSource" />; cut-1c
///     plan section 2), and it reports no declared length for a Battlespire LZSS entry: the archive stores only the
///     compressed size, and <see cref="AssetEntry.Length" /> is documented as the known DECODED length (for example
///     ARMOR.3D in 3D.BSA is stored at 13,999 bytes and decodes to 34,556).
/// </summary>
internal sealed class BethesdaBrowseSource : IAssetSource, IClassicContainerFactsSource
{
    /// <summary>Takes ownership of an already opened asset session.</summary>
    internal BethesdaBrowseSource(AssetBrowseSession session)
    {
        Session = session;
        Id = Guid.NewGuid().ToString("N");
    }

    /// <summary>Gets the native asset session whose lifetime is owned by this shared source.</summary>
    internal AssetBrowseSession Session { get; }
    /// <summary>Gets the unique identity for this opening, preventing paths from a retired opening being reused.</summary>
    public string Id { get; }
    /// <summary>Returns the native asset session's user-facing source label.</summary>
    public string DisplayName => Session.SourceLabel;
    /// <summary>Compares Bethesda virtual paths ordinally without case, matching the native filesystem resolver.</summary>
    public StringComparer PathComparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>Opens the asset side of a previously resolved source selection.</summary>
    internal static BethesdaBrowseSource Open(ExploreSourcePlan plan)
    {
        var session = plan.AssetKind switch
        {
            ExploreAssetSourceKind.Archive => AssetBrowseSession.TryOpenGameArchive(plan.AssetPath)
                ?? AssetBrowseSession.OpenArchive(plan.AssetPath),
            ExploreAssetSourceKind.DataDirectory => OpenDataDirectory(plan.AssetPath),
            _ => AssetBrowseSession.TryOpenGameRoot(plan.AssetPath) ?? AssetBrowseSession.OpenFolder(plan.AssetPath)
        };
        return new BethesdaBrowseSource(session);
    }

    /// <summary>Builds the asset tree over the existing loose/archive precedence policy.</summary>
    private static AssetBrowseSession OpenDataDirectory(string directory)
    {
        var filesystem = GameFileSystem.OpenDataFolder(directory);
        try
        {
            var label = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
            return new AssetBrowseSession(filesystem, label, directory, AssetTreeBuilder.Build(filesystem, label));
        }
        catch
        {
            filesystem.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     Enumerates virtual entries without decoding their payloads. An entry of an XnGine BSA layer that is stored
    ///     LZSS-compressed gets a null length (its decoded length is unknown until extraction); every other entry keeps
    ///     the size the filesystem reports.
    /// </summary>
    public async IAsyncEnumerable<AssetEntry> EnumerateAsync(string? prefix = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // One reader lookup per source label per enumeration: the label is the layer's archive path, so a
        // whole archive's entries share it and the layer walk runs once for them.
        var readers = new Dictionary<string, ArchiveReader?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Session.FileSystem.EnumerateFiles(prefix))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new AssetEntry(new AssetReference(Id, entry.Path), DecodedLength(entry, readers),
                Provenance: entry.Source);
        }
        await Task.CompletedTask;
    }

    /// <summary>Returns detached entry bytes; a reference from another source is rejected.</summary>
    public ValueTask<Stream> OpenReadAsync(AssetReference reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(reference.SourceId, Id, StringComparison.Ordinal))
            throw new ArgumentException("The asset belongs to a different source.", nameof(reference));
        var bytes = Session.FileSystem.TryReadAllBytes(reference.Path)
            ?? throw new FileNotFoundException("The asset could not be read.", reference.Path);
        return ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    /// <summary>
    ///     The classic container facts of one entry: the layer that wins the path is unwrapped to its archive reader,
    ///     and an XnGine BSA or ROB record yields the facts (<see cref="ClassicContainerFacts.TryDescribe" />). Null
    ///     for a loose file, a Gamebryo BSA or BA2 entry, or an absent path; a reference from another source is rejected.
    /// </summary>
    /// <remarks>
    ///     Resolves by the reference's path exactly as <see cref="OpenReadAsync" /> does, so the facts describe the same
    ///     copy the bytes come from. When that method gains the occurrence-token rule Shared's <c>IAssetSource</c>
    ///     remarks require (an unknown token is rejected, never resolved by path instead), this query must apply the
    ///     same rule, or the facts and the bytes could name different copies.
    /// </remarks>
    public ClassicContainerFacts? TryGetContainerFacts(AssetReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!string.Equals(reference.SourceId, Id, StringComparison.Ordinal))
            throw new ArgumentException("The asset belongs to a different source.", nameof(reference));
        return ClassicContainerFacts.TryDescribe(Session.FileSystem, reference.Path);
    }

    /// <summary>Releases the owned filesystem after all shared browser leases have ended.</summary>
    public ValueTask DisposeAsync()
    {
        Session.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     The entry's declared decoded length: null for an LZSS entry of an XnGine BSA layer, whose decoded length the
    ///     archive does not store; the filesystem's size otherwise. Path lookup inside the archive is last-wins, the same
    ///     record <see cref="OpenReadAsync" /> reads, so the flag and the bytes always describe the same copy.
    /// </summary>
    private long? DecodedLength(GameFileEntry entry, Dictionary<string, ArchiveReader?> readers)
    {
        if (!readers.TryGetValue(entry.Source, out var reader))
        {
            reader = ClassicContainerFacts.FindXnGineBsaReader(Session.FileSystem, entry.Source);
            readers[entry.Source] = reader;
        }

        return reader?.FindEntry(entry.Path) is { Compressed: true } ? null : entry.Size;
    }
}
