using System.Buffers.Binary;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

internal sealed class MiscEnvironmentHandler(RecordParserContext context) : RecordHandlerBase(context)
{
    private readonly AudioRecordHandler _audio = new(context);
    private readonly TerrainTextureRecordHandler _terrainTextures = new(context);

    #region Water

    /// <summary>
    ///     Parse all Water (WATR) records.
    /// </summary>
    internal List<WaterRecord> ParseWater()
    {
        var water = ParseRecordList("WATR", 4096,
            ParseWaterFromAccessor,
            record => new WaterRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FormIdToFullName.GetValueOrDefault(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });

        Context.MergeRuntimeRecords(water, 0x4E, w => w.FormId,
            (reader, entry) => reader.ReadRuntimeWater(entry), "water records");

        return water;
    }

    private WaterRecord? ParseWaterFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new WaterRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FormIdToFullName.GetValueOrDefault(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null, fullName = null, noiseTexture = null, surfaceTexture = null;
        var normalTextures = new List<string>(3);
        var legacyNormalTextures = new List<string>(3);
        byte opacity = 0;
        byte[]? waterFlags = null;
        uint? soundFormId = null;
        ushort damage = 0;
        Dictionary<string, object?>? visualProps = null;
        Dictionary<string, object?>? relatedWater = null;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                case "FULL":
                    fullName = Context.ReadFullName(subData);
                    break;
                case "NNAM":
                {
                    var texture = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrWhiteSpace(texture))
                    {
                        // Skyrim labels NNAM as the old texture layout and uses NAM2/NAM3/NAM4
                        // for its active set. Retain both without letting old NNAM entries occupy
                        // the three active sampler slots when a record carries both layouts.
                        if (Context.Game == BethesdaGame.Skyrim)
                        {
                            legacyNormalTextures.Add(texture);
                        }
                        else
                        {
                            normalTextures.Add(texture);
                            noiseTexture ??= texture;
                        }
                    }
                    break;
                }
                case "TNAM" when Context.Game == BethesdaGame.Oblivion:
                {
                    surfaceTexture = EsmStringUtils.ReadNullTermString(subData);
                    break;
                }
                case "ANAM" when sub.DataLength >= 1:
                    opacity = subData[0];
                    break;
                case "FNAM":
                {
                    waterFlags = new byte[sub.DataLength];
                    subData.CopyTo(waterFlags);
                    break;
                }
                case "SNAM" when sub.DataLength == 4:
                    soundFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "DATA" when sub.DataLength == 2:
                    damage = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt16BigEndian(subData)
                        : BinaryPrimitives.ReadUInt16LittleEndian(subData);
                    break;
                // Oblivion stores its water visual data in DATA (a ~102-byte struct), not the FNV DNAM.
                // Game-gated rather than length-gated because FNV's WATR also has a large DATA variant
                // (the DNAM-or-DATA visual union, ~186 bytes) with a different field layout. Without this
                // every Oblivion water renders with the default tint (all 23 Oblivion WATR have no DNAM).
                case "DATA" when Context.Game == BethesdaGame.Oblivion && sub.DataLength >= 100:
                {
                    visualProps = ReadOblivionWaterData(subData, record.IsBigEndian);
                    if (sub.DataLength >= 102)
                    {
                        damage = record.IsBigEndian
                            ? BinaryPrimitives.ReadUInt16BigEndian(subData.Slice(100, 2))
                            : BinaryPrimitives.ReadUInt16LittleEndian(subData.Slice(100, 2));
                    }
                    break;
                }
                // Skyrim WATR DNAM (xEdit wbDefinitionsTES5): a longer struct (~228 bytes LE / 232 SSE)
                // whose colors are shifted +4 vs FNV (an extra float at +28) and which adds Skyrim-specific
                // specular/noise fields. Game-gated because its length (≠196) and layout differ from FNV's
                // DNAM; without this every Skyrim water falls back to the default tint (the FNV 196-byte case
                // below never matches Skyrim). Grounded in the disassembled Skyrim water PS
                // (tools/GhidraProject/skyrim_water_pixel_shader_decompiled.txt).
                case "DNAM" when Context.Game == BethesdaGame.Skyrim && sub.DataLength >= 52:
                    visualProps = ReadSkyrimWaterData(subData, record.IsBigEndian);
                    break;
                // FO4 WATR DNAM (xEdit wbDefinitionsFO4, 201 bytes; layout byte-verified against retail
                // Fallout4.esm — ExtLakeQuannapowittWater etc.): Fog block (Depth Amount@0, Shallow@4/
                // Deep@8 byte-colors, color ranges@12/16, alphas@20/24 + ranges@28/32, underwater@36..),
                // Physical (Normal Magnitude@52, Reflectivity@64, Fresnel@68, Reflection color@96),
                // Specular (Sun Specular Power@100 / Magnitude@104), Noise (3 layers grouped BY FIELD:
                // WindDir@128.., WindSpeed@140.., Amplitude@152.., UVScale@164..), Silt (Amount@188,
                // Light@192/Dark@196 colors), SSR bool@200. Game-gated: the length overlaps no other
                // game's DNAM, but the field layout is FO4's alone.
                case "DNAM" when Context.Game == BethesdaGame.Fallout4 && sub.DataLength >= 200:
                    visualProps = ReadFallout4WaterData(subData, record.IsBigEndian);
                    break;
                // FO76 preserves FO4's Creation-era fog/physical/specular prefix through byte 107,
                // then ends after a shorter game-specific tail (148 bytes in current retail records).
                // Its shipped Shaders012.fxp Water group retains the FO4 lighting architecture, so
                // surface exactly the shared fields without reading FO4's absent noise/silt tail.
                case "DNAM" when Context.Game == BethesdaGame.Fallout76 && sub.DataLength >= 108:
                    visualProps = ReadFallout76WaterData(subData, record.IsBigEndian);
                    break;
                // Skyrim's active set and FO4/FO76's only set ship as NAM2/NAM3/NAM4 zstrings
                // (layer 1 first). Surface layer 1 as the compatibility noise texture, stripping
                // the "data\" prefix authors use so the cache resolves it normally. The profile
                // flag deliberately excludes Starfield: its WATR layout is unverified.
                case "NAM2" or "NAM3" or "NAM4"
                    when GameProfiles.For(Context.Game).HasVerifiedModernWatrLayout:
                {
                    var texture = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(texture))
                    {
                        texture = texture.StartsWith("data\\", StringComparison.OrdinalIgnoreCase)
                            ? texture[5..]
                            : texture;
                        normalTextures.Add(texture);
                        noiseTexture ??= texture;
                    }

                    break;
                }
                case "DNAM" when sub.DataLength == 196:
                {
                    if (SubrecordSchemaView.TryRead("DNAM", "WATR", subData, record.IsBigEndian) is { } v)
                    {
                        visualProps = v.Raw;
                    }

                    break;
                }
                case "GNAM" when sub.DataLength == 12:
                {
                    if (SubrecordSchemaView.TryRead("GNAM", "WATR", subData, record.IsBigEndian) is { } v)
                    {
                        relatedWater = v.Raw;
                    }

                    break;
                }
            }
        }

        // Compatibility for old Skyrim records that carry only repeated NNAM. Named layers take
        // authority whenever they are present, but no authored-only-NNAM record becomes textureless.
        if (normalTextures.Count == 0 && legacyNormalTextures.Count > 0)
        {
            normalTextures.AddRange(legacyNormalTextures);
            noiseTexture = normalTextures[0];
        }

        return new WaterRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            FullName = fullName,
            NoiseTexture = noiseTexture,
            SurfaceTexture = surfaceTexture,
            NormalTextures = normalTextures,
            LegacyNormalTextures = legacyNormalTextures,
            Opacity = opacity,
            WaterFlags = waterFlags,
            SoundFormId = soundFormId != 0 ? soundFormId : null,
            Damage = damage,
            VisualProperties = visualProps,
            RelatedWater = relatedWater,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    // Oblivion WATR DATA struct (xEdit wbDefinitionsTES4): 9 leading floats (wind/wave/SunPower@16/
    // Reflectivity@20/Fresnel@24/scroll), FogDistance Near@36/Far@40, then the three wbByteColors at
    // 44/48/52 (Shallow/Deep/Reflection), texture-blend, rain/displacement sims, damage. The three colors
    // + the key surface scalars are surfaced under the SAME dictionary keys WaterAppearance reads from the
    // FNV DNAM, so the shared color/surface decoder handles Oblivion unchanged. Colors are a byte sequence
    // (R,G,B,A) packed R|G<<8|B<<16 (endian-independent); the scalars are endian-aware floats.
    internal static Dictionary<string, object?> ReadOblivionWaterData(ReadOnlySpan<byte> d, bool isBigEndian)
    {
        static uint Color(ReadOnlySpan<byte> d, int off) =>
            (uint)(d[off] | (d[off + 1] << 8) | (d[off + 2] << 16));

        return new Dictionary<string, object?>
        {
            ["WindVelocity"] = ReadFloat(d, 0, isBigEndian),
            ["WindDirection"] = ReadFloat(d, 4, isBigEndian),
            ["WaveAmplitude"] = ReadFloat(d, 8, isBigEndian),
            ["WaveFrequency"] = ReadFloat(d, 12, isBigEndian),
            ["SunPower"] = ReadFloat(d, 16, isBigEndian),
            ["ReflectivityAmount"] = ReadFloat(d, 20, isBigEndian),
            ["FresnelAmount"] = ReadFloat(d, 24, isBigEndian),
            ["ScrollXSpeed"] = ReadFloat(d, 28, isBigEndian),
            ["ScrollYSpeed"] = ReadFloat(d, 32, isBigEndian),
            ["FogNear"] = ReadFloat(d, 36, isBigEndian),
            ["FogFar"] = ReadFloat(d, 40, isBigEndian),
            ["ShallowColor"] = Color(d, 44),
            ["DeepColor"] = Color(d, 48),
            ["ReflectionColor"] = Color(d, 52),
            ["TextureBlend"] = d[56] / 100f,
            ["RainForce"] = ReadFloat(d, 60, isBigEndian),
            ["RainVelocity"] = ReadFloat(d, 64, isBigEndian),
            ["RainFalloff"] = ReadFloat(d, 68, isBigEndian),
            ["RainDampener"] = ReadFloat(d, 72, isBigEndian),
            ["RainStartingSize"] = ReadFloat(d, 76, isBigEndian),
            ["DisplacementForce"] = ReadFloat(d, 80, isBigEndian),
            ["DisplacementVelocity"] = ReadFloat(d, 84, isBigEndian),
            ["DisplacementFalloff"] = ReadFloat(d, 88, isBigEndian),
            ["DisplacementDampener"] = ReadFloat(d, 92, isBigEndian),
            ["DisplacementStartingSize"] = ReadFloat(d, 96, isBigEndian),
        };
    }

    // Skyrim WATR DNAM struct (xEdit wbDefinitionsTES5, little-endian). Leading: 4 unused wind/wave
    // floats, then Sun Specular Power@16, Reflectivity@20, Fresnel@24, an extra float@28, fog Above
    // Near@32/Far@36; then the three wbByteColors (R,G,B,A) at 40/44/48 (Shallow/Deep/Reflection) —
    // shifted +4 from FNV by that float@28. Later: noise Wind Direction@100.., Wind Speed@112..,
    // under-water fog Near@144/Far@148 (the depth-fade analog → DepthFalloff), Specular Power@156
    // (the sun-spec exponent → Shininess), noise UV Scale@172.., Amplitude@184... Colors are packed
    // R|G<<8|B<<16 (endian-independent); scalars are endian-aware floats. Deeper fields exist only in
    // a full (non-truncated, optionalFromElement=36) DNAM, so each is added only when in-bounds.
    // Surfaced under the SAME dictionary keys WaterAppearance reads (see ReadOblivionWaterData).
    internal static Dictionary<string, object?> ReadSkyrimWaterData(ReadOnlySpan<byte> d, bool isBigEndian)
    {
        static uint Color(ReadOnlySpan<byte> d, int off) =>
            (uint)(d[off] | (d[off + 1] << 8) | (d[off + 2] << 16));

        var props = new Dictionary<string, object?>
        {
            ["SunPower"] = ReadFloat(d, 16, isBigEndian),
            ["ReflectivityAmount"] = ReadFloat(d, 20, isBigEndian),
            ["FresnelAmount"] = ReadFloat(d, 24, isBigEndian),
            // Above-water fog Near@32/Far@36 — the same canonical keys the classic water shader's
            // alpha fog law reads, so Skyrim water runs on its own authored planes rather than the
            // FNV NVCleanWater defaults.
            ["FogNear"] = ReadFloat(d, 32, isBigEndian),
            ["FogFar"] = ReadFloat(d, 36, isBigEndian),
            ["ShallowColor"] = Color(d, 40),
            ["DeepColor"] = Color(d, 44),
            ["ReflectionColor"] = Color(d, 48),
        };

        // Static local (the ReadOnlySpan must be passed, not captured): adds a float only when the
        // record is long enough to carry it (truncated DNAMs keep only up to the colors).
        static void Add(Dictionary<string, object?> props, ReadOnlySpan<byte> d, string key, int off, bool be)
        {
            if (off + 4 <= d.Length) props[key] = ReadFloat(d, off, be);
        }

        Add(props, d, "NoiseLayer1WindDir", 100, isBigEndian);
        Add(props, d, "NoiseLayer2WindDir", 104, isBigEndian);
        Add(props, d, "NoiseLayer3WindDir", 108, isBigEndian);
        Add(props, d, "NoiseLayer1WindSpeed", 112, isBigEndian);
        Add(props, d, "NoiseLayer2WindSpeed", 116, isBigEndian);
        Add(props, d, "NoiseLayer3WindSpeed", 120, isBigEndian);
        Add(props, d, "DepthFalloffStart", 144, isBigEndian);
        Add(props, d, "DepthFalloffEnd", 148, isBigEndian);
        Add(props, d, "Shininess", 156, isBigEndian);
        Add(props, d, "NoiseLayer1UVScale", 172, isBigEndian);
        Add(props, d, "NoiseLayer2UVScale", 176, isBigEndian);
        Add(props, d, "NoiseLayer3UVScale", 180, isBigEndian);
        Add(props, d, "NoiseLayer1AmpScale", 184, isBigEndian);
        Add(props, d, "NoiseLayer2AmpScale", 188, isBigEndian);
        Add(props, d, "NoiseLayer3AmpScale", 192, isBigEndian);
        return props;
    }

    // FO4 WATR DNAM (201 bytes; see the game-gated case above for the layout provenance). The shared
    // canonical keys (ShallowColor/DeepColor/ReflectionColor, ReflectivityAmount, FresnelAmount,
    // SunPower, NoiseLayerN*) feed the same WaterAppearance decode as every other game; the FO4-only
    // keys (alpha/color depth ranges, Sun Specular Magnitude, silt) drive the FO4 water shader variant
    // (WaterShaderVariant.Fo4Water — tools/GhidraProject/fo4_water_pixel_shader_decompiled.txt).
    internal static Dictionary<string, object?> ReadFallout4WaterData(ReadOnlySpan<byte> d, bool isBigEndian)
    {
        var properties = ReadCreationWaterDataPrefix(d, isBigEndian);
        properties["NoiseLayer1WindDir"] = ReadFloat(d, 128, isBigEndian);
        properties["NoiseLayer2WindDir"] = ReadFloat(d, 132, isBigEndian);
        properties["NoiseLayer3WindDir"] = ReadFloat(d, 136, isBigEndian);
        properties["NoiseLayer1WindSpeed"] = ReadFloat(d, 140, isBigEndian);
        properties["NoiseLayer2WindSpeed"] = ReadFloat(d, 144, isBigEndian);
        properties["NoiseLayer3WindSpeed"] = ReadFloat(d, 148, isBigEndian);
        properties["NoiseLayer1AmpScale"] = ReadFloat(d, 152, isBigEndian);
        properties["NoiseLayer2AmpScale"] = ReadFloat(d, 156, isBigEndian);
        properties["NoiseLayer3AmpScale"] = ReadFloat(d, 160, isBigEndian);
        properties["NoiseLayer1UVScale"] = ReadFloat(d, 164, isBigEndian);
        properties["NoiseLayer2UVScale"] = ReadFloat(d, 168, isBigEndian);
        properties["NoiseLayer3UVScale"] = ReadFloat(d, 172, isBigEndian);
        properties["NoiseLayer1Falloff"] = ReadFloat(d, 176, isBigEndian);
        properties["NoiseLayer2Falloff"] = ReadFloat(d, 180, isBigEndian);
        properties["NoiseLayer3Falloff"] = ReadFloat(d, 184, isBigEndian);
        properties["SiltAmount"] = ReadFloat(d, 188, isBigEndian);
        properties["LightSiltColor"] = ReadWaterColor(d, 192);
        properties["DarkSiltColor"] = ReadWaterColor(d, 196);
        if (d.Length > 200) properties["ScreenSpaceReflections"] = d[200] != 0;
        return properties;
    }

    /// <summary>
    ///     FO76 WATR DNAM. Retail records are 148 bytes and preserve FO4's fog, physical, and
    ///     specular prefix exactly through byte 107; the later FO4 noise/silt block is absent.
    /// </summary>
    internal static Dictionary<string, object?> ReadFallout76WaterData(ReadOnlySpan<byte> d, bool isBigEndian)
    {
        var properties = ReadCreationWaterDataPrefix(d, isBigEndian);
        // The retail 148-byte layout names these five trailing floats Unknown 1..5. Keep them
        // losslessly without assigning FO4's incompatible noise-layer semantics.
        AddOptionalCreationFloat(properties, d, "ModernUnknown1", 128, isBigEndian);
        AddOptionalCreationFloat(properties, d, "ModernUnknown2", 132, isBigEndian);
        AddOptionalCreationFloat(properties, d, "ModernUnknown3", 136, isBigEndian);
        AddOptionalCreationFloat(properties, d, "ModernUnknown4", 140, isBigEndian);
        AddOptionalCreationFloat(properties, d, "ModernUnknown5", 144, isBigEndian);
        return properties;
    }

    private static Dictionary<string, object?> ReadCreationWaterDataPrefix(
        ReadOnlySpan<byte> d,
        bool isBigEndian)
    {
        var properties = new Dictionary<string, object?>
        {
            // Depth Amount and the four range fields are retained as separately-authored values.
            // The exact t15 LUT generator is still open; do not collapse the range fields into
            // multipliers or normalize them during parsing.
            ["DepthAmount"] = ReadFloat(d, 0, isBigEndian),
            ["ShallowColor"] = ReadWaterColor(d, 4),
            ["DeepColor"] = ReadWaterColor(d, 8),
            ["ColorShallowRange"] = ReadFloat(d, 12, isBigEndian),
            ["ColorDeepRange"] = ReadFloat(d, 16, isBigEndian),
            ["ShallowAlpha"] = ReadFloat(d, 20, isBigEndian),
            ["DeepAlpha"] = ReadFloat(d, 24, isBigEndian),
            ["AlphaShallowRange"] = ReadFloat(d, 28, isBigEndian),
            ["AlphaDeepRange"] = ReadFloat(d, 32, isBigEndian),
            ["UnderwaterColor"] = ReadWaterColor(d, 36),
            ["UnderwaterFogAmount"] = ReadFloat(d, 40, isBigEndian),
            ["UnderwaterFogNear"] = ReadFloat(d, 44, isBigEndian),
            ["UnderwaterFogFar"] = ReadFloat(d, 48, isBigEndian),
            ["NormalMagnitude"] = ReadFloat(d, 52, isBigEndian),
            ["ShallowNormalFalloff"] = ReadFloat(d, 56, isBigEndian),
            ["DeepNormalFalloff"] = ReadFloat(d, 60, isBigEndian),
            ["ReflectivityAmount"] = ReadFloat(d, 64, isBigEndian),
            ["FresnelAmount"] = ReadFloat(d, 68, isBigEndian),
            ["SurfaceEffectFalloff"] = ReadFloat(d, 72, isBigEndian),
            ["DisplacementForce"] = ReadFloat(d, 76, isBigEndian),
            ["DisplacementVelocity"] = ReadFloat(d, 80, isBigEndian),
            ["DisplacementFalloff"] = ReadFloat(d, 84, isBigEndian),
            ["DisplacementDampener"] = ReadFloat(d, 88, isBigEndian),
            ["DisplacementStartingSize"] = ReadFloat(d, 92, isBigEndian),
            ["ReflectionColor"] = ReadWaterColor(d, 96),
            // Sun Specular Power/Magnitude: the FO4 shader's Blinn exponent + amplitude sources
            // (engine feeds them through the gloss pipeline; the DNAM values are the authored analogs).
            ["SunPower"] = ReadFloat(d, 100, isBigEndian),
            ["SunSpecularMagnitude"] = ReadFloat(d, 104, isBigEndian),
        };
        AddOptionalCreationFloat(properties, d, "SunSparklePower", 108, isBigEndian);
        AddOptionalCreationFloat(properties, d, "SunSparkleMagnitude", 112, isBigEndian);
        AddOptionalCreationFloat(properties, d, "InteriorSpecularRadius", 116, isBigEndian);
        AddOptionalCreationFloat(properties, d, "InteriorSpecularBrightness", 120, isBigEndian);
        AddOptionalCreationFloat(properties, d, "InteriorSpecularPower", 124, isBigEndian);
        return properties;
    }

    private static void AddOptionalCreationFloat(
        Dictionary<string, object?> properties,
        ReadOnlySpan<byte> data,
        string key,
        int offset,
        bool isBigEndian)
    {
        if (offset + sizeof(float) <= data.Length)
            properties[key] = ReadFloat(data, offset, isBigEndian);
    }

    private static uint ReadWaterColor(ReadOnlySpan<byte> d, int offset) =>
        (uint)(d[offset] | (d[offset + 1] << 8) | (d[offset + 2] << 16));

    #endregion

    #region Weather

    /// <summary>
    ///     Parse all Weather (WTHR) records.
    /// </summary>
    internal List<WeatherRecord> ParseWeather()
    {
        return ParseRecordList("WTHR", 4096,
            ParseWeatherFromAccessor,
            record => new WeatherRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });
    }

    private WeatherRecord? ParseWeatherFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new WeatherRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        var sounds = new List<WeatherSound>();
        var cloudLayers = new SortedDictionary<int, string>();
        List<WeatherColor>? colors = null;
        IReadOnlyList<WeatherColor>? cloudColors = null;
        List<WeatherAmbientCube>? directionalAmbientBands = null;
        IReadOnlyList<WeatherCloudAlpha>? cloudAlphas = null;
        float[]? cloudSpeedsX = null;
        float[]? cloudSpeedsY = null;
        IReadOnlyList<float>? fogDistances = null;
        WeatherData? weatherData = null;
        WeatherHdr? weatherHdr = null;
        WeatherColor? sunGlareColor = null;
        WeatherColor? moonGlareColor = null;
        WeatherTimeBands<uint>? imageSpaceModifiers = null;
        // Nullable slots preserve the distinction between an absent optional band (FO3 only
        // authors 00IAD..03IAD) and an explicitly authored null FormID. The evaluator falls back
        // from an absent HighNoon band to Day, while an authored zero retains the engine's neutral
        // default modifier for that slot.
        var legacyImageSpaceIds = new uint?[6];
        var hasLegacyImageSpaceIds = false;

        // NAM0/PNAM per-category stride. Two distinct NAM0 layouts:
        //  • FO3/FNV/Oblivion: a fixed 10 color categories; only the band count varies (FO3=4, FNV=6),
        //    so it's read structurally from NAM0's length (DetectWeatherBands).
        //  • Skyrim/FO4/FO76/SF1: a wbWeatherTimeOfDay struct per category whose width is form-versioned
        //    (FO4/FO76/SF1 widen 4→8 RGBA bands at form version 111) and whose category COUNT grows with
        //    form version (10→19) — so the /10 structural divide is invalid. Key the stride off the game +
        //    form version instead and read whatever categories fit (base lighting rows plus Skyrim far fog 12).
        var modernWeather = GameProfiles.For(Context.Game).HasModernWeatherLayout;
        var formVersion = ReadRecordFormVersion(record);
        var modernStride = modernWeather ? ModernWeatherStride(Context.Game, formVersion) : 0;
        var weatherBands = modernWeather ? 0 : DetectWeatherBands(data, dataSize, record.IsBigEndian);

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                // FO3 authors four scalar cloud speeds in ONAM. Some transitional FNV records retain
                // that layout, so treat it as cloud data for both games rather than a FormID.
                case "ONAM" when GameProfiles.For(Context.Game).HasOnamCloudSpeeds:
                    cloudSpeedsX = ReadCloudSpeeds(subData, record.IsBigEndian, Context.Game);
                    break;
                // FO3/FNV store their per-band IMAD references as binary signatures \0IAD..\5IAD.
                case var iadSig when TryWeatherImageSpaceAdapterIndex(iadSig, out var iadIndex)
                                     && sub.DataLength == 4:
                    legacyImageSpaceIds[iadIndex] = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    hasLegacyImageSpaceIds = true;
                    break;
                case "SNAM" when sub.DataLength == 8:
                {
                    if (SubrecordSchemaView.TryRead("SNAM", "WTHR", subData, record.IsBigEndian) is { } v)
                    {
                        sounds.Add(new WeatherSound
                        {
                            SoundFormId = v.UInt32("Sound"),
                            Type = v.UInt32("Type")
                        });
                    }

                    break;
                }
                // Cloud-layer texture paths (null-terminated strings, big-endian-safe — the converter
                // leaves these unswapped), stored by layer index. FO3/FNV use DNAM/CNAM/ANAM/BNAM (layers
                // 0–3); Skyrim/FO4/FO76/SF1 use the ?0TX scheme (layer N = (char)(0x30+N) + "0TX", up to 29
                // layers) — both per xEdit's wbWeatherCloudTextures. Recognized by SIGNATURE (structural, no
                // game gate); the active weather's clouds derive from these.
                case var cloudSig when TryCloudLayerIndex(cloudSig, Context.Game, out var cloudLayer):
                {
                    var cloud = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrWhiteSpace(cloud))
                    {
                        cloudLayers[cloudLayer] = cloud;
                    }

                    break;
                }
                // NAM0 "Weather Colors": 10 categories × 24 bytes (FNV), each category being SIX RGBA
                // time bands (Sunrise/Day/Sunset/Night/HighNoon/Midnight) per the fopdoc FalloutNV WTHR
                // definition. Drives the atmosphere renderer's sky/sun/ambient/fog palette (see
                // WeatherColorType for the index meaning).
                case "NAM0" when sub.DataLength >= 16:
                    colors = modernWeather
                        ? ReadWeatherColorsModern(subData, record.IsBigEndian, modernStride)
                        : ReadWeatherColors(subData, record.IsBigEndian, weatherBands);
                    break;
                // PNAM "Cloud Colors" (xEdit wbWeatherCloudColors): one Time-of-Day color PER CLOUD LAYER —
                // the SAME 24-byte 6-band RGBA struct as a NAM0 category. The engine uploads this per layer
                // as the cloud shader's per-draw color/opacity uniform (SkyShader::SetupGeometryConstants,
                // MemDebug XEX) — so it tints the cloud sheet and its alpha is the layer opacity per band.
                case "PNAM" when sub.DataLength >= 16:
                    cloudColors = modernWeather
                        ? ReadWeatherColorsModern(subData, record.IsBigEndian, modernStride)
                        : ReadWeatherColors(subData, record.IsBigEndian, weatherBands);
                    break;
                // JNAM "Cloud Alphas" (Skyrim/FO4/FO76/SF1; xEdit wbWeatherCloudAlphas): per-cloud-layer
                // per-time-of-day OPACITY floats — the engine's real per-layer cloud opacity (PNAM's alpha
                // byte is unused). Modern-only; FO3/FNV carry no JNAM. Stride is form-versioned exactly like
                // the colors (FO4/76/SF1 widen 4→8 float bands at version 111); every authored band is
                // retained. Floats are endian-aware so Xbox (byte-swapped) and PC agree.
                case "JNAM" when modernWeather && sub.DataLength >= 16:
                    cloudAlphas = ReadCloudAlphas(subData, record.IsBigEndian,
                        ModernCloudAlphaStride(Context.Game, formVersion));
                    break;
                // QNAM/RNAM "X/Y Cloud Speeds" (xEdit wbWeatherCloudSpeed): the per-layer per-axis UV
                // scroll rate Clouds::Update accumulates. Every supported generation authors ONE UNSIGNED
                // byte per layer biased around 127 (127 = still), including FO4/FO76. Treating modern bytes
                // as floats collapsed four authored layer identities into each decoded value.
                case "QNAM":
                    cloudSpeedsX = ReadCloudSpeeds(subData, record.IsBigEndian, Context.Game);
                    break;
                case "RNAM":
                    cloudSpeedsY = ReadCloudSpeeds(subData, record.IsBigEndian, Context.Game);
                    break;
                // Skyrim+ stores four/eight per-time-band IMGS FormIDs in one packed IMSP block.
                case "IMSP" when modernWeather && sub.DataLength >= 16:
                    imageSpaceModifiers = ReadWeatherImageSpaces(subData, record.IsBigEndian,
                        modernStride >= 32 ? 8 : 4);
                    break;
                // DALC "Directional Ambient Lighting Colors" (xEdit wbAmbientColors): six RGBA byte
                // colors (X+/X−/Y+/Y−/Z+/Z−) + specular RGBA + fresnel float — ONE SUBRECORD PER
                // TIME BAND, in NAM0 band order (Skyrim 4; FO4/FO76 8 at form version 111+).
                // Skyrim+ engines light exteriors from this cube, not the NAM0 Ambient row; each
                // band reduces to the directions' mean for the single-ambient atmosphere.
                // Structural — no FO3/FNV record carries DALC.
                case "DALC" when sub.DataLength >= 24:
                    (directionalAmbientBands ??= []).Add(ReadDirectionalAmbientCube(subData, record.IsBigEndian));
                    break;
                // Oblivion carries its HDR settings on each WTHR, not in IMGS.
                case "HNAM" when Context.Game == BethesdaGame.Oblivion && sub.DataLength >= 56:
                    weatherHdr = ReadWeatherHdr(subData, record.IsBigEndian);
                    break;
                // Skyrim stores these four authored RGB time bands outside NAM0. The fourth byte of
                // each color is padding, not alpha; normalize it to opaque before billboard sampling.
                case "NAM2" when Context.Game == BethesdaGame.Skyrim && sub.DataLength >= 16:
                    sunGlareColor = ReadWeatherRgbRow(subData, record.IsBigEndian);
                    break;
                case "NAM3" when Context.Game == BethesdaGame.Skyrim && sub.DataLength >= 16:
                    moonGlareColor = ReadWeatherRgbRow(subData, record.IsBigEndian);
                    break;
                // FNAM fog scalars: day/night near + far, optional power pair, and Skyrim max-opacity pair.
                case "FNAM" when sub.DataLength >= 4:
                    fogDistances = ReadFogDistances(subData, record.IsBigEndian);
                    break;
                // DATA: 15-byte struct (wind speed, sun glare, precip timing, flags, lightning color).
                case "DATA" when sub.DataLength >= 15:
                    weatherData = ReadWeatherData(subData, Context.Game);
                    break;
            }
        }

        if (imageSpaceModifiers is null && hasLegacyImageSpaceIds)
        {
            imageSpaceModifiers = new WeatherTimeBands<uint>(
                legacyImageSpaceIds[0].GetValueOrDefault(), legacyImageSpaceIds[1].GetValueOrDefault(),
                legacyImageSpaceIds[2].GetValueOrDefault(), legacyImageSpaceIds[3].GetValueOrDefault())
            {
                HighNoon = legacyImageSpaceIds[4],
                Midnight = legacyImageSpaceIds[5],
            };
        }

        // TES4 keeps the two cloud speeds in DATA and names its textures Lower=CNAM / Upper=DNAM.
        if (Context.Game == BethesdaGame.Oblivion && weatherData is { } tes4Data)
        {
            cloudSpeedsX ??=
            [
                NormalizeLegacyCloudSpeedByte(tes4Data.CloudSpeedLower ?? 0),
                NormalizeLegacyCloudSpeedByte(tes4Data.CloudSpeedUpper ?? 0),
            ];
        }

        // TES4 also stores its per-layer cloud colors INSIDE NAM0 — categories 2 "Clouds-Lower" and
        // 9 "Clouds-Upper" (xEdit wbWeatherColors IsTES4 arms; FO3+ repurpose those slots as Unused
        // and moved cloud colors to the per-layer PNAM parsed above). Route them into the same
        // cloudColors slots (index 0 → CNAM lower layer, 1 → DNAM upper layer) so the sky renderer
        // samples the authored time-of-day bands — without this every Oblivion cloud layer fell back
        // to the white tint and glowed at night.
        if (Context.Game == BethesdaGame.Oblivion && cloudColors is null && colors is { Count: >= 10 })
        {
            cloudColors = [colors[2], colors[9]];
        }

        IReadOnlyList<string> textureValues = cloudLayers.Count == 0 ? [] : cloudLayers.Values.ToList();
        IReadOnlyList<int> textureIndices = cloudLayers.Count == 0 ? [] : cloudLayers.Keys.ToList();
        var ambientBands = BuildDirectionalAmbientBands(directionalAmbientBands);
        var authoritativeCloudLayers = BuildCloudLayers(
            cloudLayers, cloudSpeedsX, cloudSpeedsY, cloudColors, cloudAlphas);

        return new WeatherRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            ImageSpaceModifier = imageSpaceModifiers is { Day: not 0 } ? imageSpaceModifiers.Day : null,
            ImageSpaceModifiers = imageSpaceModifiers,
            Sounds = sounds,
            // Present cloud layers in source-index order. The texture list remains dense for the sky-dome
            // shape walk, but retain every original sparse index so QNAM/RNAM/PNAM/JNAM use their authored
            // array slot (SkyrimClear jumps from source layer 7 to 15/19/20/28).
            CloudLayerTextures = textureValues,
            CloudLayerSourceIndices = textureIndices,
            CloudLayers = authoritativeCloudLayers,
            Colors = colors ?? [],
            DirectionalAmbient = BuildDirectionalAmbient(ambientBands),
            DirectionalAmbientCubes = ambientBands,
            CloudColors = cloudColors ?? [],
            CloudLayerAlphas = cloudAlphas ?? [],
            CloudSpeedsX = cloudSpeedsX ?? [],
            CloudSpeedsY = cloudSpeedsY ?? [],
            FogDistances = fogDistances ?? [],
            Data = weatherData,
            Hdr = weatherHdr,
            SunGlareColor = sunGlareColor,
            MoonGlareColor = moonGlareColor,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    // Maps a WTHR cloud-texture subrecord signature to its layer index, per xEdit's wbWeatherCloudTextures.
    // FO3/FNV: DNAM/CNAM/ANAM/BNAM = layers 0–3. Skyrim/FO4/FO76/SF1: the ?0TX scheme — signature is
    // (char)(0x30 + layer) + "0TX" for layers 0–28 (0x30..0x40 then 'A'..'L'). Structural (keyed off the
    // actual signature), so no game gate is needed — no FO3/FNV record carries ?0TX, and Skyrim+ weathers
    // that ship the legacy DNAM/CNAM/ANAM/BNAM map them to the same layers, overwritten by ?0TX if present.
    internal static bool TryCloudLayerIndex(string signature, out int layer)
        => TryCloudLayerIndex(signature, BethesdaGame.Unknown, out layer);

    internal static bool TryCloudLayerIndex(string signature, BethesdaGame game, out int layer)
    {
        // Oblivion names the lower cloud texture CNAM and upper DNAM. Later engines reverse those
        // two legacy signatures (DNAM layer 0 / CNAM layer 1).
        if (game == BethesdaGame.Oblivion)
        {
            if (signature == "CNAM") { layer = 0; return true; }
            if (signature == "DNAM") { layer = 1; return true; }
        }

        switch (signature)
        {
            case "DNAM": layer = 0; return true;
            case "CNAM": layer = 1; return true;
            case "ANAM": layer = 2; return true;
            case "BNAM": layer = 3; return true;
        }

        if (signature.Length == 4 && signature[1] == '0' && signature[2] == 'T' && signature[3] == 'X')
        {
            var n = signature[0] - 0x30;
            if (n is >= 0 and <= 28)
            {
                layer = n;
                return true;
            }
        }

        layer = -1;
        return false;
    }

    /// <summary>Maps the binary FO3/FNV WTHR signatures \0IAD..\5IAD to semantic band slots.</summary>
    internal static bool TryWeatherImageSpaceAdapterIndex(string signature, out int index)
    {
        if (signature.Length == 4 && signature[1] == 'I' && signature[2] == 'A' && signature[3] == 'D'
            && signature[0] is >= '\0' and <= '\u0005')
        {
            index = signature[0];
            return true;
        }

        index = -1;
        return false;
    }

    // Weather color categories (NAM0) and per-layer cloud colors (PNAM). New Vegas stores SIX RGBA time
    // bands per entry (Sunrise/Day/Sunset/Night/HighNoon/Midnight = 24 bytes); Fallout 3 — the format the
    // earliest crash dumps carry — stores only FOUR (Sunrise/Day/Sunset/Night = 16 bytes), since FNV added
    // High Noon + Midnight (xEdit wbWeatherColors). The band count is read structurally from the data via
    // <see cref="DetectWeatherBands" /> rather than assumed, so FO3-era records don't drift 8 bytes per
    // category (which read the sky horizon black at noon / white at night). Each 4-byte band is read with
    // the record's endianness as one uint and the RGBA bytes extracted from fixed bit positions, so Xbox
    // (the converter byte-swaps each group) and PC produce the same RGBA.
    internal static List<WeatherColor> ReadWeatherColors(ReadOnlySpan<byte> data, bool isBigEndian, int bandsPerEntry)
    {
        const int bandBytes = 4;
        if (bandsPerEntry < 4)
        {
            bandsPerEntry = 6; // fall back to the FNV layout if the count can't be determined
        }

        var entryBytes = bandBytes * bandsPerEntry;
        var count = data.Length / entryBytes;
        var colors = new List<WeatherColor>(count);
        for (var i = 0; i < count; i++)
        {
            var b = i * entryBytes;
            var sunrise = ReadRgba(data.Slice(b, bandBytes), isBigEndian);
            var day = ReadRgba(data.Slice(b + bandBytes, bandBytes), isBigEndian);
            var sunset = ReadRgba(data.Slice(b + 2 * bandBytes, bandBytes), isBigEndian);
            var night = ReadRgba(data.Slice(b + 3 * bandBytes, bandBytes), isBigEndian);
            if (bandsPerEntry >= 6)
            {
                colors.Add(new WeatherColor(sunrise, day, sunset, night,
                    ReadRgba(data.Slice(b + 4 * bandBytes, bandBytes), isBigEndian),
                    ReadRgba(data.Slice(b + 5 * bandBytes, bandBytes), isBigEndian)));
            }
            else
            {
                colors.Add(new WeatherColor(sunrise, day, sunset, night));
            }
        }

        return colors;
    }

    // Skyrim/FO4/FO76/SF1 NAM0 + PNAM categories: each is a wbWeatherTimeOfDay struct of FOUR RGBA bands
    // (Sunrise/Day/Sunset/Night = 16 bytes). FO4/FO76/SF1 form version 111+ append FOUR more bands
    // (Early/Late Sunrise + Early/Late Sunset = 32 bytes total) per xEdit's wbWeatherTimeOfDay. Those extra
    // bands are semantic transition colors with no engine "High Noon"/"Midnight" analogue and are
    // retained explicitly. Unlike FO3/FNV, the category COUNT here is
    // form-version dependent (10→19), so the stride is taken as given rather than derived from the length;
    // the atmosphere renderer consumes the base lighting rows plus Skyrim's far-fog category 12; those
    // ordinals are stable across the modern games. Reading whatever fits at the correct stride is therefore
    // sufficient. Each band is one endian-aware uint with RGBA from fixed bit slots.
    internal static List<WeatherColor> ReadWeatherColorsModern(ReadOnlySpan<byte> data, bool isBigEndian, int strideBytes)
    {
        const int bandBytes = 4;
        if (strideBytes < 4 * bandBytes)
        {
            strideBytes = 4 * bandBytes; // never read fewer than the four base bands
        }

        var count = data.Length / strideBytes;
        var colors = new List<WeatherColor>(count);
        for (var i = 0; i < count; i++)
        {
            var b = i * strideBytes;
            var sunrise = ReadRgba(data.Slice(b, bandBytes), isBigEndian);
            var day = ReadRgba(data.Slice(b + bandBytes, bandBytes), isBigEndian);
            var sunset = ReadRgba(data.Slice(b + 2 * bandBytes, bandBytes), isBigEndian);
            var night = ReadRgba(data.Slice(b + 3 * bandBytes, bandBytes), isBigEndian);
            var bands = new WeatherTimeBands<WeatherRgba>(sunrise, day, sunset, night);
            if (strideBytes >= 8 * bandBytes)
            {
                bands = bands with
                {
                    EarlySunrise = ReadRgba(data.Slice(b + 4 * bandBytes, bandBytes), isBigEndian),
                    LateSunrise = ReadRgba(data.Slice(b + 5 * bandBytes, bandBytes), isBigEndian),
                    EarlySunset = ReadRgba(data.Slice(b + 6 * bandBytes, bandBytes), isBigEndian),
                    LateSunset = ReadRgba(data.Slice(b + 7 * bandBytes, bandBytes), isBigEndian),
                };
            }

            colors.Add(new WeatherColor(bands));
        }

        return colors;
    }

    // The widening game set and threshold (form version 111, xEdit wbWeatherTimeOfDay's
    // wbFromVersion(111, …)) live on GameProfile so the NAM0/PNAM color and JNAM cloud-alpha
    // strides can't drift from each other or from the profile registry.
    internal static bool HasWideTimeOfDayBands(BethesdaGame game, int formVersion) =>
        GameProfiles.For(game).WideTimeOfDayBandsFormVersion is { } widensAt && formVersion >= widensAt;

    // Per-category NAM0/PNAM stride for the version-trailered games. Stride = bands × 4 bytes (16 or 32).
    internal static int ModernWeatherStride(BethesdaGame game, int formVersion)
    {
        const int bandBytes = 4;
        return (HasWideTimeOfDayBands(game, formVersion) ? 8 : 4) * bandBytes;
    }

    // JNAM "Cloud Alphas": per-layer opacity FLOATS (Sunrise/Day/Sunset/Night), one struct per cloud layer.
    // FO4/FO76/SF1 widen 4→8 float bands at form version 111 (the extra Early/Late Sunrise/Sunset aids),
    // exactly like the colors; Skyrim is always 4. Stride = bands × 4 bytes (16 or 32).
    internal static int ModernCloudAlphaStride(BethesdaGame game, int formVersion)
    {
        const int floatBytes = 4;
        return (HasWideTimeOfDayBands(game, formVersion) ? 8 : 4) * floatBytes;
    }

    /// <summary>
    ///     Reads an ONAM/QNAM/RNAM per-layer cloud-speed array. xEdit's shared
    ///     <c>wbWeatherCloudSpeed</c> defines every supported generation, including FO4/FO76, as one U8
    ///     per layer, but the byte semantics differ. FO3/FNV ONAM is an unsigned scalar magnitude
    ///     (<c>b / 255</c>); Skyrim+ QNAM/RNAM are signed axes biased around 127. Endianness does not
    ///     affect individual bytes.
    /// </summary>
    internal static float[] ReadCloudSpeeds(ReadOnlySpan<byte> data, bool isBigEndian, BethesdaGame game)
    {
        var legacyEncoding = GameProfiles.For(game).UsesLegacyCloudSpeedEncoding;
        var speeds = new float[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            speeds[i] = legacyEncoding
                ? NormalizeLegacyCloudSpeedByte(data[i])
                : NormalizeCloudSpeedByte(data[i]);
        }

        return speeds;
    }

    internal static float NormalizeCloudSpeedByte(byte value) => (value - 127f) / 127f;

    internal static float NormalizeLegacyCloudSpeedByte(byte value) => value / 255f;

    // Reads the JNAM cloud-alpha array: one per-layer <see cref="WeatherCloudAlpha" /> (Sunrise/Day/Sunset/
    // Night floats) per stride-byte block. Floats are endian-aware so Xbox (byte-swapped) and PC agree.
    internal static List<WeatherCloudAlpha> ReadCloudAlphas(ReadOnlySpan<byte> data, bool isBigEndian, int strideBytes)
    {
        const int floatBytes = 4;
        if (strideBytes < 4 * floatBytes)
        {
            strideBytes = 4 * floatBytes; // never read fewer than the four base bands
        }

        var count = data.Length / strideBytes;
        var alphas = new List<WeatherCloudAlpha>(count);
        for (var i = 0; i < count; i++)
        {
            var b = i * strideBytes;
            var sunrise = ReadFloat(data, b, isBigEndian);
            var day = ReadFloat(data, b + floatBytes, isBigEndian);
            var sunset = ReadFloat(data, b + (2 * floatBytes), isBigEndian);
            var night = ReadFloat(data, b + (3 * floatBytes), isBigEndian);
            var bands = new WeatherTimeBands<float>(sunrise, day, sunset, night);
            if (strideBytes >= 8 * floatBytes)
            {
                bands = bands with
                {
                    EarlySunrise = ReadFloat(data, b + (4 * floatBytes), isBigEndian),
                    LateSunrise = ReadFloat(data, b + (5 * floatBytes), isBigEndian),
                    EarlySunset = ReadFloat(data, b + (6 * floatBytes), isBigEndian),
                    LateSunset = ReadFloat(data, b + (7 * floatBytes), isBigEndian),
                };
            }

            alphas.Add(new WeatherCloudAlpha(bands));
        }

        return alphas;
    }

    // FO3/FNV/Skyrim/FO4/FO76/SF1 store a u16 form version at record-header offset 20; Oblivion's
    // 20-byte header has none. Scanners parse that field once into the semantic record descriptor so
    // every decoder shares one endian-correct authority. Unknown/legacy records use zero, which keeps
    // the narrow pre-111 weather layout as the safe fallback.
    private static int ReadRecordFormVersion(DetectedMainRecord record) => record.FormVersion ?? 0;

    // The number of RGBA time bands per weather-color entry (4 for FO3, 6 for FNV). NAM0 carries a fixed
    // 10 color categories in both games, so its byte length / 10 gives the per-category stride and /4 the
    // band count — a structural read (no game/version gate). PNAM (cloud colors) uses the same band count.
    // Defaults to the FNV 6-band layout when NAM0 is absent or its length isn't a clean 10-category block.
    private const int WeatherColorCategories = 10;

    internal static int DetectWeatherBands(byte[] data, int dataSize, bool bigEndian)
    {
        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, bigEndian))
        {
            if (sub.Signature != "NAM0" || sub.DataLength < WeatherColorCategories * 4)
            {
                continue;
            }

            if (sub.DataLength % WeatherColorCategories != 0)
            {
                break;
            }

            var stride = sub.DataLength / WeatherColorCategories;
            var bands = stride / 4;
            return bands is 4 or 6 ? bands : 6;
        }

        return 6;
    }

    // One DALC band → the arithmetic mean of its six direction colors (X+/X−/Y+/Y−/Z+/Z−, the
    // first 24 bytes; the trailing specular + fresnel are not read). The mean is the single-color
    // reduction of the engine's ambient cube — Bethesda authors up-facing directions dimmer (the
    // sun already lights those) and down/occluded faces brighter, so the mean tracks the overall
    // authored ambient level per band.
    internal static WeatherRgba ReadDirectionalAmbientMean(ReadOnlySpan<byte> data, bool isBigEndian)
        => ReadDirectionalAmbientCube(data, isBigEndian).Mean;

    internal static WeatherAmbientCube ReadDirectionalAmbientCube(ReadOnlySpan<byte> data, bool isBigEndian) =>
        new()
        {
            PositiveX = ReadRgba(data.Slice(0, 4), isBigEndian),
            NegativeX = ReadRgba(data.Slice(4, 4), isBigEndian),
            PositiveY = ReadRgba(data.Slice(8, 4), isBigEndian),
            NegativeY = ReadRgba(data.Slice(12, 4), isBigEndian),
            PositiveZ = ReadRgba(data.Slice(16, 4), isBigEndian),
            NegativeZ = ReadRgba(data.Slice(20, 4), isBigEndian),
            Specular = data.Length >= 28 ? ReadRgba(data.Slice(24, 4), isBigEndian) : null,
            FresnelPower = data.Length >= 32 ? ReadFloat(data, 28, isBigEndian) : null,
        };

    // Assembles the per-band DALC means into a WeatherColor in NAM0 band order (Sunrise/Day/
    // Sunset/Night followed by FO4's four explicit transition bands). HighNoon/Midnight remain
    // unauthored because they are not part of the modern format.
    private static WeatherTimeBands<WeatherAmbientCube>? BuildDirectionalAmbientBands(
        List<WeatherAmbientCube>? bands)
    {
        if (bands is not { Count: > 0 })
        {
            return null;
        }

        WeatherAmbientCube Band(int i) => bands[Math.Min(i, bands.Count - 1)];
        var result = new WeatherTimeBands<WeatherAmbientCube>(Band(0), Band(1), Band(2), Band(3));
        if (bands.Count >= 8)
        {
            result = result with
            {
                EarlySunrise = Band(4),
                LateSunrise = Band(5),
                EarlySunset = Band(6),
                LateSunset = Band(7),
            };
        }

        return result;
    }

    private static WeatherColor? BuildDirectionalAmbient(WeatherTimeBands<WeatherAmbientCube>? bands)
    {
        if (bands is null) return null;
        return new WeatherColor(new WeatherTimeBands<WeatherRgba>(
            bands.Sunrise.Mean, bands.Day.Mean, bands.Sunset.Mean, bands.Night.Mean)
        {
            EarlySunrise = bands.EarlySunrise?.Mean,
            LateSunrise = bands.LateSunrise?.Mean,
            EarlySunset = bands.EarlySunset?.Mean,
            LateSunset = bands.LateSunset?.Mean,
        });
    }

    internal static WeatherTimeBands<uint> ReadWeatherImageSpaces(
        ReadOnlySpan<byte> data, bool isBigEndian, int bandCount)
    {
        var bands = new WeatherTimeBands<uint>(
            RecordParserContext.ReadFormId(data.Slice(0, 4), isBigEndian),
            RecordParserContext.ReadFormId(data.Slice(4, 4), isBigEndian),
            RecordParserContext.ReadFormId(data.Slice(8, 4), isBigEndian),
            RecordParserContext.ReadFormId(data.Slice(12, 4), isBigEndian));
        if (bandCount >= 8 && data.Length >= 32)
        {
            bands = bands with
            {
                EarlySunrise = RecordParserContext.ReadFormId(data.Slice(16, 4), isBigEndian),
                LateSunrise = RecordParserContext.ReadFormId(data.Slice(20, 4), isBigEndian),
                EarlySunset = RecordParserContext.ReadFormId(data.Slice(24, 4), isBigEndian),
                LateSunset = RecordParserContext.ReadFormId(data.Slice(28, 4), isBigEndian),
            };
        }

        return bands;
    }

    private static List<WeatherCloudLayer> BuildCloudLayers(
        SortedDictionary<int, string> textures,
        float[]? speedsX,
        float[]? speedsY,
        IReadOnlyList<WeatherColor>? colors,
        IReadOnlyList<WeatherCloudAlpha>? alphas)
    {
        var max = textures.Count > 0 ? textures.Keys.Max() : -1;
        max = Math.Max(max, (speedsX?.Length ?? 0) - 1);
        max = Math.Max(max, (speedsY?.Length ?? 0) - 1);
        max = Math.Max(max, (colors?.Count ?? 0) - 1);
        max = Math.Max(max, (alphas?.Count ?? 0) - 1);
        if (max < 0) return [];

        var layers = new List<WeatherCloudLayer>(max + 1);
        for (var i = 0; i <= max; i++)
        {
            layers.Add(new WeatherCloudLayer
            {
                SourceIndex = i,
                Texture = textures.GetValueOrDefault(i),
                SpeedU = i < (speedsX?.Length ?? 0) ? speedsX![i] : 0f,
                SpeedV = i < (speedsY?.Length ?? 0) ? speedsY![i] : 0f,
                Color = i < (colors?.Count ?? 0) ? colors![i] : null,
                Opacity = i < (alphas?.Count ?? 0) ? alphas![i] : null,
            });
        }

        return layers;
    }

    internal static WeatherHdr ReadWeatherHdr(ReadOnlySpan<byte> data, bool isBigEndian) =>
        new()
        {
            EyeAdaptSpeed = ReadFloat(data, 0, isBigEndian),
            BlurRadius = ReadFloat(data, 4, isBigEndian),
            BlurPasses = ReadFloat(data, 8, isBigEndian),
            EmissiveMult = ReadFloat(data, 12, isBigEndian),
            TargetLum = ReadFloat(data, 16, isBigEndian),
            UpperLumClamp = ReadFloat(data, 20, isBigEndian),
            BrightScale = ReadFloat(data, 24, isBigEndian),
            BrightClamp = ReadFloat(data, 28, isBigEndian),
            LumRampNoTex = ReadFloat(data, 32, isBigEndian),
            LumRampMin = ReadFloat(data, 36, isBigEndian),
            LumRampMax = ReadFloat(data, 40, isBigEndian),
            SunlightDimmer = ReadFloat(data, 44, isBigEndian),
            GrassDimmer = ReadFloat(data, 48, isBigEndian),
            TreeDimmer = ReadFloat(data, 52, isBigEndian),
        };

    internal static WeatherColor ReadWeatherRgbRow(ReadOnlySpan<byte> data, bool isBigEndian)
    {
        // Skyrim NAM2/NAM3 are four RGBX rows. Preserve the fourth byte losslessly: captures show
        // retail records commonly store zero there, and Sun/Moon::Update supplies visibility through
        // separate alpha state rather than interpreting this padding as opacity.
        static WeatherRgba Band(ReadOnlySpan<byte> source, int offset, bool bigEndian) =>
            ReadRgba(source.Slice(offset, 4), bigEndian);

        return new WeatherColor(
            Band(data, 0, isBigEndian),
            Band(data, 4, isBigEndian),
            Band(data, 8, isBigEndian),
            Band(data, 12, isBigEndian));
    }

    private static WeatherRgba ReadRgba(ReadOnlySpan<byte> band, bool isBigEndian)
    {
        var v = isBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(band)
            : BinaryPrimitives.ReadUInt32LittleEndian(band);
        return new WeatherRgba((byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24));
    }

    private static List<float> ReadFogDistances(ReadOnlySpan<byte> data, bool isBigEndian)
    {
        var count = data.Length / 4;
        var fog = new List<float>(count);
        for (var i = 0; i < count; i++)
        {
            fog.Add(ReadFloat(data, i * 4, isBigEndian));
        }

        return fog;
    }

    private static WeatherData ReadWeatherData(ReadOnlySpan<byte> d, BethesdaGame game) =>
        // 15-byte layout per the DATA/WTHR converter schema: WindSpeed(0), pad(1,2), TransDelta(3),
        // SunGlare(4), SunDamage(5), PrecipBeginFadeIn(6), PrecipEndFadeOut(7), ThunderBeginFadeIn(8),
        // ThunderEndFadeOut(9), ThunderFreq(10), Flags(11), LightningR/G/B(12,13,14).
        new()
        {
            WindSpeed = d[0],
            CloudSpeedLower = game == BethesdaGame.Oblivion ? d[1] : null,
            CloudSpeedUpper = game == BethesdaGame.Oblivion ? d[2] : null,
            TransDelta = d[3],
            SunGlare = d[4],
            SunDamage = d[5],
            PrecipitationBeginFadeIn = d[6],
            PrecipitationEndFadeOut = d[7],
            ThunderLightningBeginFadeIn = d[8],
            ThunderLightningEndFadeOut = d[9],
            ThunderLightningFrequency = d[10],
            Flags = d[11],
            LightningColor = new WeatherRgba(d[12], d[13], d[14], 255),
        };

    /// <summary>
    ///     Reads the low-nibble cinematic-enable flags from the terminal source-endian dword in the
    ///     148/152-byte FO3/FNV layouts. The normalized in-memory parameter block stores the flags
    ///     dword at offset 148; <c>TESImageSpace::Load</c>'s four-byte Skin Dimmer insertion maps a
    ///     148-byte source's dword at 144 to that position.
    ///     <para>
    ///         The dword immediately after Tint.Amount (offset 128/132) is a distinct unknown field,
    ///         even though retail values there often resemble a mask. A 132-byte source ends after
    ///         that unknown dword and therefore has no terminal flag-bearing lane.
    ///     </para>
    /// </summary>
    internal static ImageSpaceCinematicFlags ReadImageSpaceCinematicFlags(
        ReadOnlySpan<byte> data, bool isBigEndian)
    {
        var offset = data.Length switch
        {
            148 => 144,
            152 => 148,
            _ => -1,
        };
        if (offset < 0) return ImageSpaceCinematicFlags.None;

        var raw = isBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, sizeof(uint)))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, sizeof(uint)));
        return (ImageSpaceCinematicFlags)(raw & (uint)ImageSpaceCinematicFlags.All);
    }

    /// <summary>
    ///     Semantic FO3/FNV DNAM reader. Both games share a character-for-character identical xEdit
    ///     definition, and three layouts ship in BOTH of them: 152 B (record form version ≥ 14) adds
    ///     Skin Dimmer at +56 and shifts everything after it by four bytes; 148 B and 132 B omit it
    ///     and differ only in trailing padding. Skin Dimmer is normalized to the manager default of
    ///     1 when absent rather than reproducing TESImageSpace::Load's undefined short-read tail.
    ///     <para>
    ///         DNAM LENGTH is the discriminator on purpose: it is self-describing and cannot
    ///         overrun, per the project's version-keyed-parsing convention (a field's size is read
    ///         structurally). The record's form version is a cross-check only — see
    ///         <see cref="WarnOnImageSpaceLayoutVersionMismatch" />. ⚠ xEdit declares Skin Dimmer
    ///         <c>wbFromVersion(10)</c>, but retail data shows it first at version 14
    ///         (132 B = v1-9, 148 B = v11-13, 152 B = v14-15, measured across both masters).
    ///     </para>
    ///     <para>
    ///         No reliable authored on-disk fade block is established. Normalized engine parameter
    ///         storage names the four dwords before the terminal flags as cinematic fade RGBA, but
    ///         retail DNAM tails do not reliably contain authored floats. The source-endian dword
    ///         lanes are therefore retained opaquely rather than projected as semantic fade values.
    ///     </para>
    /// </summary>
    internal static ImageSpaceClassicData ReadClassicImageSpaceDnam(
        ReadOnlySpan<byte> data, bool isBigEndian)
    {
        var sourceLayout = data.Length switch
        {
            132 => ImageSpaceClassicDnamLayout.Dnam132,
            148 => ImageSpaceClassicDnamLayout.Dnam148,
            152 => ImageSpaceClassicDnamLayout.Dnam152,
            _ => throw new ArgumentException(
                "FO3/FNV IMGS DNAM must use the measured 132-, 148-, or 152-byte layout.",
                nameof(data)),
        };
        var hasSkinDimmer = sourceLayout == ImageSpaceClassicDnamLayout.Dnam152;
        var auxiliaryBase = hasSkinDimmer ? 60 : 56;
        var cinematicBase = auxiliaryBase + 40;
        var postBodyOffset = cinematicBase + 32;
        var hasExplicitFlags = sourceLayout != ImageSpaceClassicDnamLayout.Dnam132;
        var postBodyWordCount = hasExplicitFlags ? 5 : 1;
        var postBodyWords = new uint[postBodyWordCount];
        for (var index = 0; index < postBodyWords.Length; index++)
        {
            var wordBytes = data.Slice(postBodyOffset + index * sizeof(uint), sizeof(uint));
            postBodyWords[index] = isBigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(wordBytes)
                : BinaryPrimitives.ReadUInt32LittleEndian(wordBytes);
        }
        var hdr = new ImageSpaceHdr
        {
            EyeAdaptSpeed = ReadFloat(data, 0, isBigEndian),
            BlurRadius = ReadFloat(data, 4, isBigEndian),
            BlurPasses = ReadFloat(data, 8, isBigEndian),
            EmissiveMult = ReadFloat(data, 12, isBigEndian),
            TargetLum = ReadFloat(data, 16, isBigEndian),
            UpperLumClamp = ReadFloat(data, 20, isBigEndian),
            BrightScale = ReadFloat(data, 24, isBigEndian),
            BrightClamp = ReadFloat(data, 28, isBigEndian),
            LumRampNoTex = ReadFloat(data, 32, isBigEndian),
            LumRampMin = ReadFloat(data, 36, isBigEndian),
            LumRampMax = ReadFloat(data, 40, isBigEndian),
            SunlightDimmer = ReadFloat(data, 44, isBigEndian),
            GrassDimmer = ReadFloat(data, 48, isBigEndian),
            TreeDimmer = ReadFloat(data, 52, isBigEndian),
            SkinDimmer = hasSkinDimmer ? ReadFloat(data, 56, isBigEndian) : 1f,
        };
        var bloom = new ImageSpaceClassicBloom(
            ReadFloat(data, auxiliaryBase, isBigEndian),
            ReadFloat(data, auxiliaryBase + 4, isBigEndian),
            ReadFloat(data, auxiliaryBase + 8, isBigEndian));
        var getHit = new ImageSpaceClassicGetHit(
            ReadFloat(data, auxiliaryBase + 12, isBigEndian),
            ReadFloat(data, auxiliaryBase + 16, isBigEndian),
            ReadFloat(data, auxiliaryBase + 20, isBigEndian));
        var nightEye = new ImageSpaceClassicNightEye(
            ReadFloat(data, auxiliaryBase + 24, isBigEndian),
            ReadFloat(data, auxiliaryBase + 28, isBigEndian),
            ReadFloat(data, auxiliaryBase + 32, isBigEndian),
            ReadFloat(data, auxiliaryBase + 36, isBigEndian));
        var cinematic = new ImageSpaceCinematic
        {
            HasExplicitFlags = hasExplicitFlags,
            Flags = ReadImageSpaceCinematicFlags(data, isBigEndian),
            Saturation = ReadFloat(data, cinematicBase, isBigEndian),
            ContrastAvgLum = ReadFloat(data, cinematicBase + 4, isBigEndian),
            Contrast = ReadFloat(data, cinematicBase + 8, isBigEndian),
            Brightness = ReadFloat(data, cinematicBase + 12, isBigEndian),
        };
        var tint = new ImageSpaceTint
        {
            Red = ReadFloat(data, cinematicBase + 16, isBigEndian),
            Green = ReadFloat(data, cinematicBase + 20, isBigEndian),
            Blue = ReadFloat(data, cinematicBase + 24, isBigEndian),
            Amount = ReadFloat(data, cinematicBase + 28, isBigEndian),
        };
        return new ImageSpaceClassicData
        {
            SourceLayout = sourceLayout,
            Hdr = hdr,
            Bloom = bloom,
            GetHit = getHit,
            NightEye = nightEye,
            Cinematic = cinematic,
            Tint = tint,
            PostBodyWords = postBodyWords,
        };
    }

    /// <summary>
    ///     Cross-checks the record's form version against the layout its DNAM length implies, and
    ///     warns when they disagree. Length stays authoritative for decoding (it cannot overrun);
    ///     a mismatch means either a mod authored an unusual record or the measured version
    ///     threshold is wrong — both worth surfacing, and both silent before 2026-08-11.
    /// </summary>
    private static void WarnOnImageSpaceLayoutVersionMismatch(
        uint formId, int dnamLength, int formVersion, BethesdaGame game)
    {
        if (GameProfiles.For(game).ImageSpaceSkinDimmerFormVersion is not { } skinDimmerFrom ||
            formVersion <= 0)
        {
            return;
        }

        var lengthSaysSkinDimmer = dnamLength >= 152;
        var versionSaysSkinDimmer = formVersion >= skinDimmerFrom;
        if (lengthSaysSkinDimmer == versionSaysSkinDimmer) return;

        Logger.Instance.Warn(
            $"  [Semantic] IMGS 0x{formId:X8}: DNAM is {dnamLength} bytes (Skin Dimmer " +
            $"{(lengthSaysSkinDimmer ? "present" : "absent")}) but form version {formVersion} " +
            $"implies {(versionSaysSkinDimmer ? "present" : "absent")}. Decoded by length.");
    }

    #endregion

    #region Climate

    /// <summary>
    ///     Parse all Climate (CLMT) records. A worldspace's CNAM points at one of these; it carries the
    ///     weather list + chances, sun textures, and sunrise/sunset/moon timing the atmosphere model uses.
    /// </summary>
    internal List<ClimateRecord> ParseClimate()
    {
        return ParseRecordList("CLMT", 256,
            ParseClimateFromAccessor,
            record => new ClimateRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });
    }

    private ClimateRecord? ParseClimateFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new ClimateRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null, sunTexture = null, sunGlare = null, modelPath = null;
        IReadOnlyList<ClimateWeatherEntry>? weatherTypes = null;
        ClimateTimingData? timing = null;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                case "FNAM":
                    sunTexture = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "GNAM":
                    sunGlare = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MODL":
                    modelPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                // WLST: weather list. FO3/FNV entries are 12 bytes (Weather FormID + Chance int32 + Global
                // FormID); Oblivion (TES4) entries are 8 bytes — Weather FormID + Chance, NO Global (xEdit
                // wbArrayS WLST). Accept >= 8 and pick the stride by game, else a 1-entry Oblivion WLST (8
                // bytes) is rejected outright and multi-entry ones misparse at the 12-byte stride.
                case "WLST" when sub.DataLength >= 8:
                    weatherTypes = ReadClimateWeatherList(
                        subData, record.IsBigEndian, Context.Game == BethesdaGame.Oblivion ? 8 : 12);
                    break;
                // TNAM: 6-byte timing (sunrise/sunset begin+end, volatility, moon phase length).
                case "TNAM" when sub.DataLength >= 6:
                    timing = new ClimateTimingData(
                        subData[0], subData[1], subData[2], subData[3], subData[4], subData[5]);
                    break;
            }
        }

        return new ClimateRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            SunTexture = sunTexture,
            SunGlareTexture = sunGlare,
            ModelPath = modelPath,
            WeatherTypes = weatherTypes ?? [],
            Timing = timing,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private static List<ClimateWeatherEntry> ReadClimateWeatherList(ReadOnlySpan<byte> data, bool isBigEndian, int entryBytes)
    {
        var count = data.Length / entryBytes;
        var list = new List<ClimateWeatherEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var b = i * entryBytes;
            var weather = RecordParserContext.ReadFormId(data.Slice(b, 4), isBigEndian);
            var chance = isBigEndian
                ? BinaryPrimitives.ReadInt32BigEndian(data.Slice(b + 4, 4))
                : BinaryPrimitives.ReadInt32LittleEndian(data.Slice(b + 4, 4));
            // Oblivion (8-byte entries) has no Global FormID — leave it 0 so the entry is treated as
            // ungated (correct: TES4 carries no conditional-weather global here). FO3/FNV read it at +8.
            var global = entryBytes >= 12 ? RecordParserContext.ReadFormId(data.Slice(b + 8, 4), isBigEndian) : 0u;
            list.Add(new ClimateWeatherEntry(weather, chance, global));
        }

        return list;
    }

    /// <summary>
    ///     Parse all classic Image Space Modifier (IMAD) records. Unlike GenericEsmRecord's
    ///     signature-keyed dictionary, this path appends repeated frame tables and therefore cannot
    ///     discard authored keys that share a subrecord signature.
    /// </summary>
    internal List<ImageSpaceModifierRecord> ParseImageSpaceModifiers()
    {
        var modifiers = ParseRecordList("IMAD", 512,
            ParseImageSpaceModifierFromAccessor,
            record => new ImageSpaceModifierRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian,
            });

        // Append only runtime-only forms. A byte-stream IMAD remains the lossless authority
        // for its FormID and is never overlaid by a reconstructed runtime object.
        Context.MergeRuntimeRecords(modifiers, 0x54, modifier => modifier.FormId,
            (reader, entry) => reader.ReadRuntimeImageSpaceModifier(entry),
            "image space modifiers");

        return modifiers;
    }

    private ImageSpaceModifierRecord? ParseImageSpaceModifierFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new ImageSpaceModifierRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian,
            };
        }

        var (data, dataSize) = recordData.Value;
        string? editorId = null;
        ImageSpaceModifierData? modifierData = null;
        var parameterKeys = new List<ImageSpaceModifierFloatKey>?[21, 2];
        var scalarKeys = new Dictionary<string, List<ImageSpaceModifierFloatKey>>(StringComparer.Ordinal);
        var tintKeys = new List<ImageSpaceModifierColorKey>();
        var fadeKeys = new List<ImageSpaceModifierColorKey>();
        var ordered = new List<ImageSpaceModifierRawSubrecord>();
        uint? introSound = null;
        uint? outroSound = null;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);
            ordered.Add(new ImageSpaceModifierRawSubrecord(sub.Signature, subData.ToArray()));

            if (TryImageSpaceModifierParameterSignature(sub.Signature, out var parameter, out var operation))
            {
                var list = parameterKeys[(int)parameter, (int)operation] ??= [];
                list.AddRange(ReadImageSpaceModifierFloatKeys(subData, record.IsBigEndian));
                continue;
            }

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(editorId)) Context.FormIdToEditorId[record.FormId] = editorId;
                    break;
                case "DNAM" when sub.DataLength >= 8:
                    modifierData = ReadImageSpaceModifierData(subData, record.IsBigEndian);
                    break;
                case "TNAM":
                    tintKeys.AddRange(ReadImageSpaceModifierColorKeys(subData, record.IsBigEndian));
                    break;
                case "NAM3":
                    fadeKeys.AddRange(ReadImageSpaceModifierColorKeys(subData, record.IsBigEndian));
                    break;
                case "RDSD" when sub.DataLength >= 4:
                    introSound = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "RDSI" when sub.DataLength >= 4:
                    outroSound = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "BNAM" or "VNAM" or "RNAM" or "SNAM" or "UNAM" or
                     "NAM1" or "NAM2" or "WNAM" or "XNAM" or "YNAM" or "NAM4":
                {
                    if (!scalarKeys.TryGetValue(sub.Signature, out var keys))
                    {
                        keys = [];
                        scalarKeys.Add(sub.Signature, keys);
                    }
                    keys.AddRange(ReadImageSpaceModifierFloatKeys(subData, record.IsBigEndian));
                    break;
                }
            }
        }

        var parameters = new ImageSpaceModifierParameterTimeline[21];
        for (var i = 0; i < parameters.Length; i++)
        {
            parameters[i] = new ImageSpaceModifierParameterTimeline(
                (ImageSpaceModifierParameter)i,
                parameterKeys[i, (int)ImageSpaceModifierOperation.Multiply] ?? [],
                parameterKeys[i, (int)ImageSpaceModifierOperation.Add] ?? []);
        }

        return new ImageSpaceModifierRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            Data = modifierData,
            Parameters = parameters,
            ScalarTimelines = scalarKeys.ToDictionary(
                p => p.Key, p => (IReadOnlyList<ImageSpaceModifierFloatKey>)p.Value,
                StringComparer.Ordinal),
            TintColorTimeline = tintKeys,
            FadeColorTimeline = fadeKeys,
            IntroSoundFormId = introSound is not 0 ? introSound : null,
            OutroSoundFormId = outroSound is not 0 ? outroSound : null,
            OrderedSubrecords = ordered,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian,
        };
    }

    /// <summary>
    ///     IMAD DNAM mixes numeric values with packed byte fields. The bAnimatable quartet at 0,
    ///     radial-target quartet at 200, and DoF target/mode quartet at 224 retain byte order;
    ///     numeric fields use record endianness.
    /// </summary>
    internal static ImageSpaceModifierData ReadImageSpaceModifierData(ReadOnlySpan<byte> data, bool isBigEndian)
    {
        var payload = new List<uint>(Math.Max(0, (data.Length - 8) / 4));
        for (var offset = 8; offset + 4 <= data.Length; offset += 4)
        {
            // Runtime ImageSpaceModifierData::Endian leaves the packed radial-target
            // (C8..CB) and DoF target/mode (E0..E3) byte quartets untouched.
            payload.Add(isBigEndian && offset is not (200 or 224)
                ? BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4))
                : BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4)));
        }

        return new ImageSpaceModifierData
        {
            AnimatableFlag = data[0] != 0 ? 1u : 0u,
            Duration = ReadFloat(data, 4, isBigEndian),
            RawPayload = payload,
        };
    }

    internal static IReadOnlyList<ImageSpaceModifierFloatKey> ReadImageSpaceModifierFloatKeys(
        ReadOnlySpan<byte> data, bool isBigEndian)
    {
        var keys = new List<ImageSpaceModifierFloatKey>(data.Length / 8);
        for (var offset = 0; offset + 8 <= data.Length; offset += 8)
        {
            keys.Add(new ImageSpaceModifierFloatKey(
                ReadFloat(data, offset, isBigEndian),
                ReadFloat(data, offset + 4, isBigEndian)));
        }
        return keys;
    }

    internal static IReadOnlyList<ImageSpaceModifierColorKey> ReadImageSpaceModifierColorKeys(
        ReadOnlySpan<byte> data, bool isBigEndian)
    {
        var keys = new List<ImageSpaceModifierColorKey>(data.Length / 20);
        for (var offset = 0; offset + 20 <= data.Length; offset += 20)
        {
            keys.Add(new ImageSpaceModifierColorKey(
                ReadFloat(data, offset, isBigEndian),
                ReadFloat(data, offset + 4, isBigEndian),
                ReadFloat(data, offset + 8, isBigEndian),
                ReadFloat(data, offset + 12, isBigEndian),
                ReadFloat(data, offset + 16, isBigEndian)));
        }
        return keys;
    }

    internal static bool TryImageSpaceModifierParameterSignature(
        string signature, out ImageSpaceModifierParameter parameter, out ImageSpaceModifierOperation operation)
    {
        if (signature.Length == 4 && signature[1] == 'I' && signature[2] == 'A' && signature[3] == 'D')
        {
            var prefix = signature[0];
            if (prefix <= '\u0014')
            {
                parameter = (ImageSpaceModifierParameter)prefix;
                operation = ImageSpaceModifierOperation.Multiply;
                return true;
            }
            if (prefix is >= '@' and <= 'T')
            {
                parameter = (ImageSpaceModifierParameter)(prefix - '@');
                operation = ImageSpaceModifierOperation.Add;
                return true;
            }
        }

        parameter = default;
        operation = default;
        return false;
    }

    /// <summary>
    ///     Parse all Image Space (IMGS) records. Cells reference one via XCIM and worldspaces via INAM;
    ///     the viewer's tonemap stage reads the HDR (TargetLUM/UpperLUMClamp) + cinematic
    ///     (saturation/contrast/brightness/tint) values to reproduce the engine's post-process.
    /// </summary>
    internal List<ImageSpaceRecord> ParseImageSpaces()
    {
        return ParseRecordList("IMGS", 128,
            ParseImageSpaceFromAccessor,
            record => new ImageSpaceRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });
    }

    private ImageSpaceRecord? ParseImageSpaceFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new ImageSpaceRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        ImageSpaceHdr? hdr = null;
        ImageSpaceClassicData? classicDnam = null;
        ImageSpaceModernHdr? modernHdr = null;
        ImageSpaceCinematic? cinematic = null;
        ImageSpaceTint? tint = null;
        ImageSpaceDepthOfField? depthOfFieldData = null;
        string? lutTexturePath = null;
        var hasSplitModernHdr = false;
        var modernImageSpace = GameProfiles.For(Context.Game).ImageSpaceFamily is not null;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                // FO3/FNV: a single DNAM block (the HNAM/CNAM/TNAM split below is Skyrim-family).
                // The 152-byte layout adds Skin Dimmer at +56 and shifts the rest by four; 148 and
                // 132 omit it and differ only in trailing padding. LENGTH is the discriminator —
                // self-describing and overrun-safe — with the record's form version cross-checked
                // for a warning only (see WarnOnImageSpaceLayoutVersionMismatch).
                case "DNAM" when sub.DataLength is 132 or 148 or 152:
                {
                    classicDnam = ReadClassicImageSpaceDnam(subData, record.IsBigEndian);
                    hdr = classicDnam.Hdr;
                    cinematic = classicDnam.Cinematic;
                    tint = classicDnam.Tint;
                    WarnOnImageSpaceLayoutVersionMismatch(
                        record.FormId, sub.DataLength, ReadRecordFormVersion(record), Context.Game);
                    break;
                }
                // Skyrim/FO4-family legacy packed layout: seven HDR values, cinematic, then tint.
                // ENAM omits the two later HNAM-only fields; those stay null rather than inheriting
                // values from unrelated ordinals. Split HNAM/CNAM/TNAM wins when both are present.
                case "ENAM" when modernImageSpace && sub.DataLength >= 56:
                {
                    var packed = ReadModernImageSpacePackedData(subData, record.IsBigEndian, Context.Game);
                    if (!hasSplitModernHdr) modernHdr = packed.Hdr;
                    cinematic ??= packed.Cinematic;
                    tint ??= packed.Tint;
                    break;
                }
                // Modern 36-byte HNAM has two incompatible semantic layouts. Never decode it as the
                // classic FO3/FNV ImageSpaceHdr ordinal block.
                case "HNAM" when modernImageSpace && sub.DataLength >= 36:
                    modernHdr = ReadModernImageSpaceHdr(subData, record.IsBigEndian, Context.Game);
                    hasSplitModernHdr = true;
                    break;
                // Non-modern split layout retained for old plugin compatibility.
                case "HNAM" when sub.DataLength >= 36:
                    hdr = new ImageSpaceHdr
                    {
                        EyeAdaptSpeed = ReadFloat(subData, 0, record.IsBigEndian),
                        BlurRadius = ReadFloat(subData, 4, record.IsBigEndian),
                        BlurPasses = ReadFloat(subData, 8, record.IsBigEndian),
                        EmissiveMult = ReadFloat(subData, 12, record.IsBigEndian),
                        TargetLum = ReadFloat(subData, 16, record.IsBigEndian),
                        UpperLumClamp = ReadFloat(subData, 20, record.IsBigEndian),
                        BrightScale = ReadFloat(subData, 24, record.IsBigEndian),
                        BrightClamp = ReadFloat(subData, 28, record.IsBigEndian),
                        LumRampNoTex = ReadFloat(subData, 32, record.IsBigEndian),
                    };
                    break;
                case "CNAM" when sub.DataLength >= 12:
                    cinematic = new ImageSpaceCinematic
                    {
                        HasExplicitFlags = false,
                        Saturation = ReadFloat(subData, 0, record.IsBigEndian),
                        Brightness = ReadFloat(subData, 4, record.IsBigEndian),
                        Contrast = ReadFloat(subData, 8, record.IsBigEndian),
                    };
                    break;
                case "TNAM" when sub.DataLength >= 16:
                    tint = new ImageSpaceTint
                    {
                        Amount = ReadFloat(subData, 0, record.IsBigEndian),
                        Red = ReadFloat(subData, 4, record.IsBigEndian),
                        Green = ReadFloat(subData, 8, record.IsBigEndian),
                        Blue = ReadFloat(subData, 12, record.IsBigEndian),
                    };
                    break;
                case "DNAM" when modernImageSpace && sub.DataLength >= 16:
                    depthOfFieldData = ReadModernImageSpaceDepthOfField(subData, record.IsBigEndian);
                    break;
                case "TX00" when modernImageSpace:
                    lutTexturePath = EsmStringUtils.ReadNullTermString(subData);
                    break;
            }
        }

        IReadOnlyList<float>? depthOfField = null;
        if (depthOfFieldData is not null)
        {
            depthOfField = depthOfFieldData.VignetteRadius is { } vignetteRadius
                ? [depthOfFieldData.Strength, depthOfFieldData.Distance, depthOfFieldData.Range,
                    depthOfFieldData.SkyBlurRadius, vignetteRadius,
                    depthOfFieldData.VignetteStrength.GetValueOrDefault()]
                : [depthOfFieldData.Strength, depthOfFieldData.Distance, depthOfFieldData.Range,
                    depthOfFieldData.SkyBlurRadius];
        }

        return new ImageSpaceRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            Hdr = hdr,
            ClassicDnam = classicDnam,
            ModernHdr = modernHdr,
            Cinematic = cinematic,
            Tint = tint,
            DepthOfField = depthOfField,
            DepthOfFieldData = depthOfFieldData,
            LutTexturePath = lutTexturePath,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    internal static ImageSpaceModernHdr ReadModernImageSpaceHdr(
        ReadOnlySpan<byte> data, bool isBigEndian, BethesdaGame game)
    {
        if (data.Length < 36) throw new ArgumentException("Modern IMGS HNAM requires 36 bytes.", nameof(data));
        var fallout4Family = GameProfiles.For(game).ImageSpaceFamily == ImageSpaceModernFamily.Fallout4;
        return fallout4Family
            ? new ImageSpaceModernHdr
            {
                Family = ImageSpaceModernFamily.Fallout4,
                EyeAdaptSpeed = ReadFloat(data, 0, isBigEndian),
                TonemapE = ReadFloat(data, 4, isBigEndian),
                BloomThreshold = ReadFloat(data, 8, isBigEndian),
                BloomScale = ReadFloat(data, 12, isBigEndian),
                AutoExposureMax = ReadFloat(data, 16, isBigEndian),
                AutoExposureMin = ReadFloat(data, 20, isBigEndian),
                SunlightScale = ReadFloat(data, 24, isBigEndian),
                SkyScale = ReadFloat(data, 28, isBigEndian),
                MiddleGray = ReadFloat(data, 32, isBigEndian),
            }
            : new ImageSpaceModernHdr
            {
                Family = ImageSpaceModernFamily.Skyrim,
                EyeAdaptSpeed = ReadFloat(data, 0, isBigEndian),
                BloomBlurRadius = ReadFloat(data, 4, isBigEndian),
                BloomThreshold = ReadFloat(data, 8, isBigEndian),
                BloomScale = ReadFloat(data, 12, isBigEndian),
                ReceiveBloomThreshold = ReadFloat(data, 16, isBigEndian),
                White = ReadFloat(data, 20, isBigEndian),
                SunlightScale = ReadFloat(data, 24, isBigEndian),
                SkyScale = ReadFloat(data, 28, isBigEndian),
                EyeAdaptStrength = ReadFloat(data, 32, isBigEndian),
            };
    }

    internal static ImageSpacePackedData ReadModernImageSpacePackedData(
        ReadOnlySpan<byte> data, bool isBigEndian, BethesdaGame game)
    {
        if (data.Length < 56) throw new ArgumentException("Modern IMGS ENAM requires 56 bytes.", nameof(data));
        var fallout4Family = GameProfiles.For(game).ImageSpaceFamily == ImageSpaceModernFamily.Fallout4;
        var combined = ReadFloat(data, 16, isBigEndian);
        var modernHdr = fallout4Family
            ? new ImageSpaceModernHdr
            {
                Family = ImageSpaceModernFamily.Fallout4,
                IsLegacyPackedEnam = true,
                EyeAdaptSpeed = ReadFloat(data, 0, isBigEndian),
                TonemapE = ReadFloat(data, 4, isBigEndian),
                BloomThreshold = ReadFloat(data, 8, isBigEndian),
                BloomScale = ReadFloat(data, 12, isBigEndian),
                AutoExposureMax = combined,
                AutoExposureMin = combined,
                SunlightScale = ReadFloat(data, 20, isBigEndian),
                SkyScale = ReadFloat(data, 24, isBigEndian),
            }
            : new ImageSpaceModernHdr
            {
                Family = ImageSpaceModernFamily.Skyrim,
                IsLegacyPackedEnam = true,
                EyeAdaptSpeed = ReadFloat(data, 0, isBigEndian),
                BloomBlurRadius = ReadFloat(data, 4, isBigEndian),
                BloomThreshold = ReadFloat(data, 8, isBigEndian),
                BloomScale = ReadFloat(data, 12, isBigEndian),
                ReceiveBloomThreshold = combined,
                SunlightScale = ReadFloat(data, 20, isBigEndian),
                SkyScale = ReadFloat(data, 24, isBigEndian),
            };
        return new ImageSpacePackedData(
            modernHdr,
            new ImageSpaceCinematic
            {
                HasExplicitFlags = false,
                Saturation = ReadFloat(data, 28, isBigEndian),
                Brightness = ReadFloat(data, 32, isBigEndian),
                Contrast = ReadFloat(data, 36, isBigEndian),
            },
            new ImageSpaceTint
            {
                Amount = ReadFloat(data, 40, isBigEndian),
                Red = ReadFloat(data, 44, isBigEndian),
                Green = ReadFloat(data, 48, isBigEndian),
                Blue = ReadFloat(data, 52, isBigEndian),
            });
    }

    internal static ImageSpaceDepthOfField ReadModernImageSpaceDepthOfField(
        ReadOnlySpan<byte> data, bool isBigEndian)
    {
        if (data.Length < 16) throw new ArgumentException("Modern IMGS DNAM requires 16 bytes.", nameof(data));
        var skyBlurRadius = isBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data.Slice(14, 2))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(14, 2));
        return new ImageSpaceDepthOfField
        {
            Strength = ReadFloat(data, 0, isBigEndian),
            Distance = ReadFloat(data, 4, isBigEndian),
            Range = ReadFloat(data, 8, isBigEndian),
            Unused0 = data[12],
            Unused1 = data[13],
            SkyBlurRadius = skyBlurRadius,
            VignetteRadius = data.Length >= 20 ? ReadFloat(data, 16, isBigEndian) : null,
            VignetteStrength = data.Length >= 24 ? ReadFloat(data, 20, isBigEndian) : null,
            RawData = data.ToArray(),
        };
    }

    #endregion

    #region Sounds

    /// <summary>
    ///     Parse all Sound (SOUN) records.
    /// </summary>
    internal List<SoundRecord> ParseSounds() => _audio.ParseSounds();

    #endregion

    #region Music Types

    /// <summary>
    ///     Parse all Music Type (MUSC) records.
    /// </summary>
    internal List<MusicTypeRecord> ParseMusicTypes() => _audio.ParseMusicTypes();

    #endregion

    #region Texture Sets

    /// <summary>
    ///     Parse all Texture Set (TXST) records.
    /// </summary>
    internal List<TextureSetRecord> ParseTextureSets() => _terrainTextures.ParseTextureSets();

    #endregion

    #region Material Swaps

    /// <summary>
    ///     Parse all Material Swap (MSWP) records (FO4/FO76 per-placement <c>.bgsm</c> substitutions).
    /// </summary>
    internal List<MaterialSwapRecord> ParseMaterialSwaps() => _terrainTextures.ParseMaterialSwaps();

    #endregion

    #region Landscape Textures

    /// <summary>
    ///     Parse all Landscape Texture (LTEX) records used by LAND texture layers.
    /// </summary>
    internal List<LandscapeTextureRecord> ParseLandscapeTextures() => _terrainTextures.ParseLandscapeTextures();

    #endregion

    #region Grass

    /// <summary>
    ///     Parse all Grass (GRAS) records. Referenced from LTEX via the GNAM subrecord:
    ///     each grass type is a small mesh the engine scatters across terrain painted with
    ///     a particular landscape texture.
    /// </summary>
    internal List<GrassRecord> ParseGrass() => _terrainTextures.ParseGrass();

    #endregion

    #region Audio Location Controllers

    /// <summary>
    ///     Parse all Audio Location Controller (ALOC) records.
    /// </summary>
    internal List<AudioLocationControllerRecord> ParseAudioLocationControllers() =>
        _audio.ParseAudioLocationControllers();

    #endregion

    #region Load Screen Types

    /// <summary>
    ///     Parse all Load Screen Type (LSCT) records.
    /// </summary>
    internal List<LoadScreenTypeRecord> ParseLoadScreenTypes()
    {
        var types = ParseAccessorOnly("LSCT", 256, ParseLoadScreenTypeFromAccessor);

        Context.MergeRuntimeRecords(types, 0x6E, t => t.FormId,
            (reader, entry) => reader.ReadRuntimeLoadScreenType(entry), "load screen types");

        return types;
    }

    private LoadScreenTypeRecord? ParseLoadScreenTypeFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return null;
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        Dictionary<string, object?>? layoutData = null;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);
            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                case "DATA" when sub.DataLength == 88:
                {
                    if (SubrecordSchemaView.TryRead("DATA", "LSCT", subData, record.IsBigEndian) is { } v)
                    {
                        layoutData = v.Raw;
                    }

                    break;
                }
            }
        }

        return new LoadScreenTypeRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            LayoutData = layoutData,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    private static float ReadFloat(ReadOnlySpan<byte> data, int offset, bool isBigEndian)
    {
        return isBigEndian
            ? BinaryPrimitives.ReadSingleBigEndian(data[offset..])
            : BinaryPrimitives.ReadSingleLittleEndian(data[offset..]);
    }
}
