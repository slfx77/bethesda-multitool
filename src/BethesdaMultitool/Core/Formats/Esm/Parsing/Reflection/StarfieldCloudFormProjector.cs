using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.Reflection;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Reflection;

/// <summary>
///     Strict full-object entry point for the retail Starfield CLDF reflection stream. CLDF does
///     not define RFDP/RDIF inheritance, so a DIFF stream is rejected before semantic projection.
/// </summary>
internal static class StarfieldCloudFormDecoder
{
    internal static bool TryDecode(
        ReadOnlySpan<byte> data,
        out StarfieldCloudFormDefinition? definition,
        out string? error)
    {
        definition = null;
        error = null;
        if (!StarfieldCloudFormReflectionSchema.TryValidate(data, out error) ||
            !BethesdaReflectionReader.TryReadObject(
                data,
                expectDiff: false,
                StarfieldCloudFormProjector.RootType,
                out var reflected,
                out error))
        {
            return false;
        }

        return StarfieldCloudFormProjector.TryProject(reflected!, out definition, out error);
    }
}

/// <summary>
///     Verifies the exact retail CLDF class metadata before the generic reader erases scalar width
///     distinctions (for example UInt8 versus UInt32). Runtime offsets remain diagnostic metadata,
///     but class names, flags, field order, field names, and serialized types are contractual.
/// </summary>
internal static class StarfieldCloudFormReflectionSchema
{
    private const uint ChunkBeth = 0x48544542; // BETH
    private const uint ChunkStrt = 0x54525453; // STRT
    private const uint ChunkType = 0x45505954; // TYPE
    private const uint ChunkClas = 0x53414C43; // CLAS
    private const uint ChunkList = 0x5453494C; // LIST
    private const uint ChunkObjt = 0x544A424F; // OBJT
    private const uint ChunkDiff = 0x46464944; // DIFF
    private const int HeaderSize = 24;

    private static readonly IReadOnlyDictionary<string, ExpectedClass> ExpectedClasses =
        new Dictionary<string, ExpectedClass>(StringComparer.Ordinal)
        {
            ["BGSCloudForm::ShadowParams"] = Class(0,
                ("Enabled", "Bool"),
                ("OpacityTexture", "String"),
                ("TilingPerKm", "Float"),
                ("ElevationKm", "Float"),
                ("Strength", "Float"),
                ("WindScale", "Float")),
            ["XMCOLOR"] = Class(8,
                ("r", "UInt8"),
                ("g", "UInt8"),
                ("b", "UInt8"),
                ("a", "UInt8")),
            ["BGSCloudForm"] = Class(0,
                ("Shadows", "BGSCloudForm::ShadowParams"),
                ("Layers", "List"),
                ("Planes", "List"),
                ("pCloudCardSequence", "Ref")),
            ["BGSCloudForm::CloudLayer"] = Class(0,
                ("Name", "String"),
                ("ColorTexture", "String"),
                ("ThicknessTexture", "String"),
                ("NormalTexture", "String"),
                ("OpacityTexture", "String"),
                ("ElevationKm", "Float"),
                ("HeightKm", "Float"),
                ("DistanceKm", "Float"),
                ("Thickness", "Float"),
                ("TextureShadowOffset", "Float"),
                ("TextureShadowStrength", "Float"),
                ("NormalShadowStrength", "Float"),
                ("Tiling", "UInt32"),
                ("VerticalTiling", "UInt32"),
                ("TopBlendDistanceKm", "Float"),
                ("TopBlendStartKm", "Float"),
                ("BottomBlendDistanceKm", "Float"),
                ("BottomBlendStartKm", "Float"),
                ("WindScale", "Float"),
                ("Density", "Float"),
                ("Coverage", "Float"),
                ("AlphaAdd", "Float"),
                ("AlphaMultiply", "Float"),
                ("Tint", "XMCOLOR")),
            ["BGSCloudForm::CloudPlane"] = Class(0,
                ("Name", "String"),
                ("ColorTexture", "String"),
                ("ThicknessTexture", "String"),
                ("NormalTexture", "String"),
                ("OpacityTexture", "String"),
                ("ElevationKm", "Float"),
                ("FadeStartKm", "Float"),
                ("FadeDistanceKm", "Float"),
                ("Thickness", "Float"),
                ("TextureShadowOffset", "Float"),
                ("TextureShadowStrength", "Float"),
                ("NormalShadowStrength", "Float"),
                ("TilingPerKm", "Float"),
                ("WindScale", "Float"),
                ("Density", "Float"),
                ("Coverage", "Float"),
                ("AlphaAdd", "Float"),
                ("AlphaMultiply", "Float"),
                ("Tint", "XMCOLOR"))
        };

    internal static bool TryValidate(ReadOnlySpan<byte> data, out string? error)
    {
        error = null;
        if (data.Length < HeaderSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(data) != ChunkBeth ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 8 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) != 4 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[16..]) != ChunkStrt)
        {
            error = "Invalid version-4 BETH reflection header for CLDF.";
            return false;
        }

        var totalChunks = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        var stringByteCount = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);
        if (totalChunks < 3 || stringByteCount > (uint)(data.Length - HeaderSize) ||
            !TryReadStrings(data.Slice(HeaderSize, (int)stringByteCount), out var strings))
        {
            error = "CLDF reflection has an invalid string table.";
            return false;
        }

        var remainingChunkCount = totalChunks - 2;
        var position = HeaderSize + (int)stringByteCount;
        var classes = new Dictionary<string, ActualClass>(StringComparer.Ordinal);
        uint? declaredClassCount = null;
        var sawObject = false;
        var listChunkCount = 0;
        for (var chunkIndex = 0u; chunkIndex < remainingChunkCount; chunkIndex++)
        {
            if (position > data.Length - 8)
            {
                error = "CLDF reflection has a truncated chunk header.";
                return false;
            }

            var chunkType = BinaryPrimitives.ReadUInt32LittleEndian(data[position..]);
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(data[(position + 4)..]);
            if (chunkSize > (uint)(data.Length - position - 8))
            {
                error = "CLDF reflection has a truncated chunk body.";
                return false;
            }

            var body = data.Slice(position + 8, (int)chunkSize);
            if (chunkType == ChunkType)
            {
                if (sawObject || declaredClassCount.HasValue || body.Length != sizeof(uint))
                {
                    error = "CLDF reflection has a duplicate, misplaced, or malformed TYPE chunk.";
                    return false;
                }

                declaredClassCount = BinaryPrimitives.ReadUInt32LittleEndian(body);
            }
            else if (chunkType == ChunkClas)
            {
                if (sawObject || !declaredClassCount.HasValue ||
                    !TryReadClass(body, strings, out var className, out var actualClass, out error) ||
                    !classes.TryAdd(className!, actualClass!))
                {
                    error ??= "CLDF reflection has a duplicate or misplaced CLAS definition.";
                    return false;
                }
            }
            else if (chunkType == ChunkObjt)
            {
                sawObject = true;
            }
            else if (chunkType == ChunkDiff)
            {
                error = "CLDF reflection has an unexpected DIFF object; retail CLDF is REFL-only.";
                return false;
            }
            else if (chunkType == ChunkList)
            {
                if (!sawObject || listChunkCount >= 2 ||
                    !TryValidateListHeader(body, strings, listChunkCount, out error))
                {
                    error ??= "CLDF reflection has an unexpected or misplaced LIST chunk.";
                    return false;
                }

                listChunkCount++;
            }

            position += 8 + (int)chunkSize;
        }

        if (position != data.Length)
        {
            error = "CLDF reflection has trailing bytes outside its declared chunks.";
            return false;
        }

        if (listChunkCount != 2)
        {
            error = $"CLDF reflection has {listChunkCount} LIST chunks; the retail schema requires " +
                    "exactly Layers then Planes.";
            return false;
        }

        if (declaredClassCount != (uint)ExpectedClasses.Count || classes.Count != ExpectedClasses.Count)
        {
            error = $"CLDF reflection declares {declaredClassCount?.ToString() ?? "no"} classes and " +
                    $"defines {classes.Count}; the retail schema requires exactly {ExpectedClasses.Count}.";
            return false;
        }

        foreach (var (className, expected) in ExpectedClasses)
        {
            if (!classes.TryGetValue(className, out var actual))
            {
                error = $"CLDF reflection is missing retail class '{className}'.";
                return false;
            }

            if (actual.Flags != expected.Flags || actual.Fields.Count != expected.Fields.Count)
            {
                error = $"CLDF reflection class '{className}' does not match the retail flags/field count.";
                return false;
            }

            for (var fieldIndex = 0; fieldIndex < expected.Fields.Count; fieldIndex++)
            {
                var expectedField = expected.Fields[fieldIndex];
                var actualField = actual.Fields[fieldIndex];
                if (!string.Equals(actualField.Name, expectedField.Name, StringComparison.Ordinal) ||
                    !string.Equals(actualField.Type, expectedField.Type, StringComparison.Ordinal))
                {
                    error = $"CLDF reflection class '{className}' field {fieldIndex} is " +
                            $"'{actualField.Name}:{actualField.Type}', expected " +
                            $"'{expectedField.Name}:{expectedField.Type}'.";
                    return false;
                }
            }
        }

        return true;
    }

    private static bool TryValidateListHeader(
        ReadOnlySpan<byte> body,
        IReadOnlyDictionary<uint, string> strings,
        int listIndex,
        out string? error)
    {
        error = null;
        var expectedElementType = listIndex == 0
            ? "BGSCloudForm::CloudLayer"
            : "BGSCloudForm::CloudPlane";
        var minimumElementSize = listIndex == 0 ? 86 : 66;
        if (body.Length < 8 ||
            !TryResolveType(BinaryPrimitives.ReadUInt32LittleEndian(body), strings, out var elementType) ||
            !string.Equals(elementType, expectedElementType, StringComparison.Ordinal))
        {
            error = $"CLDF LIST {listIndex} is not List<{expectedElementType}>.";
            return false;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
        var maximumRepresentableCount = (uint)((body.Length - 8) / minimumElementSize);
        if (count > maximumRepresentableCount)
        {
            error = $"CLDF List<{expectedElementType}> count {count} cannot fit in its " +
                    $"{body.Length}-byte body.";
            return false;
        }

        return true;
    }

    private static bool TryReadStrings(
        ReadOnlySpan<byte> payload,
        out IReadOnlyDictionary<uint, string> strings)
    {
        var decoded = new Dictionary<uint, string>();
        var position = 0;
        while (position < payload.Length)
        {
            var terminator = payload[position..].IndexOf((byte)0);
            if (terminator < 0)
            {
                strings = decoded;
                return false;
            }

            decoded[(uint)position] = Encoding.ASCII.GetString(payload.Slice(position, terminator));
            position += terminator + 1;
        }

        strings = decoded;
        return position == payload.Length;
    }

    private static bool TryReadClass(
        ReadOnlySpan<byte> body,
        IReadOnlyDictionary<uint, string> strings,
        out string? className,
        out ActualClass? actualClass,
        out string? error)
    {
        className = null;
        actualClass = null;
        error = null;
        if (body.Length < 12 ||
            !strings.TryGetValue(BinaryPrimitives.ReadUInt32LittleEndian(body), out className))
        {
            error = "CLDF CLAS has no valid class name.";
            return false;
        }

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(body[8..]);
        var fieldCount = BinaryPrimitives.ReadUInt16LittleEndian(body[10..]);
        if (body.Length != 12 + (fieldCount * 12))
        {
            error = $"CLDF CLAS '{className}' has a malformed field table.";
            return false;
        }

        var fields = new List<ExpectedField>(fieldCount);
        for (var fieldIndex = 0; fieldIndex < fieldCount; fieldIndex++)
        {
            var field = body.Slice(12 + (fieldIndex * 12), 12);
            if (!strings.TryGetValue(BinaryPrimitives.ReadUInt32LittleEndian(field), out var fieldName) ||
                !TryResolveType(
                    BinaryPrimitives.ReadUInt32LittleEndian(field[4..]), strings, out var fieldType))
            {
                error = $"CLDF CLAS '{className}' field {fieldIndex} has an invalid name or type.";
                return false;
            }

            fields.Add(new ExpectedField(fieldName, fieldType!));
        }

        actualClass = new ActualClass(flags, Array.AsReadOnly(fields.ToArray()));
        return true;
    }

    private static bool TryResolveType(
        uint token,
        IReadOnlyDictionary<uint, string> strings,
        out string? type)
    {
        if (strings.TryGetValue(token, out type))
        {
            return true;
        }

        type = token switch
        {
            0xFFFFFF01 => "null",
            0xFFFFFF02 => "String",
            0xFFFFFF03 => "List",
            0xFFFFFF04 => "Map",
            0xFFFFFF05 => "Ref",
            0xFFFFFF08 => "Int8",
            0xFFFFFF09 => "UInt8",
            0xFFFFFF0A => "Int16",
            0xFFFFFF0B => "UInt16",
            0xFFFFFF0C => "Int32",
            0xFFFFFF0D => "UInt32",
            0xFFFFFF0E => "Int64",
            0xFFFFFF0F => "UInt64",
            0xFFFFFF10 => "Bool",
            0xFFFFFF11 => "Float",
            0xFFFFFF12 => "Double",
            0xFFFFFF13 => "Unknown",
            _ => null
        };
        return type is not null;
    }

    private static ExpectedClass Class(ushort flags, params (string Name, string Type)[] fields) =>
        new(flags, Array.AsReadOnly(fields.Select(field =>
            new ExpectedField(field.Name, field.Type)).ToArray()));

    private sealed record ExpectedClass(ushort Flags, IReadOnlyList<ExpectedField> Fields);
    private sealed record ActualClass(ushort Flags, IReadOnlyList<ExpectedField> Fields);
    private readonly record struct ExpectedField(string Name, string Type);
}

/// <summary>
///     Projects the complete retail <c>BGSCloudForm</c> schema. The typed result exposes the
///     authored textures, tint, density, coverage, elevation, tiling, and wind values needed by a
///     future source-backed renderer, while retaining the remaining proven structural fields
///     without assigning renderer meaning to them.
/// </summary>
internal static class StarfieldCloudFormProjector
{
    internal const string RootType = "BGSCloudForm";
    private const string ShadowType = "BGSCloudForm::ShadowParams";
    private const string LayerType = "BGSCloudForm::CloudLayer";
    private const string PlaneType = "BGSCloudForm::CloudPlane";
    private const string TintType = "XMCOLOR";

    private static readonly HashSet<string> RootFields = new(StringComparer.Ordinal)
    {
        "Shadows", "Layers", "Planes", "pCloudCardSequence"
    };

    private static readonly HashSet<string> ShadowFields = new(StringComparer.Ordinal)
    {
        "Enabled", "OpacityTexture", "TilingPerKm", "ElevationKm", "Strength", "WindScale"
    };

    private static readonly HashSet<string> LayerFields = new(StringComparer.Ordinal)
    {
        "Name", "ColorTexture", "ThicknessTexture", "NormalTexture", "OpacityTexture",
        "ElevationKm", "HeightKm", "DistanceKm", "Thickness", "TextureShadowOffset",
        "TextureShadowStrength", "NormalShadowStrength", "Tiling", "VerticalTiling",
        "TopBlendDistanceKm", "TopBlendStartKm", "BottomBlendDistanceKm", "BottomBlendStartKm",
        "WindScale", "Density", "Coverage", "AlphaAdd", "AlphaMultiply", "Tint"
    };

    private static readonly HashSet<string> PlaneFields = new(StringComparer.Ordinal)
    {
        "Name", "ColorTexture", "ThicknessTexture", "NormalTexture", "OpacityTexture",
        "ElevationKm", "FadeStartKm", "FadeDistanceKm", "Thickness", "TextureShadowOffset",
        "TextureShadowStrength", "NormalShadowStrength", "TilingPerKm", "WindScale", "Density",
        "Coverage", "AlphaAdd", "AlphaMultiply", "Tint"
    };

    private static readonly HashSet<string> TintFields = new(StringComparer.Ordinal)
    {
        "r", "g", "b", "a"
    };

    internal static bool TryProject(
        BethesdaReflectionObject reflected,
        out StarfieldCloudFormDefinition? definition,
        out string? error)
    {
        definition = null;
        error = null;
        if (!string.Equals(reflected.TypeName, RootType, StringComparison.Ordinal))
        {
            error = $"CLDF reflection root '{reflected.TypeName}' is not '{RootType}'.";
            return false;
        }

        if (!TryRequireExactFields(reflected, RootFields, out error) ||
            !TryReadObject(reflected, "Shadows", ShadowType, out var shadowObject, out error) ||
            !TryProjectShadows(shadowObject!, out var shadows, out error) ||
            !TryProjectLayers(reflected, out var layers, out error) ||
            !TryProjectPlanes(reflected, out var planes, out error) ||
            !TryReadReference(reflected, "pCloudCardSequence", out var cardSequence, out error))
        {
            return false;
        }

        definition = new StarfieldCloudFormDefinition(
            shadows!, layers!, planes!, cardSequence);
        return true;
    }

    private static bool TryProjectShadows(
        BethesdaReflectionObject reflected,
        out StarfieldCloudShadowParams? shadows,
        out string? error)
    {
        shadows = null;
        if (!TryRequireExactFields(reflected, ShadowFields, out error) ||
            !TryReadBool(reflected, "Enabled", out var enabled, out error) ||
            !TryReadString(reflected, "OpacityTexture", out var opacityTexture, out error) ||
            !TryReadFloat(reflected, "TilingPerKm", out var tilingPerKm, out error) ||
            !TryReadFloat(reflected, "ElevationKm", out var elevationKm, out error) ||
            !TryReadFloat(reflected, "Strength", out var strength, out error) ||
            !TryReadFloat(reflected, "WindScale", out var windScale, out error))
        {
            return false;
        }

        shadows = new StarfieldCloudShadowParams(
            enabled, opacityTexture!, tilingPerKm, elevationKm, strength, windScale);
        return true;
    }

    private static bool TryProjectLayers(
        BethesdaReflectionObject parent,
        out IReadOnlyList<StarfieldCloudLayer>? layers,
        out string? error)
    {
        layers = null;
        if (!TryReadList(parent, "Layers", LayerType, out var values, out error))
        {
            return false;
        }

        var projected = new List<StarfieldCloudLayer>(values!.Count);
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] is not BethesdaReflectionObjectValue item ||
                !string.Equals(item.Value.TypeName, LayerType, StringComparison.Ordinal))
            {
                error = $"Reflected field '{RootType}.Layers[{index}]' is not '{LayerType}'.";
                return false;
            }

            if (!TryProjectLayer(item.Value, out var layer, out error))
            {
                error = $"CLDF Layers[{index}]: {error}";
                return false;
            }

            projected.Add(layer!);
        }

        layers = Array.AsReadOnly(projected.ToArray());
        return true;
    }

    private static bool TryProjectLayer(
        BethesdaReflectionObject reflected,
        out StarfieldCloudLayer? layer,
        out string? error)
    {
        layer = null;
        if (!TryRequireExactFields(reflected, LayerFields, out error) ||
            !TryReadString(reflected, "Name", out var name, out error) ||
            !TryReadString(reflected, "ColorTexture", out var colorTexture, out error) ||
            !TryReadString(reflected, "ThicknessTexture", out var thicknessTexture, out error) ||
            !TryReadString(reflected, "NormalTexture", out var normalTexture, out error) ||
            !TryReadString(reflected, "OpacityTexture", out var opacityTexture, out error) ||
            !TryReadFloat(reflected, "ElevationKm", out var elevationKm, out error) ||
            !TryReadFloat(reflected, "HeightKm", out var heightKm, out error) ||
            !TryReadFloat(reflected, "DistanceKm", out var distanceKm, out error) ||
            !TryReadFloat(reflected, "Thickness", out var thickness, out error) ||
            !TryReadFloat(reflected, "TextureShadowOffset", out var textureShadowOffset, out error) ||
            !TryReadFloat(reflected, "TextureShadowStrength", out var textureShadowStrength, out error) ||
            !TryReadFloat(reflected, "NormalShadowStrength", out var normalShadowStrength, out error) ||
            !TryReadUInt32(reflected, "Tiling", out var tiling, out error) ||
            !TryReadUInt32(reflected, "VerticalTiling", out var verticalTiling, out error) ||
            !TryReadFloat(reflected, "TopBlendDistanceKm", out var topBlendDistanceKm, out error) ||
            !TryReadFloat(reflected, "TopBlendStartKm", out var topBlendStartKm, out error) ||
            !TryReadFloat(reflected, "BottomBlendDistanceKm", out var bottomBlendDistanceKm, out error) ||
            !TryReadFloat(reflected, "BottomBlendStartKm", out var bottomBlendStartKm, out error) ||
            !TryReadFloat(reflected, "WindScale", out var windScale, out error) ||
            !TryReadFloat(reflected, "Density", out var density, out error) ||
            !TryReadFloat(reflected, "Coverage", out var coverage, out error) ||
            !TryReadFloat(reflected, "AlphaAdd", out var alphaAdd, out error) ||
            !TryReadFloat(reflected, "AlphaMultiply", out var alphaMultiply, out error) ||
            !TryReadObject(reflected, "Tint", TintType, out var tintObject, out error) ||
            !TryProjectTint(tintObject!, out var tint, out error))
        {
            return false;
        }

        layer = new StarfieldCloudLayer(
            name!, colorTexture!, thicknessTexture!, normalTexture!, opacityTexture!,
            elevationKm, heightKm, distanceKm, thickness, textureShadowOffset,
            textureShadowStrength, normalShadowStrength, tiling, verticalTiling,
            topBlendDistanceKm, topBlendStartKm, bottomBlendDistanceKm, bottomBlendStartKm,
            windScale, density, coverage, alphaAdd, alphaMultiply, tint!);
        return true;
    }

    private static bool TryProjectPlanes(
        BethesdaReflectionObject parent,
        out IReadOnlyList<StarfieldCloudPlane>? planes,
        out string? error)
    {
        planes = null;
        if (!TryReadList(parent, "Planes", PlaneType, out var values, out error))
        {
            return false;
        }

        var projected = new List<StarfieldCloudPlane>(values!.Count);
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] is not BethesdaReflectionObjectValue item ||
                !string.Equals(item.Value.TypeName, PlaneType, StringComparison.Ordinal))
            {
                error = $"Reflected field '{RootType}.Planes[{index}]' is not '{PlaneType}'.";
                return false;
            }

            if (!TryProjectPlane(item.Value, out var plane, out error))
            {
                error = $"CLDF Planes[{index}]: {error}";
                return false;
            }

            projected.Add(plane!);
        }

        planes = Array.AsReadOnly(projected.ToArray());
        return true;
    }

    private static bool TryProjectPlane(
        BethesdaReflectionObject reflected,
        out StarfieldCloudPlane? plane,
        out string? error)
    {
        plane = null;
        if (!TryRequireExactFields(reflected, PlaneFields, out error) ||
            !TryReadString(reflected, "Name", out var name, out error) ||
            !TryReadString(reflected, "ColorTexture", out var colorTexture, out error) ||
            !TryReadString(reflected, "ThicknessTexture", out var thicknessTexture, out error) ||
            !TryReadString(reflected, "NormalTexture", out var normalTexture, out error) ||
            !TryReadString(reflected, "OpacityTexture", out var opacityTexture, out error) ||
            !TryReadFloat(reflected, "ElevationKm", out var elevationKm, out error) ||
            !TryReadFloat(reflected, "FadeStartKm", out var fadeStartKm, out error) ||
            !TryReadFloat(reflected, "FadeDistanceKm", out var fadeDistanceKm, out error) ||
            !TryReadFloat(reflected, "Thickness", out var thickness, out error) ||
            !TryReadFloat(reflected, "TextureShadowOffset", out var textureShadowOffset, out error) ||
            !TryReadFloat(reflected, "TextureShadowStrength", out var textureShadowStrength, out error) ||
            !TryReadFloat(reflected, "NormalShadowStrength", out var normalShadowStrength, out error) ||
            !TryReadFloat(reflected, "TilingPerKm", out var tilingPerKm, out error) ||
            !TryReadFloat(reflected, "WindScale", out var windScale, out error) ||
            !TryReadFloat(reflected, "Density", out var density, out error) ||
            !TryReadFloat(reflected, "Coverage", out var coverage, out error) ||
            !TryReadFloat(reflected, "AlphaAdd", out var alphaAdd, out error) ||
            !TryReadFloat(reflected, "AlphaMultiply", out var alphaMultiply, out error) ||
            !TryReadObject(reflected, "Tint", TintType, out var tintObject, out error) ||
            !TryProjectTint(tintObject!, out var tint, out error))
        {
            return false;
        }

        plane = new StarfieldCloudPlane(
            name!, colorTexture!, thicknessTexture!, normalTexture!, opacityTexture!,
            elevationKm, fadeStartKm, fadeDistanceKm, thickness, textureShadowOffset,
            textureShadowStrength, normalShadowStrength, tilingPerKm, windScale, density,
            coverage, alphaAdd, alphaMultiply, tint!);
        return true;
    }

    private static bool TryProjectTint(
        BethesdaReflectionObject reflected,
        out StarfieldCloudTint? tint,
        out string? error)
    {
        tint = null;
        if (!TryRequireExactFields(reflected, TintFields, out error) ||
            !TryReadUInt8(reflected, "r", out var red, out error) ||
            !TryReadUInt8(reflected, "g", out var green, out error) ||
            !TryReadUInt8(reflected, "b", out var blue, out error) ||
            !TryReadUInt8(reflected, "a", out var alpha, out error))
        {
            return false;
        }

        tint = new StarfieldCloudTint(red, green, blue, alpha);
        return true;
    }

    private static bool TryReadList(
        BethesdaReflectionObject parent,
        string fieldName,
        string expectedElementType,
        out IReadOnlyList<BethesdaReflectionValue>? values,
        out string? error)
    {
        values = null;
        if (!TryGetField(parent, fieldName, out var field, out error))
        {
            return false;
        }

        if (field is not BethesdaReflectionListValue list ||
            !string.Equals(list.ElementType, expectedElementType, StringComparison.Ordinal))
        {
            error = $"Reflected field '{parent.TypeName}.{fieldName}' is not List<{expectedElementType}>.";
            return false;
        }

        values = list.Values;
        return true;
    }

    private static bool TryReadReference(
        BethesdaReflectionObject parent,
        string fieldName,
        out uint value,
        out string? error)
    {
        value = 0;
        if (!TryGetField(parent, fieldName, out var field, out error))
        {
            return false;
        }

        if (field is not BethesdaReflectionReferenceValue
            {
                ValueType: "UInt32",
                Value: BethesdaReflectionUnsignedValue unsigned
            } || unsigned.Value > uint.MaxValue)
        {
            error = $"Reflected field '{parent.TypeName}.{fieldName}' is not Ref<UInt32>.";
            return false;
        }

        value = (uint)unsigned.Value;
        return true;
    }

    private static bool TryReadObject(
        BethesdaReflectionObject parent,
        string fieldName,
        string expectedType,
        out BethesdaReflectionObject? value,
        out string? error)
    {
        value = null;
        if (!TryGetField(parent, fieldName, out var field, out error))
        {
            return false;
        }

        if (field is not BethesdaReflectionObjectValue reflected ||
            !string.Equals(reflected.Value.TypeName, expectedType, StringComparison.Ordinal))
        {
            error = $"Reflected field '{parent.TypeName}.{fieldName}' is not '{expectedType}'.";
            return false;
        }

        value = reflected.Value;
        return true;
    }

    private static bool TryReadString(
        BethesdaReflectionObject parent,
        string fieldName,
        out string? value,
        out string? error)
    {
        value = null;
        if (!TryGetField(parent, fieldName, out var field, out error))
        {
            return false;
        }

        if (field is not BethesdaReflectionStringValue reflected)
        {
            error = $"Reflected field '{parent.TypeName}.{fieldName}' is not String.";
            return false;
        }

        value = reflected.Value;
        return true;
    }

    private static bool TryReadBool(
        BethesdaReflectionObject parent,
        string fieldName,
        out bool value,
        out string? error)
    {
        value = false;
        if (!TryGetField(parent, fieldName, out var field, out error))
        {
            return false;
        }

        if (field is not BethesdaReflectionBoolValue reflected)
        {
            error = $"Reflected field '{parent.TypeName}.{fieldName}' is not Bool.";
            return false;
        }

        value = reflected.Value;
        return true;
    }

    private static bool TryReadFloat(
        BethesdaReflectionObject parent,
        string fieldName,
        out float value,
        out string? error)
    {
        value = 0;
        if (!TryGetField(parent, fieldName, out var field, out error))
        {
            return false;
        }

        if (field is not BethesdaReflectionFloatValue reflected ||
            !double.IsFinite(reflected.Value) ||
            reflected.Value is < -float.MaxValue or > float.MaxValue)
        {
            error = $"Reflected field '{parent.TypeName}.{fieldName}' is not a finite Float.";
            return false;
        }

        value = (float)reflected.Value;
        return true;
    }

    private static bool TryReadUInt32(
        BethesdaReflectionObject parent,
        string fieldName,
        out uint value,
        out string? error)
    {
        value = 0;
        if (!TryGetField(parent, fieldName, out var field, out error))
        {
            return false;
        }

        if (field is not BethesdaReflectionUnsignedValue reflected || reflected.Value > uint.MaxValue)
        {
            error = $"Reflected field '{parent.TypeName}.{fieldName}' is not UInt32.";
            return false;
        }

        value = (uint)reflected.Value;
        return true;
    }

    private static bool TryReadUInt8(
        BethesdaReflectionObject parent,
        string fieldName,
        out byte value,
        out string? error)
    {
        value = 0;
        if (!TryGetField(parent, fieldName, out var field, out error))
        {
            return false;
        }

        if (field is not BethesdaReflectionUnsignedValue reflected || reflected.Value > byte.MaxValue)
        {
            error = $"Reflected field '{parent.TypeName}.{fieldName}' is not UInt8.";
            return false;
        }

        value = (byte)reflected.Value;
        return true;
    }

    private static bool TryGetField(
        BethesdaReflectionObject parent,
        string fieldName,
        out BethesdaReflectionValue? value,
        out string? error)
    {
        error = null;
        if (parent.Fields.TryGetValue(fieldName, out value))
        {
            return true;
        }

        error = $"Full reflected object '{parent.TypeName}' is missing field '{fieldName}'.";
        return false;
    }

    private static bool TryRequireExactFields(
        BethesdaReflectionObject reflected,
        IReadOnlySet<string> expected,
        out string? error)
    {
        error = null;
        if (reflected.Fields.Count != expected.Count)
        {
            error = $"Full reflected object '{reflected.TypeName}' has {reflected.Fields.Count} fields; " +
                    $"the retail schema requires exactly {expected.Count}.";
            return false;
        }

        foreach (var fieldName in reflected.Fields.Keys)
        {
            if (!expected.Contains(fieldName))
            {
                error = $"Full reflected object '{reflected.TypeName}' contains unknown field '{fieldName}'.";
                return false;
            }
        }

        return true;
    }
}
