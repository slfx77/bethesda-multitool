namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

/// <summary>
///     Landscape Texture (LTEX) record used by LAND texture layers.
/// </summary>
public record LandscapeTextureRecord
{
    public uint FormId { get; init; }

    public string? EditorId { get; init; }

    public string? IconPath { get; init; }

    public string? SmallIconPath { get; init; }

    public uint? TextureSetFormId { get; init; }

    public byte[]? HavokData { get; init; }

    public byte[]? SpecularData { get; init; }

    public List<uint> GrassFormIds { get; init; } = [];

    public long Offset { get; init; }

    public bool IsBigEndian { get; init; }
}
