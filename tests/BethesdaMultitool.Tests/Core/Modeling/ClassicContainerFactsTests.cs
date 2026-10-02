using System.Text;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Assets;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling;

/// <summary>
///     The container facts query (cut-1c plan section 2) through the production path: synthetic archives in temp files
///     opened by the asset session and <c>BethesdaBrowseSource</c>. Each kind reports its fields (index, id, stored
///     size and offset, LZSS flag and census, ROB segment type and 80-byte header), the stored bytes are the bytes as
///     shipped (compressed for an LZSS entry, so they differ from the decoded read), a loose folder and a non-BMT source
///     answer null, and the enumeration reports no declared length for an LZSS entry (the plan's slice-3 control: an
///     LZSS entry reporting its stored size as Length fails).
/// </summary>
public sealed class ClassicContainerFactsTests : IDisposable
{
    private static readonly byte[] PayloadA = Encoding.ASCII.GetBytes("MESH-A-PAYLOAD-BYTES-0123456789");
    private static readonly byte[] PayloadB = Encoding.ASCII.GetBytes("MESH-B");

    private readonly ClassicContainerFixture _fixture = new();

    public void Dispose()
    {
        _fixture.Dispose();
    }

    [Fact]
    public async Task NumberedBsa_ReportsKindIdIndexAndStoredBytes()
    {
        var path = _fixture.WriteNumberedBsa((44004u, PayloadB), (44005u, PayloadA));
        var source = _fixture.OpenArchive(path);

        var facts = ClassicContainerFacts.TryQuery(source, new AssetReference(source.Id, "44005"));

        Assert.NotNull(facts);
        Assert.Equal(ClassicContainerKind.NumberedXnGineBsa, facts.Kind);
        Assert.Equal(Path.GetFileName(path), facts.ContainerName);
        Assert.Equal("44005", facts.EntryName);
        Assert.Equal(1, facts.EntryIndex);
        Assert.Equal(44005u, facts.EntryId);
        Assert.Equal(4 + PayloadB.Length, facts.StoredOffset);
        Assert.Equal(PayloadA.Length, facts.StoredSize);
        Assert.False(facts.IsCompressed);
        Assert.Equal(2, facts.EntryCount);
        Assert.Equal(0, facts.CompressedEntryCount);
        Assert.False(facts.ArchiveHasCompressedEntries);
        Assert.Null(facts.SegmentType);
        Assert.True(facts.SegmentHeader.IsEmpty);
        Assert.Equal(PayloadA, facts.ReadStoredBytes());

        var entry = await ClassicContainerFixture.FindEntryAsync(source, "44005");
        Assert.Equal(PayloadA.Length, entry.Length);
    }

    /// <summary>Path lookup is last-wins, and the index says which copy that was (ARCH3D's repeated ids).</summary>
    [Fact]
    public void NumberedBsa_RepeatedId_DescribesTheLastRecord_WithItsIndex()
    {
        var source = _fixture.OpenArchive(_fixture.WriteNumberedBsa((5090u, PayloadA), (1u, PayloadB), (5090u, PayloadB)));

        var facts = ClassicContainerFacts.TryQuery(source, new AssetReference(source.Id, "5090"));

        Assert.NotNull(facts);
        Assert.Equal(2, facts.EntryIndex);
        Assert.Equal(PayloadB, facts.ReadStoredBytes());
        Assert.NotEqual(PayloadA, facts.ReadStoredBytes());
    }

    [Fact]
    public async Task NamedBsa_LzssEntry_ReportsCompressedStoredBytes_AndNoDeclaredLength()
    {
        var compressed = ClassicContainerFixture.LzssLiteral(PayloadA);
        var source = _fixture.OpenArchive(_fixture.WriteNamedBsa(("ARMOR.3D", PayloadA, true), ("PLAIN.3D", PayloadB, false)));

        var facts = ClassicContainerFacts.TryQuery(source, new AssetReference(source.Id, "ARMOR.3D"));

        Assert.NotNull(facts);
        Assert.Equal(ClassicContainerKind.NamedXnGineBsa, facts.Kind);
        Assert.Equal("ARMOR.3D", facts.EntryName);
        Assert.Equal(0, facts.EntryIndex);
        Assert.Null(facts.EntryId);
        Assert.True(facts.IsCompressed);
        Assert.Equal(compressed.Length, facts.StoredSize);
        Assert.NotEqual(PayloadA.Length, facts.StoredSize);
        Assert.Equal(compressed, facts.ReadStoredBytes());
        Assert.NotEqual(PayloadA, facts.ReadStoredBytes());
        Assert.Equal(2, facts.EntryCount);
        Assert.Equal(1, facts.CompressedEntryCount);
        Assert.True(facts.ArchiveHasCompressedEntries);

        // The source still decodes: the read is the payload, not the stored bytes.
        await using (var stream = await source.OpenReadAsync(new AssetReference(source.Id, "ARMOR.3D"), TestContext.Current.CancellationToken))
        {
            var decoded = new byte[PayloadA.Length + 1];
            var read = await stream.ReadAtLeastAsync(decoded, PayloadA.Length, throwOnEndOfStream: false, TestContext.Current.CancellationToken);
            Assert.Equal(PayloadA.Length, read);
            Assert.Equal(PayloadA, decoded[..read]);
        }

        // The slice-3 control: the LZSS entry declares NO length; reporting the stored size would be wrong by construction.
        var lzss = await ClassicContainerFixture.FindEntryAsync(source, "ARMOR.3D");
        Assert.Null(lzss.Length);
        Assert.NotEqual(compressed.Length, lzss.Length);

        // And the uncompressed sibling keeps its size.
        var plain = await ClassicContainerFixture.FindEntryAsync(source, "PLAIN.3D");
        Assert.Equal(PayloadB.Length, plain.Length);
        var plainFacts = ClassicContainerFacts.TryQuery(source, plain.Reference);
        Assert.NotNull(plainFacts);
        Assert.False(plainFacts.IsCompressed);
        Assert.Equal(1, plainFacts.EntryIndex);
        Assert.Equal(1, plainFacts.CompressedEntryCount);
    }

    [Fact]
    public async Task NamedBsa_WithoutLzss_KeepsEveryLength()
    {
        var source = _fixture.OpenArchive(_fixture.WriteNamedBsa(("A.3D", PayloadA, false), ("B.3D", PayloadB, false)));

        var facts = ClassicContainerFacts.TryQuery(source, new AssetReference(source.Id, "B.3D"));
        Assert.NotNull(facts);
        Assert.Equal(ClassicContainerKind.NamedXnGineBsa, facts.Kind);
        Assert.Equal(0, facts.CompressedEntryCount);

        await foreach (var entry in source.EnumerateAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.NotNull(entry.Length);
        }
    }

    [Fact]
    public void Rob_ReportsSegmentTypeAndHeader()
    {
        var path = _fixture.WriteRob(("GR_COMP", 0u, PayloadA), ("BWAGA001", 512u, []), ("HBBLD01", 0u, PayloadB));
        var source = _fixture.OpenArchive(path);

        var facts = ClassicContainerFacts.TryQuery(source, new AssetReference(source.Id, "HBBLD01.3D"));

        Assert.NotNull(facts);
        Assert.Equal(ClassicContainerKind.RedguardRob, facts.Kind);
        Assert.Equal("HBBLD01", facts.EntryName);
        Assert.Equal(2, facts.EntryIndex);
        Assert.Null(facts.EntryId);
        Assert.Equal(PayloadB.Length, facts.StoredSize);
        Assert.False(facts.IsCompressed);
        Assert.Equal(3, facts.EntryCount);
        Assert.Equal(0u, facts.SegmentType);
        Assert.Equal(ClassicContainerFacts.RobSegmentHeaderLength, facts.SegmentHeader.Length);
        Assert.Equal(80, ClassicContainerFacts.RobSegmentHeaderLength);
        var header = facts.SegmentHeader.Span;
        Assert.Equal((uint)(80 + PayloadB.Length), BitConverter.ToUInt32(header[..4]));
        Assert.Equal("HBBLD01", Encoding.ASCII.GetString(header.Slice(4, 7)));
        Assert.Equal(0, header[11]);
        Assert.Equal((uint)PayloadB.Length, BitConverter.ToUInt32(header[76..]));
        Assert.Equal(PayloadB, facts.ReadStoredBytes());

        var empty = ClassicContainerFacts.TryQuery(source, new AssetReference(source.Id, "BWAGA001.3D"));
        Assert.NotNull(empty);
        Assert.Equal(512u, empty.SegmentType);
        Assert.Equal(1, empty.EntryIndex);
        Assert.Equal(0, empty.StoredSize);
        Assert.Empty(empty.ReadStoredBytes());
    }

    [Fact]
    public async Task LooseFolder_AnswersNoFacts_AndKeepsTheFileLength()
    {
        var path = _fixture.WriteLoose("44005", PayloadA);
        var source = _fixture.OpenFolder(path);

        Assert.Null(ClassicContainerFacts.TryQuery(source, new AssetReference(source.Id, "44005")));
        var entry = await ClassicContainerFixture.FindEntryAsync(source, "44005");
        Assert.Equal(PayloadA.Length, entry.Length);
    }

    [Fact]
    public void ASourceWithoutFacts_AnswersNull()
    {
        var memory = new InMemoryAssetSource();
        var entry = memory.Add("meshes/44005", PayloadA);

        Assert.Null(ClassicContainerFacts.TryQuery(memory, entry.Reference));
    }

    [Fact]
    public void AbsentPath_AnswersNull_AndAForeignReferenceIsRefused()
    {
        var source = _fixture.OpenArchive(_fixture.WriteNumberedBsa((1u, PayloadA)));

        Assert.Null(ClassicContainerFacts.TryQuery(source, new AssetReference(source.Id, "2")));
        Assert.Throws<ArgumentException>(() =>
            ClassicContainerFacts.TryQuery(source, new AssetReference("another-source", "1")));
    }
}
