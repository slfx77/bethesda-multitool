namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Scanning;

internal sealed record RuntimeRefrExtraDataCensus(
    int SampleCount,
    int ValidRefrCount,
    int RefsWithExtraData,
    int VisitedNodeCount,
    IReadOnlyDictionary<byte, int> TypeCounts,
    int OwnershipCount,
    int LockCount,
    int TeleportCount,
    int MapMarkerCount,
    int EnableParentCount,
    int LinkedRefCount,
    int EncounterZoneCount,
    int StartingPositionCount,
    int StartingWorldOrCellCount,
    int PackageStartLocationCount,
    int MerchantContainerCount,
    int LeveledCreatureCount,
    int RadiusCount,
    int CountCount,
    int EditorIdCount);
