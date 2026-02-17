using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

internal sealed class AiRecordHandler(RecordParserContext context)
{
    private readonly RecordParserContext _context = context;

    /// <summary>
    ///     Reconstruct all AI Package (PACK) records from the scan result.
    ///     Only extracts location data (PLDT) needed for NPC spawn resolution.
    /// </summary>
    internal List<PackageRecord> ReconstructPackages()
    {
        var packages = new List<PackageRecord>();
        var packRecords = _context.GetRecordsByType("PACK").ToList();

        if (_context.Accessor == null)
        {
            // Without accessor, only basic scan result data is available
            foreach (var record in packRecords)
            {
                packages.Add(new PackageRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(16384);
            try
            {
                foreach (var record in packRecords)
                {
                    var package = ReconstructPackageFromAccessor(record, buffer);
                    packages.Add(package);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        SupplementWithRuntimePackages(packages);

        return packages;
    }

    /// <summary>
    ///     Supplement ESM-scanned packages with runtime PACK structs from hash table entries.
    ///     DMPs may have PACK structs that ESM fragment scan missed.
    /// </summary>
    private void SupplementWithRuntimePackages(List<PackageRecord> packages)
    {
        if (_context.RuntimeReader == null)
        {
            return;
        }

        var existingFormIds = new HashSet<uint>(packages.Select(p => p.FormId));
        foreach (var entry in _context.ScanResult.RuntimeEditorIds)
        {
            if (entry.FormType != 0x49 || existingFormIds.Contains(entry.FormId))
            {
                continue;
            }

            var pkg = _context.RuntimeReader.ReadRuntimePackage(entry);
            if (pkg != null)
            {
                packages.Add(pkg);
            }
        }
    }

    private PackageRecord ReconstructPackageFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = _context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new PackageRecord
            {
                FormId = record.FormId,
                EditorId = _context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        PackageData? packageData = null;
        PackageSchedule? schedule = null;
        PackageLocation? location = null;
        PackageLocation? location2 = null;
        PackageTarget? target = null;
        PackageTarget? target2 = null;
        bool isRepeatable = false;
        bool isStartingLocationLinkedRef = false;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "PKDT" when sub.DataLength >= 10:
                    packageData = ParsePackageData(subData, record.IsBigEndian);
                    break;
                case "PSDT" when sub.DataLength >= 8:
                    schedule = ParsePackageSchedule(subData, record.IsBigEndian);
                    break;
                case "PLDT" when sub.DataLength >= 12:
                    location ??= ParsePackageLocation(subData, record.IsBigEndian);
                    break;
                case "PLD2" when sub.DataLength >= 12:
                    location2 ??= ParsePackageLocation(subData, record.IsBigEndian);
                    break;
                case "PTDT" when sub.DataLength >= 16:
                    target ??= ParsePackageTarget(subData, record.IsBigEndian);
                    break;
                case "PTD2" when sub.DataLength >= 16:
                    target2 ??= ParsePackageTarget(subData, record.IsBigEndian);
                    break;
                case "PKPT" when sub.DataLength >= 2:
                    (isRepeatable, isStartingLocationLinkedRef) = ParsePatrolData(subData);
                    break;
            }
        }

        return new PackageRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? _context.GetEditorId(record.FormId),
            Data = packageData,
            Schedule = schedule,
            Location = location,
            Location2 = location2,
            Target = target,
            Target2 = target2,
            IsRepeatable = isRepeatable,
            IsStartingLocationLinkedRef = isStartingLocationLinkedRef,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    /// <summary>
    ///     Parse PKDT subrecord (12 bytes). Layout from PDB PACKAGE_DATA:
    ///     [0-3] iPackFlags (uint32) — endian-dependent
    ///     [4]   cPackType (byte) — no endian swap
    ///     [5]   unused
    ///     [6-7] iFOBehaviorFlags (uint16) — endian-dependent
    ///     [8-9] iPackageSpecificFlags (uint16) — endian-dependent
    ///     [10-11] unknown
    /// </summary>
    internal static PackageData ParsePackageData(ReadOnlySpan<byte> data, bool isBigEndian)
    {
        var generalFlags = isBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data)
            : BinaryPrimitives.ReadUInt32LittleEndian(data);

        var type = data[4];

        var foBehavior = isBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data[6..])
            : BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);

        var typeSpecific = isBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data[8..])
            : BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);

        return new PackageData
        {
            Type = type,
            GeneralFlags = generalFlags,
            FalloutBehaviorFlags = foBehavior,
            TypeSpecificFlags = typeSpecific
        };
    }

    /// <summary>
    ///     Parse PKPT subrecord (2 bytes). Layout from PDB PACK_PATROL_DATA:
    ///     [0] bRepeatable (bool byte)
    ///     [1] bStartingLocationAtLinkedRef (bool byte)
    /// </summary>
    internal static (bool IsRepeatable, bool IsStartingLocationLinkedRef) ParsePatrolData(
        ReadOnlySpan<byte> data)
    {
        return (data[0] != 0, data[1] != 0);
    }

    /// <summary>
    ///     Parse PSDT subrecord (8 bytes). Bytes 0-3 are single bytes (no endian swap).
    ///     Duration at [4..8] is int32 (endian-dependent, in hours).
    /// </summary>
    internal static PackageSchedule ParsePackageSchedule(ReadOnlySpan<byte> data, bool isBigEndian)
    {
        var duration = isBigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(data[4..])
            : BinaryPrimitives.ReadInt32LittleEndian(data[4..]);

        return new PackageSchedule
        {
            Month = (sbyte)data[0],
            DayOfWeek = (sbyte)data[1],
            Date = (sbyte)data[2],
            Time = (sbyte)data[3],
            Duration = duration
        };
    }

    /// <summary>
    ///     Parse PTDT/PTD2 subrecord (16 bytes).
    ///     [0]=Type, [1-3]=pad, [4-7]=FormID/Union, [8-11]=CountDistance, [12-15]=AcquireRadius.
    /// </summary>
    internal static PackageTarget ParsePackageTarget(ReadOnlySpan<byte> data, bool isBigEndian)
    {
        var type = data[0];

        var formIdOrType = isBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data[4..])
            : BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);

        var countDistance = isBigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(data[8..])
            : BinaryPrimitives.ReadInt32LittleEndian(data[8..]);

        var acquireRadius = isBigEndian
            ? BinaryPrimitives.ReadSingleBigEndian(data[12..])
            : BinaryPrimitives.ReadSingleLittleEndian(data[12..]);

        return new PackageTarget
        {
            Type = type,
            FormIdOrType = formIdOrType,
            CountDistance = countDistance,
            AcquireRadius = acquireRadius
        };
    }

    private static PackageLocation ParsePackageLocation(ReadOnlySpan<byte> data, bool isBigEndian)
    {
        var type = data[0];
        var union = isBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data[4..])
            : BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        var radius = isBigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(data[8..])
            : BinaryPrimitives.ReadInt32LittleEndian(data[8..]);

        return new PackageLocation
        {
            Type = type,
            Union = union,
            Radius = radius
        };
    }
}
