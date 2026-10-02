using System.Buffers.Binary;
using System.Security.Cryptography;

namespace BethesdaMultitool.Core.Formats.FaceGen.Egm;

/// <summary>Reads the original little-endian FREGM002 layout with exact length, bounded allocations and finite modes.</summary>
public static class EgmReader
{
    /// <summary>Maximum encoded input supported by this bounded reader.</summary>
    public const int MaximumEncodedBytes = 16 * 1024 * 1024;
    /// <summary>Maximum complete vertex domain supported by this reader.</summary>
    public const int MaximumVertices = 500_000;
    /// <summary>Maximum symmetric or asymmetric modes, matching the existing carving profile's independent family caps.</summary>
    public const int MaximumModesPerFamily = 200;

    /// <summary>Reads one bounded file and releases its handle before returning owned document data.</summary>
    /// <param name="path">The explicit filesystem path to one EGM document.</param>
    /// <param name="cancellationToken">Cancellation checked before opening, during I/O and while parsing modes.</param>
    /// <returns>A complete document whose retained arrays do not depend on the file handle or input buffer.</returns>
    /// <exception cref="NotSupportedException">The revision or declared resource limits are unsupported.</exception>
    /// <exception cref="InvalidDataException">The complete bytes contain invalid framing, counts or arithmetic.</exception>
    /// <exception cref="IOException">The file cannot be read completely or changes length during the read.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested before or during the operation.</exception>
    public static async Task<EgmDocument> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumEncodedBytes)
        {
            throw new NotSupportedException($"EGM exceeds the {MaximumEncodedBytes}-byte input limit.");
        }
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        var extra = new byte[1];
        if (await stream.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new IOException("EGM file length changed while being read.");
        }
        return Read(bytes, cancellationToken);
    }

    /// <summary>Parses both mode families and preserves every header field without interpreting an unknown basis version.</summary>
    /// <param name="bytes">One complete encoded document; retained fields are copied before returning.</param>
    /// <param name="cancellationToken">Cancellation checked before allocation and at bounded parsing intervals.</param>
    /// <returns>An owned document with original packed values and the exact source SHA-256.</returns>
    /// <exception cref="NotSupportedException">The revision or declared resource limits are unsupported.</exception>
    /// <exception cref="InvalidDataException">Framing, counts, stored scales or decoded displacements are invalid.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested during an authored check.</exception>
    public static EgmDocument Read(ReadOnlySpan<byte> bytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length < 64)
        {
            throw new InvalidDataException("EGM header is truncated.");
        }
        if (bytes.Length > MaximumEncodedBytes)
        {
            throw new NotSupportedException($"EGM exceeds the {MaximumEncodedBytes}-byte input limit.");
        }
        if (!bytes[..8].SequenceEqual("FREGM002"u8))
        {
            throw new NotSupportedException("Only little-endian FREGM002 input is supported.");
        }
        var vertexCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        var symmetricCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        var asymmetricCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        var basisKey = BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]);
        if (vertexCount == 0)
        {
            throw new InvalidDataException("EGM requires a nonempty vertex domain.");
        }
        if (vertexCount > MaximumVertices || symmetricCount > MaximumModesPerFamily || asymmetricCount > MaximumModesPerFamily)
        {
            throw new NotSupportedException("EGM declared counts exceed this reader's bounded profile.");
        }
        var expectedSize = 64L + ((long)symmetricCount + asymmetricCount) * (4L + vertexCount * 6L);
        if (expectedSize != bytes.Length)
        {
            throw new InvalidDataException($"EGM exact encoded length is {expectedSize}, but the input has {bytes.Length} bytes.");
        }
        var offset = 64;
        var symmetric = ReadModes(bytes, ref offset, (int)vertexCount, (int)symmetricCount, cancellationToken);
        var asymmetric = ReadModes(bytes, ref offset, (int)vertexCount, (int)asymmetricCount, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new EgmDocument((int)vertexCount, basisKey, bytes.Slice(24, 40).ToArray(), symmetric, asymmetric,
            Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
    }

    /// <summary>Copies one source family after the complete document length has been validated with wide arithmetic.</summary>
    /// <param name="bytes">The complete input with exact framing already checked.</param>
    /// <param name="offset">The current source position, advanced past this family's modes.</param>
    /// <param name="vertexCount">The bounded complete number of XYZ triples per mode.</param>
    /// <param name="modeCount">The bounded number of modes in this family.</param>
    /// <param name="cancellationToken">Cancellation checked per mode and every 4096 packed components.</param>
    /// <returns>A newly owned array of original packed basis modes.</returns>
    /// <exception cref="InvalidDataException">A scale or decoded displacement is non-finite.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested during an authored check.</exception>
    private static EgmBasisMode[] ReadModes(ReadOnlySpan<byte> bytes, ref int offset, int vertexCount, int modeCount,
        CancellationToken cancellationToken)
    {
        var result = new EgmBasisMode[modeCount];
        for (var mode = 0; mode < result.Length; mode++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scale = BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]);
            offset += 4;
            if (!float.IsFinite(scale))
            {
                throw new InvalidDataException("EGM contains a non-finite mode scale.");
            }
            var packed = new short[checked(vertexCount * 3)];
            for (var component = 0; component < packed.Length; component++)
            {
                if ((component & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                var value = BinaryPrimitives.ReadInt16LittleEndian(bytes[offset..]);
                offset += 2;
                if (!float.IsFinite(value * scale))
                {
                    throw new InvalidDataException("EGM scaled mode displacement overflows a finite coordinate.");
                }
                packed[component] = value;
            }
            result[mode] = new EgmBasisMode(scale, packed);
        }
        return result;
    }
}
