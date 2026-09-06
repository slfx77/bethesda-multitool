namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     One row of a zone's <c>.sur</c> surface table — a texture plus the UV window it is sampled
///     through. 351 rows over the 21 retail zones, 7 to 27 per zone.
///     <para>
///         <b>The texture index is the LAST byte of the record, not the first.</b> Under that
///         reading <c>max(TextureIndex) == textureCount - 1</c> in 21/21 zones, where the count is
///         byte 0 of the inflated <c>&lt;zone&gt;.ztx</c> (azra 20 against 21 textures, ffarena 6
///         against 7, GlacierCrawl 5 against 6, dstar_e 21 against 22). That is the strongest
///         cross-file identity in this family and it is what fixes the field order; reading the
///         index off the front instead leaves a byte whose range fits nothing in the install.
///     </para>
///     <para>
///         A zone's surface count exceeds its texture count because several rows share one texture
///         with different offsets — azra samples texture 15 twice, at U = -720 and U = 830 — so a
///         surface is "texture plus UV window". The remaining fields are read but their meanings
///         are hypotheses and are recorded as such: <see cref="Log2U" /> and <see cref="Log2V" />
///         run 1..7 and look like log2 texel extents (7 = the full 128-pixel texture),
///         <see cref="OffsetU" /> and <see cref="OffsetV" /> look like 1/256 UV offsets, and
///         <see cref="Flags" /> only ever takes 0, 1, 32 or 128. What selects a surface per map
///         tile is still open: no <c>.zmp</c> tile byte lane is bounded by the surface count in
///         every zone.
///     </para>
/// </summary>
/// <param name="Log2U">Byte +0, 1..7 (hypothesis: log2 of the U extent in texels).</param>
/// <param name="Log2V">Byte +1, 1..7 (hypothesis: log2 of the V extent).</param>
/// <param name="OffsetU">s16 at +2 (hypothesis: U offset in 1/256 units).</param>
/// <param name="OffsetV">s16 at +4 (hypothesis: V offset in 1/256 units).</param>
/// <param name="Flags">Byte +6; 0, 1, 32 or 128 on retail.</param>
/// <param name="TextureIndex">Byte +7 — index into the zone's <c>.ztx</c> texture set.</param>
internal sealed record ShadowkeySurface(
    byte Log2U,
    byte Log2V,
    short OffsetU,
    short OffsetV,
    byte Flags,
    byte TextureIndex);
