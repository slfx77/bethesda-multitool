using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using BethesdaMultitool.Core.Semantic.LoadOrder;

namespace BethesdaMultitool.Core.Assets;

/// <summary>Record identity is independent of the archive/loose-file selection policy.</summary>
internal sealed record AssetRecordOwner(string Status, string Signature, uint? FileLocalFormId,
    uint? LoadOrderFormId, string? Plugin, string? FilePath, long? RecordOffset, uint? Flags,
    string? SourceSha256 = null)
{
    internal static AssetRecordOwner Selected(LoadOrderRecordVersion source, string? sha256 = null) =>
        new(source.WasClamped ? "MappedWithClamp" : "Selected", source.Signature, source.FileLocalFormId,
            source.LoadOrderFormId, source.Plugin, source.FilePath, source.Offset, source.Flags, sha256);

    internal static AssetRecordOwner Unavailable(string signature, uint? id) =>
        new("Unavailable", signature, null, id, null, null, null, null);
}

/// <summary>A record field, selected component, or observed asset dependency; never a guessed filename owner.</summary>
internal sealed record AssetUseNode(string Id, AssetRecordOwner? Owner, string Component, string Field,
    string? RequestedPath, string Relation, ImmutableArray<string> Parents, int? NifBlockIndex = null);

/// <summary>Digest identifies the complete immutable physical receipt, including failed attempts.</summary>
internal sealed record AssetUseRead(string ReceiptId, string PlanIdentity, long Sequence,
    string RequestedPath, AssetSelectionStatus Status, string? PayloadSha256)
{
    internal static AssetUseRead From(AssetSelectionReceipt receipt) => new(
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(AssetSelectionJson.Serialize([receipt])))),
        receipt.PlanIdentity, receipt.Sequence, receipt.RequestedPath, receipt.Status, receipt.PayloadSha256);
}

internal sealed record AssetUseBinding(string UseId, ImmutableArray<AssetUseRead> Reads,
    string Basis = "declared-request-within-scene")
{
    public string Status => Reads.IsEmpty ? "NotObserved" :
        Reads.Where(r => r.Status == AssetSelectionStatus.Selected).Select(r => r.PayloadSha256).Distinct().Skip(1).Any()
            ? "MultipleObservedPayloads" : "ObservedReadAttempts";
}

/// <summary>One physical read can have many consuming uses. Cached bytes do not acquire a mutable owner.</summary>
internal sealed record AssetUseGraph(ImmutableArray<AssetUseNode> Nodes, ImmutableArray<AssetUseBinding> Bindings)
{
    internal static AssetUseGraph Empty { get; } = new([], []);

    internal AssetUseGraph Bind(IEnumerable<AssetSelectionReceipt> receipts)
    {
        var byPath = receipts.Distinct().GroupBy(r => Normalize(r.RequestedPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(AssetUseRead.From).Distinct().ToImmutableArray(), StringComparer.OrdinalIgnoreCase);
        var explicitReads = Bindings.Where(b => b.Basis == "component-read-scope").ToDictionary(b => b.UseId);
        return this with
        {
            Bindings = Nodes.Where(n => n.RequestedPath is not null).Select(n => explicitReads.GetValueOrDefault(n.Id) ??
                new AssetUseBinding(n.Id, byPath.GetValueOrDefault(Normalize(n.RequestedPath!), []))).ToImmutableArray()
        };
    }

    internal static string Normalize(string path) => path.Replace('/', '\\').TrimStart('\\');
}

internal sealed class AssetUseGraphBuilder
{
    private readonly List<AssetUseNode> _nodes = [];

    internal string Add(AssetRecordOwner? owner, string component, string field, string? path,
        string relation = "record-field", IEnumerable<string>? parents = null, int? nifBlockIndex = null)
    {
        var id = $"use-{_nodes.Count + 1}";
        _nodes.Add(new(id, owner, component, field, path, relation,
            parents?.ToImmutableArray() ?? [], nifBlockIndex));
        return id;
    }

    internal AssetUseGraph Build() => new(_nodes.ToImmutableArray(), []);
}
