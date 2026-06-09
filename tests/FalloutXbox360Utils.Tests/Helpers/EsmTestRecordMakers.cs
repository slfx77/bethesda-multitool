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
    ///     Default <see cref="ActorBaseSubrecord" /> used by NPC/Creature encoder tests that
    ///     don't care about ACBS contents — just need a non-null Stats instance so the
    ///     encoder doesn't throw. Level=1 keeps BaseHealth math deterministic.
    /// </summary>
    public static ActorBaseSubrecord MakeMinimalAcbs()
    {
        return new ActorBaseSubrecord(
            0,
            0,
            0,
            1,
            1,
            1,
            100,
            0f,
            0,
            0,
            0,
            false);
    }
}