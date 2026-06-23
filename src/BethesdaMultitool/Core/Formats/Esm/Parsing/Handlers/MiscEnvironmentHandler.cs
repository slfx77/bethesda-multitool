using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
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
        byte[]? cloudSpeedsX = null;
        byte[]? cloudSpeedsY = null;
        IReadOnlyList<float>? fogDistances = null;
        WeatherData? weatherData = null;

        // Determine the RGBA band count once (FO3=4, FNV=6) from NAM0's length so NAM0 and PNAM both
        // parse with the right per-entry stride regardless of subrecord order.
        var weatherBands = DetectWeatherBands(data, dataSize, record.IsBigEndian);

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
                    colors = ReadWeatherColors(subData, record.IsBigEndian, weatherBands);
                    break;
                // PNAM "Cloud Colors" (xEdit wbWeatherCloudColors): one Time-of-Day color PER CLOUD LAYER —
                // the SAME 24-byte 6-band RGBA struct as a NAM0 category. The engine uploads this per layer
                // as the cloud shader's per-draw color/opacity uniform (SkyShader::SetupGeometryConstants,
                // MemDebug XEX) — so it tints the cloud sheet and its alpha is the layer opacity per band.
                case "PNAM" when sub.DataLength >= 16:
                    cloudColors = ReadWeatherColors(subData, record.IsBigEndian, weatherBands);
                    break;
                // QNAM/RNAM "X/Y Cloud Speeds" (FNV; xEdit wbWeatherCloudSpeed): one u8 per cloud layer,
                // the per-axis UV scroll rate. Clouds::Update accumulates layer scroll ∝ this byte × dt, so
                // each layer drifts at its own speed/direction. Bytes need no endian swap.
                case "QNAM":
                    cloudSpeedsX = subData.ToArray();
                    break;
                case "RNAM":
                    cloudSpeedsY = subData.ToArray();
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
                // WLST: weather list — 12-byte entries (Weather FormID, Chance int32, Global FormID).
                case "WLST" when sub.DataLength >= 12:
                    weatherTypes = ReadClimateWeatherList(subData, record.IsBigEndian);
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

    private static List<ClimateWeatherEntry> ReadClimateWeatherList(ReadOnlySpan<byte> data, bool isBigEndian)
    {
        const int entryBytes = 12;
        var count = data.Length / entryBytes;
        var list = new List<ClimateWeatherEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var b = i * entryBytes;
            var weather = RecordParserContext.ReadFormId(data.Slice(b, 4), isBigEndian);
            var chance = isBigEndian
                ? BinaryPrimitives.ReadInt32BigEndian(data.Slice(b + 4, 4))
                : BinaryPrimitives.ReadInt32LittleEndian(data.Slice(b + 4, 4));
            var global = RecordParserContext.ReadFormId(data.Slice(b + 8, 4), isBigEndian);
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
