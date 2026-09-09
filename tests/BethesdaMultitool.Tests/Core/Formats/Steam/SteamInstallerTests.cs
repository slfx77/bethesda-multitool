using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using BethesdaMultitool.Core.Formats.Steam;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Steam;

/// <summary>
///     The Steam retail-disc installer reader (<c>.sim</c> manifest + <c>.sid</c> payload).
///     <para>
///         The payload is forgeable here — unlike an InstallShield cabinet — because the block
///         format is small and self-contained, so these tests BUILD encrypted discs with a known
///         key and read them back. That makes the decryption path genuinely covered rather than
///         merely exercised, and it is the only way to test it at all: no key ships in this repo.
///     </para>
/// </summary>
public sealed class SteamInstallerTests : IDisposable
{
    // Arbitrary 16-byte legacy-shaped depot key. NOT a real Valve key; these tests encrypt their
    // own fixtures with it, so any 16 bytes do.
    private static readonly byte[] TestKey =
        Convert.FromHexString("000102030405060708090a0b0c0d0e0f");

    private static readonly byte[] KeyCompletion =
    [
        0xA8, 0x19, 0x4D, 0x02, 0x19, 0x3C, 0xD0, 0x37,
        0x92, 0x93, 0x7D, 0x27, 0x59, 0x0A, 0xEC, 0xBD
    ];

    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort.
            }
        }
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sidtest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    // ---------------------------------------------------------------- manifest

    [Fact]
    public void Manifest_ReadsPathsDepotsAndParts()
    {
        var dir = NewTempDir();
        var sim = Path.Combine(dir, "game.sim");
        File.WriteAllBytes(sim, BuildManifest(
        [
            new Declared("Data", "FalloutNV.esm", 22382, 0, 1234, 0),
            new Declared("", "FalloutNV.exe", 22393, 4096, 99, 2)
        ]));

        var manifest = SteamInstallerManifest.Parse(sim);

        Assert.Equal(2, manifest.Files.Count);
        Assert.Equal("Data/FalloutNV.esm", manifest.Files[0].Path);
        Assert.Equal(22382u, manifest.Files[0].DepotId);
        Assert.Equal(1234, manifest.Files[0].Size);
        Assert.Equal(0, manifest.Files[0].PartIndex);

        Assert.Equal("FalloutNV.exe", manifest.Files[1].Path);
        Assert.Equal(22393u, manifest.Files[1].DepotId);
        Assert.Equal(4096, manifest.Files[1].Offset);
        Assert.Equal(2, manifest.Files[1].PartIndex);

        Assert.Equal([22382u, 22393u], manifest.Depots);
    }

    /// <summary>
    ///     The record count is not stored — it is derived from the string-table size and EOF. A
    ///     manifest whose table does not land exactly on EOF must be REFUSED, not read past.
    /// </summary>
    [Fact]
    public void Manifest_RejectsRecordTableThatDoesNotTileToEof()
    {
        var dir = NewTempDir();
        var sim = Path.Combine(dir, "ragged.sim");
        var good = BuildManifest([new Declared("Data", "a.esm", 1, 0, 1, 0)]);
        File.WriteAllBytes(sim, [.. good, 0x00, 0x00, 0x00]); // 3 stray bytes: no longer a multiple of 32

        var ex = Assert.Throws<InvalidDataException>(() => SteamInstallerManifest.Parse(sim));
        Assert.Contains("multiple of 32", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Manifest_RejectsForeignMagic()
    {
        var dir = NewTempDir();
        var sim = Path.Combine(dir, "not.sim");
        File.WriteAllBytes(sim, Encoding.ASCII.GetBytes("BSA\0____________________________"));

        Assert.Throws<InvalidDataException>(() => SteamInstallerManifest.Parse(sim));
        Assert.False(SteamInstallerManifest.HasMagic(sim));
    }

    /// <summary>
    ///     ⚠ String offsets are relative to +16. Reading them as absolute yields a name that is
    ///     short by exactly 16 bytes and still parses — this pins the offset base so that silent
    ///     failure cannot come back.
    /// </summary>
    [Fact]
    public void Manifest_StringOffsetsAreRelativeToSixteen()
    {
        var dir = NewTempDir();
        var sim = Path.Combine(dir, "offsets.sim");
        File.WriteAllBytes(sim, BuildManifest([new Declared("Textures", "landscape.dds", 7, 0, 5, 0)]));

        var manifest = SteamInstallerManifest.Parse(sim);

        // Exact, not "contains": an absolute read would truncate the leading characters.
        Assert.Equal("Textures/landscape.dds", manifest.Files[0].Path);
    }

    // ---------------------------------------------------------------- key store

    [Theory]
    [InlineData("\"22381\"  \"000102030405060708090a0b0c0d0e0f\"")]
    [InlineData("22381:000102030405060708090a0b0c0d0e0f")]
    [InlineData("22381 = 000102030405060708090a0b0c0d0e0f")]
    public void KeyStore_AcceptsBothLineShapes(string line)
    {
        var dir = NewTempDir();
        var file = Path.Combine(dir, "keys.txt");
        File.WriteAllLines(file, ["# comment", "garbage line", line]);

        var store = SteamDepotKeyStore.Load(file);

        Assert.Equal(TestKey, store.Find(22381));
        Assert.Null(store.Find(99999));
    }

    /// <summary>
    ///     The generator hands the CLI a key file that lives outside the build tree through
    ///     <see cref="SteamDepotKeyStore.EnvironmentVariable" />; it must win over a file beside
    ///     the manifest, and a stale variable pointing nowhere must fall back rather than fail.
    /// </summary>
    [Fact]
    public void KeyStore_EnvironmentVariableTakesPrecedenceOverBesideProbe()
    {
        var dir = NewTempDir();
        var sim = Path.Combine(dir, "game.sim");
        File.WriteAllBytes(sim, [0x1F, 0x4C, 0xD0, 0x3F]);
        File.WriteAllLines(Path.Combine(dir, "depot_keys.txt"),
            ["\"22381\" \"ffffffffffffffffffffffffffffffff\""]);
        var external = Path.Combine(dir, "elsewhere.txt");
        File.WriteAllLines(external, ["\"22381\" \"000102030405060708090a0b0c0d0e0f\""]);

        var previous = Environment.GetEnvironmentVariable(SteamDepotKeyStore.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(SteamDepotKeyStore.EnvironmentVariable, external);
            Assert.Equal(TestKey, SteamDepotKeyStore.LoadBeside(sim).Find(22381));

            Environment.SetEnvironmentVariable(SteamDepotKeyStore.EnvironmentVariable,
                Path.Combine(dir, "does-not-exist.txt"));
            Assert.Equal(Convert.FromHexString("ffffffffffffffffffffffffffffffff"),
                SteamDepotKeyStore.LoadBeside(sim).Find(22381));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SteamDepotKeyStore.EnvironmentVariable, previous);
        }
    }

    [Fact]
    public void KeyStore_RejectsAThirtyTwoByteSteam3Key()
    {
        // The Steam3 key is 64 hex chars. Silently accepting it would produce noise, not an error,
        // so the store must not take it at all.
        var dir = NewTempDir();
        var file = Path.Combine(dir, "keys.txt");
        File.WriteAllLines(file,
            ["\"22381\" \"cd475383b32cb0055c08242d5763a28aca80d1cbf045b269f24ae042e89dc2d7\""]);

        Assert.Null(SteamDepotKeyStore.Load(file).Find(22381));
    }

    // ---------------------------------------------------------------- payload

    [Fact]
    public void Archive_ExtractsCompressedAndUncompressedMembers()
    {
        var payload = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("NEW VEGAS 1.0 ", 400)));
        var small = new byte[] { 1, 2, 3, 4, 5, 6, 7 };

        var dir = BuildDisc(
            [
                new Member("Data/big.bsa", 22381, payload, true),
                new Member("small.ini", 22381, small, false)
            ],
            true);

        using var archive = SteamInstallerArchive.Open(Path.Combine(dir, "game.sim"));

        Assert.Empty(archive.MissingKeyDepots);
        var big = archive.Manifest.Files.Single(f => f.Path == "Data/big.bsa");
        var ini = archive.Manifest.Files.Single(f => f.Path == "small.ini");

        Assert.Equal(payload, archive.Extract(big));
        Assert.Equal(small, archive.Extract(ini));
    }

    /// <summary>A file's blocks may continue into the next <c>.sid</c> part.</summary>
    [Fact]
    public void Archive_FollowsAFileAcrossPartBoundaries()
    {
        var first = Encoding.ASCII.GetBytes(new string('A', 500));
        var second = Encoding.ASCII.GetBytes(new string('B', 500));
        var dir = BuildDisc(
            [new Member("Data/split.bsa", 22381, first, false, second)],
            true);

        using var archive = SteamInstallerArchive.Open(Path.Combine(dir, "game.sim"));
        var extracted = archive.Extract(archive.Manifest.Files[0]);

        Assert.Equal(1000, extracted.Length);
        Assert.Equal([.. first, .. second], extracted);
    }

    /// <summary>Listing is keyless on purpose — only extraction needs a depot key.</summary>
    [Fact]
    public void Archive_ListsWithoutAnyKeyButRefusesToExtract()
    {
        var dir = BuildDisc(
            [new Member("Data/x.bsa", 22381, [9, 9, 9, 9], false)],
            false);

        using var archive = SteamInstallerArchive.Open(Path.Combine(dir, "game.sim"));

        Assert.Single(archive.Manifest.Files);
        Assert.Equal("Data/x.bsa", archive.Manifest.Files[0].Path);
        Assert.Equal([22381u], archive.MissingKeyDepots);

        var ex = Assert.Throws<InvalidOperationException>(() => archive.Extract(archive.Manifest.Files[0]));
        Assert.Contains("22381", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A wrong key must FAIL, not return plausible garbage. The PK signature check is what
    ///     catches it, so this pins that the check exists.
    /// </summary>
    [Fact]
    public void Archive_WrongKeyIsRejectedRatherThanReturningNoise()
    {
        var payload = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("data ", 200)));
        var dir = BuildDisc([new Member("Data/y.bsa", 22381, payload, true)], false);

        File.WriteAllLines(Path.Combine(dir, "depot_keys.txt"),
            ["\"22381\" \"ffeeddccbbaa99887766554433221100\""]);

        using var archive = SteamInstallerArchive.Open(Path.Combine(dir, "game.sim"));
        var ex = Assert.Throws<InvalidDataException>(() => archive.Extract(archive.Manifest.Files[0]));
        Assert.Contains("PK local file header", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- real disc

    /// <summary>
    ///     The retail disc, listed WITHOUT a key. Proves the manifest path against real bytes;
    ///     extraction is not covered here because no depot key ships with this repository.
    /// </summary>
    [Fact]
    [Trait("Category", TestCategories.BucketB)]
    public void RetailDisc_ManifestListsEveryDeclaredFile()
    {
        BucketBTestGuard.SkipUnlessEnabled();

        var sim = RealAssetPaths.SteamRetailDiscManifest();
        Assert.SkipUnless(sim is not null && File.Exists(sim),
            "The New Vegas Steam retail disc manifest is not staged.");

        var manifest = SteamInstallerManifest.Parse(sim);

        Assert.Equal(431, manifest.Files.Count);
        Assert.Equal([22381u, 22382u, 22393u], manifest.Depots);
        Assert.Equal(411, manifest.Files.Count(f => f.DepotId == 22381));
        Assert.Equal(8, manifest.Files.Count(f => f.DepotId == 22382));
        Assert.Equal(12, manifest.Files.Count(f => f.DepotId == 22393));

        var master = manifest.Files.Single(f =>
            f.Path.Equals("Data/FalloutNV.esm", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(245_491_701, master.Size);
        Assert.Equal(22382u, master.DepotId);

        var exe = manifest.Files.Single(f =>
            f.Path.Equals("FalloutNV.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(16_397_640, exe.Size);
        Assert.Equal(22393u, exe.DepotId);
    }

    /// <summary>Builds a <c>.sim</c> byte-for-byte: header, string table at +24, records to EOF.</summary>
    private static byte[] BuildManifest(IReadOnlyList<Declared> declared)
    {
        var strings = new MemoryStream();
        var offsets = new Dictionary<string, uint>(StringComparer.Ordinal);

        uint Intern(string value)
        {
            if (offsets.TryGetValue(value, out var existing))
            {
                return existing;
            }

            // File offset of this string, expressed relative to +16 as the format requires.
            var fileOffset = 24 + (uint)strings.Length;
            offsets[value] = fileOffset - 16;
            var bytes = Encoding.Latin1.GetBytes(value);
            strings.Write(bytes, 0, bytes.Length);
            strings.WriteByte(0);
            return offsets[value];
        }

        var records = new MemoryStream();
        var pending = new List<(uint Name, uint Folder, Declared Row)>();
        foreach (var row in declared)
        {
            pending.Add((Intern(row.Name), Intern(row.Folder), row));
        }

        Span<byte> record = stackalloc byte[32];
        record.Clear();
        foreach (var (name, folder, row) in pending)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(record, name);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], folder);
            BinaryPrimitives.WriteUInt32LittleEndian(record[8..], row.Depot);
            BinaryPrimitives.WriteUInt32LittleEndian(record[12..], (uint)row.Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(record[20..], (uint)row.Size);
            BinaryPrimitives.WriteUInt32LittleEndian(record[28..], (uint)(1 | (row.Part << 8)));
            records.Write(record);
        }

        var stringBytes = strings.ToArray();
        var output = new MemoryStream();
        var header = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x3FD04C1F);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)stringBytes.Length);
        output.Write(header);
        output.Write(stringBytes);
        output.Write(records.ToArray());
        return output.ToArray();
    }

    /// <summary>
    ///     Writes a whole synthetic disc: one <c>.sim</c> plus the <c>.sid</c> parts, with every
    ///     block encrypted exactly the way the reader expects to find it.
    /// </summary>
    private string BuildDisc(IReadOnlyList<Member> members, bool writeKeys)
    {
        var dir = NewTempDir();

        var part0 = new MemoryStream();
        var part1 = new MemoryStream();
        var declared = new List<Declared>();

        foreach (var member in members)
        {
            var separator = member.Path.LastIndexOf('/');
            var folder = separator < 0 ? string.Empty : member.Path[..separator];
            var name = separator < 0 ? member.Path : member.Path[(separator + 1)..];

            var offset = part0.Length;
            part0.Write(BuildBlock(member.Data, member.Compressed));
            long total = member.Data.Length;

            if (member.Continuation is not null)
            {
                part1.Write(BuildBlock(member.Continuation, member.Compressed));
                total += member.Continuation.Length;
            }

            declared.Add(new Declared(folder, name, member.Depot, offset, total, 0));
        }

        File.WriteAllBytes(Path.Combine(dir, "game.sim"), BuildManifest(declared));
        File.WriteAllBytes(Path.Combine(dir, "game_0.sid"), part0.ToArray());
        File.WriteAllBytes(Path.Combine(dir, "game_1.sid"), part1.ToArray());

        if (writeKeys)
        {
            File.WriteAllLines(Path.Combine(dir, "depot_keys.txt"),
                [.. members.Select(m => $"\"{m.Depot}\" \"{Convert.ToHexString(TestKey).ToLowerInvariant()}\"")]);
        }

        return dir;
    }

    /// <summary>One encrypted block: 8-byte plaintext header, encrypted IV, then CBC ciphertext.</summary>
    private static byte[] BuildBlock(byte[] data, bool compressed)
    {
        byte[] plaintext;
        if (compressed)
        {
            using var deflated = new MemoryStream();
            using (var compressor = new DeflateStream(deflated, CompressionLevel.Optimal, true))
            {
                compressor.Write(data, 0, data.Length);
            }

            // 30-byte PK local file header with a 3-char name and no extra field.
            var pk = new byte[30 + 3];
            BinaryPrimitives.WriteUInt32LittleEndian(pk, 0x04034B50);
            BinaryPrimitives.WriteUInt16LittleEndian(pk.AsSpan(26), 3);
            BinaryPrimitives.WriteUInt16LittleEndian(pk.AsSpan(28), 0);
            Encoding.ASCII.GetBytes("zip").CopyTo(pk, 30);
            plaintext = [.. pk, .. deflated.ToArray()];
        }
        else
        {
            plaintext = data;
        }

        var padLength = 16 - plaintext.Length % 16;
        var padded = new byte[plaintext.Length + padLength];
        plaintext.CopyTo(padded, 0);

        var aesKey = new byte[32];
        TestKey.CopyTo(aesKey, 0);
        KeyCompletion.CopyTo(aesKey, 16);

        using var aes = Aes.Create();
        aes.Key = aesKey;
        aes.Padding = PaddingMode.None;

        var iv = RandomNumberGenerator.GetBytes(16);
        var encryptedIv = aes.EncryptEcb(iv, PaddingMode.None); // reader ECB-DECRYPTS this
        var ciphertext = aes.EncryptCbc(padded, iv, PaddingMode.None);

        var payloadLength = encryptedIv.Length + ciphertext.Length;
        var block = new byte[8 + payloadLength];
        block[0] = (byte)(payloadLength & 0xFF);
        block[1] = (byte)((payloadLength >> 8) & 0xFF);
        block[2] = (byte)((payloadLength >> 16) & 0xFF);
        block[3] = (byte)(16 + padLength);
        block[4] = (byte)(data.Length & 0xFF);
        block[5] = (byte)((data.Length >> 8) & 0xFF);
        block[6] = (byte)((data.Length >> 16) & 0xFF);
        block[7] = (byte)(compressed ? 3 : 1);
        encryptedIv.CopyTo(block, 8);
        ciphertext.CopyTo(block, 8 + encryptedIv.Length);
        return block;
    }

    // ---------------------------------------------------------------- fixtures

    private sealed record Declared(string Folder, string Name, uint Depot, long Offset, long Size, int Part);

    private sealed record Member(string Path, uint Depot, byte[] Data, bool Compressed, byte[]? Continuation = null);
}
