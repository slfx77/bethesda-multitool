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

        return packages;
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
        PackageLocation? location = null;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "PLDT" when sub.DataLength >= 12:
                case "PLD2" when sub.DataLength >= 12 && location == null:
                    location ??= ParsePackageLocation(subData, record.IsBigEndian);
                    break;
            }
        }

        return new PackageRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? _context.GetEditorId(record.FormId),
            Location = location,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
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
