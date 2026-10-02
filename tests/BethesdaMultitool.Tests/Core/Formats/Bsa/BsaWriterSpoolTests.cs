using BethesdaMultitool.Core.Formats.Bsa;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Bsa;

public sealed class BsaWriterSpoolTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Colliding_output_paths_coalesce_only_identical_spool_regions(bool identical)
    {
        using var spool = new AssetSpool();
        var first = spool.Append("Sound/Voice/line.ogg", [1, 2, 3], CancellationToken.None);
        var second = spool.Append("sound\\voice\\LINE.ogg", [1, 2, identical ? (byte)3 : (byte)4], CancellationToken.None);
        spool.Seal();
        if (identical)
        {
            var selected = Assert.Single(PackedAsset.CoalesceOutputPaths([second, first], CancellationToken.None));
            using var output = new MemoryStream();
            selected.CopyTo(output, CancellationToken.None);
            Assert.Equal(new byte[] { 1, 2, 3 }, output.ToArray());
        }
        else
        {
            var forward = Assert.Throws<InvalidDataException>(() => PackedAsset.CoalesceOutputPaths([first, second], CancellationToken.None));
            var reverse = Assert.Throws<InvalidDataException>(() => PackedAsset.CoalesceOutputPaths([second, first], CancellationToken.None));
            Assert.Equal(forward.Message, reverse.Message);
            Assert.Contains("sound\\voice\\line.ogg", forward.Message);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void File_regions_preserve_archive_bytes_and_extracted_payloads(bool compressed, bool embedded)
    {
        using var fixture = new Fixture();
        var first = new byte[200003];
        new Random(27).NextBytes(first);
        byte[] voice = [0x4f, 0x67, 0x67, 0x53, 1, 2, 3, 4];
        var source = Path.Combine(fixture.Root, "source.bin");
        File.WriteAllBytes(source, [0xAA, ..first, ..voice, 0xBB]);
        using var diskWriter = new BsaWriter(compressed, embedFileNames: embedded);
        diskWriter.AddFileFromDisk("meshes\\test.nif", source, 1, first.Length);
        diskWriter.AddFileFromDisk("sound\\voice\\test.esp\\voice\\line.ogg", source, 1 + first.Length, voice.Length);
        using var memoryWriter = new BsaWriter(compressed, embedFileNames: embedded);
        memoryWriter.AddFile("meshes\\test.nif", first);
        memoryWriter.AddFile("sound\\voice\\test.esp\\voice\\line.ogg", voice);
        using var expected = new MemoryStream();
        memoryWriter.Write(expected);
        using var actual = new NonSeekingOutput();
        diskWriter.Write(actual);
        Assert.Equal(expected.ToArray(), actual.Bytes);
        Assert.InRange(actual.LargestWrite, 1, 65536);
        var output = Path.Combine(fixture.Root, "roundtrip.bsa");
        File.WriteAllBytes(output, actual.Bytes);
        using var archive = ArchiveReader.Open(output);
        Assert.Equal(first, archive.ReadFile("meshes\\test.nif"));
        Assert.Equal(voice, archive.ReadFile("sound\\voice\\test.esp\\voice\\line.ogg"));
    }

    [Fact]
    public void Output_equal_to_registered_input_is_rejected_before_truncation()
    {
        using var fixture = new Fixture();
        var source = Path.Combine(fixture.Root, "source.bin");
        byte[] original = [1, 2, 3, 4];
        File.WriteAllBytes(source, original);
        using var writer = new BsaWriter(false);
        writer.AddFileFromDisk("meshes\\test.nif", source);
        Assert.Throws<IOException>(() => writer.Write(Path.Combine(fixture.Root, ".", "source.bin")));
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    [Fact]
    public void A_truncated_registered_source_fails_instead_of_publishing_short_data()
    {
        using var fixture = new Fixture();
        var source = Path.Combine(fixture.Root, "source.bin");
        File.WriteAllBytes(source, new byte[100]);
        using var writer = new BsaWriter(false);
        writer.AddFileFromDisk("meshes\\test.nif", source, 20, 80);
        File.WriteAllBytes(source, new byte[30]);
        using var output = new MemoryStream();
        Assert.Throws<EndOfStreamException>(() => writer.Write(output));
    }

    [Fact]
    public void Cancellation_during_output_copy_stops_and_writer_can_be_reused()
    {
        using var fixture = new Fixture();
        var source = Path.Combine(fixture.Root, "source.bin");
        File.WriteAllBytes(source, new byte[300000]);
        using var writer = new BsaWriter(false);
        writer.AddFileFromDisk("meshes\\test.nif", source);
        using var cancellation = new CancellationTokenSource();
        using var interrupted = new NonSeekingOutput(() => cancellation.Cancel());
        Assert.Throws<OperationCanceledException>(() => writer.Write(interrupted, cancellation.Token));
        Assert.InRange(interrupted.Bytes.Length, 65536, 131072);
        using var retry = new MemoryStream();
        writer.Write(retry);
        Assert.True(retry.Length > 300000);
    }

    [Fact]
    public void Conversion_spool_owns_payloads_until_disposal_and_then_removes_its_file()
    {
        var spool = new AssetSpool();
        var spoolPath = spool.Path;
        try
        {
            byte[] first = [1, 2, 3];
            var retained = spool.Append("a", first, CancellationToken.None);
            spool.Append("b", [4, 5], CancellationToken.None);
            first[0] = 99;
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => spool.Append("c", [6], cancelled.Token));
            spool.Seal();
            using var output = new MemoryStream();
            retained.CopyTo(output, CancellationToken.None);
            Assert.Equal(new byte[] { 1, 2, 3 }, output.ToArray());
            Assert.Equal(5, new FileInfo(spoolPath).Length);
        }
        finally { spool.Dispose(); }
        Assert.False(File.Exists(spoolPath));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "bmt-writer-fixture-" + Guid.NewGuid().ToString("N"))).FullName;
        public void Dispose() => Directory.Delete(Root, true);
    }

    private sealed class NonSeekingOutput(Action? cancelAfterPayload = null) : Stream
    {
        private readonly MemoryStream _data = new();
        internal byte[] Bytes => _data.ToArray();
        internal int LargestWrite { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            LargestWrite = Math.Max(LargestWrite, count);
            _data.Write(buffer, offset, count);
            if (_data.Length >= 65536) cancelAfterPayload?.Invoke();
        }
        protected override void Dispose(bool disposing) { if (disposing) _data.Dispose(); base.Dispose(disposing); }
    }
}
