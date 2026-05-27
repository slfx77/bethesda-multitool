using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;

internal sealed class AiRecordHandler(RecordParserContext context) : RecordHandlerBase(context)
{
    /// <summary>
    ///     Parse all AI Package (PACK) records from the scan result.
    ///     Only extracts location data (PLDT) needed for NPC spawn resolution.
    /// </summary>
    internal List<PackageRecord> ParsePackages()
    {
        var packages = ParseRecordList("PACK", 16384,
            (record, buffer) => ParsePackageFromAccessor(record, buffer),
            record => new PackageRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });

        Context.MergeRuntimeRecords(packages, 0x49, p => p.FormId,
            (reader, entry) => reader.ReadRuntimePackage(entry), "packages");

        return packages;
    }

    private PackageRecord ParsePackageFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new PackageRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        PackageData? packageData = null;
        PackageSchedule? schedule = null;
        PackageUseWeaponData? useWeaponData = null;
        PackageDialogueData? dialogueData = null;
        PackageIdleCollection? idleCollection = null;
        PackageLocation? location = null;
        PackageLocation? location2 = null;
        PackageTarget? target = null;
        PackageTarget? target2 = null;
        var conditions = new List<DialogueCondition>();
        var isRepeatable = false;
        var isStartingLocationLinkedRef = false;
        var hasEatMarker = false;
        var hasUseItemMarker = false;
        var hasAmbushMarker = false;
        uint? combatStyleFormId = null;
        PackageEventActionBuilder? currentEventAction = null;
        PackageEventActionBuilder? onBeginBuilder = null;
        PackageEventActionBuilder? onEndBuilder = null;
        PackageEventActionBuilder? onChangeBuilder = null;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            if (TryStartEventAction(sub.Signature, out var eventKind))
            {
                currentEventAction = new PackageEventActionBuilder(eventKind);
                switch (eventKind)
                {
                    case PackageEventActionKind.OnBegin:
                        onBeginBuilder = currentEventAction;
                        break;
                    case PackageEventActionKind.OnEnd:
                        onEndBuilder = currentEventAction;
                        break;
                    case PackageEventActionKind.OnChange:
                        onChangeBuilder = currentEventAction;
                        break;
                }

                continue;
            }

            if (currentEventAction is not null
                && PackageEventActionBuilder.IsEventPayloadSubrecord(sub.Signature))
            {
                currentEventAction.ApplySubrecord(sub.Signature, subData, record.IsBigEndian);
                continue;
            }

            currentEventAction = null;

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
                case "PKW3" when sub.DataLength >= 24:
                    useWeaponData = ParsePackageUseWeaponData(subData, record.IsBigEndian);
                    break;
                case "PKDD" when sub.DataLength >= 24:
                    dialogueData = ParsePackageDialogueData(subData, record.IsBigEndian);
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
                case "CTDA" when sub.DataLength >= 28:
                    conditions.Add(CtdaParser.Decode(subData, record.IsBigEndian));
                    break;
                case "CIS1" when conditions.Count > 0:
                {
                    var last = conditions[^1];
                    conditions[^1] = last with
                    {
                        Parameter1String = EsmStringUtils.ReadNullTermString(subData)
                    };
                    break;
                }
                case "CIS2" when conditions.Count > 0:
                {
                    var last = conditions[^1];
                    conditions[^1] = last with
                    {
                        Parameter2String = EsmStringUtils.ReadNullTermString(subData)
                    };
                    break;
                }
                case "PKPT" when sub.DataLength >= 2:
                    (isRepeatable, isStartingLocationLinkedRef) = ParsePatrolData(subData);
                    break;
                case "PKED":
                    hasEatMarker = true;
                    break;
                case "PUID":
                    hasUseItemMarker = true;
                    break;
                case "PKAM":
                    hasAmbushMarker = true;
                    break;
                case "IDLF" when sub.DataLength >= 1:
                    idleCollection = (idleCollection ?? new PackageIdleCollection()) with { Flags = subData[0] };
                    break;
                case "IDLC" when sub.DataLength >= 1:
                    idleCollection = (idleCollection ?? new PackageIdleCollection()) with { Count = subData[0] };
                    break;
                case "IDLT" when sub.DataLength >= 4:
                    idleCollection = (idleCollection ?? new PackageIdleCollection()) with
                    {
                        TimerCheckForIdle = ReadSingle(subData, record.IsBigEndian)
                    };
                    break;
                case "IDLA" when sub.DataLength >= 4:
                    idleCollection = (idleCollection ?? new PackageIdleCollection()) with
                    {
                        IdleAnimationFormIds = ParseFormIdArray(subData, record.IsBigEndian)
                    };
                    break;
                case "CNAM" when sub.DataLength == 4:
                    combatStyleFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
            }
        }

        return new PackageRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            Data = packageData,
            Schedule = schedule,
            UseWeaponData = useWeaponData,
            DialogueData = dialogueData,
            IdleCollection = idleCollection,
            Location = location,
            Location2 = location2,
            Target = target,
            Target2 = target2,
            Conditions = conditions,
            IsRepeatable = isRepeatable,
            IsStartingLocationLinkedRef = isStartingLocationLinkedRef,
            HasEatMarker = hasEatMarker,
            HasUseItemMarker = hasUseItemMarker,
            HasAmbushMarker = hasAmbushMarker,
            OnBegin = onBeginBuilder?.Build(editorId, record.FormId, Context.GetEditorId),
            OnEnd = onEndBuilder?.Build(editorId, record.FormId, Context.GetEditorId),
            OnChange = onChangeBuilder?.Build(editorId, record.FormId, Context.GetEditorId),
            CombatStyleFormId = combatStyleFormId,
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

    /// <summary>Detect and zero out uninitialized memory patterns in 32-bit flags.</summary>
    internal static uint SanitizeFlags32(uint value)
    {
        // 0xCDCDCDCD = MS CRT debug heap uninitialized fill
        if (value == 0xCDCDCDCD)
        {
            return 0;
        }

        return value;
    }

    /// <summary>Detect and zero out uninitialized memory patterns in 16-bit flags.</summary>
    internal static ushort SanitizeFlags16(ushort value)
    {
        // High byte 0xCD or 0xCC indicates uninitialized/partially-overwritten heap memory.
        // Patterns seen: 0xCDCD, 0xCDC5, 0xCDD1, 0xCDF5, 0xCC21, 0xCC31
        var highByte = value >> 8;
        if (highByte is 0xCD or 0xCC)
        {
            return 0;
        }

        return value;
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
    ///     Parse PKDD subrecord (24 bytes). Layout from PDB PACK_DIALOGUE_DATA:
    ///     float fFov, FormID iTopicID, three bool bytes, float distance,
    ///     bool bSayTo, uint trigger type.
    /// </summary>
    internal static PackageDialogueData ParsePackageDialogueData(ReadOnlySpan<byte> data, bool isBigEndian)
    {
        return new PackageDialogueData
        {
            Fov = ReadSingle(data, isBigEndian),
            TopicFormId = RecordParserContext.ReadFormId(data[4..], isBigEndian),
            NoHeadtracking = data[8] != 0,
            DoNotControlTarget = data[9] != 0,
            SpeakerMoveTalk = data[10] != 0,
            DistanceStartTalking = ReadSingle(data[12..], isBigEndian),
            SayTo = data[16] != 0,
            TriggerType = isBigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(data[20..])
                : BinaryPrimitives.ReadUInt32LittleEndian(data[20..])
        };
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

    internal static PackageUseWeaponData ParsePackageUseWeaponData(
        ReadOnlySpan<byte> data,
        bool isBigEndian)
    {
        var burstCount = isBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data[6..])
            : BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        var volleyShotsMin = isBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data[8..])
            : BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        var volleyShotsMax = isBigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data[10..])
            : BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);
        var volleyWaitMin = isBigEndian
            ? BinaryPrimitives.ReadSingleBigEndian(data[12..])
            : BinaryPrimitives.ReadSingleLittleEndian(data[12..]);
        var volleyWaitMax = isBigEndian
            ? BinaryPrimitives.ReadSingleBigEndian(data[16..])
            : BinaryPrimitives.ReadSingleLittleEndian(data[16..]);
        var weaponFormId = isBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data[20..])
            : BinaryPrimitives.ReadUInt32LittleEndian(data[20..]);

        return new PackageUseWeaponData
        {
            AlwaysHit = data[0] != 0,
            DoNoDamage = data[1] != 0,
            Crouch = data[2] != 0,
            HoldFire = data[3] != 0,
            VolleyFire = data[4] != 0,
            RepeatFire = data[5] != 0,
            BurstCount = burstCount,
            VolleyShotsMin = volleyShotsMin,
            VolleyShotsMax = volleyShotsMax,
            VolleyWaitMin = volleyWaitMin,
            VolleyWaitMax = volleyWaitMax,
            WeaponFormId = weaponFormId != 0 ? weaponFormId : null
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

    /// <summary>
    ///     Parse all Idle Animation (IDLE) records.
    /// </summary>
    internal List<IdleAnimationRecord> ParseIdleAnimations()
    {
        var idles = ParseAccessorOnly("IDLE", 1024, ParseIdleAnimationFromAccessor);

        Context.MergeRuntimeRecords(idles, 0x48, i => i.FormId,
            (reader, entry) => reader.ReadRuntimeIdleAnimation(entry), "idle animations");

        return idles;
    }

    private IdleAnimationRecord? ParseIdleAnimationFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return null;
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null, modelPath = null;
        uint parentIdle = 0, previousIdle = 0;
        byte animData = 0, loopMin = 0, loopMax = 0, flagsEx = 0;
        ushort replayDelay = 0;
        var conditionCount = 0;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);
            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                case "MODL":
                    modelPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "ANAM" when sub.DataLength >= 8:
                {
                    if (SubrecordSchemaView.TryRead("ANAM", "IDLE", subData, record.IsBigEndian) is { } v)
                    {
                        parentIdle = v.UInt32("Parent");
                        previousIdle = v.UInt32("Previous");
                    }

                    break;
                }
                case "DATA" when sub.DataLength is 8 or 6:
                {
                    if (SubrecordSchemaView.TryRead("DATA", "IDLE", subData, record.IsBigEndian) is { } v)
                    {
                        animData = v.Byte("AnimData");
                        loopMin = v.Byte("LoopMin");
                        loopMax = v.Byte("LoopMax");
                        replayDelay = v.UInt16("ReplayDelay");
                        flagsEx = v.Byte("FlagsEx");
                    }

                    break;
                }
                case "CTDA":
                    conditionCount++;
                    break;
            }
        }

        return new IdleAnimationRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            ModelPath = modelPath,
            ParentIdleFormId = parentIdle,
            PreviousIdleFormId = previousIdle,
            AnimData = animData,
            LoopMin = loopMin,
            LoopMax = loopMax,
            ReplayDelay = replayDelay,
            FlagsEx = flagsEx,
            ConditionCount = conditionCount,
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

    private static bool TryStartEventAction(string signature, out PackageEventActionKind kind)
    {
        switch (signature)
        {
            case "POBA":
                kind = PackageEventActionKind.OnBegin;
                return true;
            case "POEA":
                kind = PackageEventActionKind.OnEnd;
                return true;
            case "POCA":
                kind = PackageEventActionKind.OnChange;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private static List<uint> ParseFormIdArray(ReadOnlySpan<byte> data, bool isBigEndian)
    {
        var ids = new List<uint>(data.Length / 4);
        for (var offset = 0; offset + 4 <= data.Length; offset += 4)
        {
            ids.Add(RecordParserContext.ReadFormId(data[offset..], isBigEndian));
        }

        return ids;
    }

    private static float ReadSingle(ReadOnlySpan<byte> data, bool isBigEndian)
    {
        return isBigEndian
            ? BinaryPrimitives.ReadSingleBigEndian(data)
            : BinaryPrimitives.ReadSingleLittleEndian(data);
    }

    private sealed class PackageEventActionBuilder(PackageEventActionKind kind)
    {
        private readonly List<string> _sourceTexts = [];
        private readonly List<DialogueResultScriptParser.DialogueResultScriptBuilder> _blocks = [];
        private DialogueResultScriptParser.DialogueResultScriptBuilder? _currentBlock;
        private uint? _pendingVariableIndex;
        private byte _pendingVariableType;

        private uint _idleFormId;
        private uint _topicFormId;

        internal static bool IsEventPayloadSubrecord(string signature) =>
            signature is "INAM" or "TNAM" or "SCHR" or "SCDA" or "SCTX" or "SCRO"
                or "SLSD" or "SCVR" or "SCRV" or "NEXT";

        internal void ApplySubrecord(string signature, ReadOnlySpan<byte> data, bool isBigEndian)
        {
            switch (signature)
            {
                case "INAM" when data.Length >= 4:
                    _idleFormId = RecordParserContext.ReadFormId(data, isBigEndian);
                    break;
                case "TNAM" when data.Length >= 4:
                    _topicFormId = RecordParserContext.ReadFormId(data, isBigEndian);
                    break;
                case "SCHR":
                    DialogueResultScriptParser.FlushPendingVariable(
                        _currentBlock, ref _pendingVariableIndex, ref _pendingVariableType);
                    _currentBlock = new DialogueResultScriptParser.DialogueResultScriptBuilder();
                    _blocks.Add(_currentBlock);
                    break;
                case "SCTX":
                {
                    var sourceText = EsmStringUtils.ReadNullTermString(data);
                    if (!string.IsNullOrEmpty(sourceText))
                    {
                        _sourceTexts.Add(sourceText);
                    }

                    break;
                }
                case "SCDA":
                    _currentBlock ??= DialogueResultScriptParser.StartImplicitResultScript(_blocks);
                    _currentBlock.CompiledData = data.ToArray();
                    _currentBlock.IsBigEndianBytecode = isBigEndian;
                    break;
                case "SCRO" when data.Length >= 4:
                    _currentBlock ??= DialogueResultScriptParser.StartImplicitResultScript(_blocks);
                    _currentBlock.ReferencedObjects.Add(RecordParserContext.ReadFormId(data, isBigEndian));
                    break;
                case "SLSD" when data.Length >= 16:
                    _currentBlock ??= DialogueResultScriptParser.StartImplicitResultScript(_blocks);
                    _pendingVariableIndex = isBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(data)
                        : BinaryPrimitives.ReadUInt32LittleEndian(data);
                    var isIntegerRaw = isBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(data[12..])
                        : BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
                    _pendingVariableType = isIntegerRaw != 0 ? (byte)1 : (byte)0;
                    break;
                case "SCVR":
                {
                    _currentBlock ??= DialogueResultScriptParser.StartImplicitResultScript(_blocks);
                    var variableName = EsmStringUtils.ReadNullTermString(data);
                    if (_pendingVariableIndex.HasValue)
                    {
                        _currentBlock.Variables.Add(new ScriptVariableInfo(
                            _pendingVariableIndex.Value, variableName, _pendingVariableType));
                        _pendingVariableIndex = null;
                    }

                    break;
                }
                case "SCRV" when data.Length >= 4:
                    _currentBlock ??= DialogueResultScriptParser.StartImplicitResultScript(_blocks);
                    var variableIndex = RecordParserContext.ReadFormId(data, isBigEndian);
                    _currentBlock.ReferencedObjects.Add(0x80000000 | variableIndex);
                    break;
                case "NEXT":
                    DialogueResultScriptParser.FlushPendingVariable(
                        _currentBlock, ref _pendingVariableIndex, ref _pendingVariableType);
                    _currentBlock ??= DialogueResultScriptParser.StartImplicitResultScript(_blocks);
                    _currentBlock.HasNextSeparator = true;
                    _currentBlock = null;
                    break;
            }
        }

        internal PackageEventAction Build(string? editorId, uint packageFormId, Func<uint, string?> resolveFormName)
        {
            DialogueResultScriptParser.FlushPendingVariable(
                _currentBlock, ref _pendingVariableIndex, ref _pendingVariableType);

            return new PackageEventAction
            {
                Kind = kind,
                IdleFormId = _idleFormId,
                TopicFormId = _topicFormId,
                Scripts = DialogueResultScriptParser.BuildResultScripts(
                    _sourceTexts, _blocks, editorId, packageFormId, resolveFormName)
            };
        }
    }
}
