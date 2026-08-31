using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.Reflection;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Reflection;

/// <summary>
///     Strict entry point for the full REFL/OBJT carried by retail Starfield VOLI records. Retail
///     has no VOLI RFDP/RDIF records, so a DIFF chunk is deliberately rejected by the reflection
///     decoder instead of being treated as a partial settings object.
/// </summary>
internal static class StarfieldVolumetricLightingDecoder
{
    private const string RootType = "BGSVolumetricLighting";

    internal static bool TryDecode(
        ReadOnlySpan<byte> data,
        out StarfieldVolumetricLightingSettings? settings,
        out string? error)
    {
        settings = null;
        if (!StarfieldVolumetricLightingReflectionSchema.TryValidate(data, out error) ||
            !BethesdaReflectionReader.TryReadObject(
                data, false, RootType, out var reflected, out error))
        {
            return false;
        }

        return StarfieldVolumetricLightingProjector.TryProject(
            reflected!, out settings, out error);
    }
}

/// <summary>
///     Pins the complete retail VOLI CLAS metadata before the generic tree intentionally coalesces
///     Float and Double into the same reflected value representation. Runtime offsets are ignored,
///     but class flags, field order, names, and serialized types are exact.
/// </summary>
internal static class StarfieldVolumetricLightingReflectionSchema
{
    private const uint ChunkBeth = 0x48544542; // BETH
    private const uint ChunkStrt = 0x54525453; // STRT
    private const uint ChunkType = 0x45505954; // TYPE
    private const uint ChunkClas = 0x53414C43; // CLAS
    private const uint ChunkObjt = 0x544A424F; // OBJT
    private const uint ChunkDiff = 0x46464944; // DIFF
    private const int HeaderSize = 24;

    private static readonly IReadOnlyDictionary<string, ExpectedClass> ExpectedClasses =
        new Dictionary<string, ExpectedClass>(StringComparer.Ordinal)
        {
            ["XMFLOAT4"] = Class(8,
                ("x", "Float"), ("y", "Float"), ("z", "Float"), ("w", "Float")),
            ["BGSVolumetricLightingSettings::FogThicknessSettings"] = Class(0,
                ("ThicknessNoiseScale", "Float"),
                ("ThicknessNoiseBias", "Float"),
                ("MinFogThickness", "Float"),
                ("MaxFogThickness", "Float")),
            ["BGSVolumetricLightingSettings::FogMapSettings"] = Class(0,
                ("HeightAboveTerrain", "Float"),
                ("TerrainMatch", "Float"),
                ("Albedo", "XMFLOAT4"),
                ("Anisotropy", "Float"),
                ("MinMeanFreePath", "Float"),
                ("MaxMeanFreePath", "Float"),
                ("HeightFalloffExponent", "Float"),
                ("Span", "Float")),
            ["BGSVolumetricLightingSettings::FogDensitySettings"] = Class(0,
                ("DensityNoiseScale", "Float"),
                ("DensityNoiseBias", "Float"),
                ("MinFogDensity", "Float"),
                ("MaxFogDensity", "Float"),
                ("DensityStartDistance", "Float"),
                ("DensityFullDistance", "Float"),
                ("DensityDistanceExponent", "Float")),
            ["BGSVolumetricLightingSettings::ExteriorAndInteriorSettings"] = Class(0,
                ("ScatteringVolumeNear", "Float"),
                ("ScatteringVolumeFar", "Float"),
                ("HighFrequencyNoiseScale", "Float"),
                ("HighFrequencyNoiseDensityScale", "Float")),
            ["BGSVolumetricLightingSettings"] = Class(0,
                ("ExteriorAndInterior", "BGSVolumetricLightingSettings::ExteriorAndInteriorSettings"),
                ("Exterior", "BGSVolumetricLightingSettings::ExteriorSettings"),
                ("DistantLighting", "BGSVolumetricLightingSettings::DistantLightingSettings")),
            ["BGSVolumetricLightingSettings::ExteriorSettings"] = Class(0,
                ("FogThickness", "BGSVolumetricLightingSettings::FogThicknessSettings"),
                ("FogDensity", "BGSVolumetricLightingSettings::FogDensitySettings"),
                ("HorizonFog", "BGSVolumetricLightingSettings::HorizonFogSettings"),
                ("FogMap", "BGSVolumetricLightingSettings::FogMapSettings")),
            ["BGSVolumetricLighting"] = Class(0,
                ("Settings", "BGSVolumetricLightingSettings")),
            ["BGSVolumetricLightingSettings::HorizonFogSettings"] = Class(0,
                ("FogThickness", "Float"),
                ("FogDensity", "Float"),
                ("DensityStartDistance", "Float"),
                ("DensityFullDistance", "Float")),
            ["BGSVolumetricLightingSettings::DistantLightingSettings"] = Class(0,
                ("ScatteringTransition", "Float"),
                ("ScatteringFar", "Float"))
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
            error = "Invalid version-4 BETH reflection header for VOLI.";
            return false;
        }

        var totalChunks = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        var stringByteCount = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);
        if (totalChunks < 3 || stringByteCount > (uint)(data.Length - HeaderSize) ||
            !TryReadStrings(data.Slice(HeaderSize, (int)stringByteCount), out var strings))
        {
            error = "VOLI reflection has an invalid string table.";
            return false;
        }

        var remainingChunkCount = totalChunks - 2;
        var position = HeaderSize + (int)stringByteCount;
        var classes = new Dictionary<string, ActualClass>(StringComparer.Ordinal);
        uint? declaredClassCount = null;
        var objectCount = 0;
        for (var chunkIndex = 0u; chunkIndex < remainingChunkCount; chunkIndex++)
        {
            if (position > data.Length - 8)
            {
                error = "VOLI reflection has a truncated chunk header.";
                return false;
            }

            var chunkType = BinaryPrimitives.ReadUInt32LittleEndian(data[position..]);
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(data[(position + 4)..]);
            if (chunkSize > (uint)(data.Length - position - 8))
            {
                error = "VOLI reflection has a truncated chunk body.";
                return false;
            }

            var body = data.Slice(position + 8, (int)chunkSize);
            if (chunkType == ChunkType)
            {
                if (objectCount != 0 || declaredClassCount.HasValue || body.Length != sizeof(uint))
                {
                    error = "VOLI reflection has a duplicate, misplaced, or malformed TYPE chunk.";
                    return false;
                }

                declaredClassCount = BinaryPrimitives.ReadUInt32LittleEndian(body);
            }
            else if (chunkType == ChunkClas)
            {
                if (objectCount != 0 || !declaredClassCount.HasValue ||
                    !TryReadClass(body, strings, out var className, out var actualClass, out error) ||
                    !classes.TryAdd(className!, actualClass!))
                {
                    error ??= "VOLI reflection has a duplicate or misplaced CLAS definition.";
                    return false;
                }
            }
            else if (chunkType == ChunkObjt)
            {
                objectCount++;
            }
            else if (chunkType == ChunkDiff)
            {
                error = "VOLI reflection has an unexpected DIFF object; retail VOLI is REFL-only.";
                return false;
            }
            else
            {
                error = $"VOLI reflection has unsupported side chunk 0x{chunkType:X8}.";
                return false;
            }

            position += 8 + (int)chunkSize;
        }

        if (position != data.Length)
        {
            error = "VOLI reflection has trailing bytes outside its declared chunks.";
            return false;
        }

        if (objectCount != 1)
        {
            error = $"VOLI reflection has {objectCount} OBJT chunks; exactly one is required.";
            return false;
        }

        if (declaredClassCount != (uint)ExpectedClasses.Count || classes.Count != ExpectedClasses.Count)
        {
            error = $"VOLI reflection declares {declaredClassCount?.ToString() ?? "no"} classes and " +
                    $"defines {classes.Count}; the retail schema requires exactly {ExpectedClasses.Count}.";
            return false;
        }

        foreach (var (className, expected) in ExpectedClasses)
        {
            if (!classes.TryGetValue(className, out var actual))
            {
                error = $"VOLI reflection is missing expected class '{className}'.";
                return false;
            }

            if (actual.Flags != expected.Flags)
            {
                error = $"VOLI reflection class '{className}' has flags {actual.Flags}; " +
                        $"the retail schema requires {expected.Flags}.";
                return false;
            }

            var commonFieldCount = Math.Min(actual.Fields.Count, expected.Fields.Count);
            for (var fieldIndex = 0; fieldIndex < commonFieldCount; fieldIndex++)
            {
                var expectedField = expected.Fields[fieldIndex];
                var actualField = actual.Fields[fieldIndex];
                if (!string.Equals(actualField.Name, expectedField.Name, StringComparison.Ordinal) ||
                    !string.Equals(actualField.Type, expectedField.Type, StringComparison.Ordinal))
                {
                    error = $"VOLI reflection class '{className}' field {fieldIndex} is " +
                            $"'{actualField.Name}:{actualField.Type}', expected " +
                            $"'{expectedField.Name}:{expectedField.Type}'.";
                    return false;
                }
            }

            if (actual.Fields.Count != expected.Fields.Count)
            {
                error = $"VOLI reflection class '{className}' defines {actual.Fields.Count} fields; " +
                        $"the retail schema requires {expected.Fields.Count}.";
                return false;
            }
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
            error = "VOLI CLAS has no valid class name.";
            return false;
        }

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(body[8..]);
        var fieldCount = BinaryPrimitives.ReadUInt16LittleEndian(body[10..]);
        if (body.Length != 12 + (fieldCount * 12))
        {
            error = $"VOLI CLAS '{className}' has a malformed field table.";
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
                error = $"VOLI CLAS '{className}' field {fieldIndex} has an invalid name or type.";
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
///     Projects all 32 finite Float leaves in the retail <c>BGSVolumetricLighting</c> schema. This
///     projector is intentionally exact: renamed, missing, additional, or mistyped fields fail
///     closed so schema drift cannot silently masquerade as valid authored fog.
/// </summary>
internal static class StarfieldVolumetricLightingProjector
{
    private const string RootType = "BGSVolumetricLighting";
    private const string SettingsType = "BGSVolumetricLightingSettings";
    private const string ExteriorAndInteriorType =
        "BGSVolumetricLightingSettings::ExteriorAndInteriorSettings";
    private const string ExteriorType = "BGSVolumetricLightingSettings::ExteriorSettings";
    private const string FogThicknessType = "BGSVolumetricLightingSettings::FogThicknessSettings";
    private const string FogDensityType = "BGSVolumetricLightingSettings::FogDensitySettings";
    private const string HorizonFogType = "BGSVolumetricLightingSettings::HorizonFogSettings";
    private const string FogMapType = "BGSVolumetricLightingSettings::FogMapSettings";
    private const string DistantLightingType =
        "BGSVolumetricLightingSettings::DistantLightingSettings";
    private const string Float4Type = "XMFLOAT4";

    internal static bool TryProject(
        BethesdaReflectionObject reflected,
        out StarfieldVolumetricLightingSettings? settings,
        out string? error)
    {
        settings = null;
        error = null;
        if (!string.Equals(reflected.TypeName, RootType, StringComparison.Ordinal))
        {
            error = $"VOLI reflection root '{reflected.TypeName}' is not '{RootType}'.";
            return false;
        }

        if (!TryRequireExactFields(reflected, ["Settings"], out error) ||
            !TryReadObject(reflected, "Settings", SettingsType, out var value, out error) ||
            !TryReadSettings(value!, out settings, out error))
        {
            settings = null;
            return false;
        }

        return true;
    }

    private static bool TryReadSettings(
        BethesdaReflectionObject value,
        out StarfieldVolumetricLightingSettings? settings,
        out string? error)
    {
        settings = null;
        if (!TryRequireExactFields(
                value, ["ExteriorAndInterior", "Exterior", "DistantLighting"], out error) ||
            !TryReadObject(
                value, "ExteriorAndInterior", ExteriorAndInteriorType,
                out var exteriorAndInteriorObject, out error) ||
            !TryReadExteriorAndInterior(
                exteriorAndInteriorObject!, out var exteriorAndInterior, out error) ||
            !TryReadObject(value, "Exterior", ExteriorType, out var exteriorObject, out error) ||
            !TryReadExterior(exteriorObject!, out var exterior, out error) ||
            !TryReadObject(
                value, "DistantLighting", DistantLightingType,
                out var distantObject, out error) ||
            !TryReadDistantLighting(distantObject!, out var distantLighting, out error))
        {
            return false;
        }

        settings = new StarfieldVolumetricLightingSettings(
            exteriorAndInterior!, exterior!, distantLighting!);
        return true;
    }

    private static bool TryReadExteriorAndInterior(
        BethesdaReflectionObject value,
        out StarfieldVolumetricExteriorAndInteriorSettings? settings,
        out string? error)
    {
        settings = null;
        if (!TryRequireExactFields(
                value,
                [
                    "ScatteringVolumeNear", "ScatteringVolumeFar",
                    "HighFrequencyNoiseScale", "HighFrequencyNoiseDensityScale"
                ],
                out error) ||
            !TryReadFloat(value, "ScatteringVolumeNear", out var scatteringNear, out error) ||
            !TryReadFloat(value, "ScatteringVolumeFar", out var scatteringFar, out error) ||
            !TryReadFloat(value, "HighFrequencyNoiseScale", out var noiseScale, out error) ||
            !TryReadFloat(
                value, "HighFrequencyNoiseDensityScale", out var noiseDensityScale, out error))
        {
            return false;
        }

        settings = new StarfieldVolumetricExteriorAndInteriorSettings(
            scatteringNear, scatteringFar, noiseScale, noiseDensityScale);
        return true;
    }

    private static bool TryReadExterior(
        BethesdaReflectionObject value,
        out StarfieldVolumetricExteriorSettings? settings,
        out string? error)
    {
        settings = null;
        if (!TryRequireExactFields(
                value, ["FogThickness", "FogDensity", "HorizonFog", "FogMap"], out error) ||
            !TryReadObject(
                value, "FogThickness", FogThicknessType, out var thicknessObject, out error) ||
            !TryReadFogThickness(thicknessObject!, out var thickness, out error) ||
            !TryReadObject(
                value, "FogDensity", FogDensityType, out var densityObject, out error) ||
            !TryReadFogDensity(densityObject!, out var density, out error) ||
            !TryReadObject(
                value, "HorizonFog", HorizonFogType, out var horizonObject, out error) ||
            !TryReadHorizonFog(horizonObject!, out var horizon, out error) ||
            !TryReadObject(value, "FogMap", FogMapType, out var fogMapObject, out error) ||
            !TryReadFogMap(fogMapObject!, out var fogMap, out error))
        {
            return false;
        }

        settings = new StarfieldVolumetricExteriorSettings(
            thickness!, density!, horizon!, fogMap!);
        return true;
    }

    private static bool TryReadFogThickness(
        BethesdaReflectionObject value,
        out StarfieldVolumetricFogThicknessSettings? settings,
        out string? error)
    {
        settings = null;
        if (!TryRequireExactFields(
                value,
                ["ThicknessNoiseScale", "ThicknessNoiseBias", "MinFogThickness", "MaxFogThickness"],
                out error) ||
            !TryReadFloat(value, "ThicknessNoiseScale", out var scale, out error) ||
            !TryReadFloat(value, "ThicknessNoiseBias", out var bias, out error) ||
            !TryReadFloat(value, "MinFogThickness", out var minimum, out error) ||
            !TryReadFloat(value, "MaxFogThickness", out var maximum, out error))
        {
            return false;
        }

        settings = new StarfieldVolumetricFogThicknessSettings(scale, bias, minimum, maximum);
        return true;
    }

    private static bool TryReadFogDensity(
        BethesdaReflectionObject value,
        out StarfieldVolumetricFogDensitySettings? settings,
        out string? error)
    {
        settings = null;
        if (!TryRequireExactFields(
                value,
                [
                    "DensityNoiseScale", "DensityNoiseBias", "MinFogDensity", "MaxFogDensity",
                    "DensityStartDistance", "DensityFullDistance", "DensityDistanceExponent"
                ],
                out error) ||
            !TryReadFloat(value, "DensityNoiseScale", out var scale, out error) ||
            !TryReadFloat(value, "DensityNoiseBias", out var bias, out error) ||
            !TryReadFloat(value, "MinFogDensity", out var minimum, out error) ||
            !TryReadFloat(value, "MaxFogDensity", out var maximum, out error) ||
            !TryReadFloat(value, "DensityStartDistance", out var start, out error) ||
            !TryReadFloat(value, "DensityFullDistance", out var full, out error) ||
            !TryReadFloat(value, "DensityDistanceExponent", out var exponent, out error))
        {
            return false;
        }

        settings = new StarfieldVolumetricFogDensitySettings(
            scale, bias, minimum, maximum, start, full, exponent);
        return true;
    }

    private static bool TryReadHorizonFog(
        BethesdaReflectionObject value,
        out StarfieldVolumetricHorizonFogSettings? settings,
        out string? error)
    {
        settings = null;
        if (!TryRequireExactFields(
                value,
                ["FogThickness", "FogDensity", "DensityStartDistance", "DensityFullDistance"],
                out error) ||
            !TryReadFloat(value, "FogThickness", out var thickness, out error) ||
            !TryReadFloat(value, "FogDensity", out var density, out error) ||
            !TryReadFloat(value, "DensityStartDistance", out var start, out error) ||
            !TryReadFloat(value, "DensityFullDistance", out var full, out error))
        {
            return false;
        }

        settings = new StarfieldVolumetricHorizonFogSettings(thickness, density, start, full);
        return true;
    }

    private static bool TryReadFogMap(
        BethesdaReflectionObject value,
        out StarfieldVolumetricFogMapSettings? settings,
        out string? error)
    {
        settings = null;
        if (!TryRequireExactFields(
                value,
                [
                    "HeightAboveTerrain", "TerrainMatch", "Albedo", "Anisotropy",
                    "MinMeanFreePath", "MaxMeanFreePath", "HeightFalloffExponent", "Span"
                ],
                out error) ||
            !TryReadFloat(value, "HeightAboveTerrain", out var height, out error) ||
            !TryReadFloat(value, "TerrainMatch", out var terrainMatch, out error) ||
            !TryReadObject(value, "Albedo", Float4Type, out var albedoObject, out error) ||
            !TryReadFloat4(albedoObject!, out var albedo, out error) ||
            !TryReadFloat(value, "Anisotropy", out var anisotropy, out error) ||
            !TryReadFloat(value, "MinMeanFreePath", out var minimumMeanFreePath, out error) ||
            !TryReadFloat(value, "MaxMeanFreePath", out var maximumMeanFreePath, out error) ||
            !TryReadFloat(value, "HeightFalloffExponent", out var heightExponent, out error) ||
            !TryReadFloat(value, "Span", out var span, out error))
        {
            return false;
        }

        settings = new StarfieldVolumetricFogMapSettings(
            height, terrainMatch, albedo!, anisotropy,
            minimumMeanFreePath, maximumMeanFreePath, heightExponent, span);
        return true;
    }

    private static bool TryReadDistantLighting(
        BethesdaReflectionObject value,
        out StarfieldVolumetricDistantLightingSettings? settings,
        out string? error)
    {
        settings = null;
        if (!TryRequireExactFields(
                value, ["ScatteringTransition", "ScatteringFar"], out error) ||
            !TryReadFloat(value, "ScatteringTransition", out var transition, out error) ||
            !TryReadFloat(value, "ScatteringFar", out var far, out error))
        {
            return false;
        }

        settings = new StarfieldVolumetricDistantLightingSettings(transition, far);
        return true;
    }

    private static bool TryReadFloat4(
        BethesdaReflectionObject value,
        out StarfieldVolumetricFloat4? vector,
        out string? error)
    {
        vector = null;
        if (!TryRequireExactFields(value, ["x", "y", "z", "w"], out error) ||
            !TryReadFloat(value, "x", out var x, out error) ||
            !TryReadFloat(value, "y", out var y, out error) ||
            !TryReadFloat(value, "z", out var z, out error) ||
            !TryReadFloat(value, "w", out var w, out error))
        {
            return false;
        }

        vector = new StarfieldVolumetricFloat4(x, y, z, w);
        return true;
    }

    private static bool TryReadFloat(
        BethesdaReflectionObject parent,
        string fieldName,
        out float value,
        out string? error)
    {
        value = 0;
        if (!parent.Fields.TryGetValue(fieldName, out var field))
        {
            error = $"Full VOLI object '{parent.TypeName}' is missing field '{fieldName}'.";
            return false;
        }

        if (field is not BethesdaReflectionFloatValue reflected ||
            !double.IsFinite(reflected.Value) ||
            reflected.Value is < -float.MaxValue or > float.MaxValue)
        {
            error = $"VOLI field '{parent.TypeName}.{fieldName}' is not a finite Float.";
            return false;
        }

        value = (float)reflected.Value;
        error = null;
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
        if (!parent.Fields.TryGetValue(fieldName, out var field))
        {
            error = $"Full VOLI object '{parent.TypeName}' is missing field '{fieldName}'.";
            return false;
        }

        if (field is not BethesdaReflectionObjectValue reflected ||
            !string.Equals(reflected.Value.TypeName, expectedType, StringComparison.Ordinal))
        {
            error = $"VOLI field '{parent.TypeName}.{fieldName}' is not an object of exact type " +
                    $"'{expectedType}'.";
            return false;
        }

        value = reflected.Value;
        error = null;
        return true;
    }

    private static bool TryRequireExactFields(
        BethesdaReflectionObject value,
        IReadOnlyList<string> expected,
        out string? error)
    {
        foreach (var fieldName in expected)
        {
            if (!value.Fields.ContainsKey(fieldName))
            {
                error = $"Full VOLI object '{value.TypeName}' is missing field '{fieldName}'.";
                return false;
            }
        }

        if (value.Fields.Count != expected.Count)
        {
            var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
            var unexpected = value.Fields.Keys.FirstOrDefault(field => !expectedSet.Contains(field));
            error = unexpected is null
                ? $"VOLI object '{value.TypeName}' does not have the exact retail field set."
                : $"VOLI object '{value.TypeName}' has unexpected field '{unexpected}'.";
            return false;
        }

        error = null;
        return true;
    }
}
