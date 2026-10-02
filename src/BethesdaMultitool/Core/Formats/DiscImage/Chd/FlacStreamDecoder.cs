namespace BethesdaMultitool.Core.Formats.DiscImage.Chd;

/// <summary>
///     Decodes a run of bare FLAC frames (RFC 9639, no <c>fLaC</c> header, no metadata) into
///     16-bit interleaved samples: what the <c>flac</c> and <c>cdfl</c> CHD codecs store. chdman
///     encodes every hunk as 44.1 kHz stereo 16-bit; when a frame header says "from STREAMINFO"
///     those are the values assumed. Frames are consumed until the requested sample count is met,
///     and the number of bytes they occupied is reported, because the CD codec appends the
///     subcode stream straight after the last frame with no length field between.
///     <para>
///         Every frame's CRC-8 (header) and CRC-16 (whole frame) are verified: a wrong decode of
///         a residual partition would otherwise resynchronise on the next frame and hand back a
///         hunk that is subtly, silently wrong.
///     </para>
/// </summary>
internal sealed class FlacStreamDecoder
{
    // chdman encodes every hunk as 44,100 Hz stereo; only the sample SIZE is ever read back out
    // of a frame header, because a decoder that never plays audio has no use for the rate.
    private const int DefaultBitsPerSample = 16;

    private int[][] _channelBuffers = [];
    private int _channelCount;

    /// <summary>
    ///     Decodes frames from <paramref name="input" /> until <paramref name="samplesPerChannel" />
    ///     samples have been produced, writing them interleaved as 16-bit words into
    ///     <paramref name="output" /> (little-endian, or big-endian when <paramref name="bigEndian" />).
    ///     Returns the number of input bytes the frames occupied.
    /// </summary>
    public int Decode(ReadOnlySpan<byte> input, int channels, int samplesPerChannel, Span<byte> output, bool bigEndian)
    {
        if (output.Length < samplesPerChannel * channels * 2)
        {
            throw new ArgumentException("Output buffer is too small for the requested samples.", nameof(output));
        }

        EnsureBuffers(channels, 0);
        var produced = 0;
        var position = 0;
        while (produced < samplesPerChannel)
        {
            if (position >= input.Length)
            {
                throw new InvalidDataException("FLAC: the stream ended before every sample was decoded.");
            }

            var frameBytes = DecodeFrame(input[position..], channels, out var blockSize);
            var take = Math.Min(blockSize, samplesPerChannel - produced);
            var outIndex = produced * channels * 2;
            for (var sample = 0; sample < take; sample++)
            {
                for (var channel = 0; channel < channels; channel++)
                {
                    var value = _channelBuffers[channel][sample];
                    if (bigEndian)
                    {
                        output[outIndex++] = (byte)(value >> 8);
                        output[outIndex++] = (byte)value;
                    }
                    else
                    {
                        output[outIndex++] = (byte)value;
                        output[outIndex++] = (byte)(value >> 8);
                    }
                }
            }

            produced += take;
            position += frameBytes;
        }

        return position;
    }

    private void EnsureBuffers(int channels, int blockSize)
    {
        if (_channelCount != channels)
        {
            _channelBuffers = new int[channels][];
            _channelCount = channels;
        }

        for (var channel = 0; channel < channels; channel++)
        {
            if (_channelBuffers[channel] is null || _channelBuffers[channel].Length < blockSize)
            {
                _channelBuffers[channel] = new int[Math.Max(blockSize, 4096)];
            }
        }
    }

    /// <summary>Decodes one frame at the start of <paramref name="data" />; returns its length in bytes.</summary>
    private int DecodeFrame(ReadOnlySpan<byte> data, int expectedChannels, out int blockSize)
    {
        var reader = new FlacBitReader(data);
        var sync = reader.Read(15);
        if (sync != 0x7FFC)
        {
            throw new InvalidDataException("FLAC: frame sync code not found.");
        }

        reader.Read(1); // blocking strategy: irrelevant to a decoder that never seeks
        var blockSizeCode = (int)reader.Read(4);
        var sampleRateCode = (int)reader.Read(4);
        var channelAssignment = (int)reader.Read(4);
        var sampleSizeCode = (int)reader.Read(3);
        if (reader.Read(1) != 0)
        {
            throw new InvalidDataException("FLAC: reserved frame-header bit set.");
        }

        // The coded frame/sample number: a UTF-8-shaped integer whose value a hunk decoder does
        // not need, but whose length it must step over.
        var lead = (int)reader.Read(8);
        var continuation = 0;
        if ((lead & 0x80) != 0)
        {
            var mask = 0x40;
            while ((lead & mask) != 0)
            {
                continuation++;
                mask >>= 1;
            }

            if (continuation is 0 or > 6)
            {
                throw new InvalidDataException("FLAC: malformed coded number in the frame header.");
            }

            for (var i = 0; i < continuation; i++)
            {
                if ((reader.Read(8) & 0xC0) != 0x80)
                {
                    throw new InvalidDataException("FLAC: malformed coded number in the frame header.");
                }
            }
        }

        blockSize = blockSizeCode switch
        {
            0 => throw new InvalidDataException("FLAC: reserved block size code."),
            1 => 192,
            >= 2 and <= 5 => 576 << (blockSizeCode - 2),
            6 => (int)reader.Read(8) + 1,
            7 => (int)reader.Read(16) + 1,
            _ => 256 << (blockSizeCode - 8),
        };

        switch (sampleRateCode)
        {
            case 12:
                reader.Read(8);
                break;
            case 13:
            case 14:
                reader.Read(16);
                break;
            case 15:
                throw new InvalidDataException("FLAC: invalid sample rate code.");
        }

        var bitsPerSample = sampleSizeCode switch
        {
            0 => DefaultBitsPerSample,
            1 => 8,
            2 => 12,
            4 => 16,
            5 => 20,
            6 => 24,
            7 => 32,
            _ => throw new InvalidDataException("FLAC: reserved sample size code."),
        };

        var headerBytes = reader.BytePosition;
        var crc8 = (int)reader.Read(8);
        if (Crc8(data[..headerBytes]) != crc8)
        {
            throw new InvalidDataException("FLAC: frame header CRC-8 mismatch.");
        }

        int channels;
        var decorrelation = 0; // 0 independent, 1 left/side, 2 right/side, 3 mid/side
        if (channelAssignment <= 7)
        {
            channels = channelAssignment + 1;
        }
        else if (channelAssignment <= 10)
        {
            channels = 2;
            decorrelation = channelAssignment - 7;
        }
        else
        {
            throw new InvalidDataException("FLAC: reserved channel assignment.");
        }

        if (channels != expectedChannels)
        {
            throw new InvalidDataException($"FLAC: frame carries {channels} channel(s); the hunk expects {expectedChannels}.");
        }

        EnsureBuffers(channels, blockSize);
        for (var channel = 0; channel < channels; channel++)
        {
            var extra = (decorrelation, channel) switch
            {
                (1, 1) => 1,
                (2, 0) => 1,
                (3, 1) => 1,
                _ => 0,
            };
            DecodeSubframe(ref reader, _channelBuffers[channel], blockSize, bitsPerSample + extra);
        }

        var left = _channelBuffers[0];
        var right = channels > 1 ? _channelBuffers[1] : null;
        switch (decorrelation)
        {
            case 1:
                for (var i = 0; i < blockSize; i++)
                {
                    right![i] = left[i] - right[i];
                }

                break;
            case 2:
                for (var i = 0; i < blockSize; i++)
                {
                    left[i] += right![i];
                }

                break;
            case 3:
                for (var i = 0; i < blockSize; i++)
                {
                    var side = right![i];
                    var mid = (left[i] << 1) | (side & 1);
                    left[i] = (mid + side) >> 1;
                    right[i] = (mid - side) >> 1;
                }

                break;
        }

        reader.AlignToByte();
        var frameEnd = reader.BytePosition;
        var crc16 = (int)reader.Read(16);
        if (Crc16(data[..frameEnd]) != crc16)
        {
            throw new InvalidDataException("FLAC: frame CRC-16 mismatch.");
        }

        return reader.BytePosition;
    }

    private static void DecodeSubframe(ref FlacBitReader reader, int[] samples, int blockSize, int bitsPerSample)
    {
        if (reader.Read(1) != 0)
        {
            throw new InvalidDataException("FLAC: subframe padding bit set.");
        }

        var type = (int)reader.Read(6);
        var wasted = 0;
        if (reader.Read(1) != 0)
        {
            wasted = 1;
            while (reader.Read(1) == 0)
            {
                wasted++;
            }
        }

        var bits = bitsPerSample - wasted;
        if (bits <= 0)
        {
            throw new InvalidDataException("FLAC: wasted bits exceed the sample size.");
        }

        if (type == 0)
        {
            var value = reader.ReadSigned(bits);
            Array.Fill(samples, value, 0, blockSize);
        }
        else if (type == 1)
        {
            for (var i = 0; i < blockSize; i++)
            {
                samples[i] = reader.ReadSigned(bits);
            }
        }
        else if (type is >= 8 and <= 12)
        {
            var order = type - 8;
            for (var i = 0; i < order; i++)
            {
                samples[i] = reader.ReadSigned(bits);
            }

            DecodeResidual(ref reader, samples, blockSize, order);
            RestoreFixed(samples, blockSize, order);
        }
        else if (type >= 32)
        {
            var order = (type & 31) + 1;
            for (var i = 0; i < order; i++)
            {
                samples[i] = reader.ReadSigned(bits);
            }

            var precision = (int)reader.Read(4) + 1;
            if (precision == 16)
            {
                throw new InvalidDataException("FLAC: invalid LPC coefficient precision.");
            }

            var shift = reader.ReadSigned(5);
            Span<int> coefficients = stackalloc int[order];
            for (var i = 0; i < order; i++)
            {
                coefficients[i] = reader.ReadSigned(precision);
            }

            DecodeResidual(ref reader, samples, blockSize, order);
            RestoreLpc(samples, blockSize, order, coefficients, shift);
        }
        else
        {
            throw new InvalidDataException("FLAC: reserved subframe type.");
        }

        if (wasted > 0)
        {
            for (var i = 0; i < blockSize; i++)
            {
                samples[i] <<= wasted;
            }
        }
    }

    private static void DecodeResidual(ref FlacBitReader reader, int[] samples, int blockSize, int order)
    {
        var method = (int)reader.Read(2);
        var parameterBits = method switch
        {
            0 => 4,
            1 => 5,
            _ => throw new InvalidDataException("FLAC: reserved residual coding method."),
        };
        var escape = (1 << parameterBits) - 1;
        var partitionOrder = (int)reader.Read(4);
        var partitions = 1 << partitionOrder;
        var partitionSamples = blockSize >> partitionOrder;
        if (partitionSamples < order && partitionOrder != 0 || partitionSamples == 0 && partitions > 1)
        {
            throw new InvalidDataException("FLAC: residual partition smaller than the predictor order.");
        }

        var index = order;
        for (var partition = 0; partition < partitions; partition++)
        {
            var count = partition == 0 ? partitionSamples - order : partitionSamples;
            if (partitionOrder == 0)
            {
                count = blockSize - order;
            }

            if (count < 0 || index + count > blockSize)
            {
                throw new InvalidDataException("FLAC: residual partitions do not tile the block.");
            }

            var parameter = (int)reader.Read(parameterBits);
            if (parameter == escape)
            {
                var rawBits = (int)reader.Read(5);
                for (var i = 0; i < count; i++)
                {
                    samples[index++] = rawBits == 0 ? 0 : reader.ReadSigned(rawBits);
                }

                continue;
            }

            for (var i = 0; i < count; i++)
            {
                var quotient = 0;
                while (reader.Read(1) == 0)
                {
                    quotient++;
                    if (quotient > 1 << 20)
                    {
                        throw new InvalidDataException("FLAC: runaway Rice quotient.");
                    }
                }

                var remainder = parameter == 0 ? 0 : (int)reader.Read(parameter);
                var folded = ((uint)quotient << parameter) | (uint)remainder;
                samples[index++] = (int)(folded >> 1) ^ -(int)(folded & 1);
            }
        }
    }

    private static void RestoreFixed(int[] samples, int blockSize, int order)
    {
        for (var i = order; i < blockSize; i++)
        {
            samples[i] += order switch
            {
                0 => 0,
                1 => samples[i - 1],
                2 => 2 * samples[i - 1] - samples[i - 2],
                3 => 3 * samples[i - 1] - 3 * samples[i - 2] + samples[i - 3],
                _ => 4 * samples[i - 1] - 6 * samples[i - 2] + 4 * samples[i - 3] - samples[i - 4],
            };
        }
    }

    private static void RestoreLpc(int[] samples, int blockSize, int order, ReadOnlySpan<int> coefficients, int shift)
    {
        for (var i = order; i < blockSize; i++)
        {
            long sum = 0;
            for (var j = 0; j < order; j++)
            {
                sum += (long)coefficients[j] * samples[i - 1 - j];
            }

            samples[i] += (int)(sum >> shift);
        }
    }

    private static int Crc8(ReadOnlySpan<byte> data)
    {
        var crc = 0;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x80) != 0 ? ((crc << 1) ^ 0x07) & 0xFF : (crc << 1) & 0xFF;
            }
        }

        return crc;
    }

    private static int Crc16(ReadOnlySpan<byte> data)
    {
        var crc = 0;
        foreach (var value in data)
        {
            crc ^= value << 8;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x8005) & 0xFFFF : (crc << 1) & 0xFFFF;
            }
        }

        return crc;
    }

    /// <summary>MSB-first bit reader that also reports its byte position, for the CRCs and the frame length.</summary>
    private ref struct FlacBitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private ulong _buffer;
        private int _bits;
        private int _offset;

        public FlacBitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
        }

        /// <summary>Position of the next unread byte when the reader is byte-aligned.</summary>
        public int BytePosition => _offset - _bits / 8;

        public uint Read(int numBits)
        {
            if (numBits == 0)
            {
                return 0;
            }

            while (_bits < numBits)
            {
                if (_offset >= _data.Length)
                {
                    throw new InvalidDataException("FLAC: frame data ended early.");
                }

                _buffer |= (ulong)_data[_offset++] << (56 - _bits);
                _bits += 8;
            }

            var value = (uint)(_buffer >> (64 - numBits));
            _buffer <<= numBits;
            _bits -= numBits;
            return value;
        }

        public int ReadSigned(int numBits)
        {
            if (numBits == 32)
            {
                return (int)Read(32);
            }

            var value = (int)Read(numBits);
            var shift = 32 - numBits;
            return (value << shift) >> shift;
        }

        public void AlignToByte()
        {
            var partial = _bits & 7;
            if (partial != 0)
            {
                Read(partial);
            }
        }
    }
}
