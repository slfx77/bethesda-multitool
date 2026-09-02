using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Nif.Materials;

internal sealed partial class StarfieldMaterialDatabase
{
    private const uint ChunkUser = 0x52455355; // 'USER'
    private const uint ChunkUserDiff = 0x44525355; // 'USRD'

    private readonly Dictionary<string, ControllerClassDefinition> _controllerClassDefinitions =
        new(StringComparer.Ordinal);

    private readonly Dictionary<uint, Dictionary<int, StarfieldMaterialUvAnimationPolicy>>
        _uvAnimationsByObject = [];

    /// <summary>
    ///     Resolves the authored looping UV-offset controller for the projected base layer. CE2
    ///     effect materials may animate several layers independently; the current native mesh
    ///     renderer projects only layer zero, so this policy deliberately returns only that layer's
    ///     exactly representable constant-rate loop.
    /// </summary>
    internal StarfieldMaterialUvAnimationPolicy ResolveBaseLayerUvAnimation(string materialPath)
    {
        return ResolveLayerUvAnimation(materialPath, 0);
    }

    /// <summary>Resolves one zero-based CE2 material layer's reducible UV-offset animation.</summary>
    internal StarfieldMaterialUvAnimationPolicy ResolveLayerUvAnimation(
        string materialPath,
        int layerIndex)
    {
        if (layerIndex < 0 || !TryResolveRoot(materialPath, out var root))
        {
            return default;
        }

        var visited = new HashSet<uint>();
        for (var current = root; current != 0 && visited.Add(current);)
        {
            if (_uvAnimationsByObject.TryGetValue(current, out var animations) &&
                animations.TryGetValue(layerIndex, out var animation))
            {
                return animation;
            }

            current = GetEffectiveBaseObject(current);
        }

        return default;
    }

    private void CaptureControllerClassDefinition(
        ReadOnlySpan<byte> body,
        string className,
        IReadOnlyDictionary<uint, string> strings)
    {
        if (className.Length == 0 || body.Length < 12)
        {
            return;
        }

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(body[8..]);
        var declaredCount = BinaryPrimitives.ReadUInt16LittleEndian(body[10..]);
        var fields = new List<ControllerFieldDefinition>(declaredCount);
        var pos = 12;
        while (fields.Count < declaredCount && pos + 12 <= body.Length)
        {
            var fieldNameReference = BinaryPrimitives.ReadUInt32LittleEndian(body[pos..]);
            var fieldTypeReference = BinaryPrimitives.ReadUInt32LittleEndian(body[(pos + 4)..]);
            pos += 12;
            if (!strings.TryGetValue(fieldNameReference, out var fieldName) ||
                !TryResolveControllerType(fieldTypeReference, strings, out var fieldType))
            {
                return;
            }

            fields.Add(new ControllerFieldDefinition(fieldName, fieldType));
        }

        if (fields.Count == declaredCount)
        {
            _controllerClassDefinitions[className] =
                new ControllerClassDefinition((flags & 4) != 0, [.. fields]);
        }
    }

    private void ReadControllerComponent(
        ReadOnlySpan<byte> body,
        uint owner,
        bool isDiff,
        ReadOnlySpan<byte> data,
        ref int streamPosition,
        ref uint chunksRemaining,
        IReadOnlyDictionary<uint, string> strings)
    {
        var bodyPosition = 4; // component class reference
        if (!TryReadControllerClass(
                "BSBind::ControllerComponent",
                body,
                ref bodyPosition,
                isDiff,
                data,
                ref streamPosition,
                ref chunksRemaining,
                strings,
                out var component) ||
            !TryGetField(component, "upControllers", out var controllers) ||
            !TryGetField(controllers, "MappingsA", out var mappings) ||
            mappings.Items is not { } items)
        {
            return;
        }

        Dictionary<int, StarfieldMaterialUvAnimationPolicy>? resolved = null;
        foreach (var mapping in items)
        {
            if (!TryReadUvOffsetLayer(mapping, out var layerIndex) ||
                !TryGetField(mapping, "Controller", out var controller) ||
                !string.Equals(
                    controller.ClassName,
                    "BSBind::Float2DCurveController",
                    StringComparison.Ordinal) ||
                !TryResolveLinearUvLoop(controller, out var policy))
            {
                continue;
            }

            resolved ??= [];
            resolved[layerIndex] = policy;
        }

        if (resolved is not null)
        {
            _uvAnimationsByObject[owner] = resolved;
        }
    }

    private static bool TryReadUvOffsetLayer(ControllerValue mapping, out int layerIndex)
    {
        layerIndex = -1;
        if (!TryGetField(mapping, "Address", out var address) ||
            !TryGetField(address, "Path", out var path) ||
            path.Items is not { Count: 1 } pathItems ||
            pathItems[0].Scalar is not string target ||
            !target.StartsWith("UVOffset", StringComparison.Ordinal) ||
            !int.TryParse(target.AsSpan("UVOffset".Length), out var oneBasedLayer) ||
            oneBasedLayer is < 1 or > 8)
        {
            return false;
        }

        layerIndex = oneBasedLayer - 1;
        return true;
    }

    private static bool TryResolveLinearUvLoop(
        ControllerValue controller,
        out StarfieldMaterialUvAnimationPolicy policy)
    {
        policy = default;
        if (!TryGetField(controller, "Loop", out var loopNode) ||
            loopNode.Scalar is not bool loop ||
            !loop ||
            !TryGetField(controller, "Mask", out var maskNode) ||
            maskNode.Scalar is not string mask ||
            !mask.Contains('X', StringComparison.OrdinalIgnoreCase) ||
            !mask.Contains('Y', StringComparison.OrdinalIgnoreCase) ||
            !TryGetField(controller, "Curve", out var curve) ||
            !TryGetField(curve, "XCurve", out var xCurve) ||
            !TryGetField(curve, "YCurve", out var yCurve) ||
            !TryResolveLinearLoopAxis(xCurve, out var xInitial, out var xVelocity, out var xPeriod) ||
            !TryResolveLinearLoopAxis(yCurve, out var yInitial, out var yVelocity, out var yPeriod) ||
            !NearlyEqual(xPeriod, yPeriod) ||
            !float.IsFinite(xVelocity) ||
            !float.IsFinite(yVelocity) ||
            xPeriod <= 0f ||
            !WrapsAtPeriod(xVelocity, xPeriod) ||
            !WrapsAtPeriod(yVelocity, yPeriod))
        {
            return false;
        }

        policy = new StarfieldMaterialUvAnimationPolicy(
            true,
            new Vector2(xInitial, yInitial),
            new Vector2(xVelocity, yVelocity),
            xPeriod);
        return true;
    }

    private static bool TryResolveLinearLoopAxis(
        ControllerValue curve,
        out float initial,
        out float velocity,
        out float period)
    {
        initial = 0f;
        velocity = 0f;
        period = 0f;
        if (!TryGetFloat(curve, "InputDistance", out period) ||
            !float.IsFinite(period) ||
            period <= 0f ||
            !TryGetField(curve, "Type", out var typeNode) ||
            typeNode.Scalar is not string interpolation ||
            !interpolation.Equals("Linear", StringComparison.OrdinalIgnoreCase) ||
            !TryGetField(curve, "Controls", out var controlsNode) ||
            controlsNode.Items is not { Count: > 0 } controls)
        {
            return false;
        }

        var points = new List<(float Input, float Value)>(controls.Count);
        foreach (var control in controls)
        {
            if (!TryGetFloat(control, "Input", out var input) ||
                !TryGetFloat(control, "Value", out var value) ||
                !float.IsFinite(input) ||
                !float.IsFinite(value))
            {
                return false;
            }

            points.Add((input, value));
        }

        points.Sort(static (left, right) => left.Input.CompareTo(right.Input));
        if (points.Count == 1)
        {
            initial = points[0].Value;
            return true;
        }

        if (points.Count != 2 ||
            !NearlyEqual(points[0].Input, 0f) ||
            !NearlyEqual(points[1].Input, period))
        {
            return false;
        }

        initial = points[0].Value;
        velocity = (points[1].Value - points[0].Value) / period;
        return true;
    }

    private static bool NearlyEqual(float left, float right)
    {
        return MathF.Abs(left - right) <= 1e-4f * MathF.Max(1f, MathF.Max(MathF.Abs(left), MathF.Abs(right)));
    }

    private static bool WrapsAtPeriod(float velocity, float period)
    {
        var displacement = velocity * period;
        return float.IsFinite(displacement) && NearlyEqual(displacement, MathF.Round(displacement));
    }

    private static bool TryGetField(
        ControllerValue value,
        string name,
        out ControllerValue field)
    {
        if (value.Fields is not null && value.Fields.TryGetValue(name, out var resolved))
        {
            field = resolved;
            return true;
        }

        field = null!;
        return false;
    }

    private static bool TryGetFloat(ControllerValue value, string name, out float result)
    {
        result = 0f;
        if (!TryGetField(value, name, out var field) || field.Scalar is not float number)
        {
            return false;
        }

        result = number;
        return true;
    }

    private bool TryReadControllerClass(
        string className,
        ReadOnlySpan<byte> body,
        ref int bodyPosition,
        bool isDiff,
        ReadOnlySpan<byte> data,
        ref int streamPosition,
        ref uint chunksRemaining,
        IReadOnlyDictionary<uint, string> strings,
        out ControllerValue value)
    {
        value = null!;
        if (!_controllerClassDefinitions.TryGetValue(className, out var definition))
        {
            return false;
        }

        if (definition.IsUser)
        {
            if (!TryReadNestedChunk(
                    data,
                    ref streamPosition,
                    ref chunksRemaining,
                    out var chunkType,
                    out var userBody) ||
                chunkType is not (ChunkUser or ChunkUserDiff) ||
                userBody.Length < 8 ||
                !TryResolveControllerType(
                    BinaryPrimitives.ReadUInt32LittleEndian(userBody),
                    strings,
                    out var declaredType) ||
                declaredType.ClassName != className ||
                !TryResolveControllerType(
                    BinaryPrimitives.ReadUInt32LittleEndian(userBody[4..]),
                    strings,
                    out var convertedType) ||
                convertedType.ClassName != className)
            {
                return false;
            }

            var userPosition = 8;
            return TryReadControllerFields(
                className,
                definition,
                userBody,
                ref userPosition,
                chunkType == ChunkUserDiff,
                data,
                ref streamPosition,
                ref chunksRemaining,
                strings,
                out value);
        }

        return TryReadControllerFields(
            className,
            definition,
            body,
            ref bodyPosition,
            isDiff,
            data,
            ref streamPosition,
            ref chunksRemaining,
            strings,
            out value);
    }

    private bool TryReadControllerFields(
        string className,
        ControllerClassDefinition definition,
        ReadOnlySpan<byte> body,
        ref int bodyPosition,
        bool isDiff,
        ReadOnlySpan<byte> data,
        ref int streamPosition,
        ref uint chunksRemaining,
        IReadOnlyDictionary<uint, string> strings,
        out ControllerValue value)
    {
        value = new ControllerValue { ClassName = className, Fields = new(StringComparer.Ordinal) };
        if (!isDiff)
        {
            foreach (var field in definition.Fields)
            {
                if (!TryReadControllerValue(
                        field.Type,
                        body,
                        ref bodyPosition,
                        false,
                        data,
                        ref streamPosition,
                        ref chunksRemaining,
                        strings,
                        out var fieldValue))
                {
                    return false;
                }

                value.Fields[field.Name] = fieldValue;
            }

            return true;
        }

        while (bodyPosition + 2 <= body.Length)
        {
            var fieldIndex = BinaryPrimitives.ReadUInt16LittleEndian(body[bodyPosition..]);
            bodyPosition += 2;
            if (fieldIndex == ushort.MaxValue)
            {
                return true;
            }

            if (fieldIndex >= definition.Fields.Length)
            {
                return false;
            }

            var field = definition.Fields[fieldIndex];
            if (!TryReadControllerValue(
                    field.Type,
                    body,
                    ref bodyPosition,
                    true,
                    data,
                    ref streamPosition,
                    ref chunksRemaining,
                    strings,
                    out var fieldValue))
            {
                return false;
            }

            value.Fields[field.Name] = fieldValue;
        }

        return false;
    }

    private bool TryReadControllerValue(
        ControllerValueType type,
        ReadOnlySpan<byte> body,
        ref int bodyPosition,
        bool isDiff,
        ReadOnlySpan<byte> data,
        ref int streamPosition,
        ref uint chunksRemaining,
        IReadOnlyDictionary<uint, string> strings,
        out ControllerValue value)
    {
        value = new ControllerValue();
        switch (type.Kind)
        {
            case ControllerValueKind.String:
                if (!TryReadControllerString(body, ref bodyPosition, out var text)) return false;
                value.Scalar = text;
                return true;
            case ControllerValueKind.Bool:
                if (bodyPosition >= body.Length) return false;
                value.Scalar = body[bodyPosition++] != 0;
                return true;
            case ControllerValueKind.Float:
                if (bodyPosition + 4 > body.Length) return false;
                value.Scalar = BitConverter.ToSingle(body.Slice(bodyPosition, 4));
                bodyPosition += 4;
                return true;
            case ControllerValueKind.Int8:
                if (bodyPosition >= body.Length) return false;
                value.Scalar = unchecked((sbyte)body[bodyPosition++]);
                return true;
            case ControllerValueKind.UInt8:
                if (bodyPosition >= body.Length) return false;
                value.Scalar = body[bodyPosition++];
                return true;
            case ControllerValueKind.Int16:
            case ControllerValueKind.UInt16:
                if (bodyPosition + 2 > body.Length) return false;
                value.Scalar = BinaryPrimitives.ReadUInt16LittleEndian(body[bodyPosition..]);
                bodyPosition += 2;
                return true;
            case ControllerValueKind.Int32:
            case ControllerValueKind.UInt32:
                if (bodyPosition + 4 > body.Length) return false;
                value.Scalar = BinaryPrimitives.ReadUInt32LittleEndian(body[bodyPosition..]);
                bodyPosition += 4;
                return true;
            case ControllerValueKind.Int64:
            case ControllerValueKind.UInt64:
            case ControllerValueKind.Double:
                if (bodyPosition + 8 > body.Length) return false;
                value.Scalar = BinaryPrimitives.ReadUInt64LittleEndian(body[bodyPosition..]);
                bodyPosition += 8;
                return true;
            case ControllerValueKind.Ref:
                if (bodyPosition + 4 > body.Length ||
                    !TryResolveControllerType(
                        BinaryPrimitives.ReadUInt32LittleEndian(body[bodyPosition..]),
                        strings,
                        out var referencedType))
                {
                    return false;
                }

                bodyPosition += 4;
                return TryReadControllerValue(
                    referencedType,
                    body,
                    ref bodyPosition,
                    isDiff,
                    data,
                    ref streamPosition,
                    ref chunksRemaining,
                    strings,
                    out value);
            case ControllerValueKind.List:
                if (!TryReadNestedChunk(
                        data,
                        ref streamPosition,
                        ref chunksRemaining,
                        out var listChunkType,
                        out var listBody) ||
                    listChunkType != ChunkList ||
                    listBody.Length < 8 ||
                    !TryResolveControllerType(
                        BinaryPrimitives.ReadUInt32LittleEndian(listBody),
                        strings,
                        out var elementType))
                {
                    return false;
                }

                var count = BinaryPrimitives.ReadUInt32LittleEndian(listBody[4..]);
                if (count > 65_536)
                {
                    return false;
                }

                var listPosition = 8;
                var items = new List<ControllerValue>((int)count);
                for (var index = 0u; index < count; index++)
                {
                    if (!TryReadControllerValue(
                            elementType,
                            listBody,
                            ref listPosition,
                            isDiff,
                            data,
                            ref streamPosition,
                            ref chunksRemaining,
                            strings,
                            out var item))
                    {
                        return false;
                    }

                    items.Add(item);
                }

                value.Items = items;
                return true;
            case ControllerValueKind.Class when type.ClassName is { } nestedClass:
                return TryReadControllerClass(
                    nestedClass,
                    body,
                    ref bodyPosition,
                    isDiff,
                    data,
                    ref streamPosition,
                    ref chunksRemaining,
                    strings,
                    out value);
            default:
                return false;
        }
    }

    private static bool TryReadNestedChunk(
        ReadOnlySpan<byte> data,
        ref int streamPosition,
        ref uint chunksRemaining,
        out uint type,
        out ReadOnlySpan<byte> body)
    {
        type = 0;
        body = default;
        if (chunksRemaining == 0 || !TryReadChunk(data, ref streamPosition, out type, out body))
        {
            return false;
        }

        chunksRemaining--;
        return true;
    }

    private static bool TryReadControllerString(
        ReadOnlySpan<byte> body,
        ref int position,
        out string value)
    {
        value = string.Empty;
        if (position + 2 > body.Length)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(body[position..]);
        position += 2;
        if (position + length > body.Length)
        {
            return false;
        }

        value = Encoding.ASCII.GetString(body.Slice(position, length)).TrimEnd('\0');
        position += length;
        return true;
    }

    private static bool TryResolveControllerType(
        uint reference,
        IReadOnlyDictionary<uint, string> strings,
        out ControllerValueType type)
    {
        if (strings.TryGetValue(reference, out var className))
        {
            type = new ControllerValueType(ControllerValueKind.Class, className);
            return true;
        }

        var builtIn = reference - 0xFFFFFF01u;
        type = builtIn switch
        {
            0 => new ControllerValueType(ControllerValueKind.None, null),
            1 => new ControllerValueType(ControllerValueKind.String, null),
            2 => new ControllerValueType(ControllerValueKind.List, null),
            3 => new ControllerValueType(ControllerValueKind.Map, null),
            4 => new ControllerValueType(ControllerValueKind.Ref, null),
            7 => new ControllerValueType(ControllerValueKind.Int8, null),
            8 => new ControllerValueType(ControllerValueKind.UInt8, null),
            9 => new ControllerValueType(ControllerValueKind.Int16, null),
            10 => new ControllerValueType(ControllerValueKind.UInt16, null),
            11 => new ControllerValueType(ControllerValueKind.Int32, null),
            12 => new ControllerValueType(ControllerValueKind.UInt32, null),
            13 => new ControllerValueType(ControllerValueKind.Int64, null),
            14 => new ControllerValueType(ControllerValueKind.UInt64, null),
            15 => new ControllerValueType(ControllerValueKind.Bool, null),
            16 => new ControllerValueType(ControllerValueKind.Float, null),
            17 => new ControllerValueType(ControllerValueKind.Double, null),
            _ => default
        };
        return type.Kind != ControllerValueKind.Unknown;
    }

    private sealed class ControllerValue
    {
        internal string? ClassName { get; init; }
        internal object? Scalar { get; set; }
        internal Dictionary<string, ControllerValue>? Fields { get; init; }
        internal List<ControllerValue>? Items { get; set; }
    }

    private readonly record struct ControllerFieldDefinition(
        string Name,
        ControllerValueType Type);

    private sealed record ControllerClassDefinition(
        bool IsUser,
        ControllerFieldDefinition[] Fields);

    private readonly record struct ControllerValueType(
        ControllerValueKind Kind,
        string? ClassName);

    private enum ControllerValueKind : byte
    {
        Unknown,
        None,
        String,
        List,
        Map,
        Ref,
        Int8,
        UInt8,
        Int16,
        UInt16,
        Int32,
        UInt32,
        Int64,
        UInt64,
        Bool,
        Float,
        Double,
        Class
    }
}
