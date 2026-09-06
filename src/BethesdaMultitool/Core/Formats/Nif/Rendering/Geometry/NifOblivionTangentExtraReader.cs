using System.Buffers.Binary;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;

/// <summary>Recovers the authored tangent basis attached to a supported PC Oblivion shape.</summary>
internal static class NifOblivionTangentExtraReader
{
    // Format/axis oracle: TestOutput/current-goal/06-oblivion-facgen/attachments-and-tails-20260905/
    // classic-tangent-extra-oracle.md. This is an independent implementation, not copied reader code.
    // The opaque payload's Xbox byte order and other classic exporters are not established here.
    internal static (float[] Tangents, float[] Bitangents)? Read(
        byte[] data,
        NifInfo? nif,
        int shapeIndex,
        int vertexCount,
        float[]? normals)
    {
        if (nif is null || !IsSupported(nif) || shapeIndex < 0 || shapeIndex >= nif.Blocks.Count ||
            vertexCount is <= 0 or > ushort.MaxValue || normals is null || normals.Length != vertexCount * 3)
        {
            return null;
        }

        var shape = nif.Blocks[shapeIndex];
        if (shape.TypeName is not ("NiTriShape" or "NiTriStrips") ||
            !TryGetBlock(data, shape, out var shapeData) ||
            !TryGetExtraLinks(shapeData, out var links))
        {
            return null;
        }

        var payload = FindPayload(data, nif, links);
        if (payload.Length != vertexCount * 24)
        {
            return null;
        }

        var tangents = new float[vertexCount * 3];
        var bitangents = new float[vertexCount * 3];
        var secondArrayOffset = vertexCount * 12;
        for (var i = 0; i < vertexCount; i++)
        {
            // Storage is all +V vectors, then all +U vectors. The native shader expects X=T, Y=B.
            var bitangent = ReadVector(payload, i * 12);
            var tangent = ReadVector(payload, secondArrayOffset + i * 12);
            var normal = new Vector3(normals[i * 3], normals[i * 3 + 1], normals[i * 3 + 2]);
            if (!IsUsableBasis(normal, tangent, bitangent))
            {
                return null;
            }

            StoreVector(tangents, i * 3, tangent);
            StoreVector(bitangents, i * 3, bitangent);
        }

        // Keep both authored vectors, including mirrored handedness; do not rebuild B=cross(N,T).
        return (tangents, bitangents);
    }

    private static bool IsSupported(NifInfo nif)
    {
        if (nif.IsBigEndian || !nif.HasInlineStrings ||
            nif.UserVersion != 11 || nif.BsVersion != 11)
        {
            return false;
        }

        return nif.BinaryVersion switch
        {
            0x14000004 => nif.HeaderString == "Gamebryo File Format, Version 20.0.0.4",
            0x14000005 => nif.HeaderString == "Gamebryo File Format, Version 20.0.0.5",
            _ => false
        };
    }

    private static bool TryGetBlock(byte[] data, BlockInfo block, out ReadOnlySpan<byte> bytes)
    {
        bytes = default;
        if (block.DataOffset < 0 || block.Size < 0 || block.Size > data.Length ||
            block.DataOffset > data.Length - block.Size)
        {
            return false;
        }

        bytes = data.AsSpan(block.DataOffset, block.Size);
        return true;
    }

    private static bool TryGetExtraLinks(ReadOnlySpan<byte> shape, out ReadOnlySpan<byte> links)
    {
        links = default;
        if (shape.Length < 8)
        {
            return false;
        }

        var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(shape);
        if (nameLength > shape.Length - 8)
        {
            return false;
        }

        var afterName = shape[((int)nameLength + 4)..];
        var count = BinaryPrimitives.ReadUInt32LittleEndian(afterName);
        if (count > (afterName.Length - 4) / 4)
        {
            return false;
        }

        links = afterName.Slice(4, (int)count * 4);
        return true;
    }

    private static ReadOnlySpan<byte> FindPayload(byte[] data, NifInfo nif, ReadOnlySpan<byte> links)
    {
        ReadOnlySpan<byte> result = default;
        var found = false;
        for (var offset = 0; offset < links.Length; offset += 4)
        {
            var index = BinaryPrimitives.ReadInt32LittleEndian(links[offset..]);
            if (!TryReadAttachment(data, nif, index, out var payload, out var matches))
            {
                return default;
            }

            if (!matches)
            {
                continue;
            }

            if (found)
            {
                return default; // Multiple matching attachments have no established precedence.
            }

            found = true;
            result = payload;
        }

        return result;
    }

    private static bool TryReadAttachment(
        byte[] data,
        NifInfo nif,
        int index,
        out ReadOnlySpan<byte> payload,
        out bool matches)
    {
        payload = default;
        matches = false;
        if (index < 0 || index >= nif.Blocks.Count)
        {
            return false;
        }

        var extra = nif.Blocks[index];
        if (extra.TypeName != "NiBinaryExtraData")
        {
            return true;
        }

        return TryGetBlock(data, extra, out var extraData) &&
               TryGetNamedPayload(extraData, out payload, out matches);
    }

    private static bool TryGetNamedPayload(
        ReadOnlySpan<byte> extra,
        out ReadOnlySpan<byte> payload,
        out bool matches)
    {
        payload = default;
        matches = false;
        if (extra.Length < 4)
        {
            return false;
        }

        var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(extra);
        if (nameLength > extra.Length - 4)
        {
            return false;
        }

        matches = extra.Slice(4, (int)nameLength)
            .SequenceEqual("Tangent space (binormal & tangent vectors)"u8);
        if (!matches)
        {
            return true;
        }

        var afterName = extra[((int)nameLength + 4)..];
        if (afterName.Length < 4 ||
            BinaryPrimitives.ReadUInt32LittleEndian(afterName) != afterName.Length - 4)
        {
            return false;
        }

        payload = afterName[4..];
        return true;
    }

    private static Vector3 ReadVector(ReadOnlySpan<byte> data, int offset)
    {
        return new Vector3(
            BinaryPrimitives.ReadSingleLittleEndian(data[offset..]),
            BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 4)..]),
            BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 8)..]));
    }

    private static bool IsUsableBasis(Vector3 normal, Vector3 tangent, Vector3 bitangent)
    {
        // Stock iron greaves shape 18 has four finite, nonzero, nearly opposite T/B pairs.
        // The authored basis need not be orthogonal: the shader multiplies its rows directly,
        // never inverts the matrix. A determinant gate would drop the entire valid payload.
        return IsUsableVector(normal) && IsUsableVector(tangent) && IsUsableVector(bitangent);
    }

    private static bool IsUsableVector(Vector3 vector)
    {
        var lengthSquared = vector.LengthSquared();
        return float.IsFinite(lengthSquared) && lengthSquared > 1e-12f;
    }

    private static void StoreVector(float[] target, int offset, Vector3 vector)
    {
        target[offset] = vector.X;
        target[offset + 1] = vector.Y;
        target[offset + 2] = vector.Z;
    }
}
