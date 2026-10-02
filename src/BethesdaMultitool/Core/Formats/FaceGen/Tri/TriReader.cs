using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace BethesdaMultitool.Core.Formats.FaceGen.Tri;

/// <summary>Reads the documented little-endian FRTRI003 layout with bounded allocation and exact consumption.</summary>
public static class TriReader
{
    /// <summary>Maximum accepted encoded document size; oversized input is explicitly unsupported.</summary>
    public const int MaximumEncodedBytes = 16 * 1024 * 1024;
    /// <summary>Maximum combined base and statistical vertex domain.</summary>
    public const int MaximumVertices = 500_000;
    /// <summary>Maximum number of morph records across both families.</summary>
    public const int MaximumMorphs = 4096;
    /// <summary>Maximum label length measured in stored character units.</summary>
    public const int MaximumLabelUnits = 4096;

    /// <summary>Reads a bounded file, verifies its observed length, and releases the stream before returning the document.</summary>
    public static async Task<TriDocument> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumEncodedBytes)
        {
            throw new NotSupportedException($"TRI exceeds the {MaximumEncodedBytes}-byte limit.");
        }
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        var extra = new byte[1];
        if (await stream.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new IOException("TRI file length changed while being read.");
        }
        return Read(bytes, cancellationToken);
    }

    /// <summary>Parses owned read-only document arrays; no string search, endian guessing or trailing-byte tolerance is used.</summary>
    public static TriDocument Read(ReadOnlySpan<byte> bytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length > MaximumEncodedBytes)
        {
            throw new NotSupportedException($"TRI exceeds the {MaximumEncodedBytes}-byte limit.");
        }
        var reader = new Cursor(bytes, cancellationToken);
        if (!reader.Take(8).SequenceEqual("FRTRI003"u8))
        {
            throw new NotSupportedException("Only little-endian FRTRI003 documents are supported.");
        }
        var counts = new int[9];
        for (var index = 0; index < 6; index++)
        {
            counts[index] = reader.Count("header count");
        }
        var flags = unchecked((uint)reader.Int32());
        for (var index = 6; index < counts.Length; index++)
        {
            counts[index] = reader.Count("header count");
        }
        var header = new TriHeader(counts, flags, reader.Take(16).ToArray());
        ValidateHeader(header);
        var vertices = reader.Vectors3(header.VertexCount);
        var targets = reader.Vectors3(header.StatisticalVertexCount);
        var triangles = reader.Indices(checked(header.TriangleCount * 3), header.VertexCount);
        var quads = reader.Indices(checked(header.QuadCount * 4), header.VertexCount);
        reader.Require((long)header.VertexLabelCount * 8 + (long)header.SurfaceLabelCount * 20);
        var vertexLabels = new TriVertexLabel[header.VertexLabelCount];
        for (var index = 0; index < vertexLabels.Length; index++)
        {
            vertexLabels[index] = new TriVertexLabel(reader.Index(header.VertexCount), reader.Label(false, 1));
        }
        var surfaceLabels = new TriSurfaceLabel[header.SurfaceLabelCount];
        for (var index = 0; index < surfaceLabels.Length; index++)
        {
            surfaceLabels[index] = new TriSurfaceLabel(reader.Int32(), reader.Vector3(),
                reader.Label(false, (flags & 2) == 0 ? 1 : 2));
        }
        var hasUvs = (flags & 1) != 0;
        var uvCount = header.TextureCoordinateCount == 0 ? header.VertexCount : header.TextureCoordinateCount;
        if (!hasUvs)
        {
            uvCount = 0;
        }
        var uvs = reader.Vectors2(uvCount);
        var triangleUvs = hasUvs && header.TextureCoordinateCount > 0
            ? reader.Indices(triangles.Length, uvCount) : [];
        var quadUvs = hasUvs && header.TextureCoordinateCount > 0
            ? reader.Indices(quads.Length, uvCount) : [];
        reader.Require((long)header.DifferentialMorphCount * (9L + 6L * header.VertexCount)
            + (long)header.StatisticalMorphCount * 9);
        var differentials = new TriDifferentialMorph[header.DifferentialMorphCount];
        for (var index = 0; index < differentials.Length; index++)
        {
            var label = reader.Label(true, 1);
            var scale = reader.Float();
            var deltas = reader.Deltas(checked(header.VertexCount * 3), scale);
            differentials[index] = new TriDifferentialMorph(label, scale, deltas);
        }
        var statistics = new TriStatisticalMorph[header.StatisticalMorphCount];
        var targetOffset = 0;
        for (var index = 0; index < statistics.Length; index++)
        {
            var label = reader.Label(true, 1);
            var count = reader.Count("statistical affected count");
            if (count > targets.Length - targetOffset)
            {
                throw new InvalidDataException("TRI statistical records exceed the declared target domain.");
            }
            var indices = reader.Indices(count, header.VertexCount);
            statistics[index] = new TriStatisticalMorph(label, indices, header.VertexCount + targetOffset,
                targets.AsMemory(targetOffset, count));
            targetOffset += count;
        }
        if (targetOffset != targets.Length)
        {
            throw new InvalidDataException("TRI statistical affected counts do not sum to K.");
        }
        reader.RequireEnd();
        cancellationToken.ThrowIfCancellationRequested();
        return new TriDocument(header, new TriPayload
        {
            Vertices = vertices, StatisticalVertices = targets, Triangles = triangles, Quads = quads,
            VertexLabels = vertexLabels, SurfaceLabels = surfaceLabels, TextureCoordinates = uvs,
            TriangleTextureIndices = triangleUvs, QuadTextureIndices = quadUvs,
            DifferentialMorphs = differentials, StatisticalMorphs = statistics
        }, Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
    }

    /// <summary>Rejects unsupported extension bits and counts before size-dependent allocations or integer products.</summary>
    private static void ValidateHeader(TriHeader header)
    {
        if ((header.ExtensionFlags & ~3u) != 0)
        {
            throw new NotSupportedException($"Unsupported TRI extension flags 0x{header.ExtensionFlags:X8}.");
        }
        if (header.VertexCount == 0)
        {
            throw new InvalidDataException("TRI requires at least one base vertex.");
        }
        if ((long)header.VertexCount + header.StatisticalVertexCount > MaximumVertices
            || (long)header.DifferentialMorphCount + header.StatisticalMorphCount > MaximumMorphs
            || header.TriangleCount > MaximumEncodedBytes / 12
            || header.QuadCount > MaximumEncodedBytes / 16
            || header.VertexLabelCount > MaximumEncodedBytes / 8
            || header.SurfaceLabelCount > MaximumEncodedBytes / 20
            || header.TextureCoordinateCount > MaximumEncodedBytes / 8)
        {
            throw new NotSupportedException("TRI declared counts exceed this reader's bounded profile.");
        }
    }

    /// <summary>A checked stack-only cursor; arrays are allocated only after their minimum encoded size is available.</summary>
    private ref struct Cursor
    {
        private readonly ReadOnlySpan<byte> _bytes;
        private readonly CancellationToken _token;
        private int _offset;

        /// <summary>Starts at the beginning of the caller-owned byte span.</summary>
        internal Cursor(ReadOnlySpan<byte> bytes, CancellationToken token)
        {
            _bytes = bytes;
            _token = token;
            _offset = 0;
        }

        /// <summary>Checks cancellation and remaining bytes with wide arithmetic before allocation.</summary>
        internal readonly void Require(long count)
        {
            _token.ThrowIfCancellationRequested();
            if (count < 0 || count > _bytes.Length - _offset)
            {
                throw new InvalidDataException($"Truncated TRI data at byte {_offset}.");
            }
        }

        /// <summary>Consumes exactly one checked byte range without allocating.</summary>
        internal ReadOnlySpan<byte> Take(int count)
        {
            Require(count);
            var result = _bytes.Slice(_offset, count);
            _offset += count;
            return result;
        }

        /// <summary>Reads one little-endian signed word.</summary>
        internal int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        /// <summary>Reads a nonnegative count without silently reinterpreting the sign bit.</summary>
        internal int Count(string field)
        {
            var value = Int32();
            if (value < 0)
            {
                throw new InvalidDataException($"Negative TRI {field}.");
            }
            return value;
        }

        /// <summary>Reads one finite IEEE single value; signed zero is retained.</summary>
        internal float Float()
        {
            var value = BitConverter.Int32BitsToSingle(Int32());
            if (!float.IsFinite(value))
            {
                throw new InvalidDataException("TRI contains a non-finite coordinate or scale.");
            }
            return value;
        }

        /// <summary>Reads one finite source vector.</summary>
        internal Vector3 Vector3() => new(Float(), Float(), Float());

        /// <summary>Copies a checked finite vector array in original order.</summary>
        internal Vector3[] Vectors3(int count)
        {
            Require((long)count * 12);
            var result = new Vector3[count];
            for (var index = 0; index < count; index++)
            {
                result[index] = Vector3();
            }
            return result;
        }

        /// <summary>Copies a checked finite UV array without coordinate conversion.</summary>
        internal Vector2[] Vectors2(int count)
        {
            Require((long)count * 8);
            var result = new Vector2[count];
            for (var index = 0; index < count; index++)
            {
                result[index] = new Vector2(Float(), Float());
            }
            return result;
        }

        /// <summary>Reads an index in an explicit exclusive domain.</summary>
        internal int Index(int limit)
        {
            var value = Int32();
            if ((uint)value >= (uint)limit)
            {
                throw new InvalidDataException($"TRI index {value} is outside [0, {limit}).");
            }
            return value;
        }

        /// <summary>Copies a checked index array without reordering or discarding duplicate entries.</summary>
        internal int[] Indices(int count, int limit)
        {
            Require((long)count * 4);
            var result = new int[count];
            for (var index = 0; index < count; index++)
            {
                result[index] = Index(limit);
            }
            return result;
        }

        /// <summary>Copies packed deltas while requiring their finite-scale products to remain finite.</summary>
        internal short[] Deltas(int count, float scale)
        {
            Require((long)count * 2);
            var result = new short[count];
            for (var index = 0; index < count; index++)
            {
                result[index] = BinaryPrimitives.ReadInt16LittleEndian(Take(2));
                if (!float.IsFinite(result[index] * scale))
                {
                    throw new InvalidDataException("TRI scaled differential overflows a finite coordinate.");
                }
            }
            return result;
        }

        /// <summary>Retains exact bytes, requiring terminal NUL only where the documented morph-label layout requires it.</summary>
        internal TriLabel Label(bool nullTerminated, int width)
        {
            var units = Count("label length");
            if (units > MaximumLabelUnits)
            {
                throw new NotSupportedException($"TRI label exceeds {MaximumLabelUnits} character units.");
            }
            var bytes = Take(units * width).ToArray();
            var content = bytes.AsSpan();
            if (nullTerminated)
            {
                if (content.IsEmpty || content[^1] != 0)
                {
                    throw new InvalidDataException("TRI morph label lacks its required terminal NUL.");
                }
                content = content[..^1];
                if (content.Contains((byte)0))
                {
                    throw new InvalidDataException("TRI morph label contains an embedded NUL.");
                }
            }
            var text = width == 1 ? Encoding.Latin1.GetString(content) : Encoding.Unicode.GetString(content);
            return new TriLabel(bytes, text, width);
        }

        /// <summary>Rejects unconsumed bytes instead of silently accepting an unknown suffix.</summary>
        internal readonly void RequireEnd()
        {
            Require(0);
            if (_offset != _bytes.Length)
            {
                throw new InvalidDataException($"TRI contains {_bytes.Length - _offset} unexpected trailing bytes.");
            }
        }
    }
}
