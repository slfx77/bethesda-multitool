using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Tactics;

/// <summary>
///     A Fallout Tactics <c>.mis</c> mission — the container around a zlib-compressed world.
///     Original RE 2026-09-06; every Tactics reference is GPL, so nothing is ported.
///     <para>
///         Header: <c>"&lt;world&gt;"</c> + NUL, a two-character ASCII version, a NUL, then
///         <b>the uncompressed size TWICE</b> as little-endian u32, then a raw zlib stream.
///         Measured over all 103 retail missions: the two sizes are equal on 103/103, and the stream
///         inflates to EXACTLY that size on 103/103 — so the header is self-checking and a truncated
///         or misidentified file cannot pass.
///     </para>
///     <para>
///         ⚠ <b>There are TWO versions in retail — "68" on 87 missions and "69" on 16</b> — so a
///         reader that pins one rejects a sixth of the campaign. (A four-file sample shows only 68,
///         which is exactly how you would come to pin the wrong one.)
///     </para>
///     <para>
///         ⚑ The inflated payload opens with <c>"&lt;mph&gt;"</c> + NUL on 103/103, continuing the
///         family's <c>'&lt;tag&gt;' + NUL + version</c> framing, and carries length-prefixed team
///         names ("BOS", "Tribals (T)", "Raiders (R)") of the same shape
///         <see cref="TacticsPropertyBag" /> uses. Its interior is not decoded here.
///     </para>
/// </summary>
internal sealed class TacticsMissionFile
{
    /// <summary>The tag every mission opens with, NUL included.</summary>
    public const string Tag = "<world>";

    /// <summary>Bytes before the zlib stream: tag + NUL, 2 version chars, NUL, and two u32 sizes.</summary>
    public const int HeaderLength = 19;

    /// <summary>The tag the inflated world opens with.</summary>
    public const string WorldTag = "<mph>";

    private TacticsMissionFile(string name, string version, byte[] world, char worldVersion, IReadOnlyList<string> teams)
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

    /// <summary>The world's own version character — <c>'8'</c> on all 103 retail missions.</summary>
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

        var worldVersion = world.Length > 6 ? (char)world[6] : '\0';
        mission = new TacticsMissionFile(name, version, world, worldVersion, ReadTeams(world));
        error = string.Empty;
        return true;
    }

    /// <summary>
    ///     Reads the world's team roster: a u32 count, that many u32-length-prefixed names, then a
    ///     parallel u32 array of the same length. Verified on 103/103 retail missions.
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

            var length = BinaryPrimitives.ReadUInt32LittleEndian(world[position..]);
            position += 4;
            if (length > 128 || position + length > world.Length)
            {
                return [];
            }

            teams.Add(Encoding.ASCII.GetString(world.Slice(position, (int)length)));
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
