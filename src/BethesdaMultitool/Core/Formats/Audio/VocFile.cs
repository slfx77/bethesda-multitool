// Clean-room implementation from the published Creative Voice File (.VOC) specification — the
// block-type table, the time-constant formula and the checksum rule are all long-standing public
// documentation of a DOS-era format. No third-party source was consulted or ported.

using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Audio;

/// <summary>
///     A Creative Voice File (<c>.VOC</c>), the sound-effect container used by DOS-era games
///     including TES Arena. A 26-byte header is followed by a chain of typed blocks: a one-byte
///     type, a 24-bit little-endian payload length, then the payload. Type 0 terminates the file
///     and is the only block with no length field.
///     <para>
///         Sample rate is stored as a "time constant" rather than a rate:
///         <c>rate = 1000000 / (256 - timeConstant)</c>. Every rate in a retail Arena install is
///         one of ten values produced by that formula (4000 through 11111 Hz), which is why they
///         look arbitrary.
///     </para>
/// </summary>
internal sealed class VocFile
{
    /// <summary>The 20-byte signature, including its terminating EOF character.</summary>
    private static ReadOnlySpan<byte> Signature => "Creative Voice File"u8;

    /// <summary>
    ///     The DOS end-of-file byte that closes the signature. It is what let <c>TYPE file.voc</c>
    ///     stop cleanly at the header instead of spraying binary at the terminal.
    /// </summary>
    private const byte SignatureEof = 0x1A;

    /// <summary>Value a silent 8-bit unsigned sample takes (mid-scale).</summary>
    private const byte Silence8Bit = 0x80;

    private VocFile(
        string name,
        int sampleRate,
        int bitsPerSample,
        int channels,
        byte[] samples,
        IReadOnlyList<string> texts,
        int? repeatCount)
    {
        Name = name;
        SampleRate = sampleRate;
        BitsPerSample = bitsPerSample;
        Channels = channels;
        Samples = samples;
        Texts = texts;
        RepeatCount = repeatCount;
    }

    /// <summary>Logical file name this was parsed from.</summary>
    public string Name { get; }

    /// <summary>Sample rate in Hz, from the first sound block.</summary>
    public int SampleRate { get; }

    /// <summary>Bits per sample — 8 for every retail Arena effect.</summary>
    public int BitsPerSample { get; }

    /// <summary>Channel count.</summary>
    public int Channels { get; }

    /// <summary>
    ///     Raw PCM in the file's native encoding: unsigned for 8-bit, signed little-endian for
    ///     16-bit. Both are exactly what a RIFF/WAVE data chunk expects, so no conversion is needed.
    /// </summary>
    public byte[] Samples { get; }

    /// <summary>Any ASCII text blocks the file carries.</summary>
    public IReadOnlyList<string> Texts { get; }

    /// <summary>
    ///     Repeat count from a block-6 loop marker, when present; <c>0xFFFF</c> means loop forever.
    ///     Null when the file does not loop. Looping is metadata — the decoded PCM is the body once.
    /// </summary>
    public int? RepeatCount { get; }

    /// <summary>Total sample frames.</summary>
    public int FrameCount => Channels * BitsPerSample == 0
        ? 0
        : Samples.Length / (Channels * (BitsPerSample / 8));

    /// <summary>Duration in seconds.</summary>
    public double DurationSeconds => SampleRate == 0 ? 0 : (double)FrameCount / SampleRate;

    /// <summary>True when the byte stream opens with the Creative Voice File signature.</summary>
    public static bool IsVoc(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length > Signature.Length
               && bytes[..Signature.Length].SequenceEqual(Signature)
               && bytes[Signature.Length] == SignatureEof;
    }

    /// <summary>Parses a .VOC file.</summary>
    public static VocFile Parse(ReadOnlySpan<byte> bytes, string name)
    {
        if (!IsVoc(bytes))
        {
            throw new InvalidDataException($"'{name}' is not a Creative Voice File (signature mismatch).");
        }

        if (bytes.Length < 26)
        {
            throw new InvalidDataException($"'{name}' is truncated ({bytes.Length} bytes; the header is 26).");
        }

        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes[20..]);
        var version = BinaryPrimitives.ReadUInt16LittleEndian(bytes[22..]);
        var checksum = BinaryPrimitives.ReadUInt16LittleEndian(bytes[24..]);

        // The header carries its own integrity check: ~version + 0x1234, truncated to 16 bits.
        var expected = (ushort)(~version + 0x1234);
        if (checksum != expected)
        {
            throw new InvalidDataException(
                $"'{name}' header checksum is 0x{checksum:X4}; version 0x{version:X4} requires 0x{expected:X4}.");
        }

        if (headerSize < 26 || headerSize > bytes.Length)
        {
            throw new InvalidDataException(
                $"'{name}' declares a {headerSize}-byte header, which does not fit the file ({bytes.Length} bytes).");
        }

        var pcm = new List<byte>();
        var texts = new List<string>();
        int? repeatCount = null;

        var sampleRate = 0;
        var bitsPerSample = 8;
        var channels = 1;
        var sawSound = false;

        // A type-8 block configures the type-1 block that follows it instead of carrying audio.
        int? pendingRate = null;
        int? pendingChannels = null;

        var offset = (int)headerSize;
        while (offset < bytes.Length)
        {
            var blockType = bytes[offset];
            if (blockType == 0)
            {
                break;
            }

            if (offset + 4 > bytes.Length)
            {
                throw new InvalidDataException($"'{name}' ends inside a block header.");
            }

            var length = bytes[offset + 1] | (bytes[offset + 2] << 8) | (bytes[offset + 3] << 16);
            var payloadStart = offset + 4;
            if (payloadStart + length > bytes.Length)
            {
                throw new InvalidDataException(
                    $"'{name}' has a block of type {blockType} claiming {length} bytes, past end of file.");
            }

            var payload = bytes.Slice(payloadStart, length);

            switch (blockType)
            {
                case 1:
                {
                    if (payload.Length < 2)
                    {
                        throw new InvalidDataException($"'{name}' has a truncated sound block.");
                    }

                    var codec = payload[1];
                    if (codec != 0)
                    {
                        throw new NotSupportedException(
                            $"'{name}' uses VOC codec {codec}; only 0 (uncompressed PCM) is supported. " +
                            "No retail Arena effect uses a compressed codec.");
                    }

                    if (!sawSound)
                    {
                        sampleRate = pendingRate ?? RateFromTimeConstant(payload[0]);
                        channels = pendingChannels ?? 1;
                        bitsPerSample = 8;
                        sawSound = true;
                    }

                    pendingRate = null;
                    pendingChannels = null;
                    pcm.AddRange(payload[2..]);
                    break;
                }

                case 2:
                    // Continuation: more samples under the previous block's settings.
                    pcm.AddRange(payload);
                    break;

                case 3:
                {
                    // Silence: a run length and a time constant, with no stored samples.
                    if (payload.Length < 3)
                    {
                        throw new InvalidDataException($"'{name}' has a truncated silence block.");
                    }

                    var runLength = BinaryPrimitives.ReadUInt16LittleEndian(payload) + 1;
                    if (!sawSound)
                    {
                        sampleRate = RateFromTimeConstant(payload[2]);
                        sawSound = true;
                    }

                    pcm.AddRange(Enumerable.Repeat(Silence8Bit, runLength));
                    break;
                }

                case 4:
                    // Marker — a synchronization id with no audio.
                    break;

                case 5:
                {
                    var end = payload.IndexOf((byte)0);
                    texts.Add(Encoding.Latin1.GetString(end < 0 ? payload : payload[..end]));
                    break;
                }

                case 6:
                    repeatCount = payload.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(payload) : 0;
                    break;

                case 7:
                    // Repeat end — the loop body is everything since block 6.
                    break;

                case 8:
                {
                    // Extended attributes for the next sound block: a 16-bit time constant, a
                    // codec, and a stereo flag. The rate formula differs from the 8-bit one.
                    if (payload.Length < 4)
                    {
                        throw new InvalidDataException($"'{name}' has a truncated extended block.");
                    }

                    var timeConstant = BinaryPrimitives.ReadUInt16LittleEndian(payload);
                    var blockChannels = payload[3] == 0 ? 1 : 2;
                    pendingChannels = blockChannels;
                    pendingRate = 256_000_000 / (blockChannels * (65_536 - timeConstant));
                    break;
                }

                case 9:
                {
                    if (payload.Length < 12)
                    {
                        throw new InvalidDataException($"'{name}' has a truncated new-format sound block.");
                    }

                    var codec = BinaryPrimitives.ReadUInt16LittleEndian(payload[10..]);
                    if (codec != 0 && codec != 4)
                    {
                        throw new NotSupportedException(
                            $"'{name}' uses VOC codec {codec}; only uncompressed PCM is supported.");
                    }

                    if (!sawSound)
                    {
                        sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload);
                        bitsPerSample = payload[4];
                        channels = payload[5];
                        sawSound = true;
                    }

                    pcm.AddRange(payload[12..]);
                    break;
                }

                default:
                    throw new InvalidDataException($"'{name}' has an unrecognized VOC block type {blockType}.");
            }

            offset = payloadStart + length;
        }

        if (!sawSound)
        {
            throw new InvalidDataException($"'{name}' contains no sound blocks.");
        }

        return new VocFile(name, sampleRate, bitsPerSample, channels, [.. pcm], texts, repeatCount);
    }

    /// <summary>
    ///     The 8-bit time-constant formula, <c>1000000 / (256 - tc)</c>. A constant of 256 would
    ///     divide by zero and never appears in a valid file.
    /// </summary>
    private static int RateFromTimeConstant(byte timeConstant)
    {
        return 1_000_000 / (256 - timeConstant);
    }
}
