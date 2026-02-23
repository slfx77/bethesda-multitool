using System.Buffers;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

/// <summary>
///     Parses INFO records from ESM data, handling CTDA conditions, subrecord iteration,
///     and split INFO record merging (Xbox 360 splits INFO into Base + Response records).
/// </summary>
internal sealed class DialogueConditionParser(RecordParserContext context)
{
    private readonly RecordParserContext _context = context;

    /// <summary>
    ///     Parse all INFO records into DialogueRecord instances.
    ///     Uses accessor-based subrecord parsing when available, otherwise falls back to scan result.
    /// </summary>
    internal List<DialogueRecord> ParseAllInfoRecords()
    {
        var dialogues = new List<DialogueRecord>();
        var infoRecords = _context.GetRecordsByType("INFO").ToList();

        if (_context.Accessor == null)
        {
            foreach (var record in infoRecords)
            {
                var dialogue = ReconstructDialogueFromScanResult(record);
                if (dialogue != null)
                {
                    dialogues.Add(dialogue);
                }
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                foreach (var record in infoRecords)
                {
                    var dialogue = ReconstructDialogueFromAccessor(record, buffer);
                    if (dialogue != null)
                    {
                        dialogues.Add(dialogue);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return dialogues;
    }

    /// <summary>
    ///     Merges split INFO records that share the same FormID.
    ///     Xbox 360 splits INFO into Base (CTDA, ANAM, PNAM, TCLT, TCLF, NAME, DNAM, QSTI)
    ///     and Response (TRDT, NAM1) records. This combines them into a single DialogueRecord.
    /// </summary>
    internal static List<DialogueRecord> MergeSplitInfoRecords(List<DialogueRecord> dialogues)
    {
        return dialogues
            .GroupBy(d => d.FormId)
            .Select(g =>
            {
                // Start with the record that has the most response data
                var best = g.OrderByDescending(d => d.Responses.Count)
                    .ThenByDescending(d => d.Responses.Sum(r => r.Text?.Length ?? 0))
                    .First();

                // Merge missing fields from other records with the same FormID
                foreach (var other in g)
                {
                    if (ReferenceEquals(other, best))
                    {
                        continue;
                    }

                    if (best.SpeakerFormId is null or 0 && other.SpeakerFormId is not null and not 0)
                    {
                        best = best with { SpeakerFormId = other.SpeakerFormId };
                    }

                    if (best.SpeakerFactionFormId is null or 0 && other.SpeakerFactionFormId is not null and not 0)
                    {
                        best = best with { SpeakerFactionFormId = other.SpeakerFactionFormId };
                    }

                    if (best.SpeakerRaceFormId is null or 0 && other.SpeakerRaceFormId is not null and not 0)
                    {
                        best = best with { SpeakerRaceFormId = other.SpeakerRaceFormId };
                    }

                    if (best.SpeakerVoiceTypeFormId is null or 0 && other.SpeakerVoiceTypeFormId is not null and not 0)
                    {
                        best = best with { SpeakerVoiceTypeFormId = other.SpeakerVoiceTypeFormId };
                    }

                    if (best.QuestFormId is null or 0 && other.QuestFormId is not null and not 0)
                    {
                        best = best with { QuestFormId = other.QuestFormId };
                    }

                    if (best.TopicFormId is null or 0 && other.TopicFormId is not null and not 0)
                    {
                        best = best with { TopicFormId = other.TopicFormId };
                    }

                    if (best.PreviousInfo is null or 0 && other.PreviousInfo is not null and not 0)
                    {
                        best = best with { PreviousInfo = other.PreviousInfo };
                    }

                    if (best.Difficulty == 0 && other.Difficulty != 0)
                    {
                        best = best with { Difficulty = other.Difficulty };
                    }

                    if (string.IsNullOrEmpty(best.EditorId) && !string.IsNullOrEmpty(other.EditorId))
                    {
                        best = best with { EditorId = other.EditorId };
                    }

                    if (best.LinkToTopics.Count == 0 && other.LinkToTopics.Count > 0)
                    {
                        best = best with { LinkToTopics = other.LinkToTopics };
                    }

                    if (best.LinkFromTopics.Count == 0 && other.LinkFromTopics.Count > 0)
                    {
                        best = best with { LinkFromTopics = other.LinkFromTopics };
                    }

                    if (best.AddTopics.Count == 0 && other.AddTopics.Count > 0)
                    {
                        best = best with { AddTopics = other.AddTopics };
                    }

                    if (best.Responses.Count == 0 && other.Responses.Count > 0)
                    {
                        best = best with { Responses = other.Responses };
                    }

                    if (other.ConditionFunctions.Count > 0)
                    {
                        var merged = new HashSet<ushort>(best.ConditionFunctions);
                        merged.UnionWith(other.ConditionFunctions);
                        best = best with { ConditionFunctions = merged.ToList() };
                    }
                }

                return best;
            })
            .ToList();
    }

    private DialogueRecord? ReconstructDialogueFromScanResult(DetectedMainRecord record)
    {
        // Find response texts strictly within this INFO record's data bounds
        var dataStart = record.Offset + 24; // Skip main record header
        var dataEnd = dataStart + record.DataSize;
        var responseTexts = _context.ScanResult.ResponseTexts
            .Where(r => r.Offset >= dataStart && r.Offset < dataEnd)
            .ToList();

        var responses = responseTexts.Select(rt => new DialogueResponse
        {
            Text = rt.Text
        }).ToList();

        return new DialogueRecord
        {
            FormId = record.FormId,
            EditorId = _context.GetEditorId(record.FormId),
            Responses = responses,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private DialogueRecord? ReconstructDialogueFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = _context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return ReconstructDialogueFromScanResult(record);
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        uint? topicFormId = null;
        uint? questFormId = null;
        uint? speakerFormId = null;
        uint? previousInfo = null;
        uint difficulty = 0;
        var hasResultScript = false;
        var responses = new List<DialogueResponse>();
        var linkToTopics = new List<uint>();
        var linkFromTopics = new List<uint>();
        var addTopics = new List<uint>();

        // CTDA condition-based speaker tracking
        uint? conditionSpeaker = null;
        uint? conditionFaction = null;
        uint? conditionRace = null;
        uint? conditionVoiceType = null;
        uint? snamSpeaker = null;
        var conditionFunctions = new List<ushort>();

        // Track current response being built
        string? currentResponseText = null;
        uint currentEmotionType = 0;
        var currentEmotionValue = 0;
        byte currentResponseNumber = 0;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(editorId))
                        _context.FormIdToEditorId[record.FormId] = editorId;
                    break;
                case "QSTI" when sub.DataLength == 4:
                    questFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "NAM1":
                    // Save previous response if any
                    if (currentResponseText != null)
                    {
                        responses.Add(new DialogueResponse
                        {
                            Text = currentResponseText,
                            EmotionType = currentEmotionType,
                            EmotionValue = currentEmotionValue,
                            ResponseNumber = currentResponseNumber
                        });
                    }

                    currentResponseText = EsmStringUtils.ReadNullTermString(subData);
                    currentEmotionType = 0;
                    currentEmotionValue = 0;
                    break;
                case "TRDT" when sub.DataLength >= 24:
                {
                    var fields = SubrecordDataReader.ReadFields("TRDT", null, subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        currentEmotionType = SubrecordDataReader.GetUInt32(fields, "EmotionType");
                        currentEmotionValue = SubrecordDataReader.GetInt32(fields, "EmotionValue");
                        currentResponseNumber = SubrecordDataReader.GetByte(fields, "ResponseNumber");
                    }
                }

                    break;
                case "PNAM" when sub.DataLength == 4:
                    previousInfo = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "ANAM" when sub.DataLength == 4:
                    speakerFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "SNAM" when sub.DataLength == 4:
                    snamSpeaker = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "TPIC" when sub.DataLength == 4:
                    topicFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "TCLT" when sub.DataLength == 4:
                {
                    var tcltFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    if (tcltFormId != 0)
                    {
                        linkToTopics.Add(tcltFormId);
                    }

                    break;
                }
                case "TCLF" when sub.DataLength == 4:
                {
                    var tclfFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    if (tclfFormId != 0)
                    {
                        linkFromTopics.Add(tclfFormId);
                    }

                    break;
                }
                case "NAME" when sub.DataLength == 4:
                {
                    var nameFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    if (nameFormId != 0)
                    {
                        addTopics.Add(nameFormId);
                    }

                    break;
                }
                case "DNAM" when sub.DataLength >= 4:
                    difficulty = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    if (difficulty > 10)
                    {
                        difficulty = 0;
                    }

                    break;
                case "SCHR":
                    hasResultScript = true;
                    break;
                case "CTDA" when sub.DataLength >= 28:
                {
                    ParseCtdaCondition(subData, record.IsBigEndian, conditionFunctions,
                        ref conditionSpeaker, ref conditionFaction, ref conditionRace, ref conditionVoiceType);
                    break;
                }
            }
        }

        // Add final response if any
        if (currentResponseText != null)
        {
            responses.Add(new DialogueResponse
            {
                Text = currentResponseText,
                EmotionType = currentEmotionType,
                EmotionValue = currentEmotionValue,
                ResponseNumber = currentResponseNumber
            });
        }

        return new DialogueRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? _context.GetEditorId(record.FormId),
            TopicFormId = topicFormId,
            QuestFormId = questFormId,
            // Speaker priority: ANAM > SNAM > CTDA GetIsID > (topic TNAM propagated later)
            SpeakerFormId = speakerFormId ?? snamSpeaker ?? conditionSpeaker,
            SpeakerFactionFormId = conditionFaction,
            SpeakerRaceFormId = conditionRace,
            SpeakerVoiceTypeFormId = conditionVoiceType,
            ConditionFunctions = conditionFunctions,
            Responses = responses,
            PreviousInfo = previousInfo,
            Difficulty = difficulty,
            LinkToTopics = linkToTopics,
            LinkFromTopics = linkFromTopics,
            AddTopics = addTopics,
            HasResultScript = hasResultScript,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    /// <summary>
    ///     Parse a CTDA condition subrecord, extracting speaker-related function parameters.
    /// </summary>
    private static void ParseCtdaCondition(
        Span<byte> subData,
        bool isBigEndian,
        List<ushort> conditionFunctions,
        ref uint? conditionSpeaker,
        ref uint? conditionFaction,
        ref uint? conditionRace,
        ref uint? conditionVoiceType)
    {
        var fields = SubrecordDataReader.ReadFields("CTDA", null, subData, isBigEndian);
        if (fields.Count == 0)
        {
            return;
        }

        var functionIndex = SubrecordDataReader.GetUInt16(fields, "FunctionIndex");
        conditionFunctions.Add(functionIndex);

        var param1 = SubrecordDataReader.GetUInt32(fields, "Parameter1");
        var runOn = SubrecordDataReader.GetUInt32(fields, "RunOn");
        var compValue = SubrecordDataReader.GetFloat(fields, "ComparisonValue");
        var typeByte = SubrecordDataReader.GetByte(fields, "Type");
        var compOp = (typeByte >> 5) & 0x7; // 0=Eq, 1=NotEq, 2=Gt, 3=GtEq, 4=Lt, 5=LtEq

        // Positive match: boolean function returns true for this param
        // Equal/GtEq + compValue~1.0  OR  NotEqual + compValue~0.0  OR  Greater + compValue~0.0
        var isPositive = runOn == 0 &&
                         ((compOp is 0 or 3 && compValue >= 0.99f) ||
                          (compOp is 1 && compValue < 0.01f) ||
                          (compOp is 2 && compValue < 0.01f));

        if (isPositive)
        {
            switch (functionIndex)
            {
                case 0x48: // GetIsID -> specific NPC speaker
                    conditionSpeaker ??= param1;
                    break;
                case 0x47: // GetInFaction -> faction-based shared dialogue
                    conditionFaction ??= param1;
                    break;
                case 0x45: // GetIsRace -> race-based dialogue
                    conditionRace ??= param1;
                    break;
                case 0x1AB: // GetIsVoiceType -> voice-type-based generic dialogue
                    conditionVoiceType ??= param1;
                    break;
            }
        }
    }
}
