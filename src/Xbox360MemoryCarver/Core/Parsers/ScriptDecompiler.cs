using System.Text;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Parsers;

/// <summary>
///     Decompiler for compiled Bethesda ObScript bytecode (Xbox 360 / Big-Endian).
///     
///     Based on analysis of xNVSE's ScriptAnalyzer implementation.
///     This provides basic decompilation - not all opcodes are fully supported.
///     
///     Reference: https://github.com/xNVSE/NVSE/blob/master/nvse/nvse/ScriptAnalyzer.cpp
/// </summary>
public class ScriptDecompiler
{
    // Statement opcodes
    private const ushort Op_ScriptName = 0x0010;
    private const ushort Op_Begin = 0x0011;
    private const ushort Op_End = 0x0012;
    private const ushort Op_Short = 0x0013;
    private const ushort Op_Long = 0x0014;
    private const ushort Op_Float = 0x0015;
    private const ushort Op_Set = 0x0016;
    private const ushort Op_If = 0x0017;
    private const ushort Op_Else = 0x0018;
    private const ushort Op_ElseIf = 0x0019;
    private const ushort Op_EndIf = 0x001A;
    private const ushort Op_While = 0x001B;
    private const ushort Op_Ref = 0x001C;
    private const ushort Op_RefFunc = 0x001D;
    private const ushort Op_Return = 0x001E;
    private const ushort Op_Loop = 0x001F;

    // Begin block event types (partial list from Oblivion/FNV)
    private static readonly Dictionary<ushort, string> EventTypes = new()
    {
        [0x0000] = "GameMode",
        [0x0001] = "MenuMode",
        [0x0002] = "OnActivate",
        [0x0003] = "OnAdd",
        [0x0004] = "OnEquip",
        [0x0005] = "OnUnequip",
        [0x0006] = "OnDrop",
        [0x0007] = "SayToDone",
        [0x0008] = "OnHit",
        [0x0009] = "OnHitWith",
        [0x000A] = "OnDeath",
        [0x000B] = "OnMurder",
        [0x000C] = "OnCombatEnd",
        [0x000D] = "OnLoad",
        [0x000E] = "OnMagicEffectHit",
        [0x000F] = "OnTrigger",
        [0x0010] = "OnTriggerEnter",
        [0x0011] = "OnTriggerLeave",
        [0x0012] = "OnActorEquip",
        [0x0013] = "OnActorUnequip",
        [0x0014] = "OnReset",
        [0x0015] = "ScriptEffectStart",
        [0x0016] = "ScriptEffectFinish",
        [0x0017] = "ScriptEffectUpdate",
        [0x0018] = "OnPackageStart",
        [0x0019] = "OnPackageDone",
        [0x001A] = "OnPackageChange",
        [0x001B] = "OnStartCombat",
        [0x001C] = "OnSell",
        [0x001D] = "OnOpen",
        [0x001E] = "OnClose",
        // FNV specific
        [0x0020] = "OnGrab",
        [0x0021] = "OnRelease",
        [0x0022] = "OnDestructionStageChange",
        [0x0023] = "OnFire",
        [0x0024] = "OnNPCActivate"
    };

    // Common command opcodes (partial list - there are hundreds)
    private static readonly Dictionary<ushort, string> CommandNames = new()
    {
        // Basic functions
        [0x1000] = "unk1000",
        [0x1001] = "unk1001",
        [0x1005] = "SetStage",
        [0x1006] = "GetStage",
        [0x1007] = "GetStageDone",
        [0x100C] = "GetSecondsPassed",
        [0x100F] = "Activate",
        [0x1012] = "GetDistance",
        [0x1018] = "GetPos",
        [0x1019] = "SetPos",
        [0x101A] = "GetAngle",
        [0x101B] = "SetAngle",
        [0x101C] = "GetStartingPos",
        [0x101D] = "GetStartingAngle",
        [0x1024] = "GetActorValue",
        [0x1025] = "SetActorValue",
        [0x1026] = "ModActorValue",
        [0x1027] = "GetBaseActorValue",
        [0x102A] = "GetLineOfSight",
        [0x102B] = "AddSpell",
        [0x102C] = "RemoveSpell",
        [0x102E] = "GetItemCount",
        [0x1034] = "AddItem",
        [0x1035] = "RemoveItem",
        [0x1036] = "EquipItem",
        [0x1037] = "UnequipItem",
        [0x103B] = "GetSelf",
        [0x103C] = "GetContainer",
        [0x1046] = "Enable",
        [0x1047] = "Disable",
        [0x104B] = "GetDisabled",
        [0x104E] = "PlayGroup",
        [0x104F] = "LoopGroup",
        [0x1059] = "StartConversation",
        [0x105D] = "GetDead",
        [0x1064] = "GetCurrentAIPackage",
        [0x1065] = "IsCurrentFurnitureRef",
        [0x1066] = "IsCurrentFurnitureObj",
        [0x1068] = "GetFactionRank",
        [0x1069] = "GetFactionRankDifference",
        [0x106A] = "GetDetected",
        [0x1070] = "MoveTo",
        [0x1072] = "GetRandomPercent",
        [0x1073] = "GetQuestVariable",
        [0x1078] = "Say",
        [0x1079] = "SayTo",
        [0x107A] = "GetScriptVariable",
        [0x107C] = "StartQuest",
        [0x107D] = "StopQuest",
        [0x107E] = "GetQuestRunning",
        [0x1084] = "ShowMessage",
        [0x1085] = "SetAlert",
        [0x108A] = "Kill",
        [0x108D] = "GetHeadingAngle",
        [0x108F] = "IsActionRef",
        [0x1097] = "GetInCell",
        [0x109C] = "GetIsReference",
        [0x10A0] = "SetFactionRank",
        [0x10A1] = "ModFactionRank",
        [0x10A3] = "PlaceAtMe",
        [0x10A5] = "GetIsID",
        [0x10C1] = "IsWeaponOut",
        [0x10C5] = "GetCrimeGold",
        [0x10C6] = "SetCrimeGold",
        [0x10C7] = "ModCrimeGold",
        [0x10D8] = "GetPlayerControlsDisabled",
        [0x10DF] = "DisablePlayerControls",
        [0x10E0] = "EnablePlayerControls",
        [0x10E9] = "GetDetectionLevel",
        [0x1100] = "ForceActorValue",
        [0x1101] = "ModCurrentValue",
        [0x110A] = "ResetHealth",
        [0x1137] = "GetIsCreature",
        [0x1139] = "GetPlayerTeammate",
        [0x113E] = "ShowBarterMenu",
        [0x1143] = "GetInZone",
        [0x1148] = "SetEssential",
        [0x118E] = "SetWeather",
        // NVSE commands start at higher opcodes
        [0x1400] = "GetNVSEVersion",
        [0x1401] = "GetNVSERevision"
    };

    private readonly ReadOnlyMemory<byte> _data;
    private readonly int _offset;
    private readonly int _size;
    private readonly List<string> _variables = [];
    private readonly List<string> _refVariables = [];
    private int _indentLevel;

    public ScriptDecompiler(ReadOnlyMemory<byte> data, int offset, int size)
    {
        _data = data;
        _offset = offset;
        _size = size;
    }

    /// <summary>
    ///     Decompile the script bytecode to source text.
    /// </summary>
    public DecompilationResult Decompile()
    {
        var result = new DecompilationResult();
        var sb = new StringBuilder();
        var span = _data.Span;
        var pos = _offset;
        var end = _offset + _size;

        try
        {
            while (pos + 4 <= end && pos < span.Length)
            {
                var opcode = BinaryUtils.ReadUInt16BE(span, pos);
                var length = BinaryUtils.ReadUInt16BE(span, pos + 2);
                var dataStart = pos + 4;

                // Get the statement data
                var stmtData = length > 0 && dataStart + length <= span.Length
                    ? span.Slice(dataStart, length)
                    : ReadOnlySpan<byte>.Empty;

                var line = DecompileStatement(opcode, stmtData, ref result);
                if (!string.IsNullOrEmpty(line))
                {
                    sb.Append(new string('\t', _indentLevel));
                    sb.AppendLine(line);
                }

                pos = dataStart + length;
            }

            result.Success = true;
            result.DecompiledText = sb.ToString();
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            result.DecompiledText = sb.ToString() + $"\n; ERROR: {ex.Message}";
        }

        return result;
    }

    private string DecompileStatement(ushort opcode, ReadOnlySpan<byte> data, ref DecompilationResult result)
    {
        switch (opcode)
        {
            case Op_ScriptName:
                result.ScriptType = "Object";
                return "scn DecompiledScript";

            case Op_Begin:
                _indentLevel = 0;
                var beginLine = DecompileBegin(data, ref result);
                _indentLevel = 1;
                return beginLine;

            case Op_End:
                _indentLevel = 0;
                return "End";

            case Op_Short:
                var shortVar = $"sVar{_variables.Count}";
                _variables.Add(shortVar);
                return $"short {shortVar}";

            case Op_Long:
                var longVar = $"iVar{_variables.Count}";
                _variables.Add(longVar);
                return $"int {longVar}";

            case Op_Float:
                var floatVar = $"fVar{_variables.Count}";
                _variables.Add(floatVar);
                return $"float {floatVar}";

            case Op_Ref:
                var refVar = $"rVar{_refVariables.Count}";
                _refVariables.Add(refVar);
                return $"ref {refVar}";

            case Op_Set:
                return DecompileSet(data);

            case Op_If:
                var ifLine = DecompileConditional("If", data);
                _indentLevel++;
                return ifLine;

            case Op_ElseIf:
                _indentLevel--;
                var elifLine = DecompileConditional("ElseIf", data);
                _indentLevel++;
                return elifLine;

            case Op_Else:
                _indentLevel--;
                var elseLine = "Else";
                _indentLevel++;
                return elseLine;

            case Op_EndIf:
                _indentLevel--;
                return "EndIf";

            case Op_While:
                var whileLine = DecompileConditional("While", data);
                _indentLevel++;
                return whileLine;

            case Op_Loop:
                _indentLevel--;
                return "Loop";

            case Op_Return:
                return "Return";

            case Op_RefFunc:
                // Reference function call - structure is different
                return "; RefFunction call (complex)";

            default:
                // Check if it's a command
                if (opcode >= 0x1000)
                {
                    return DecompileCommand(opcode, data);
                }
                return $"; Unknown opcode 0x{opcode:X4}";
        }
    }

    private string DecompileBegin(ReadOnlySpan<byte> data, ref DecompilationResult result)
    {
        if (data.Length < 6) return "Begin ; (insufficient data)";

        var eventType = BinaryUtils.ReadUInt16BE(data, 0);
        var jumpLength = BinaryUtils.ReadUInt32BE(data, 2);

        var eventName = EventTypes.TryGetValue(eventType, out var name) ? name : $"UnknownEvent(0x{eventType:X4})";

        result.EventBlocks.Add(eventName);

        return $"Begin {eventName}";
    }

    private string DecompileSet(ReadOnlySpan<byte> data)
    {
        // Set statement has: varIdx (2) + expression data
        // This is simplified - full decompilation would need expression parsing
        if (data.Length < 2) return "Set ; (insufficient data)";

        var varIdx = BinaryUtils.ReadUInt16BE(data, 0);
        var varName = varIdx < _variables.Count ? _variables[varIdx] : $"var{varIdx}";

        // TODO: Parse expression
        return $"Set {varName} To ; (expression)";
    }

    private string DecompileConditional(string keyword, ReadOnlySpan<byte> data)
    {
        // Conditional has: jumpOffset (2) + expression data
        if (data.Length < 2) return $"{keyword} ; (insufficient data)";

        var jumpOffset = BinaryUtils.ReadUInt16BE(data, 0);

        // TODO: Parse condition expression
        return $"{keyword} ; (condition, jump={jumpOffset})";
    }

    private string DecompileCommand(ushort opcode, ReadOnlySpan<byte> data)
    {
        var cmdName = CommandNames.TryGetValue(opcode, out var name) ? name : $"Cmd_{opcode:X4}";

        // TODO: Parse command arguments based on command info
        if (data.Length > 0)
        {
            return $"{cmdName} ; ({data.Length} bytes args)";
        }

        return cmdName;
    }
}

/// <summary>
///     Result of script decompilation.
/// </summary>
public class DecompilationResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string DecompiledText { get; set; } = string.Empty;
    public string ScriptType { get; set; } = "Unknown";
    public List<string> EventBlocks { get; } = [];
}
