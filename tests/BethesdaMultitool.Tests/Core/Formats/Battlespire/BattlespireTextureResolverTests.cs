using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Battlespire;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Battlespire;

/// <summary>
///     The resolver over a synthetic <c>BSI.BSA</c> (an XnGine name-record BSA holding one two-pixel
///     BSI image): a key that names the image yields its pixels through the CMAP, a colour key
///     yields a solid texture without touching the archive, and a name the archive lacks is
///     reported rather than guessed.
/// </summary>
public sealed class BattlespireTextureResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bmt-bsi-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    /// <summary>A BSI chunk: 4-byte tag, BIG-endian u32 length, payload.</summary>
    private static byte[] Chunk(string tag, params byte[] payload)
    {
        var header = new byte[8];
        Encoding.ASCII.GetBytes(tag.PadRight(4)).CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)payload.Length);
        return [.. header, .. payload];
    }

    /// <summary>A 2x1 image whose pixels are palette indices 10 and 40, through a grey CMAP (entry i = 6-bit i/4).</summary>
    private static byte[] Image()
    {
        var header = new byte[26];
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(4), 2);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(6), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(14), 1);

        var map = new byte[768];
        for (var i = 0; i < 256; i++)
        {
            map[i * 3] = map[i * 3 + 1] = map[i * 3 + 2] = (byte)(i / 4);
        }

        return [.. Chunk("BHDR", header), .. Chunk("CMAP", map), .. Chunk("DATA", 10, 40), .. Chunk("END ")];
    }

    /// <summary>An XnGine name-record BSA: u16 count, u16 0x0100, payloads, then 18-byte directory records.</summary>
    private static byte[] Bsa(params (string Name, byte[] Payload)[] entries)
    {
        var file = new List<byte>();
        file.AddRange(BitConverter.GetBytes((ushort)entries.Length));
        file.AddRange(BitConverter.GetBytes((ushort)0x0100));
        foreach (var (_, payload) in entries)
        {
            file.AddRange(payload);
        }

        foreach (var (name, payload) in entries)
        {
            var record = new byte[18];
            Encoding.ASCII.GetBytes(name).CopyTo(record, 0);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(14), payload.Length);
            file.AddRange(record);
        }

        return [.. file];
    }

    private string WriteArchive()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "BSI.BSA"), Bsa(("WALL35.BSI", Image())));
        return _root;
    }

    [Fact]
    public void ANamedKey_ResolvesToTheImagesPixelsThroughItsColourMap()
    {
        using var resolver = BattlespireTextureResolver.Open(WriteArchive());
        Assert.True(resolver.HasArchive);

        var decoded = resolver.ResolveDecoded(0xC4EB, 0xA5BD);
        Assert.NotNull(decoded);
        Assert.Equal((2, 1), (decoded.Width, decoded.Height));
        // Index 10 → 6-bit 2 → 8; index 40 → 6-bit 10 → 40; opaque.
        Assert.Equal([8, 8, 8, 255, 40, 40, 40, 255], decoded.Pixels);

        var png = resolver.Resolve(0xC4EB, 0xA5BD);
        Assert.NotNull(png);
        Assert.Equal(("wall35", 2, 1), (png.Name, png.Width, png.Height));
        Assert.Equal("battlespire/bsi/wall35", BattlespireTextureResolver.TexturePathFor(0xC4EB, 0xA5BD));
        Assert.Empty(resolver.MissingNames);
    }

    [Fact]
    public void ANameTheArchiveLacks_IsReportedOnceAndResolvesToNothing()
    {
        using var resolver = BattlespireTextureResolver.Open(WriteArchive());

        // bok2 = 0x46E09ABF: a legal name, not in this archive.
        Assert.Null(resolver.Resolve(0x46E0, 0x9ABF));
        Assert.Null(resolver.ResolveDecoded(0x46E0, 0x9ABF));
        Assert.Equal(["bok2"], resolver.MissingNames);
        Assert.Equal("battlespire/bsi/bok2", BattlespireTextureResolver.TexturePathFor(0x46E0, 0x9ABF));
    }

    [Fact]
    public void AColourKey_BecomesASolidTexture_EvenWithoutAnArchive()
    {
        using var resolver = BattlespireTextureResolver.Open(Path.Combine(_root, "nowhere"));
        Assert.False(resolver.HasArchive);

        var decoded = resolver.ResolveDecoded(0xFFFF, 0x632A);
        Assert.NotNull(decoded);
        Assert.Equal((BattlespireTextureResolver.SolidColorTextureSize, BattlespireTextureResolver.SolidColorTextureSize),
            (decoded.Width, decoded.Height));
        Assert.All(Enumerable.Range(0, decoded.Pixels.Length / 4),
            i => Assert.Equal([99, 99, 173, 255], decoded.Pixels.AsSpan(i * 4, 4).ToArray()));

        Assert.Equal("color_3195", resolver.Resolve(0xFFFF, 0x632A)!.Name);
        Assert.Equal("battlespire/color/3195", BattlespireTextureResolver.TexturePathFor(0xFFFF, 0x632A));

        // Without the archive a NAMED key resolves to nothing and is reported.
        Assert.Null(resolver.Resolve(0xC4EB, 0xA5BD));
        Assert.Empty(resolver.MissingNames);
        Assert.Null(BattlespireTextureResolver.TexturePathFor(0xF423, 0xFFFF));
    }
}
