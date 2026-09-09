namespace BethesdaMultitool.Core.Formats.Travels.OblivionMobile;

/// <summary>
///     Which tile atlas each Oblivion mobile level is drawn with.
///     <para>
///         ⚠⚠ <b>A <c>.jtm</c> never names its atlas, and pairing by stem is WRONG.</b> The
///         pairing lives in the <c>.scr</c> scripts, as the two halves of a <c>LOADMAP</c>:
///         <c>l04_1</c> draws with <c>l01_1.cml</c>, <c>l06_1</c> with <c>l03_l3.cml</c>, and
///         <c>l10_1</c> with <c>l02_l2.cml</c>. Same-stem pairing produces a level that renders
///         completely and looks entirely plausible while being built from the wrong tileset — the
///         kind of wrong that survives review because nothing about it looks broken.
///     </para>
///     <para>
///         A SET per map rather than one name: retail is 16 maps with 0 conflicts, but a build that
///         paired one map two ways must show both rather than silently keep whichever script was
///         read first. <c>l01_r.jtm</c> is an orphan no script mentions and pairs with nothing.
///     </para>
/// </summary>
internal static class OblivionMobileAtlasPairing
{
    private static readonly char[] PathSeparators = ['/', '\\'];

    /// <summary>
    ///     Maps a tile-map file name to every atlas file name a script pairs it with, keyed
    ///     case-insensitively. Script references carry paths; only the file name is kept, because
    ///     that is what an install enumerates.
    /// </summary>
    public static Dictionary<string, SortedSet<string>> Build(IEnumerable<OblivionMobileScript> scripts)
    {
        ArgumentNullException.ThrowIfNull(scripts);

        var pairing = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var script in scripts)
        {
            foreach (var reference in script.Maps)
            {
                var tileMap = FileNameOf(reference.TileMap);
                if (!pairing.TryGetValue(tileMap, out var set))
                {
                    set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                    pairing[tileMap] = set;
                }

                set.Add(FileNameOf(reference.SpriteSet));
            }
        }

        return pairing;
    }

    /// <summary>
    ///     The one atlas <paramref name="tileMapFileName" /> is drawn with, or null when no script
    ///     names one — or when more than one does.
    ///     <para>
    ///         Ambiguity answers null rather than picking, because a renderer that guessed would be
    ///         indistinguishable from one that knew. Retail never reaches that branch.
    ///     </para>
    /// </summary>
    public static string? ResolveSingle(
        IReadOnlyDictionary<string, SortedSet<string>> pairing, string tileMapFileName)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(tileMapFileName);

        return pairing.TryGetValue(FileNameOf(tileMapFileName), out var set) && set.Count == 1
            ? set.Min
            : null;
    }

    /// <summary>The last path segment, in either separator.</summary>
    public static string FileNameOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var slash = path.LastIndexOfAny(PathSeparators);
        return slash < 0 ? path : path[(slash + 1)..];
    }
}
