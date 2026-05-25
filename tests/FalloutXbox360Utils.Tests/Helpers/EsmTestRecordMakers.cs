using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils.Tests.Helpers;

/// <summary>
///     Shared factories for ESM record subcomponents that show up identically across
///     multiple encoder/sanitizer test files. Top-level record factories (MakeNpc,
///     MakeQuest, MakePerk, etc.) stay file-local because each test wants different
///     bespoke wiring — only the truly-shared subrecord builders live here.
/// </summary>
internal static class EsmTestRecordMakers
{
    /// <summary>
    ///     Default <see cref="ActorBaseSubrecord"/> used by NPC/Creature encoder tests that
    ///     don't care about ACBS contents — just need a non-null Stats instance so the
    ///     encoder doesn't throw. Level=1 keeps BaseHealth math deterministic.
    /// </summary>
    public static ActorBaseSubrecord MakeMinimalAcbs()
    {
        return new ActorBaseSubrecord(
            Flags: 0,
            FatigueBase: 0,
            BarterGold: 0,
            Level: 1,
            CalcMin: 1,
            CalcMax: 1,
            SpeedMultiplier: 100,
            KarmaAlignment: 0f,
            DispositionBase: 0,
            TemplateFlags: 0,
            Offset: 0,
            IsBigEndian: false);
    }
}
