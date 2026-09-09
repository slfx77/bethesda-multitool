namespace BethesdaMultitool.Core.Formats.Tactics;

/// <summary>
///     The <c>&lt;saveh&gt;</c> v2 header that opens a Fallout Tactics save AND the mission
///     snapshot archived inside it. Original RE 2026-09-07 on <c>Snake.sav</c> (273,875 B, written
///     by the user 2026-09-07); every Tactics reference is GPL, so nothing is ported.
///     <para>
///         Body, all little-endian: <b>u8 flag</b>; <b>five wide strings</b> (u32 with bit 31 set
///         + UTF-16LE — see <see cref="TacticsCursor" />); <b>eight <c>&lt;zar&gt;</c> image slots</b>
///         (<see cref="TacticsZarImage" />; empty slots are 21 bytes); <b>six floats</b>. Measured on
///         both instances in the fixture: the outer header is 65,019 B (a 280x165 screenshot, a
///         128x76 thumbnail, three 25x33 portraits, three empty slots, floats 30, 30, 36, 0, 0, 0)
///         and the embedded one 309 B (eight empty slots, six zeros).
///     </para>
///     <para>
///         ⚑ The five strings, in order: the speech-text path (<c>''</c> on the outer header,
///         <c>locale/missions/mission01/MIS_01_Speech.txt</c> on the embedded — the shipped file
///         that carries the briefing text the world holds; ⚠ NOT byte-for-byte, see
///         <see cref="TacticsMissionSnapshot" />: the shipped file wraps its lines and writes the
///         paragraph breaks as literal <c>\n</c> escapes, so raw containment FAILS after 308 of the
///         1,732 characters and the two agree only once escapes and layout whitespace are dropped),
///         the title
///         (<c>New Save Game</c>), the save name (<c>Snake</c> — equal to the file stem, an oracle
///         from outside the file), the mission display name (<c>Brahmin Wood</c>) and the
///         in-game date and time (<c>Jan 1 2197.  06:29</c>; 2197 is the campaign's start year).
///     </para>
///     <para>
///         ⛔ "u32 1, then a lone 0x80 byte" was the first reading of the outer body and is WRONG:
///         the embedded header's body opens <c>00 | 2B 00 00 80</c>, which that reading turns into
///         u32 0x00002B00 and garbage. <c>u8 + wide string</c> tiles both — the outer header is
///         flag 1 followed by an EMPTY wide string (<c>00 00 00 80</c>).
///     </para>
///     <para>
///         ⚠ Single fixture: the flag (1 outer, 0 embedded), the fixed counts of 5 / 8 / 6 and the
///         float semantics are each measured on two headers and one campaign. What the three
///         portraits and the floats mean is NOT established.
///     </para>
/// </summary>
internal sealed class TacticsSaveHeader
{
    /// <summary>The tag.</summary>
    public const string Tag = "saveh";

    /// <summary>The only version measured (2/2 headers).</summary>
    public const string RetailVersion = "2";

    /// <summary>Wide strings in the body.</summary>
    public const int StringCount = 5;

    /// <summary>Image slots in the body.</summary>
    public const int ImageSlotCount = 8;

    /// <summary>Floats closing the body.</summary>
    public const int FloatCount = 6;

    private TacticsSaveHeader(byte flag, IReadOnlyList<string> strings, IReadOnlyList<TacticsZarImage> images,
        IReadOnlyList<float> floats, int length)
    {
        Flag = flag;
        Strings = strings;
        Images = images;
        Floats = floats;
        Length = length;
    }

    /// <summary>The leading byte — 1 on the user's save, 0 on the embedded mission snapshot. Meaning open.</summary>
    public byte Flag { get; }

    /// <summary>The five strings in file order.</summary>
    public IReadOnlyList<string> Strings { get; }

    /// <summary>String 0: the localized speech file the mission's briefing came from (empty on the outer header).</summary>
    public string SpeechTextPath => Strings[0];

    /// <summary>String 1: the title the save browser shows, e.g. <c>New Save Game</c>.</summary>
    public string Title => Strings[1];

    /// <summary>String 2: the save's own name — equal to the file stem on the fixture.</summary>
    public string SaveName => Strings[2];

    /// <summary>String 3: the current mission's display name, e.g. <c>Brahmin Wood</c>.</summary>
    public string MissionName => Strings[3];

    /// <summary>String 4: the in-game date and time, e.g. <c>Jan 1 2197.  06:29</c>.</summary>
    public string GameTime => Strings[4];

    /// <summary>The eight image slots; a slot without pixels reports <see cref="TacticsZarImage.HasImage" /> false.</summary>
    public IReadOnlyList<TacticsZarImage> Images { get; }

    /// <summary>The six trailing floats — 30, 30, 36, 0, 0, 0 on the fixture; semantics open.</summary>
    public IReadOnlyList<float> Floats { get; }

    /// <summary>Bytes the header occupies, tag included.</summary>
    public int Length { get; }

    /// <summary>Content probe: the framing with the <c>saveh</c> tag.</summary>
    public static bool IsSaveHeader(ReadOnlySpan<byte> bytes)
    {
        return TacticsTagChunk.Is(bytes, Tag);
    }

    /// <summary>Reads a header at the cursor and leaves it positioned after the six floats.</summary>
    public static TacticsSaveHeader Read(TacticsCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        var start = cursor.Position;
        var chunk = cursor.Tag(Tag);
        if (!string.Equals(chunk.Version, RetailVersion, StringComparison.Ordinal))
        {
            throw cursor.Fail(start, $"<{Tag}> version '{chunk.Version}' is not the measured '{RetailVersion}'");
        }

        var flag = cursor.U8();

        var strings = new string[StringCount];
        for (var i = 0; i < strings.Length; i++)
        {
            strings[i] = cursor.WideString();
        }

        var images = new TacticsZarImage[ImageSlotCount];
        for (var i = 0; i < images.Length; i++)
        {
            images[i] = TacticsZarImage.Read(cursor);
        }

        var floats = new float[FloatCount];
        for (var i = 0; i < floats.Length; i++)
        {
            floats[i] = cursor.F32();
        }

        return new TacticsSaveHeader(flag, strings, images, floats, cursor.Position - start);
    }
}
