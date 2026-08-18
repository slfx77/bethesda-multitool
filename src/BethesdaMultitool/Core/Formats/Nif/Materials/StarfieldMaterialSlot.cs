namespace BethesdaMultitool.Core.Formats.Nif.Materials;

/// <summary>
///     One resolved texture slot of a Starfield material: either a texture path, or a flat colour that
///     stands in for an absent texture.
///     <para>
///         Both outcomes are normal authoring. A CE2 texture set may declare a
///         <c>BSMaterial::TextureReplacement</c> instead of an image for a slot — plain plastics,
///         painted trim and similar surfaces carry no albedo map at all, just a colour. Measured on
///         retail, that is 26% of the shapes drawn in New Atlantis / Akila City / Mars Launchpad, so
///         collapsing "no path" to "untextured" leaves a quarter of the scene white.
///     </para>
/// </summary>
/// <param name="TexturePath">Data-relative texture path, or null when the slot is a flat colour.</param>
/// <param name="ReplacementRgba">Flat colour as RGBA8 (R in the low byte), or null when a path is used.</param>
internal readonly record struct StarfieldMaterialSlot(string? TexturePath, uint? ReplacementRgba)
{
    /// <summary>True when the slot yielded either a texture or a colour.</summary>
    public bool IsResolved => TexturePath is { Length: > 0 } || ReplacementRgba.HasValue;
}
