using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Decodes one BSPackedAdditionalGeometryData block (plan section 3, "Packed-stream layout table"; section 6, slice
///     10) into <see cref="NifPackedGeometryStreams" />: the layout is recognized from the block's own stream table
///     (<see cref="NifPackedGeometryLayout.Match" />), the data block's structure is checked against the measured one,
///     and every channel is decoded by its measured encoding. A block that matches no layout, or whose payload is
///     empty, absent or inconsistent with its counts, is not decoded: the caller keeps it as native state with the
///     reason and detail returned here. Nothing is guessed from a vector length.
/// </summary>
/// <remarks>
///     <para>
///         Structure (measured 2026-09-24 on every retail block): every channel names Block Index 0 with Flags 2 and
///         Total Size = Num Vertices x Unit Size; exactly one data block has data, with one inner block at offset 0,
///         Block Size = Num Vertices x stride and its own Total Size = stride. The Shader Index is recorded and compared
///         with the layout's measured value, never keyed on.
///     </para>
///     <para>
///         Non-finite values follow the reader's policy for inline streams (<see cref="NifModelGeometryData" />): a
///         NaN or infinite position or normal is corrupt input and throws <see cref="InvalidDataException" /> with
///         the block, channel, vertex, component and file offset; a non-finite texture coordinate is counted so the
///         caller reads that component as 0, keeps the channel's exact bits in a raw attribute and reports it; a
///         non-finite tangent or bitangent is counted so the caller drops the typed tangent basis with a diagnostic; a
///         non-finite weight is counted so the skin stays native state (Shared rejects it).
///     </para>
/// </remarks>
internal static class NifPackedGeometryDecoder
{
    /// <summary>The NiAGDDataStream Flags value every retail channel carries.</summary>
    public const long MeasuredStreamFlags = 2;

    /// <summary>The tolerance on the three sibling weights' sum for the slot-3 sentinel.</summary>
    public const double SentinelSiblingTolerance = 2e-3;

    /// <summary>
    ///     The largest slot-3 half counted as the exporter's residual rather than a fourth weight: 0x0002 = 2^-23 (the
    ///     measurement records exactly 2^-24 and 2^-23 on X360's 1,745 L3 + 125 L4 residual vertices, never more).
    ///     Nothing in the measurement establishes a wider threshold, so none is used.
    /// </summary>
    public const ushort ResidualSlot3Bits = 0x0002;

    /// <summary>Decodes the block when its layout is known and its structure is the measured one.</summary>
    /// <param name="block">The BSPackedAdditionalGeometryData block, decoded by the schema decoder.</param>
    /// <param name="platform">The platform whose color byte order applies.</param>
    /// <param name="streams">The decoded channels, or null when the block is not decoded.</param>
    /// <param name="reason">The NativeOnly coverage reason when the block is not decoded, else null.</param>
    /// <param name="detail">A short explanation when the block is not decoded, else null.</param>
    /// <returns>True when <paramref name="streams" /> is set.</returns>
    /// <exception cref="InvalidDataException">
    ///     A field of a typed block did not decode with its declared shape, or a position, normal or texture
    ///     coordinate is not finite.
    /// </exception>
    public static bool TryDecode(NifDecodedBlock block, NifPackedPlatformSelection platform,
        out NifPackedGeometryStreams? streams, out string? reason, out string? detail)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(platform);
        streams = null;
        reason = null;
        detail = null;

        var root = block.Root;
        var vertexCount = checked((int)Integer(block, root, "Num Vertices"));
        var infos = ArrayField(block, root, "Block Infos");
        var table = new List<(uint Type, uint UnitSize, uint Offset)>(infos.Count);
        var strides = new HashSet<uint>();
        var blockIndices = new HashSet<long>();
        var flags = new HashSet<long>();
        var totalSizesFit = true;
        foreach (var item in infos.Items)
        {
            if (item is not NifStructValue info)
            {
                throw Shape(block, "Block Infos", item);
            }

            var type = (uint)Integer(block, info, "Type");
            var unitSize = (uint)Integer(block, info, "Unit Size");
            var totalSize = Integer(block, info, "Total Size");
            var stride = (uint)Integer(block, info, "Stride");
            blockIndices.Add(Integer(block, info, "Block Index"));
            flags.Add(Integer(block, info, "Flags"));
            table.Add((type, unitSize, (uint)Integer(block, info, "Block Offset")));
            strides.Add(stride);
            totalSizesFit &= totalSize == (long)vertexCount * unitSize;
        }

        if (strides.Count != 1)
        {
            reason = NifModelCoverage.PackedLayoutUnknownReason;
            detail = strides.Count == 0
                ? "the block declares no channel."
                : string.Create(CultureInfo.InvariantCulture,
                    $"its channels declare {strides.Count} different strides ({string.Join(", ", strides)}).");
            return false;
        }

        var declaredStride = strides.Single();
        var layout = NifPackedGeometryLayout.Match(table, declaredStride);
        if (layout is null)
        {
            reason = NifModelCoverage.PackedLayoutUnknownReason;
            detail = string.Create(CultureInfo.InvariantCulture,
                $"its stream table {NifPackedGeometryLayout.KeyOf(table, declaredStride)} matches none of the six " +
                $"measured layouts.");
            return false;
        }

        var structure = new JsonObject
        {
            ["channels"] = infos.Count,
            ["blockIndices"] = NifModelNativeValues.Integers(blockIndices.Select(i => (int)i).ToList()),
            ["flags"] = NifModelNativeValues.Integers(flags.Select(f => (int)f).ToList()),
            ["totalSizesEqualVerticesTimesUnitSize"] = totalSizesFit
        };
        if (blockIndices.Count != 1 || !blockIndices.Contains(0) || flags.Count != 1 ||
            !flags.Contains(MeasuredStreamFlags) || !totalSizesFit)
        {
            reason = NifModelCoverage.PackedPayloadReason;
            detail = "its channels do not all name data block 0 with flags 2 and Total Size = Num Vertices x Unit " +
                     "Size, which every measured block does.";
            return false;
        }

        var blocks = ArrayField(block, root, "Blocks");
        structure["dataBlocks"] = blocks.Count;
        NifStructValue? dataBlock = null;
        var withData = 0;
        foreach (var item in blocks.Items)
        {
            if (item is not NifStructValue entry)
            {
                throw Shape(block, "Blocks", item);
            }

            if (Integer(block, entry, "Has Data") != 0 && entry.TryGet("Data Block", out var inner) &&
                inner is NifStructValue innerStruct)
            {
                withData++;
                dataBlock ??= innerStruct;
            }
        }

        structure["dataBlocksWithData"] = withData;
        if (withData != 1 || dataBlock is null)
        {
            reason = NifModelCoverage.PackedPayloadReason;
            detail = withData == 0
                ? "no data block carries a payload."
                : string.Create(CultureInfo.InvariantCulture,
                    $"{withData} data blocks carry a payload; the measured layouts have exactly one.");
            return false;
        }

        var blockSize = Integer(block, dataBlock, "Block Size");
        var innerBlocks = Integer(block, dataBlock, "Num Blocks");
        var numData = Integer(block, dataBlock, "Num Data");
        var payload = BytesField(block, dataBlock, "Data");
        var shaderIndex = dataBlock.TryGet("Shader Index", out var shaderValue) && shaderValue is NifIntegerValue shader
            ? shader.Value
            : -1;
        var blockTotal = dataBlock.TryGet("Total Size", out var totalValue) && totalValue is NifIntegerValue total
            ? total.Value
            : -1;
        var offsetsAtZero = dataBlock.TryGet("Block Offsets", out var offsetsValue) &&
                            offsetsValue is NifArrayValue { IsRows: false } offsets &&
                            offsets.Items.All(v => v is NifIntegerValue { Value: 0 });
        structure["blockSize"] = blockSize;
        structure["innerBlocks"] = innerBlocks;
        structure["innerBlockOffsetsAllZero"] = offsetsAtZero;
        structure["numData"] = numData;
        structure["payloadBytes"] = payload.Length;
        structure["shaderIndex"] = shaderIndex;
        structure["shaderIndexMeasuredForLayout"] = layout.MeasuredShaderIndex;
        structure["shaderIndexMatchesMeasured"] = shaderIndex == layout.MeasuredShaderIndex;
        structure["blockTotalSize"] = blockTotal;
        structure["blockTotalSizeEqualsStride"] = blockTotal == layout.Stride;

        var expected = (long)vertexCount * layout.Stride;
        if (vertexCount == 0)
        {
            // The same authored condition as an inline data block with Num Vertices 0: no primitive, the same reason
            // on every platform (the inline reader says "it stores no vertices").
            reason = NifModelCoverage.EmptyGeometryReason;
            detail = "it declares zero vertices";
            return false;
        }

        if (payload.Length == 0)
        {
            reason = NifModelCoverage.PackedPayloadReason;
            detail = string.Create(CultureInfo.InvariantCulture,
                $"its payload is empty although it declares {vertexCount} vertices.");
            return false;
        }

        if (payload.Length != expected || blockSize != expected || innerBlocks != 1 || !offsetsAtZero)
        {
            reason = NifModelCoverage.PackedPayloadReason;
            detail = string.Create(CultureInfo.InvariantCulture,
                $"its payload holds {payload.Length} bytes and Block Size {blockSize} with {innerBlocks} inner " +
                $"block(s), but {vertexCount} vertices x stride {layout.Stride} = {expected} bytes in one inner " +
                $"block at offset 0 are required.");
            return false;
        }

        var payloadOffset = PayloadOffset(block, innerBlocks, numData);
        streams = Decode(block, layout, platform, vertexCount, payloadOffset, payload.Span, structure);
        return true;
    }

    private static NifPackedGeometryStreams Decode(NifDecodedBlock block, NifPackedGeometryLayout layout,
        NifPackedPlatformSelection platform, int vertexCount, int payloadOffset, ReadOnlySpan<byte> payload,
        JsonObject structure)
    {
        var stride = layout.Stride;
        var positions = new float[vertexCount * 3];
        var normals = new float[vertexCount * 3];
        var texCoords = new float[vertexCount * 2];
        var bitangents = new float[vertexCount * 3];
        var tangents = new float[vertexCount * 3];
        var colors = layout.HasVertexColors ? new float[vertexCount * 4] : null;
        var weights = layout.IsSkinned ? new float[vertexCount * 4] : null;
        var storedSlot3 = layout.IsSkinned ? new float[vertexCount] : null;
        var boneIndices = layout.IsSkinned ? new byte[vertexCount * 4] : null;
        var censuses = new List<NifPackedHalfCensus>();
        int tangentFrameNonFinite = 0, texCoordNonFinite = 0, weightNonFinite = 0, sentinels = 0, slot3NonZero = 0,
            slot3Residual = 0, colorOrderSensitive = 0;

        foreach (var stream in layout.Streams)
        {
            switch (stream.Encoding)
            {
                case NifPackedStreamEncoding.HalfVector3:
                    censuses.Add(DecodeHalfVector3(block, stream, payload, vertexCount, stride, payloadOffset,
                        Target3(stream.Kind, positions, normals, bitangents, tangents),
                        stream.Kind is NifPackedStreamKind.Position or NifPackedStreamKind.Normal,
                        ref tangentFrameNonFinite));
                    break;
                case NifPackedStreamEncoding.FloatVector3:
                    DecodeFloatVector3(block, stream, payload, vertexCount, stride, payloadOffset,
                        Target3(stream.Kind, positions, normals, bitangents, tangents),
                        stream.Kind == NifPackedStreamKind.Normal, ref tangentFrameNonFinite);
                    break;
                case NifPackedStreamEncoding.HalfVector2:
                    DecodeHalfVector2(stream, payload, vertexCount, stride, texCoords, ref texCoordNonFinite);
                    break;
                case NifPackedStreamEncoding.D3DColor:
                    DecodeColors(stream, payload, vertexCount, stride, platform.Platform, colors!,
                        ref colorOrderSensitive);
                    break;
                case NifPackedStreamEncoding.HalfWeights4:
                    DecodeWeights(stream, payload, vertexCount, stride, weights!, storedSlot3!, ref weightNonFinite,
                        ref sentinels, ref slot3NonZero, ref slot3Residual);
                    break;
                case NifPackedStreamEncoding.UByte4Reversed:
                    DecodeBoneIndices(stream, payload, vertexCount, stride, boneIndices!);
                    break;
                default:
                    throw new InvalidOperationException($"Unhandled packed encoding {stream.Encoding}.");
            }
        }

        return new NifPackedGeometryStreams(block.Index, layout, platform, vertexCount, payloadOffset, positions,
            normals, texCoords, bitangents, tangents, colors, weights, boneIndices, tangentFrameNonFinite,
            texCoordNonFinite, weightNonFinite, sentinels, slot3NonZero, slot3Residual, colorOrderSensitive,
            censuses.AsReadOnly(), structure)
        {
            StoredSlot3Weights = storedSlot3
        };
    }

    private static float[] Target3(NifPackedStreamKind kind, float[] positions, float[] normals, float[] bitangents,
        float[] tangents)
    {
        return kind switch
        {
            NifPackedStreamKind.Position => positions,
            NifPackedStreamKind.Normal => normals,
            NifPackedStreamKind.Bitangent => bitangents,
            NifPackedStreamKind.Tangent => tangents,
            _ => throw new InvalidOperationException($"{kind} is not a three-component channel.")
        };
    }

    private static NifPackedHalfCensus DecodeHalfVector3(NifDecodedBlock block, NifPackedStream stream,
        ReadOnlySpan<byte> payload, int vertexCount, int stride, int payloadOffset, float[] target, bool required,
        ref int nonFinite)
    {
        int ones = 0, others = 0;
        ushort? firstOther = null;
        for (var v = 0; v < vertexCount; v++)
        {
            var at = v * stride + stream.Offset;
            for (var c = 0; c < 3; c++)
            {
                var value = NifPackedHalf.ToSingle(BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(at + c * 2, 2)));
                if (!float.IsFinite(value))
                {
                    if (required)
                    {
                        throw NonFinite(block, stream, v, c, payloadOffset + at + c * 2,
                            BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(at + c * 2, 2)));
                    }

                    nonFinite++;
                }

                target[v * 3 + c] = value;
            }

            var fourth = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(at + 6, 2));
            if (fourth == NifPackedHalf.OneBits)
            {
                ones++;
            }
            else
            {
                others++;
                firstOther ??= fourth;
            }
        }

        return new NifPackedHalfCensus(stream.Kind, ones, others, firstOther);
    }

    private static void DecodeFloatVector3(NifDecodedBlock block, NifPackedStream stream, ReadOnlySpan<byte> payload,
        int vertexCount, int stride, int payloadOffset, float[] target, bool required, ref int nonFinite)
    {
        for (var v = 0; v < vertexCount; v++)
        {
            var at = v * stride + stream.Offset;
            for (var c = 0; c < 3; c++)
            {
                var bits = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(at + c * 4, 4));
                var value = BitConverter.UInt32BitsToSingle(bits);
                if (!float.IsFinite(value))
                {
                    if (required)
                    {
                        throw NonFinite(block, stream, v, c, payloadOffset + at + c * 4, bits);
                    }

                    nonFinite++;
                }

                target[v * 3 + c] = value;
            }
        }
    }

    /// <summary>Two halves widened exactly; a NaN or infinite component is kept and counted (the caller neutralizes it).</summary>
    private static void DecodeHalfVector2(NifPackedStream stream, ReadOnlySpan<byte> payload, int vertexCount,
        int stride, float[] target, ref int nonFinite)
    {
        for (var v = 0; v < vertexCount; v++)
        {
            var at = v * stride + stream.Offset;
            for (var c = 0; c < 2; c++)
            {
                var value = NifPackedHalf.ToSingle(BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(at + c * 2, 2)));
                if (!float.IsFinite(value))
                {
                    nonFinite++;
                }

                target[v * 2 + c] = value;
            }
        }
    }

    /// <summary>
    ///     Four bytes / 255 in the platform's memory order: X360 A, R, G, B (a big-endian D3DCOLOR), PS3 A, G, B, R;
    ///     written as R, G, B, A. Counts the vertices whose bytes 1..3 are not all equal, the only ones on which the
    ///     two orders decode differently (a grey or white vertex reads the same under both).
    /// </summary>
    private static void DecodeColors(NifPackedStream stream, ReadOnlySpan<byte> payload, int vertexCount, int stride,
        NifPackedPlatform platform, float[] target, ref int orderSensitive)
    {
        for (var v = 0; v < vertexCount; v++)
        {
            var at = v * stride + stream.Offset;
            byte b0 = payload[at], b1 = payload[at + 1], b2 = payload[at + 2], b3 = payload[at + 3];
            if (b1 != b2 || b2 != b3)
            {
                orderSensitive++;
            }

            var (r, g, b, a) = platform == NifPackedPlatform.Ps3 ? (b3, b1, b2, b0) : (b1, b2, b3, b0);
            target[v * 4] = r / 255f;
            target[v * 4 + 1] = g / 255f;
            target[v * 4 + 2] = b / 255f;
            target[v * 4 + 3] = a / 255f;
        }
    }

    /// <summary>
    ///     Four halves = partition slots 0..3, stored slot for slot (a vertex may use slot 3 with slots 1 and 2 empty).
    ///     A slot-3 value of exactly 1.0 whose siblings sum to 1 within <see cref="SentinelSiblingTolerance" /> is the
    ///     sentinel and reads as 0; no renormalization. Any other non-zero slot-3 half is kept raw and counted: a
    ///     value at or below <see cref="ResidualSlot3Bits" /> (2^-24 or 2^-23, the exporter's residual on vertices
    ///     whose PC partition uses at most three slots) as a residual, anything larger as a non-zero fourth weight.
    ///     The decoded fourth half itself, before the sentinel rule, is kept per vertex in
    ///     <paramref name="storedSlot3" /> (the X360 engine never reads it; the influences' facts count where it
    ///     differs from the engine's weight).
    /// </summary>
    private static void DecodeWeights(NifPackedStream stream, ReadOnlySpan<byte> payload, int vertexCount,
        int stride, float[] target, float[] storedSlot3, ref int nonFinite, ref int sentinels, ref int slot3NonZero,
        ref int slot3Residual)
    {
        var residualCeiling = NifPackedHalf.ToSingle(ResidualSlot3Bits);
        for (var v = 0; v < vertexCount; v++)
        {
            var at = v * stride + stream.Offset;
            for (var k = 0; k < 4; k++)
            {
                var value = NifPackedHalf.ToSingle(BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(at + k * 2, 2)));
                if (!float.IsFinite(value))
                {
                    nonFinite++;
                }

                target[v * 4 + k] = value;
            }

            var siblings = (double)target[v * 4] + target[v * 4 + 1] + target[v * 4 + 2];
            var w3 = target[v * 4 + 3];
            storedSlot3[v] = w3;
            if (w3 == 1f && Math.Abs(siblings - 1.0) <= SentinelSiblingTolerance)
            {
                target[v * 4 + 3] = 0f;
                sentinels++;
            }
            else if (w3 != 0f)
            {
                // 2^-24 and 2^-23 are exact in float, so the value test equals a raw-bits test for 0x0001 / 0x0002;
                // a negative or non-finite half is neither and counts as non-zero (the skin then stays native).
                if (w3 > 0f && w3 <= residualCeiling)
                {
                    slot3Residual++;
                }
                else
                {
                    slot3NonZero++;
                }
            }
        }
    }

    /// <summary>Slot k = byte [3 - k] of the big-endian uint32 (its low byte is slot 0).</summary>
    private static void DecodeBoneIndices(NifPackedStream stream, ReadOnlySpan<byte> payload, int vertexCount,
        int stride, byte[] target)
    {
        for (var v = 0; v < vertexCount; v++)
        {
            var at = v * stride + stream.Offset;
            for (var k = 0; k < 4; k++)
            {
                target[v * 4 + k] = payload[at + 3 - k];
            }
        }
    }

    /// <summary>
    ///     The absolute file offset of the vertex payload: the Blocks array's span, then Has Data (1), Block Size (4),
    ///     Num Blocks (4), the inner offsets (4 each), Num Data (4) and the Data Sizes (4 each).
    /// </summary>
    private static int PayloadOffset(NifDecodedBlock block, long innerBlocks, long numData)
    {
        foreach (var span in block.Spans)
        {
            if (string.Equals(span.Path, "Blocks", StringComparison.Ordinal))
            {
                return checked((int)(span.Offset + 1 + 4 + 4 + 4 * innerBlocks + 4 + 4 * numData));
            }
        }

        return block.Offset;
    }

    private static long Integer(NifDecodedBlock block, NifStructValue owner, string field)
    {
        return owner.TryGet(field, out var value) && value is NifIntegerValue integer
            ? integer.Value
            : throw new InvalidDataException(
                $"NIF block {block.Index} ({block.Type}) did not decode the integer '{field}' of {owner.TypeName}.");
    }

    private static NifArrayValue ArrayField(NifDecodedBlock block, NifStructValue owner, string field)
    {
        if (!owner.TryGet(field, out var value))
        {
            throw new InvalidDataException(
                $"NIF block {block.Index} ({block.Type}) did not decode '{field}' of {owner.TypeName}.");
        }

        return value as NifArrayValue ?? throw Shape(block, field, value);
    }

    private static ReadOnlyMemory<byte> BytesField(NifDecodedBlock block, NifStructValue owner, string field)
    {
        if (!owner.TryGet(field, out var value))
        {
            throw new InvalidDataException(
                $"NIF block {block.Index} ({block.Type}) did not decode '{field}' of {owner.TypeName}.");
        }

        return value is NifByteArrayValue bytes ? bytes.Bytes : throw Shape(block, field, value);
    }

    private static InvalidDataException Shape(NifDecodedBlock block, string field, NifValue value)
    {
        return new InvalidDataException(
            $"NIF block {block.Index} ({block.Type}) decoded '{field}' as {value}, not the expected shape.");
    }

    private static InvalidDataException NonFinite(NifDecodedBlock block, NifPackedStream stream, int vertex,
        int component, int offset, uint bits)
    {
        return new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
            $"NIF block {block.Index} ({block.Type}), packed {stream.Kind} channel, vertex {vertex} component " +
            $"{component} at offset 0x{offset:X}: the value 0x{bits:X} is not finite."));
    }
}
