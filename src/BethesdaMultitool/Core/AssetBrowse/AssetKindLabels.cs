using Slfx77.Multitool.Core.Browsing;

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>
///     Names every <see cref="AssetNodeKind" /> for presentation: the singular caption a gallery tile and the
///     preview placeholder show, and the option the Shared asset-type filter lists.
///     <para>
///         Filter options use the Shared plural labels (<c>Browser.AssetType.*</c>) wherever the framework
///         defines one, so every application names the same kind the same way; <see cref="AssetNodeKind.Save" />
///         has no Shared label and falls back to the application key, which the Shared control resolves from
///         the application catalog. A filter identity is the enum member name, compared ordinally.
///     </para>
/// </summary>
internal static class AssetKindLabels
{
    /// <summary>The application resource key of a kind's singular caption, such as "Sprite".</summary>
    /// <param name="kind">Any node kind, folders included.</param>
    /// <returns>The <c>AssetKind_*</c> key.</returns>
    internal static string CaptionKey(AssetNodeKind kind) => "AssetKind_" + kind;

    /// <summary>The ordinal identity the Shared filter selection uses for a filterable kind.</summary>
    /// <param name="kind">A filterable kind; folders are never options.</param>
    /// <returns>The enum member name.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The kind is a folder or undefined.</exception>
    internal static string FilterId(AssetNodeKind kind)
    {
        if (kind == AssetNodeKind.Folder || !Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Folders and undefined kinds are not filter options.");
        return kind.ToString();
    }

    /// <summary>The resource key the Shared filter shows for a kind: the Shared plural label, or the application fallback.</summary>
    /// <param name="kind">A filterable kind.</param>
    /// <returns>A <c>Browser.AssetType.*</c> key, or <c>AssetTypeFilter_Saves</c> for saves.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The kind is a folder or undefined.</exception>
    internal static string FilterLabelKey(AssetNodeKind kind) => kind switch
    {
        AssetNodeKind.Texture => "Browser.AssetType.Texture",
        AssetNodeKind.Sprite => "Browser.AssetType.Sprite",
        AssetNodeKind.Model => "Browser.AssetType.Model",
        AssetNodeKind.Audio => "Browser.AssetType.Audio",
        AssetNodeKind.Video => "Browser.AssetType.Video",
        AssetNodeKind.Map => "Browser.AssetType.Map",
        AssetNodeKind.Text => "Browser.AssetType.Text",
        AssetNodeKind.Plugin => "Browser.AssetType.Plugin",
        AssetNodeKind.Archive => "Browser.AssetType.Archive",
        AssetNodeKind.Raw => "Browser.AssetType.Other",
        AssetNodeKind.Save => "AssetTypeFilter_Saves",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Folders and undefined kinds are not filter options.")
    };

    /// <summary>One Shared filter option per kind present, in the census's display order, carrying its leaf count.</summary>
    /// <param name="census">The kinds present in a source, as <see cref="AssetKindCensus.Present" /> returns them.</param>
    /// <returns>Options ready for <see cref="BrowserTypeFilterSelection.ReplaceOptions" />.</returns>
    internal static IReadOnlyList<BrowserTypeFilterOption> CreateFilterOptions(IEnumerable<AssetKindCount> census)
    {
        ArgumentNullException.ThrowIfNull(census);
        return census.Select(entry => new BrowserTypeFilterOption(FilterId(entry.Kind), FilterLabelKey(entry.Kind), entry.Count))
            .ToArray();
    }

    /// <summary>The kinds a set of selected filter identities names; identities that name no filterable kind are ignored.</summary>
    /// <param name="selectedIds">The Shared selection's selected identities.</param>
    /// <returns>The named kinds, in <see cref="AssetKindFilter.FilterableKinds" /> order.</returns>
    internal static IReadOnlyList<AssetNodeKind> SelectedKinds(IEnumerable<string> selectedIds)
    {
        ArgumentNullException.ThrowIfNull(selectedIds);
        var ids = selectedIds.ToHashSet(StringComparer.Ordinal);
        return AssetKindFilter.FilterableKinds.Where(kind => ids.Contains(FilterId(kind))).ToArray();
    }
}
