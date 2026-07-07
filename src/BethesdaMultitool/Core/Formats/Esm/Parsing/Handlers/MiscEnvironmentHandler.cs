using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
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

        string? editorId = null, fullName = null, noiseTexture = null;
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
                    noiseTexture = EsmStringUtils.ReadNullTermString(subData);
                    break;
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
                case "DATA" when Context.Game == BethesdaGame.Oblivion && sub.DataLength >= 56:
                    visualProps = ReadOblivionWaterData(subData, record.IsBigEndian);
                    break;
                // Skyrim WATR DNAM (xEdit wbDefinitionsTES5): a longer struct (~228 bytes LE / 232 SSE)
                // whose colors are shifted +4 vs FNV (an extra float at +28) and which adds Skyrim-specific
                // specular/noise fields. Game-gated because its length (≠196) and layout differ from FNV's
                // DNAM; without this every Skyrim water falls back to the default tint (the FNV 196-byte case
                // below never matches Skyrim). Grounded in the disassembled Skyrim water PS
                // (tools/GhidraProject/skyrim_water_pixel_shader_decompiled.txt).
                case "DNAM" when Context.Game == BethesdaGame.Skyrim && sub.DataLength >= 52:
                    visualProps = ReadSkyrimWaterData(subData, record.IsBigEndian);
                    break;
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

        return new WaterRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            FullName = fullName,
            NoiseTexture = noiseTexture,
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
            ["SunPower"] = ReadFloat(d, 16, isBigEndian),
            ["ReflectivityAmount"] = ReadFloat(d, 20, isBigEndian),
            ["FresnelAmount"] = ReadFloat(d, 24, isBigEndian),
            // Wind/scroll (@0-15/@28-35) feed the noise scroll like FNV's DNAM layer fields; the WATR
            // fog distances (@36/@40) are Oblivion's depth-fade analog (the WATER000.pso FogParam).
            ["NoiseLayer1WindSpeed"] = ReadFloat(d, 0, isBigEndian),
            ["NoiseLayer1WindDir"] = ReadFloat(d, 4, isBigEndian),
            ["DepthFalloffStart"] = ReadFloat(d, 36, isBigEndian),
            ["DepthFalloffEnd"] = ReadFloat(d, 40, isBigEndian),
            ["ShallowColor"] = Color(d, 44),
            ["DeepColor"] = Color(d, 48),
            ["ReflectionColor"] = Color(d, 52),
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
        uint? imageSpaceMod = null;
        var sounds = new List<WeatherSound>();
        var cloudLayers = new SortedDictionary<int, string>();
        IReadOnlyList<WeatherColor>? colors = null;
        IReadOnlyList<WeatherColor>? cloudColors = null;
        IReadOnlyList<WeatherCloudAlpha>? cloudAlphas = null;
        float[]? cloudSpeedsX = null;
        float[]? cloudSpeedsY = null;
        IReadOnlyList<float>? fogDistances = null;
        WeatherData? weatherData = null;

        // NAM0/PNAM per-category stride. Two distinct NAM0 layouts:
        //  • FO3/FNV/Oblivion: a fixed 10 color categories; only the band count varies (FO3=4, FNV=6),
        //    so it's read structurally from NAM0's length (DetectWeatherBands).
        //  • Skyrim/FO4/FO76/SF1: a wbWeatherTimeOfDay struct per category whose width is form-versioned
        //    (FO4/FO76/SF1 widen 4→8 RGBA bands at form version 111) and whose category COUNT grows with
        //    form version (10→19) — so the /10 structural divide is invalid. Key the stride off the game +
        //    form version instead and read whatever categories fit (only 0–9 are consumed downstream).
        var modernWeather = Context.Game is BethesdaGame.Skyrim or BethesdaGame.Fallout4
            or BethesdaGame.Fallout76 or BethesdaGame.Starfield;
        var modernStride = modernWeather ? ModernWeatherStride(Context.Game, ReadRecordFormVersion(record)) : 0;
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
                case "ONAM" when sub.DataLength == 4:
                    imageSpaceMod = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
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
                case var cloudSig when TryCloudLayerIndex(cloudSig, out var cloudLayer):
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
                // the colors (FO4/76/SF1 widen 4→8 float bands at version 111); only the four base bands are
                // kept. Floats are endian-aware so Xbox (byte-swapped) and PC agree.
                case "JNAM" when modernWeather && sub.DataLength >= 16:
                    cloudAlphas = ReadCloudAlphas(subData, record.IsBigEndian, ModernCloudAlphaStride(Context.Game, ReadRecordFormVersion(record)));
                    break;
                // QNAM/RNAM "X/Y Cloud Speeds" (xEdit wbWeatherCloudSpeed): the per-layer per-axis UV
                // scroll rate Clouds::Update accumulates. Layout is game-generation-keyed: FNV/FO3/Skyrim
                // author ONE SIGNED BYTE per layer; FO4/FO76 author ONE FLOAT per layer — reading FO4's
                // float bytes as per-layer speeds made every layer race in a nonsense direction. Both
                // forms normalize to −1‥1 here (0 = still) so the renderer stays layout-agnostic.
                case "QNAM":
                    cloudSpeedsX = ReadCloudSpeeds(subData, record.IsBigEndian, Context.Game);
                    break;
                case "RNAM":
                    cloudSpeedsY = ReadCloudSpeeds(subData, record.IsBigEndian, Context.Game);
                    break;
                // FNAM "Fog Distances": 6 floats (24 bytes).
                case "FNAM" when sub.DataLength >= 4:
                    fogDistances = ReadFogDistances(subData, record.IsBigEndian);
                    break;
                // DATA: 15-byte struct (wind speed, sun glare, precip timing, flags, lightning color).
                case "DATA" when sub.DataLength >= 15:
                    weatherData = ReadWeatherData(subData);
                    break;
            }
        }

        return new WeatherRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            ImageSpaceModifier = imageSpaceMod != 0 ? imageSpaceMod : null,
            Sounds = sounds,
            // Present cloud layers in layer-index order (dense, gaps dropped) — the renderer maps each
            // cloud dome shape to the ordinal-th layer.
            CloudLayerTextures = cloudLayers.Count == 0 ? [] : cloudLayers.Values.ToList(),
            Colors = colors ?? [],
            CloudColors = cloudColors ?? [],
            CloudLayerAlphas = cloudAlphas ?? [],
            CloudSpeedsX = cloudSpeedsX ?? [],
            CloudSpeedsY = cloudSpeedsY ?? [],
            FogDistances = fogDistances ?? [],
            Data = weatherData,
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
    {
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
            // FO3 (4 bands) has no separate High Noon / Midnight — fall back to Day / Night so the
            // atmosphere resolver still gets sensible values across the clock.
            var highNoon = bandsPerEntry >= 5 ? ReadRgba(data.Slice(b + 4 * bandBytes, bandBytes), isBigEndian) : day;
            var midnight = bandsPerEntry >= 6 ? ReadRgba(data.Slice(b + 5 * bandBytes, bandBytes), isBigEndian) : night;
            colors.Add(new WeatherColor(sunrise, day, sunset, night, highNoon, midnight));
        }

        return colors;
    }

    // Skyrim/FO4/FO76/SF1 NAM0 + PNAM categories: each is a wbWeatherTimeOfDay struct of FOUR RGBA bands
    // (Sunrise/Day/Sunset/Night = 16 bytes). FO4/FO76/SF1 form version 111+ append FOUR more bands
    // (Early/Late Sunrise + Early/Late Sunset = 32 bytes total) per xEdit's wbWeatherTimeOfDay. Those extra
    // bands are interpolation aids with no engine "High Noon"/"Midnight" analogue, so HighNoon/Midnight
    // fall back to Day/Night (matching the FO3 fallback). Unlike FO3/FNV, the category COUNT here is
    // form-version dependent (10→19), so the stride is taken as given rather than derived from the length;
    // only categories 0–9 (Sky/Fog/Ambient/Sunlight/Sun/Stars/Sky-Lower/Horizon/…) are consumed by the
    // atmosphere renderer and they sit at identical ordinals across every game, so reading whatever fits at
    // the correct stride is sufficient. Each band is one endian-aware uint with RGBA from fixed bit slots.
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
            colors.Add(new WeatherColor(sunrise, day, sunset, night, day, night));
        }

        return colors;
    }

    // Per-category NAM0/PNAM stride for the version-trailered games. FO4/FO76/SF1 widen each category from
    // 4 to 8 RGBA bands at form version 111 (xEdit wbWeatherTimeOfDay's wbFromVersion(111, …)); Skyrim never
    // does. Stride = bands × 4 bytes (16 or 32).
    internal static int ModernWeatherStride(BethesdaGame game, int formVersion)
    {
        const int bandBytes = 4;
        var wide = formVersion >= 111
            && game is BethesdaGame.Fallout4 or BethesdaGame.Fallout76 or BethesdaGame.Starfield;
        return (wide ? 8 : 4) * bandBytes;
    }

    // JNAM "Cloud Alphas": per-layer opacity FLOATS (Sunrise/Day/Sunset/Night), one struct per cloud layer.
    // FO4/FO76/SF1 widen 4→8 float bands at form version 111 (the extra Early/Late Sunrise/Sunset aids),
    // exactly like the colors; Skyrim is always 4. Stride = bands × 4 bytes (16 or 32). Only the four base
    // bands are read regardless of stride, since they sit at the same fixed offsets in both layouts.
    internal static int ModernCloudAlphaStride(BethesdaGame game, int formVersion)
    {
        const int floatBytes = 4;
        var wide = formVersion >= 111
            && game is BethesdaGame.Fallout4 or BethesdaGame.Fallout76 or BethesdaGame.Starfield;
        return (wide ? 8 : 4) * floatBytes;
    }

    /// <summary>
    ///     Reads a QNAM/RNAM per-layer cloud-speed array, normalized to −1‥1 (0 = still). FNV/FO3/Skyrim
    ///     author one SIGNED byte per layer (÷127); FO4/FO76/Starfield one float per layer. FO4 floats are
    ///     small (CK-authored ~±0.1), so they're scaled ×10 into the same domain — a visual calibration
    ///     (the engine's exact scroll constant is unread), but sign + per-layer relative speeds are exact.
    /// </summary>
    internal static float[] ReadCloudSpeeds(ReadOnlySpan<byte> data, bool isBigEndian, BethesdaGame game)
    {
        var floatForm = game is BethesdaGame.Fallout4 or BethesdaGame.Fallout76 or BethesdaGame.Starfield
                        && data.Length >= 4 && data.Length % 4 == 0;
        if (!floatForm)
        {
            var bytes = new float[data.Length];
            for (var i = 0; i < data.Length; i++)
            {
                bytes[i] = (sbyte)data[i] / 127f;
            }

            return bytes;
        }

        var speeds = new float[data.Length / 4];
        for (var i = 0; i < speeds.Length; i++)
        {
            var raw = isBigEndian
                ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data[(i * 4)..])
                : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4)..]);
            var value = BitConverter.UInt32BitsToSingle(raw);
            speeds[i] = float.IsFinite(value) ? Math.Clamp(value * 10f, -1f, 1f) : 0f;
        }

        return speeds;
    }

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
            alphas.Add(new WeatherCloudAlpha(sunrise, day, sunset, night));
        }

        return alphas;
    }

    // FO3/FNV/Skyrim/FO4/FO76/SF1 store a u16 "form version" at record-header offset 0x14 (20); Oblivion's
    // 20-byte header has none. Read it (endian-aware) only for the modern games, where it gates NAM0's band
    // width. Returns 0 when unavailable (scan-only path or a short/legacy header) — callers then take the
    // narrow 4-band stride, the safe default for pre-111 records.
    private int ReadRecordFormVersion(DetectedMainRecord record)
    {
        const int formVersionHeaderOffset = 20;
        if (Context.Accessor is null || record.HeaderSize < formVersionHeaderOffset + 2)
        {
            return 0;
        }

        var buf = new byte[2];
        Context.Accessor.ReadArray(record.Offset + formVersionHeaderOffset, buf, 0, 2);
        return record.IsBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(buf)
            : BinaryPrimitives.ReadUInt16LittleEndian(buf);
    }

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

    private static WeatherData ReadWeatherData(ReadOnlySpan<byte> d) =>
        // 15-byte layout per the DATA/WTHR converter schema: WindSpeed(0), pad(1,2), TransDelta(3),
        // SunGlare(4), SunDamage(5), PrecipBeginFadeIn(6), PrecipEndFadeOut(7), ThunderBeginFadeIn(8),
        // ThunderEndFadeOut(9), ThunderFreq(10), Flags(11), LightningR/G/B(12,13,14).
        new()
        {
            WindSpeed = d[0],
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
