using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

internal readonly record struct TriSectionInfo(
    string Name,
    int Offset,
    int Length,
    int ElementCount,
    int ElementSize);

internal readonly record struct TriStringInfo(
    string Value,
    int Offset,
    int Length,
    bool IsIdentifierLike);

internal enum TriRecordPayloadKind
{
    Opaque = 0,
    Float3 = 1,
    UInt32 = 2
}

/// <summary>
///     Describes a decompilation-confirmed FRTRI003 record family after the GECK
///     has materialized it into the generation context. These offsets are context
///     object offsets, not raw on-disk byte offsets. Where payload metadata is
///     present, the nested payload has been confirmed as a plain contiguous typed
///     array in the materialized record.
/// </summary>
internal readonly record struct TriRecordFamilyInfo(
    string Name,
    int CountHint,
    int RecordSize,
    TriRecordPayloadKind PayloadKind,
    int PayloadElementSize,
    int GenerationContextOffset,
    int? MaterializedPayloadRootOffset,
    int? MaterializedPayloadBeginOffset,
    int? MaterializedPayloadEndOffset,
    int? MaterializedPayloadCapacityOffset,
    int? PreservedScalarOffset);

/// <summary>
///     Parses FaceGen TRI generation-input files (FRTRI003 format).
///     This exposes the stable 64-byte header plus the first confirmed payload
///     blocks for ongoing reverse-engineering of the later section layout.
/// </summary>
internal sealed class TriParser
{
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
    public int VertexBlock1Count => checked((int)GetHeaderWord(0x1C));
    public int StructuredSectionGroupCountHint => checked((int)GetHeaderWord(0x20));
    public int InlineVectorMorphRecordCountHint => checked((int)GetHeaderWord(0x24));
    public int IndexedMorphRecordCountHint => checked((int)GetHeaderWord(0x28));
    public int NamedMetadataRecordCountHint => checked((int)GetHeaderWord(0x2C));
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
            Payload = payload,
            RemainingPayload = remainingPayload
        };
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
        for (var i = startOffset; i < data.Length; i++)
        {
            if (!IsPrintableAscii(data[i]))
            {
                continue;
            }

            var end = i;
            while (end < data.Length && IsPrintableAscii(data[end]))
            {
                end++;
            }

            if (end >= data.Length || data[end] != 0 || end - i < 3)
            {
                continue;
            }

            var value = Encoding.ASCII.GetString(data, i, end - i);
            results.Add(new TriStringInfo(
                value,
                i,
                end - i,
                LooksIdentifierLike(value)));
            i = end;
        }

        return [.. results];
    }

    private static bool IsPrintableAscii(byte value)
    {
        return value is >= 0x20 and <= 0x7E;
    }

    private static bool LooksIdentifierLike(string value)
    {
        if (value.Length < 3)
        {
            return false;
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
