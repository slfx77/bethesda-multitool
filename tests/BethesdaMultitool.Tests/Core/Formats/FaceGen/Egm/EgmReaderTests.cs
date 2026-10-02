using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.FaceGen.Egm;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.FaceGen.Egm;

/// <summary>Verifies complete EGM representation and malformed boundaries with authored synthetic bytes.</summary>
public sealed class EgmReaderTests
{
    /// <summary>The basis word is retained separately from forty reserved bytes, and both mode families cover every vertex.</summary>
    [Fact]
    public void RetainsBasisSourceIdentityAndAllPackedVertices()
    {
        var bytes = EgmFixture.Create();
        bytes[24] = 0xA7;
        bytes[63] = 0x13;
        var document = EgmReader.Read(bytes, TestContext.Current.CancellationToken);
        Assert.Equal(5, document.VertexCount);
        Assert.Equal(2001060901u, document.BasisKey);
        Assert.Equal(40, document.Reserved.Length);
        Assert.Equal(0xA7, document.Reserved.Span[0]);
        Assert.Equal(0x13, document.Reserved.Span[^1]);
        Assert.Equal(2, document.SymmetricModes.Count);
        Assert.Single(document.AsymmetricModes);
        Assert.Equal(15, document.SymmetricModes[0].PackedDeltas.Length);
        Assert.Equal(new Vector3(0, 5, 0), document.SymmetricModes[0].GetDelta(4));
        Assert.Equal(new Vector3(0, 0, -2), document.SymmetricModes[1].GetDelta(4));
        Assert.Equal(new Vector3(2, 0, 0), document.AsymmetricModes[0].GetDelta(4));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), document.SourceHash);
        Assert.Equal(166, document.EncodedSize);
        Assert.Throws<ArgumentOutOfRangeException>(() => document.AsymmetricModes[0].GetDelta(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.AsymmetricModes[0].GetDelta(5));
        var unknownBasis = EgmReader.Read(EgmFixture.Create(0xFEDCBA98), TestContext.Current.CancellationToken);
        Assert.Equal(0xFEDCBA98u, unknownBasis.BasisKey);
    }

    /// <summary>Every proper prefix and a surplus suffix fail exact document framing.</summary>
    [Fact]
    public void RejectsAllTruncationsTrailingBytesAndUnknownMagic()
    {
        var bytes = EgmFixture.Create();
        for (var length = 0; length < bytes.Length; length++)
        {
            var prefix = bytes[..length];
            Assert.Throws<InvalidDataException>(() => EgmReader.Read(prefix, TestContext.Current.CancellationToken));
        }
        Assert.Throws<InvalidDataException>(() => EgmReader.Read([.. bytes, 1], TestContext.Current.CancellationToken));
        bytes[0] = 0;
        Assert.Throws<NotSupportedException>(() => EgmReader.Read(bytes, TestContext.Current.CancellationToken));
    }

    /// <summary>Hostile unsigned counts are rejected before allocations and valid zero-mode documents remain inspectable.</summary>
    [Fact]
    public void EnforcesCountCapsAndExactArithmetic()
    {
        foreach (var offset in new[] { 8, 12, 16 })
        {
            var bytes = EgmFixture.Create();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), uint.MaxValue);
            Assert.Throws<NotSupportedException>(() => EgmReader.Read(bytes, TestContext.Current.CancellationToken));
        }
        var empty = EgmFixture.Create();
        BinaryPrimitives.WriteUInt32LittleEndian(empty.AsSpan(8), 0);
        Assert.Throws<InvalidDataException>(() => EgmReader.Read(empty, TestContext.Current.CancellationToken));
        var zeroModes = EgmFixture.Create()[..64];
        BinaryPrimitives.WriteUInt32LittleEndian(zeroModes.AsSpan(12), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(zeroModes.AsSpan(16), 0);
        var zero = EgmReader.Read(zeroModes, TestContext.Current.CancellationToken);
        Assert.Equal(5, zero.VertexCount);
        Assert.Empty(zero.SymmetricModes);
        Assert.Empty(zero.AsymmetricModes);
    }

    /// <summary>Rejects non-finite stored scales and overflowing products despite otherwise finite source fields.</summary>
    [Fact]
    public void RejectsNonFiniteAndOverflowingModes()
    {
        foreach (var value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var bytes = EgmFixture.Create();
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(64), value);
            Assert.Throws<InvalidDataException>(() => EgmReader.Read(bytes, TestContext.Current.CancellationToken));
        }
        var overflow = EgmFixture.Create();
        BinaryPrimitives.WriteSingleLittleEndian(overflow.AsSpan(64), float.MaxValue);
        Assert.Throws<InvalidDataException>(() => EgmReader.Read(overflow, TestContext.Current.CancellationToken));
    }

    /// <summary>File length is bounded before allocation, handles are released and pre-cancellation precedes file opening.</summary>
    /// <returns>A task completing after bounded I/O and cancellation controls, with its authored temporary file removed.</returns>
    [Fact]
    public async Task BoundsFileReadsAndObservesCancellation()
    {
        var directory = Path.Combine("TestOutput", "egm-reader");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".egm");
        try
        {
            var bytes = EgmFixture.Create();
            await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
            var document = await EgmReader.ReadFileAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(166, document.EncodedSize);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(EgmReader.MaximumEncodedBytes + 1L);
            }
            await Assert.ThrowsAsync<NotSupportedException>(() => EgmReader.ReadFileAsync(path, TestContext.Current.CancellationToken));
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();
            Assert.Throws<OperationCanceledException>(() => EgmReader.Read(bytes, cancellation.Token));
            await Assert.ThrowsAsync<OperationCanceledException>(() => EgmReader.ReadFileAsync(path + ".missing", cancellation.Token));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
