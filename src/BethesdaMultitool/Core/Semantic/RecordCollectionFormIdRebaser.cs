using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.Semantic;

/// <summary>
///     Rebases parsed FormIDs in semantic ESM model objects using an explicit FormID property registry.
/// </summary>
internal static class RecordCollectionFormIdRebaser
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> WritablePropertyCache = new();

    /// <summary>
    ///     Deep-clones a record collection, rewriting every registered FormID-bearing property through
    ///     <paramref name="mapFormId" />.
    /// </summary>
    internal static RecordCollection Rebase(RecordCollection records, Func<uint, uint> mapFormId)
    {
        return (RecordCollection)CloneValue(records, nameof(RecordCollection), mapFormId)!;
    }

    private static object? CloneValue(object? value, string propertyName, Func<uint, uint> mapFormId)
    {
        if (value == null)
        {
            return null;
        }

        var type = value.GetType();
        if (type == typeof(string) || type.IsEnum || type == typeof(decimal))
        {
            return value;
        }

        if (type == typeof(uint))
        {
            return EsmFormIdPropertyRegistry.IsFormIdProperty(propertyName)
                ? mapFormId((uint)value)
                : value;
        }

        if (type == typeof(uint?))
        {
            var nullable = (uint?)value;
            return nullable.HasValue && EsmFormIdPropertyRegistry.IsFormIdProperty(propertyName)
                ? mapFormId(nullable.Value)
                : nullable;
        }

        if (type.IsPrimitive)
        {
            return value;
        }

        // WSLT entries are immutable positional records, so the ordinary property-by-property clone
        // below cannot construct them. Keep this narrow: these are the two FormIDs authored in each
        // Starfield CLMT WSLT tuple (WTHS target and optional GLOB gate).
        if (value is ClimateWeatherSettingsEntry weatherSettingsEntry)
        {
            return new ClimateWeatherSettingsEntry(
                mapFormId(weatherSettingsEntry.WeatherSettingsFormId),
                weatherSettingsEntry.Chance,
                mapFormId(weatherSettingsEntry.GlobalFormId));
        }

        // CLDF definitions are also immutable positional records. Copy their IReadOnlyList containers
        // to honor the rebaser's deep-clone contract; the layer/plane elements are immutable records.
        // Their only FormID is the optional cloud-card sequence reference; zero is the authored
        // "no sequence" sentinel and must not be handed to a mapper whose contract only covers actual FormIDs.
        if (value is StarfieldCloudFormDefinition cloudFormDefinition)
        {
            return new StarfieldCloudFormDefinition(
                cloudFormDefinition.Shadows,
                new List<StarfieldCloudLayer>(cloudFormDefinition.Layers),
                new List<StarfieldCloudPlane>(cloudFormDefinition.Planes),
                cloudFormDefinition.CloudCardSequenceFormId == 0
                    ? 0
                    : mapFormId(cloudFormDefinition.CloudCardSequenceFormId));
        }

        // ATMO envelopes and patches are immutable records whose nullable references distinguish
        // absent DIFF members (null) from authored null references (zero). Clone them explicitly so
        // every actual FormID is rebased, zero never reaches the mapper, and the source patch is not
        // aliased into the rebased collection.
        if (value is StarfieldAtmospherePatch atmospherePatch)
        {
            return CloneAtmospherePatch(atmospherePatch, mapFormId);
        }

        if (value is StarfieldAtmosphereRecord atmosphereRecord)
        {
            return atmosphereRecord with
            {
                FormId = MapNonZeroFormId(atmosphereRecord.FormId, mapFormId),
                ParentFormId = MapOptionalFormId(atmosphereRecord.ParentFormId, mapFormId),
                Patch = atmosphereRecord.Patch is null
                    ? null
                    : CloneAtmospherePatch(atmosphereRecord.Patch, mapFormId)
            };
        }

        // PNDT mixes genuine FormIDs with scalar UInt32 identifiers and raw coordinate bits. Its
        // dedicated rebaser knows the exact boundary; generic property-name cloning must not infer it.
        if (value is StarfieldPlanetDataRecord planetDataRecord)
        {
            return StarfieldPlanetDataFormIdRebaser.Rebase(planetDataRecord, mapFormId);
        }

        // STDT's DNAM is a scalar system identifier while SNAM/PNAM/HNAM are FormIDs. Delegate to
        // the exact typed boundary so a numerically FormID-shaped system ID is never load-order mapped.
        if (value is StarfieldStarDataRecord starDataRecord)
        {
            return StarfieldStarDataFormIdRebaser.Rebase(starDataRecord, mapFormId);
        }

        // SUNP has only three FormID positions: the record envelope, outer RFDP, and reflected
        // pParent. Every other UInt32/float is scalar presentation data. The explicit clone also
        // preserves null (DIFF omission), authored zero, and the deep-clone contract.
        if (value is StarfieldSunPresetPatch sunPresetPatch)
        {
            return CloneSunPresetPatch(sunPresetPatch, mapFormId);
        }

        if (value is StarfieldSunPresetRecord sunPresetRecord)
        {
            return sunPresetRecord with
            {
                FormId = MapNonZeroFormId(sunPresetRecord.FormId, mapFormId),
                ParentFormId = MapOptionalFormId(sunPresetRecord.ParentFormId, mapFormId),
                Patch = sunPresetRecord.Patch is null
                    ? null
                    : CloneSunPresetPatch(sunPresetRecord.Patch, mapFormId)
            };
        }

        // CUR3 contains no FormID-bearing content: its serializer marker, float bit patterns, and
        // control values are scalars. Rebase only the record envelope and deep-clone every retained
        // container so the mapper can never be invoked for numerically FormID-shaped curve data.
        if (value is StarfieldCurve3DDefinition curve3DDefinition)
        {
            return CloneCurve3DDefinition(curve3DDefinition);
        }

        if (value is StarfieldFloatCurve floatCurve)
        {
            return CloneFloatCurve(floatCurve);
        }

        if (value is StarfieldCurve3DRecord curve3DRecord)
        {
            return curve3DRecord with
            {
                FormId = MapNonZeroFormId(curve3DRecord.FormId, mapFormId),
                Definition = curve3DRecord.Definition is null
                    ? null
                    : CloneCurve3DDefinition(curve3DRecord.Definition)
            };
        }

        // FO76 WTHR HNAM is an immutable positional WeatherTimeBands<uint>. Rebase each actual VOLI
        // reference while preserving both authored zero and absent optional slots; the generic object
        // clone below cannot construct positional records and would otherwise return the source object.
        if (value is WeatherTimeBands<uint> weatherFormIds &&
            EsmFormIdPropertyRegistry.IsFormIdProperty(propertyName))
        {
            uint MapRequired(uint formId) => formId == 0 ? 0 : mapFormId(formId);
            uint? MapOptional(uint? formId) => formId switch
            {
                null => null,
                0 => 0,
                _ => mapFormId(formId.Value)
            };

            return new WeatherTimeBands<uint>(
                MapRequired(weatherFormIds.Sunrise),
                MapRequired(weatherFormIds.Day),
                MapRequired(weatherFormIds.Sunset),
                MapRequired(weatherFormIds.Night))
            {
                HighNoon = MapOptional(weatherFormIds.HighNoon),
                Midnight = MapOptional(weatherFormIds.Midnight),
                EarlySunrise = MapOptional(weatherFormIds.EarlySunrise),
                LateSunrise = MapOptional(weatherFormIds.LateSunrise),
                EarlySunset = MapOptional(weatherFormIds.EarlySunset),
                LateSunset = MapOptional(weatherFormIds.LateSunset)
            };
        }

        if (type.IsArray)
        {
            // A uint[] whose property is a registered FormID property (e.g. the Morrowind LAND grid's
            // VtexTextureFormIds) holds FormIDs and must be rebased element-by-element. Every other array
            // (byte[] payloads, the raw VTEX TextureIndices grid, etc.) is opaque data and passes through
            // unchanged — the name gate keeps raw uint indices from being mistaken for FormIDs. (FormID
            // *lists* are handled separately in CloneList.)
            return value is uint[] uintArray && EsmFormIdPropertyRegistry.IsFormIdProperty(propertyName)
                ? RebaseUIntArray(uintArray, mapFormId)
                : value;
        }

        if (value is IDictionary dictionary)
        {
            return CloneDictionary(dictionary, propertyName, mapFormId);
        }

        if (value is IList list)
        {
            return CloneList(list, propertyName, mapFormId);
        }

        if (!IsEsmModelType(type))
        {
            return value;
        }

        object clone;
        try
        {
            clone = Activator.CreateInstance(type)!;
        }
        catch (MissingMethodException)
        {
            return value;
        }

        foreach (var property in GetWritableProperties(type))
        {
            if (type == typeof(RecordCollection) && property.Name == nameof(RecordCollection.DialogueTree))
            {
                property.SetValue(clone, null);
                continue;
            }

            var originalPropertyValue = property.GetValue(value);
            var clonedPropertyValue = CloneValue(originalPropertyValue, property.Name, mapFormId);
            property.SetValue(clone, clonedPropertyValue);
        }

        return clone;
    }

    private static uint[] RebaseUIntArray(uint[] source, Func<uint, uint> mapFormId)
    {
        var result = new uint[source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            result[i] = mapFormId(source[i]);
        }

        return result;
    }

    private static StarfieldAtmospherePatch CloneAtmospherePatch(
        StarfieldAtmospherePatch source,
        Func<uint, uint> mapFormId)
    {
        return new StarfieldAtmospherePatch
        {
            ParentFormId = MapOptionalFormId(source.ParentFormId, mapFormId),
            SunPresetOverrideFormId = MapOptionalFormId(source.SunPresetOverrideFormId, mapFormId),
            ClimateOverrideFormId = MapOptionalFormId(source.ClimateOverrideFormId, mapFormId)
        };
    }

    private static StarfieldSunPresetPatch CloneSunPresetPatch(
        StarfieldSunPresetPatch source,
        Func<uint, uint> mapFormId)
    {
        return new StarfieldSunPresetPatch
        {
            ParentFormId = MapOptionalFormId(source.ParentFormId, mapFormId),
            SunColor = CloneSunPresetFloat4(source.SunColor),
            SunIlluminance = source.SunIlluminance,
            SunGlareColor = CloneSunPresetFloat4(source.SunGlareColor),
            SunDiskTexture = source.SunDiskTexture,
            SunDiskScreenSizeMin = source.SunDiskScreenSizeMin,
            SunDiskScreenSizeMax = source.SunDiskScreenSizeMax,
            DuskDawnPreset = source.DuskDawnPreset is null
                ? null
                : new StarfieldSunPresetDawnDuskPatch
                {
                    DirectionalColor = CloneSunPresetFloat4(
                        source.DuskDawnPreset.DirectionalColor),
                    TransitionStartAngle = source.DuskDawnPreset.TransitionStartAngle,
                    TransitionEndAngle = source.DuskDawnPreset.TransitionEndAngle
                },
            NightPreset = source.NightPreset is null
                ? null
                : new StarfieldSunPresetNightPatch
                {
                    DirectionalColor = CloneSunPresetFloat4(
                        source.NightPreset.DirectionalColor),
                    DirectionalIlluminance = source.NightPreset.DirectionalIlluminance,
                    GlareColor = CloneSunPresetFloat4(source.NightPreset.GlareColor)
                }
        };
    }

    private static StarfieldSunPresetFloat4Patch? CloneSunPresetFloat4(
        StarfieldSunPresetFloat4Patch? source) =>
        source is null
            ? null
            : new StarfieldSunPresetFloat4Patch
            {
                X = source.X,
                Y = source.Y,
                Z = source.Z,
                W = source.W
            };

    private static StarfieldCurve3DDefinition CloneCurve3DDefinition(
        StarfieldCurve3DDefinition source) =>
        new(
            CloneFloatCurve(source.XCurve),
            CloneFloatCurve(source.YCurve),
            CloneFloatCurve(source.ZCurve));

    private static StarfieldFloatCurve CloneFloatCurve(StarfieldFloatCurve source)
    {
        var controls = new List<StarfieldFloatCurveControl>(source.Controls.Count);
        foreach (var control in source.Controls)
        {
            controls.Add(new StarfieldFloatCurveControl(control.Input, control.Value));
        }

        return source with
        {
            Controls = controls,
            RawSerializedMetadata = source.RawSerializedMetadata.ToArray(),
            RawControlListBody = source.RawControlListBody.ToArray()
        };
    }

    private static uint MapNonZeroFormId(uint formId, Func<uint, uint> mapFormId) =>
        formId == 0 ? 0 : mapFormId(formId);

    private static uint? MapOptionalFormId(uint? formId, Func<uint, uint> mapFormId) =>
        formId switch
        {
            null => null,
            0 => 0,
            _ => mapFormId(formId.Value)
        };

    private static object CloneList(IList source, string propertyName, Func<uint, uint> mapFormId)
    {
        var listType = source.GetType();
        var elementType = listType.IsGenericType ? listType.GetGenericArguments()[0] : typeof(object);
        var targetType = typeof(List<>).MakeGenericType(elementType);
        var target = (IList)Activator.CreateInstance(targetType)!;

        foreach (var item in source)
        {
            if (elementType == typeof(uint) && EsmFormIdPropertyRegistry.IsFormIdProperty(propertyName))
            {
                target.Add(mapFormId((uint)item!));
            }
            else
            {
                target.Add(CloneValue(item, propertyName, mapFormId));
            }
        }

        return target;
    }

    private static object CloneDictionary(IDictionary source, string propertyName, Func<uint, uint> mapFormId)
    {
        var dictionaryType = source.GetType();
        if (!dictionaryType.IsGenericType)
        {
            return source;
        }

        var genericArgs = dictionaryType.GetGenericArguments();
        var keyType = genericArgs[0];
        var valueType = genericArgs[1];
        var targetType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType);
        var target = (IDictionary)Activator.CreateInstance(targetType)!;
        var rebaseKeys = keyType == typeof(uint) &&
                         EsmFormIdPropertyRegistry.IsFormIdKeyedDictionary(propertyName);

        foreach (DictionaryEntry entry in source)
        {
            var key = rebaseKeys ? mapFormId((uint)entry.Key) : entry.Key;
            var value = CloneValue(entry.Value, propertyName, mapFormId);
            target[key] = value;
        }

        return target;
    }

    private static bool IsEsmModelType(Type type)
    {
        return type.Namespace != null &&
               type.Namespace.StartsWith("BethesdaMultitool.Core.Formats.Esm.Models", StringComparison.Ordinal);
    }

    private static PropertyInfo[] GetWritableProperties(Type type)
    {
        return WritablePropertyCache.GetOrAdd(type, static t => t
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead && property.SetMethod != null && property.SetMethod.IsPublic)
            .ToArray());
    }
}
