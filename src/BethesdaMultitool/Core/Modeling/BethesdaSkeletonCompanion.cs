using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling;

/// <summary>
///     The skeleton file the CLI's <c>--skeleton</c> names (cut-1b slice 10, plan section 1.7), served to the reader
///     through the read context's companion resolver: the workflow sets <see cref="BethesdaModelRegistration.SkeletonOption" />
///     to <see cref="Path" /> and composes <see cref="Compose" /> over its texture resolver, so the reader hands the option
///     value to its lookup unchanged and receives this file's occurrence. A lookup of any other name goes to the inner
///     resolver (or, without one, to Shared's default: the input's own source, basenames only).
/// </summary>
/// <remarks>
///     The file is opened as a one-file folder source (the same shape the workflow gives a loose model input). Ownership:
///     the caller keeps this companion alive until the operation and every returned handle are finished, and disposes
///     it, which releases the folder source (<see cref="ModelCompanionResolver" />'s contract).
/// </remarks>
public sealed class BethesdaSkeletonCompanion : IAsyncDisposable
{
    private readonly FolderAssetSource _source;
    private readonly ModelSourceItem _item;

    private BethesdaSkeletonCompanion(FolderAssetSource source, ModelSourceItem item, string path)
    {
        _source = source;
        _item = item;
        Path = path;
    }

    /// <summary>The skeleton's full path: the <see cref="BethesdaModelRegistration.SkeletonOption" /> value the workflow sets.</summary>
    public string Path { get; }

    /// <summary>The skeleton's occurrence.</summary>
    public ModelSourceItem Item => _item;

    /// <summary>Opens the file a <c>--skeleton</c> value names, or returns null when none was given.</summary>
    /// <param name="skeleton">The value as typed (relative to the working directory, or absolute), or null.</param>
    /// <returns>The companion, or null for a null, empty or whitespace value.</returns>
    /// <exception cref="FileNotFoundException">The value names no existing file.</exception>
    public static BethesdaSkeletonCompanion? Open(string? skeleton)
    {
        if (string.IsNullOrWhiteSpace(skeleton))
        {
            return null;
        }

        var full = System.IO.Path.GetFullPath(skeleton);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException("The --skeleton file does not exist.", full);
        }

        var folder = new FolderAssetSource(System.IO.Path.GetDirectoryName(full)!);
        var reference = new AssetReference(folder.Id, System.IO.Path.GetFileName(full));
        var item = new ModelSourceItem(folder, new AssetEntry(reference, new FileInfo(full).Length, Provenance: full));
        return new BethesdaSkeletonCompanion(folder, item, full);
    }

    /// <summary>
    ///     A resolver that answers <see cref="Path" /> (exactly, ordinal) with this skeleton's occurrence and every other
    ///     name through <paramref name="inner" />, or through Shared's default resolution within the owner's source when
    ///     there is no inner resolver.
    /// </summary>
    /// <param name="inner">The workflow's texture resolver, or null.</param>
    /// <returns>The composed resolver.</returns>
    public ModelCompanionResolver Compose(ModelCompanionResolver? inner)
    {
        return (owner, name, cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(owner);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(name, Path, StringComparison.Ordinal))
            {
                return ValueTask.FromResult<IReadOnlyList<ModelSourceItem>>([_item]);
            }

            return inner is not null ? inner(owner, name, cancellationToken) : WithinSourceAsync(owner, name,
                cancellationToken);
        };
    }

    /// <summary>Releases the folder source.</summary>
    public ValueTask DisposeAsync()
    {
        return _source.DisposeAsync();
    }

    /// <summary>
    ///     Shared's default companion resolution (the read context's own fallback): exact siblings of the owner, else
    ///     every basename match within the owner's source; a path is refused with <see cref="ArgumentException" />.
    /// </summary>
    private static async ValueTask<IReadOnlyList<ModelSourceItem>> WithinSourceAsync(ModelSourceItem owner, string name,
        CancellationToken cancellationToken)
    {
        var entries = await CompanionResolver.FindAsync(owner.Source, owner.Reference, name, cancellationToken)
            .ConfigureAwait(false);
        var items = new ModelSourceItem[entries.Count];
        for (var index = 0; index < entries.Count; index++)
        {
            items[index] = new ModelSourceItem(owner.Source, entries[index]);
        }

        return items;
    }
}
