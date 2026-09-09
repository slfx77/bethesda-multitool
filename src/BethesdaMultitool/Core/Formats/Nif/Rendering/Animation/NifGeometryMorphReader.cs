using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>Bounded modern BS34 ordinary relative morph reader; unsupported layouts remain static.</summary>
internal static class NifGeometryMorphReader
{
    internal const int MaximumTargets = 256;
    internal const int MaximumVectors = 2_097_152;
    internal const int MaximumKeys = 2_097_152;

    internal static bool TryRead(byte[] data, NifInfo nif, BlockInfo controller, out NifGeometryMorphData morph)
    {
        morph = null!;
        if (nif.BinaryVersion != 0x14020007 || nif.BsVersion != 34 || nif.IsBigEndian ||
            controller.TypeName != "NiGeomMorpherController" || !Readable(data, controller, 37)) return false;

        var p = controller.DataOffset;
        var next = BinaryUtils.ReadInt32(data, p, false);
        var flags = BinaryUtils.ReadUInt16(data, p + 4, false);
        var frequency = BinaryUtils.ReadFloat(data, p + 6, false);
        var phase = BinaryUtils.ReadFloat(data, p + 10, false);
        var start = BinaryUtils.ReadFloat(data, p + 14, false);
        var stop = BinaryUtils.ReadFloat(data, p + 18, false);
        var shapeRef = BinaryUtils.ReadInt32(data, p + 22, false);
        var morpherFlags = BinaryUtils.ReadUInt16(data, p + 26, false);
        var dataRef = BinaryUtils.ReadInt32(data, p + 28, false);
        var count = BinaryUtils.ReadUInt32(data, p + 33, false);
        // No manager/incremental/backwards/normal-update or chained-controller inference in this route.
        if (next != -1 || flags is not (72 or 76) || morpherFlags != 0 || data[p + 32] > 1 ||
            !Finite(frequency) || !Finite(phase) || !Finite(start) || !Finite(stop) || stop <= start ||
            count is < 1 or > MaximumTargets || controller.Size != 37L + 8L * count ||
            !Block(nif, shapeRef, out var shape) || shape.TypeName is not ("NiTriShape" or "NiTriStrips") ||
            !Block(nif, dataRef, out var morphBlock) || morphBlock.TypeName != "NiMorphData" ||
            !Readable(data, morphBlock, 9)) return false;

        var m = morphBlock.DataOffset;
        var morphCount = BinaryUtils.ReadUInt32(data, m, false);
        var vertices = BinaryUtils.ReadUInt32(data, m + 4, false);
        if (morphCount != count || vertices == 0 || vertices > MaximumVectors / count || data[m + 8] != 1 ||
            morphBlock.Size != 9L + count * (4L + 12L * vertices)) return false;

        var targets = new NifGeometryMorphTarget[(int)count];
        var totalKeys = 0;
        for (var target = 0; target < targets.Length; target++)
        {
            var offset = checked(m + 9 + target * (4 + 12 * (int)vertices));
            var nameRef = BinaryUtils.ReadInt32(data, offset, false);
            if ((uint)nameRef >= (uint)nif.Strings.Count || string.IsNullOrWhiteSpace(nif.Strings[nameRef]))
                return false;
            var positions = new Vector3[(int)vertices];
            for (var vertex = 0; vertex < positions.Length; vertex++)
            {
                var v = offset + 4 + vertex * 12;
                positions[vertex] = new Vector3(BinaryUtils.ReadFloat(data, v, false),
                    BinaryUtils.ReadFloat(data, v + 4, false), BinaryUtils.ReadFloat(data, v + 8, false));
                if (!Finite(positions[vertex])) return false;
            }

            var item = p + 37 + target * 8;
            var interpolatorRef = BinaryUtils.ReadInt32(data, item, false);
            var fallback = BinaryUtils.ReadFloat(data, item + 4, false);
            if (!Finite(fallback) ||
                !TryReadCurve(data, nif, interpolatorRef, fallback, ref totalKeys, out var curve)) return false;
            targets[target] = new NifGeometryMorphTarget(nif.Strings[nameRef], positions, curve);
        }

        morph = new NifGeometryMorphData(shapeRef, frequency, phase, start, stop, flags == 72, targets);
        if (!ValidateShape(data, nif, shape, controller.Index, morph)) return false;
        var bounds = NifGeometryMorphEvaluator.GetConservativeBounds(morph);
        return Finite(bounds.Minimum) && Finite(bounds.Maximum);
    }

    private static bool ValidateShape(byte[] data, NifInfo nif, BlockInfo shape, int controllerIndex,
        NifGeometryMorphData morph)
    {
        if (!Readable(data, shape, 12)) return false;
        var end = (long)shape.DataOffset + shape.Size;
        var extras = BinaryUtils.ReadUInt32(data, shape.DataOffset + 4, false);
        var controllerOffset = shape.DataOffset + 8L + 4L * extras;
        if (controllerOffset + 64L > end) return false;
        var p = (int)controllerOffset;
        if (BinaryUtils.ReadInt32(data, p, false) != controllerIndex) return false;
        p += 60; // controller, BS34 flags, local SRT
        var properties = BinaryUtils.ReadUInt32(data, p, false);
        var geometryOffset = p + 8L + 4L * properties; // property count/list, collision reference
        if (geometryOffset + 8L > end) return false;
        p = (int)geometryOffset;
        var geometryRef = BinaryUtils.ReadInt32(data, p, false);
        if (BinaryUtils.ReadInt32(data, p + 4, false) != -1 || !Block(nif, geometryRef, out var geometry) ||
            geometry.TypeName is not ("NiTriShapeData" or "NiTriStripsData") ||
            !Readable(data, geometry, 9)) return false;
        var vertexCount = BinaryUtils.ReadUInt16(data, geometry.DataOffset + 4, false);
        if (vertexCount != morph.VertexCount || data[geometry.DataOffset + 8] != 1 ||
            geometry.Size < 9L + 12L * vertexCount) return false;
        for (var vertex = 0; vertex < vertexCount; vertex++)
        {
            var v = geometry.DataOffset + 9 + 12 * vertex;
            var source = new Vector3(BinaryUtils.ReadFloat(data, v, false),
                BinaryUtils.ReadFloat(data, v + 4, false), BinaryUtils.ReadFloat(data, v + 8, false));
            if (source != morph.Targets[0].Positions[vertex]) return false;
        }

        return true;
    }

    private static bool TryReadCurve(byte[] data, NifInfo nif, int interpolatorRef, float fallback,
        ref int totalKeys, out NifMorphScalarCurve curve)
    {
        curve = new NifMorphScalarCurve(NifKeyInterpolation.Linear, fallback, []);
        if (interpolatorRef == -1) return true;
        if (!Block(nif, interpolatorRef, out var interpolator) || interpolator.TypeName != "NiFloatInterpolator" ||
            !Readable(data, interpolator, 8) || interpolator.Size != 8) return false;
        var defaultValue = BinaryUtils.ReadFloat(data, interpolator.DataOffset, false);
        var dataRef = BinaryUtils.ReadInt32(data, interpolator.DataOffset + 4, false);
        if (dataRef == -1)
        {
            // Invalid interpolator defaults report failure, leaving the current serialized weight.
            if (Finite(defaultValue)) curve = curve with { FallbackWeight = defaultValue };
            return true;
        }

        if (!Block(nif, dataRef, out var values) || values.TypeName != "NiFloatData" ||
            !Readable(data, values, 4)) return false;
        var count = BinaryUtils.ReadUInt32(data, values.DataOffset, false);
        if (count == 0)
        {
            if (values.Size != 4) return false;
            if (Finite(defaultValue)) curve = curve with { FallbackWeight = defaultValue };
            return true;
        }

        if (count > MaximumKeys - totalKeys || !Readable(data, values, 8)) return false;
        var basis = BinaryUtils.ReadUInt32(data, values.DataOffset + 4, false);
        if (basis is not (1 or 2)) return false;
        var stride = basis == 1 ? 8 : 16;
        if (values.Size != 8L + count * stride) return false;
        var keys = new NifMorphScalarKey[(int)count];
        for (var index = 0; index < keys.Length; index++)
        {
            var k = values.DataOffset + 8 + index * stride;
            var key = new NifMorphScalarKey(BinaryUtils.ReadFloat(data, k, false),
                BinaryUtils.ReadFloat(data, k + 4, false),
                basis == 2 ? BinaryUtils.ReadFloat(data, k + 8, false) : 0f,
                basis == 2 ? BinaryUtils.ReadFloat(data, k + 12, false) : 0f);
            if (!Finite(key.Time) || !Finite(key.Value) || !Finite(key.InTangent) || !Finite(key.OutTangent) ||
                (index > 0 && key.Time <= keys[index - 1].Time)) return false;
            keys[index] = key;
        }

        totalKeys += keys.Length;
        curve = new NifMorphScalarCurve((NifKeyInterpolation)basis, fallback, keys);
        return true;
    }

    private static bool Block(NifInfo nif, int index, out BlockInfo block)
    {
        block = null!;
        if ((uint)index >= (uint)nif.Blocks.Count || nif.Blocks[index].Index != index) return false;
        block = nif.Blocks[index];
        return true;
    }

    private static bool Readable(byte[] data, BlockInfo block, int minimum)
    {
        return block.DataOffset >= 0 && block.Size >= minimum && (long)block.DataOffset + block.Size <= data.Length;
    }

    internal static bool Finite(float value)
    {
        return float.IsFinite(value) && MathF.Abs(value) < 1e20f;
    }

    internal static bool Finite(Vector3 value)
    {
        return Finite(value.X) && Finite(value.Y) && Finite(value.Z);
    }
}
