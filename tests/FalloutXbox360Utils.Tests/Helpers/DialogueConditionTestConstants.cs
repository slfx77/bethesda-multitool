namespace FalloutXbox360Utils.Tests.Helpers;

/// <summary>
///     CTDA condition-function indices used across CTDA sanitizer/encoder tests.
///     These are the low-byte function index field (not the +0x1000 script opcode form).
///     Comments document which parameter slot expects a FormID vs an enum/int — the
///     ConditionSanitizer's IsFormParameter logic depends on this distinction.
/// </summary>
internal static class DialogueConditionTestConstants
{
    public const ushort GetActorValue = 0x000E;     // Param1 = ActorValue enum (NOT a FormID)
    public const ushort GetIsRace = 0x0045;         // Param1 = Race FormID
    public const ushort GetIsID = 0x0048;           // Param1 = Object FormID
    public const ushort GetQuestRunning = 0x0038;   // Param1 = Quest FormID
    public const ushort HasPerk = 0x01C1;           // Param1 = Perk FormID, Param2 = Int
}
