using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Parses FaceGen TRI generation-input files (FRTRI003 format).
///     This exposes the stable 64-byte header, the first confirmed payload
///     block, and conservative metadata about the remaining fixed-width and
///     post-vector tail regions for ongoing reverse-engineering. The currently
///     exposed `VertexBlock1` is still provisional: current anchor samples show
///     that the second fixed-width region behaves like a mixed raw region rather
///     than a uniformly trustworthy float3 block.
/// </summary>
internal sealed class TriParser
{
    private readonly record struct LengthPrefixedIdentifierInfo(
        string Value,
        int Offset,
        int NameLengthWithTerminator);

    internal const string ExpectedMagic = "FRTRI003";
    internal const int HeaderSize = 64;
    private const int HeaderWordCount = (HeaderSize - 8) / 4;

    private uint[] _headerWords = [];
    private TriSectionInfo[] _sections = [];
    private Vector3[] _vertexBlock0 = [];
    private Vector3[] _vertexBlock1 = [];
    private TriStringInfo[] _tailStrings = [];
    private TriStringInfo[] _identifierLikeTailStrings = [];
    private TriRecordFamilyInfo[] _recordFamilies = [];
    private TriRawTailFamilyInfo[] _rawTailFamilies = [];
    private TriEarlyMixedRegionCandidate? _earlyMixedRegionCandidate;
    private TriTrailingStatisticalRegionCandidate? _trailingStatisticalRegionCandidate;
    private TriDifferentialRegionCandidate? _differentialRegionCandidate;

    public string Magic { get; private init; } = ExpectedMagic;
    public int VertexCount { get; private init; }
    public int TriangleCount { get; private init; }
    public IReadOnlyList<uint> HeaderWords => _headerWords;
    public IReadOnlyList<TriSectionInfo> Sections => _sections;
    public IReadOnlyList<Vector3> VertexBlock0 => _vertexBlock0;
    public IReadOnlyList<Vector3> VertexBlock1 => _vertexBlock1;
    public IReadOnlyList<TriStringInfo> TailStrings => _tailStrings;
    public IReadOnlyList<TriStringInfo> IdentifierLikeTailStrings => _identifierLikeTailStrings;
    public IReadOnlyList<TriRecordFamilyInfo> RecordFamilies => _recordFamilies;
    public IReadOnlyList<TriRawTailFamilyInfo> RawTailFamilies => _rawTailFamilies;
    public TriEarlyMixedRegionCandidate? EarlyMixedRegionCandidate => _earlyMixedRegionCandidate;
    public TriTrailingStatisticalRegionCandidate? TrailingStatisticalRegionCandidate =>
        _trailingStatisticalRegionCandidate;
    public TriDifferentialRegionCandidate? DifferentialRegionCandidate => _differentialRegionCandidate;
    public int VertexBlock1Count => checked((int)GetHeaderWord(0x1C));
    public int StructuredSectionGroupCountHint => checked((int)GetHeaderWord(0x20));
    public int InlineVectorMorphRecordCountHint => checked((int)GetHeaderWord(0x24));
    public int IndexedMorphRecordCountHint => checked((int)GetHeaderWord(0x28));

    /// <summary>
    ///     Legacy provisional name for header word `0x2C`. Current anchor samples
    ///     also support reading this word as the aggregate payload-count hint for
    ///     the trailing EOF statistical record chain.
    /// </summary>
    public int NamedMetadataRecordCountHint => checked((int)GetHeaderWord(0x2C));

    public int StatisticalIndexAggregateCountHint => checked((int)GetHeaderWord(0x2C));
    public int RepeatedVertexCountHint => checked((int)GetHeaderWord(0x1C));
    public int StructuredRecordCountHint => StructuredSectionGroupCountHint;
    public int Record34CountHint => InlineVectorMorphRecordCountHint;
    public int Record38CountHint => IndexedMorphRecordCountHint;
    public int Record2CHint => NamedMetadataRecordCountHint;
    public byte[] Payload { get; private init; } = [];
    public int PayloadLength => Payload.Length;
    public byte[] RemainingPayload { get; private init; } = [];
    public int RemainingPayloadLength => RemainingPayload.Length;

    /// <summary>
    ///     Returns a raw 32-bit header word by byte offset. Offsets must be in the
    ///     0x08..0x3C range and aligned to 4 bytes.
    /// </summary>
    public uint GetHeaderWord(int byteOffset)
    {
        if (byteOffset < 8 || byteOffset >= HeaderSize || (byteOffset & 3) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteOffset));
        }

        return _headerWords[(byteOffset - 8) / 4];
    }

    public static TriParser? Parse(byte[] data)
    {
        if (data.Length < HeaderSize)
        {
            return null;
        }

        var magic = Encoding.ASCII.GetString(data, 0, 8);
        if (!string.Equals(magic, ExpectedMagic, StringComparison.Ordinal))
        {
            return null;
        }

        var headerWords = new uint[HeaderWordCount];
        for (var i = 0; i < headerWords.Length; i++)
        {
            headerWords[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8 + i * 4, 4));
        }

        var payload = new byte[data.Length - HeaderSize];
        if (payload.Length > 0)
        {
            Buffer.BlockCopy(data, HeaderSize, payload, 0, payload.Length);
        }

        var offset = HeaderSize;
        if (!TryReadVector3Array(data, ref offset, checked((int)headerWords[0]), out var vertexBlock0))
        {
            return null;
        }

        var earlyMixedRegionCandidate = TryInferEarlyMixedRegionCandidate(
            data,
            offset,
            checked((int)headerWords[9]),
            checked((int)headerWords[0]));

        if (!TryReadVector3Array(data, ref offset, checked((int)headerWords[5]), out var vertexBlock1))
        {
            return null;
        }

        var remainingPayload = new byte[data.Length - offset];
        if (remainingPayload.Length > 0)
        {
            Buffer.BlockCopy(data, offset, remainingPayload, 0, remainingPayload.Length);
        }

        var tailStrings = ExtractTailStrings(data, offset);
        var recordFamilies = new[]
        {
            new TriRecordFamilyInfo(
                "NamedMetadata",
                checked((int)headerWords[9]),
                0x2C,
                TriRecordPayloadKind.Opaque,
                0,
                0xB4,
                null,
                null,
                null,
                null,
                null),
            new TriRecordFamilyInfo(
                "DifferentialMorph",
                checked((int)headerWords[7]),
                0x34,
                TriRecordPayloadKind.Float3,
                12,
                0xCC,
                0x1C,
                0x28,
                0x2C,
                0x30,
                null),
            new TriRecordFamilyInfo(
                "StatisticalMorph",
                checked((int)headerWords[8]),
                0x38,
                TriRecordPayloadKind.UInt32,
                4,
                0xE4,
                0x20,
                0x2C,
                0x30,
                0x34,
                0x1C)
        };
        var rawTailFamilies = new[]
        {
            new TriRawTailFamilyInfo(
                "NamedMetadata",
                checked((int)headerWords[9]),
                0x2C,
                true,
                TriRecordPayloadKind.Opaque,
                0,
                false,
                false,
                false),
            new TriRawTailFamilyInfo(
                "DifferentialMorph",
                checked((int)headerWords[7]),
                0x34,
                true,
                TriRecordPayloadKind.Float3,
                12,
                false,
                false,
                false),
            new TriRawTailFamilyInfo(
                "StatisticalMorph",
                checked((int)headerWords[8]),
                0x38,
                true,
                TriRecordPayloadKind.UInt32,
                4,
                true,
                true,
                true)
        };
        var trailingStatisticalRegionCandidate = TryInferTrailingStatisticalRegionCandidate(
            data,
            tailStrings,
            checked((int)headerWords[8]),
            checked((int)headerWords[9]));
        var differentialRegionCandidate = TryInferDifferentialRegionCandidate(
            data,
            offset,
            checked((int)headerWords[0]),
            checked((int)headerWords[7]),
            trailingStatisticalRegionCandidate);
        var sections = new List<TriSectionInfo>
        {
            new("Header", 0, HeaderSize, 1, HeaderSize),
            new("VertexBlock0", HeaderSize, checked(vertexBlock0.Length * 12), vertexBlock0.Length, 12),
            new("VertexBlock1", HeaderSize + checked(vertexBlock0.Length * 12),
                checked(vertexBlock1.Length * 12), vertexBlock1.Length, 12)
        };

        sections.Add(new TriSectionInfo("RemainingTail", offset, remainingPayload.Length, remainingPayload.Length, 1));

        return new TriParser
        {
            Magic = magic,
            VertexCount = checked((int)headerWords[0]),
            TriangleCount = checked((int)headerWords[1]),
            _headerWords = headerWords,
            _sections = [.. sections],
            _vertexBlock0 = vertexBlock0,
            _vertexBlock1 = vertexBlock1,
            _tailStrings = tailStrings,
            _identifierLikeTailStrings = [.. tailStrings.Where(static info => info.IsIdentifierLike)],
            _recordFamilies = recordFamilies,
            _rawTailFamilies = rawTailFamilies,
            _earlyMixedRegionCandidate = earlyMixedRegionCandidate,
            _trailingStatisticalRegionCandidate = trailingStatisticalRegionCandidate,
            _differentialRegionCandidate = differentialRegionCandidate,
            Payload = payload,
            RemainingPayload = remainingPayload
        };
    }

    private static TriEarlyMixedRegionCandidate? TryInferEarlyMixedRegionCandidate(
        byte[] data,
        int offset,
        int leadingFloat3CountHint,
        int tripletCountHint)
    {
        if (offset < HeaderSize || offset > data.Length || leadingFloat3CountHint < 0 || tripletCountHint < 0)
        {
            return null;
        }

        if (leadingFloat3CountHint == 0 && tripletCountHint == 0)
        {
            return null;
        }

        var leadingFloat3Length = checked(leadingFloat3CountHint * 12);
        var tripletLength = checked(tripletCountHint * 12);
        var totalLength = checked(leadingFloat3Length + tripletLength);
        if (data.Length - offset < totalLength)
        {
            return null;
        }

        var tripletOffset = offset + leadingFloat3Length;
        uint tripletMinObservedValue = 0;
        uint tripletMaxObservedValue = 0;
        var uniqueTripletValues = new HashSet<uint>();
        var tripletValueCountBelowLeadingFloat3CountHint = 0;
        var tripletValueCountBelowTripletCountHint = 0;
        var meshIndexTripletRowCount = 0;
        var contiguousMeshIndexTripletRowCount = 0;
        var firstNonMeshIndexTripletRowIndex = -1;
        var successiveTripletRowsSharingTwoOrMoreValuesCount = 0;
        (uint A, uint B, uint C)? previousTriplet = null;

        if (tripletCountHint > 0)
        {
            tripletMinObservedValue = uint.MaxValue;
            for (var i = 0; i < tripletCountHint; i++)
            {
                var rawOffset = tripletOffset + i * 12;
                var a = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(rawOffset, 4));
                var b = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(rawOffset + 4, 4));
                var c = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(rawOffset + 8, 4));
                var currentTriplet = (a, b, c);

                tripletMinObservedValue = Math.Min(tripletMinObservedValue, Math.Min(a, Math.Min(b, c)));
                tripletMaxObservedValue = Math.Max(tripletMaxObservedValue, Math.Max(a, Math.Max(b, c)));
                uniqueTripletValues.Add(a);
                uniqueTripletValues.Add(b);
                uniqueTripletValues.Add(c);

                if (a < leadingFloat3CountHint)
                {
                    tripletValueCountBelowLeadingFloat3CountHint++;
                }

                if (b < leadingFloat3CountHint)
                {
                    tripletValueCountBelowLeadingFloat3CountHint++;
                }

                if (c < leadingFloat3CountHint)
                {
                    tripletValueCountBelowLeadingFloat3CountHint++;
                }

                if (a < tripletCountHint)
                {
                    tripletValueCountBelowTripletCountHint++;
                }

                if (b < tripletCountHint)
                {
                    tripletValueCountBelowTripletCountHint++;
                }

                if (c < tripletCountHint)
                {
                    tripletValueCountBelowTripletCountHint++;
                }

                var isMeshIndexTripletRow = a < tripletCountHint && b < tripletCountHint && c < tripletCountHint;
                if (isMeshIndexTripletRow)
                {
                    meshIndexTripletRowCount++;
                    if (firstNonMeshIndexTripletRowIndex < 0)
                    {
                        contiguousMeshIndexTripletRowCount++;
                    }
                }
                else if (firstNonMeshIndexTripletRowIndex < 0)
                {
                    firstNonMeshIndexTripletRowIndex = i;
                }

                if (previousTriplet.HasValue)
                {
                    var previous = previousTriplet.Value;
                    var sharedCount = 0;
                    if (previous.A == a || previous.A == b || previous.A == c)
                    {
                        sharedCount++;
                    }

                    if (previous.B == a || previous.B == b || previous.B == c)
                    {
                        sharedCount++;
                    }

                    if (previous.C == a || previous.C == b || previous.C == c)
                    {
                        sharedCount++;
                    }

                    if (sharedCount >= 2)
                    {
                        successiveTripletRowsSharingTwoOrMoreValuesCount++;
                    }
                }

                previousTriplet = currentTriplet;
            }
        }

        return new TriEarlyMixedRegionCandidate(
            offset,
            totalLength,
            leadingFloat3CountHint,
            offset,
            leadingFloat3Length,
            tripletCountHint,
            tripletOffset,
            tripletLength,
            tripletMinObservedValue,
            tripletMaxObservedValue,
            uniqueTripletValues.Count,
            tripletValueCountBelowLeadingFloat3CountHint,
            tripletValueCountBelowTripletCountHint,
            meshIndexTripletRowCount,
            contiguousMeshIndexTripletRowCount,
            firstNonMeshIndexTripletRowIndex,
            successiveTripletRowsSharingTwoOrMoreValuesCount);
    }

    private static TriTrailingStatisticalRegionCandidate? TryInferTrailingStatisticalRegionCandidate(
        byte[] data,
        TriStringInfo[] tailStrings,
        int statisticalRecordCountHint,
        int headerWord2CHint)
    {
        if (statisticalRecordCountHint <= 0 || tailStrings.Length < statisticalRecordCountHint)
        {
            return null;
        }

        var candidates = tailStrings.Skip(tailStrings.Length - statisticalRecordCountHint).ToArray();
        var records = new TriTrailingStatisticalRecordCandidate[candidates.Length];
        var aggregatePayloadCount = 0;
        var previousEnd = -1;

        for (var i = 0; i < candidates.Length; i++)
        {
            var candidate = candidates[i];
            if (candidate.Offset < 4)
            {
                return null;
            }

            var nameLengthWithTerminator = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                data.AsSpan(candidate.Offset - 4, 4)));
            if (nameLengthWithTerminator != candidate.Length + 1)
            {
                return null;
            }

            var payloadCountOffset = candidate.Offset + nameLengthWithTerminator;
            if (payloadCountOffset + 4 > data.Length)
            {
                return null;
            }

            var payloadCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                data.AsSpan(payloadCountOffset, 4)));
            var payloadLength = checked(payloadCount * 4);
            var payloadOffset = payloadCountOffset + 4;
            var recordOffset = candidate.Offset - 4;
            var recordLength = checked(4 + nameLengthWithTerminator + 4 + payloadLength);
            var recordEnd = checked(recordOffset + recordLength);

            if (recordEnd > data.Length)
            {
                return null;
            }

            if (previousEnd != -1 && recordOffset != previousEnd)
            {
                return null;
            }

            records[i] = new TriTrailingStatisticalRecordCandidate(
                candidate.Value,
                recordOffset,
                recordLength,
                nameLengthWithTerminator,
                payloadCount,
                payloadOffset,
                payloadLength);

            aggregatePayloadCount = checked(aggregatePayloadCount + payloadCount);
            previousEnd = recordEnd;
        }

        if (records.Length == 0 || previousEnd != data.Length)
        {
            return null;
        }

        return new TriTrailingStatisticalRegionCandidate(
            records[0].RecordOffset,
            checked(data.Length - records[0].RecordOffset),
            records.Length,
            aggregatePayloadCount,
            aggregatePayloadCount == headerWord2CHint,
            records);
    }

    private static TriDifferentialRegionCandidate? TryInferDifferentialRegionCandidate(
        byte[] data,
        int startOffset,
        int vertexCount,
        int differentialRecordCountHint,
        TriTrailingStatisticalRegionCandidate? trailingStatisticalRegionCandidate)
    {
        if (differentialRecordCountHint <= 0 || vertexCount <= 0)
        {
            return null;
        }

        var boundaryOffset = trailingStatisticalRegionCandidate?.Offset ?? data.Length;
        var identifierStrings = ExtractLengthPrefixedIdentifierStrings(data, startOffset);
        var candidates = identifierStrings
            .Where(info => info.Offset < boundaryOffset)
            .ToArray();
        if (candidates.Length < differentialRecordCountHint)
        {
            return null;
        }

        candidates = candidates
            .Skip(candidates.Length - differentialRecordCountHint)
            .ToArray();

        var packedDeltaPayloadLengthPerRecord = checked(vertexCount * 6);
        var records = new TriDifferentialRecordCandidate[candidates.Length];
        var previousEnd = -1;

        for (var i = 0; i < candidates.Length; i++)
        {
            var candidate = candidates[i];
            var recordOffset = candidate.Offset - 4;
            var scaleOffset = candidate.Offset + candidate.NameLengthWithTerminator;
            var payloadOffset = scaleOffset + 4;
            var recordLength = checked(4 + candidate.NameLengthWithTerminator + 4 + packedDeltaPayloadLengthPerRecord);
            var recordEnd = checked(recordOffset + recordLength);
            var nextStart = i + 1 < candidates.Length ? candidates[i + 1].Offset - 4 : boundaryOffset;

            if (recordEnd > data.Length || recordEnd != nextStart)
            {
                return null;
            }

            if (previousEnd != -1 && recordOffset != previousEnd)
            {
                return null;
            }

            var scale = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(scaleOffset, 4));
            records[i] = new TriDifferentialRecordCandidate(
                candidate.Value,
                recordOffset,
                recordLength,
                candidate.NameLengthWithTerminator,
                scale,
                payloadOffset,
                packedDeltaPayloadLengthPerRecord);
            previousEnd = recordEnd;
        }

        if (records.Length == 0)
        {
            return null;
        }

        return new TriDifferentialRegionCandidate(
            records[0].RecordOffset,
            checked(boundaryOffset - records[0].RecordOffset),
            records.Length,
            vertexCount,
            packedDeltaPayloadLengthPerRecord,
            records);
    }

    private static bool TryReadVector3Array(byte[] data, ref int offset, int count, out Vector3[] values)
    {
        values = [];
        if (count < 0)
        {
            return false;
        }

        var byteLength = checked(count * 12);
        if (data.Length - offset < byteLength)
        {
            return false;
        }

        values = new Vector3[count];
        var span = data.AsSpan(offset, byteLength);
        for (var i = 0; i < count; i++)
        {
            var x = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(i * 12, 4));
            var y = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(i * 12 + 4, 4));
            var z = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(i * 12 + 8, 4));
            values[i] = new Vector3(x, y, z);
        }

        offset += byteLength;
        return true;
    }

    private static TriStringInfo[] ExtractTailStrings(byte[] data, int startOffset)
    {
        var results = new List<TriStringInfo>();
        var i = startOffset;
        while (i < data.Length)
        {
            if (!IsPrintableAscii(data[i]))
            {
                i++;
                continue;
            }

            var end = i;
            while (end < data.Length && IsPrintableAscii(data[end]))
            {
                end++;
            }

            if (end >= data.Length || data[end] != 0 || end - i < 3)
            {
                i++;
                continue;
            }

            var value = Encoding.ASCII.GetString(data, i, end - i);
            results.Add(new TriStringInfo(
                value,
                i,
                end - i,
                LooksIdentifierLike(value)));
            i = end + 1;
        }

        return [.. results];
    }

    private static LengthPrefixedIdentifierInfo[] ExtractLengthPrefixedIdentifierStrings(byte[] data, int startOffset)
    {
        var results = new List<LengthPrefixedIdentifierInfo>();
        for (var i = startOffset + 4; i < data.Length; i++)
        {
            var rawNameLengthWithTerminator = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i - 4, 4));
            if (rawNameLengthWithTerminator < 2 || rawNameLengthWithTerminator > 64)
            {
                continue;
            }

            var nameLengthWithTerminator = (int)rawNameLengthWithTerminator;
            var end = checked(i + nameLengthWithTerminator - 1);
            if (end >= data.Length || data[end] != 0)
            {
                continue;
            }

            var length = nameLengthWithTerminator - 1;
            var span = data.AsSpan(i, length);
            if (length == 0 || !AllPrintableAscii(span))
            {
                continue;
            }

            var value = Encoding.ASCII.GetString(span);
            if (!LooksIdentifierLike(value))
            {
                continue;
            }

            results.Add(new LengthPrefixedIdentifierInfo(value, i, nameLengthWithTerminator));
        }

        return [.. results];
    }

    private static bool AllPrintableAscii(ReadOnlySpan<byte> span)
    {
        foreach (var value in span)
        {
            if (!IsPrintableAscii(value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPrintableAscii(byte value)
    {
        return value is >= 0x20 and <= 0x7E;
    }

    private static bool LooksIdentifierLike(string value)
    {
        if (value.Length < 3)
        {
            return value.Length > 0 &&
                   value.All(ch => char.IsLetterOrDigit(ch) || ch == '_') &&
                   value.Any(char.IsLetter);
        }

        var hasLetter = false;
        foreach (var ch in value)
        {
            if (!(char.IsLetterOrDigit(ch) || ch == '_'))
            {
                return false;
            }

            hasLetter |= char.IsLetter(ch);
        }

        return hasLetter;
    }
}
