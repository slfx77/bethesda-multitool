using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>Supplies a synthetic material database and textures through the real resolver's source contract.</summary>
/// <param name="database">Owned fixture bytes, never mutated by the source.</param>
/// <param name="textures">Fixture paths and decoded channel values.</param>
internal sealed class NifMaterialFixtureSource(byte[] database,
    IReadOnlyDictionary<string, DecodedTexture> textures) : INifTextureSource
{
    /// <summary>Returns exact fixture pixels after the resolver's path normalization.</summary>
    public DecodedTexture? TryLoad(string path) => textures.GetValueOrDefault(path);

    /// <summary>Returns only the declared synthetic material database.</summary>
    public byte[]? TryLoadRaw(string path) => IsDatabase(path) ? database : null;

    /// <summary>Reports only fixture entries.</summary>
    public bool Exists(string path) => IsDatabase(path) || textures.ContainsKey(path);

    /// <summary>Declines persistent metadata because these fixtures have no filesystem identity.</summary>
    public bool TryGetAssetMetadata(string path, out NifTextureSourceAssetMetadata metadata)
    {
        metadata = default;
        return false;
    }

    /// <summary>No resources are retained outside the managed fixture buffers.</summary>
    public void Dispose() { }

    /// <summary>Matches the resolver's compiled-material database path without accepting unrelated assets.</summary>
    private static bool IsDatabase(string path) => string.Equals(path, @"materials\materialsbeta.cdb", StringComparison.OrdinalIgnoreCase);
}
