using System.Collections;
using System.Reflection;
using BethesdaMultitool.CLI.Commands.Analysis;
using BethesdaMultitool.CLI.Formatters;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using Xunit;

namespace BethesdaMultitool.Tests.CLI.Formatters;

public sealed class RecordInventoryCoverageTests
{
    public static IEnumerable<object[]> SemanticCollectionProperties =>
        typeof(RecordCollection)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property =>
                property.PropertyType.IsGenericType &&
                property.PropertyType.GetGenericTypeDefinition() == typeof(List<>) &&
                property.Name is not nameof(RecordCollection.MapMarkers) and
                    not nameof(RecordCollection.RuntimeScripts))
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => new object[] { property.Name });

    public static TheoryData<string, string> PreviouslyHiddenCollections => new()
    {
        { nameof(RecordCollection.ArmorAddons), "ARMA" },
        { nameof(RecordCollection.AudioLocationControllers), "ALOC" },
        { nameof(RecordCollection.BodyPartData), "BPTD" },
        { nameof(RecordCollection.CameraPaths), "CPTH" },
        { nameof(RecordCollection.CaravanCards), "CCRD" },
        { nameof(RecordCollection.CaravanDecks), "CDCK" },
        { nameof(RecordCollection.CaravanMoney), "CMNY" },
        { nameof(RecordCollection.Climate), "CLMT" },
        { nameof(RecordCollection.ConstructibleObjects), "COBJ" },
        { nameof(RecordCollection.Debris), "DEBR" },
        { nameof(RecordCollection.DehydrationStages), "DEHY" },
        { nameof(RecordCollection.EncounterZones), "ECZN" },
        { nameof(RecordCollection.Eyes), "EYES" },
        { nameof(RecordCollection.Grasses), "GRAS" },
        { nameof(RecordCollection.Hair), "HAIR" },
        { nameof(RecordCollection.HeadParts), "HDPT" },
        { nameof(RecordCollection.HungerStages), "HUNG" },
        { nameof(RecordCollection.IdleAnimations), "IDLE" },
        { nameof(RecordCollection.ImageSpaceModifiers), "IMAD" },
        { nameof(RecordCollection.ImageSpaces), "IMGS" },
        { nameof(RecordCollection.ImpactData), "IPCT" },
        { nameof(RecordCollection.LandTextures), "LTEX" },
        { nameof(RecordCollection.LightingTemplates), "LGTM" },
        { nameof(RecordCollection.LoadScreenTypes), "LSCT" },
        { nameof(RecordCollection.MaterialSwaps), "MSWP" },
        { nameof(RecordCollection.MenuIcons), "MICN" },
        { nameof(RecordCollection.NavMeshes), "NAVM" },
        { nameof(RecordCollection.NavMeshInfoMaps), "NAVI" },
        { nameof(RecordCollection.PlacedGrenades), "PGRE" },
        { nameof(RecordCollection.RadiationStages), "RADS" },
        { nameof(RecordCollection.RecipeCategories), "RCCT" },
        { nameof(RecordCollection.Regions), "REGN" },
        { nameof(RecordCollection.SleepDeprivationStages), "SLPD" },
        { nameof(RecordCollection.Sounds), "SOUN" },
        { nameof(RecordCollection.StaticCollections), "SCOL" },
        { nameof(RecordCollection.TextureSets), "TXST" },
        { nameof(RecordCollection.VoiceTypes), "VTYP" },
        { nameof(RecordCollection.Water), "WATR" },
        { nameof(RecordCollection.Weather), "WTHR" }
    };

    [Theory]
    [MemberData(nameof(SemanticCollectionProperties))]
    public void SemanticTypedCollection_IsVisibleAndIncludedInParsedTotal(string propertyName)
    {
        var records = CreateSingleRecordCollection(propertyName);

        Assert.Equal(1, records.TotalRecordsParsed);
        Assert.Single(RecordFlattener.Flatten(records));
        Assert.Single(StatsCommand.BuildCategories(records));
    }

    [Theory]
    [MemberData(nameof(SemanticCollectionProperties))]
    public void SemanticTypedCollection_SurvivesLoadOrderMerge(string propertyName)
    {
        var baseRecords = CreateSingleRecordCollection(propertyName, "Base");
        var overlayRecords = CreateSingleRecordCollection(propertyName, "Overlay");

        var merged = baseRecords.MergeWith(overlayRecords);
        var collectionProperty = typeof(RecordCollection).GetProperty(propertyName) ??
                                 throw new InvalidOperationException($"Unknown collection {propertyName}.");
        var mergedList = (IList)(collectionProperty.GetValue(merged) ??
                                 throw new InvalidOperationException($"Merged {propertyName} is null."));
        var mergedRecord = Assert.Single(mergedList.Cast<object>());

        Assert.Equal(
            $"Overlay{mergedRecord.GetType().Name}",
            mergedRecord.GetType().GetProperty("EditorId")?.GetValue(mergedRecord));
    }

    [Theory]
    [MemberData(nameof(PreviouslyHiddenCollections))]
    public void PreviouslyHiddenCollection_UsesItsPhysicalRecordSignature(
        string propertyName,
        string expectedType)
    {
        var records = CreateSingleRecordCollection(propertyName);

        Assert.Equal(expectedType, Assert.Single(RecordFlattener.Flatten(records)).Type);
        Assert.Equal(expectedType, Assert.Single(StatsCommand.BuildCategories(records)).Type);
    }

    [Fact]
    public void LeveledNpcList_PreservesLvlnSignature()
    {
        var records = new RecordCollection
        {
            LeveledLists = [new LeveledListRecord { FormId = 0x01020304, ListType = "LVLN" }]
        };

        Assert.Equal("LVLN", Assert.Single(RecordFlattener.Flatten(records)).Type);
        Assert.Equal("LVLI/LVLN/LVLC", Assert.Single(StatsCommand.BuildCategories(records)).Type);
    }

    private static RecordCollection CreateSingleRecordCollection(string propertyName, string prefix = "Test")
    {
        var collectionProperty = typeof(RecordCollection).GetProperty(propertyName) ??
                                 throw new InvalidOperationException($"Unknown collection {propertyName}.");
        var recordType = collectionProperty.PropertyType.GetGenericArguments()[0];
        var record = Activator.CreateInstance(recordType) ??
                     throw new InvalidOperationException($"Cannot create {recordType.Name}.");
        var formIdProperty = recordType.GetProperty("FormId") ??
                             throw new InvalidOperationException($"{recordType.Name} has no FormId.");
        formIdProperty.SetValue(record, 0x01020304u);
        recordType.GetProperty("EditorId")?.SetValue(record, $"{prefix}{recordType.Name}");
        recordType.GetProperty("RecordType")?.SetValue(record, "TEST");

        var list = (IList)(Activator.CreateInstance(collectionProperty.PropertyType) ??
                           throw new InvalidOperationException($"Cannot create {collectionProperty.PropertyType}."));
        list.Add(record);

        var records = new RecordCollection();
        collectionProperty.SetValue(records, list);
        return records;
    }
}
