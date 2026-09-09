using System.IO.Compression;
using System.Text;

namespace BethesdaMultitool.Tests.Core.Formats.Tactics;

/// <summary>
///     Byte writers for hand-built Fallout Tactics save fixtures, shaped after <c>Snake.sav</c>
///     measured 2026-09-07. Each one spells out one convention of the family and nothing else:
///     a TAG is <c>'&lt;' name '&gt;'</c> NUL, an ASCII version, NUL; a WIDE string is a u32 with
///     bit 31 SET followed by that many UTF-16LE code units and no terminator; an ASCII string is
///     the same u32 with bit 31 clear followed by that many bytes.
///     <para>
///         ⚑ Nothing here calls the readers under test — the vectors that carry the claim are
///         written byte by byte in the test methods, and these builders only keep the structural
///         fixtures (an archive of two entries, a whole campaign record) readable.
///     </para>
/// </summary>
internal static class TacticsSyntheticBytes
{
    /// <summary>The bracketless literal that opens a campaign's variable table: 17 bytes.</summary>
    public static byte[] VariableTableHeader =>
        [.. Encoding.ASCII.GetBytes("varTableHeader"), 0, (byte)'1', 0];

    public static byte[] Tag(string name, string version)
    {
        return [.. Encoding.ASCII.GetBytes("<" + name + ">"), 0, .. Encoding.ASCII.GetBytes(version), 0];
    }

    public static byte[] Wide(string text)
    {
        return [.. BitConverter.GetBytes(0x8000_0000u | (uint)text.Length), .. Encoding.Unicode.GetBytes(text)];
    }

    public static byte[] Ascii(string text)
    {
        return [.. BitConverter.GetBytes((uint)text.Length), .. Encoding.ASCII.GetBytes(text)];
    }

    public static byte[] U32(uint value)
    {
        return BitConverter.GetBytes(value);
    }

    public static byte[] I32(int value)
    {
        return BitConverter.GetBytes(value);
    }

    public static byte[] F32(float value)
    {
        return BitConverter.GetBytes(value);
    }

    public static byte[] Concat(params byte[][] parts)
    {
        return parts.SelectMany(part => part).ToArray();
    }

    /// <summary>
    ///     An empty <c>&lt;zar&gt;</c> slot — the 21 bytes a save writes where it has no picture:
    ///     tag (8), width 0, height 0, a zero palette flag and a zero data length.
    /// </summary>
    public static byte[] EmptyZar()
    {
        return Concat(Tag("zar", "4"), U32(0), U32(0), [0], U32(0));
    }

    /// <summary>A <c>&lt;saveh&gt;</c> v2: flag, five wide strings, eight empty slots, six floats.</summary>
    public static byte[] SaveHeader(byte flag, IReadOnlyList<string> strings, IReadOnlyList<float> floats)
    {
        ArgumentNullException.ThrowIfNull(strings);
        ArgumentNullException.ThrowIfNull(floats);

        var parts = new List<byte[]> { Tag("saveh", "2"), new[] { flag } };
        parts.AddRange(strings.Select(Wide));
        for (var i = 0; i < 8; i++)
        {
            parts.Add(EmptyZar());
        }

        parts.AddRange(floats.Select(F32));
        return Concat([.. parts]);
    }

    /// <summary>An <c>&lt;esh&gt;</c> v1 bag of text properties, the shape a campaign's location list uses.</summary>
    public static byte[] TextBag(params (string Name, string Value)[] properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        var parts = new List<byte[]> { Tag("esh", "1"), U32((uint)properties.Length) };
        foreach (var (name, value) in properties)
        {
            // A text property's payload is itself a u32-length-prefixed ASCII string, which is why
            // the reader strips four bytes before handing it back.
            var payload = Ascii(value);
            parts.Add(Ascii(name));
            parts.Add(U32(4));
            parts.Add(U32((uint)payload.Length));
            parts.Add(payload);
        }

        return Concat([.. parts]);
    }

    /// <summary>Wraps a world payload in the <c>&lt;world&gt;</c> container: size twice, then zlib.</summary>
    public static byte[] WorldContainer(byte[] world, string version = "70")
    {
        ArgumentNullException.ThrowIfNull(world);

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionMode.Compress, true))
        {
            deflate.Write(world);
        }

        return Concat(
            [.. "<world>"u8, 0],
            [.. Encoding.ASCII.GetBytes(version), 0],
            U32((uint)world.Length),
            U32((uint)world.Length),
            compressed.ToArray());
    }

    /// <summary>
    ///     A save world's HEAD, in the order measured: the mission path, <c>&lt;sgd&gt;</c> with a
    ///     72-byte body, the briefing list, <c>&lt;SSG&gt;</c> with a 20-byte body,
    ///     <c>&lt;entity_file&gt;</c> with its class names, and the 12-byte entity-list header.
    /// </summary>
    public static byte[] WorldHead(
        string missionPath,
        string briefingText,
        IReadOnlyList<string> classNames,
        string sgdVersion = "5")
    {
        ArgumentNullException.ThrowIfNull(classNames);

        var parts = new List<byte[]>
        {
            Wide(missionPath),
            Tag("sgd", sgdVersion),
            new byte[72],
            U32(1),
            Wide("Brief"),
            U32(1),
            U32(1),
            Wide(briefingText),
            Tag("SSG", "1"),
            new byte[20],
            Tag("entity_file", "3"),
            U32((uint)classNames.Count)
        };

        parts.AddRange(classNames.Select(Wide));

        // u16, u16, u32, u16, u16 = (2000, 955, 1398, 62, 47) on the fixture.
        parts.Add([0xD0, 0x07, 0xBB, 0x03]);
        parts.Add(U32(1398));
        parts.Add([62, 0, 47, 0]);
        return Concat([.. parts]);
    }

    /// <summary>A <c>&lt;Team&gt;</c> v2 chunk: the name (wide or ASCII), a flag byte and four more.</summary>
    public static byte[] Team(string name, byte flag, bool wide = true)
    {
        return Concat(Tag("Team", "2"), wide ? Wide(name) : Ascii(name), [flag], U32(0));
    }
}