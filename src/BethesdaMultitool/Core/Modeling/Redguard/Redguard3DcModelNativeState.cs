using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Redguard;
using BethesdaMultitool.Core.Formats.Xngine;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using BethesdaMultitool.Core.Modeling.Xngine;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Redguard;

/// <summary>
///     The native-state rows of a Redguard <c>.3DC</c> document (cut-1c plan section 4, "Native state"), each at payload
///     version <see cref="PayloadVersion" />: <see cref="HeaderKind" /> and <see cref="PreambleKind" /> (document);
///     the <c>.3D</c> per-plane row <see cref="XnGineModelNativeState.PlanesKind" /> (mesh 0; the plane list is byte for
///     byte the <c>.3D</c> layout, and it keeps the texture keys, plan decision D3) and, only when a plane yields no
///     triangle, <see cref="XnGineModelNativeState.OmittedPlanesKind" />; <see cref="FramesKind" /> (mesh 0: the frame
///     table with its fourth dwords, the block lengths and every normal and plane-data block's digest); with
///     <see cref="ModelNativeDetail.Full" /> one <see cref="FrameNormalsKind" /> and one <see cref="FramePlaneDataKind" />
///     row per frame carrying the raw block; <see cref="UnreferencedPointsKind" /> (mesh 0, only when a point reaches no
///     vertex: its stored value in every frame); <see cref="UnaccountedKind" /> (document, when the region exists);
///     <see cref="ClipKind" /> (the clip, or the document for a single-frame stack); the <c>.3D</c> rule row
///     <see cref="XnGineModelNativeState.UvRuleKind" /> with the pose-union triangulation; and
///     <see cref="XnGineModelNativeState.ContainerKind" /> when the source answered container facts.
/// </summary>
/// <remarks>
///     Bounds (the cut-1a rule): the per-frame list is summarized as its count plus the SHA-256 of
///     <see cref="FramesCanonicalEncoding" /> when it has more than <see cref="XnGineModelNativeState.MaximumInlineElements" />
///     entries, unless the read asked for full detail; raw bytes are retained only with full detail. The header's
///     +24/+48/+52 offsets are recorded with the measured note that they are frame 1's on wide files only; the reader
///     takes every block from the frame table.
/// </remarks>
internal static class Redguard3DcModelNativeState
{
    /// <summary>The header row kind.</summary>
    public const string HeaderKind = "bmt.redguard.3dc.header";

    /// <summary>The frame-block preamble row kind.</summary>
    public const string PreambleKind = "bmt.redguard.3dc.preamble";

    /// <summary>The frame-table row kind.</summary>
    public const string FramesKind = "bmt.redguard.3dc.frames";

    /// <summary>The raw per-frame normal block row kind (full detail only).</summary>
    public const string FrameNormalsKind = "bmt.redguard.3dc.frame-normals";

    /// <summary>The raw per-frame plane-data block row kind (full detail only).</summary>
    public const string FramePlaneDataKind = "bmt.redguard.3dc.frame-plane-data";

    /// <summary>The unaccounted-region row kind.</summary>
    public const string UnaccountedKind = "bmt.redguard.3dc.unaccounted";

    /// <summary>
    ///     The unreferenced-points row kind (slice-6 review finding 2): the points no primitive vertex names, with their
    ///     stored value in every frame, which no vertex, morph target or other row carries (7 points in 4 retail files:
    ///     LASRA001 1, SKELA001, SKELA002 and SKELA004 2 each).
    /// </summary>
    public const string UnreferencedPointsKind = "bmt.redguard.3dc.unreferenced-points";

    /// <summary>
    ///     The encoding the unreferenced-points digest and raw content use: per unreferenced point, ascending, the int32
    ///     point index, then for frames 0 to N-1 the point's bytes exactly as that frame's point block stores them (12,
    ///     int32 x, y, z, for the keyframe and every wide frame; 6, int16 dx, dy, dz, for a narrow later frame), all
    ///     little-endian.
    /// </summary>
    public const string UnreferencedPointsCanonicalEncoding = "bmt.redguard.3dc.unreferenced-points-canonical/1";

    /// <summary>The clip row kind.</summary>
    public const string ClipKind = "bmt.redguard.3dc.clip";

    /// <summary>The payload schema version of every kind.</summary>
    public const int PayloadVersion = 1;

    /// <summary>
    ///     The encoding the per-frame summary digest is computed over: per frame, int32 ordinal, int32 point, normal and
    ///     plane-data offsets, u8 whether a fourth dword exists, int32 fourth dword (0 when absent), then the 32-byte
    ///     SHA-256 of the normal block and of the plane-data block (all little-endian).
    /// </summary>
    public const string FramesCanonicalEncoding = "bmt.redguard.3dc.frames-canonical/1";

    /// <summary>The measured note on the header's +24/+48/+52 offsets.</summary>
    public const string HeaderOffsetsNote =
        "header +24, +48 and +52 are frame 1's plane-data, point and normal block offsets on the 37 retail wide files " +
        "only; on the 110 narrow files they name no frame and on 22 of them +24 lies past the end of the file; the " +
        "reader takes every block from the frame table";

    /// <summary>Everything the rows describe for one read.</summary>
    /// <param name="Item">The source occurrence.</param>
    /// <param name="Bytes">The file the reader parsed.</param>
    /// <param name="Sha256">The file's SHA-256.</param>
    /// <param name="File">The parsed frame stack.</param>
    /// <param name="Mesh">The stored-UV keyframe parse.</param>
    /// <param name="GameEvidence">How the game was established.</param>
    /// <param name="Container">The container facts, or null.</param>
    /// <param name="Planes">The pose-union triangulation per plane.</param>
    /// <param name="Rules">The UV rule result per plane.</param>
    /// <param name="Geometry">The built geometry.</param>
    /// <param name="PoseDependentNgons">The n-gons whose pose-union kept corners differ from the keyframe's own.</param>
    /// <param name="UnreferencedPoints">The points no primitive vertex names, ascending.</param>
    /// <param name="Tiling">The byte-area tiling.</param>
    /// <param name="Detail">The requested native detail.</param>
    public sealed record Inputs(
        ModelSourceItem Item,
        byte[] Bytes,
        string Sha256,
        Redguard3DcFile File,
        XnGineMesh Mesh,
        string GameEvidence,
        ClassicContainerFacts? Container,
        IReadOnlyList<XnGineTriangulatedPlane> Planes,
        IReadOnlyList<XnGineUvRuleResult> Rules,
        XnGineModelGeometryResult Geometry,
        IReadOnlyList<int> PoseDependentNgons,
        IReadOnlyList<int> UnreferencedPoints,
        ByteAreaTiling Tiling,
        ModelNativeDetail Detail);

    /// <summary>Builds every row (see the type summary).</summary>
    /// <exception cref="NotSupportedException">The omitted-planes list alone exceeds the payload budget.</exception>
    public static IReadOnlyList<SceneNativeState> Build(Inputs inputs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var full = inputs.Detail == ModelNativeDetail.Full;
        var file = inputs.File;
        var document = new SceneElementRef(SceneElementKind.Document);
        var mesh = new SceneElementRef(SceneElementKind.Mesh, 0);
        var rows = new List<SceneNativeState>
        {
            new(document, HeaderKind, PayloadVersion, Header(inputs).ToJsonString(),
                Location(inputs, Redguard3DcModelCoverage.HeaderElement, 0, XnGineMesh.HeaderLength)),
            new(document, PreambleKind, PayloadVersion, Preamble(file).ToJsonString(),
                Location(inputs, Redguard3DcModelCoverage.FrameBlockElement, file.FrameBlockOffset,
                    file.FrameTableOffset - file.FrameBlockOffset)),
            new(mesh, XnGineModelNativeState.PlanesKind, XnGineModelNativeState.PayloadVersion,
                XnGineModelNativeState.PlanesPayload(inputs.Mesh.Planes, full),
                Location(inputs, "planes", file.PlaneListOffset, file.PlaneListEnd - file.PlaneListOffset))
        };

        if (inputs.Planes.Any(static plane => plane.IsOmitted))
        {
            var omitted = XnGineModelNativeState.OmittedPlanesPayload(inputs.Mesh, inputs.Planes,
                inputs.Geometry.EmptiedKeys).ToJsonString();
            if (omitted.Length > SceneNativeState.MaximumPayloadCharacters)
            {
                throw new NotSupportedException(string.Create(CultureInfo.InvariantCulture,
                    $"The stack's omitted planes need {omitted.Length} payload characters, more than the " +
                    $"{SceneNativeState.MaximumPayloadCharacters} one native-state row allows."));
            }

            rows.Add(new SceneNativeState(mesh, XnGineModelNativeState.OmittedPlanesKind,
                XnGineModelNativeState.PayloadVersion, omitted));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var blocks = Enumerable.Range(0, file.FrameCount).Select(file.FrameBlocks).ToList();
        rows.Add(new SceneNativeState(mesh, FramesKind, PayloadVersion, Frames(file, blocks, full),
            Location(inputs, Redguard3DcModelCoverage.FrameTableElement, file.FrameTableOffset,
                file.PlaneListOffset - file.FrameTableOffset)));
        if (full)
        {
            foreach (var block in blocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add(Range(inputs, mesh, FrameNormalsKind, block.Normals, block.Frame));
                rows.Add(Range(inputs, mesh, FramePlaneDataKind, block.PlaneData, block.Frame));
            }
        }

        if (inputs.UnreferencedPoints.Count > 0)
        {
            rows.Add(UnreferencedPointsRow(inputs, mesh, full));
        }

        foreach (var gap in inputs.Tiling.Gaps)
        {
            var payload = RangePayload(inputs, gap);
            payload["declaredLength"] = file.UnaccountedLength;
            ReadOnlyMemory<byte> raw = inputs.Bytes.AsMemory(gap.Start, gap.Length);
            rows.Add(new SceneNativeState(document, UnaccountedKind, PayloadVersion, payload.ToJsonString(),
                Location(inputs, Redguard3DcModelCoverage.UnaccountedElement, gap.Start, gap.Length),
                full ? raw : (ReadOnlyMemory<byte>?)null));
        }

        var clipTarget = file.FrameCount >= 2 ? new SceneElementRef(SceneElementKind.Animation, 0) : document;
        rows.Add(new SceneNativeState(clipTarget, ClipKind, PayloadVersion,
            Redguard3DcModelAnimation.NativePayload(file.FrameCount).ToJsonString()));

        var rule = XnGineModelNativeState.UvRulePayload(inputs.Planes, inputs.Rules, inputs.Geometry, unfold: false,
            inputs.Detail, XnGineTriangulation.PoseUnionRuleId);
        rule["keptCornerRule"] = "an n-gon corner is kept when the reference corner test keeps it in at least one pose";
        rule["poseDependentNgons"] = inputs.PoseDependentNgons.Count;
        rule["poseDependentNgonOrdinals"] = full || inputs.PoseDependentNgons.Count <= XnGineModelNativeState.MaximumInlineElements
            ? XnGineModelNativeState.Integers(inputs.PoseDependentNgons)
            : new JsonObject
            {
                ["count"] = inputs.PoseDependentNgons.Count,
                ["note"] = "summarized: more than 64 ordinals; read with full native detail"
            };
        rule["normals"] = "Flat on every primitive (plan decision D4): no normal a .3D reader could use is stored";
        rule["uvDecoded"] = false;
        rule["uvNote"] = "the .3DC UV encoding is not decoded (plan decision D3): the portable UVs apply the .3D " +
                         "reference rule to the keyframe without the packed-UV unfold";
        rows.Add(new SceneNativeState(document, XnGineModelNativeState.UvRuleKind, XnGineModelNativeState.PayloadVersion,
            rule.ToJsonString()));

        if (inputs.Container is { } container)
        {
            rows.Add(new SceneNativeState(document, XnGineModelNativeState.ContainerKind,
                XnGineModelNativeState.PayloadVersion, XnGineModelNativeState.ContainerPayload(container).ToJsonString()));
        }

        return rows;
    }

    /// <summary>The SHA-256 of the per-frame canonical encoding (<see cref="FramesCanonicalEncoding" />).</summary>
    public static string FramesDigest(Redguard3DcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> fixedPart = stackalloc byte[21];
        for (var frame = 0; frame < file.FrameCount; frame++)
        {
            var record = file.FrameTable[frame];
            var blocks = file.FrameBlocks(frame);
            BinaryPrimitives.WriteInt32LittleEndian(fixedPart, frame);
            BinaryPrimitives.WriteInt32LittleEndian(fixedPart[4..], record.PointOffset);
            BinaryPrimitives.WriteInt32LittleEndian(fixedPart[8..], record.NormalOffset);
            BinaryPrimitives.WriteInt32LittleEndian(fixedPart[12..], record.PlaneDataOffset);
            fixedPart[16] = record.FourthDword is null ? (byte)0 : (byte)1;
            BinaryPrimitives.WriteInt32LittleEndian(fixedPart[17..], record.FourthDword ?? 0);
            hash.AppendData(fixedPart);
            hash.AppendData(SHA256.HashData(blocks.NormalBytes.Span));
            hash.AppendData(SHA256.HashData(blocks.PlaneDataBytes.Span));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    ///     The canonical bytes (<see cref="UnreferencedPointsCanonicalEncoding" />) of <paramref name="points" />: each
    ///     point's index, then its bytes copied from every frame's point block of <paramref name="bytes" />.
    /// </summary>
    internal static byte[] UnreferencedPointsCanonical(ReadOnlySpan<byte> bytes, Redguard3DcFile file,
        IReadOnlyList<int> points)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(points);
        var later = file.WideFrames ? Redguard3DcFile.WidePointLength : Redguard3DcFile.NarrowPointLength;
        var perPoint = 4 + Redguard3DcFile.WidePointLength + (file.FrameCount - 1) * later;
        var canonical = new byte[points.Count * perPoint];
        var cursor = 0;
        foreach (var point in points)
        {
            BinaryPrimitives.WriteInt32LittleEndian(canonical.AsSpan(cursor), point);
            cursor += 4;
            for (var frame = 0; frame < file.FrameCount; frame++)
            {
                var length = frame == 0 ? Redguard3DcFile.WidePointLength : later;
                bytes.Slice(file.FrameTable[frame].PointOffset + point * length, length).CopyTo(canonical.AsSpan(cursor));
                cursor += length;
            }
        }

        return canonical;
    }

    /// <summary>
    ///     The unreferenced-points row (<see cref="UnreferencedPointsKind" />): the count, the layout, the SHA-256 of the
    ///     canonical encoding, and each point with its keyframe triple and its later-frame values exactly as stored
    ///     (<c>later</c>, frames 1 to N-1: wide int32 poses, narrow int16 deltas from the keyframe) inline with full detail
    ///     or up to <see cref="XnGineModelNativeState.MaximumInlineElements" /> points (else, or past the row's payload
    ///     budget, summarized); with full detail the canonical bytes are the raw content, so the points survive a dump at
    ///     any count.
    /// </summary>
    private static SceneNativeState UnreferencedPointsRow(Inputs inputs, SceneElementRef target, bool full)
    {
        var file = inputs.File;
        var canonical = UnreferencedPointsCanonical(inputs.Bytes, file, inputs.UnreferencedPoints);
        var payload = new JsonObject
        {
            ["count"] = inputs.UnreferencedPoints.Count,
            ["pointCount"] = file.PointCount,
            ["frameCount"] = file.FrameCount,
            ["width"] = file.WideFrames ? "wide" : "narrow",
            ["rule"] = XnGineModelNativeState.UnreferencedPointsRule,
            ["keyframeValues"] = "int32 x, y, z, as stored",
            ["laterValues"] = file.WideFrames
                ? "int32 x, y, z poses of frames 1 to N-1, as stored"
                : "int16 dx, dy, dz deltas from the keyframe of frames 1 to N-1, as stored (never accumulated)",
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(canonical)),
            ["hashOf"] = UnreferencedPointsCanonicalEncoding
        };
        if (full || inputs.UnreferencedPoints.Count <= XnGineModelNativeState.MaximumInlineElements)
        {
            payload["points"] = UnreferencedPointValues(canonical, file);
            if (payload.ToJsonString().Length > SceneNativeState.MaximumPayloadCharacters)
            {
                payload.Remove("points");
                payload["note"] = "summarized: the values exceed one row's payload budget; the raw content holds them";
            }
        }
        else
        {
            payload["note"] = "summarized: more than 64 points; read with full native detail";
        }

        ReadOnlyMemory<byte> raw = canonical;
        return new SceneNativeState(target, UnreferencedPointsKind, PayloadVersion, payload.ToJsonString(),
            Location(inputs, Redguard3DcModelCoverage.FramePointsElement(0), file.FrameTable[0].PointOffset,
                (long)file.PointCount * Redguard3DcFile.WidePointLength),
            full ? raw : (ReadOnlyMemory<byte>?)null);
    }

    /// <summary>The unreferenced points decoded back from their canonical bytes, one object per point.</summary>
    private static JsonArray UnreferencedPointValues(byte[] canonical, Redguard3DcFile file)
    {
        var points = new JsonArray();
        var span = canonical.AsSpan();
        var cursor = 0;
        while (cursor < span.Length)
        {
            var point = BinaryPrimitives.ReadInt32LittleEndian(span[cursor..]);
            var keyframe = XnGineModelNativeState.Integers([
                BinaryPrimitives.ReadInt32LittleEndian(span[(cursor + 4)..]),
                BinaryPrimitives.ReadInt32LittleEndian(span[(cursor + 8)..]),
                BinaryPrimitives.ReadInt32LittleEndian(span[(cursor + 12)..])
            ]);
            cursor += 4 + Redguard3DcFile.WidePointLength;
            var later = new JsonArray();
            for (var frame = 1; frame < file.FrameCount; frame++)
            {
                later.Add(file.WideFrames
                    ? XnGineModelNativeState.Integers([
                        BinaryPrimitives.ReadInt32LittleEndian(span[cursor..]),
                        BinaryPrimitives.ReadInt32LittleEndian(span[(cursor + 4)..]),
                        BinaryPrimitives.ReadInt32LittleEndian(span[(cursor + 8)..])
                    ])
                    : XnGineModelNativeState.Integers([
                        BinaryPrimitives.ReadInt16LittleEndian(span[cursor..]),
                        BinaryPrimitives.ReadInt16LittleEndian(span[(cursor + 2)..]),
                        BinaryPrimitives.ReadInt16LittleEndian(span[(cursor + 4)..])
                    ]));
                cursor += file.WideFrames ? Redguard3DcFile.WidePointLength : Redguard3DcFile.NarrowPointLength;
            }

            points.Add(new JsonObject { ["point"] = point, ["keyframe"] = keyframe, ["later"] = later });
        }

        return points;
    }

    /// <summary>The header row: every header field, the offsets note, the frame layout, the game and the file digest.</summary>
    private static JsonObject Header(Inputs inputs)
    {
        var bytes = inputs.Bytes.AsSpan();
        var file = inputs.File;
        var plus24 = BinaryPrimitives.ReadInt32LittleEndian(bytes[24..]);
        var plus48 = BinaryPrimitives.ReadInt32LittleEndian(bytes[48..]);
        var plus52 = BinaryPrimitives.ReadInt32LittleEndian(bytes[52..]);
        var frameOne = file.FrameCount > 1 ? file.FrameTable[1] : (Redguard3DcFrameTableRecord?)null;
        return new JsonObject
        {
            ["tag"] = inputs.Mesh.VersionTag,
            ["pointCount"] = file.PointCount,
            ["planeCount"] = file.PlaneCount,
            ["radius"] = inputs.Mesh.Radius,
            ["frameCount"] = file.FrameCount,
            ["frameBlockOffset"] = file.FrameBlockOffset,
            ["plus24"] = plus24,
            ["plus28"] = BinaryPrimitives.ReadInt32LittleEndian(bytes[28..]),
            ["plus32"] = BinaryPrimitives.ReadInt32LittleEndian(bytes[32..]),
            ["plus36"] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[36..]),
            ["plus40"] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[40..]),
            ["plus44"] = file.HeaderUnknown44,
            ["plus48"] = plus48,
            ["plus52"] = plus52,
            ["plus56"] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[56..]),
            ["planeListOffset"] = file.PlaneListOffset,
            ["planeListEnd"] = file.PlaneListEnd,
            ["recordLength"] = bytes.Length,
            ["sha256"] = inputs.Sha256,
            ["width"] = file.WideFrames ? "wide" : "narrow",
            ["frameRecordDwords"] = file.FrameRecordDwords,
            ["headerOffsetsAreFrameOne"] = frameOne is { } one && plus24 == one.PlaneDataOffset &&
                                           plus48 == one.PointOffset && plus52 == one.NormalOffset,
            ["headerOffsetsNote"] = HeaderOffsetsNote,
            ["layout"] = XnGineMeshLayout.Daggerfall.ToString(),
            ["planeHeaderLength"] = XnGineContentFacts.DaggerfallPlaneHeaderLength,
            ["uvHandling"] = inputs.Mesh.UvHandling.ToString(),
            ["game"] = "Redguard",
            ["gameEvidence"] = inputs.GameEvidence
        };
    }

    /// <summary>The preamble row: the six frame-block dwords, the two decoded ones named.</summary>
    private static JsonObject Preamble(Redguard3DcFile file)
    {
        return new JsonObject
        {
            ["dwords"] = XnGineModelNativeState.Integers(file.Preamble),
            ["frameTableOffset"] = file.FrameTableOffset,
            ["unaccountedLength"] = file.UnaccountedLength,
            ["undecodedDwords"] = XnGineModelNativeState.Integers([1, 3, 4, 5]),
            ["note"] = "dword 3 is 8209 on all 110 retail narrow files and 16402 on 19 of the 37 wide ones; dwords 1, 4 " +
                       "and 5 vary per file"
        };
    }

    /// <summary>The frames row: the layout, then every frame inline up to the bound (or with full detail), else summarized.</summary>
    private static string Frames(Redguard3DcFile file, IReadOnlyList<Redguard3DcFrameBlocks> blocks, bool full)
    {
        var payload = new JsonObject
        {
            ["width"] = file.WideFrames ? "wide" : "narrow",
            ["recordDwords"] = file.FrameRecordDwords,
            ["frameCount"] = file.FrameCount,
            ["keyframePointBytes"] = file.PointCount * Redguard3DcFile.WidePointLength,
            ["laterPointBytes"] = file.PointCount *
                                  (file.WideFrames ? Redguard3DcFile.WidePointLength : Redguard3DcFile.NarrowPointLength),
            ["normalBlockBytes"] = file.PlaneCount *
                                   (file.WideFrames ? Redguard3DcFile.WideNormalLength : Redguard3DcFile.NarrowNormalLength),
            ["planeDataBlockBytes"] = file.PlaneCount * (file.WideFrames
                ? Redguard3DcFile.WidePlaneDataLength
                : Redguard3DcFile.NarrowPlaneDataLength),
            ["normalsNote"] = file.WideFrames
                ? "authored per-pose plane normals (int32 triples); the geometry is Flat, so they are native state only"
                : "4-byte per-plane frame records, undecoded"
        };
        if (full || file.FrameCount <= XnGineModelNativeState.MaximumInlineElements)
        {
            var frames = new JsonArray();
            for (var frame = 0; frame < file.FrameCount; frame++)
            {
                var record = file.FrameTable[frame];
                frames.Add(new JsonObject
                {
                    ["frame"] = frame,
                    ["pointOffset"] = record.PointOffset,
                    ["normalOffset"] = record.NormalOffset,
                    ["planeDataOffset"] = record.PlaneDataOffset,
                    ["fourthDword"] = record.FourthDword is { } fourth ? JsonValue.Create(fourth) : null,
                    ["normalsSha256"] = Convert.ToHexStringLower(SHA256.HashData(blocks[frame].NormalBytes.Span)),
                    ["planeDataSha256"] = Convert.ToHexStringLower(SHA256.HashData(blocks[frame].PlaneDataBytes.Span))
                });
            }

            payload["frames"] = frames;
            var text = payload.ToJsonString();
            if (text.Length <= SceneNativeState.MaximumPayloadCharacters)
            {
                return text;
            }

            payload.Remove("frames");
            payload["note"] = "summarized: the full list exceeds one row's payload budget";
        }

        payload["frames"] = new JsonObject
        {
            ["count"] = file.FrameCount,
            ["sha256"] = FramesDigest(file),
            ["hashOf"] = FramesCanonicalEncoding
        };
        return payload.ToJsonString();
    }

    /// <summary>A raw block row (full detail only): its frame, range and digest, with the bytes.</summary>
    private static SceneNativeState Range(Inputs inputs, SceneElementRef target, string kind, ByteArea area, int frame)
    {
        var payload = RangePayload(inputs, area);
        payload["frame"] = frame;
        ReadOnlyMemory<byte> raw = inputs.Bytes.AsMemory(area.Start, area.Length);
        return new SceneNativeState(target, kind, PayloadVersion, payload.ToJsonString(),
            Location(inputs, area.Name, area.Start, area.Length), raw);
    }

    /// <summary>A byte range's offset, end, length and digest.</summary>
    private static JsonObject RangePayload(Inputs inputs, ByteArea area)
    {
        return new JsonObject
        {
            ["start"] = area.Start,
            ["end"] = area.End,
            ["length"] = area.Length,
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(inputs.Bytes.AsSpan(area.Start, area.Length)))
        };
    }

    /// <summary>A source location inside the parsed file.</summary>
    private static SceneSourceLocation Location(Inputs inputs, string element, long offset, long? length)
    {
        var reference = inputs.Item.Reference;
        return new SceneSourceLocation(reference.SourceId, element, offset, length, reference);
    }
}
