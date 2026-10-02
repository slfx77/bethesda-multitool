using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Core.Games;

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
        return (RecordCollection)CloneValue(records, nameof(RecordCollection), mapFormId, records.Game)!;
    }

    /// <summary>Rebases an independently selected child model through the same FormID registry.</summary>
    internal static T RebaseModel<T>(T value, Func<uint, uint> mapFormId, BethesdaGame game) where T : class =>
        (T)CloneValue(value, typeof(T).Name, mapFormId, game)!;

    private static object? CloneValue(object? value, string propertyName, Func<uint, uint> mapFormId, BethesdaGame game)
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

        // These fields are tagged unions; names alone cannot decide whether their values are FormIDs.
        if (value is PackageLocation location)
        {
            return location with { Union = PackageReferenceIntegrity.LocationTypeIsFormId(location.Type)
                ? MapNonZeroFormId(location.Union, mapFormId) : location.Union };
        }
        if (value is PackageTarget target)
        {
            return target with { FormIdOrType = PackageReferenceIntegrity.TargetTypeIsFormId(target.Type)
                ? MapNonZeroFormId(target.FormIdOrType, mapFormId) : target.FormIdOrType };
        }
        if (value is DialogueCondition condition)
        {
            return RebaseCondition(condition, game, mapFormId);
        }
        if (value is PerkCondition perkCondition)
        {
            return RebasePerkCondition(perkCondition, game, mapFormId);
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
            uint MapRequired(uint formId)
            {
                return formId == 0 ? 0 : mapFormId(formId);
            }

            uint? MapOptional(uint? formId)
            {
                return formId switch
                {
                    null => null,
                    0 => 0,
                    _ => mapFormId(formId.Value)
                };
            }

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
            return CloneDictionary(dictionary, propertyName, mapFormId, game);
        }

        if (value is IList list)
        {
            return CloneList(list, propertyName, mapFormId, game);
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
            var clonedPropertyValue = CloneValue(originalPropertyValue, property.Name, mapFormId, game);
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
        StarfieldSunPresetFloat4Patch? source)
    {
        return source is null
            ? null
            : new StarfieldSunPresetFloat4Patch
            {
                X = source.X,
                Y = source.Y,
                Z = source.Z,
                W = source.W
            };
    }

    private static StarfieldCurve3DDefinition CloneCurve3DDefinition(
        StarfieldCurve3DDefinition source)
    {
        return new StarfieldCurve3DDefinition(
            CloneFloatCurve(source.XCurve),
            CloneFloatCurve(source.YCurve),
            CloneFloatCurve(source.ZCurve));
    }

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

    private static uint MapNonZeroFormId(uint formId, Func<uint, uint> mapFormId)
    {
        return formId == 0 ? 0 : mapFormId(formId);
    }

    private static uint? MapOptionalFormId(uint? formId, Func<uint, uint> mapFormId)
    {
        return formId switch
        {
            null => null,
            0 => 0,
            _ => mapFormId(formId.Value)
        };
    }

    private static DialogueCondition RebaseCondition(DialogueCondition condition, BethesdaGame game,
        Func<uint, uint> mapFormId)
    {
        var table = ConditionFunctionTable.For(game);
        uint MapParameter(int slot, uint raw, string? text)
        {
            return text is null && table.TryClassifyParam(condition.FunctionIndex, slot, condition.Type,
                condition.RunOn, condition.Parameter1, out var kind) && kind == ConditionParamKind.FormId
                ? MapNonZeroFormId(raw, mapFormId) : raw;
        }
        return condition with
        {
            Parameter1 = MapParameter(0, condition.Parameter1, condition.Parameter1String),
            Parameter2 = MapParameter(1, condition.Parameter2, condition.Parameter2String),
            ComparisonValue = condition.UsesGlobalComparison
                ? BitConverter.UInt32BitsToSingle(MapNonZeroFormId(condition.ComparisonGlobalFormId, mapFormId))
                : condition.ComparisonValue,
            Reference = DialogueConditionReferencePolicy.IsSemanticReferenceSlot(condition, game)
                ? MapNonZeroFormId(condition.Reference, mapFormId) : condition.Reference
        };
    }

    private static PerkCondition RebasePerkCondition(PerkCondition condition, BethesdaGame game,
        Func<uint, uint> mapFormId)
    {
        var table = ConditionFunctionTable.For(game);
        var flags = condition.Flags ?? (byte)(condition.ComparisonOperator << 5);
        bool IsFormParameter(int slot) =>
            table.TryClassifyParam(condition.FunctionIndex, slot, flags, condition.RunOn,
                condition.Parameter1, out var kind) && kind == ConditionParamKind.FormId;

        var firstIsForm = IsFormParameter(0);
        var secondIsForm = IsFormParameter(1);
        return condition with
        {
            // The numeric value is the decoded parameter, not the runtime pointer retained in
            // RuntimeRawData. Keep it in the same namespace as its optional resolved FormID.
            Parameter1 = firstIsForm ? MapNonZeroFormId(condition.Parameter1, mapFormId) : condition.Parameter1,
            Parameter1FormId = firstIsForm
                ? MapOptionalFormId(condition.Parameter1FormId, mapFormId) : condition.Parameter1FormId,
            Parameter2 = secondIsForm ? MapNonZeroFormId(condition.Parameter2, mapFormId) : condition.Parameter2,
            Parameter2FormId = secondIsForm
                ? MapOptionalFormId(condition.Parameter2FormId, mapFormId) : condition.Parameter2FormId,
            ComparisonGlobalFormId = MapOptionalFormId(condition.ComparisonGlobalFormId, mapFormId),
            ReferenceFormId = DialogueConditionReferencePolicy.IsSemanticReferenceSlot(
                condition.FunctionIndex, condition.RunOn ?? 0, game)
                ? MapOptionalFormId(condition.ReferenceFormId, mapFormId) : condition.ReferenceFormId,
            RuntimeRawData = condition.RuntimeRawData?.ToArray(),
            RecoveryIssues = condition.RecoveryIssues.ToList()
        };
    }

    private static object CloneList(IList source, string propertyName, Func<uint, uint> mapFormId, BethesdaGame game)
    {
        var listType = source.GetType();
        var elementType = listType.IsGenericType ? listType.GetGenericArguments()[0] : typeof(object);
        var targetType = typeof(List<>).MakeGenericType(elementType);
        var target = (IList)Activator.CreateInstance(targetType)!;

        foreach (var item in source)
        {
            if (elementType == typeof(uint) && EsmFormIdPropertyRegistry.IsFormIdProperty(propertyName))
            {
                var id = (uint)item!;
                // SCRV shares this ordered table with SCRO, but its high-bit-tagged local index is not a FormID.
                target.Add(propertyName == nameof(ScriptRecord.ReferencedObjects) && (id & 0x80000000) != 0
                    ? id : MapNonZeroFormId(id, mapFormId));
            }
            else
            {
                target.Add(CloneValue(item, propertyName, mapFormId, game));
            }
        }

        return target;
    }

    private static object CloneDictionary(IDictionary source, string propertyName, Func<uint, uint> mapFormId, BethesdaGame game)
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
            var value = CloneValue(entry.Value, propertyName, mapFormId, game);
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
