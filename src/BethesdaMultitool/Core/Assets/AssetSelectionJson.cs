using System.Text.Json;
using System.Text.Json.Serialization;

namespace BethesdaMultitool.Core.Assets;

internal static class AssetSelectionJson
{
    internal static string Serialize(IEnumerable<AssetSelectionReceipt> receipts) =>
        JsonSerializer.Serialize(receipts.ToArray(), AssetSelectionJsonContext.Default.AssetSelectionReceiptArray);

    internal static string SerializeUses(AssetUseGraph graph) =>
        JsonSerializer.Serialize(graph, AssetSelectionJsonContext.Default.AssetUseGraph);
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(AssetSelectionReceipt[]))]
[JsonSerializable(typeof(AssetUseGraph))]
internal partial class AssetSelectionJsonContext : JsonSerializerContext { }
