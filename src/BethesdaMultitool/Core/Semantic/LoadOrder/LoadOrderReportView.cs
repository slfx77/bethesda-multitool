using BethesdaMultitool.Core.Formats.Esm.Models;

namespace BethesdaMultitool.Core.Semantic.LoadOrder;

/// <summary>Report adapter over the same physical selection used by inspection and derived views.</summary>
internal sealed class LoadOrderReportView(LoadOrderSelectionView selection)
{
    internal LoadOrderSelectionView Selection => selection;
    internal PluginLoadOrder Order => selection.Order;
    internal LoadOrderRecordIndex Index => selection.Index;
    internal RecordCollection Records => selection.Records;
    internal HashSet<uint> IncludedFormIds => selection.IncludedFormIds;
    internal string Status(LoadOrderRecordIdentity identity) => selection.Status(identity);

    internal static async Task<LoadOrderReportView> LoadAsync(PluginLoadOrder order,
        CancellationToken cancellationToken = default) =>
        new(await LoadOrderSelectionView.LoadAsync(order, cancellationToken));

    internal static LoadOrderReportView FromSources(PluginLoadOrder order, LoadOrderRecordIndex index,
        IEnumerable<(PluginLoadOrderEntry Entry, RecordCollection Records)> sources) =>
        new(LoadOrderSelectionView.FromSources(order, index, sources));
}
