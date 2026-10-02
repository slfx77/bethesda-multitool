using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Xngine;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Xngine;

/// <summary>
///     The native-state rows of an XnGine <c>.3D</c> document (cut-1c plan section 3.4), each at payload version
///     <see cref="PayloadVersion" />: <see cref="HeaderKind" /> (document), <see cref="PlanesKind" /> (mesh 0),
///     <see cref="OmittedPlanesKind" /> (mesh 0, only when a plane yields no triangle),
///     <see cref="UnreferencedPointsKind" /> (mesh 0, only when a point reaches no vertex), <see cref="PlaneDataKind" />
///     (mesh 0, when that area fits), <see cref="ObjectDataKind" /> (document, when its count is positive), one
///     <see cref="UnclaimedKind" /> row per unclaimed range (document), <see cref="UvRuleKind" /> (document) and
///     <see cref="ContainerKind" /> (document, only when the source answered container facts).
/// </summary>
/// <remarks>
///     Bounds (the cut-1a rule): the per-plane array is summarized as its count plus the SHA-256 of its canonical
///     encoding (<see cref="PlanesCanonicalEncoding" />) when it has more than <see cref="MaximumInlineElements" />
///     entries, unless the read asked for <see cref="ModelNativeDetail.Full" />; raw bytes (plane data, unclaimed ranges)
///     are retained only with Full. The omitted planes are always listed in full (at most 66 per retail mesh). A row
///     whose payload would exceed <see cref="SceneNativeState.MaximumPayloadCharacters" /> falls back to the summary.
/// </remarks>
internal static class XnGineModelNativeState
{
    /// <summary>The header row kind.</summary>
    public const string HeaderKind = "bmt.xngine.header";

    /// <summary>The per-plane row kind.</summary>
    public const string PlanesKind = "bmt.xngine.planes";

    /// <summary>The omitted-planes row kind.</summary>
    public const string OmittedPlanesKind = "bmt.xngine.omitted-planes";

    /// <summary>
    ///     The unreferenced-points row kind (slice-6 review finding 2): the points no primitive vertex names, with their
    ///     stored coordinates, which reach no other carrier.
    /// </summary>
    public const string UnreferencedPointsKind = "bmt.xngine.unreferenced-points";

    /// <summary>
    ///     The encoding the unreferenced-points digest and raw content use: per unreferenced point, ascending, the int32
    ///     point index then the point's 12 stored bytes (int32 x, y, z), all little-endian.
    /// </summary>
    public const string UnreferencedPointsCanonicalEncoding = "bmt.xngine.unreferenced-points-canonical/1";

    /// <summary>What makes a point unreferenced, as both XnGine readers' rows state it.</summary>
    public const string UnreferencedPointsRule =
        "a point is unreferenced when no primitive vertex names it: no plane corner does, or only corners of planes " +
        "that yield no triangle (bmt.xngine.omitted-planes keeps those planes' corner lists); its coordinates reach no " +
        "vertex, morph target or other row";

    /// <summary>The plane-data row kind.</summary>
    public const string PlaneDataKind = "bmt.xngine.plane-data";

    /// <summary>The object-data row kind.</summary>
    public const string ObjectDataKind = "bmt.xngine.object-data";

    /// <summary>The unclaimed-range row kind.</summary>
    public const string UnclaimedKind = "bmt.xngine.unclaimed";

    /// <summary>The UV and triangulation rule row kind.</summary>
    public const string UvRuleKind = "bmt.xngine.uv-rule";

    /// <summary>The container row kind.</summary>
    public const string ContainerKind = "bmt.xngine.container";

    /// <summary>The payload schema version of every kind.</summary>
    public const int PayloadVersion = 1;

    /// <summary>Arrays with more elements than this are summarized unless the read asked for full detail.</summary>
    public const int MaximumInlineElements = 64;

    /// <summary>
    ///     The encoding the per-plane summary digest is computed over: per plane, int32 ordinal, u8 corner count, u8
    ///     unknown byte, u32 texture key, u8 header-tail length, then the tail bytes (all little-endian).
    /// </summary>
    public const string PlanesCanonicalEncoding = "bmt.xngine.planes-canonical/1";

    /// <summary>Everything the rows describe for one read.</summary>
    /// <param name="Item">The source occurrence.</param>
    /// <param name="Bytes">The record the reader parsed (for an LZSS entry, the decompressed bytes).</param>
    /// <param name="Sha256">The record's SHA-256.</param>
    /// <param name="Mesh">The stored-UV parse.</param>
    /// <param name="Identity">The game identity.</param>
    /// <param name="Container">The container facts, or null.</param>
    /// <param name="Planes">The triangulation per plane.</param>
    /// <param name="Rules">The UV rule result per plane.</param>
    /// <param name="Geometry">The built geometry.</param>
    /// <param name="UnreferencedPoints">The points no primitive vertex names, ascending.</param>
    /// <param name="Tiling">The byte-area tiling.</param>
    /// <param name="Unfold">Whether the packed-UV unfold was applied.</param>
    /// <param name="Detail">The requested native detail.</param>
    public sealed record Inputs(
        ModelSourceItem Item,
        byte[] Bytes,
        string Sha256,
        XnGineMesh Mesh,
        XnGineGameIdentity Identity,
        ClassicContainerFacts? Container,
        IReadOnlyList<XnGineTriangulatedPlane> Planes,
        IReadOnlyList<XnGineUvRuleResult> Rules,
        XnGineModelGeometryResult Geometry,
        IReadOnlyList<int> UnreferencedPoints,
        ByteAreaTiling Tiling,
        bool Unfold,
        ModelNativeDetail Detail);

    /// <summary>Builds every row (see the type summary).</summary>
    /// <exception cref="NotSupportedException">The omitted-planes list alone exceeds the payload budget.</exception>
    public static IReadOnlyList<SceneNativeState> Build(Inputs inputs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var full = inputs.Detail == ModelNativeDetail.Full;
        var document = new SceneElementRef(SceneElementKind.Document);
        var mesh = new SceneElementRef(SceneElementKind.Mesh, 0);
        var rows = new List<SceneNativeState>
        {
            new(document, HeaderKind, PayloadVersion, Header(inputs).ToJsonString(),
                Location(inputs, XnGineModelCoverage.HeaderElement, 0, XnGineMesh.HeaderLength))
        };
        cancellationToken.ThrowIfCancellationRequested();
        var planeList = inputs.Mesh.PlaneListOffset;
        rows.Add(new SceneNativeState(mesh, PlanesKind, PayloadVersion, PlanesPayload(inputs.Mesh.Planes, full),
            Location(inputs, "planes", planeList, inputs.Mesh.PlaneListEnd - planeList)));

        if (inputs.Planes.Any(static plane => plane.IsOmitted))
        {
            var omitted = OmittedPlanesPayload(inputs.Mesh, inputs.Planes, inputs.Geometry.EmptiedKeys).ToJsonString();
            if (omitted.Length > SceneNativeState.MaximumPayloadCharacters)
            {
                throw new NotSupportedException(string.Create(CultureInfo.InvariantCulture,
                    $"The mesh's omitted planes need {omitted.Length} payload characters, more than the " +
                    $"{SceneNativeState.MaximumPayloadCharacters} one native-state row allows."));
            }

            rows.Add(new SceneNativeState(mesh, OmittedPlanesKind, PayloadVersion, omitted));
        }

        if (inputs.UnreferencedPoints.Count > 0)
        {
            rows.Add(UnreferencedPointsRow(inputs, mesh, full));
        }

        foreach (var area in inputs.Tiling.Areas.Where(static area =>
                     area.Name == XnGineModelCoverage.PlaneDataElement))
        {
            rows.Add(Range(inputs, mesh, PlaneDataKind, area, full, new JsonObject
            {
                ["planeCount"] = inputs.Mesh.Planes.Count,
                ["recordLength"] = 24
            }));
        }

        if (inputs.Mesh.ObjectDataCount > 0)
        {
            var offset = inputs.Mesh.ObjectDataOffset;
            var holder = XnGineModelCoverage.ObjectDataHolder(inputs.Tiling, offset);
            var unclaimed = holder is { } held &&
                            held.Name.StartsWith(ByteAreaTiling.UnclaimedPrefix, StringComparison.Ordinal);
            var payload = new JsonObject
            {
                ["offset"] = offset,
                ["count"] = inputs.Mesh.ObjectDataCount,
                ["startsUnclaimedRange"] = inputs.Tiling.Gaps.Any(gap => gap.Start == offset),
                ["heldBy"] = holder?.Name,
                ["heldByUnclaimedRange"] = unclaimed,
                ["note"] = ObjectDataNote(holder, unclaimed)
            };
            var location = offset >= 0 ? Location(inputs, XnGineModelCoverage.ObjectDataElement, offset, null) : null;
            rows.Add(new SceneNativeState(document, ObjectDataKind, PayloadVersion, payload.ToJsonString(), location));
        }

        foreach (var gap in inputs.Tiling.Gaps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(Range(inputs, document, UnclaimedKind, gap, full, new JsonObject()));
        }

        rows.Add(new SceneNativeState(document, UvRuleKind, PayloadVersion, UvRulePayload(inputs.Planes, inputs.Rules,
            inputs.Geometry, inputs.Unfold, inputs.Detail, XnGineTriangulation.RuleId).ToJsonString()));
        if (inputs.Container is { } container)
        {
            rows.Add(new SceneNativeState(document, ContainerKind, PayloadVersion,
                ContainerPayload(container).ToJsonString()));
        }

        return rows;
    }

    /// <summary>The header row: the tag, every header field, the walk's end, the identity and the record digest.</summary>
    private static JsonObject Header(Inputs inputs)
    {
        var bytes = inputs.Bytes.AsSpan();
        var mesh = inputs.Mesh;
        var identity = inputs.Identity;
        return new JsonObject
        {
            ["tag"] = mesh.VersionTag,
            ["pointCount"] = mesh.Points.Count,
            ["planeCount"] = mesh.Planes.Count,
            ["radius"] = mesh.Radius,
            ["plus16"] = BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]),
            ["plus20"] = BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]),
            ["planeDataOffset"] = mesh.PlaneDataOffset,
            ["objectDataOffset"] = mesh.ObjectDataOffset,
            ["objectDataCount"] = mesh.ObjectDataCount,
            ["plus36"] = mesh.Unknown2,
            ["plus40"] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[40..]),
            ["plus44"] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[44..]),
            ["pointListOffset"] = mesh.PointListOffset,
            ["normalListOffset"] = mesh.NormalListOffset,
            ["plus56"] = mesh.Unknown3,
            ["planeListOffset"] = mesh.PlaneListOffset,
            ["planeListEnd"] = mesh.PlaneListEnd,
            ["recordLength"] = mesh.RecordLength,
            ["sha256"] = inputs.Sha256,
            ["objectId"] = identity.ObjectId is { } id ? JsonValue.Create(id) : null,
            ["layout"] = mesh.Layout.ToString(),
            ["planeHeaderLength"] = XnGineContentFacts.PlaneHeaderLengthOf(mesh.Layout),
            ["uvHandling"] = mesh.UvHandling.ToString(),
            ["game"] = identity.Game.ToString(),
            ["gameStep"] = identity.Step.ToString(),
            ["gameEvidence"] = identity.Evidence
        };
    }

    /// <summary>
    ///     The per-plane row's payload (<see cref="PlanesKind" />): every plane's ordinal, corner count, unknown byte,
    ///     texture key and header tail, inline up to <see cref="MaximumInlineElements" /> (or with full detail), else the
    ///     count and the digest of <see cref="PlanesCanonicalEncoding" />. The <c>.3DC</c> reader writes the same row, its
    ///     plane list being byte for byte the <c>.3D</c> layout.
    /// </summary>
    internal static string PlanesPayload(IReadOnlyList<XnGinePlane> planes, bool full)
    {
        ArgumentNullException.ThrowIfNull(planes);
        var payload = new JsonObject { ["count"] = planes.Count };
        if (full || planes.Count <= MaximumInlineElements)
        {
            var array = new JsonArray();
            foreach (var plane in planes)
            {
                array.Add(new JsonObject
                {
                    ["ordinal"] = plane.Index,
                    ["pointCount"] = plane.Points.Count,
                    ["unknown1"] = plane.Unknown1,
                    ["textureKey"] = plane.TextureKey,
                    ["textureArchive"] = plane.TextureArchive,
                    ["textureRecord"] = plane.TextureRecord,
                    ["headerTail"] = Convert.ToHexStringLower(plane.HeaderTail.Span)
                });
            }

            payload["planes"] = array;
            var text = payload.ToJsonString();
            if (text.Length <= SceneNativeState.MaximumPayloadCharacters)
            {
                return text;
            }

            payload = new JsonObject
            {
                ["count"] = planes.Count,
                ["note"] = "summarized: the full list exceeds one row's payload budget"
            };
        }

        payload["planes"] = new JsonObject
        {
            ["count"] = planes.Count,
            ["sha256"] = PlanesDigest(planes),
            ["hashOf"] = PlanesCanonicalEncoding
        };
        return payload.ToJsonString();
    }

    /// <summary>The SHA-256 of the planes' canonical encoding (<see cref="PlanesCanonicalEncoding" />).</summary>
    public static string PlanesDigest(IReadOnlyList<XnGinePlane> planes)
    {
        ArgumentNullException.ThrowIfNull(planes);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> fixedPart = stackalloc byte[11];
        foreach (var plane in planes)
        {
            BinaryPrimitives.WriteInt32LittleEndian(fixedPart, plane.Index);
            fixedPart[4] = checked((byte)plane.Points.Count);
            fixedPart[5] = plane.Unknown1;
            BinaryPrimitives.WriteUInt32LittleEndian(fixedPart[6..], plane.TextureKey);
            fixedPart[10] = checked((byte)plane.HeaderTail.Length);
            hash.AppendData(fixedPart);
            hash.AppendData(plane.HeaderTail.Span);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    ///     The omitted-planes row's payload (<see cref="OmittedPlanesKind" />): every no-triangle plane of
    ///     <paramref name="triangulated" /> in full, and the texture keys left with no primitive.
    /// </summary>
    internal static JsonObject OmittedPlanesPayload(XnGineMesh mesh, IReadOnlyList<XnGineTriangulatedPlane> triangulated,
        IReadOnlyList<XnGineTextureKey> emptiedKeys)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(triangulated);
        ArgumentNullException.ThrowIfNull(emptiedKeys);
        var planes = new JsonArray();
        foreach (var result in triangulated.Where(static plane => plane.IsOmitted))
        {
            var plane = mesh.Planes[result.PlaneIndex];
            planes.Add(new JsonObject
            {
                ["ordinal"] = plane.Index,
                ["textureKey"] = plane.TextureKey,
                ["textureArchive"] = plane.TextureArchive,
                ["textureRecord"] = plane.TextureRecord,
                ["reasonCode"] = ReasonCode(result.Omission),
                ["reason"] = XnGineModelCoverage.ReasonFor(result.Omission),
                ["keptCorners"] = Integers(result.KeptCorners),
                ["referenceTriangles"] = result.ReferenceTriangleCount,
                ["points"] = Integers(plane.Points.Select(static p => p.PointIndex).ToList()),
                ["u"] = Integers(plane.Points.Select(static p => p.U).ToList()),
                ["v"] = Integers(plane.Points.Select(static p => p.V).ToList()),
                ["normal"] = Integers([plane.Normal.X, plane.Normal.Y, plane.Normal.Z])
            });
        }

        var keys = new JsonArray();
        foreach (var key in emptiedKeys)
        {
            keys.Add(new JsonObject
            {
                ["textureKey"] = key.Key,
                ["textureArchive"] = key.Archive,
                ["textureRecord"] = key.Record,
                ["material"] = key.MaterialName
            });
        }

        return new JsonObject { ["planes"] = planes, ["omittedTextureKeys"] = keys };
    }

    /// <summary>
    ///     The canonical bytes (<see cref="UnreferencedPointsCanonicalEncoding" />) of <paramref name="points" />, copied
    ///     from the stored point list at <paramref name="pointListOffset" /> in <paramref name="bytes" />.
    /// </summary>
    internal static byte[] UnreferencedPointsCanonical(ReadOnlySpan<byte> bytes, int pointListOffset,
        IReadOnlyList<int> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        const int pointLength = XnGineContentFacts.PointLength;
        var canonical = new byte[points.Count * (4 + pointLength)];
        var cursor = 0;
        foreach (var point in points)
        {
            BinaryPrimitives.WriteInt32LittleEndian(canonical.AsSpan(cursor), point);
            bytes.Slice(pointListOffset + point * pointLength, pointLength).CopyTo(canonical.AsSpan(cursor + 4));
            cursor += 4 + pointLength;
        }

        return canonical;
    }

    /// <summary>
    ///     The unreferenced-points row (<see cref="UnreferencedPointsKind" />): the count, the rule, the SHA-256 of the
    ///     canonical encoding, and each point with its stored coordinates inline with full detail or up to
    ///     <see cref="MaximumInlineElements" /> points (else summarized); with full detail the canonical bytes are the raw
    ///     content, so the points survive a dump at any count.
    /// </summary>
    private static SceneNativeState UnreferencedPointsRow(Inputs inputs, SceneElementRef target, bool full)
    {
        var mesh = inputs.Mesh;
        var canonical = UnreferencedPointsCanonical(inputs.Bytes, mesh.PointListOffset, inputs.UnreferencedPoints);
        var payload = new JsonObject
        {
            ["count"] = inputs.UnreferencedPoints.Count,
            ["pointCount"] = mesh.Points.Count,
            ["rule"] = UnreferencedPointsRule,
            ["values"] = "int32 x, y, z, as stored",
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(canonical)),
            ["hashOf"] = UnreferencedPointsCanonicalEncoding
        };
        if (full || inputs.UnreferencedPoints.Count <= MaximumInlineElements)
        {
            var points = new JsonArray();
            foreach (var point in inputs.UnreferencedPoints)
            {
                var stored = mesh.Points[point];
                points.Add(new JsonObject
                {
                    ["point"] = point,
                    ["position"] = Integers([stored.X, stored.Y, stored.Z])
                });
            }

            payload["points"] = points;
            if (payload.ToJsonString().Length > SceneNativeState.MaximumPayloadCharacters)
            {
                payload.Remove("points");
                payload["note"] = "summarized: the list exceeds one row's payload budget; the raw content holds it";
            }
        }
        else
        {
            payload["note"] = "summarized: more than 64 points; read with full native detail";
        }

        ReadOnlyMemory<byte> raw = canonical;
        return new SceneNativeState(target, UnreferencedPointsKind, PayloadVersion, payload.ToJsonString(),
            Location(inputs, XnGineModelCoverage.PointsElement, mesh.PointListOffset,
                (long)mesh.Points.Count * XnGineContentFacts.PointLength),
            full ? raw : (ReadOnlyMemory<byte>?)null);
    }

    /// <summary>The stable code of an omission reason.</summary>
    public static string ReasonCode(XnGinePlaneOmission omission)
    {
        return omission switch
        {
            XnGinePlaneOmission.FewerThanThreeCorners => "fewer-than-3-corners",
            XnGinePlaneOmission.FewerThanThreeKeptCorners => "fewer-than-3-kept-corners",
            XnGinePlaneOmission.ZeroNormalNoArea => "zero-normal-no-area",
            _ => throw new ArgumentOutOfRangeException(nameof(omission), omission, "The plane is not omitted.")
        };
    }

    /// <summary>
    ///     The object-data row's note, from where the offset falls (<see cref="XnGineModelCoverage.ObjectDataHolder" />):
    ///     only an unclaimed range's <c>bmt.xngine.unclaimed</c> row can keep the bytes; an offset inside a declared area
    ///     (2 retail records) or outside the record leaves only the offset and count.
    /// </summary>
    private static string ObjectDataNote(ByteArea? holder, bool unclaimed)
    {
        const string prefix = "no stated length and variable-length records; ";
        if (holder is not { } area)
        {
            return prefix + "the offset lies outside the record, so only the offset and count are kept";
        }

        return unclaimed
            ? prefix + "the offset lies in the unclaimed range " + area.Name +
              ", whose bmt.xngine.unclaimed row keeps those bytes"
            : prefix + "the offset lies inside the declared " + area.Name +
              " area, so no bmt.xngine.unclaimed row holds these bytes; only the offset and count are kept";
    }

    /// <summary>
    ///     The UV and triangulation rule row's payload (<see cref="UvRuleKind" />): the rule ids (the triangulation rule is
    ///     <paramref name="triangulationRule" />, <see cref="XnGineTriangulation.RuleId" /> for a static mesh and
    ///     <see cref="XnGineTriangulation.PoseUnionRuleId" /> for a <c>.3DC</c> stack), whether and how the unfold
    ///     applied, the counts, and the drawn planes whose corners repeat a source point (the faces Blender omits).
    /// </summary>
    internal static JsonObject UvRulePayload(IReadOnlyList<XnGineTriangulatedPlane> planes,
        IReadOnlyList<XnGineUvRuleResult> rules, XnGineModelGeometryResult geometry, bool unfold, ModelNativeDetail detail,
        string triangulationRule)
    {
        ArgumentNullException.ThrowIfNull(planes);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentException.ThrowIfNullOrWhiteSpace(triangulationRule);
        return new JsonObject
        {
            ["uvRule"] = XnGineUvRule.RuleId,
            ["uvProvenance"] = SceneValueProvenance.Assumed.ToString(),
            ["unfoldApplied"] = unfold,
            ["unfoldedValues"] = rules.Sum(static rule => rule.UnfoldedValues),
            ["degenerateFits"] = rules.Count(static rule => rule.DegenerateFit),
            ["textureSize"] = new JsonObject
            {
                ["width"] = XnGineModelGeometry.FallbackTextureSize,
                ["height"] = XnGineModelGeometry.FallbackTextureSize,
                ["provenance"] = SceneValueProvenance.Assumed.ToString(),
                ["reason"] = "textures are not resolved by this reader yet; the legacy export's 64-texel fallback"
            },
            ["triangulationRule"] = triangulationRule,
            ["windingRule"] = XnGineModelBasis.WindingRuleId,
            ["collinearCornersDropped"] = planes.Where(static plane => !plane.IsOmitted)
                .Sum(static plane => plane.CollinearCornersDropped),
            ["referenceTriangles"] = planes.Sum(static plane => plane.ReferenceTriangleCount),
            ["triangles"] = planes.Sum(static plane => plane.Triangles.Count),
            ["zeroAreaTriangles"] = planes.Sum(static plane => plane.ZeroAreaTriangles),
            ["omittedZeroAreaTriangles"] = planes.Sum(static plane => plane.OmittedZeroAreaTriangles),
            ["foldedTriangles"] = planes.Sum(static plane => plane.FoldedTriangles),
            ["omittedPlanes"] = planes.Count(static plane => plane.IsOmitted),
            ["omittedTextureKeys"] = geometry.EmptiedKeys.Count,
            ["repeatedPointPlanes"] = geometry.RepeatedPointPlanes.Count,
            ["repeatedPointPlanesWithArea"] = geometry.RepeatedPointPlanesWithArea,
            ["repeatedPointPlaneOrdinals"] = RepeatedPointOrdinals(geometry, detail)
        };
    }

    /// <summary>
    ///     The drawn planes whose corners repeat a source point, inline up to <see cref="MaximumInlineElements" /> (or
    ///     with full detail; 9 at most per retail mesh), else their count with a note.
    /// </summary>
    private static JsonNode RepeatedPointOrdinals(XnGineModelGeometryResult geometry, ModelNativeDetail detail)
    {
        var ordinals = geometry.RepeatedPointPlanes;
        return detail == ModelNativeDetail.Full || ordinals.Count <= MaximumInlineElements
            ? Integers(ordinals)
            : new JsonObject
            {
                ["count"] = ordinals.Count,
                ["note"] = "summarized: more than 64 ordinals; read with full native detail"
            };
    }

    /// <summary>
    ///     The container row's payload (<see cref="ContainerKind" />): the container facts, the stored digest of an LZSS
    ///     entry, a ROB segment's header.
    /// </summary>
    internal static JsonObject ContainerPayload(ClassicContainerFacts container)
    {
        ArgumentNullException.ThrowIfNull(container);
        var payload = new JsonObject
        {
            ["kind"] = container.Kind.ToString(),
            ["container"] = container.ContainerName,
            ["entryName"] = container.EntryName,
            ["entryIndex"] = container.EntryIndex,
            ["entryCount"] = container.EntryCount,
            ["entryId"] = container.EntryId is { } id ? JsonValue.Create(id) : null,
            ["storedOffset"] = container.StoredOffset,
            ["storedSize"] = container.StoredSize,
            ["compressed"] = container.IsCompressed,
            ["compressedEntryCount"] = container.CompressedEntryCount
        };
        if (container.IsCompressed)
        {
            payload["storedSha256"] = Convert.ToHexStringLower(SHA256.HashData(container.ReadStoredBytes()));
        }

        if (container.SegmentType is { } type)
        {
            payload["segmentType"] = type;
            payload["segmentHeader"] = Convert.ToHexStringLower(container.SegmentHeader.Span);
        }

        return payload;
    }

    /// <summary>A byte-range row: offset, end, length and digest always; the raw bytes with full detail.</summary>
    private static SceneNativeState Range(Inputs inputs, SceneElementRef target, string kind, ByteArea area, bool full,
        JsonObject extra)
    {
        ReadOnlyMemory<byte> bytes = inputs.Bytes.AsMemory(area.Start, area.Length);
        var payload = new JsonObject
        {
            ["start"] = area.Start,
            ["end"] = area.End,
            ["length"] = area.Length,
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes.Span))
        };
        foreach (var (name, value) in extra.ToList())
        {
            extra.Remove(name);
            payload[name] = value;
        }

        return new SceneNativeState(target, kind, PayloadVersion, payload.ToJsonString(),
            Location(inputs, area.Name, area.Start, area.Length), full ? bytes : (ReadOnlyMemory<byte>?)null);
    }

    /// <summary>An integer list as a JSON array.</summary>
    internal static JsonArray Integers(IReadOnlyList<int> values)
    {
        return new JsonArray(values.Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray());
    }

    /// <summary>A source location inside the parsed record.</summary>
    private static SceneSourceLocation Location(Inputs inputs, string element, long offset, long? length)
    {
        var reference = inputs.Item.Reference;
        return new SceneSourceLocation(reference.SourceId, element, offset, length, reference);
    }
}
