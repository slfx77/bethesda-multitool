using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Redguard;

namespace BethesdaMultitool.Core.Formats.Classic;

/// <summary>
///     Synthesizes one <c>RTXT</c> record per <c>ENGLISH.RTX</c> entry — every line of dialogue,
///     examine text and menu string, with whether it is voiced and at what rate and depth. The
///     record's identity is its 4-character tag, the label the scripts pass; four printable
///     characters do not fit the 24-bit index, so it goes through <see cref="ClassicNameHash" />
///     and the retail test pins that no two of the 4,866 collide.
/// </summary>
internal static class RedguardTextRecordSource
{
    /// <summary>Domain byte for <c>RTXT</c> records; the index is the 24-bit hash of the tag.</summary>
    public const byte TextDomain = 0x33;

    public const string TextRecordType = "RTXT";

    /// <summary>Reads <c>ENGLISH.RTX</c> under <paramref name="dataRoot" /> and appends its records.</summary>
    public static void Populate(string dataRoot, RecordCollection records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);
        ArgumentNullException.ThrowIfNull(records);

        var path = Path.Combine(dataRoot, RedguardRtxFile.FileName);
        if (!File.Exists(path))
        {
            return;
        }

        using var database = RedguardRtxFile.Open(path);
        var seen = new Dictionary<uint, string>();
        foreach (var entry in database.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = Build(entry);
            if (!seen.TryAdd(record.FormId, entry.Tag))
            {
                throw new InvalidDataException(
                    $"{database.Name}: tag '{entry.Tag}' hashes to the same FormID as '{seen[record.FormId]}' (0x{record.FormId:X8}); the name hash needs widening.");
            }

            records.GenericRecords.Add(record);
        }
    }

    /// <summary>The record form of one text-database entry.</summary>
    public static GenericEsmRecord Build(RedguardRtxEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Tag"] = entry.Tag,
            ["Index"] = entry.Index,
            ["Text"] = entry.Text,
            ["Voiced"] = entry.IsVoiced
        };

        if (entry.Sound is { } sound)
        {
            fields["SampleRate"] = sound.SampleRate;
            fields["Depth"] = sound.DepthDescription;
            fields["Duration"] = Math.Round(sound.DurationSeconds, 3);
            fields["SampleBytes"] = sound.ByteLength;
        }

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(TextDomain, ClassicNameHash.Of(entry.Tag, 24)),
            RecordType = TextRecordType,
            EditorId = ClassicRecordNaming.ToEditorId("RTX_" + entry.Tag),
            FullName = ClassicRecordNaming.Summarize(entry.Text),
            Fields = fields
        };
    }
}
