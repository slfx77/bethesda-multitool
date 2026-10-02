using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.AssetBrowse;
using Slfx77.Multitool.Core.Assets;

namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     Writes synthetic classic containers (a numbered XnGine BSA, a named XnGine BSA with optional Battlespire LZSS
///     entries, a Redguard ROB) to temp files and opens them through the production asset session and
///     <see cref="BethesdaBrowseSource" />, so a test exercises the same path a <c>mesh</c> command takes to an archive
///     entry: content probe, archive backend, virtual file system, browse source. The writers restate the container
///     layouts from the plan and the parser documentation; they share no code with the parsers.
/// </summary>
/// <remarks>
///     The LZSS writer emits a literal-only stream (a flag byte of 0xFF before each run of eight literals), which the
///     Battlespire decoder decodes back to the payload exactly, so a compressed entry's stored bytes differ from its
///     decoded bytes by construction (every 8 payload bytes cost 9 stored bytes). Dispose deletes the files and
///     disposes every source opened through the fixture.
/// </remarks>
internal sealed class ClassicContainerFixture : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "bmt-classic-containers-" + Guid.NewGuid().ToString("N"));

    private readonly List<BethesdaBrowseSource> _sources = [];

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var source in _sources)
        {
            source.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _sources.Clear();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>A numbered-form XnGine BSA (u16 count + u16 0x0200, payloads from 4, 8-byte records at EOF).</summary>
    public string WriteNumberedBsa(params (uint Id, byte[] Payload)[] entries)
    {
        using var stream = new MemoryStream();
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(word, (ushort)entries.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(word[2..], 0x0200);
        stream.Write(word);
        foreach (var (_, payload) in entries)
        {
            stream.Write(payload);
        }

        Span<byte> record = stackalloc byte[8];
        foreach (var (id, payload) in entries)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(record, id);
            BinaryPrimitives.WriteInt32LittleEndian(record[4..], payload.Length);
            stream.Write(record);
        }

        return Write(stream.ToArray(), ".bsa");
    }

    /// <summary>
    ///     A named-form XnGine BSA (u16 count + u16 0x0100, payloads from 4, 18-byte records at EOF). A compressed
    ///     entry's payload is stored as a literal-only Battlespire LZSS stream with the 0x0100 flag.
    /// </summary>
    public string WriteNamedBsa(params (string Name, byte[] Payload, bool Compressed)[] entries)
    {
        using var stream = new MemoryStream();
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(word, (ushort)entries.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(word[2..], 0x0100);
        stream.Write(word);
        var stored = new List<byte[]>(entries.Length);
        foreach (var (_, payload, compressed) in entries)
        {
            var bytes = compressed ? LzssLiteral(payload) : payload;
            stored.Add(bytes);
            stream.Write(bytes);
        }

        Span<byte> record = stackalloc byte[18];
        for (var i = 0; i < entries.Length; i++)
        {
            record.Clear();
            Encoding.ASCII.GetBytes(entries[i].Name, record[..12]);
            BinaryPrimitives.WriteUInt16LittleEndian(record[12..], entries[i].Compressed ? (ushort)0x0100 : (ushort)0);
            BinaryPrimitives.WriteInt32LittleEndian(record[14..], stored[i].Length);
            stream.Write(record);
        }

        return Write(stream.ToArray(), ".bsa");
    }

    /// <summary>A ROB: OARC(4) = count, OARD(length), 80-byte segment headers + payloads, "END ".</summary>
    public string WriteRob(params (string Name, uint Type, byte[] Payload)[] segments)
    {
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[20];
        "OARC"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)segments.Length);
        "OARD"u8.CopyTo(header[12..]);
        var dataLength = segments.Sum(s => 80 + s.Payload.Length);
        BinaryPrimitives.WriteUInt32BigEndian(header[16..], (uint)dataLength);
        stream.Write(header);

        Span<byte> segment = stackalloc byte[80];
        foreach (var (name, type, payload) in segments)
        {
            segment.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(segment, (uint)(80 + payload.Length));
            Encoding.ASCII.GetBytes(name, segment.Slice(4, 8));
            BinaryPrimitives.WriteUInt32LittleEndian(segment[12..], type);
            BinaryPrimitives.WriteUInt32LittleEndian(segment[76..], (uint)payload.Length);
            stream.Write(segment);
            stream.Write(payload);
        }

        stream.Write("END "u8);
        return Write(stream.ToArray(), ".rob");
    }

    /// <summary>A loose file in a fresh directory of the fixture (no install markers around it).</summary>
    public string WriteLoose(string fileName, byte[] bytes)
    {
        var folder = Path.Combine(_directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    ///     The literal-only Battlespire LZSS encoding of <paramref name="payload" />: a 0xFF flag byte (eight set bits,
    ///     LSB first, each a literal) before every run of up to eight payload bytes.
    /// </summary>
    public static byte[] LzssLiteral(byte[] payload)
    {
        var stream = new MemoryStream();
        for (var offset = 0; offset < payload.Length; offset += 8)
        {
            stream.WriteByte(0xFF);
            stream.Write(payload, offset, Math.Min(8, payload.Length - offset));
        }

        return stream.ToArray();
    }

    /// <summary>Opens an archive written by this fixture through the production session, as the model workflow does.</summary>
    public BethesdaBrowseSource OpenArchive(string path)
    {
        var source = new BethesdaBrowseSource(AssetBrowseSession.OpenArchive(path));
        _sources.Add(source);
        return source;
    }

    /// <summary>Opens the folder holding a loose file written by this fixture through the production session.</summary>
    public BethesdaBrowseSource OpenFolder(string loosePath)
    {
        var source = new BethesdaBrowseSource(AssetBrowseSession.OpenFolder(Path.GetDirectoryName(loosePath)!));
        _sources.Add(source);
        return source;
    }

    /// <summary>The entry of a source at a virtual path, found through the source's own enumeration.</summary>
    public static async Task<AssetEntry> FindEntryAsync(IAssetSource source, string path)
    {
        await foreach (var entry in source.EnumerateAsync(cancellationToken: CancellationToken.None))
        {
            if (source.PathComparer.Equals(entry.Reference.Path, path))
            {
                return entry;
            }
        }

        throw new FileNotFoundException("The source has no such entry.", path);
    }

    private string Write(byte[] bytes, string extension)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
