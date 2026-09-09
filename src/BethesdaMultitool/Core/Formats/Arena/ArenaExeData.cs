using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Arena;

/// <summary>One province as the EXECUTABLE stores it — distinct from CITYDATA's <c>ArenaProvince</c>.</summary>
internal sealed record ArenaExeProvince(int Index, int X, int Y, int Width, int Height, string Name)
{
    /// <summary>Right edge of the province's rectangle on the world map.</summary>
    public int Right => X + Width;

    /// <summary>Bottom edge of the province's rectangle on the world map.</summary>
    public int Bottom => Y + Height;
}

/// <summary>
///     Tables hardcoded inside Arena's <c>A.EXE</c>, read from the PKLITE-unpacked image that
///     <c>classic exe</c> produces (174,021 packed → 304,624 bytes).
///     <para>
///         Arena keeps a great deal of its world in the executable rather than in data files, so
///         these tables are the only source for several record types. This class grows a table at a
///         time; each is located by a CONTENT ANCHOR and then validated, never by a bare hardcoded
///         offset, so a differently-built executable either matches or is rejected rather than
///         producing plausible nonsense from the wrong address.
///     </para>
/// </summary>
internal static class ArenaExeData
{
    /// <summary>Provinces in the game, including the Imperial Province.</summary>
    public const int ProvinceCount = 9;

    /// <summary>Bytes per province record: four u16 fields then a NUL-padded name.</summary>
    public const int ProvinceRecordLength = 0x62;

    /// <summary>Bytes of numeric fields ahead of the name inside a province record.</summary>
    public const int ProvinceNameOffset = 8;

    /// <summary>Width of Arena's world map in pixels; every province rectangle fits inside it.</summary>
    public const int WorldMapWidth = 320;

    /// <summary>Height of Arena's world map in pixels.</summary>
    public const int WorldMapHeight = 200;

    /// <summary>The first province's name, used to anchor the table.</summary>
    private const string FirstProvinceName = "High Rock";

    /// <summary>Character attributes, in the game's order. Strength first.</summary>
    public const int AttributeCount = 8;

    /// <summary>Playable character classes.</summary>
    public const int ClassCount = 18;

    /// <summary>Playable races.</summary>
    public const int RaceCount = 8;

    /// <summary>Armour materials, weakest first.</summary>
    public const int MaterialCount = 8;

    /// <summary>Count of weapons the game defines.</summary>
    public const int WeaponCount = 18;

    /// <summary>Count of armour pieces (cuirass through tower shield).</summary>
    public const int ArmorPieceCount = 11;

    /// <summary>Count of monster names, before the pool continues into class-based enemies (Rat through Lich).</summary>
    public const int CreatureCount = 23;

    /// <summary>Months in the Tamrielic calendar.</summary>
    public const int MonthCount = 12;

    /// <summary>Days in the Tamrielic week.</summary>
    public const int DayCount = 7;

    /// <summary>Spell effect names.</summary>
    public const int SpellEffectCount = 25;

    /// <summary>Noble titles, before the pool continues into metal descriptors.</summary>
    public const int NobleTitleCount = 14;

    /// <summary>
    ///     Reads the province table, or returns null when this image does not contain a recognisable
    ///     one.
    ///     <para>
    ///         Measured on the retail unpacked image 2026-09-06: nine records of 98 bytes beginning
    ///         at 0x0392F0, each <c>u16 x, u16 y, u16 width, u16 height</c> then a NUL-terminated
    ///         name padded to 90 bytes. The fields are a RECTANGLE, not a bounding box —
    ///         <c>x &lt; right</c> holds only because width is added, and reading them as
    ///         (left, top, right, bottom) puts Summerset Isle at a negative size.
    ///     </para>
    ///     <para>
    ///         The reading is confirmed geographically, not merely arithmetically: High Rock lands
    ///         northwest, Skyrim north-centre, Morrowind northeast, Summerset southwest, Black Marsh
    ///         southeast and the Imperial Province centre — which is Tamriel.
    ///     </para>
    ///     <para>
    ///         ⚠⚠ <b>This is a SECOND province table and it does not agree with CITYDATA.</b>
    ///         <c>ArenaCityDataFile</c> reads its own name + rectangle per province, and all nine
    ///         rectangles differ — High Rock is (37,32,86,57) here against (41,26,83,61) there,
    ///         Morrowind (190,31,102,93) against (198,28,81,87) — and even the spelling differs
    ///         (<c>Summerset Isle</c> here, <c>Summurset Isle</c> in CITYDATA). They are close
    ///         enough to be the same intent and far enough apart to be independently authored.
    ///         ⛔ Which one the game actually draws with is NOT established, so neither is
    ///         presented as correct and nothing here overrides the CITYDATA-derived APRV records.
    ///         A consumer picking a rectangle must know there are two.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<ArenaExeProvince>? TryReadProvinces(ReadOnlySpan<byte> unpackedExe)
    {
        var anchor = FindAnchor(unpackedExe, FirstProvinceName);
        if (anchor < ProvinceNameOffset)
        {
            return null;
        }

        var start = anchor - ProvinceNameOffset;
        if (start + ProvinceCount * ProvinceRecordLength > unpackedExe.Length)
        {
            return null;
        }

        var provinces = new List<ArenaExeProvince>(ProvinceCount);
        for (var i = 0; i < ProvinceCount; i++)
        {
            var record = unpackedExe.Slice(start + i * ProvinceRecordLength, ProvinceRecordLength);
            var x = BinaryPrimitives.ReadUInt16LittleEndian(record);
            var y = BinaryPrimitives.ReadUInt16LittleEndian(record[2..]);
            var width = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
            var height = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
            var name = ReadName(record[ProvinceNameOffset..]);

            // Every record must be a named rectangle inside the world map. A table found at the
            // wrong address fails this immediately rather than yielding nine plausible provinces.
            if (name.Length == 0 || width == 0 || height == 0
                || x + width > WorldMapWidth || y + height > WorldMapHeight)
            {
                return null;
            }

            provinces.Add(new ArenaExeProvince(i, x, y, width, height, name));
        }

        return provinces;
    }

    /// <summary>
    ///     Reads a run of NUL-terminated strings that begins at a known first entry.
    ///     <para>
    ///         ⚑ Anchored on CONTENT and validated by COUNT, never by a bare offset — the same rule
    ///         <see cref="TryReadProvinces" /> follows, so a different build is rejected rather than
    ///         mis-read. A pool that does not yield exactly <paramref name="count" /> non-empty
    ///         strings returns null.
    ///     </para>
    /// </summary>
    private static List<string>? TryReadPool(ReadOnlySpan<byte> exe, string firstEntry, int count)
    {
        var at = FindAnchor(exe, firstEntry);
        if (at < 0)
        {
            return null;
        }

        var names = new List<string>(count);
        while (names.Count < count && at < exe.Length)
        {
            var end = at;
            while (end < exe.Length && exe[end] is >= 0x20 and < 0x7F)
            {
                end++;
            }

            if (end == at || end >= exe.Length || exe[end] != 0)
            {
                return null;
            }

            names.Add(Encoding.Latin1.GetString(exe[at..end]));
            at = end + 1;
        }

        return names.Count == count ? names : null;
    }

    /// <summary>
    ///     The eight character attributes: Strength, Intelligence, Willpower, Agility, Speed,
    ///     Endurance, Personality, Luck.
    ///     <para>
    ///         ⚠⚠ The anchor is <c>"Strength\0Intelligence"</c>, NOT <c>"Strength"</c>. That
    ///         word alone occurs SIX times and its first hit is 0x03D751, nowhere near this
    ///         pool at 0x03E1FC — a one-word anchor reads a DIFFERENT table, and the count
    ///         check cannot catch it because eight arbitrary strings still count eight.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<string>? TryReadAttributes(ReadOnlySpan<byte> unpackedExe)
    {
        return TryReadPool(unpackedExe, "Strength\0Intelligence", AttributeCount);
    }

    /// <summary>
    ///     The eighteen character classes, Mage first through Knight.
    ///     <para>
    ///         ⚠ A NINETEENTH string <c>"BattleMage"</c> follows the pool with different casing to
    ///         the <c>"Battlemage"</c> inside it. The count stops at 18 deliberately; that trailing
    ///         entry is a separate use, not a class.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<string>? TryReadClasses(ReadOnlySpan<byte> unpackedExe)
    {
        return TryReadPool(unpackedExe, "Mage\0Spellsword", ClassCount);
    }

    /// <summary>
    ///     The eight races in PLURAL form (Bretons, Redguards, …).
    ///     <para>
    ///         ⚑ The executable holds TWO race pools back to back — plural then singular — so a
    ///         caller wanting "Breton" must ask for <see cref="TryReadRacesSingular" />. Reading the
    ///         first pool and assuming singular yields "Bretons" everywhere.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<string>? TryReadRacesPlural(ReadOnlySpan<byte> unpackedExe)
    {
        return TryReadPool(unpackedExe, "Bretons", RaceCount);
    }

    /// <summary>The eight races in SINGULAR form (Breton, Redguard, …).</summary>
    public static IReadOnlyList<string>? TryReadRacesSingular(ReadOnlySpan<byte> unpackedExe)
    {
        return TryReadPool(unpackedExe, "Breton\0", RaceCount);
    }

    /// <summary>
    ///     The 18 weapon names, Staff through Long Bow. The whole pool is weapons, so the count is
    ///     the pool.
    /// </summary>
    public static IReadOnlyList<string>? TryReadWeapons(ReadOnlySpan<byte> unpackedExe)
    {
        return TryReadPool(unpackedExe, "Staff\0Dagger", WeaponCount);
    }

    /// <summary>
    ///     The 11 armour piece names, Cuirass through Tower Shield.
    ///     <para>
    ///         ⚠ The pool CONTINUES past them into body-part words (Chest, Hands, Legs, Shoulder
    ///         twice, Head, Foot) and four entries reading "General", so reading to the end of the
    ///         run yields 22 "armour pieces". ⚠ It is also PRECEDED by a stray <c>.75</c>, which is
    ///         why the anchor starts at Cuirass rather than at the run's first string.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<string>? TryReadArmorPieces(ReadOnlySpan<byte> unpackedExe)
    {
        return TryReadPool(unpackedExe, "Cuirass\0Gauntlets", ArmorPieceCount);
    }

    /// <summary>
    ///     The 22 monster names, Rat through Lich.
    ///     <para>
    ///         ⚠ The pool CONTINUES into 20 class-based enemies (Mage, Spellsword, … Knight,
    ///         BattleMage) that duplicate <see cref="TryReadClasses" />'s list, so reading the whole
    ///         run gives 42 "creatures" of which half are not monsters. ⚑ The join is visible in the
    ///         data: the tail spells <c>BattleMage</c> with a capital M where the class list spells
    ///         it <c>Battlemage</c>.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<string>? TryReadCreatures(ReadOnlySpan<byte> unpackedExe)
    {
        return TryReadPool(unpackedExe, "Rat\0Goblin", CreatureCount);
    }

    /// <summary>
    ///     The 12 month names, Morning Star through Evening Star.
    ///     <para>
    ///         ⚑ The count is an EXTERNAL fact — the calendar has twelve months — which is what makes
    ///         this validated rather than fitted. ⚠ The run's first string is <c>1Morning Star</c>:
    ///         a stray digit precedes the pool, so the anchor must start inside it.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<string>? TryReadMonths(ReadOnlySpan<byte> unpackedExe)
    {
        return TryReadPool(unpackedExe, "Morning Star\0Sun's Dawn", MonthCount);
    }

    /// <summary>
    ///     The 7 day names, Morndas through Sundas. ⚑ Seven is likewise an external fact, and the
    ///     days are separated from the months by a run of binary junk rather than following them.
    /// </summary>
    public static IReadOnlyList<string>? TryReadDays(ReadOnlySpan<byte> unpackedExe)
    {
        return TryReadPool(unpackedExe, "Morndas\0Tirdas", DayCount);
    }

    /// <summary>The 25 spell effect names, Cause through Spell Resistance. The whole pool.</summary>
    public static IReadOnlyList<string>? TryReadSpellEffects(ReadOnlySpan<byte> unpackedExe)
    {
        return TryReadPool(unpackedExe, "Cause\0Continuous Damage", SpellEffectCount);
    }

    /// <summary>
    ///     The 14 noble titles, Lord through Empress.
    ///     <para>
    ///         ⚠ The pool CONTINUES into 12 metal descriptors carrying their articles — "a Brass",
    ///         "an Iron", … "a Crystal" — so reading the run to its end gives 26 "titles". Those are
    ///         the same metals <see cref="TryReadMaterials" /> reads without articles.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<string>? TryReadNobleTitles(ReadOnlySpan<byte> unpackedExe)
    {
        return TryReadPool(unpackedExe, "Lord\0Duke", NobleTitleCount);
    }

    /// <summary>The eight armour materials: Iron, Steel, Silver, Elven, Dwarven, Mithril, Adamantium, Ebony.</summary>
    public static IReadOnlyList<string>? TryReadMaterials(ReadOnlySpan<byte> unpackedExe)
    {
        return TryReadPool(unpackedExe, "Iron\0Steel", MaterialCount);
    }

    private static string ReadName(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        var text = end < 0 ? field : field[..end];
        return Encoding.Latin1.GetString(text).Trim();
    }

    private static int FindAnchor(ReadOnlySpan<byte> haystack, string needle)
    {
        Span<byte> pattern = stackalloc byte[needle.Length];
        Encoding.ASCII.GetBytes(needle, pattern);
        return haystack.IndexOf(pattern);
    }
}
