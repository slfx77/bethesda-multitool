using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Media.Audio.Lip;

/// <summary>Reads verified Fallout 3/New Vegas revision-one compressed, little-endian LIP files.</summary>
/// <remarks>Time O(encoded bytes + expanded bytes), space O(expanded bytes). Unknown variants fail explicitly.</remarks>
public static class LipDecoder
{
    /// <summary>Maximum accepted compressed file size, independent of a supplied header.</summary>
    public const int MaximumEncodedBytes = 5 * 1024 * 1024;
    /// <summary>Maximum expanded body size, checked before allocation.</summary>
    public const int MaximumDecodedBytes = 64 * 1024 * 1024;

    /// <summary>Decodes one complete file, rejecting trailing data, invalid samples and unsupported layouts.</summary>
    public static LipTimeline Decode(ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (data.Length < 12 || data.Length > MaximumEncodedBytes)
            throw new InvalidDataException("LIP input is truncated or exceeds the encoded size limit.");
        var revision = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        if (revision != 1 || flags != 1)
            throw new NotSupportedException($"Unsupported LIP revision/flags: {revision}/0x{flags:X}. Only revision 1, compressed little-endian FO3/FNV files are verified.");
        // Every validated FO3/FNV file declares sixteen bytes beyond the expanded body.
        // This is retained as a format relationship, not treated as a compressed-length estimate.
        if (declaredSize < 24 || declaredSize > MaximumDecodedBytes + 16)
            throw new InvalidDataException("LIP declared size is outside the supported range.");
        var body = Expand(data[12..], checked((int)declaredSize - 16), cancellationToken);
        var frameCount = BinaryPrimitives.ReadUInt32LittleEndian(body);
        var startingFrame = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4));
        if (8L + frameCount * 132L != body.Length)
            throw new InvalidDataException("LIP sample count does not match the 16-phoneme/17-modifier layout.");
        var samples = new float[(body.Length - 8) / sizeof(float)];
        for (var index = 0; index < samples.Length; index++)
        {
            if ((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var value = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(8 + index * sizeof(float)));
            if (!float.IsFinite(value)) throw new InvalidDataException($"LIP sample {index} is not finite.");
            samples[index] = value;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new LipTimeline(declaredSize, data.Length, checked((int)frameCount), startingFrame, samples);
    }

    /// <summary>Reads a size-bounded file and releases its stream before returning decoded samples.</summary>
    public static async Task<LipTimeline> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length > MaximumEncodedBytes) throw new InvalidDataException("LIP file exceeds the encoded size limit.");
        var data = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
        return Decode(data, cancellationToken);
    }

    /// <summary>Expands literal nonzero bytes and zero-byte markers followed by a little-endian ushort run.</summary>
    private static byte[] Expand(ReadOnlySpan<byte> encoded, int expectedSize, CancellationToken cancellationToken)
    {
        var result = new byte[expectedSize];
        var written = 0;
        for (var position = 0; position < encoded.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = encoded[position++];
            if (value != 0)
            {
                if (written == result.Length) throw new InvalidDataException("LIP payload exceeds the declared decoded size.");
                result[written++] = value;
                continue;
            }
            if (encoded.Length - position < 2) throw new InvalidDataException("Truncated LIP zero-run marker.");
            var count = BinaryPrimitives.ReadUInt16LittleEndian(encoded[position..]);
            position += 2;
            if (count == 0 || count > result.Length - written)
                throw new InvalidDataException("Invalid LIP zero-run length.");
            written += count; // The fresh array is already zero-filled, including runs inside float encodings.
        }
        if (written != expectedSize) throw new InvalidDataException("LIP payload ends before the declared decoded size.");
        return result;
    }
}
