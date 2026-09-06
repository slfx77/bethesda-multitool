namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     What to paint on one zone face: a scene texture key, a vertex colour, or both.
///     <para>
///         A null <see cref="TextureKey" /> means "no texture" and the colour carries the whole
///         appearance, which is what the debug resolver produces while the real surface-to-tile
///         mapping is unresolved. When a texture IS named, the colour is still emitted and acts as
///         a modulation term, so a resolver can tint without needing a second seam.
///     </para>
/// </summary>
/// <param name="TextureKey">
///     A scene-generated texture path (see <c>BethesdaViewerScene.AddGeneratedTexture</c>), or
///     <see langword="null" /> for untextured geometry. Faces are batched by this value, so a
///     resolver that returns few distinct keys produces few draw calls.
/// </param>
/// <param name="R">Vertex colour red.</param>
/// <param name="G">Vertex colour green.</param>
/// <param name="B">Vertex colour blue.</param>
/// <param name="A">Vertex colour alpha.</param>
internal readonly record struct ShadowkeyTileMaterial(string? TextureKey, byte R, byte G, byte B, byte A)
{
    /// <summary>An untextured, fully opaque face of one flat colour.</summary>
    public static ShadowkeyTileMaterial Colour(byte r, byte g, byte b) => new(null, r, g, b, 255);
}

/// <summary>
///     The single swap point for Shadowkey zone texturing.
///     <para>
///         ⚠ The mapping from a map tile to the texture the game draws on it is NOT solved. The
///         <c>.sur</c> table is "texture plus UV window" and its rows are indexed from somewhere,
///         but no candidate lane has been found that is bounded by the surface count in all 21
///         retail zones (see <see cref="ShadowkeySurface" />), and the prototype's eight surface
///         slots exceed the zone's texture count in eight zones, so they are not texture indices
///         either. Until that is settled the zone renders through
///         <see cref="ShadowkeyDebugTileMaterials" />.
///     </para>
///     <para>
///         This interface exists so that settling it later costs one class. The zone builder asks
///         for a material per face and never reads a surface slot itself.
///     </para>
/// </summary>
internal interface IShadowkeyTileMaterialResolver
{
    /// <summary>The material for one face. Must be pure — the builder may call it in any order.</summary>
    ShadowkeyTileMaterial Resolve(in ShadowkeyTileFace face);
}

/// <summary>
///     The placeholder resolver: untextured vertex colours that make a zone's SHAPE readable while
///     its texturing is unresolved.
///     <para>
///         Deliberately not a single flat grey. Floors are shaded by height so slopes and terraces
///         read as relief, ceilings are cooler and darker so a room reads as enclosed, and the two
///         wall axes differ slightly so corners are visible. This is diagnostic colour in the same
///         sense as the Redguard <c>.WLD</c> layer export — a stable hue per value, never a claim
///         about the game's own art.
///     </para>
/// </summary>
internal sealed class ShadowkeyDebugTileMaterials : IShadowkeyTileMaterialResolver
{
    /// <summary>A shared instance; the resolver holds no state.</summary>
    public static ShadowkeyDebugTileMaterials Instance { get; } = new();

    /// <summary>
    ///     Floor height, in tiles, that shades fully dark. Retail floors span roughly -30.6 to
    ///     13.7 tiles, but nearly all of a given zone sits in a much narrower band, so the ramp is
    ///     deliberately short: a full-range ramp would render every zone one flat tone.
    /// </summary>
    public const float ShadeLowTiles = -4f;

    /// <summary>Floor height, in tiles, that shades fully light.</summary>
    public const float ShadeHighTiles = 12f;

    /// <summary>Resolves the diagnostic colour for one face.</summary>
    public ShadowkeyTileMaterial Resolve(in ShadowkeyTileFace face)
    {
        var height = MeanFloorTiles(face.Prototype);
        var ramp = Math.Clamp((height - ShadeLowTiles) / (ShadeHighTiles - ShadeLowTiles), 0f, 1f);

        return face.Kind switch
        {
            ShadowkeyTileFaceKind.Floor => Mix(64, 78, 58, 176, 198, 150, ramp),
            ShadowkeyTileFaceKind.Ceiling => Mix(38, 44, 58, 96, 108, 132, ramp),
            ShadowkeyTileFaceKind.WallEast or ShadowkeyTileFaceKind.WallWest =>
                Mix(70, 66, 60, 190, 180, 164, ramp),
            _ => Mix(58, 55, 50, 158, 150, 137, ramp)
        };
    }

    /// <summary>The mean of a prototype's four floor corners, in tiles.</summary>
    private static float MeanFloorTiles(ShadowkeyCellPrototype prototype)
    {
        var sum = 0f;
        for (var i = 0; i < ShadowkeyCellPrototype.CornerCount; i++)
        {
            sum += ShadowkeyCellPrototype.ToUnits(prototype.FloorCorners[i]);
        }

        return sum / ShadowkeyCellPrototype.CornerCount;
    }

    /// <summary>Linear blend between a dark and a light colour.</summary>
    private static ShadowkeyTileMaterial Mix(
        byte darkR, byte darkG, byte darkB, byte lightR, byte lightG, byte lightB, float ramp)
    {
        return ShadowkeyTileMaterial.Colour(
            Lerp(darkR, lightR, ramp), Lerp(darkG, lightG, ramp), Lerp(darkB, lightB, ramp));
    }

    private static byte Lerp(byte from, byte to, float ramp) =>
        (byte)Math.Clamp((int)MathF.Round(from + ((to - from) * ramp)), 0, 255);
}
