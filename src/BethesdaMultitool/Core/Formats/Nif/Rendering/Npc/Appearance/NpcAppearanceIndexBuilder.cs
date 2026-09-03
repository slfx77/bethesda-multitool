using System.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Models;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Processing;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance;

/// <summary>Scans an ESM once and builds the <see cref="NpcAppearanceIndex" /> of all appearance-relevant record types.</summary>
internal static class NpcAppearanceIndexBuilder
{
    internal static NpcAppearanceIndex Build(
        byte[] esmData,
        bool bigEndian,
        Action<NpcAppearanceIndexBuildTiming>? timingSink = null,
        CancellationToken cancellationToken = default)
    {
        var format = PluginFormat.Detect(esmData);
        var scanTimer = Stopwatch.StartNew();
        var records = EsmRecordParser.ScanAllRecords(esmData, bigEndian, format);
        scanTimer.Stop();
        timingSink?.Invoke(new NpcAppearanceIndexBuildTiming(
            "record-descriptor-rescan",
            scanTimer.Elapsed,
            records.Count,
            0,
            esmData.LongLength));

        var index = new NpcAppearanceIndex { Game = format.Game };
        var decodeTimer = Stopwatch.StartNew();
        var decodedRecords = 0;
        long decodedBytes = 0;

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAppearanceRecord(record.Signature))
            {
                continue;
            }

            decodedRecords++;
            decodedBytes += record.DataSize;
            ProcessRecord(index, esmData, bigEndian, record);
        }

        decodeTimer.Stop();
        timingSink?.Invoke(new NpcAppearanceIndexBuildTiming(
            "appearance-record-decode",
            decodeTimer.Elapsed,
            records.Count,
            decodedRecords,
            decodedBytes));

        return index;
    }

    /// <summary>
    ///     Builds the appearance index from the record descriptors retained by the ESM analysis pass.
    ///     Only appearance-relevant record payloads are read through the session-owned mapping; the
    ///     whole plugin is neither copied nor traversed a second time.
    /// </summary>
    internal static NpcAppearanceIndex Build(
        IMemoryAccessor esmAccessor,
        long esmLength,
        IReadOnlyList<DetectedMainRecord> analyzedRecords,
        bool bigEndian,
        BethesdaGame game,
        Action<NpcAppearanceIndexBuildTiming>? timingSink = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(esmAccessor);
        ArgumentNullException.ThrowIfNull(analyzedRecords);

        // The analysis session has already resolved the game from the plugin header and master
        // list. Keep that authoritative identity: HEDR alone cannot distinguish every member of
        // the 24-byte-header family. The prefix probe is only for synthetic/legacy callers that
        // have no analyzed game identity.
        var resolvedGame = game != BethesdaGame.Unknown
            ? game
            : DetectPluginFormat(esmAccessor, esmLength).Game;
        var index = new NpcAppearanceIndex { Game = resolvedGame };
        var decodeTimer = Stopwatch.StartNew();
        var decodedRecords = 0;
        long decodedBytes = 0;

        foreach (var analyzedRecord in analyzedRecords)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAppearanceRecord(analyzedRecord.RecordType) ||
                !TryReadLocalRecord(
                    esmAccessor,
                    esmLength,
                    analyzedRecord,
                    out var recordData,
                    out var record))
            {
                continue;
            }

            decodedRecords++;
            decodedBytes += analyzedRecord.DataSize;
            ProcessRecord(index, recordData, bigEndian, record);
        }

        decodeTimer.Stop();
        timingSink?.Invoke(new NpcAppearanceIndexBuildTiming(
            "appearance-record-decode",
            decodeTimer.Elapsed,
            analyzedRecords.Count,
            decodedRecords,
            decodedBytes));
        return index;
    }

    private static bool TryReadLocalRecord(
        IMemoryAccessor accessor,
        long esmLength,
        DetectedMainRecord source,
        out byte[] recordData,
        out AnalyzerRecordInfo record)
    {
        recordData = [];
        record = null!;

        var headerSize = source.HeaderSize;
        var dataSize = (long)source.DataSize;
        var dataStart = source.Offset + headerSize;
        if (headerSize <= 0 ||
            source.Offset < 0 ||
            dataStart < source.Offset ||
            dataSize < 0 ||
            dataStart > esmLength ||
            dataSize > esmLength - dataStart ||
            dataSize > int.MaxValue - headerSize)
        {
            return false;
        }

        // The scanners consume payload bytes through NpcRecordDataReader, whose existing contract
        // expects a record-relative header. Leave that small prefix zeroed and read only the payload;
        // no scanner observes header bytes, and compressed payloads still follow the same decoder.
        recordData = new byte[headerSize + (int)dataSize];
        var bytesRead = accessor.ReadArray(dataStart, recordData, headerSize, (int)dataSize);
        if (bytesRead != dataSize)
        {
            recordData = [];
            return false;
        }

        record = new AnalyzerRecordInfo
        {
            Signature = source.RecordType,
            FormId = source.FormId,
            Flags = source.Flags,
            DataSize = source.DataSize,
            Offset = 0,
            TotalSize = checked((uint)(headerSize + dataSize)),
            RecordHeaderSize = headerSize
        };
        return true;
    }

    private static PluginFormat DetectPluginFormat(IMemoryAccessor accessor, long esmLength)
    {
        var prefixLength = (int)Math.Min(esmLength, 64);
        if (prefixLength <= 0)
        {
            return PluginFormat.Fnv;
        }

        var prefix = new byte[prefixLength];
        return accessor.ReadArray(0, prefix, 0, prefixLength) == prefixLength
            ? PluginFormat.Detect(prefix)
            : PluginFormat.Fnv;
    }

    private static bool IsAppearanceRecord(string signature)
    {
        return signature is
            "NPC_" or "CREA" or "RACE" or "HAIR" or "EYES" or "HDPT" or
            "ARMO" or "CLOT" or "ARMA" or "WEAP" or "PACK" or "IDLE" or
            "FLST" or "LVLI" or "LVLN" or "CSTY";
    }

    private static void ProcessRecord(
        NpcAppearanceIndex index,
        byte[] esmData,
        bool bigEndian,
        AnalyzerRecordInfo record)
    {
        switch (record.Signature)
        {
            case "NPC_":
                AddIfPresent(index.Npcs, record.FormId, NpcRecordScanner.Process(esmData, bigEndian, record));
                break;
            case "CREA":
                AddIfPresent(index.Creatures, record.FormId,
                    CreatureRecordScanner.Process(esmData, bigEndian, record));
                break;
            case "RACE":
                AddIfPresent(index.Races, record.FormId, RaceRecordScanner.Process(esmData, bigEndian, record));
                break;
            case "HAIR":
                AddIfPresent(index.Hairs, record.FormId, HairRecordScanner.Process(esmData, bigEndian, record));
                break;
            case "EYES":
                AddIfPresent(index.Eyes, record.FormId, EyesRecordScanner.Process(esmData, bigEndian, record));
                break;
            case "HDPT":
                AddIfPresent(index.HeadParts, record.FormId,
                    HeadPartRecordScanner.Process(esmData, bigEndian, record));
                break;
            case "ARMO":
            case "CLOT":
                AddIfPresent(index.Armors, record.FormId,
                    ArmorRecordScanner.Process(esmData, bigEndian, record, index.Game));
                break;
            case "ARMA":
                AddIfPresent(index.ArmorAddons, record.FormId,
                    ArmorAddonRecordScanner.Process(esmData, bigEndian, record));
                break;
            case "WEAP":
                AddIfPresent(index.Weapons, record.FormId,
                    WeaponRecordScanner.Process(esmData, bigEndian, record, index.Game));
                break;
            case "PACK":
                AddIfPresent(index.Packages, record.FormId, PackageRecordScanner.Process(esmData, bigEndian, record));
                break;
            case "IDLE":
            {
                var idle = IdleRecordScanner.Process(esmData, bigEndian, record);
                AddIfPresent(index.Idles, record.FormId, idle);
                if (idle?.ParentIdleFormId is uint parentIdleFormId)
                {
                    if (!index.IdleChildrenByParent.TryGetValue(parentIdleFormId, out var children))
                    {
                        children = [];
                        index.IdleChildrenByParent[parentIdleFormId] = children;
                    }

                    children.Add(record.FormId);
                }

                break;
            }
            case "FLST":
                AddIfPresent(index.FormLists, record.FormId,
                    FormListRecordScanner.Process(esmData, bigEndian, record));
                break;
            case "LVLI":
                AddLeveledList(
                    index.LeveledItemRecords,
                    index.LeveledItems,
                    record.FormId,
                    LeveledListRecordScanner.Process(esmData, bigEndian, record));
                break;
            case "LVLN":
                AddLeveledList(
                    index.LeveledNpcRecords,
                    index.LeveledNpcs,
                    record.FormId,
                    LeveledListRecordScanner.Process(esmData, bigEndian, record));
                break;
            case "CSTY":
                AddIfPresent(index.CombatStyles, record.FormId,
                    CombatStyleRecordScanner.Process(esmData, bigEndian, record));
                break;
        }
    }

    private static void AddIfPresent<TValue>(
        Dictionary<uint, TValue> index,
        uint formId,
        TValue? value)
        where TValue : class
    {
        if (value != null)
        {
            index[formId] = value;
        }
    }

    private static void AddIfPresent(
        Dictionary<uint, List<uint>> index,
        uint formId,
        List<uint>? value)
    {
        if (value != null)
        {
            index[formId] = value;
        }
    }

    private static void AddLeveledList(
        Dictionary<uint, LeveledListScanEntry> detailedIndex,
        Dictionary<uint, List<uint>> flattenedIndex,
        uint formId,
        LeveledListScanEntry? value)
    {
        if (value == null)
        {
            return;
        }

        detailedIndex[formId] = value;
        if (value.Entries.Count > 0)
        {
            flattenedIndex[formId] = value.Entries.Select(static entry => entry.FormId).ToList();
        }
    }
}

internal sealed record NpcAppearanceIndexBuildTiming(
    string Stage,
    TimeSpan Elapsed,
    int RecordsVisited,
    int RecordsDecoded,
    long BytesRead);
