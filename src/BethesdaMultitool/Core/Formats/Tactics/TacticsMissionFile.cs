using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Tactics;

/// <summary>
///     One <c>&lt;Team&gt;</c> v2 chunk of a world's entity section: the team's name, the flag byte
///     that follows it (1 on the seven teams a mission actually uses, 0 on <c>No Team</c>,
///     <c>Team 4</c> and <c>Team 8</c>) and the four bytes after that, zero on 18/18 chunks measured
///     over <c>mission01.mis</c> and the world archived in <c>Snake.sav</c> — meaning open.
/// </summary>
internal readonly record struct TacticsWorldTeam(string Name, byte Flag, uint Tail);

/// <summary>
///     A Fallout Tactics <c>.mis</c> mission — the container around a zlib-compressed world.
///     Original RE 2026-09-06; every Tactics reference is GPL, so nothing is ported.
///     <para>
///         ⚑ <b>THE POPULATION, counted once and stated once</b> (re-measured 2026-09-07 by walking
///         each of the install's 40 <c>.bos</c> archives exactly one time and then the loose tree):
///         <b>128 shipped missions</b> = 103 archived (<c>Mis-Main_0</c> 72, <c>mis-core_A</c> 4,
///         <c>_B</c> 6, <c>_C</c> 5, <c>_D</c> 5, <c>_E</c> 7, <c>mis-tutor_A</c> 4) +
///         <b>
///             25 loose on
///             disk
///         </b>
///         (12 campaign missions under <c>core/campaigns/missions/core</c>,
///         <c>core/editor/ambientExample.mis</c>, 12 multiplayer maps under <c>core/missions</c>),
///         plus the world archived inside <c>Snake.sav</c> = 129 worlds in all.
///         ⛔ An earlier revision of this comment said "206 shipped <c>.mis</c> entries … 207/207":
///         that was a corpus walked TWICE (a case-insensitive <c>mis*.bos</c> + <c>Mis*.bos</c> pair
///         with no de-duplication) and it also omitted the 25 loose files. Every figure below is
///         over the 128/129 population.
///     </para>
///     <para>
///         Header: <c>"&lt;world&gt;"</c> + NUL, a two-character ASCII version, a NUL, then
///         <b>the uncompressed size TWICE</b> as little-endian u32, then a raw zlib stream.
///         The two sizes are equal on 128/128 shipped missions and the stream inflates to EXACTLY
///         that size on 128/128 — so the header is self-checking and a truncated or misidentified
///         file cannot pass.
///     </para>
///     <para>
///         ⚑ <b>The stream also ENDS at the last byte handed in, and that is enforced</b> (added
///         2026-09-07): the buffer's last four bytes are the Adler-32 of the inflated world,
///         big-endian, as RFC 1950 requires of a stream that stops there. Measured over all 128
///         shipped missions plus the world archived inside <c>Snake.sav</c> — <b>129/129</b> leave
///         zero unused bytes AND carry the matching trailer.
///         ⚠ The inflater CANNOT tell you this: it stops at the end of the deflate data and
///         silently ignores whatever follows, so "the entry is consumed exactly" was documented but
///         unchecked until this trailer test.
///     </para>
///     <para>
///         ⚠ <b>There are TWO versions in retail — "68" on 98 shipped missions and "69" on 30</b>
///         (87/16 of the archived, 11/14 of the loose; the save's own world is "70") — so a reader
///         that pins one rejects a quarter of what ships. (A four-file sample shows only 68, which
///         is exactly how you would come to pin the wrong one.)
///     </para>
///     <para>
///         ⚑ The inflated payload opens with <c>"&lt;mph&gt;"</c> + NUL on 128/128, continuing the
///         family's <c>'&lt;tag&gt;' + NUL + version</c> framing, and carries length-prefixed team
///         names ("BOS", "Tribals (T)", "Raiders (R)") of the same shape
///         <see cref="TacticsPropertyBag" /> uses. Its interior is not decoded here.
///     </para>
/// </summary>
internal sealed class TacticsMissionFile
{
    /// <summary>The tag of the team chunks scattered through a world's entity section.</summary>
    public const string TeamTag = "Team";

    /// <summary>The only <c>&lt;Team&gt;</c> version measured (18/18 chunks over two worlds).</summary>
    public const string TeamVersion = "2";

    /// <summary>Team chunks a scan will collect before giving up; retail worlds carry nine.</summary>
    public const int MaximumTeamChunks = 256;

    /// <summary>The tag every mission opens with, NUL included.</summary>
    public const string Tag = "<world>";

    /// <summary>Bytes before the zlib stream: tag + NUL, 2 version chars, NUL, and two u32 sizes.</summary>
    public const int HeaderLength = 19;

    /// <summary>The tag the inflated world opens with.</summary>
    public const string WorldTag = "<mph>";

    private TacticsMissionFile(string name, string version, byte[] world, char worldVersion,
        IReadOnlyList<string> teams)
    {
        Name = name;
        Version = version;
        World = world;
        WorldVersion = worldVersion;
        Teams = teams;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>The two ASCII version characters — "68" or "69" on retail.</summary>
    public string Version { get; }

    /// <summary>The inflated world payload, opening with <see cref="WorldTag" />.</summary>
    public byte[] World { get; }

    /// <summary>The world's own version character — <c>'8'</c> on all 128 shipped missions.</summary>
    public char WorldVersion { get; }

    /// <summary>
    ///     The mission's team names, e.g. <c>["BOS", "Tribals (T)", "Raiders (R)"]</c>. Retail
    ///     missions carry 1-8 of them, most commonly 8.
    /// </summary>
    public IReadOnlyList<string> Teams { get; }

    /// <summary>Content probe: the tag.</summary>
    public static bool IsMission(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= HeaderLength && bytes[..7].SequenceEqual(Encoding.ASCII.GetBytes(Tag)) && bytes[7] == 0;
    }

    /// <summary>Reads and inflates a mission, throwing when it does not check out.</summary>
    public static TacticsMissionFile Parse(ReadOnlySpan<byte> bytes, string name)
    {
        if (!TryParse(bytes, name, out var mission, out var error))
        {
            throw new InvalidDataException(error);
        }

        return mission;
    }

    /// <summary>Reads and inflates a mission, reporting why rather than throwing.</summary>
    public static bool TryParse(ReadOnlySpan<byte> bytes, string name, out TacticsMissionFile mission, out string error)
    {
        mission = null!;
        if (!IsMission(bytes))
        {
            error = $"{name}: does not open with the '{Tag}' tag.";
            return false;
        }

        var version = Encoding.ASCII.GetString(bytes.Slice(8, 2));
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(bytes[11..]);
        var repeated = BinaryPrimitives.ReadUInt32LittleEndian(bytes[15..]);
        if (declared != repeated)
        {
            error = $"{name}: the two size fields disagree ({declared} and {repeated}).";
            return false;
        }

        byte[] world;
        try
        {
            using var source = new MemoryStream(bytes[HeaderLength..].ToArray());
            using var inflate = new ZLibStream(source, CompressionMode.Decompress);
            using var target = new MemoryStream();
            inflate.CopyTo(target);
            world = target.ToArray();
        }
        catch (InvalidDataException e)
        {
            error = $"{name}: the zlib stream did not inflate ({e.Message}).";
            return false;
        }

        // The declared size is the check that this really is a mission and really is intact.
        if (world.Length != declared)
        {
            error = $"{name}: inflated to {world.Length} bytes but the header declares {declared}.";
            return false;
        }

        // ⚑ ...and the trailing Adler-32 is the check that the stream ENDS where the buffer does.
        // A zlib stream (RFC 1950) closes with the Adler-32 of the inflated data, big-endian, so
        // the last four bytes matching pins the stream's end at the last byte handed in — the
        // inflater alone cannot say that, because it stops at the end of the deflate data and
        // silently ignores whatever follows.
        var trailer = BinaryPrimitives.ReadUInt32BigEndian(bytes[^4..]);
        var checksum = Adler32(world);
        if (trailer != checksum)
        {
            error = $"{name}: the zlib stream does not end at the last byte "
                    + $"(trailing checksum 0x{trailer:X8}, the inflated world's Adler-32 is 0x{checksum:X8}).";
            return false;
        }

        var worldVersion = world.Length > 6 ? (char)world[6] : '\0';
        mission = new TacticsMissionFile(name, version, world, worldVersion, ReadTeams(world));
        error = string.Empty;
        return true;
    }

    /// <summary>
    ///     Adler-32 (RFC 1950 §9) of the inflated world — the value a zlib stream carries in its
    ///     last four bytes, big-endian. Written out here because the BCL exposes CRC-32 and XxHash
    ///     but no Adler-32.
    /// </summary>
    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint Modulus = 65521;

        // 5,552 is the largest run of bytes that cannot overflow a u32 accumulator, so the two
        // divisions happen once per block instead of once per byte.
        const int Block = 5552;

        uint a = 1;
        uint b = 0;
        while (!data.IsEmpty)
        {
            var take = data.Length < Block ? data.Length : Block;
            foreach (var value in data[..take])
            {
                a += value;
                b += a;
            }

            a %= Modulus;
            b %= Modulus;
            data = data[take..];
        }

        return (b << 16) | a;
    }

    /// <summary>
    ///     Every <c>&lt;Team&gt;</c> v2 chunk in an inflated world, in file order.
    ///     <para>
    ///         ⚠ <b>This is a SCAN, not a tiling walk</b>, and it says so: the entity section a
    ///         world carries after its head is NOT decoded here, so the chunks are found by their
    ///         framing rather than by walking to them. A chunk is accepted only when the framing
    ///         reads, the version is <see cref="TeamVersion" />, its name string's length prefix
    ///         fits the buffer and five more bytes follow; anything else is skipped, so a stray
    ///         <c>&lt;Team&gt;</c> in binary data costs a skipped candidate, not a bad read.
    ///     </para>
    ///     <para>
    ///         ⚑ This is what pairs a SAVE with the mission it was made in: the world archived in
    ///         <c>Snake.sav</c> and the shipped <c>campaigns/missions/core/mission01.mis</c> carry
    ///         the SAME nine chunks —
    ///         <c>
    ///             No Team, BOS, Tribals (T), Raiders (R), Team 4,
    ///             Sentries (B), Hostages, Brahmin, Team 8
    ///         </c>
    ///         with flags 0,1,1,1,0,1,1,1,0 — even
    ///         though the save writes the names WIDE and the mission writes them ASCII, which is
    ///         exactly why <see cref="TacticsCursor.String" /> dispatches on the flag bit.
    ///         ⚠ Distinct from <see cref="Teams" />, which is the <c>&lt;mph&gt;</c> roster at the
    ///         head of a mission world (six names on mission01, and absent from a save world).
    ///     </para>
    /// </summary>
    public static IReadOnlyList<TacticsWorldTeam> ScanTeams(ReadOnlyMemory<byte> world)
    {
        var teams = new List<TacticsWorldTeam>();
        var marker = Encoding.ASCII.GetBytes($"<{TeamTag}>\0");
        var span = world.Span;
        var offset = 0;

        while (offset < span.Length && teams.Count < MaximumTeamChunks)
        {
            var found = span[offset..].IndexOf(marker);
            if (found < 0)
            {
                break;
            }

            var at = offset + found;
            offset = at + marker.Length;
            if (!TacticsTagChunk.TryRead(span[at..], out var chunk)
                || !string.Equals(chunk.Version, TeamVersion, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var cursor = new TacticsCursor(world, TeamTag, at + chunk.BodyOffset);
                var name = cursor.String();
                teams.Add(new TacticsWorldTeam(name, cursor.U8(), cursor.U32()));
                offset = cursor.Position;
            }
            catch (InvalidDataException)
            {
                // A candidate that does not read is not a team chunk; the scan moves on.
            }
        }

        return teams;
    }

    /// <summary>
    ///     Reads the world's team roster: a u32 count, that many u32-length-prefixed names, then a
    ///     parallel u32 array of the same length. Verified on 128/128 shipped missions — the 103
    ///     archived ones and the 25 loose ones — each walking a roster of 1-8 names whose count the
    ///     following u32 repeats on 128/128.
    ///     <para>
    ///         ⚠⚠ <b>A name is wide when its prefix carries bit 31</b>, and exactly ONE shipped
    ///         mission uses that: <c>core/editor/ambientExample.mis</c>. Reading the roster as ASCII
    ///         cost nothing on the 103 ARCHIVED missions and the 24 other loose ones, which is
    ///         precisely why the omission of the loose files from the measured corpus mattered — the
    ///         bug it hid returns an EMPTY roster with no error (fixed 2026-09-07).
    ///     </para>
    ///     <para>
    ///         ⚠ This is the ONLY part of the world decoded here. What follows is the mission's bulk
    ///         data — tile grid, entities, triggers — and is left in <see cref="World" /> rather than
    ///         guessed at. Returns empty if the roster does not walk, so a future world revision
    ///         degrades to "no teams" instead of throwing on an otherwise valid mission.
    ///     </para>
    /// </summary>
    private static List<string> ReadTeams(ReadOnlySpan<byte> world)
    {
        var teams = new List<string>();
        if (world.Length < 12 || !world[..5].SequenceEqual(Encoding.ASCII.GetBytes(WorldTag)))
        {
            return teams;
        }

        var position = 8;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(world[position..]);
        position += 4;
        if (count > 64)
        {
            return teams;
        }

        for (var i = 0; i < count; i++)
        {
            if (position + 4 > world.Length)
            {
                return [];
            }

            // ⚑ The prefix's BIT 31 chooses the encoding, exactly as <see cref="TacticsCursor" />
            // reads it elsewhere: set = that many UTF-16LE code units, clear = that many ASCII
            // bytes. ⚠ Retail is nearly all ASCII — 127 of the 128 shipped missions — but
            // core/editor/ambientExample.mis writes ITS two names wide, and a reader that assumes
            // ASCII silently returns an EMPTY roster there (measured 2026-09-07; the flagged prefix
            // 0x80000006 fails the length guard below, so the failure is invisible).
            var prefix = BinaryPrimitives.ReadUInt32LittleEndian(world[position..]);
            position += 4;

            var wide = (prefix & 0x8000_0000u) != 0;
            var units = prefix & 0x7FFF_FFFFu;
            var length = wide ? units * 2 : units;
            if (units > 128 || position + length > world.Length)
            {
                return [];
            }

            var text = world.Slice(position, (int)length);
            teams.Add(wide ? Encoding.Unicode.GetString(text) : Encoding.ASCII.GetString(text));
            position += (int)length;
        }

        // A parallel u32 array of the same length follows; its count repeating is the check that
        // the roster walked correctly.
        if (position + 4 > world.Length || BinaryPrimitives.ReadUInt32LittleEndian(world[position..]) != count)
        {
            return [];
        }

        return teams;
    }
}
