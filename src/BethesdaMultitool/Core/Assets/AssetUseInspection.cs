namespace BethesdaMultitool.Core.Assets;

/// <summary>Technical provenance rows for the existing inspection table; no file access or asset reads.</summary>
internal static class AssetUseInspection
{
    internal static IReadOnlyList<KeyValuePair<string, string>> Rows(AssetUseGraph graph,
        IEnumerable<AssetSelectionReceipt> receipts)
    {
        var physical = receipts.GroupBy(r => AssetUseRead.From(r).ReceiptId).ToDictionary(g => g.Key, g => g.First());
        var bindings = graph.Bindings.ToDictionary(b => b.UseId);
        return graph.Nodes.Select(node =>
        {
            var fields = new List<string>();
            if (node.Owner is { } owner)
            {
                fields.Add($"{owner.Status}: {owner.Plugin ?? "?"} {owner.Signature}");
                if (owner.FileLocalFormId is { } local) fields.Add($"local=0x{local:X8}");
                if (owner.LoadOrderFormId is { } global) fields.Add($"global=0x{global:X8}");
                if (owner.RecordOffset is { } offset) fields.Add($"record=0x{offset:X}");
                if (owner.FilePath is { } file) fields.Add(file);
                if (owner.SourceSha256 is { } hash) fields.Add($"source-sha256={hash}");
            }
            fields.Add(node.Relation);
            if (!node.Parents.IsEmpty) fields.Add("parents=" + string.Join(",", node.Parents));
            if (node.NifBlockIndex is { } block) fields.Add($"NIF block={block}");
            if (node.RequestedPath is { } path) fields.Add(path);
            if (bindings.TryGetValue(node.Id, out var binding))
            {
                fields.Add(binding.Status);
                foreach (var read in binding.Reads)
                {
                    fields.Add($"{read.Status}: {read.RequestedPath}");
                    if (physical.TryGetValue(read.ReceiptId, out var receipt) && receipt.Selected is { } selected)
                        fields.Add($"{selected.SourcePath} occurrence={selected.Occurrence} offset={selected.Offset?.ToString("X") ?? "?"}");
                    if (read.PayloadSha256 is { } hash) fields.Add($"payload-sha256={hash}");
                }
            }
            return new KeyValuePair<string, string>($"{node.Id} {node.Component}.{node.Field}", string.Join(" | ", fields));
        }).ToArray();
    }
}
