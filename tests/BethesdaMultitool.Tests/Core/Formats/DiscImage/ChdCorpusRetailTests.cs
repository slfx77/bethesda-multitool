using System.Diagnostics;
using BethesdaMultitool.Core.Formats.DiscImage.Chd;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.DiscImage;

/// <summary>
///     The proof that the native CHD reader decodes what chdman encoded, over the real corpus rather
///     than over synthetic fixtures.
///     <para>
///         ⚑ The oracle is the container's own header: chdman writes the SHA-1 of the ORIGINAL image
///         into every CHD it creates (what <c>chdman info</c> prints as "Data SHA1"), and nothing in
///         this reader can see that field while decoding. So decoding all of a CHD and reproducing
///         that hash means our bytes ARE the bytes chdman was handed — a whole-image identity, not a
///         sample. The per-hunk CRC-16 the map carries is checked on every hunk on the way, so a
///         corrupt container fails before the hash does and says which hunk.
///     </para>
///     <para>
///         ⚠ This decodes every stored byte of the corpus — tens of gigabytes — so the sweep is
///         opt-in twice over: <c>RUN_BUCKET_B=1</c> like every real-asset test, and then
///         <c>RUN_CHD_SWEEP=1</c> for the whole corpus. Without the second it verifies one CD-shaped
///         and one DVD-shaped image, which between them exercise every codec the corpus stores.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class ChdCorpusRetailTests
{
    private const string SweepVariable = "RUN_CHD_SWEEP";
    private const int CopyBufferSize = 1 << 20;

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BethesdaMultitool.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? string.Empty;
    }

    private static IReadOnlyList<string> StoredChds()
    {
        var repository = RepositoryRoot();
        var builds = Path.Combine(repository, "Sample", "Builds");
        if (repository.Length == 0 || !Directory.Exists(builds))
        {
            return [];
        }

        // Media is shared with the other tools; sweep only this project's generated builds.
        var media = Path.GetFullPath(Path.Combine(repository, "..", "Media"));
        return Directory.EnumerateDirectories(builds)
            .Select(build => Path.Combine(media, Path.GetFileName(build)))
            .Where(Directory.Exists)
            .SelectMany(build => Directory.EnumerateFiles(build, "*.chd", SearchOption.AllDirectories))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    ///     One CD-shaped and one DVD-shaped image decoded in full: between them they exercise the CD
    ///     framing with its ECC regeneration and the plain DVD path.
    /// </summary>
    [Fact]
    public void ARepresentativeStoredImageOfEachShapeDecodesToItsRecordedHash()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var stored = StoredChds();
        Assert.SkipWhen(stored.Count == 0, "No CHD media is staged.");

        string? cd = null;
        string? dvd = null;
        foreach (var path in stored)
        {
            using var probe = ChdFile.Open(path);
            if (probe.IsCdShaped)
            {
                cd ??= path;
            }
            else
            {
                dvd ??= path;
            }

            if (cd is not null && dvd is not null)
            {
                break;
            }
        }

        var verified = 0;
        foreach (var path in new[] { cd, dvd }.OfType<string>())
        {
            VerifyWholeImage(path);
            verified++;
        }

        // Without this the test passes vacuously when the corpus holds CHDs of neither shape.
        Assert.True(verified > 0, "no stored CHD was verified");
    }

    /// <summary>Every stored CHD in the corpus, decoded end to end. Opt in with <c>RUN_CHD_SWEEP=1</c>.</summary>
    [Fact]
    public void EveryStoredImageDecodesToItsRecordedHash()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(SweepVariable) == "1",
            $"Set {SweepVariable}=1 to decode every stored CHD (tens of GB).");

        var stored = StoredChds();
        Assert.SkipWhen(stored.Count == 0, "No CHD media is staged.");

        var failures = new List<string>();
        foreach (var path in stored)
        {
            try
            {
                VerifyWholeImage(path);
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException or Xunit.Sdk.XunitException)
            {
                failures.Add($"{Path.GetFileName(path)}: {e.Message}");
            }
        }

        Assert.Empty(failures);
    }

    /// <summary>
    ///     A second, independent oracle for one image: chdman's own extraction, compared byte for
    ///     byte against this reader's output. Skipped when chdman is not on the machine — the hash
    ///     check above is the primary proof and needs no external tool.
    /// </summary>
    [Fact]
    public void OneImageMatchesChdmansOwnExtractionByteForByte()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var chdman = ChdFixture.ChdmanPath();
        Assert.SkipWhen(chdman is null, $"chdman not found (set {ChdFixture.ChdmanVariable}).");

        // The smallest stored image, so the comparison stays a test rather than an errand.
        var smallest = StoredChds()
            .Select(p => (Path: p, Length: new FileInfo(p).Length))
            .OrderBy(p => p.Length)
            .Select(p => p.Path)
            .FirstOrDefault();
        Assert.SkipWhen(smallest is null, "No CHD media is staged.");

        using var chd = ChdFile.Open(smallest);
        var scratch = Path.Combine(Path.GetTempPath(), $"chd-oracle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            var stem = Path.GetFileNameWithoutExtension(smallest);
            var sheetOrImage = Path.Combine(scratch, stem + (chd.IsCdShaped ? ".cue" : ".iso"));
            var payload = chd.IsCdShaped ? Path.Combine(scratch, stem + ".bin") : sheetOrImage;
            string[] arguments = chd.IsCdShaped
                ? ["extractcd", "-i", smallest, "-o", sheetOrImage, "-ob", payload, "-f"]
                : ["extractdvd", "-i", smallest, "-o", sheetOrImage, "-f"];

            var start = new ProcessStartInfo(chdman!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start);
            Assert.NotNull(process);
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
            Assert.True(File.Exists(payload), $"chdman produced no {payload}");

            // ⚠ Compare only what both forms hold: a chdman CD extraction writes 2352-byte sectors
            // with the subcode dropped, while the CHD's logical image is 2448-byte frames. For a DVD
            // the two are the same bytes.
            using var oracle = File.OpenRead(payload);
            var ours = OurBytes(chd);
            var oracleBuffer = new byte[CopyBufferSize];
            var ourBuffer = new byte[CopyBufferSize];
            long offset = 0;
            while (true)
            {
                var fromOracle = oracle.Read(oracleBuffer, 0, oracleBuffer.Length);
                if (fromOracle == 0)
                {
                    // Both sides must end together: ours reading on would mean it decoded more.
                    Assert.Equal(0, ours.Read(ourBuffer.AsSpan(0, 1)));
                    break;
                }

                // Exactly as many bytes as the oracle produced, so the two stay in step.
                var fromUs = ours.ReadAtLeast(ourBuffer.AsSpan(0, fromOracle), fromOracle, throwOnEndOfStream: false);

                Assert.True(fromUs >= fromOracle, $"our decode ended at {offset + fromUs:N0} bytes; chdman wrote more");
                Assert.True(
                    ourBuffer.AsSpan(0, fromOracle).SequenceEqual(oracleBuffer.AsSpan(0, fromOracle)),
                    $"{Path.GetFileName(smallest)} differs from chdman's extraction within bytes {offset:N0}..{offset + fromOracle:N0}");
                offset += fromOracle;
            }

            Assert.True(offset > 0, "chdman's extraction was empty");
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static void VerifyWholeImage(string path)
    {
        using var chd = ChdFile.Open(path);
        Assert.False(chd.HasParent, $"{Path.GetFileName(path)} is a delta CHD, which the corpus never writes.");

        var decoded = Convert.ToHexString(chd.ComputeRawSha1()).ToLowerInvariant();
        Assert.Equal(chd.RawSha1Hex, decoded);
    }

    /// <summary>
    ///     Our decode in the shape chdman's extraction has: for a CD the 2352-byte sector of every
    ///     frame, read through <see cref="ChdSectorSource" /> so an audio track gets the byte swap
    ///     a <c>.bin</c> needs; for a DVD the whole logical image.
    /// </summary>
    private static Stream OurBytes(ChdFile chd)
    {
        return chd.IsCdShaped
            ? new SectorOnlyStream(new ChdSectorSource(chd))
            : new ChdStream(chd, ownsFile: false);
    }

    /// <summary>The 2352-byte sectors of every track in LBA order — what a chdman CD extraction writes.</summary>
    private sealed class SectorOnlyStream(ChdSectorSource inner) : Stream
    {
        private readonly byte[] _sector = new byte[ChdFile.CdSectorSize];
        private long _position;
        private long _loaded = -1;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.SectorCount * ChdFile.CdSectorSize;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            var total = 0;
            while (buffer.Length > 0 && _position < Length)
            {
                var lba = _position / ChdFile.CdSectorSize;
                var within = (int)(_position % ChdFile.CdSectorSize);
                if (_loaded != lba)
                {
                    var size = inner.ReadRawSector(lba, _sector);
                    if (size != ChdFile.CdSectorSize)
                    {
                        throw new InvalidDataException($"LBA {lba} is a {size}-byte track; a chdman comparison needs raw 2352-byte sectors.");
                    }

                    _loaded = lba;
                }

                var take = Math.Min(ChdFile.CdSectorSize - within, buffer.Length);
                _sector.AsSpan(within, take).CopyTo(buffer);
                buffer = buffer[take..];
                _position += take;
                total += take;
            }

            return total;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
