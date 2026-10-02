using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BethesdaMultitool.Core.Assets;
using BethesdaMultitool.Core.Formats.Bsa;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Tests.Core.Formats.Bsa;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Assets;

public sealed class AssetSelectionTests
{
    private const string AssetPath = "meshes\\clutter\\bottle.nif";

    [Theory]
    [InlineData("bsa", false)]
    [InlineData("bsa", true)]
    [InlineData("ba2", false)]
    [InlineData("ba2", true)]
    public void DeclaredOrderSelectsBytesAndRetainsShadowedCandidates(string format, bool reverse)
    {
        using var files = new Files();
        var first = files.Archive("first", format, [1, 2, 3]);
        var second = files.Archive("second", format, [4, 5, 6]);
        string[] order = reverse ? [second, first] : [first, second];
        var plan = AssetSourcePlan.FromPaths(order);
        using var session = new AssetSelectionSession(plan);

        var read = session.Read("MESHES/clutter/BOTTLE.NIF");

        byte[] expected = reverse ? [4, 5, 6] : [1, 2, 3];
        Assert.Equal(expected, read.Value);
        Assert.Equal(AssetSelectionStatus.Selected, read.Receipt.Status);
        Assert.Equal(order, read.Receipt.Candidates.Select(candidate => candidate.SourcePath));
        Assert.Equal(order[0], read.Receipt.Selected!.SourcePath);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(expected)), read.Receipt.PayloadSha256);
        Assert.Equal("read", Assert.Single(read.Receipt.Attempts).Status);
        Assert.Equal(read.Receipt, session.LastReceipt(AssetPath));
        Assert.Equal(read.Receipt, Assert.Single(session.Receipts()));
        Assert.True(session.IsCurrent(read.Receipt));
        Assert.Equal(AssetSourcePlan.DeclaredOrderPolicy, read.Receipt.Policy);
        Assert.False(plan.EnginePriorityVerified);
        Assert.Null(Assert.Single(read.Receipt.Attempts).DeclaredMount);
        using var serialized = JsonDocument.Parse(AssetSelectionJson.Serialize([read.Receipt]));
        Assert.False(serialized.RootElement[0].GetProperty("Attempts")[0].TryGetProperty("DeclaredMount", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameArchiveDuplicateOccurrencesRemainAmbiguous(bool differentCase)
    {
        using var files = new Files();
        var duplicate = files.DuplicateArchive(differentCase);
        var fallback = files.Archive("fallback", "bsa", [9]);
        using var session = new AssetSelectionSession(AssetSourcePlan.FromPaths([duplicate, fallback]));

        Assert.Equal(AssetSelectionStatus.Ambiguous, session.Probe(AssetPath).Status);
        var read = session.Read(AssetPath);

        Assert.Equal(AssetSelectionStatus.Ambiguous, read.Receipt.Status);
        Assert.Null(read.Value);
        Assert.Null(read.Receipt.Selected);
        Assert.Null(read.Receipt.PayloadSha256);
        Assert.Equal(new[] { 0, 1, 0 }, read.Receipt.Candidates.Select(candidate => candidate.Occurrence));
        Assert.Equal(2, read.Receipt.Candidates.Take(2).Select(candidate => candidate.Offset).Distinct().Count());
        Assert.Equal("ambiguous-physical-entries", Assert.Single(read.Receipt.Attempts).Status);
        Assert.Equal(duplicate, Assert.Single(read.Receipt.Attempts).DeclaredMount!.SourcePath);
    }

    [Theory]
    [InlineData("missing", false)]
    [InlineData("invalid", true)]
    [InlineData("removed", true)]
    [InlineData("changed", false)]
    public void CandidateNullAttemptsRetainDeclaredMountAcrossSerializationAndCachedObservation(
        string failure, bool withFallback)
    {
        using var files = new Files();
        var primary = failure == "missing"
            ? Path.Combine(files.Root, "missing.ba2")
            : files.Archive("primary", "ba2", [1, 2, 3]);
        if (failure == "invalid") File.WriteAllBytes(primary, "invalid archive"u8.ToArray());
        var origin = Path.Combine(files.Root, "Selected.esm");
        var mounts = new List<AssetMount> { new(primary, AssetMountKind.Archive, "selected-source", origin) };
        if (withFallback) mounts.Add(new(files.Archive("fallback", "bsa", [9]), AssetMountKind.Archive, "donor"));
        var plan = new AssetSourcePlan(mounts);
        var declaredLength = plan.Mounts[0].SourceLength;
        var declaredWriteTicks = plan.Mounts[0].SourceWriteTicks;
        if (failure == "removed") File.Delete(primary);
        if (failure == "changed") File.SetLastWriteTimeUtc(primary, File.GetLastWriteTimeUtc(primary).AddHours(1));
        using var session = new AssetSelectionSession(plan);

        var probe = session.Probe(AssetPath);
        var read = session.Read(AssetPath);
        var attempt = read.Receipt.Attempts[0];
        var snapshot = Assert.IsType<AssetMountSnapshot>(attempt.DeclaredMount);

        Assert.Null(attempt.Candidate);
        Assert.Equal(0, attempt.MountIndex);
        Assert.StartsWith("source-unavailable:", attempt.Status, StringComparison.Ordinal);
        Assert.Equal(withFallback ? AssetSelectionStatus.Selected : AssetSelectionStatus.Unavailable, read.Receipt.Status);
        Assert.Equal(withFallback ? new byte[] { 9 } : null, read.Value);
        Assert.Equal(plan.Identity, read.Receipt.PlanIdentity);
        Assert.Equal(primary, snapshot.SourcePath);
        Assert.Equal(AssetMountKind.Archive, snapshot.Kind);
        Assert.Equal("selected-source", snapshot.Role);
        Assert.Equal(origin, snapshot.Origin);
        Assert.Equal(declaredLength, snapshot.DeclaredSourceLength);
        Assert.Equal(declaredWriteTicks, snapshot.DeclaredSourceWriteTicks);
        Assert.Same(probe.Attempts[0].DeclaredMount, snapshot);
        Assert.Same(snapshot, session.Read(AssetPath).Receipt.Attempts[0].DeclaredMount);

        using var observed = session.CaptureReads();
        session.ObserveCached([read.Receipt]);
        session.ObserveCached([read.Receipt]);
        Assert.Same(read.Receipt, Assert.Single(observed.Receipts));
        using var serialized = JsonDocument.Parse(AssetSelectionJson.Serialize(observed.Receipts));
        var item = serialized.RootElement[0];
        var serializedAttempt = item.GetProperty("Attempts")[0];
        Assert.Equal(attempt.Status, serializedAttempt.GetProperty("Status").GetString());
        Assert.Equal(JsonValueKind.Null, serializedAttempt.GetProperty("Candidate").ValueKind);
        var metadata = serializedAttempt.GetProperty("DeclaredMount");
        Assert.Equal(primary, metadata.GetProperty("SourcePath").GetString());
        Assert.Equal("Archive", metadata.GetProperty("Kind").GetString());
        Assert.Equal("selected-source", metadata.GetProperty("Role").GetString());
        Assert.Equal(origin, metadata.GetProperty("Origin").GetString());
        Assert.Equal("plan-creation-file-stat", metadata.GetProperty("IdentityScope").GetString());
        if (failure == "missing")
        {
            Assert.Equal(JsonValueKind.Null, metadata.GetProperty("DeclaredSourceLength").ValueKind);
            Assert.Equal(JsonValueKind.Null, metadata.GetProperty("DeclaredSourceWriteTicks").ValueKind);
        }
        else
        {
            Assert.Equal(declaredLength!.Value, metadata.GetProperty("DeclaredSourceLength").GetInt64());
            Assert.Equal(declaredWriteTicks!.Value, metadata.GetProperty("DeclaredSourceWriteTicks").GetInt64());
        }
        if (withFallback)
            Assert.False(item.GetProperty("Attempts")[1].TryGetProperty("DeclaredMount", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiscoveryIncludesLooseOverridesOnlyWhenRequested(bool includeLoose)
    {
        using var files = new Files();
        var archive = files.Archive("packed", "bsa", [1]);
        var loose = files.Loose([2, 3]);
        var plugin = Path.Combine(files.Root, "FalloutNV.esm");
        File.WriteAllBytes(plugin, "TES4"u8.ToArray());
        var discovery = AssetSourceDiscovery.Discover(plugin, includeLooseFiles: includeLoose);
        using var session = new AssetSelectionSession(discovery.MeshPlan!);

        var read = session.Read(AssetPath);

        Assert.Equal(includeLoose ? new byte[] { 2, 3 } : [1], read.Value);
        Assert.Equal(includeLoose ? loose : archive, read.Receipt.Selected!.SourcePath);
        Assert.Equal(includeLoose ? 2 : 1, read.Receipt.Candidates.Length);
    }

    [Theory]
    [InlineData("missing", true)]
    [InlineData("removed", false)]
    [InlineData("missing-entry", false)]
    public void UnavailableLooseMountIsDistinctFromAnOrdinaryMissingEntry(string state, bool withFallback)
    {
        using var files = new Files();
        var directory = Path.Combine(files.Root, "declared-loose");
        if (state != "missing") Directory.CreateDirectory(directory);
        var origin = Path.Combine(files.Root, "Selected.esm");
        var mounts = new List<AssetMount> { new(directory, AssetMountKind.LooseDirectory, "selected-source", origin) };
        if (withFallback) mounts.Add(new(files.Archive("fallback", "bsa", [9]), AssetMountKind.Archive, "donor"));
        var plan = new AssetSourcePlan(mounts);
        if (state == "removed") Directory.Delete(directory);
        using var session = new AssetSelectionSession(plan);

        var read = session.Read(AssetPath);

        if (state == "missing-entry")
        {
            Assert.Equal(AssetSelectionStatus.Missing, read.Receipt.Status);
            Assert.Empty(read.Receipt.Attempts);
            Assert.Null(read.Value);
            return;
        }
        Assert.Equal(withFallback ? AssetSelectionStatus.Selected : AssetSelectionStatus.Unavailable, read.Receipt.Status);
        Assert.Equal(withFallback ? new byte[] { 9 } : null, read.Value);
        var attempt = read.Receipt.Attempts[0];
        Assert.Null(attempt.Candidate);
        Assert.Equal("source-unavailable:IOException", attempt.Status);
        var snapshot = Assert.IsType<AssetMountSnapshot>(attempt.DeclaredMount);
        Assert.Equal(directory, snapshot.SourcePath);
        Assert.Equal(AssetMountKind.LooseDirectory, snapshot.Kind);
        Assert.Equal("selected-source", snapshot.Role);
        Assert.Equal(origin, snapshot.Origin);
        Assert.Equal("declared-directory-path", snapshot.IdentityScope);
        Assert.Null(snapshot.DeclaredSourceLength);
        Assert.Null(snapshot.DeclaredSourceWriteTicks);
        using var serialized = JsonDocument.Parse(AssetSelectionJson.Serialize([read.Receipt]));
        var metadata = serialized.RootElement[0].GetProperty("Attempts")[0].GetProperty("DeclaredMount");
        Assert.Equal("LooseDirectory", metadata.GetProperty("Kind").GetString());
        Assert.Equal(directory, metadata.GetProperty("SourcePath").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LooseAppearanceOrReplacementInvalidatesPriorReceiptAndCacheKey(bool initiallyPresent)
    {
        using var files = new Files();
        var archive = files.Archive("packed", "ba2", [1]);
        if (initiallyPresent) files.Loose([2]);
        using var session = new AssetSelectionSession(new AssetSourcePlan([
            new(files.Root, AssetMountKind.LooseDirectory, "explicit-loose"),
            new(archive, AssetMountKind.Archive, "explicit")
        ]));
        var before = session.Read(AssetPath);
        var beforeKey = AssetCacheIdentity.Qualify(session, AssetPath, AssetPath);
        Assert.True(session.IsCurrent(before.Receipt));

        var loose = files.Loose([3, 4, 5, 6]);

        Assert.False(session.IsCurrent(before.Receipt));
        var afterKey = AssetCacheIdentity.Qualify(session, AssetPath, AssetPath);
        Assert.NotEqual(beforeKey, afterKey);
        Assert.Equal(AssetPath, AssetCacheIdentity.PathOf(afterKey));
        var after = session.Read(AssetPath);
        Assert.Equal(new byte[] { 3, 4, 5, 6 }, after.Value);
        Assert.Equal(loose, after.Receipt.Selected!.SourcePath);
        Assert.Equal(4, after.Receipt.Selected.SourceLength);
        Assert.True(session.IsCurrent(after.Receipt));
        Assert.Equal(before.Receipt.Sequence + 1, after.Receipt.Sequence);
        Assert.Equal(after.Receipt, Assert.Single(session.Receipts(before.Receipt.Sequence)));
    }

    [Theory]
    [InlineData("unreadable")]
    [InlineData("oversize")]
    [InlineData("decode-null")]
    [InlineData("decode-throws")]
    public void FailedHigherPrioritySourceFallsBackWithAttempts(string failure)
    {
        using var files = new Files();
        var primary = files.Archive("primary", "ba2", [1, 2, 3, 4]);
        if (failure == "unreadable") File.WriteAllBytes(primary, "invalid archive"u8.ToArray());
        var fallback = files.Archive("fallback", "bsa", [9]);
        using var session = new AssetSelectionSession(AssetSourcePlan.FromPaths([primary, fallback]));
        byte[]? Decode(byte[] bytes)
        {
            if (bytes[0] != 1) return bytes;
            if (failure == "decode-null") return null;
            if (failure == "decode-throws") throw new InvalidDataException("Rejected fixture payload.");
            return bytes;
        }

        var read = session.Read(AssetPath, Decode, failure == "oversize" ? 1 : 32);

        Assert.Equal(new byte[] { 9 }, read.Value);
        Assert.Equal(AssetSelectionStatus.Selected, read.Receipt.Status);
        Assert.Equal(fallback, read.Receipt.Selected!.SourcePath);
        Assert.Equal(2, read.Receipt.Attempts.Length);
        Assert.Equal(0, read.Receipt.Attempts[0].MountIndex);
        Assert.Equal(1, read.Receipt.Attempts[1].MountIndex);
        Assert.Equal("read", read.Receipt.Attempts[1].Status);
        if (failure == "decode-null") Assert.Equal("decode-unavailable", read.Receipt.Attempts[0].Status);
        else Assert.StartsWith(failure == "unreadable" ? "source-unavailable:" : "read-failed:",
            read.Receipt.Attempts[0].Status, StringComparison.Ordinal);
        Assert.False(session.IsCurrent(read.Receipt));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(new byte[] { 9 })), read.Receipt.PayloadSha256);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArchiveStatChangeRejectsStalePlanBeforeOrAfterLazyOpen(bool openBeforeChange)
    {
        using var files = new Files();
        var primary = files.Archive("primary", "bsa", [1]);
        var fallback = files.Archive("fallback", "ba2", [9]);
        using var session = new AssetSelectionSession(AssetSourcePlan.FromPaths([primary, fallback]));
        var before = openBeforeChange ? session.Read(AssetPath).Receipt : null;
        File.SetLastWriteTimeUtc(primary, File.GetLastWriteTimeUtc(primary).AddHours(1));

        var read = session.Read(AssetPath);

        Assert.Equal(new byte[] { 9 }, read.Value);
        Assert.Equal(fallback, read.Receipt.Selected!.SourcePath);
        Assert.StartsWith("source-unavailable:", read.Receipt.Attempts[0].Status, StringComparison.Ordinal);
        if (before is not null) Assert.False(session.IsCurrent(before));
        using var fresh = new AssetSelectionSession(AssetSourcePlan.FromPaths([primary, fallback]));
        Assert.Equal(new byte[] { 1 }, fresh.Read(AssetPath).Value);
        Assert.NotEqual(session.Plan.Identity, fresh.Plan.Identity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransientDecodeFailureChangesAdmissionKeyAndCleanRetryRestoresIt(bool withFallback)
    {
        using var files = new Files();
        var primary = files.Archive("primary", "ba2", [1]);
        var paths = new List<string> { primary };
        if (withFallback) paths.Add(files.Archive("fallback", "bsa", [9]));
        using var session = new AssetSelectionSession(AssetSourcePlan.FromPaths(paths));
        var originalKey = AssetCacheIdentity.Qualify(session, AssetPath, AssetPath);

        var failed = session.Read<byte[]>(AssetPath, bytes => bytes[0] == 1 ? null : bytes);
        var retryKey = AssetCacheIdentity.Qualify(session, AssetPath, AssetPath);

        Assert.Equal(withFallback ? AssetSelectionStatus.Selected : AssetSelectionStatus.Unavailable, failed.Receipt.Status);
        Assert.NotEqual(originalKey, retryKey);
        Assert.False(session.IsCurrent(failed.Receipt));
        var recovered = session.Read(AssetPath);
        Assert.Equal(new byte[] { 1 }, recovered.Value);
        Assert.True(session.IsCurrent(recovered.Receipt));
        Assert.Equal(originalKey, AssetCacheIdentity.Qualify(session, retryKey, AssetPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationDoesNotBecomeMissingOrFallbackSuccess(bool cancelDuringDecode)
    {
        using var files = new Files();
        var first = files.Archive("first", "bsa", [1]);
        var second = files.Archive("second", "ba2", [9]);
        using var session = new AssetSelectionSession(AssetSourcePlan.FromPaths([first, second]));
        using var cancellation = new CancellationTokenSource();
        var decodes = 0;
        if (!cancelDuringDecode) cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => session.Read<byte[]>(AssetPath, _ =>
        {
            decodes++;
            cancellation.Cancel();
            return null;
        }, cancellationToken: cancellation.Token));

        Assert.Equal(cancelDuringDecode ? 1 : 0, decodes);
        Assert.Empty(session.Receipts());
        Assert.Equal(0, session.Sequence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CpuAndGpuResolversSelectTheSameDecodedPixelsAndPhysicalPayload(bool malformedPrimary)
    {
        using var files = new Files();
        const string texture = "textures\\clutter\\bottle.dds";
        byte[] primaryPixels = [24, 48, 72, 255];
        byte[] fallbackPixels = [96, 120, 144, 255];
        var primaryDds = RgbaDds(primaryPixels);
        var fallbackDds = RgbaDds(fallbackPixels);
        var first = files.Archive("textures-first", "bsa",
            malformedPrimary ? "invalid DDS"u8.ToArray() : primaryDds, texture);
        var second = files.Archive("textures-second", "ba2", fallbackDds, texture);
        var plan = AssetSourcePlan.FromPaths([first, second]);
        using var cpu = new NifTextureResolver(plan);
        using var gpu = new NifGpuTextureResolver(plan);

        var cpuTexture = cpu.GetTexture(texture);
        var gpuTexture = gpu.GetTexture(texture);

        Assert.NotNull(cpuTexture);
        Assert.NotNull(gpuTexture);
        var expectedPixels = malformedPrimary ? fallbackPixels : primaryPixels;
        Assert.Equal(expectedPixels, cpuTexture.Pixels);
        Assert.Equal(GpuTexturePayloadFormat.Rgba8, gpuTexture.Format);
        Assert.Equal(expectedPixels, Assert.Single(gpuTexture.MipLevels).Bytes);
        Assert.Equal(cpuTexture.Width, gpuTexture.Width);
        Assert.Equal(cpuTexture.Height, gpuTexture.Height);
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(malformedPrimary ? fallbackDds : primaryDds));
        foreach (var selection in new[] { cpu.AssetSelection!, gpu.AssetSelection! })
        {
            var receipt = selection.LastReceipt(texture);
            Assert.NotNull(receipt);
            Assert.Equal(malformedPrimary ? second : first, receipt.Selected!.SourcePath);
            Assert.Equal(expectedHash, receipt.PayloadSha256);
            Assert.Equal(2, receipt.Candidates.Length);
            Assert.Equal(malformedPrimary ? 2 : 1, receipt.Attempts.Length);
        }
    }

    [Fact]
    public void DirectNormalDdxCompanionAppearanceAndReplacementRefreshPixelsAndBoundReceipts()
    {
        using var files = new Files();
        const string normalPath = "textures\\fixture_n.ddx";
        const string specularPath = "textures\\fixture_s.ddx";
        // This exercises the normal/specular path convention, using valid DDS payloads under
        // DDX source names. DDX container framing/decompression has its own existing fixtures.
        byte[] normalBlock = [128, 128, 0, 0, 0, 0, 0, 0, 128, 128, 0, 0, 0, 0, 0, 0];
        var normal = CompressedDds("ATI2", normalBlock);
        var white = CompressedDds("ATI1", [255, 255, 0, 0, 0, 0, 0, 0]);
        var black = CompressedDds("ATI1", new byte[8]);
        files.Loose(normal, normalPath);
        using var gpu = new NifGpuTextureResolver(AssetSourcePlan.FromPaths([files.Root]));

        var unpaired = gpu.GetTexture(normalPath);
        Assert.NotNull(unpaired);
        Assert.Equal(GpuTexturePayloadFormat.BC5, unpaired.Format);
        Assert.Equal("companion-unavailable", unpaired.Derivation);

        var specular = files.Loose(white, specularPath);
        var paired = gpu.GetTexture(normalPath);
        Assert.NotNull(paired);
        Assert.NotSame(unpaired, paired);
        Assert.Equal(GpuTexturePayloadFormat.BC3, paired.Format);
        Assert.Equal("normal-specular-paired", paired.Derivation);
        AssertAlpha(paired, 255);
        AssertPayload(paired, normalPath, normal);
        AssertPayload(paired, specularPath, white);

        var oldStamp = File.GetLastWriteTimeUtc(specular);
        files.Loose(black, specularPath);
        File.SetLastWriteTimeUtc(specular, oldStamp.AddHours(1));
        var replaced = gpu.GetTexture(normalPath);
        Assert.NotNull(replaced);
        Assert.NotSame(paired, replaced);
        AssertAlpha(replaced, 0);
        AssertPayload(replaced, normalPath, normal);
        AssertPayload(replaced, specularPath, black);
        // Earlier decoded payloads retain their own provenance after the resolver advances.
        AssertPayload(paired, specularPath, white);

        static void AssertPayload(GpuTexturePayload payload, string path, byte[] expected)
        {
            var receipt = Assert.Single(payload.AssetReadReceipts, receipt => receipt.RequestedPath == path);
            Assert.Equal(AssetSelectionStatus.Selected, receipt.Status);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(expected)), receipt.PayloadSha256);
        }

        static void AssertAlpha(GpuTexturePayload payload, byte expected)
        {
            var dds = CompressedDds("DXT5", payload.MipLevels.SelectMany(mip => mip.Bytes).ToArray());
            BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(28, 4), (uint)payload.MipCount);
            var decoded = DdsTextureDecoder.Decode(dds);
            Assert.NotNull(decoded);
            Assert.Equal(4, decoded.Width);
            Assert.Equal(4, decoded.Height);
            Assert.All(Enumerable.Range(0, 16), pixel => Assert.Equal(expected, decoded.Pixels[pixel * 4 + 3]));
        }
    }

    [Fact]
    public async Task DisposalDrainsAdmittedReadAndNeverReopensItsArchive()
    {
        using var files = new Files();
        var archive = files.Archive("packed", "ba2", [1]);
        using var session = new AssetSelectionSession(AssetSourcePlan.FromPaths([archive]));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var disposing = new ManualResetEventSlim();
        using var disposed = new ManualResetEventSlim();
        var token = TestContext.Current.CancellationToken;
        var read = Task.Run(() => session.Read(AssetPath, bytes =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), token));
            return bytes;
        }), token);
        Task? disposal = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10), token));
            disposal = Task.Run(() => { disposing.Set(); session.Dispose(); disposed.Set(); }, token);
            Assert.True(disposing.Wait(TimeSpan.FromSeconds(10), token));
            Assert.False(disposed.Wait(TimeSpan.FromMilliseconds(100), token));
        }
        finally
        {
            release.Set();
            await read.WaitAsync(TimeSpan.FromSeconds(10), token);
            if (disposal is not null) await disposal.WaitAsync(TimeSpan.FromSeconds(10), token);
        }

        Assert.True(disposed.IsSet);
        Assert.Equal(new byte[] { 1 }, (await read).Value);
        Assert.Throws<ObjectDisposedException>(() => session.Probe(AssetPath));
        Assert.Throws<ObjectDisposedException>(() => session.Read(AssetPath));
        session.Dispose();
    }

    private sealed class Files : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("bmt-asset-selection-").FullName;

        internal string Archive(string name, string format, byte[] payload, string assetPath = AssetPath)
        {
            var path = Path.Combine(Root, name + "." + format);
            if (format == "ba2") File.WriteAllBytes(path, ArchiveReaderTests.BuildGnrlBa2(0x4242, assetPath, payload));
            else
            {
                using var writer = BsaWriter.CreateWithAutoFlags([assetPath], compressFiles: false);
                writer.AddFile(assetPath, payload);
                writer.Write(path);
            }
            return path;
        }

        internal string Loose(byte[] payload, string assetPath = AssetPath)
        {
            var path = Path.Combine(Root, assetPath.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, payload);
            return path;
        }

        internal string DuplicateArchive(bool differentCase)
        {
            // Extend the established minimal GNRL fixture with two distinct physical records.
            var path = Path.Combine(Root, "duplicates.ba2");
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream, Encoding.UTF8);
            writer.Write("BTDX"u8.ToArray()); writer.Write(1u); writer.Write("GNRL"u8.ToArray());
            writer.Write(2u); writer.Write(98ul); // 24-byte header + 2*36 records + 2 payload bytes.
            for (var i = 0; i < 2; ++i)
            {
                writer.Write(0x4242u); writer.Write("nif\0"u8.ToArray()); writer.Write(0x9999u);
                writer.Write(0u); writer.Write((ulong)(96 + i)); writer.Write(0u); writer.Write(1u); writer.Write(0u);
            }
            writer.Write(new byte[] { 1, 2 });
            foreach (var name in new[] { AssetPath, differentCase ? AssetPath.ToUpperInvariant() : AssetPath })
            {
                var bytes = Encoding.UTF8.GetBytes(name);
                writer.Write((ushort)bytes.Length); writer.Write(bytes);
            }
            return path;
        }

        public void Dispose() => Directory.Delete(Root, true);
    }

    private static byte[] RgbaDds(byte[] pixel)
    {
        // One RGBA8 DDS texel with explicit byte-order masks; expected colors are independent
        // of either consumer's decoder output.
        var bytes = new byte[132];
        "DDS "u8.CopyTo(bytes);
        Word(4, 124); Word(8, 0x100F); Word(12, 1); Word(16, 1); Word(20, 4); Word(28, 1);
        Word(76, 32); Word(80, 0x41); Word(88, 32);
        Word(92, 0x000000FF); Word(96, 0x0000FF00); Word(100, 0x00FF0000); Word(104, 0xFF000000);
        Word(108, 0x1000);
        pixel.CopyTo(bytes, 128);
        return bytes;

        void Word(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    }

    private static byte[] CompressedDds(string fourCc, byte[] blocks)
    {
        var bytes = new byte[128 + blocks.Length];
        "DDS "u8.CopyTo(bytes);
        Word(4, 124); Word(8, 0x81007); Word(12, 4); Word(16, 4); Word(20, (uint)blocks.Length); Word(28, 1);
        Word(76, 32); Word(80, 4); Encoding.ASCII.GetBytes(fourCc).CopyTo(bytes, 84); Word(108, 0x1000);
        blocks.CopyTo(bytes, 128);
        return bytes;

        void Word(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    }
}
