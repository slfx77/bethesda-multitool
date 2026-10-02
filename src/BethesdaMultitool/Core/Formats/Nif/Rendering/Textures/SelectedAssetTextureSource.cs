using BethesdaMultitool.Core.Assets;
using BethesdaMultitool.Core.Formats.Dds;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

/// <summary>CPU/GPU adapter over the same physical selection and bounded read contract.</summary>
internal sealed class SelectedAssetTextureSource(AssetSourcePlan plan) : INifTextureSource
{
    internal AssetSelectionSession Selection { get; } = new(plan);
    public DecodedTexture? TryLoad(string path) => Selection.Read(path, NifTextureLoader.DecodeTextureData).Value;
    public byte[]? TryLoadRaw(string path) => Selection.Read(path).Value;
    internal SelectedAssetRead<T> ReadDecoded<T>(string path, Func<byte[], T?> decode) where T : class =>
        Selection.Read(path, decode);
    public bool Exists(string path) => Selection.Probe(path).Status == AssetSelectionStatus.Selected;

    public bool TryGetAssetMetadata(string path, out NifTextureSourceAssetMetadata metadata)
    {
        metadata = default;
        if (Selection.Probe(path).Selected is not { } selected) return false;
        metadata = new(selected.SourcePath, selected.SourceLength, selected.SourceWriteTicks,
            selected.Offset is { } offset ? checked((ulong)offset) : null, EntryIndex: selected.Occurrence);
        return true;
    }

    public void Dispose() => Selection.Dispose();
}
