namespace FalloutAudioTranscriber.Models;

/// <summary>
///     Cross-reference index built from ESM parsing.
///     Maps INFO FormIDs to their subtitle text, speaker, and quest.
///     Also indexes DIAL topics by editor ID for fallback speaker/quest matching.
/// </summary>
public class EsmLookupIndex
{
    private readonly Dictionary<uint, InfoEntry> _infoEntries = new();
    private readonly Dictionary<uint, string> _npcNames = new();
    private readonly HashSet<uint> _npcsWithFullName = new(); // NPCs with a FULL (display) name
    private readonly Dictionary<uint, uint> _npcVoiceTypes = new(); // NPC FormID → VTYP FormID
    private readonly Dictionary<string, string> _questEditorIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, string> _questNames = new();
    private readonly Dictionary<string, TopicEntry> _topicEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, string> _vtypEditorIds = new(); // VTYP FormID → EditorID

    // Built lazily: NPC FULL names (>= 4 chars) for substring matching in voice type folders
    private HashSet<string>? _npcFullNames;

    // Built lazily after indexing: lowercase VTYP EDID → list of (NPC FormID, NPC Name)
    private Dictionary<string, List<(uint FormId, string Name)>>? _voiceTypeToNpcs;

    /// <summary>Number of INFO records indexed.</summary>
    public int InfoCount => _infoEntries.Count;

    /// <summary>Number of NPC records indexed.</summary>
    public int NpcCount => _npcNames.Count;

    /// <summary>Number of quest records indexed.</summary>
    public int QuestCount => _questNames.Count;

    /// <summary>Number of DIAL topic records indexed.</summary>
    public int TopicCount => _topicEntries.Count;

    /// <summary>Number of VTYP (VoiceType) records indexed.</summary>
    public int VoiceTypeCount => _vtypEditorIds.Count;

    /// <summary>
    ///     Add or merge an INFO record. Xbox 360 splits each INFO into two records
    ///     with the same FormID (Base: QSTI/ANAM, Response: NAM1), so we merge both halves.
    /// </summary>
    public void AddInfo(uint formId, string? subtitleText, uint? speakerFormId, uint? questFormId)
    {
        if (_infoEntries.TryGetValue(formId, out var existing))
        {
            _infoEntries[formId] = new InfoEntry
            {
                SubtitleText = existing.SubtitleText ?? subtitleText,
                SpeakerFormId = existing.SpeakerFormId ?? speakerFormId,
                QuestFormId = existing.QuestFormId ?? questFormId
            };
        }
        else
        {
            _infoEntries[formId] = new InfoEntry
            {
                SubtitleText = subtitleText,
                SpeakerFormId = speakerFormId,
                QuestFormId = questFormId
            };
        }
    }

    public void AddNpc(uint formId, string name, uint? voiceTypeFormId = null, bool hasFullName = false)
    {
        _npcNames[formId] = name;
        if (hasFullName)
        {
            _npcsWithFullName.Add(formId);
        }

        if (voiceTypeFormId.HasValue)
        {
            _npcVoiceTypes[formId] = voiceTypeFormId.Value;
            _voiceTypeToNpcs = null; // Invalidate lazy cache
        }
    }

    public void AddVoiceType(uint formId, string editorId)
    {
        _vtypEditorIds[formId] = editorId;
        _voiceTypeToNpcs = null; // Invalidate lazy cache
    }

    public void AddQuest(uint formId, string name, string? editorId = null)
    {
        _questNames[formId] = name;
        if (editorId != null)
        {
            _questEditorIds[editorId] = name;
        }
    }

    public void AddTopic(string editorId, uint? questFormId, uint? speakerFormId)
    {
        _topicEntries[editorId] = new TopicEntry
        {
            QuestFormId = questFormId,
            SpeakerFormId = speakerFormId
        };
    }

    /// <summary>
    ///     Look up subtitle text for an INFO FormID.
    /// </summary>
    public string? GetSubtitleText(uint infoFormId)
    {
        return _infoEntries.TryGetValue(infoFormId, out var entry) ? entry.SubtitleText : null;
    }

    /// <summary>
    ///     Look up speaker name for an INFO FormID.
    /// </summary>
    public string? GetSpeakerName(uint infoFormId)
    {
        if (!_infoEntries.TryGetValue(infoFormId, out var entry) || !entry.SpeakerFormId.HasValue)
        {
            return null;
        }

        return _npcNames.GetValueOrDefault(entry.SpeakerFormId.Value);
    }

    /// <summary>
    ///     Look up quest name for an INFO FormID.
    /// </summary>
    public string? GetQuestName(uint infoFormId)
    {
        if (!_infoEntries.TryGetValue(infoFormId, out var entry) || !entry.QuestFormId.HasValue)
        {
            return null;
        }

        return _questNames.GetValueOrDefault(entry.QuestFormId.Value);
    }

    /// <summary>
    ///     Look up the speaker for a voice type folder name (e.g., "craigboone").
    ///     Returns the NPC name if exactly one NPC uses that voice type,
    ///     or if multiple share it but exactly one has a FULL (display) name.
    /// </summary>
    public string? GetSpeakerByVoiceType(string voiceTypeFolderName)
    {
        var map = GetVoiceTypeToNpcsMap();
        if (!map.TryGetValue(voiceTypeFolderName, out var npcs) || npcs.Count == 0)
        {
            return null;
        }

        if (npcs.Count == 1)
        {
            return npcs[0].Name;
        }

        // Multiple NPCs share this voice type — prefer the one with a display name (FULL).
        // Template creatures/NPCs typically lack FULL names.
        var withFullName = npcs.Where(n => _npcsWithFullName.Contains(n.FormId)).ToList();
        if (withFullName.Count == 1)
        {
            return withFullName[0].Name;
        }

        return null;
    }

    /// <summary>
    ///     Enrich a VoiceFileEntry with ESM metadata.
    ///     Primary: INFO FormID direct lookup for subtitle, speaker, quest.
    ///     Fallback 1: DIAL topic lookup via TopicEditorId for speaker/quest.
    ///     Fallback 2: VoiceType folder → unique NPC mapping for speaker.
    /// </summary>
    public void Enrich(VoiceFileEntry entry)
    {
        entry.SubtitleText = GetSubtitleText(entry.FormId);
        entry.EsmSubtitleText = entry.SubtitleText;
        entry.SpeakerName = GetSpeakerName(entry.FormId);
        entry.QuestName = GetQuestName(entry.FormId);

        // Fallback 1: DIAL topic lookup via TopicEditorId from filename
        if (entry.TopicEditorId is { Length: > 0 } &&
            _topicEntries.TryGetValue(entry.TopicEditorId, out var topic))
        {
            if (entry.SpeakerName == null && topic.SpeakerFormId.HasValue)
            {
                entry.SpeakerName = _npcNames.GetValueOrDefault(topic.SpeakerFormId.Value);
            }

            if (entry.QuestName == null && topic.QuestFormId.HasValue)
            {
                entry.QuestName = _questNames.GetValueOrDefault(topic.QuestFormId.Value);
            }
        }

        // Fallback 2: VoiceType folder → VTYP EDID → NPC speaker
        if (entry.SpeakerName == null && entry.VoiceType is { Length: > 0 })
        {
            entry.SpeakerName = GetSpeakerByVoiceType(entry.VoiceType);
        }

        // Fallback 3: NPC name substring in voice type folder (e.g., "bmneil" contains "Neil")
        if (entry.SpeakerName == null && entry.VoiceType is { Length: > 0 })
        {
            entry.SpeakerName = GetSpeakerByNameInVoiceType(entry.VoiceType);
        }

        // Fallback 4: Quest EDID prefix from filename (e.g., "vms19_greeting" → quest "VMS19")
        if (entry.QuestName == null && entry.TopicEditorId is { Length: > 0 })
        {
            var usIdx = entry.TopicEditorId.IndexOf('_');
            if (usIdx > 0 && _questEditorIds.TryGetValue(entry.TopicEditorId[..usIdx], out var questName))
            {
                entry.QuestName = questName;
            }
        }

        if (entry.SubtitleText != null)
        {
            entry.TranscriptionSource = "esm";
        }
    }

    /// <summary>
    ///     Last-resort fallback: check if any NPC FULL name (>= 4 chars) appears
    ///     as a case-insensitive substring within the voice type folder name.
    ///     Returns the name only if exactly one distinct name matches.
    /// </summary>
    private string? GetSpeakerByNameInVoiceType(string voiceTypeFolderName)
    {
        if (_npcFullNames == null)
        {
            _npcFullNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (formId, name) in _npcNames)
            {
                if (_npcsWithFullName.Contains(formId) && name.Length >= 4)
                {
                    _npcFullNames.Add(name);
                }
            }
        }

        string? matched = null;
        foreach (var name in _npcFullNames)
        {
            if (voiceTypeFolderName.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                if (matched != null && !matched.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return null; // Ambiguous — multiple different names match
                }

                matched = name;
            }
        }

        return matched;
    }

    private Dictionary<string, List<(uint FormId, string Name)>> GetVoiceTypeToNpcsMap()
    {
        if (_voiceTypeToNpcs != null)
        {
            return _voiceTypeToNpcs;
        }

        _voiceTypeToNpcs = new Dictionary<string, List<(uint, string)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (npcFormId, vtypFormId) in _npcVoiceTypes)
        {
            if (!_vtypEditorIds.TryGetValue(vtypFormId, out var vtypEdid))
            {
                continue;
            }

            var npcName = _npcNames.GetValueOrDefault(npcFormId, $"NPC_{npcFormId:X8}");

            if (!_voiceTypeToNpcs.TryGetValue(vtypEdid, out var list))
            {
                list = [];
                _voiceTypeToNpcs[vtypEdid] = list;
            }

            list.Add((npcFormId, npcName));
        }

        return _voiceTypeToNpcs;
    }

    private record struct InfoEntry
    {
        public uint? QuestFormId;
        public uint? SpeakerFormId;
        public string? SubtitleText;
    }

    private record struct TopicEntry
    {
        public uint? QuestFormId;
        public uint? SpeakerFormId;
    }
}
