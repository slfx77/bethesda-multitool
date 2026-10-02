using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using BethesdaMultitool.CLI.Commands.FaceGen;
using BethesdaMultitool.Core.Formats.FaceGen.Tri;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.FaceGen.Tri;

/// <summary>Checks the official section shapes and malformed boundaries with synthetic bytes only.</summary>
public sealed class TriReaderTests
{
    /// <summary>Distinguishes dense relative shorts from sparse absolute statistical targets and retains every optional section.</summary>
    [Fact]
    public void ReadsBothFamiliesAndOptionalSectionsWithoutFlattening()
    {
        var fixture = TriFixture.Create(quad: true);
        fixture.Bytes[48] = 0x81;
        var document = TriReader.Read(fixture.Bytes, TestContext.Current.CancellationToken);
        Assert.Equal(3, document.Header.VertexCount);
        Assert.Equal(2, document.Header.StatisticalVertexCount);
        Assert.Equal([0, 1, 2], document.Triangles.ToArray());
        Assert.Equal([0, 1, 2, 0], document.Quads.ToArray());
        Assert.Equal([2, 1, 3], document.TriangleTextureIndices.ToArray());
        Assert.Equal([0, 1, 2, 3], document.QuadTextureIndices.ToArray());
        Assert.Equal(4, document.TextureCoordinates.Length);
        Assert.Equal("vé", Assert.Single(document.VertexLabels).Label.Text);
        var surface = Assert.Single(document.SurfaceLabels);
        Assert.Equal(-7, surface.SurfaceIndex);
        Assert.Equal(new Vector3(0.2f, 0.3f, 0.5f), surface.Coordinates);
        Assert.Equal("é名", surface.Label.Text);
        Assert.Equal(2, surface.Label.CodeUnitWidth);
        Assert.Equal(new byte[] { 0xE9, 0, 0x0D, 0x54 }, surface.Label.Bytes.ToArray());
        Assert.Equal(0x81, document.Header.Reserved.Span[0]);
        var delta = Assert.Single(document.DifferentialMorphs);
        Assert.Equal("Ee", delta.Label.Text);
        Assert.Equal(new Vector3(0, -2, 0), delta.GetDelta(1));
        Assert.Equal((short)-4, delta.PackedDeltas.Span[4]);
        Assert.Equal(3, document.StatisticalMorphs[0].FirstTargetVertex);
        Assert.Equal(new Vector3(4, 0, 0), document.StatisticalMorphs[0].Targets.Span[0]);
        Assert.Equal(4, document.StatisticalMorphs[1].FirstTargetVertex);
        Assert.Equal([2], document.StatisticalMorphs[1].VertexIndices.ToArray());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(fixture.Bytes)), document.SourceHash);
        Assert.Equal(fixture.Bytes.Length, document.EncodedSize);
    }

    /// <summary>Exercises the alternate per-vertex UV layout and byte-width surface-label branch.</summary>
    [Fact]
    public void ReadsPerVertexUvsAndNarrowSurfaceLabels()
    {
        var document = TriReader.Read(TriFixture.Create(false, false).Bytes, TestContext.Current.CancellationToken);
        Assert.Equal(0, document.Header.TextureCoordinateCount);
        Assert.Equal(3, document.TextureCoordinates.Length);
        Assert.True(document.TriangleTextureIndices.IsEmpty);
        Assert.Equal("éa", document.SurfaceLabels[0].Label.Text);
    }

    /// <summary>Distinguishes an absent UV section and an otherwise well-framed unused statistical target.</summary>
    [Fact]
    public void HandlesNoUvsAndRejectsUnusedStatisticalTargets()
    {
        var fixture = TriFixture.Create();
        var offset = fixture.Offsets["uvs"];
        byte[] noUvs = [.. fixture.Bytes.AsSpan(0, offset), .. fixture.Bytes.AsSpan(offset + 44)];
        BinaryPrimitives.WriteInt32LittleEndian(noUvs.AsSpan(28), 0);
        BinaryPrimitives.WriteInt32LittleEndian(noUvs.AsSpan(32), 2);
        var document = TriReader.Read(noUvs, TestContext.Current.CancellationToken);
        Assert.True(document.TextureCoordinates.IsEmpty);
        Assert.True(document.TriangleTextureIndices.IsEmpty);
        Assert.Equal(2, document.StatisticalMorphs.Count);
        var targetEnd = fixture.Offsets["triangles"];
        byte[] unusedTarget = [.. fixture.Bytes.AsSpan(0, targetEnd), .. new byte[12], .. fixture.Bytes.AsSpan(targetEnd)];
        BinaryPrimitives.WriteInt32LittleEndian(unusedTarget.AsSpan(44), 3);
        Assert.Throws<InvalidDataException>(() => TriReader.Read(unusedTarget, TestContext.Current.CancellationToken));
    }

    /// <summary>Checks bounded file reads, released handles, cancellation before opening and Windows command-host routing.</summary>
    [Fact]
    public async Task FileReadsAreBoundedAndTriSelectsCliHost()
    {
        var directory = Path.Combine("TestOutput", "tri-reader");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tri");
        try
        {
            var bytes = TriFixture.Create().Bytes;
            await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
            var document = await TriReader.ReadFileAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(bytes.Length, document.EncodedSize);
            using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                exclusive.SetLength(TriReader.MaximumEncodedBytes + 1L);
            }
            await Assert.ThrowsAsync<NotSupportedException>(() => TriReader.ReadFileAsync(path, TestContext.Current.CancellationToken));
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => TriReader.ReadFileAsync(path + ".missing", cancellation.Token));
            Assert.True(Program.ShouldRunCli(["tri", "inspect", path]));
            Assert.True(Program.ShouldRunCli(["--plain", "tri", "inspect", path]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Every proper prefix and an unknown suffix must fail instead of yielding a partial document.</summary>
    [Fact]
    public void RejectsEveryTruncationAndTrailingBytes()
    {
        var bytes = TriFixture.Create().Bytes;
        for (var length = 0; length < bytes.Length; length++)
        {
            var prefix = bytes[..length];
            Assert.Throws<InvalidDataException>(() => TriReader.Read(prefix, TestContext.Current.CancellationToken));
        }
        Assert.Throws<InvalidDataException>(() => TriReader.Read([.. bytes, 7], TestContext.Current.CancellationToken));
        bytes[0] = (byte)'X';
        Assert.Throws<NotSupportedException>(() => TriReader.Read(bytes, TestContext.Current.CancellationToken));
    }

    /// <summary>Exercises count signs, allocation caps, unknown extension bits and each independently indexed section.</summary>
    [Fact]
    public void RejectsInvalidCountsAndIndicesBeforeAllocation()
    {
        foreach (var offset in new[] { 8, 12, 16, 20, 24, 28, 36, 40, 44 })
        {
            var bytes = TriFixture.Create().Bytes;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), -1);
            Assert.Throws<InvalidDataException>(() => TriReader.Read(bytes, TestContext.Current.CancellationToken));
        }
        foreach (var field in new[] { "triangles", "vertexLabelIndex", "uvIndices", "statIndex" })
        {
            var fixture = TriFixture.Create();
            BinaryPrimitives.WriteInt32LittleEndian(fixture.Bytes.AsSpan(fixture.Offsets[field]), int.MaxValue);
            Assert.Throws<InvalidDataException>(() => TriReader.Read(fixture.Bytes, TestContext.Current.CancellationToken));
        }
        var oversized = TriFixture.Create().Bytes;
        BinaryPrimitives.WriteInt32LittleEndian(oversized.AsSpan(8), int.MaxValue);
        Assert.Throws<NotSupportedException>(() => TriReader.Read(oversized, TestContext.Current.CancellationToken));
        var flags = TriFixture.Create().Bytes;
        BinaryPrimitives.WriteInt32LittleEndian(flags.AsSpan(32), 4);
        Assert.Throws<NotSupportedException>(() => TriReader.Read(flags, TestContext.Current.CancellationToken));
        var statistical = TriFixture.Create();
        BinaryPrimitives.WriteInt32LittleEndian(statistical.Bytes.AsSpan(statistical.Offsets["statCount"]), 3);
        Assert.Throws<InvalidDataException>(() => TriReader.Read(statistical.Bytes, TestContext.Current.CancellationToken));
    }

    /// <summary>Rejects non-finite stored fields and packed products that overflow despite a finite scale.</summary>
    [Fact]
    public void RejectsNonFiniteCoordinatesAndDecodedDeltas()
    {
        foreach (var field in new[] { "vertices", "uvs", "scale" })
        {
            var fixture = TriFixture.Create();
            BinaryPrimitives.WriteSingleLittleEndian(fixture.Bytes.AsSpan(fixture.Offsets[field]), float.NaN);
            Assert.Throws<InvalidDataException>(() => TriReader.Read(fixture.Bytes, TestContext.Current.CancellationToken));
        }
        var overflow = TriFixture.Create();
        BinaryPrimitives.WriteSingleLittleEndian(overflow.Bytes.AsSpan(overflow.Offsets["scale"]), float.MaxValue);
        Assert.Throws<InvalidDataException>(() => TriReader.Read(overflow.Bytes, TestContext.Current.CancellationToken));
    }

    /// <summary>Checks label bounds and terminator semantics independently of the remaining section arithmetic.</summary>
    [Fact]
    public void RejectsMalformedMorphLabels()
    {
        var missing = TriFixture.Create();
        missing.Bytes[missing.Offsets["morphLabel"] + 2] = 1;
        Assert.Throws<InvalidDataException>(() => TriReader.Read(missing.Bytes, TestContext.Current.CancellationToken));
        var embedded = TriFixture.Create();
        embedded.Bytes[embedded.Offsets["morphLabel"]] = 0;
        Assert.Throws<InvalidDataException>(() => TriReader.Read(embedded.Bytes, TestContext.Current.CancellationToken));
        var excessive = TriFixture.Create();
        BinaryPrimitives.WriteInt32LittleEndian(excessive.Bytes.AsSpan(excessive.Offsets["morphLabelLength"]), TriReader.MaximumLabelUnits + 1);
        Assert.Throws<NotSupportedException>(() => TriReader.Read(excessive.Bytes, TestContext.Current.CancellationToken));
    }

    /// <summary>Labels remain exact and ambiguous labels never silently select the first matching family.</summary>
    [Fact]
    public void MorphIdentityRejectsMissingAndAmbiguousLabels()
    {
        var document = TriReader.Read(TriFixture.Create().Bytes, TestContext.Current.CancellationToken);
        Assert.Equal(new TriMorphReference(TriMorphKind.Differential, 0), document.ResolveMorph("Ee"));
        Assert.Equal(new TriMorphReference(TriMorphKind.Statistical, 1), document.ResolveMorph("Look"));
        Assert.Throws<KeyNotFoundException>(() => document.ResolveMorph("Eee"));
        Assert.Throws<KeyNotFoundException>(() => document.ResolveMorph("ee"));
        var ambiguous = TriReader.Read(TriFixture.Create(firstStatisticalName: "Ee").Bytes, TestContext.Current.CancellationToken);
        Assert.Throws<InvalidDataException>(() => ambiguous.ResolveMorph("Ee"));
    }

    /// <summary>Complete JSON retains signed shorts, scale, labels and both target/UV domains without disposing caller output.</summary>
    [Fact]
    public void FullJsonRetainsRawRepresentationAndCancellation()
    {
        var bytes = TriFixture.Create().Bytes;
        var document = TriReader.Read(bytes, TestContext.Current.CancellationToken);
        using var output = new MemoryStream();
        TriCommand.WriteJson(output, document, true, TestContext.Current.CancellationToken);
        Assert.True(output.CanWrite);
        using var json = JsonDocument.Parse(output.ToArray());
        var root = json.RootElement;
        Assert.Equal(document.SourceHash, root.GetProperty("sha256").GetString());
        Assert.Equal(-4, root.GetProperty("differentialMorphs")[0].GetProperty("packedDeltasXYZ")[4].GetInt32());
        Assert.Equal(2, root.GetProperty("statisticalMorphs").GetArrayLength());
        Assert.Equal(6, root.GetProperty("statisticalTargetsXYZ")[5].GetSingle());
        Assert.Equal(8, root.GetProperty("textureCoordinatesUV").GetArrayLength());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => TriReader.Read(bytes, cancellation.Token));
        using var cancelledOutput = new MemoryStream();
        Assert.Throws<OperationCanceledException>(() => TriCommand.WriteJson(cancelledOutput, document, true, cancellation.Token));
        Assert.Equal(0, cancelledOutput.Length);
    }
}
