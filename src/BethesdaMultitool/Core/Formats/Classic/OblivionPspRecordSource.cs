using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Travels.OblivionPsp;

namespace BethesdaMultitool.Core.Formats.Classic;

/// <summary>
///     Synthesizes browsable records from one extracted Oblivion PSP (cancelled) UMD tree: one
///     record per entry of the <c>GR.ARC</c> data pack.
///     <para>
///         Identity is a hash of the ENTRY NAME, never its table position, because the point of
///         this title is the cross-build diff: seven dated betas survive, and the same resource
///         moves position between them as the pack is rebuilt. Hashing the name makes
///         <c>diff</c> between two staged builds compare like with like, so a record that changed
///         size shows up as a changed record rather than as a deletion plus an insertion.
///     </para>
///     <para>
///         The pack is read through <see cref="OblivionPspArchive" /> from its path rather than
///         through the mounted filesystem: a retail pack reaches 216 MB and the record source only
///         needs its header, record table and name table, which together are a few kilobytes.
///     </para>
/// </summary>
internal static class OblivionPspRecordSource
{
    /// <summary>The single reserved domain byte for Oblivion PSP records.</summary>
    public const byte Domain = 0x4F;

    /// <summary>The record signature used for a pack entry.</summary>
    public const string PackEntryRecordType = "OPAK";

    /// <summary>The pack's name inside the UMD tree's <c>USRDIR</c>.</summary>
    public const string PackFileName = "GR.ARC";

    /// <summary>How many bytes are sniffed to classify an entry's payload.</summary>
    private const int SniffLength = 12;

    private const uint RenderWareLibrary36 = 0x1802FFFF;
    private const uint RenderWareLibrary37 = 0x1C020065;

    /// <summary>
    ///     Reads the pack under <paramref name="installRoot" /> and appends one record per entry.
    ///     A tree with no pack leaves the collection empty rather than throwing — the analyzer runs
    ///     on whatever a staged build actually ships.
    /// </summary>
    public static void Populate(
        string installRoot, RecordCollection records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installRoot);
        ArgumentNullException.ThrowIfNull(records);
        cancellationToken.ThrowIfCancellationRequested();

        var pack = Path.Combine(installRoot, @"PSP_GAME\USRDIR", PackFileName);
        if (!File.Exists(pack))
        {
            return;
        }

        var archive = OblivionPspArchive.Parse(pack);

        using var stream = new FileStream(pack, FileMode.Open, FileAccess.Read, FileShare.Read);
        var seen = new Dictionary<uint, string>();
        Span<byte> sniff = stackalloc byte[SniffLength];

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var formId = ClassicFormIdScheme.Compose(Domain, ClassicNameHash.Of(entry.Name, 24));
            if (seen.TryGetValue(formId, out var clash))
            {
                throw new InvalidOperationException(
                    $"Oblivion PSP pack entries '{clash}' and '{entry.Name}' hash to the same record id " +
                    $"0x{formId:X8}; the name hash must be widened rather than renumbered.");
            }

            seen[formId] = entry.Name;
            records.GenericRecords.Add(BuildEntryRecord(entry, Classify(stream, entry, sniff), formId));
        }
    }

    /// <summary>Builds the record form of one pack entry.</summary>
    private static GenericEsmRecord BuildEntryRecord(OblivionPspArchiveEntry entry, string kind, uint formId)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Index"] = entry.Index,
            ["Offset"] = entry.Offset,
            ["Size"] = entry.Size,
            ["Kind"] = kind
        };

        // Zero-length entries are legal and interesting: the community repack of the February 2007
        // disc truncated Hub_5_Demo to nothing while keeping its record.
        if (entry.Size == 0)
        {
            fields["Empty"] = true;
        }

        return new GenericEsmRecord
        {
            FormId = formId,
            RecordType = PackEntryRecordType,
            EditorId = ClassicRecordNaming.ToEditorId(entry.Name),
            FullName = entry.Name,
            Fields = fields
        };
    }

    /// <summary>
    ///     Classifies a payload from its first bytes, falling back to the name's extension for the
    ///     formats that carry no magic. Most entries in the 2007 builds are RenderWare streams,
    ///     whose 12-byte chunk header ends in a library id — the same 3.6 and 3.7 constants that
    ///     independently corroborate the pack's little-endian reading.
    ///     <para>
    ///         Measured across the seven staged builds: the 2006-2007 packs run 74-127 RenderWare
    ///         streams plus a steady 6 language databases, 2-3 JPEG, 2 PNG and 1-2 XML — but the
    ///         June 2006 tech demo is the other way round, 43 JPEG against 14 RenderWare, because it
    ///         is a prison/crypt slideshow build rather than a game. A caller must not assume the
    ///         pack is mostly geometry.
    ///     </para>
    ///     <para>
    ///         The name fallback only ever fires for entries that HAVE an extension, which from
    ///         November 2006 is a handful of strays (<c>.sdb</c> string databases, <c>.xml</c>) and
    ///         in the June build is most of the pack (<c>.log</c>, <c>.db</c>, <c>.txd</c>).
    ///     </para>
    /// </summary>
    private static string Classify(FileStream stream, OblivionPspArchiveEntry entry, Span<byte> sniff)
    {
        if (entry.Size == 0)
        {
            return "empty";
        }

        if (entry.Size >= SniffLength)
        {
            stream.Position = entry.Offset;
            stream.ReadExactly(sniff);

            var library = BinaryPrimitives.ReadUInt32LittleEndian(sniff[8..]);
            if (library is RenderWareLibrary36 or RenderWareLibrary37)
            {
                return "renderware";
            }

            if (sniff[0] == 0x89 && sniff[1] == 'P' && sniff[2] == 'N' && sniff[3] == 'G')
            {
                return "png";
            }

            if (sniff[0] == 0xFF && sniff[1] == 0xD8)
            {
                return "jpeg";
            }

            if (sniff[0] == '<')
            {
                return "xml";
            }
        }

        // No magic matched. An extension in the name is the only other evidence the pack offers,
        // and it is what identifies the six per-language .sdb string databases every build ships.
        var extension = Path.GetExtension(entry.Name);
        return extension.Length > 1 ? extension[1..].ToLowerInvariant() : "unknown";
    }
}
