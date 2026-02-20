using FalloutAudioTranscriber.Models;
using FalloutXbox360Utils.Core.Formats.Esm;

namespace FalloutAudioTranscriber.Services;

/// <summary>
///     Parses an ESM file and builds a lookup index for INFO, NPC_, QUST, and DIAL records.
/// </summary>
public static class EsmIndexBuilder
{
    public static async Task<EsmLookupIndex> BuildAsync(
        string esmPath,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report("Reading ESM file...");

        var fileData = await File.ReadAllBytesAsync(esmPath, ct);

        progress?.Report("Parsing ESM records...");

        // EnumerateRecords returns full records with subrecords
        var records = await Task.Run(() => EsmParser.EnumerateRecords(fileData), ct);

        var index = new EsmLookupIndex();
        var infoCount = 0;
        var npcCount = 0;
        var questCount = 0;
        var dialCount = 0;
        var vtypCount = 0;

        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();

            switch (record.Header.Signature)
            {
                case "INFO":
                {
                    var subtitle = record.Subrecords
                        .FirstOrDefault(s => s.Signature == "NAM1")
                        ?.DataAsString;

                    var speakerFormId = record.Subrecords
                        .FirstOrDefault(s => s.Signature == "ANAM" && s.Data.Length >= 4)
                        ?.DataAsFormId;

                    var questFormId = record.Subrecords
                        .FirstOrDefault(s => s.Signature == "QSTI" && s.Data.Length >= 4)
                        ?.DataAsFormId;

                    index.AddInfo(record.Header.FormId, subtitle, speakerFormId, questFormId);
                    infoCount++;
                    break;
                }

                case "NPC_":
                case "CREA":
                {
                    var editorId = record.EditorId;
                    var fullName = record.Subrecords
                        .FirstOrDefault(s => s.Signature == "FULL")
                        ?.DataAsString;

                    var voiceTypeFormId = record.Subrecords
                        .FirstOrDefault(s => s.Signature == "VTCK" && s.Data.Length >= 4)
                        ?.DataAsFormId;

                    var fallback = record.Header.Signature == "CREA"
                        ? $"CREA_{record.Header.FormId:X8}"
                        : $"NPC_{record.Header.FormId:X8}";
                    var name = fullName ?? editorId ?? fallback;
                    index.AddNpc(record.Header.FormId, name, voiceTypeFormId, hasFullName: fullName != null);
                    npcCount++;
                    break;
                }

                case "QUST":
                {
                    var editorId = record.EditorId;
                    var fullName = record.Subrecords
                        .FirstOrDefault(s => s.Signature == "FULL")
                        ?.DataAsString;

                    var name = fullName ?? editorId ?? $"QUST_{record.Header.FormId:X8}";
                    index.AddQuest(record.Header.FormId, name, editorId);
                    questCount++;
                    break;
                }

                case "DIAL":
                {
                    var editorId = record.EditorId;
                    if (editorId != null)
                    {
                        var questFormId = record.Subrecords
                            .FirstOrDefault(s => s.Signature == "QSTI" && s.Data.Length >= 4)
                            ?.DataAsFormId;

                        var speakerFormId = record.Subrecords
                            .FirstOrDefault(s => s.Signature == "TNAM" && s.Data.Length >= 4)
                            ?.DataAsFormId;

                        index.AddTopic(editorId, questFormId, speakerFormId);
                        dialCount++;
                    }

                    break;
                }

                case "VTYP":
                {
                    var editorId = record.EditorId;
                    if (editorId != null)
                    {
                        index.AddVoiceType(record.Header.FormId, editorId);
                        vtypCount++;
                    }

                    break;
                }
            }
        }

        progress?.Report($"Indexed {infoCount} INFOs, {npcCount} NPCs, {questCount} quests, {dialCount} topics, {vtypCount} voice types");
        return index;
    }
}
