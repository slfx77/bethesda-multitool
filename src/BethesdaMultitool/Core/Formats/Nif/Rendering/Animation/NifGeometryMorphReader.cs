using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>Bounded modern BS34 ordinary relative morph reader; unsupported layouts remain static.</summary>
/// <remarks>
///     <para>
///         One decode path (cut-1b owner ruling D5, slice 10): the controller, the NiMorphData frame table, the
///         NiFloatInterpolator and the NiFloatData keys are read through the lossless views the model reader binds
///         (<see cref="NifGeomMorpherReader.TryReadControllerView" />, <see cref="NifGeomMorpherReader.TryReadMorphDataView" />,
///         <see cref="NifGeomMorpherReader.TryReadFloatInterpolatorView" />, <see cref="NifKeyGroupReader.TryReadDataBlockView" />);
///         this reader keeps only its own admission rules on top of them (little-endian BS 34, no next controller, flags
///         72 or 76, morpher flags 0, Always Update 0 or 1, a finite clock with stop after start, 1 to
///         <see cref="MaximumTargets" /> targets consuming the controller exactly, an NiTriShape or NiTriStrips target,
///         Relative Targets exactly 1, the morph data consuming its block exactly, named frames, finite vectors, LINEAR or
///         QUADRATIC keys strictly increasing in time, and morph 0 equal to the shape's stored positions). The morph
///         vectors are read from the bytes the frame-table view walks for extent. Every value is built from the stored
///         bits exactly as before (<see cref="BitConverter.UInt32BitsToSingle" /> of the endian-read word, which is what
///         <see cref="BinaryUtils.ReadFloat(byte[], int, bool)" /> did).
///     </para>
///     <para>
///         One documented difference from the pre-view reader: the key-group view caps a group at 1,048,576 keys (its
///         sanity cap), so an NiFloatData declaring between 1,048,577 and 2,097,152 keys, which the old reader accepted
///         within its own <see cref="MaximumKeys" /> budget, is now refused and the geometry stays static. No retail morph
///         weight curve comes near either bound.
///     </para>
/// </remarks>
internal static class NifGeometryMorphReader
{
    internal const int MaximumTargets = 256;
    internal const int MaximumVectors = 2_097_152;
    internal const int MaximumKeys = 2_097_152;

    private const int MorphDataPrefixSize = NifGeomMorpherReader.MorphDataPrefixSize;

    internal static bool TryRead(byte[] data, NifInfo nif, BlockInfo controller, out NifGeometryMorphData morph)
    {
        morph = null!;
        if (nif.BinaryVersion != 0x14020007 || nif.BsVersion != 34 || nif.IsBigEndian ||
            controller.TypeName != "NiGeomMorpherController" ||
            !NifGeomMorpherReader.TryReadControllerView(data, nif, controller, out var view)) return false;

        var header = view.Header;
        var count = (uint)view.Items.Length;
        // No manager/incremental/backwards/normal-update or chained-controller inference in this route.
        if (header.NextControllerRef != -1 || header.Flags is not (72 or 76) || view.MorpherFlags != 0 ||
            view.AlwaysUpdate > 1 || !Finite(header.Frequency) || !Finite(header.Phase) ||
            !Finite(header.StartTime) || !Finite(header.StopTime) || header.StopTime <= header.StartTime ||
            count is < 1 or > MaximumTargets || !view.ConsumedExactly ||
            !Block(nif, header.TargetRef, out var shape) || shape.TypeName is not ("NiTriShape" or "NiTriStrips") ||
            !Block(nif, view.DataRef, out var morphBlock) ||
            !NifGeomMorpherReader.TryReadMorphDataView(data, nif, morphBlock, out var frames)) return false;

        var vertices = frames.NumVertices;
        if (frames.NumMorphs != count || vertices == 0 || vertices > MaximumVectors / count ||
            frames.RelativeTargets != 1 || !frames.ConsumedExactly) return false;

        var m = morphBlock.DataOffset;
        var targets = new NifGeometryMorphTarget[(int)count];
        var totalKeys = 0;
        for (var target = 0; target < targets.Length; target++)
        {
            var offset = checked(m + MorphDataPrefixSize + target * (4 + 12 * (int)vertices));
            var nameRef = frames.FrameNameIndices[target];
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

            var item = view.Items[target];
            var fallback = BitConverter.UInt32BitsToSingle(item.WeightBits);
            if (!Finite(fallback) ||
                !TryReadCurve(data, nif, item.InterpolatorRef, fallback, ref totalKeys, out var curve)) return false;
            targets[target] = new NifGeometryMorphTarget(nif.Strings[nameRef], positions, curve);
        }

        morph = new NifGeometryMorphData(header.TargetRef, header.Frequency, header.Phase, header.StartTime,
            header.StopTime, header.Flags == 72, targets);
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
        if (!Block(nif, interpolatorRef, out var interpolator) ||
            !NifGeomMorpherReader.TryReadFloatInterpolatorView(data, nif, interpolator, out var view)) return false;
        var defaultValue = BitConverter.UInt32BitsToSingle(view.ValueBits);
        if (view.DataRef == -1)
        {
            // Invalid interpolator defaults report failure, leaving the current serialized weight.
            if (Finite(defaultValue)) curve = curve with { FallbackWeight = defaultValue };
            return true;
        }

        if (!Block(nif, view.DataRef, out var values) || values.TypeName != "NiFloatData" ||
            !NifKeyGroupReader.TryReadDataBlockView(data, nif, values, out var keysView) ||
            keysView.EndOffset != values.DataOffset + values.Size) return false;
        if (keysView.Count == 0)
        {
            if (Finite(defaultValue)) curve = curve with { FallbackWeight = defaultValue };
            return true;
        }

        if (keysView.Count > MaximumKeys - totalKeys) return false;
        if (keysView.KeyType is not ((uint)NifKeyInterpolation.Linear or (uint)NifKeyInterpolation.Quadratic))
            return false;
        var quadratic = keysView.KeyType == (uint)NifKeyInterpolation.Quadratic;
        var keys = new NifMorphScalarKey[keysView.Count];
        for (var index = 0; index < keys.Length; index++)
        {
            var key = new NifMorphScalarKey(keysView.Time(index), keysView.Value(index, 0),
                quadratic ? keysView.Forward(index, 0) : 0f,
                quadratic ? keysView.Backward(index, 0) : 0f);
            if (!Finite(key.Time) || !Finite(key.Value) || !Finite(key.InTangent) || !Finite(key.OutTangent) ||
                (index > 0 && key.Time <= keys[index - 1].Time)) return false;
            keys[index] = key;
        }

        totalKeys += keys.Length;
        curve = new NifMorphScalarCurve((NifKeyInterpolation)keysView.KeyType, fallback, keys);
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
