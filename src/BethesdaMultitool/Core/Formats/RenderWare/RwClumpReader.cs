using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Text;

namespace BethesdaMultitool.Core.Formats.RenderWare;

/// <summary>
///     Strict graph assembly for the generic CLUMPs in the six dated Oblivion PSP builds.
///     Does not evaluate Skin/HAnim, interpret opaque plugins, resolve textures, change basis,
///     or publish a normalized scene. The existing geometry and BinMesh readers own their arrays.
/// </summary>
internal static class RwClumpReader
{
    private const uint Camera = 0x05;
    private const uint Light = 0x12;

    /// <summary>
    ///     Reads exactly one complete CLUMP. Malformed or unsupported core layouts return null
    ///     with a reason. Unsupported plugin semantics instead remain in the decoded diagnostics.
    /// </summary>
    public static RwClump? TryRead(ReadOnlySpan<byte> source, out string? error)
    {
        if (!RwChunk.TryRead(source, 0, source.Length, out var root) ||
            root.Type != RwChunk.Clump || root.End != source.Length)
        {
            error = "Expected exactly one complete CLUMP.";
            return null;
        }

        try
        {
            var result = new Reader(source.ToArray()).Read();
            error = null;
            return result;
        }
        catch (InvalidDataException exception)
        {
            error = exception.Message;
            return null;
        }
    }

    private sealed class Reader(byte[] data)
    {
        private readonly List<RwClumpDiagnostic> _diagnostics = [];

        public RwClump Read()
        {
            Require(RwChunk.TryRead(data, 0, data.Length, out var root) &&
                    root.Type == RwChunk.Clump && root.End == data.Length, "Expected exactly one complete CLUMP.");
            var children = Children(root);
            Require(children.Count >= 4 && children[0].Type == RwChunk.Struct, "Missing CLUMP structure.");
            var declaration = Body(children[0], 12);
            var atomicCount = U32(declaration, 0);
            var lightCount = U32(declaration, 4);
            var cameraCount = U32(declaration, 8);
            var frames = ReadFrames(Single(children, RwChunk.FrameList));
            var geometries = ReadGeometries(Single(children, RwChunk.GeometryList));
            var atomics = new List<RwClumpAtomic>();
            var attachments = new List<RwClumpAttachment>();
            var extensions = Extensions(Single(children, RwChunk.Extension), "clump");
            var childIndex = 1;
            while (childIndex < children.Count)
            {
                var child = children[childIndex++];
                switch (child.Type)
                {
                    case RwChunk.FrameList:
                    case RwChunk.GeometryList:
                    case RwChunk.Extension:
                        break;
                    case RwChunk.Atomic:
                        atomics.Add(ReadAtomic(child, frames.Count, geometries.Count, atomics.Count));
                        break;
                    case RwChunk.Struct:
                        var frameIndex = I32(Body(child, 4), 0);
                        Require(frameIndex >= 0 && frameIndex < frames.Count, "Attachment frame index is out of range.");
                        Require(childIndex < children.Count && children[childIndex].Type is Camera or Light,
                            "A frame association must be followed by a camera or light.");
                        var attachment = children[childIndex++];
                        var nested = Children(attachment);
                        Only(nested, RwChunk.Struct, RwChunk.Extension);
                        _ = Single(nested, RwChunk.Struct);
                        _ = Extensions(Single(nested, RwChunk.Extension), $"attachment[{attachments.Count}]");
                        attachments.Add(new RwClumpAttachment(frameIndex, Opaque(attachment)));
                        _diagnostics.Add(new RwClumpDiagnostic($"attachment[{attachments.Count - 1}]", attachment.Type,
                            "Camera/light semantics are not decoded."));
                        break;
                    default:
                        throw Invalid($"Unsupported CLUMP child 0x{child.Type:X}.");
                }
            }

            Require(atomics.Count == atomicCount && attachments.Count(a => a.Chunk.Type == Light) == lightCount &&
                    attachments.Count(a => a.Chunk.Type == Camera) == cameraCount, "CLUMP object counts disagree.");
            return new RwClump(root.LibraryId, frames, geometries, atomics.AsReadOnly(), attachments.AsReadOnly(),
                extensions, _diagnostics.AsReadOnly(), data);
        }

        private ReadOnlyCollection<RwClumpFrame> ReadFrames(RwChunkHeader chunk)
        {
            var children = Children(chunk);
            Only(children, RwChunk.Struct, RwChunk.Extension);
            var body = Body(Single(children, RwChunk.Struct));
            Require(body.Length >= 4, "Truncated frame count.");
            var count = U32(body, 0);
            Require(4L + count * 56L == body.Length, "Frame structure size disagrees with its count.");
            var plugins = children.Where(c => c.Type == RwChunk.Extension).ToArray();
            Require(plugins.Length == count, "Frame extension count disagrees with frame count.");
            var frames = new RwClumpFrame[(int)count];
            for (var i = 0; i < frames.Length; i++)
            {
                var p = 4 + i * 56;
                // RenderWare stores the three basis vectors followed by translation. Keep them
                // as rows for System.Numerics row-vector multiplication; no coordinate conversion.
                var matrix = new Matrix4x4(
                    F32(body, p), F32(body, p + 4), F32(body, p + 8), 0,
                    F32(body, p + 12), F32(body, p + 16), F32(body, p + 20), 0,
                    F32(body, p + 24), F32(body, p + 28), F32(body, p + 32), 0,
                    F32(body, p + 36), F32(body, p + 40), F32(body, p + 44), 1);
                var parent = I32(body, p + 48);
                Require(parent >= -1 && parent < frames.Length, "Frame parent index is out of range.");
                frames[i] = new RwClumpFrame(matrix, parent, U32(body, p + 52), Extensions(plugins[i], $"frame[{i}]"));
            }

            // Three colors visit each parent edge once, including forward references.
            var states = new byte[frames.Length];
            for (var i = 0; i < frames.Length; i++)
            {
                var cursor = i;
                while (cursor >= 0 && states[cursor] == 0)
                {
                    states[cursor] = 1;
                    cursor = frames[cursor].ParentIndex;
                }

                Require(cursor < 0 || states[cursor] != 1, "Frame hierarchy contains a cycle.");
                cursor = i;
                while (cursor >= 0 && states[cursor] == 1)
                {
                    states[cursor] = 2;
                    cursor = frames[cursor].ParentIndex;
                }
            }

            return Array.AsReadOnly(frames);
        }

        private ReadOnlyCollection<RwClumpGeometry> ReadGeometries(RwChunkHeader chunk)
        {
            var children = Children(chunk);
            Only(children, RwChunk.Struct, RwChunk.Geometry);
            var count = U32(Body(Single(children, RwChunk.Struct), 4), 0);
            var geometries = children.Where(c => c.Type == RwChunk.Geometry).ToArray();
            Require(geometries.Length == count, "Geometry list count disagrees.");
            var result = new List<RwClumpGeometry>();
            foreach (var geometry in geometries)
            {
                var nested = Children(geometry);
                Only(nested, RwChunk.Struct, RwChunk.MaterialList, RwChunk.Extension);
                var body = Body(Single(nested, RwChunk.Struct));
                Require(body.Length >= 16, "Truncated geometry structure.");
                Require((U32(body, 0) & RwGeometry.NativeFlag) == 0, "Native geometry is unsupported.");
                Require(U32(body, 12) == 1, "Only one geometry morph target is supported.");
                var decoded = RwGeometry.TryParse(body) ?? throw Invalid("Invalid generic geometry structure.");
                ValidateGeometryValues(decoded, body);
                var (materials, map) = ReadMaterials(Single(nested, RwChunk.MaterialList), result.Count);
                var extension = Single(nested, RwChunk.Extension);
                var plugins = Children(extension);
                var binMesh = RwBinMesh.TryParse(Body(Single(plugins, RwChunk.BinMeshPlugin))) ??
                              throw Invalid("Invalid BinMesh structure.");
                ValidateTopology(decoded, binMesh, materials.Count);
                result.Add(new RwClumpGeometry(decoded, materials, map, binMesh,
                    Extensions(extension, $"geometry[{result.Count}]", RwChunk.BinMeshPlugin)));
            }

            return result.AsReadOnly();
        }

        private static void ValidateGeometryValues(RwGeometry geometry, ReadOnlySpan<byte> body)
        {
            var vertices = geometry.Positions.Length;
            var morph = 16 + (geometry.Colours is null ? 0 : vertices * 4) +
                        geometry.UvSets.Length * vertices * 8 + geometry.Triangles.Length * 8;
            Require(U32(body, morph + 16) == 1 && U32(body, morph + 20) == (geometry.Normals is null ? 0u : 1u),
                "Geometry morph presence flags disagree with its arrays.");
            Require(Finite(geometry.BoundingSphere.X) && Finite(geometry.BoundingSphere.Y) &&
                    Finite(geometry.BoundingSphere.Z) && Finite(geometry.BoundingSphere.W) && geometry.BoundingSphere.W >= 0,
                "Invalid geometry bounding sphere.");
            foreach (var position in geometry.Positions)
            {
                Require(Finite(position.X) && Finite(position.Y) && Finite(position.Z), "Nonfinite geometry position.");
            }

            if (geometry.Normals is not null)
            {
                foreach (var normal in geometry.Normals)
                {
                    Require(Finite(normal.X) && Finite(normal.Y) && Finite(normal.Z), "Nonfinite geometry normal.");
                }
            }

            foreach (var set in geometry.UvSets)
            {
                foreach (var uv in set)
                {
                    Require(Finite(uv.X) && Finite(uv.Y), "Nonfinite geometry UV.");
                }
            }
        }

        private (IReadOnlyList<RwClumpMaterial> Materials, IReadOnlyList<int> Map) ReadMaterials(
            RwChunkHeader chunk, int geometryIndex)
        {
            var children = Children(chunk);
            Only(children, RwChunk.Struct, RwChunk.Material);
            var body = Body(Single(children, RwChunk.Struct));
            Require(body.Length >= 4 && 4L + U32(body, 0) * 4L == body.Length, "Invalid material map size.");
            var count = (int)U32(body, 0);
            var materialChunks = children.Where(c => c.Type == RwChunk.Material).ToArray();
            var materials = new RwClumpMaterial[count];
            var map = new int[count];
            var next = 0;
            for (var i = 0; i < count; i++)
            {
                var reference = I32(body, 4 + i * 4);
                map[i] = reference;
                Require(reference >= -1 && reference < i, "Material reuse index must reference a preceding material.");
                if (reference >= 0)
                {
                    materials[i] = materials[reference];
                    continue;
                }

                Require(next < materialChunks.Length, "Missing material chunk.");
                materials[i] = ReadMaterial(materialChunks[next++], $"geometry[{geometryIndex}].material[{i}]");
            }

            Require(next == materialChunks.Length, "Unexpected extra material chunks.");
            return (Array.AsReadOnly(materials), Array.AsReadOnly(map));
        }

        private RwClumpMaterial ReadMaterial(RwChunkHeader chunk, string scope)
        {
            var children = Children(chunk);
            Only(children, RwChunk.Struct, RwChunk.Texture, RwChunk.Extension);
            var body = Body(Single(children, RwChunk.Struct), 28);
            var textured = U32(body, 12);
            Require(textured <= 1, "Unsupported material texture presence value.");
            var textures = children.Where(c => c.Type == RwChunk.Texture).ToArray();
            Require(textures.Length == textured, "Material texture count disagrees.");
            return new RwClumpMaterial(U32(body, 0), U32(body, 4), U32(body, 8), textured,
                F32(body, 16), F32(body, 20), F32(body, 24),
                textures.Length == 0 ? null : ReadTexture(textures[0], scope + ".texture"),
                Extensions(Single(children, RwChunk.Extension), scope));
        }

        private RwClumpTexture ReadTexture(RwChunkHeader chunk, string scope)
        {
            var children = Children(chunk);
            Only(children, RwChunk.Struct, RwChunk.String, RwChunk.Extension);
            var strings = children.Where(c => c.Type == RwChunk.String).ToArray();
            Require(strings.Length == 2, "Texture requires name and mask strings.");
            return new RwClumpTexture(U32(Body(Single(children, RwChunk.Struct), 4), 0),
                ReadString(strings[0]), ReadString(strings[1]), Extensions(Single(children, RwChunk.Extension), scope));
        }

        private string ReadString(RwChunkHeader chunk)
        {
            var body = Body(chunk);
            var nul = body.IndexOf((byte)0);
            Require(nul >= 0, "Texture string lacks a NUL terminator.");
            // Keep the single-byte source values; ASCII replacement would silently lose bytes.
            return Encoding.Latin1.GetString(body[..nul]);
        }

        private RwClumpAtomic ReadAtomic(RwChunkHeader chunk, int frameCount, int geometryCount, int index)
        {
            var children = Children(chunk);
            Only(children, RwChunk.Struct, RwChunk.Extension);
            var body = Body(Single(children, RwChunk.Struct), 16);
            var frame = I32(body, 0);
            var geometry = I32(body, 4);
            Require(frame >= 0 && frame < frameCount && geometry >= 0 && geometry < geometryCount,
                "Atomic frame or geometry index is out of range.");
            return new RwClumpAtomic(frame, geometry, U32(body, 8), U32(body, 12),
                Extensions(Single(children, RwChunk.Extension), $"atomic[{index}]"));
        }

        private ReadOnlyCollection<RwClumpChunk> Extensions(RwChunkHeader chunk, string scope, uint? decodedType = null)
        {
            var result = new List<RwClumpChunk>();
            foreach (var plugin in Children(chunk))
            {
                result.Add(Opaque(plugin));
                if (plugin.Type != decodedType)
                {
                    var reason = plugin.Type switch
                    {
                        RwChunk.SkinPlugin => "Skin influences and bind transforms are not decoded.",
                        0x11E => "HAnim hierarchy and animation semantics are not decoded.",
                        _ => "Opaque plugin semantics are not decoded."
                    };
                    _diagnostics.Add(new RwClumpDiagnostic(scope, plugin.Type, reason));
                }
            }

            return result.AsReadOnly();
        }

        private RwClumpChunk Opaque(RwChunkHeader chunk) =>
            new(chunk.Type, chunk.LibraryId, chunk.PayloadOffset - RwChunk.HeaderLength,
                data.AsMemory(chunk.PayloadOffset, chunk.Size));

        private List<RwChunkHeader> Children(RwChunkHeader parent)
        {
            var result = new List<RwChunkHeader>();
            var position = parent.PayloadOffset;
            while (position < parent.End)
            {
                Require(RwChunk.TryRead(data, position, parent.End, out var child),
                    $"Malformed nested chunk at offset {position}.");
                result.Add(child);
                position = child.End;
            }

            return result;
        }

        private ReadOnlySpan<byte> Body(RwChunkHeader chunk, int? expectedSize = null)
        {
            Require(expectedSize is null || chunk.Size == expectedSize, $"Unexpected structure size at {chunk.PayloadOffset}.");
            return data.AsSpan(chunk.PayloadOffset, chunk.Size);
        }

        private static RwChunkHeader Single(List<RwChunkHeader> children, uint type)
        {
            var matches = children.Where(c => c.Type == type).ToArray();
            Require(matches.Length == 1, $"Expected exactly one child of type 0x{type:X}.");
            return matches[0];
        }

        private static void Only(List<RwChunkHeader> children, params uint[] types)
        {
            Require(children.All(child => types.Contains(child.Type)), "Unsupported core child type.");
        }

        private static void ValidateTopology(RwGeometry geometry, RwBinMesh binMesh, int materialCount)
        {
            Require(binMesh.Flags <= 1 && binMesh.TotalAgrees, "Unsupported BinMesh flags or inconsistent total.");
            var authored = new Dictionary<(uint A, uint B, uint C, uint Material), int>();
            foreach (var triangle in geometry.Triangles)
            {
                Require(triangle.V0 < geometry.Positions.Length && triangle.V1 < geometry.Positions.Length &&
                        triangle.V2 < geometry.Positions.Length && triangle.MaterialIndex < materialCount,
                    "Geometry triangle reference is out of range.");
                AddTriangle(authored, triangle.V0, triangle.V1, triangle.V2, triangle.MaterialIndex);
            }

            var drawn = new Dictionary<(uint A, uint B, uint C, uint Material), int>();
            foreach (var split in binMesh.Splits)
            {
                Require(split.MaterialIndex < materialCount && split.Indices.All(i => i < geometry.Positions.Length),
                    "BinMesh vertex or material reference is out of range.");
                var indices = split.Indices;
                if (binMesh.IsTriangleStrip)
                {
                    for (var i = 2; i < indices.Length; i++)
                    {
                        var a = indices[i - 2];
                        var b = indices[i - 1];
                        if ((i & 1) != 0)
                        {
                            (a, b) = (b, a);
                        }

                        AddTriangle(drawn, a, b, indices[i], split.MaterialIndex);
                    }
                }
                else
                {
                    Require(indices.Length % 3 == 0, "BinMesh triangle list has incomplete corners.");
                    for (var i = 0; i < indices.Length; i += 3)
                    {
                        AddTriangle(drawn, indices[i], indices[i + 1], indices[i + 2], split.MaterialIndex);
                    }
                }
            }

            Require(authored.Count == drawn.Count && authored.All(pair => drawn.GetValueOrDefault(pair.Key) == pair.Value),
                "BinMesh triangles disagree with geometry winding, material, or multiplicity.");
        }

        private static void AddTriangle(Dictionary<(uint A, uint B, uint C, uint Material), int> triangles,
            uint a, uint b, uint c, uint material)
        {
            // Both source arrays remain intact. Strip connectors and repeated-index source faces
            // have no triangle area; exclude only those from correspondence, never collinear faces.
            // All 993 retail geometries agree this way; their source lists contain 236 such faces.
            if (a == b || b == c || c == a)
            {
                return;
            }

            (uint, uint, uint, uint) key;
            if (a < b && a < c)
            {
                key = (a, b, c, material);
            }
            else if (b < c)
            {
                key = (b, c, a, material);
            }
            else
            {
                key = (c, a, b, material);
            }

            triangles[key] = triangles.GetValueOrDefault(key) + 1;
        }

        private static uint U32(ReadOnlySpan<byte> body, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(body[offset..]);
        private static int I32(ReadOnlySpan<byte> body, int offset) => BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
        private static bool Finite(float value) => float.IsFinite(value);

        private static float F32(ReadOnlySpan<byte> body, int offset)
        {
            var value = BinaryPrimitives.ReadSingleLittleEndian(body[offset..]);
            Require(Finite(value), "Nonfinite frame or material value.");
            return value;
        }

        private static InvalidDataException Invalid(string message) => new(message);

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw Invalid(message);
            }
        }
    }
}
