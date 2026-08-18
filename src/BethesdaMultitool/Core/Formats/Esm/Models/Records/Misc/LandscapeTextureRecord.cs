namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

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

    /// <summary>
    ///     Starfield's <c>BNAM</c> "Material File" — a <c>materials\...\*.mat</c> path.
    ///     <para>
    ///         Starfield's LTEX has NO <c>TNAM</c> texture-set link at all (verified against
    ///         wbDefinitionsSF1.pas and retail <c>LDefault006Base</c>): the diffuse is reached through
    ///         the material database instead, LTEX -> BNAM -> .mat -> MRTextureFile. Every other game
    ///         keeps using <see cref="TextureSetFormId" />; this is null for them.
    ///     </para>
    /// </summary>
    public string? MaterialPath { get; init; }

    public byte[]? HavokData { get; init; }

    public byte[]? SpecularData { get; init; }

    public List<uint> GrassFormIds { get; init; } = [];

    public long Offset { get; init; }

    public bool IsBigEndian { get; init; }
}
