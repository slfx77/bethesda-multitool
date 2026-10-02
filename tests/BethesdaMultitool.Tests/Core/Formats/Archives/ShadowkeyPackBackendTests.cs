using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Archives;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Archives;

/// <summary>
///     Synthetic vectors for <see cref="ShadowkeyPackBackend" /> (cut-2 plan slice 1, decision D1): the name-gated exact
///     tiling probe, the <c>NNN_name</c> entry names, empty slots as zero-length entries, and the archive probe chain
///     claiming the pack. Packs come from <see cref="ShadowkeyTestBuilder.Pack" />.
/// </summary>
public sealed class ShadowkeyPackBackendTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"shadowkey-pack-{Guid.NewGuid():N}");

    public ShadowkeyPackBackendTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // Temp cleanup only.
        }
    }

    private string WritePack(byte[] index, byte[] pack, string? names, string stem = "models")
    {
        File.WriteAllBytes(Path.Combine(_directory, stem + ".idx"), index);
        var path = Path.Combine(_directory, stem + ".huge");
        File.WriteAllBytes(path, pack);
        if (names is not null)
        {
            File.WriteAllText(Path.Combine(_directory, stem + ".txt"), names);
        }

        return path;
    }

    [Fact]
    public void AnExactPack_IsClaimed_AndListsEverySlotByIndexAndName()
    {
        var (index, pack, names) = ShadowkeyTestBuilder.Pack(
            [ShadowkeyTestBuilder.GoldenStatic(), [], ShadowkeyTestBuilder.GoldenAnimated()],
            ["crate.bin", "NULL.bin", "rat.bin"]);
        var path = WritePack(index, pack, names);

        Assert.True(ShadowkeyPackBackend.TryProbe(path));
        using var reader = ArchiveReader.Open(path);
        var entries = reader.Backend.ListFiles();

        Assert.IsType<ShadowkeyPackBackend>(reader.Backend);
        Assert.Equal(["000_crate.bin", "001_NULL.bin", "002_rat.bin"], entries.Select(static e => e.FullPath));
        Assert.Equal(0, entries[1].Size);
        Assert.Equal(ShadowkeyTestBuilder.GoldenAnimated(), reader.Backend.Extract(entries[2]));
        Assert.Empty(reader.Backend.Extract(entries[1]));
    }

    [Fact]
    public void WithoutNames_EntriesAreNamedBySlot()
    {
        var (index, pack, _) = ShadowkeyTestBuilder.Pack([ShadowkeyTestBuilder.GoldenStatic()]);
        var path = WritePack(index, pack, null);

        using var backend = ShadowkeyPackBackend.Open(path);

        Assert.Equal("000.bin", Assert.Single(backend.ListFiles()).FullPath);
    }

    [Fact]
    public void OneMovedOffset_IsNotClaimed()
    {
        var (index, pack, names) = ShadowkeyTestBuilder.Pack(
            [ShadowkeyTestBuilder.GoldenStatic(), ShadowkeyTestBuilder.GoldenAnimated()]);
        var moved = index.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(moved.AsSpan(12), BinaryPrimitives.ReadUInt32LittleEndian(moved.AsSpan(12)) + 1);

        Assert.True(ShadowkeyPackBackend.Tiles(index, pack.Length));
        Assert.False(ShadowkeyPackBackend.Tiles(moved, pack.Length));
        Assert.False(ShadowkeyPackBackend.Tiles(index, pack.Length + 1));
        Assert.False(ShadowkeyPackBackend.TryProbe(WritePack(moved, pack, names, "moved")));
    }

    [Fact]
    public void AHugeWithoutAnIndex_OrAnotherExtension_IsNotClaimed()
    {
        var (index, pack, _) = ShadowkeyTestBuilder.Pack([ShadowkeyTestBuilder.GoldenStatic()]);
        var lone = Path.Combine(_directory, "lone.huge");
        File.WriteAllBytes(lone, pack);
        var renamed = Path.Combine(_directory, "models.bin");
        File.WriteAllBytes(renamed, pack);
        File.WriteAllBytes(Path.Combine(_directory, "models.idx"), index);

        Assert.False(ShadowkeyPackBackend.TryProbe(lone));
        Assert.False(ShadowkeyPackBackend.TryProbe(renamed));
    }
}
