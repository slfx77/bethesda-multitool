using System.Buffers.Binary;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Reflection;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

internal sealed class MiscEnvironmentHandler(RecordParserContext context) : RecordHandlerBase(context)
{
    private readonly AudioRecordHandler _audio = new(context);
    private readonly TerrainTextureRecordHandler _terrainTextures = new(context);

    #region Sounds

    /// <summary>
    ///     Parse all Sound (SOUN) records.
    /// </summary>
    internal List<SoundRecord> ParseSounds()
    {
        return _audio.ParseSounds();
    }

    #endregion

    #region Music Types

    /// <summary>
    ///     Parse all Music Type (MUSC) records.
    /// </summary>
    internal List<MusicTypeRecord> ParseMusicTypes()
    {
        return _audio.ParseMusicTypes();
    }

    #endregion

    #region Texture Sets

    /// <summary>
    ///     Parse all Texture Set (TXST) records.
    /// </summary>
    internal List<TextureSetRecord> ParseTextureSets()
    {
        return _terrainTextures.ParseTextureSets();
    }

    #endregion

    #region Material Swaps

    /// <summary>
    ///     Parse all Material Swap (MSWP) records (FO4/FO76 per-placement <c>.bgsm</c> substitutions).
    /// </summary>
    internal List<MaterialSwapRecord> ParseMaterialSwaps()
    {
        return _terrainTextures.ParseMaterialSwaps();
    }

    #endregion

    #region Landscape Textures

    /// <summary>
    ///     Parse all Landscape Texture (LTEX) records used by LAND texture layers.
    /// </summary>
    internal List<LandscapeTextureRecord> ParseLandscapeTextures()
    {
        return _terrainTextures.ParseLandscapeTextures();
    }

    #endregion

    #region Grass

    /// <summary>
    ///     Parse all Grass (GRAS) records. Referenced from LTEX via the GNAM subrecord:
    ///     each grass type is a small mesh the engine scatters across terrain painted with
    ///     a particular landscape texture.
    /// </summary>
    internal List<GrassRecord> ParseGrass()
    {
        return _terrainTextures.ParseGrass();
    }

    #endregion

    #region Audio Location Controllers

    /// <summary>
    ///     Parse all Audio Location Controller (ALOC) records.
    /// </summary>
    internal List<AudioLocationControllerRecord> ParseAudioLocationControllers()
    {
        return _audio.ParseAudioLocationControllers();
    }

    #endregion

    private static float ReadFloat(ReadOnlySpan<byte> data, int offset, bool isBigEndian)
    {
        return isBigEndian
            ? BinaryPrimitives.ReadSingleBigEndian(data[offset..])
            : BinaryPrimitives.ReadSingleLittleEndian(data[offset..]);
    }

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
        StarfieldWaterDnam? starfieldDnam = null;
        StarfieldWaterFlags? starfieldFlags = null;
        StarfieldWaterUnusedGnam? starfieldGnam = null;
        (float X, float Y, float Z)? starfieldLinearVelocity = null;
        (float X, float Y, float Z)? starfieldAngularVelocity = null;
        uint? starfieldRiverAbsorptionCurve = null;
        uint? starfieldOceanAbsorptionCurve = null;
        uint? starfieldRiverScatteringCurve = null;
        uint? starfieldOceanScatteringCurve = null;
        uint? starfieldPhytoplanktonCurve = null;
        uint? starfieldSedimentCurve = null;
        uint? starfieldYellowMatterCurve = null;
        var starfieldSeenSubrecords = Context.Game == BethesdaGame.Starfield
            ? new HashSet<string>(StringComparer.Ordinal)
            : null;
        var starfieldVisualMalformed = Context.Game == BethesdaGame.Starfield &&
                                       (record.IsBigEndian ||
                                        !HasCompleteLittleEndianSubrecordLayout(data, dataSize));

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
                    if (Context.Game == BethesdaGame.Starfield)
                    {
                        if (sub.DataLength != 1 || !starfieldSeenSubrecords!.Add("FNAM"))
                        {
                            starfieldVisualMalformed = true;
                        }
                        else
                        {
                            starfieldFlags = (StarfieldWaterFlags)subData[0];
                        }
                    }

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
                // Oblivion stores its water visual data in DATA — a SIZE-VERSIONED union (xEdit
                // wbDefinitionsTES4 sizes 102/86/62/42/2; the short forms are OLDER layouts, NOT
                // truncations of the 102-byte one — see ReadOblivionWaterData). Game-gated rather
                // than length-gated because FNV's WATR also has a large DATA variant (the
                // DNAM-or-DATA visual union, ~186 bytes) with a different field layout. The old
                // ≥ 100 gate silently dropped the authored colors of 6/23 shipped waters
                // (SwampWater, MS31Water, OblivionOil01, Blood, both CamoranLavas) to FNV fallback
                // tints — the 2026-08-08 adversarial review's confirmed finding #5. Damage trails
                // every version in its last two bytes (byte-verified: CamoranLava02@40=50,
                // OblivionOil01@60, OblivionLavaTest01@100=50).
                case "DATA" when Context.Game == BethesdaGame.Oblivion && sub.DataLength >= 42:
                {
                    visualProps = ReadOblivionWaterData(subData, record.IsBigEndian);
                    var damageOffset = sub.DataLength switch
                    {
                        >= 102 => 100,
                        86 => 84,
                        62 => 60,
                        42 => 40,
                        _ => -1 // non-retail length — no trustworthy damage position
                    };
                    if (damageOffset >= 0)
                    {
                        damage = record.IsBigEndian
                            ? BinaryPrimitives.ReadUInt16BigEndian(subData.Slice(damageOffset, 2))
                            : BinaryPrimitives.ReadUInt16LittleEndian(subData.Slice(damageOffset, 2));
                    }

                    break;
                }
                // Starfield WATR DNAM (xEdit wbDefinitionsSF1): exact 152-byte little-endian CE2
                // optical/displacement/noise payload. It does not contain classic shallow/deep
                // surface colors, so retain it only as typed authored data; rendering policy remains
                // the existing flat fallback until a separately grounded approximation is added.
                case "DNAM" when Context.Game == BethesdaGame.Starfield:
                {
                    if (!starfieldSeenSubrecords!.Add("DNAM") ||
                        TryReadStarfieldWaterDnam(subData, record.IsBigEndian) is not { } decoded)
                    {
                        starfieldVisualMalformed = true;
                    }
                    else
                    {
                        starfieldDnam = decoded;
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
                // FO76's retail 148-byte DNAM is NOT a short FO4 prefix. It replaces FO4's two
                // packed byte colors with float RGB payloads: opacity/transmission at
                // +4 and base color at +16. Reading it as FO4 turns many shipped waters black (and
                // some into arbitrary colors made from float bytes). Exact-size gate this recovered
                // layout; malformed or future variants remain unresolved instead of being guessed.
                case "DNAM" when Context.Game == BethesdaGame.Fallout76 &&
                                      !record.IsBigEndian && sub.DataLength == 148:
                    visualProps = ReadFallout76WaterData(subData, record.IsBigEndian);
                    break;
                // Skyrim's active set and FO4/FO76's only set ship as NAM2/NAM3/NAM4 zstrings
                // (layer 1 first). Surface layer 1 as the compatibility noise texture, stripping
                // the "data\" prefix authors use so the cache resolves it normally. The profile
                // flag deliberately excludes Starfield: CE2 WATR has no NAM2/NAM3/NAM4 paths.
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
                // The 196-byte schema is the classic FO3/FNV layout. Game-gate it so an unknown
                // future FO76/Starfield payload with the same size cannot silently fall through to
                // a physically unrelated decoder after failing its own exact layout gate above.
                case "DNAM" when
                    (Context.Game is BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas) &&
                    sub.DataLength == 196:
                {
                    if (SubrecordSchemaView.TryRead("DNAM", "WATR", subData, record.IsBigEndian) is { } v)
                    {
                        visualProps = v.Raw;
                    }

                    break;
                }
                // Starfield follows DNAM with an unused 12-byte GNAM plus NAM0/NAM1 velocity
                // vectors. Preserve GNAM as raw little-endian dwords rather than assigning the
                // float-vector semantics xEdit reserves for NAM0/NAM1.
                case "GNAM" when Context.Game == BethesdaGame.Starfield:
                {
                    if (!starfieldSeenSubrecords!.Add("GNAM") ||
                        TryReadStarfieldWaterGnam(subData, record.IsBigEndian) is not { } gnam)
                    {
                        starfieldVisualMalformed = true;
                    }
                    else
                    {
                        starfieldGnam = gnam;
                    }

                    break;
                }
                case "NAM0" when Context.Game == BethesdaGame.Starfield:
                {
                    if (!starfieldSeenSubrecords!.Add("NAM0") ||
                        TryReadStarfieldWaterVector(subData, record.IsBigEndian) is not { } vector)
                    {
                        starfieldVisualMalformed = true;
                    }
                    else
                    {
                        starfieldLinearVelocity = vector;
                    }

                    break;
                }
                case "NAM1" when Context.Game == BethesdaGame.Starfield:
                {
                    if (!starfieldSeenSubrecords!.Add("NAM1") ||
                        TryReadStarfieldWaterVector(subData, record.IsBigEndian) is not { } vector)
                    {
                        starfieldVisualMalformed = true;
                    }
                    else
                    {
                        starfieldAngularVelocity = vector;
                    }

                    break;
                }
                // Seven optional CUR3 references from the Starfield WATR schema. A present field is
                // exact-size only; malformed or duplicate input invalidates the typed envelope.
                case "ENAM" or "HNAM" or "JNAM" or "LNAM" or "MNAM" or "QNAM" or "UNAM"
                    when Context.Game == BethesdaGame.Starfield:
                {
                    if (sub.DataLength != sizeof(uint) || !starfieldSeenSubrecords!.Add(sub.Signature))
                    {
                        starfieldVisualMalformed = true;
                        break;
                    }

                    var rawFormId = RecordParserContext.ReadFormId(subData, false);
                    uint? curveFormId = rawFormId == 0 ? null : rawFormId;
                    switch (sub.Signature)
                    {
                        case "ENAM": starfieldRiverAbsorptionCurve = curveFormId; break;
                        case "HNAM": starfieldOceanAbsorptionCurve = curveFormId; break;
                        case "JNAM": starfieldRiverScatteringCurve = curveFormId; break;
                        case "LNAM": starfieldOceanScatteringCurve = curveFormId; break;
                        case "MNAM": starfieldPhytoplanktonCurve = curveFormId; break;
                        case "QNAM": starfieldSedimentCurve = curveFormId; break;
                        case "UNAM": starfieldYellowMatterCurve = curveFormId; break;
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

        if (Context.Game == BethesdaGame.Starfield &&
            !starfieldVisualMalformed &&
            starfieldDnam is not null &&
            starfieldFlags is { } decodedStarfieldFlags)
        {
            visualProps = new Dictionary<string, object?>
            {
                ["StarfieldVisualData"] = new StarfieldWaterVisualData
                {
                    Dnam = starfieldDnam,
                    Flags = decodedStarfieldFlags,
                    Gnam = starfieldGnam,
                    LinearVelocity = starfieldLinearVelocity,
                    AngularVelocity = starfieldAngularVelocity,
                    RiverAbsorptionCurveFormId = starfieldRiverAbsorptionCurve,
                    OceanAbsorptionCurveFormId = starfieldOceanAbsorptionCurve,
                    RiverScatteringCurveFormId = starfieldRiverScatteringCurve,
                    OceanScatteringCurveFormId = starfieldOceanScatteringCurve,
                    PhytoplanktonCurveFormId = starfieldPhytoplanktonCurve,
                    SedimentCurveFormId = starfieldSedimentCurve,
                    YellowMatterCurveFormId = starfieldYellowMatterCurve
                }
            };
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
    // TES4 WATR DATA is a SIZE-VERSIONED union (xEdit wbDefinitionsTES4 sizes 102/86/62/42/2).
    // The short forms are OLDER layouts, NOT byte prefixes of the 102-byte one — adversarially
    // byte-verified against retail Oblivion.esm 2026-08-18:
    //   42: seven floats (wind/wave/sun/reflectivity/fresnel) @0..27, then the THREE COLORS at
    //       @28/32/36 and damage u16 @40 (Blood 0x00090DDC decodes thematic blood-red there;
    //       CamoranLava02 0x0003AFD4 damage=50 matches its full-size sibling's @100).
    //   62: eleven floats (+scroll+fog) @0..43, colors @44/48/52, TextureBlend @56, damage @60
    //       (OblivionOil01 0x0003AB07).
    //  86: the 62-form through @56, then THREE-float Rain @60/64/68 and THREE-float Displacement
    //       @72/76/80 (no Dampener/StartingSize in this vintage), damage @84 (SwampWater,
    //       MS31Water — reading the full layout here misattributes the sim floats).
    //  102: five-float Rain @60..79 and Displacement @80..99, damage @100.
    internal static Dictionary<string, object?> ReadOblivionWaterData(ReadOnlySpan<byte> d, bool isBigEndian)
    {
        static uint Color(ReadOnlySpan<byte> d, int off)
        {
            return (uint)(d[off] | (d[off + 1] << 8) | (d[off + 2] << 16));
        }

        var props = new Dictionary<string, object?>
        {
            ["WindVelocity"] = ReadFloat(d, 0, isBigEndian),
            ["WindDirection"] = ReadFloat(d, 4, isBigEndian),
            ["WaveAmplitude"] = ReadFloat(d, 8, isBigEndian),
            ["WaveFrequency"] = ReadFloat(d, 12, isBigEndian),
            ["SunPower"] = ReadFloat(d, 16, isBigEndian),
            ["ReflectivityAmount"] = ReadFloat(d, 20, isBigEndian),
            ["FresnelAmount"] = ReadFloat(d, 24, isBigEndian)
        };

        if (d.Length < 62)
        {
            // 42-byte vintage: the colors immediately follow the seven floats.
            props["ShallowColor"] = Color(d, 28);
            props["DeepColor"] = Color(d, 32);
            props["ReflectionColor"] = Color(d, 36);
            return props;
        }

        props["ScrollXSpeed"] = ReadFloat(d, 28, isBigEndian);
        props["ScrollYSpeed"] = ReadFloat(d, 32, isBigEndian);
        props["FogNear"] = ReadFloat(d, 36, isBigEndian);
        props["FogFar"] = ReadFloat(d, 40, isBigEndian);
        props["ShallowColor"] = Color(d, 44);
        props["DeepColor"] = Color(d, 48);
        props["ReflectionColor"] = Color(d, 52);
        props["TextureBlend"] = d[56] / 100f;

        if (d.Length >= 102)
        {
            props["RainForce"] = ReadFloat(d, 60, isBigEndian);
            props["RainVelocity"] = ReadFloat(d, 64, isBigEndian);
            props["RainFalloff"] = ReadFloat(d, 68, isBigEndian);
            props["RainDampener"] = ReadFloat(d, 72, isBigEndian);
            props["RainStartingSize"] = ReadFloat(d, 76, isBigEndian);
            props["DisplacementForce"] = ReadFloat(d, 80, isBigEndian);
            props["DisplacementVelocity"] = ReadFloat(d, 84, isBigEndian);
            props["DisplacementFalloff"] = ReadFloat(d, 88, isBigEndian);
            props["DisplacementDampener"] = ReadFloat(d, 92, isBigEndian);
            props["DisplacementStartingSize"] = ReadFloat(d, 96, isBigEndian);
        }
        else if (d.Length >= 86)
        {
            // Three-float sim vintage: Force/Velocity/Falloff only, displacement directly after.
            props["RainForce"] = ReadFloat(d, 60, isBigEndian);
            props["RainVelocity"] = ReadFloat(d, 64, isBigEndian);
            props["RainFalloff"] = ReadFloat(d, 68, isBigEndian);
            props["DisplacementForce"] = ReadFloat(d, 72, isBigEndian);
            props["DisplacementVelocity"] = ReadFloat(d, 76, isBigEndian);
            props["DisplacementFalloff"] = ReadFloat(d, 80, isBigEndian);
        }

        return props;
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
        static uint Color(ReadOnlySpan<byte> d, int off)
        {
            return (uint)(d[off] | (d[off + 1] << 8) | (d[off + 2] << 16));
        }

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
            ["ReflectionColor"] = Color(d, 48)
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
    ///     Strictly decodes Starfield's exact 152-byte little-endian WATR DNAM as defined by
    ///     xEdit's <c>wbDefinitionsSF1</c>. Every single-precision field must be finite. No range
    ///     policy is imposed because negative fog planes and other authored values are valid CE2
    ///     data, and no compatibility colors or shader parameters are synthesized here.
    /// </summary>
    internal static StarfieldWaterDnam? TryReadStarfieldWaterDnam(
        ReadOnlySpan<byte> data,
        bool isBigEndian)
    {
        const int expectedLength = 152;
        const int underwaterColorOffset = 32;
        if (isBigEndian || data.Length != expectedLength) return null;

        // DNAM is otherwise a dense float array. Validate every scalar first so a single NaN or
        // infinity rejects the whole typed payload instead of leaking into later optical math.
        for (var offset = 0; offset < expectedLength; offset += sizeof(float))
        {
            if (offset == underwaterColorOffset) continue;
            if (!float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset, sizeof(float)))))
            {
                return null;
            }
        }

        static float F(ReadOnlySpan<byte> source, int offset) =>
            BinaryPrimitives.ReadSingleLittleEndian(source.Slice(offset, sizeof(float)));

        return new StarfieldWaterDnam
        {
            DepthAmount = F(data, 0),
            AbsorptionRanges = (F(data, 4), F(data, 8), F(data, 12)),
            PhytoplanktonConcentration = F(data, 16),
            SedimentConcentration = F(data, 20),
            YellowMatterConcentration = F(data, 24),
            Oceanness = F(data, 28),
            UnderwaterColor = (data[32], data[33], data[34], data[35]),
            UnderwaterFogAmount = F(data, 36),
            UnderwaterFogNear = F(data, 40),
            UnderwaterFogFar = F(data, 44),
            NormalMagnitude = F(data, 48),
            ShallowNormalFalloff = F(data, 52),
            DeepNormalFalloff = F(data, 56),
            SurfaceEffectFalloff = F(data, 60),
            DisplacementForce = F(data, 64),
            DisplacementVelocity = F(data, 68),
            DisplacementFalloff = F(data, 72),
            DisplacementDampener = F(data, 76),
            DisplacementStartingSize = F(data, 80),
            Layer1 = new StarfieldWaterNoiseLayer
            {
                WindDirection = F(data, 84), WindSpeed = F(data, 96),
                AmplitudeScale = F(data, 108), UvScale = F(data, 120), NoiseFalloff = F(data, 132)
            },
            Layer2 = new StarfieldWaterNoiseLayer
            {
                WindDirection = F(data, 88), WindSpeed = F(data, 100),
                AmplitudeScale = F(data, 112), UvScale = F(data, 124), NoiseFalloff = F(data, 136)
            },
            Layer3 = new StarfieldWaterNoiseLayer
            {
                WindDirection = F(data, 92), WindSpeed = F(data, 104),
                AmplitudeScale = F(data, 116), UvScale = F(data, 128), NoiseFalloff = F(data, 140)
            },
            FlowmapScale = F(data, 144),
            Roughness = F(data, 148)
        };
    }

    private static StarfieldWaterUnusedGnam? TryReadStarfieldWaterGnam(
        ReadOnlySpan<byte> data,
        bool isBigEndian)
    {
        if (isBigEndian || data.Length != 3 * sizeof(uint)) return null;

        return new StarfieldWaterUnusedGnam
        {
            Word0 = BinaryPrimitives.ReadUInt32LittleEndian(data),
            Word1 = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(sizeof(uint))),
            Word2 = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(2 * sizeof(uint)))
        };
    }

    private static (float X, float Y, float Z)? TryReadStarfieldWaterVector(
        ReadOnlySpan<byte> data,
        bool isBigEndian)
    {
        if (isBigEndian || data.Length != 3 * sizeof(float)) return null;

        var x = BinaryPrimitives.ReadSingleLittleEndian(data);
        var y = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(sizeof(float)));
        var z = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(2 * sizeof(float)));
        return float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z)
            ? (x, y, z)
            : null;
    }

    /// <summary>
    ///     FO76 WATR DNAM (exactly 148 bytes in all 47 records in the installed retail
    ///     SeventySix.esm audited 2026-08-30).
    ///     The leading optical block is independently recovered by the vendored fo76utils renderer:
    ///     depth at +0, float RGB channel opacity at +4, and float RGB base color at +16.
    ///     The FO4-compatible physical block resumes four bytes earlier than FO4 at +32 because the
    ///     Creation color/alpha-range block is absent. FO76 then replaces FO4's displacement/specular
    ///     tail with five field-grouped three-layer normal vectors at +84..+140 and a terminal +144
    ///     fingerprint. No FO4 reflection-color or sun/specular keys are projected from those bytes.
    /// </summary>
    internal static Dictionary<string, object?> ReadFallout76WaterData(ReadOnlySpan<byte> d, bool isBigEndian)
    {
        if (TryReadFallout76WaterVisualData(d, isBigEndian) is not { } visual) return [];

        var properties = new Dictionary<string, object?>
        {
            ["Fallout76VisualData"] = visual,
            ["DepthAmount"] = visual.DepthAmount,
            // Compatibility endpoints for the existing FO4-family renderer. FO76 authors one
            // base color plus per-channel opacity, not shallow/deep body colors, so both endpoints
            // must be the base color. Preserve the exact float vectors separately below.
            ["ShallowColor"] = PackNormalizedRgb(visual.BaseColor),
            ["DeepColor"] = PackNormalizedRgb(visual.BaseColor),
            ["Fallout76Unknown28"] = visual.Unknown28,
            ["UnderwaterColor"] = PackRgb(visual.UnderwaterColor),
            ["UnderwaterFogAmount"] = visual.UnderwaterFogAmount,
            ["UnderwaterFogNear"] = visual.UnderwaterFogNear,
            ["UnderwaterFogFar"] = visual.UnderwaterFogFar,
            ["NormalMagnitude"] = visual.NormalMagnitude,
            ["ShallowNormalFalloff"] = visual.ShallowNormalFalloff,
            ["DeepNormalFalloff"] = visual.DeepNormalFalloff,
            // Compatibility projections for the current shader. The offsets are strong structural
            // inferences, but their FO76 SetupMaterial bindings have not yet been recovered; the
            // typed payload keeps that qualification explicit.
            ["ReflectivityAmount"] = visual.ReflectivityCandidate,
            ["FresnelAmount"] = visual.FresnelCandidate,
            ["SurfaceEffectFalloff"] = visual.SurfaceEffectFalloff,
            ["DisplacementForce"] = visual.DisplacementForce,
            ["DisplacementVelocity"] = visual.DisplacementVelocity,
            ["DisplacementFalloff"] = visual.DisplacementFalloff,
            ["NoiseLayer1WindDir"] = visual.Layer1.WindDirDegrees,
            ["NoiseLayer2WindDir"] = visual.Layer2.WindDirDegrees,
            ["NoiseLayer3WindDir"] = visual.Layer3.WindDirDegrees,
            ["NoiseLayer1WindSpeed"] = visual.Layer1.WindSpeed,
            ["NoiseLayer2WindSpeed"] = visual.Layer2.WindSpeed,
            ["NoiseLayer3WindSpeed"] = visual.Layer3.WindSpeed,
            ["NoiseLayer1AmpScale"] = visual.Layer1.AmpScale,
            ["NoiseLayer2AmpScale"] = visual.Layer2.AmpScale,
            ["NoiseLayer3AmpScale"] = visual.Layer3.AmpScale,
            ["NoiseLayer1UVScale"] = visual.Layer1.UvScale,
            ["NoiseLayer2UVScale"] = visual.Layer2.UvScale,
            ["NoiseLayer3UVScale"] = visual.Layer3.UvScale,
            ["NoiseLayer1Falloff"] = visual.Layer1.Falloff,
            ["NoiseLayer2Falloff"] = visual.Layer2.Falloff,
            ["NoiseLayer3Falloff"] = visual.Layer3.Falloff,
            ["Fallout76Unknown144"] = visual.Unknown144
        };
        return properties;
    }

    internal static Fallout76WaterVisualData? TryReadFallout76WaterVisualData(
        ReadOnlySpan<byte> data,
        bool isBigEndian)
    {
        // FO76 has no big-endian plugin format. Treating a byte-swapped synthetic payload as this
        // layout would hide format-detection mistakes rather than provide useful compatibility.
        if (isBigEndian || data.Length != 148 || data[35] != 0) return null;

        var depth = ReadFloat(data, 0, false);
        var opacity = ReadNormalizedRgb(data, 4, false);
        var baseColor = ReadNormalizedRgb(data, 16, false);
        if (!float.IsFinite(depth) || depth <= 0f || opacity is null || baseColor is null) return null;

        var layer1 = ReadFallout76NoiseLayer(data, 0);
        var layer2 = ReadFallout76NoiseLayer(data, 1);
        var layer3 = ReadFallout76NoiseLayer(data, 2);
        var unknown144 = ReadFloat(data, 144, false);
        if (layer1 is null || layer2 is null || layer3 is null ||
            BitConverter.SingleToUInt32Bits(unknown144) != BitConverter.SingleToUInt32Bits(1f))
        {
            return null;
        }

        var requiredScalars = new[]
        {
            ReadFloat(data, 28, false), ReadFloat(data, 36, false), ReadFloat(data, 40, false),
            ReadFloat(data, 44, false), ReadFloat(data, 48, false), ReadFloat(data, 52, false),
            ReadFloat(data, 56, false), ReadFloat(data, 60, false), ReadFloat(data, 64, false),
            ReadFloat(data, 68, false), ReadFloat(data, 72, false), ReadFloat(data, 76, false),
            ReadFloat(data, 80, false)
        };
        if (requiredScalars.Any(value => !float.IsFinite(value))) return null;

        return new Fallout76WaterVisualData(
            depth,
            opacity.Value,
            baseColor.Value,
            requiredScalars[0],
            (data[32], data[33], data[34]),
            requiredScalars[1],
            requiredScalars[2],
            requiredScalars[3],
            requiredScalars[4],
            requiredScalars[5],
            requiredScalars[6],
            requiredScalars[7],
            requiredScalars[8],
            requiredScalars[9],
            requiredScalars[10],
            requiredScalars[11],
            requiredScalars[12],
            layer1.Value,
            layer2.Value,
            layer3.Value,
            unknown144);
    }

    private static WaterNoiseLayer? ReadFallout76NoiseLayer(ReadOnlySpan<byte> data, int layer)
    {
        var direction = ReadFloat(data, 84 + (layer * sizeof(float)), false);
        var speed = ReadFloat(data, 96 + (layer * sizeof(float)), false);
        var amplitude = ReadFloat(data, 108 + (layer * sizeof(float)), false);
        var uvScale = ReadFloat(data, 120 + (layer * sizeof(float)), false);
        var falloff = ReadFloat(data, 132 + (layer * sizeof(float)), false);
        return float.IsFinite(direction) && direction is >= 0f and <= 360f &&
               float.IsFinite(speed) && speed >= 0f &&
               float.IsFinite(amplitude) && amplitude >= 0f &&
               float.IsFinite(uvScale) && uvScale >= 0f &&
               float.IsFinite(falloff) && falloff >= 0f
            ? new WaterNoiseLayer(uvScale, direction, speed, amplitude, falloff)
            : null;
    }

    private static (float R, float G, float B)? ReadNormalizedRgb(
        ReadOnlySpan<byte> data,
        int offset,
        bool isBigEndian)
    {
        var r = ReadFloat(data, offset, isBigEndian);
        var g = ReadFloat(data, offset + sizeof(float), isBigEndian);
        var b = ReadFloat(data, offset + (2 * sizeof(float)), isBigEndian);
        return float.IsFinite(r) && float.IsFinite(g) && float.IsFinite(b) &&
               r is >= 0f and <= 1f && g is >= 0f and <= 1f && b is >= 0f and <= 1f
            ? (r, g, b)
            : null;
    }

    private static uint PackNormalizedRgb((float R, float G, float B) color)
    {
        static byte ToByte(float value) =>
            (byte)Math.Clamp((int)MathF.Round(value * byte.MaxValue), byte.MinValue, byte.MaxValue);

        return (uint)(ToByte(color.R) | (ToByte(color.G) << 8) | (ToByte(color.B) << 16));
    }

    private static uint PackRgb((byte R, byte G, byte B) color)
    {
        return (uint)(color.R | (color.G << 8) | (color.B << 16));
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
            ["SunSpecularMagnitude"] = ReadFloat(d, 104, isBigEndian)
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

    private static uint ReadWaterColor(ReadOnlySpan<byte> d, int offset)
    {
        return (uint)(d[offset] | (d[offset + 1] << 8) | (d[offset + 2] << 16));
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
        WeatherTimeBands<uint>? volumetricLightingFormIds = null;
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
                // FO76's WTHR HNAM is eight ordered VOLI FormIDs. This must remain game-scoped:
                // Oblivion uses the same signature for an unrelated 14-float HDR structure. The audited
                // retail SeventySix.esm carries one exact 32-byte HNAM on every one of its 121 WTHRs.
                case "HNAM" when Context.Game == BethesdaGame.Fallout76 && sub.DataLength == 32:
                    volumetricLightingFormIds = ReadWeatherImageSpaces(subData, record.IsBigEndian, 8);
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
                Midnight = legacyImageSpaceIds[5]
            };
        }

        // TES4 keeps the two cloud speeds in DATA and names its textures Lower=CNAM / Upper=DNAM.
        if (Context.Game == BethesdaGame.Oblivion && weatherData is { } tes4Data)
        {
            cloudSpeedsX ??=
            [
                NormalizeLegacyCloudSpeedByte(tes4Data.CloudSpeedLower ?? 0),
                NormalizeLegacyCloudSpeedByte(tes4Data.CloudSpeedUpper ?? 0)
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
            VolumetricLightingFormIds = volumetricLightingFormIds,
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
    {
        return TryCloudLayerIndex(signature, BethesdaGame.Unknown, out layer);
    }

    internal static bool TryCloudLayerIndex(string signature, BethesdaGame game, out int layer)
    {
        // Oblivion names the lower cloud texture CNAM and upper DNAM. Later engines reverse those
        // two legacy signatures (DNAM layer 0 / CNAM layer 1).
        if (game == BethesdaGame.Oblivion)
        {
            if (signature == "CNAM")
            {
                layer = 0;
                return true;
            }

            if (signature == "DNAM")
            {
                layer = 1;
                return true;
            }
        }

        switch (signature)
        {
            case "DNAM":
                layer = 0;
                return true;
            case "CNAM":
                layer = 1;
                return true;
            case "ANAM":
                layer = 2;
                return true;
            case "BNAM":
                layer = 3;
                return true;
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
    internal static List<WeatherColor> ReadWeatherColorsModern(ReadOnlySpan<byte> data, bool isBigEndian,
        int strideBytes)
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
                    LateSunset = ReadRgba(data.Slice(b + 7 * bandBytes, bandBytes), isBigEndian)
                };
            }

            colors.Add(new WeatherColor(bands));
        }

        return colors;
    }

    // The widening game set and threshold (form version 111, xEdit wbWeatherTimeOfDay's
    // wbFromVersion(111, …)) live on GameProfile so the NAM0/PNAM color and JNAM cloud-alpha
    // strides can't drift from each other or from the profile registry.
    internal static bool HasWideTimeOfDayBands(BethesdaGame game, int formVersion)
    {
        return GameProfiles.For(game).WideTimeOfDayBandsFormVersion is { } widensAt && formVersion >= widensAt;
    }

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

    internal static float NormalizeCloudSpeedByte(byte value)
    {
        return (value - 127f) / 127f;
    }

    internal static float NormalizeLegacyCloudSpeedByte(byte value)
    {
        return value / 255f;
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
            var sunset = ReadFloat(data, b + 2 * floatBytes, isBigEndian);
            var night = ReadFloat(data, b + 3 * floatBytes, isBigEndian);
            var bands = new WeatherTimeBands<float>(sunrise, day, sunset, night);
            if (strideBytes >= 8 * floatBytes)
            {
                bands = bands with
                {
                    EarlySunrise = ReadFloat(data, b + 4 * floatBytes, isBigEndian),
                    LateSunrise = ReadFloat(data, b + 5 * floatBytes, isBigEndian),
                    EarlySunset = ReadFloat(data, b + 6 * floatBytes, isBigEndian),
                    LateSunset = ReadFloat(data, b + 7 * floatBytes, isBigEndian)
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
    private static int ReadRecordFormVersion(DetectedMainRecord record)
    {
        return record.FormVersion ?? 0;
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

    // One DALC band → the arithmetic mean of its six direction colors (X+/X−/Y+/Y−/Z+/Z−, the
    // first 24 bytes; the trailing specular + fresnel are not read). The mean is the single-color
    // reduction of the engine's ambient cube — Bethesda authors up-facing directions dimmer (the
    // sun already lights those) and down/occluded faces brighter, so the mean tracks the overall
    // authored ambient level per band.
    internal static WeatherRgba ReadDirectionalAmbientMean(ReadOnlySpan<byte> data, bool isBigEndian)
    {
        return ReadDirectionalAmbientCube(data, isBigEndian).Mean;
    }

    internal static WeatherAmbientCube ReadDirectionalAmbientCube(ReadOnlySpan<byte> data, bool isBigEndian)
    {
        return new WeatherAmbientCube
        {
            PositiveX = ReadRgba(data.Slice(0, 4), isBigEndian),
            NegativeX = ReadRgba(data.Slice(4, 4), isBigEndian),
            PositiveY = ReadRgba(data.Slice(8, 4), isBigEndian),
            NegativeY = ReadRgba(data.Slice(12, 4), isBigEndian),
            PositiveZ = ReadRgba(data.Slice(16, 4), isBigEndian),
            NegativeZ = ReadRgba(data.Slice(20, 4), isBigEndian),
            Specular = data.Length >= 28 ? ReadRgba(data.Slice(24, 4), isBigEndian) : null,
            FresnelPower = data.Length >= 32 ? ReadFloat(data, 28, isBigEndian) : null
        };
    }

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

        WeatherAmbientCube Band(int i)
        {
            return bands[Math.Min(i, bands.Count - 1)];
        }

        var result = new WeatherTimeBands<WeatherAmbientCube>(Band(0), Band(1), Band(2), Band(3));
        if (bands.Count >= 8)
        {
            result = result with
            {
                EarlySunrise = Band(4),
                LateSunrise = Band(5),
                EarlySunset = Band(6),
                LateSunset = Band(7)
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
            LateSunset = bands.LateSunset?.Mean
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
                LateSunset = RecordParserContext.ReadFormId(data.Slice(28, 4), isBigEndian)
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
                Opacity = i < (alphas?.Count ?? 0) ? alphas![i] : null
            });
        }

        return layers;
    }

    internal static WeatherHdr ReadWeatherHdr(ReadOnlySpan<byte> data, bool isBigEndian)
    {
        return new WeatherHdr
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
            TreeDimmer = ReadFloat(data, 52, isBigEndian)
        };
    }

    internal static WeatherColor ReadWeatherRgbRow(ReadOnlySpan<byte> data, bool isBigEndian)
    {
        // Skyrim NAM2/NAM3 are four RGBX rows. Preserve the fourth byte losslessly: captures show
        // retail records commonly store zero there, and Sun/Moon::Update supplies visibility through
        // separate alpha state rather than interpreting this padding as opacity.
        static WeatherRgba Band(ReadOnlySpan<byte> source, int offset, bool bigEndian)
        {
            return ReadRgba(source.Slice(offset, 4), bigEndian);
        }

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

    private static WeatherData ReadWeatherData(ReadOnlySpan<byte> d, BethesdaGame game)
    {
        // 15-byte layout per the DATA/WTHR converter schema: WindSpeed(0), pad(1,2), TransDelta(3),
        // SunGlare(4), SunDamage(5), PrecipBeginFadeIn(6), PrecipEndFadeOut(7), ThunderBeginFadeIn(8),
        // ThunderEndFadeOut(9), ThunderFreq(10), Flags(11), LightningR/G/B(12,13,14).
        return new WeatherData
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
            LightningColor = new WeatherRgba(d[12], d[13], d[14], 255)
        };
    }

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
            _ => -1
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
                nameof(data))
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
            SkinDimmer = hasSkinDimmer ? ReadFloat(data, 56, isBigEndian) : 1f
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
            Brightness = ReadFloat(data, cinematicBase + 12, isBigEndian)
        };
        var tint = new ImageSpaceTint
        {
            Red = ReadFloat(data, cinematicBase + 16, isBigEndian),
            Green = ReadFloat(data, cinematicBase + 20, isBigEndian),
            Blue = ReadFloat(data, cinematicBase + 24, isBigEndian),
            Amount = ReadFloat(data, cinematicBase + 28, isBigEndian)
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
            PostBodyWords = postBodyWords
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

    #region Fallout 76 Volumetric Lighting

    /// <summary>
    ///     Parse Fallout 76's classic VOLI schema. Starfield reuses the signature for a reflected
    ///     object, so this path is game-gated and publishes to a separate typed collection.
    /// </summary>
    internal List<Fallout76VolumetricLightingRecord> ParseFallout76VolumetricLightingSettings()
    {
        if (Context.Game != BethesdaGame.Fallout76)
        {
            return [];
        }

        return ParseRecordList(
            "VOLI",
            256,
            ParseFallout76VolumetricLightingFromAccessor,
            record => CreateFallout76VolumetricLightingFailure(
                record,
                "Fallout 76 VOLI bytes are unavailable without record-byte access."));
    }

    private Fallout76VolumetricLightingRecord ParseFallout76VolumetricLightingFromAccessor(
        DetectedMainRecord record,
        byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return CreateFallout76VolumetricLightingFailure(
                record,
                "Fallout 76 VOLI record bytes could not be read.");
        }

        var (data, dataSize) = recordData.Value;
        var editorIdPrefix = CaptureFallout76VolumetricLightingEditorIdPrefix(
            data, dataSize, record.IsBigEndian);
        if (!string.IsNullOrWhiteSpace(editorIdPrefix))
        {
            Context.FormIdToEditorId[record.FormId] = editorIdPrefix;
        }

        if (record.IsBigEndian)
        {
            return CreateFallout76VolumetricLightingFailure(
                record,
                "Fallout 76's classic VOLI schema is supported only in little-endian records.",
                editorIdPrefix);
        }

        if (Context.NonContiguousRecordFormIds.Contains(record.FormId) ||
            Context.PartiallyRecoveredFormIds.Contains(record.FormId))
        {
            return CreateFallout76VolumetricLightingFailure(
                record,
                "Fallout 76 VOLI decoding rejects non-contiguous or partially recovered record bytes.",
                editorIdPrefix);
        }

        if (!TryDecodeFallout76VolumetricLighting(
                data.AsSpan(0, dataSize), out var editorId, out var settings, out var failure))
        {
            return CreateFallout76VolumetricLightingFailure(
                record,
                failure ?? "Fallout 76 VOLI schema validation failed.",
                editorId ?? editorIdPrefix);
        }

        Context.FormIdToEditorId[record.FormId] = editorId!;
        return new Fallout76VolumetricLightingRecord
        {
            FormId = record.FormId,
            EditorId = editorId,
            Settings = settings,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private static bool TryDecodeFallout76VolumetricLighting(
        ReadOnlySpan<byte> data,
        out string? editorId,
        out Fallout76VolumetricLightingSettings? settings,
        out string? failure)
    {
        editorId = null;
        settings = null;
        failure = null;

        var fields = new List<(string Signature, int DataOffset, int DataLength)>(13);
        var offset = 0;
        while (offset < data.Length)
        {
            if (offset > data.Length - EsmSubrecordUtils.SubrecordHeaderSize)
            {
                failure = "Fallout 76 VOLI has a truncated subrecord header.";
                return false;
            }

            var signature = System.Text.Encoding.ASCII.GetString(
                data.Slice(offset, sizeof(uint)));
            var dataLength = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + sizeof(uint))..]);
            offset += EsmSubrecordUtils.SubrecordHeaderSize;

            // No retail field needs XXXX: EDID is at most 49 bytes and every numeric field is four.
            // Rejecting it keeps this decoder pinned to the audited classic schema rather than accepting
            // an alternate envelope which has not been observed in any of the 212 retail records.
            if (signature == "XXXX")
            {
                failure = "Fallout 76 VOLI does not permit an XXXX extended-length subrecord.";
                return false;
            }

            if (dataLength > data.Length - offset)
            {
                failure = $"Fallout 76 VOLI {signature} extends past the record boundary.";
                return false;
            }

            fields.Add((signature, offset, dataLength));
            offset += dataLength;
        }

        if (fields.Count is not 12 and not 13)
        {
            failure =
                $"Fallout 76 VOLI requires 12 fields, or 13 when optional LNAM is present; found {fields.Count}.";
            return false;
        }

        var requiredPrefix = new[]
        {
            "EDID", "CNAM", "DNAM", "ENAM", "FNAM", "GNAM", "HNAM", "INAM", "JNAM", "KNAM"
        };
        for (var index = 0; index < requiredPrefix.Length; index++)
        {
            if (fields[index].Signature != requiredPrefix[index])
            {
                failure =
                    $"Fallout 76 VOLI expected {requiredPrefix[index]} at field {index}; " +
                    $"found {fields[index].Signature}.";
                return false;
            }
        }

        var tailIndex = requiredPrefix.Length;
        var hasPhaseContribution = fields[tailIndex].Signature == "LNAM";
        if (hasPhaseContribution)
        {
            tailIndex++;
        }

        if (tailIndex + 2 != fields.Count ||
            fields[tailIndex].Signature != "MNAM" ||
            fields[tailIndex + 1].Signature != "NNAM")
        {
            failure =
                "Fallout 76 VOLI requires optional LNAM followed by exactly MNAM and NNAM.";
            return false;
        }

        var edidField = fields[0];
        var edidBytes = data.Slice(edidField.DataOffset, edidField.DataLength);
        if (edidBytes.Length < 2 || edidBytes[^1] != 0 || edidBytes[..^1].IndexOf((byte)0) >= 0)
        {
            failure = "Fallout 76 VOLI EDID must be one non-empty null-terminated string.";
            return false;
        }

        editorId = EsmStringUtils.DecodeGameText(edidBytes[..^1]);
        if (string.IsNullOrWhiteSpace(editorId))
        {
            failure = "Fallout 76 VOLI EDID is empty.";
            return false;
        }

        var decodedFloats = new Dictionary<string, float>(StringComparer.Ordinal);
        for (var index = 1; index < fields.Count; index++)
        {
            var field = fields[index];
            if (field.DataLength != sizeof(float))
            {
                failure =
                    $"Fallout 76 VOLI {field.Signature} must be exactly four bytes; " +
                    $"found {field.DataLength}.";
                return false;
            }

            var value = BinaryPrimitives.ReadSingleLittleEndian(data[field.DataOffset..]);
            if (!float.IsFinite(value))
            {
                failure = $"Fallout 76 VOLI {field.Signature} is non-finite.";
                return false;
            }

            decodedFloats[field.Signature] = value;
        }

        settings = new Fallout76VolumetricLightingSettings
        {
            Intensity = decodedFloats["CNAM"],
            CustomColorContribution = decodedFloats["DNAM"],
            ColorRed = decodedFloats["ENAM"],
            ColorGreen = decodedFloats["FNAM"],
            ColorBlue = decodedFloats["GNAM"],
            DensityContribution = decodedFloats["HNAM"],
            DensitySize = decodedFloats["INAM"],
            DensityWindSpeed = decodedFloats["JNAM"],
            DensityFallingSpeed = decodedFloats["KNAM"],
            PhaseFunctionContribution = hasPhaseContribution ? decodedFloats["LNAM"] : null,
            PhaseFunctionScattering = decodedFloats["MNAM"],
            SamplingRepartitionRangeFactor = decodedFloats["NNAM"]
        };
        return true;
    }

    private static string? CaptureFallout76VolumetricLightingEditorIdPrefix(
        byte[] data,
        int dataSize,
        bool isBigEndian)
    {
        if (dataSize < EsmSubrecordUtils.SubrecordHeaderSize || dataSize > data.Length)
        {
            return null;
        }

        var signature = data.AsSpan(0, sizeof(uint));
        if (!(isBigEndian ? signature.SequenceEqual("DIDE"u8) : signature.SequenceEqual("EDID"u8)))
        {
            return null;
        }

        var length = isBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(sizeof(uint)))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(sizeof(uint)));
        if (length < 2 || EsmSubrecordUtils.SubrecordHeaderSize + length > dataSize)
        {
            return null;
        }

        var payload = data.AsSpan(EsmSubrecordUtils.SubrecordHeaderSize, length);
        return payload[^1] == 0 && payload[..^1].IndexOf((byte)0) < 0
            ? EsmStringUtils.DecodeGameText(payload[..^1])
            : null;
    }

    private Fallout76VolumetricLightingRecord CreateFallout76VolumetricLightingFailure(
        DetectedMainRecord record,
        string failure,
        string? editorId = null)
    {
        return new Fallout76VolumetricLightingRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            DecodeFailure = failure,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Starfield Weather Settings

    /// <summary>
    ///     Parse Starfield Creation Engine 2 Weather Settings (WTHS). These reflected records are
    ///     deliberately kept separate from legacy WTHR records: CLMT WSLT entries reference WTHS,
    ///     and substituting a WTHR would silently select the wrong atmosphere architecture.
    /// </summary>
    internal List<StarfieldWeatherSettingsRecord> ParseStarfieldWeatherSettings()
    {
        if (Context.Game != BethesdaGame.Starfield)
        {
            return [];
        }

        return ParseRecordList("WTHS", 4096,
            ParseStarfieldWeatherSettingsFromAccessor,
            record => CreateWeatherSettingsFailure(
                record,
                "WTHS reflection payload is unavailable without record-byte access."));
    }

    private StarfieldWeatherSettingsRecord ParseStarfieldWeatherSettingsFromAccessor(
        DetectedMainRecord record,
        byte[] buffer)
    {
        if (record.IsBigEndian)
        {
            return CreateWeatherSettingsFailure(
                record,
                "Starfield WTHS reflection streams are supported only in little-endian records.");
        }

        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return CreateWeatherSettingsFailure(record, "WTHS record bytes could not be read.");
        }

        if (Context.NonContiguousRecordFormIds.Contains(record.FormId) ||
            Context.PartiallyRecoveredFormIds.Contains(record.FormId))
        {
            return CreateWeatherSettingsFailure(
                record,
                "WTHS reflection decoding rejects non-contiguous or partially recovered record bytes.");
        }

        var (data, dataSize) = recordData.Value;
        if (!HasCompleteLittleEndianSubrecordLayout(data, dataSize))
        {
            return CreateWeatherSettingsFailure(
                record,
                "WTHS has a truncated or malformed outer subrecord layout.");
        }

        string? editorId = null;
        byte[]? fullPayload = null;
        byte[]? diffPayload = null;
        uint? outerParentFormId = null;
        string? structuralFailure = null;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, false))
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
                case "RFDP":
                    if (outerParentFormId.HasValue || sub.DataLength != sizeof(uint))
                    {
                        structuralFailure ??= "WTHS has a duplicate or malformed RFDP parent.";
                    }
                    else
                    {
                        outerParentFormId = BinaryPrimitives.ReadUInt32LittleEndian(subData);
                    }

                    break;
                case "REFL":
                    if (fullPayload != null)
                    {
                        structuralFailure ??= "WTHS has duplicate REFL payloads.";
                    }
                    else
                    {
                        fullPayload = subData.ToArray();
                    }

                    break;
                case "RDIF":
                    if (diffPayload != null)
                    {
                        structuralFailure ??= "WTHS has duplicate RDIF payloads.";
                    }
                    else
                    {
                        diffPayload = subData.ToArray();
                    }

                    break;
                default:
                    NoteUnmodeledSubrecord("WTHS", sub.Signature, sub.DataLength);
                    break;
            }
        }

        var payloadKind = (fullPayload, diffPayload) switch
        {
            (not null, null) => StarfieldWeatherSettingsPayloadKind.FullObject,
            (null, not null) => StarfieldWeatherSettingsPayloadKind.Diff,
            _ => StarfieldWeatherSettingsPayloadKind.Unknown
        };
        if (structuralFailure == null)
        {
            if ((fullPayload == null) == (diffPayload == null))
            {
                structuralFailure = "WTHS must contain exactly one REFL or RDIF payload.";
            }
            else if (fullPayload != null && outerParentFormId.HasValue)
            {
                structuralFailure = "A root WTHS REFL payload must not carry RFDP.";
            }
            else if (diffPayload != null && (!outerParentFormId.HasValue || outerParentFormId.Value == 0))
            {
                structuralFailure = "A WTHS RDIF payload requires a non-zero RFDP parent.";
            }
        }

        StarfieldWeatherSettingsPatch? patch = null;
        if (structuralFailure == null)
        {
            var reflectionPayload = fullPayload ?? diffPayload!;
            if (!StarfieldWeatherSettingsDecoder.TryDecode(
                    reflectionPayload, payloadKind, out patch, out structuralFailure))
            {
                structuralFailure ??= "WTHS reflection decoding failed.";
            }
            else if (payloadKind == StarfieldWeatherSettingsPayloadKind.FullObject &&
                     patch!.ParentFormId != 0)
            {
                structuralFailure = "A root WTHS REFL payload must declare a null pParent.";
                patch = null;
            }
            else if (payloadKind == StarfieldWeatherSettingsPayloadKind.Diff &&
                     patch!.ParentFormId.HasValue &&
                     patch.ParentFormId.Value != outerParentFormId!.Value)
            {
                structuralFailure = "WTHS RFDP disagrees with the reflected pParent field.";
                patch = null;
            }
        }

        return new StarfieldWeatherSettingsRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            ParentFormId = outerParentFormId,
            PayloadKind = payloadKind,
            Patch = structuralFailure == null ? patch : null,
            DecodeFailure = structuralFailure,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private StarfieldWeatherSettingsRecord CreateWeatherSettingsFailure(
        DetectedMainRecord record,
        string failure)
    {
        return new StarfieldWeatherSettingsRecord
        {
            FormId = record.FormId,
            EditorId = Context.GetEditorId(record.FormId),
            PayloadKind = StarfieldWeatherSettingsPayloadKind.Unknown,
            DecodeFailure = failure,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private static bool HasCompleteLittleEndianSubrecordLayout(byte[] data, int dataSize)
    {
        const uint extendedLengthSignature = 0x58585858; // XXXX
        if (dataSize < 0 || dataSize > data.Length)
        {
            return false;
        }

        var offset = 0;
        uint? pendingExtendedLength = null;
        while (offset < dataSize)
        {
            if (offset > dataSize - EsmSubrecordUtils.SubrecordHeaderSize)
            {
                return false;
            }

            var signature = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
            var shortLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4));
            if (signature == extendedLengthSignature)
            {
                if (pendingExtendedLength.HasValue || shortLength != sizeof(uint) ||
                    offset > dataSize - EsmSubrecordUtils.SubrecordHeaderSize - sizeof(uint))
                {
                    return false;
                }

                pendingExtendedLength = BinaryPrimitives.ReadUInt32LittleEndian(
                    data.AsSpan(offset + EsmSubrecordUtils.SubrecordHeaderSize));
                offset += EsmSubrecordUtils.SubrecordHeaderSize + sizeof(uint);
                continue;
            }

            if (pendingExtendedLength.HasValue && shortLength != 0)
            {
                return false;
            }

            var payloadLength = pendingExtendedLength ?? shortLength;
            pendingExtendedLength = null;
            var remaining = (long)dataSize - offset - EsmSubrecordUtils.SubrecordHeaderSize;
            if (payloadLength > remaining)
            {
                return false;
            }

            offset += EsmSubrecordUtils.SubrecordHeaderSize + checked((int)payloadLength);
        }

        return offset == dataSize && !pendingExtendedLength.HasValue;
    }

    #endregion

    #region Starfield Atmospheres

    /// <summary>
    ///     Parses the bounded Starfield ATMO structural contract. Retail records are exactly one
    ///     non-empty EDID followed by either a full REFL root, or a non-zero RFDP followed by RDIF.
    ///     Malformed records remain in the result as failure envelopes so a later load-order override
    ///     cannot silently expose an earlier valid definition.
    /// </summary>
    internal List<StarfieldAtmosphereRecord> ParseStarfieldAtmospheres()
    {
        if (Context.Game != BethesdaGame.Starfield)
        {
            return [];
        }

        return ParseRecordList(
            "ATMO",
            4096,
            ParseStarfieldAtmosphereFromAccessor,
            record => CreateAtmosphereFailure(
                record,
                "ATMO reflection payload is unavailable without record-byte access."));
    }

    private StarfieldAtmosphereRecord ParseStarfieldAtmosphereFromAccessor(
        DetectedMainRecord record,
        byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return CreateAtmosphereFailure(record, "ATMO record bytes could not be read.");
        }

        var (data, dataSize) = recordData.Value;
        var editorIdPrefix = CaptureStarfieldAtmosphereEditorIdPrefix(
            data, dataSize, record.IsBigEndian);
        if (!string.IsNullOrWhiteSpace(editorIdPrefix))
        {
            Context.FormIdToEditorId[record.FormId] = editorIdPrefix;
        }

        if (record.IsBigEndian)
        {
            return CreateAtmosphereFailure(
                record,
                "Starfield ATMO reflection streams are supported only in little-endian records.",
                editorIdPrefix);
        }

        if (Context.NonContiguousRecordFormIds.Contains(record.FormId) ||
            Context.PartiallyRecoveredFormIds.Contains(record.FormId))
        {
            return CreateAtmosphereFailure(
                record,
                "ATMO reflection decoding rejects non-contiguous or partially recovered record bytes.",
                editorIdPrefix);
        }

        if (!TryReadStarfieldAtmosphereEnvelope(
                data.AsSpan(0, dataSize),
                out var editorId,
                out var parentFormId,
                out var payloadKind,
                out var reflectionPayload,
                out var failure))
        {
            return CreateAtmosphereFailure(
                record,
                failure ?? "ATMO outer-record validation failed.",
                editorId ?? editorIdPrefix,
                parentFormId,
                payloadKind);
        }

        Context.FormIdToEditorId[record.FormId] = editorId!;
        if (!StarfieldAtmosphereDecoder.TryDecode(
                reflectionPayload!, payloadKind, out var patch, out failure))
        {
            return CreateAtmosphereFailure(
                record,
                failure ?? "ATMO reflection decoding failed.",
                editorId,
                parentFormId,
                payloadKind);
        }

        if (payloadKind == StarfieldAtmospherePayloadKind.FullObject && patch!.ParentFormId != 0)
        {
            return CreateAtmosphereFailure(
                record,
                "A root ATMO REFL payload must declare a null pParent.",
                editorId,
                parentFormId,
                payloadKind);
        }

        if (payloadKind == StarfieldAtmospherePayloadKind.Diff &&
            patch!.ParentFormId.HasValue &&
            patch.ParentFormId.Value != parentFormId!.Value)
        {
            return CreateAtmosphereFailure(
                record,
                "ATMO RFDP disagrees with the reflected pParent field.",
                editorId,
                parentFormId,
                payloadKind);
        }

        return new StarfieldAtmosphereRecord
        {
            FormId = record.FormId,
            EditorId = editorId,
            ParentFormId = parentFormId,
            PayloadKind = payloadKind,
            Patch = patch,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private static bool TryReadStarfieldAtmosphereEnvelope(
        ReadOnlySpan<byte> data,
        out string? editorId,
        out uint? parentFormId,
        out StarfieldAtmospherePayloadKind payloadKind,
        out byte[]? reflectionPayload,
        out string? failure)
    {
        editorId = null;
        parentFormId = null;
        payloadKind = StarfieldAtmospherePayloadKind.Unknown;
        reflectionPayload = null;
        failure = null;

        var fields = new List<(string Signature, int DataOffset, int DataLength)>(3);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;
        while (offset < data.Length)
        {
            if (offset > data.Length - EsmSubrecordUtils.SubrecordHeaderSize)
            {
                failure = "ATMO has a truncated subrecord header.";
                return false;
            }

            var signature = System.Text.Encoding.ASCII.GetString(data.Slice(offset, sizeof(uint)));
            var dataLength = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + sizeof(uint))..]);
            offset += EsmSubrecordUtils.SubrecordHeaderSize;

            // None of the audited 594 retail ATMO records uses XXXX. Rejecting an alternate outer
            // representation keeps the structural contract evidence-bounded.
            if (signature == "XXXX")
            {
                failure = "ATMO does not permit an XXXX extended-length subrecord.";
                return false;
            }

            if (dataLength > data.Length - offset)
            {
                failure = $"ATMO {signature} extends past the record boundary.";
                return false;
            }

            if (signature is not "EDID" and not "RFDP" and not "REFL" and not "RDIF")
            {
                failure = $"ATMO has unsupported outer subrecord '{signature}'.";
                return false;
            }

            if (!seen.Add(signature))
            {
                failure = $"ATMO has duplicate {signature} subrecords.";
                return false;
            }

            fields.Add((signature, offset, dataLength));
            offset += dataLength;
        }

        if (fields.Count == 0 || fields[0].Signature != "EDID")
        {
            failure = "ATMO requires EDID as its first outer subrecord.";
            return false;
        }

        var edidField = fields[0];
        var edidBytes = data.Slice(edidField.DataOffset, edidField.DataLength);
        if (edidBytes.Length < 2 || edidBytes[^1] != 0 || edidBytes[..^1].IndexOf((byte)0) >= 0)
        {
            failure = "ATMO EDID must be one non-empty null-terminated string.";
            return false;
        }

        editorId = EsmStringUtils.DecodeGameText(edidBytes[..^1]);
        if (string.IsNullOrWhiteSpace(editorId))
        {
            failure = "ATMO EDID is empty.";
            return false;
        }

        if (fields.Count == 2 && fields[1].Signature == "REFL")
        {
            payloadKind = StarfieldAtmospherePayloadKind.FullObject;
            reflectionPayload = data.Slice(fields[1].DataOffset, fields[1].DataLength).ToArray();
            return true;
        }

        if (fields.Count == 3 && fields[1].Signature == "RFDP" && fields[2].Signature == "RDIF")
        {
            payloadKind = StarfieldAtmospherePayloadKind.Diff;
            if (fields[1].DataLength != sizeof(uint))
            {
                failure = $"ATMO RFDP must be exactly four bytes; found {fields[1].DataLength}.";
                return false;
            }

            parentFormId = BinaryPrimitives.ReadUInt32LittleEndian(data[fields[1].DataOffset..]);
            if (parentFormId.Value == 0)
            {
                failure = "An ATMO RDIF payload requires a non-zero RFDP parent.";
                return false;
            }

            reflectionPayload = data.Slice(fields[2].DataOffset, fields[2].DataLength).ToArray();
            return true;
        }

        failure =
            "ATMO outer fields must be exactly EDID+REFL or EDID+RFDP+RDIF in retail order.";
        return false;
    }

    private static string? CaptureStarfieldAtmosphereEditorIdPrefix(
        byte[] data,
        int dataSize,
        bool isBigEndian)
    {
        if (dataSize < EsmSubrecordUtils.SubrecordHeaderSize || dataSize > data.Length)
        {
            return null;
        }

        var signature = data.AsSpan(0, sizeof(uint));
        if (!(isBigEndian ? signature.SequenceEqual("DIDE"u8) : signature.SequenceEqual("EDID"u8)))
        {
            return null;
        }

        var length = isBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(sizeof(uint)))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(sizeof(uint)));
        if (length < 2 || EsmSubrecordUtils.SubrecordHeaderSize + length > dataSize)
        {
            return null;
        }

        var payload = data.AsSpan(EsmSubrecordUtils.SubrecordHeaderSize, length);
        if (payload[^1] != 0 || payload[..^1].IndexOf((byte)0) >= 0)
        {
            return null;
        }

        var editorId = EsmStringUtils.DecodeGameText(payload[..^1]);
        return string.IsNullOrWhiteSpace(editorId) ? null : editorId;
    }

    private StarfieldAtmosphereRecord CreateAtmosphereFailure(
        DetectedMainRecord record,
        string failure,
        string? editorId = null,
        uint? parentFormId = null,
        StarfieldAtmospherePayloadKind payloadKind = StarfieldAtmospherePayloadKind.Unknown)
    {
        return new StarfieldAtmosphereRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            ParentFormId = parentFormId,
            PayloadKind = payloadKind,
            DecodeFailure = failure,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Starfield Planet Data

    /// <summary>
    ///     Parses the source-backed PNDT selection subset. Physical records remain in load order so
    ///     master CNAM and ordered EOVR deltas can be folded after plugin rebasing.
    /// </summary>
    internal List<StarfieldPlanetDataRecord> ParseStarfieldPlanetData()
    {
        if (Context.Game != BethesdaGame.Starfield)
        {
            return [];
        }

        return ParseRecordList(
            "PNDT",
            32768,
            ParseStarfieldPlanetDataFromAccessor,
            record => CreatePlanetDataFailure(
                record,
                "PNDT typed fields are unavailable without record-byte access."));
    }

    private StarfieldPlanetDataRecord ParseStarfieldPlanetDataFromAccessor(
        DetectedMainRecord record,
        byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData is null)
        {
            return CreatePlanetDataFailure(record, "PNDT record bytes could not be read.");
        }

        var (data, dataSize) = recordData.Value;
        if (Context.NonContiguousRecordFormIds.Contains(record.FormId) ||
            Context.PartiallyRecoveredFormIds.Contains(record.FormId))
        {
            return CreatePlanetDataFailure(
                record,
                "PNDT typed decoding rejects non-contiguous or partially recovered record bytes.");
        }

        StarfieldPlanetDataDecoder.TryDecode(
            data.AsSpan(0, dataSize),
            record.IsBigEndian,
            out var decoded,
            out var failure);
        var editorId = decoded.EditorId ?? Context.GetEditorId(record.FormId);
        if (!string.IsNullOrWhiteSpace(editorId))
        {
            Context.FormIdToEditorId[record.FormId] = editorId;
        }

        return decoded with
        {
            FormId = record.FormId,
            EditorId = editorId,
            DecodeFailure = failure ?? decoded.DecodeFailure,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private StarfieldPlanetDataRecord CreatePlanetDataFailure(
        DetectedMainRecord record,
        string failure)
    {
        return new StarfieldPlanetDataRecord
        {
            FormId = record.FormId,
            EditorId = Context.GetEditorId(record.FormId),
            DecodeFailure = failure,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Starfield Star Data

    /// <summary>
    ///     Parses the bounded Starfield STDT routing subset. The large stellar-presentation payloads
    ///     remain opaque; only EDID and the established DNAM/SNAM/PNAM/HNAM scalar/reference fields
    ///     are projected. Malformed records remain as failure envelopes so a later broken override
    ///     cannot expose an earlier valid definition.
    /// </summary>
    internal List<StarfieldStarDataRecord> ParseStarfieldStarData()
    {
        if (Context.Game != BethesdaGame.Starfield)
        {
            return [];
        }

        return ParseRecordList(
            "STDT",
            131072, // Audited retail maximum body is 0x185CD bytes.
            ParseStarfieldStarDataFromAccessor,
            record => CreateStarDataFailure(
                record,
                "STDT typed routing fields are unavailable without record-byte access."));
    }

    private StarfieldStarDataRecord ParseStarfieldStarDataFromAccessor(
        DetectedMainRecord record,
        byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData is null)
        {
            return CreateStarDataFailure(record, "STDT record bytes could not be read.");
        }

        var (data, dataSize) = recordData.Value;
        var editorIdPrefix = CaptureLeadingStarfieldEditorId(
            data, dataSize, record.IsBigEndian);
        if (!string.IsNullOrWhiteSpace(editorIdPrefix))
        {
            Context.FormIdToEditorId[record.FormId] = editorIdPrefix;
        }

        if (Context.NonContiguousRecordFormIds.Contains(record.FormId) ||
            Context.PartiallyRecoveredFormIds.Contains(record.FormId))
        {
            return CreateStarDataFailure(
                record,
                "STDT typed decoding rejects non-contiguous or partially recovered record bytes.",
                editorIdPrefix);
        }

        StarfieldStarDataDecoder.TryDecode(
            data.AsSpan(0, dataSize),
            record.IsBigEndian,
            out var decoded,
            out var failure);
        var editorId = decoded.EditorId ?? editorIdPrefix ?? Context.GetEditorId(record.FormId);
        if (!string.IsNullOrWhiteSpace(editorId))
        {
            Context.FormIdToEditorId[record.FormId] = editorId;
        }

        return decoded with
        {
            FormId = record.FormId,
            EditorId = editorId,
            DecodeFailure = failure ?? decoded.DecodeFailure,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private StarfieldStarDataRecord CreateStarDataFailure(
        DetectedMainRecord record,
        string failure,
        string? editorId = null)
    {
        return new StarfieldStarDataRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            DecodeFailure = failure,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Starfield Sun Presets

    /// <summary>
    ///     Parses the exact bounded Starfield SUNP outer contract: EDID+REFL for a full root, or
    ///     EDID+RFDP+RDIF for an inheritance diff. The reflected decoder authenticates the complete
    ///     four-class retail schema; no solar interpolation or lighting equation is inferred here.
    /// </summary>
    internal List<StarfieldSunPresetRecord> ParseStarfieldSunPresets()
    {
        if (Context.Game != BethesdaGame.Starfield)
        {
            return [];
        }

        return ParseRecordList(
            "SUNP",
            4096,
            ParseStarfieldSunPresetFromAccessor,
            record => CreateSunPresetFailure(
                record,
                "SUNP reflection payload is unavailable without record-byte access."));
    }

    private StarfieldSunPresetRecord ParseStarfieldSunPresetFromAccessor(
        DetectedMainRecord record,
        byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData is null)
        {
            return CreateSunPresetFailure(record, "SUNP record bytes could not be read.");
        }

        var (data, dataSize) = recordData.Value;
        var editorIdPrefix = CaptureLeadingStarfieldEditorId(
            data, dataSize, record.IsBigEndian);
        if (!string.IsNullOrWhiteSpace(editorIdPrefix))
        {
            Context.FormIdToEditorId[record.FormId] = editorIdPrefix;
        }

        if (record.IsBigEndian)
        {
            return CreateSunPresetFailure(
                record,
                "Starfield SUNP reflection streams are supported only in little-endian records.",
                editorIdPrefix);
        }

        if (Context.NonContiguousRecordFormIds.Contains(record.FormId) ||
            Context.PartiallyRecoveredFormIds.Contains(record.FormId))
        {
            return CreateSunPresetFailure(
                record,
                "SUNP reflection decoding rejects non-contiguous or partially recovered record bytes.",
                editorIdPrefix);
        }

        if (!TryReadStarfieldSunPresetEnvelope(
                data.AsSpan(0, dataSize),
                out var editorId,
                out var parentFormId,
                out var payloadKind,
                out var reflectionPayload,
                out var failure))
        {
            return CreateSunPresetFailure(
                record,
                failure ?? "SUNP outer-record validation failed.",
                editorId ?? editorIdPrefix,
                parentFormId,
                payloadKind);
        }

        Context.FormIdToEditorId[record.FormId] = editorId!;
        if (!StarfieldSunPresetDecoder.TryDecode(
                reflectionPayload!, payloadKind, out var patch, out failure))
        {
            return CreateSunPresetFailure(
                record,
                failure ?? "SUNP reflection decoding failed.",
                editorId,
                parentFormId,
                payloadKind);
        }

        if (payloadKind == StarfieldSunPresetPayloadKind.FullObject && patch!.ParentFormId != 0)
        {
            return CreateSunPresetFailure(
                record,
                "A root SUNP REFL payload must declare a null pParent.",
                editorId,
                parentFormId,
                payloadKind);
        }

        if (payloadKind == StarfieldSunPresetPayloadKind.Diff &&
            (patch!.ParentFormId is not { } reflectedParent ||
             reflectedParent != parentFormId!.Value))
        {
            return CreateSunPresetFailure(
                record,
                "SUNP RFDP requires an explicitly authored equal reflected pParent.",
                editorId,
                parentFormId,
                payloadKind);
        }

        return new StarfieldSunPresetRecord
        {
            FormId = record.FormId,
            EditorId = editorId,
            ParentFormId = parentFormId,
            PayloadKind = payloadKind,
            Patch = patch,
            Offset = record.Offset,
            IsBigEndian = false
        };
    }

    private static bool TryReadStarfieldSunPresetEnvelope(
        ReadOnlySpan<byte> data,
        out string? editorId,
        out uint? parentFormId,
        out StarfieldSunPresetPayloadKind payloadKind,
        out byte[]? reflectionPayload,
        out string? failure)
    {
        editorId = null;
        parentFormId = null;
        payloadKind = StarfieldSunPresetPayloadKind.Unknown;
        reflectionPayload = null;
        failure = null;

        var fields = new List<(string Signature, int DataOffset, int DataLength)>(3);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;
        while (offset < data.Length)
        {
            if (offset > data.Length - EsmSubrecordUtils.SubrecordHeaderSize)
            {
                failure = "SUNP has a truncated subrecord header.";
                return false;
            }

            var signature = System.Text.Encoding.ASCII.GetString(data.Slice(offset, sizeof(uint)));
            var dataLength = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + sizeof(uint))..]);
            offset += EsmSubrecordUtils.SubrecordHeaderSize;

            // The complete 52-record retail inventory contains no outer XXXX marker. Rejecting a
            // different representation keeps this structural contract bounded to observed source.
            if (signature == "XXXX")
            {
                failure = "SUNP does not permit an XXXX extended-length subrecord.";
                return false;
            }

            if (dataLength > data.Length - offset)
            {
                failure = $"SUNP {signature} extends past the record boundary.";
                return false;
            }

            if (signature is not "EDID" and not "RFDP" and not "REFL" and not "RDIF")
            {
                failure = $"SUNP has unsupported outer subrecord '{signature}'.";
                return false;
            }

            if (!seen.Add(signature))
            {
                failure = $"SUNP has duplicate {signature} subrecords.";
                return false;
            }

            fields.Add((signature, offset, dataLength));
            offset += dataLength;
        }

        var isFull = fields.Count == 2 &&
                     fields[0].Signature == "EDID" &&
                     fields[1].Signature == "REFL";
        var isDiff = fields.Count == 3 &&
                     fields[0].Signature == "EDID" &&
                     fields[1].Signature == "RFDP" &&
                     fields[2].Signature == "RDIF";
        payloadKind = isFull
            ? StarfieldSunPresetPayloadKind.FullObject
            : isDiff
                ? StarfieldSunPresetPayloadKind.Diff
                : StarfieldSunPresetPayloadKind.Unknown;

        if (!isFull && !isDiff)
        {
            failure =
                "SUNP outer fields must be exactly EDID+REFL or EDID+RFDP+RDIF in retail order.";
            return false;
        }

        var edidField = fields[0];
        var edidBytes = data.Slice(edidField.DataOffset, edidField.DataLength);
        if (edidBytes.Length < 2 || edidBytes[^1] != 0 ||
            edidBytes[..^1].IndexOf((byte)0) >= 0)
        {
            failure = "SUNP EDID must be one non-empty null-terminated string.";
            return false;
        }

        editorId = EsmStringUtils.DecodeGameText(edidBytes[..^1]);
        if (string.IsNullOrWhiteSpace(editorId))
        {
            failure = "SUNP EDID is empty.";
            return false;
        }

        if (isFull)
        {
            reflectionPayload = data.Slice(fields[1].DataOffset, fields[1].DataLength).ToArray();
            return true;
        }

        if (fields[1].DataLength != sizeof(uint))
        {
            failure = $"SUNP RFDP must be exactly four bytes; found {fields[1].DataLength}.";
            return false;
        }

        parentFormId = BinaryPrimitives.ReadUInt32LittleEndian(data[fields[1].DataOffset..]);
        if (parentFormId.Value == 0)
        {
            failure = "A SUNP RDIF payload requires a non-zero RFDP parent.";
            return false;
        }

        reflectionPayload = data.Slice(fields[2].DataOffset, fields[2].DataLength).ToArray();
        return true;
    }

    private static string? CaptureLeadingStarfieldEditorId(
        byte[] data,
        int dataSize,
        bool isBigEndian)
    {
        if (dataSize < EsmSubrecordUtils.SubrecordHeaderSize || dataSize > data.Length)
        {
            return null;
        }

        var signature = data.AsSpan(0, sizeof(uint));
        if (!(isBigEndian ? signature.SequenceEqual("DIDE"u8) : signature.SequenceEqual("EDID"u8)))
        {
            return null;
        }

        var length = isBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(sizeof(uint)))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(sizeof(uint)));
        if (length < 2 || EsmSubrecordUtils.SubrecordHeaderSize + length > dataSize)
        {
            return null;
        }

        var payload = data.AsSpan(EsmSubrecordUtils.SubrecordHeaderSize, length);
        if (payload[^1] != 0 || payload[..^1].IndexOf((byte)0) >= 0)
        {
            return null;
        }

        var editorId = EsmStringUtils.DecodeGameText(payload[..^1]);
        return string.IsNullOrWhiteSpace(editorId) ? null : editorId;
    }

    private StarfieldSunPresetRecord CreateSunPresetFailure(
        DetectedMainRecord record,
        string failure,
        string? editorId = null,
        uint? parentFormId = null,
        StarfieldSunPresetPayloadKind payloadKind = StarfieldSunPresetPayloadKind.Unknown)
    {
        return new StarfieldSunPresetRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            ParentFormId = parentFormId,
            PayloadKind = payloadKind,
            DecodeFailure = failure,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Starfield 3D Curves

    /// <summary>
    ///     Parses the bounded Starfield CUR3 outer contract. Retail CUR3 records are standalone
    ///     <c>EDID+REFL</c> objects. The reflected decoder retains the three authored axes exactly;
    ///     this path does not define curve sampling or water-shader semantics.
    /// </summary>
    internal List<StarfieldCurve3DRecord> ParseStarfieldCurves3D()
    {
        if (Context.Game != BethesdaGame.Starfield)
        {
            return [];
        }

        return ParseRecordList(
            "CUR3",
            2048, // Audited retail maximum REFL body is 1017 bytes.
            ParseStarfieldCurve3DFromAccessor,
            record => CreateCurve3DFailure(
                record,
                "CUR3 reflection payload is unavailable without record-byte access."));
    }

    private StarfieldCurve3DRecord ParseStarfieldCurve3DFromAccessor(
        DetectedMainRecord record,
        byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData is null)
        {
            return CreateCurve3DFailure(record, "CUR3 record bytes could not be read.");
        }

        var (data, dataSize) = recordData.Value;
        var editorIdPrefix = CaptureLeadingStarfieldEditorId(
            data, dataSize, record.IsBigEndian);
        if (!string.IsNullOrWhiteSpace(editorIdPrefix))
        {
            Context.FormIdToEditorId[record.FormId] = editorIdPrefix;
        }

        if (record.IsBigEndian)
        {
            return CreateCurve3DFailure(
                record,
                "Starfield CUR3 reflection streams are supported only in little-endian records.",
                editorIdPrefix);
        }

        if (Context.NonContiguousRecordFormIds.Contains(record.FormId) ||
            Context.PartiallyRecoveredFormIds.Contains(record.FormId))
        {
            return CreateCurve3DFailure(
                record,
                "CUR3 reflection decoding rejects non-contiguous or partially recovered record bytes.",
                editorIdPrefix);
        }

        if (!TryReadStarfieldCurve3DEnvelope(
                data.AsSpan(0, dataSize),
                out var editorId,
                out var reflectionPayload,
                out var failure))
        {
            return CreateCurve3DFailure(
                record,
                failure ?? "CUR3 outer-record validation failed.",
                editorId ?? editorIdPrefix);
        }

        Context.FormIdToEditorId[record.FormId] = editorId!;
        if (!StarfieldCurve3DDecoder.TryDecode(
                reflectionPayload!, out var definition, out failure))
        {
            return CreateCurve3DFailure(
                record,
                failure ?? "CUR3 reflection decoding failed.",
                editorId);
        }

        return new StarfieldCurve3DRecord
        {
            FormId = record.FormId,
            EditorId = editorId,
            Definition = definition,
            Offset = record.Offset,
            IsBigEndian = false
        };
    }

    private static bool TryReadStarfieldCurve3DEnvelope(
        ReadOnlySpan<byte> data,
        out string? editorId,
        out byte[]? reflectionPayload,
        out string? failure)
    {
        editorId = null;
        reflectionPayload = null;
        failure = null;

        var fields = new List<(string Signature, int DataOffset, int DataLength)>(2);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;
        while (offset < data.Length)
        {
            if (offset > data.Length - EsmSubrecordUtils.SubrecordHeaderSize)
            {
                failure = "CUR3 has a truncated subrecord header.";
                return false;
            }

            var signature = System.Text.Encoding.ASCII.GetString(data.Slice(offset, sizeof(uint)));
            var dataLength = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + sizeof(uint))..]);
            offset += EsmSubrecordUtils.SubrecordHeaderSize;

            if (signature == "XXXX")
            {
                failure = "CUR3 does not permit an XXXX extended-length subrecord.";
                return false;
            }

            if (dataLength > data.Length - offset)
            {
                failure = $"CUR3 {signature} extends past the record boundary.";
                return false;
            }

            if (signature is not "EDID" and not "REFL")
            {
                failure = $"CUR3 has unsupported outer subrecord '{signature}'.";
                return false;
            }

            if (!seen.Add(signature))
            {
                failure = $"CUR3 has duplicate {signature} subrecords.";
                return false;
            }

            fields.Add((signature, offset, dataLength));
            offset += dataLength;
        }

        if (fields.Count != 2 || fields[0].Signature != "EDID" || fields[1].Signature != "REFL")
        {
            failure = "CUR3 outer fields must be exactly EDID+REFL in retail order.";
            return false;
        }

        var edidField = fields[0];
        var edidBytes = data.Slice(edidField.DataOffset, edidField.DataLength);
        if (edidBytes.Length < 2 || edidBytes[^1] != 0 ||
            edidBytes[..^1].IndexOf((byte)0) >= 0)
        {
            failure = "CUR3 EDID must be one non-empty null-terminated string.";
            return false;
        }

        foreach (var value in edidBytes[..^1])
        {
            if (value <= 0x7F)
            {
                continue;
            }

            failure = "CUR3 EDID must contain ASCII bytes only.";
            return false;
        }

        editorId = System.Text.Encoding.ASCII.GetString(edidBytes[..^1]);
        if (string.IsNullOrWhiteSpace(editorId))
        {
            failure = "CUR3 EDID is empty.";
            return false;
        }

        var reflectionField = fields[1];
        if (reflectionField.DataLength == 0)
        {
            failure = "CUR3 REFL must not be empty.";
            return false;
        }

        reflectionPayload = data.Slice(
            reflectionField.DataOffset, reflectionField.DataLength).ToArray();
        return true;
    }

    private StarfieldCurve3DRecord CreateCurve3DFailure(
        DetectedMainRecord record,
        string failure,
        string? editorId = null)
    {
        return new StarfieldCurve3DRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            DecodeFailure = failure,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Starfield Volumetric Lighting

    /// <summary>
    ///     Parse complete Starfield volumetric-lighting definitions (VOLI). Retail VOLI records
    ///     carry one full REFL object and have no reflected-diff inheritance representation.
    /// </summary>
    internal List<StarfieldVolumetricLightingRecord> ParseStarfieldVolumetricLightingSettings()
    {
        if (Context.Game != BethesdaGame.Starfield)
        {
            return [];
        }

        return ParseRecordList(
            "VOLI",
            4096,
            ParseStarfieldVolumetricLightingFromAccessor,
            record => CreateVolumetricLightingFailure(
                record,
                "VOLI reflection payload is unavailable without record-byte access."));
    }

    private StarfieldVolumetricLightingRecord ParseStarfieldVolumetricLightingFromAccessor(
        DetectedMainRecord record,
        byte[] buffer)
    {
        if (!TryReadStandaloneStarfieldReflection(
                record, buffer, "VOLI", out var editorId, out var reflectionPayload, out var failure))
        {
            return CreateVolumetricLightingFailure(
                record,
                failure ?? "VOLI outer-record validation failed.",
                editorId);
        }

        if (!StarfieldVolumetricLightingDecoder.TryDecode(
                reflectionPayload!, out var settings, out failure))
        {
            return CreateVolumetricLightingFailure(
                record,
                failure ?? "VOLI reflection decoding failed.",
                editorId);
        }

        return new StarfieldVolumetricLightingRecord
        {
            FormId = record.FormId,
            EditorId = editorId,
            Settings = settings,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private StarfieldVolumetricLightingRecord CreateVolumetricLightingFailure(
        DetectedMainRecord record,
        string failure,
        string? editorId = null)
    {
        return new StarfieldVolumetricLightingRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            DecodeFailure = failure,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Starfield Cloud Forms

    /// <summary>
    ///     Parse complete Starfield cloud definitions (CLDF). Retail CLDF records carry one full
    ///     REFL object and have no reflected-diff inheritance representation.
    /// </summary>
    internal List<StarfieldCloudFormRecord> ParseStarfieldCloudForms()
    {
        if (Context.Game != BethesdaGame.Starfield)
        {
            return [];
        }

        return ParseRecordList(
            "CLDF",
            8192, // Retail bodies reach 0x1285; keep the shared buffer above that boundary.
            ParseStarfieldCloudFormFromAccessor,
            record => CreateCloudFormFailure(
                record,
                "CLDF reflection payload is unavailable without record-byte access."));
    }

    private StarfieldCloudFormRecord ParseStarfieldCloudFormFromAccessor(
        DetectedMainRecord record,
        byte[] buffer)
    {
        if (!TryReadStandaloneStarfieldReflection(
                record, buffer, "CLDF", out var editorId, out var reflectionPayload, out var failure))
        {
            return CreateCloudFormFailure(
                record,
                failure ?? "CLDF outer-record validation failed.",
                editorId);
        }

        if (!StarfieldCloudFormDecoder.TryDecode(
                reflectionPayload!, out var definition, out failure))
        {
            return CreateCloudFormFailure(
                record,
                failure ?? "CLDF reflection decoding failed.",
                editorId);
        }

        return new StarfieldCloudFormRecord
        {
            FormId = record.FormId,
            EditorId = editorId,
            Definition = definition,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private StarfieldCloudFormRecord CreateCloudFormFailure(
        DetectedMainRecord record,
        string failure,
        string? editorId = null)
    {
        return new StarfieldCloudFormRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            DecodeFailure = failure,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    /// <summary>
    ///     Reads the exact outer subrecord envelope shared by standalone Starfield reflection
    ///     definitions. The typed decoder receives bytes only after the complete little-endian
    ///     framing and the one-full-REFL/no-inheritance contract have been established.
    /// </summary>
    private bool TryReadStandaloneStarfieldReflection(
        DetectedMainRecord record,
        byte[] buffer,
        string recordType,
        out string? editorId,
        out byte[]? reflectionPayload,
        out string? failure)
    {
        editorId = Context.GetEditorId(record.FormId);
        reflectionPayload = null;
        failure = null;

        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            failure = $"{recordType} record bytes could not be read.";
            return false;
        }

        var (data, dataSize) = recordData.Value;
        CaptureStandaloneReflectionEditorIdPrefix(
            data, dataSize, record.IsBigEndian, record.FormId, ref editorId);

        if (record.IsBigEndian)
        {
            failure = $"Starfield {recordType} reflection streams are supported only in little-endian records.";
            return false;
        }

        if (Context.NonContiguousRecordFormIds.Contains(record.FormId) ||
            Context.PartiallyRecoveredFormIds.Contains(record.FormId))
        {
            failure =
                $"{recordType} reflection decoding rejects non-contiguous or partially recovered record bytes.";
            return false;
        }

        if (!HasCompleteLittleEndianSubrecordLayout(data, dataSize))
        {
            failure = $"{recordType} has a truncated or malformed outer subrecord layout.";
            return false;
        }

        var editorIdSeen = false;
        var reflectionPayloadCount = 0;
        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, false))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);
            switch (sub.Signature)
            {
                case "EDID":
                    if (editorIdSeen)
                    {
                        failure ??= $"{recordType} has duplicate EDID subrecords.";
                        break;
                    }

                    editorIdSeen = true;
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                case "REFL":
                    reflectionPayloadCount++;
                    reflectionPayload ??= subData.ToArray();
                    break;
                case "RDIF":
                    failure ??= $"{recordType} does not support RDIF reflection diffs.";
                    break;
                case "RFDP":
                    failure ??= $"{recordType} does not support RFDP reflection inheritance.";
                    break;
                default:
                    NoteUnmodeledSubrecord(recordType, sub.Signature, sub.DataLength);
                    failure ??=
                        $"{recordType} has unsupported outer subrecord '{sub.Signature}'.";
                    break;
            }
        }

        if (reflectionPayloadCount != 1)
        {
            failure ??= $"{recordType} must contain exactly one REFL payload.";
        }

        return failure == null;
    }

    /// <summary>
    ///     Preserves a complete leading EDID even when a later subrecord is malformed or truncated.
    ///     The ordinary iterator yields only wholly resident subrecords, so this never consumes the
    ///     damaged tail and does not weaken the complete-layout gate above.
    /// </summary>
    private void CaptureStandaloneReflectionEditorIdPrefix(
        byte[] data,
        int dataSize,
        bool isBigEndian,
        uint formId,
        ref string? editorId)
    {
        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, isBigEndian))
        {
            if (sub.Signature != "EDID")
            {
                continue;
            }

            editorId = EsmStringUtils.ReadNullTermString(
                data.AsSpan(sub.DataOffset, sub.DataLength));
            if (!string.IsNullOrEmpty(editorId))
            {
                Context.FormIdToEditorId[formId] = editorId;
            }

            return;
        }
    }

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
        IReadOnlyList<ClimateWeatherSettingsEntry>? weatherSettingsTypes = null;
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
                // Starfield separates CE2 reflected weather settings (WTHS) from the three retained
                // legacy WTHR records. WSLT uses the same 12-byte (FormID, chance, global) tuple as
                // WLST, but its FormIDs MUST stay in a separate domain: resolving one through the WTHR
                // index would silently substitute the wrong atmosphere architecture.
                case "WSLT" when Context.Game == BethesdaGame.Starfield && sub.DataLength >= 12:
                    weatherSettingsTypes = ReadClimateWeatherSettingsList(subData, record.IsBigEndian);
                    break;
                // TNAM: five common bytes (sunrise/sunset begin+end, volatility), followed by the
                // legacy moon/phase byte when present. Retail Starfield CLMT authors exactly five;
                // xEdit's SF1 definition deliberately omits the phase field.
                case "TNAM" when sub.DataLength >= 5:
                    timing = new ClimateTimingData(
                        subData[0], subData[1], subData[2], subData[3], subData[4],
                        sub.DataLength >= 6 ? subData[5] : (byte)0)
                    {
                        HasMoonPhaseLength = sub.DataLength >= 6
                    };
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
            WeatherSettingsTypes = weatherSettingsTypes ?? [],
            Timing = timing,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private static List<ClimateWeatherEntry> ReadClimateWeatherList(ReadOnlySpan<byte> data, bool isBigEndian,
        int entryBytes)
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

    private static List<ClimateWeatherSettingsEntry> ReadClimateWeatherSettingsList(
        ReadOnlySpan<byte> data,
        bool isBigEndian)
    {
        const int entryBytes = 12;
        var count = data.Length / entryBytes;
        var list = new List<ClimateWeatherSettingsEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var b = i * entryBytes;
            var weatherSettings = RecordParserContext.ReadFormId(data.Slice(b, 4), isBigEndian);
            var chance = isBigEndian
                ? BinaryPrimitives.ReadInt32BigEndian(data.Slice(b + 4, 4))
                : BinaryPrimitives.ReadInt32LittleEndian(data.Slice(b + 4, 4));
            var global = RecordParserContext.ReadFormId(data.Slice(b + 8, 4), isBigEndian);
            list.Add(new ClimateWeatherSettingsEntry(weatherSettings, chance, global));
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
                IsBigEndian = record.IsBigEndian
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
                IsBigEndian = record.IsBigEndian
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
            IsBigEndian = record.IsBigEndian
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
            RawPayload = payload
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
                        LumRampNoTex = ReadFloat(subData, 32, record.IsBigEndian)
                    };
                    break;
                case "CNAM" when sub.DataLength >= 12:
                    cinematic = new ImageSpaceCinematic
                    {
                        HasExplicitFlags = false,
                        Saturation = ReadFloat(subData, 0, record.IsBigEndian),
                        Brightness = ReadFloat(subData, 4, record.IsBigEndian),
                        Contrast = ReadFloat(subData, 8, record.IsBigEndian)
                    };
                    break;
                case "TNAM" when sub.DataLength >= 16:
                    tint = new ImageSpaceTint
                    {
                        Amount = ReadFloat(subData, 0, record.IsBigEndian),
                        Red = ReadFloat(subData, 4, record.IsBigEndian),
                        Green = ReadFloat(subData, 8, record.IsBigEndian),
                        Blue = ReadFloat(subData, 12, record.IsBigEndian)
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
                ?
                [
                    depthOfFieldData.Strength, depthOfFieldData.Distance, depthOfFieldData.Range,
                    depthOfFieldData.SkyBlurRadius, vignetteRadius,
                    depthOfFieldData.VignetteStrength.GetValueOrDefault()
                ]
                :
                [
                    depthOfFieldData.Strength, depthOfFieldData.Distance, depthOfFieldData.Range,
                    depthOfFieldData.SkyBlurRadius
                ];
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
                MiddleGray = ReadFloat(data, 32, isBigEndian)
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
                EyeAdaptStrength = ReadFloat(data, 32, isBigEndian)
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
                SkyScale = ReadFloat(data, 24, isBigEndian)
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
                SkyScale = ReadFloat(data, 24, isBigEndian)
            };
        return new ImageSpacePackedData(
            modernHdr,
            new ImageSpaceCinematic
            {
                HasExplicitFlags = false,
                Saturation = ReadFloat(data, 28, isBigEndian),
                Brightness = ReadFloat(data, 32, isBigEndian),
                Contrast = ReadFloat(data, 36, isBigEndian)
            },
            new ImageSpaceTint
            {
                Amount = ReadFloat(data, 40, isBigEndian),
                Red = ReadFloat(data, 44, isBigEndian),
                Green = ReadFloat(data, 48, isBigEndian),
                Blue = ReadFloat(data, 52, isBigEndian)
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
            RawData = data.ToArray()
        };
    }

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
}
