using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Battlespire;

/// <summary>A fixed-capacity SAVEVARS block: its count word, the non-zero slots found, and the records the count claims.</summary>
internal sealed class BattlespireSaveVarsBlock<T>
{
    internal BattlespireSaveVarsBlock(int declaredCount, int populatedCount, IReadOnlyList<T> records)
    {
        DeclaredCount = declaredCount;
        PopulatedCount = populatedCount;
        Records = records;
    }

    /// <summary>The count word beside the block.</summary>
    public int DeclaredCount { get; }

    /// <summary>How many slots hold any non-zero byte — the check that DISCRIMINATES a block boundary.</summary>
    public int PopulatedCount { get; }

    /// <summary>The first <see cref="DeclaredCount" /> records.</summary>
    public IReadOnlyList<T> Records { get; }

    /// <summary>True when the count word equals the populated-slot count (6/6 blocks on the fixture).</summary>
    public bool CountsAgree => DeclaredCount == PopulatedCount;
}

/// <summary>Maps an NPC's RecordID to the 3-letter conversation stem its <c>&lt;stem&gt;[BMFT].TXT</c> files use.</summary>
internal readonly record struct BattlespireSaveConversation(uint RecordId, string Stem);

/// <summary>
///     One of the level's placed enemies (56 on L1). <paramref name="EnemyId" /> is
///     <c>EnemyList + 1</c> because 0 means "random" in the BS6; <see cref="EnemyListIndex" /> undoes it.
/// </summary>
internal readonly record struct BattlespireSaveStaticEnemy(
    uint RecordId,
    int EnemyId,
    int Speed,
    int Strength,
    int SpellPoints,
    int Health,
    int Skill,
    int Goal,
    uint SpellArray,
    IReadOnlyList<int> SpellIds)
{
    public int EnemyListIndex => EnemyId - 1;

    public string? EnemyName => BattlespireSaveLists.EnemyName(EnemyListIndex);
}

/// <summary>A gem's or trap's health/spell-point modifier: the object's RecordID, the signed amount, and which pool.</summary>
internal readonly record struct BattlespireSaveHpSpModify(uint RecordId, int Value, bool IsSpellPoints);

/// <summary>A sigil of warding: the object's RecordID, its Y (== the BS6 <c>POSI.y</c> raw, 3/3) and a zero word.</summary>
internal readonly record struct BattlespireSaveSigil(uint RecordId, int Y, uint Unknown);

/// <summary>A conversation variable: its hash and (boolean-shaped) value; the name resolves through UESP's list.</summary>
internal readonly record struct BattlespireSaveVariable(uint Hash, uint Value)
{
    public string? Name => BattlespireSaveLists.VariableName(Hash);
}

/// <summary>An NPC's local conversation variables, keyed by its RecordID (a BS6 id when the NPC has no tree record).</summary>
internal readonly record struct BattlespireSaveLocalVariables(
    int Slot,
    uint RecordId,
    IReadOnlyList<BattlespireSaveVariable> Variables);

/// <summary>
///     Battlespire's <c>SAVEVARS.DAT</c>: a fixed 34,805-byte file of fixed-capacity blocks, all
///     little-endian. Original RE 2026-09-07 against the SAVE0 fixture, with UESP's
///     "Mod:Battlespire/Save Game Format" as the spec.
///     <para>
///         ⚑
///         <b>
///             EXACT TILING: 34,805 = 791 + 260 + 24 + 1,280 + 2,137 + 1,024 + 4 + 7,168 + 4 + 1,152
///             + 4 + 512 + 4 + 768 + 4 + 97 + 4 + 10,752 + 8,704 + 16 + 96
///         </b>
///         , every block at capacity x
///         record length. ⚠ Fixed sizes tile for ANY partition that sums to the file length, so the
///         tiling alone discriminates nothing. What does: each count word equals its block's count
///         of non-zero slots — ConversationMap 21/21, StaticEnemy 56/56, HP/SP 7/7, Block5 0/0, Sigil
///         3/3, GlobalVariable 26/26 — and the ids inside resolve OUTSIDE the file (StaticEnemy
///         RecordIDs -> tree monsters with EnemyType == EnemyId - 1 on 56/56; 21/21 conversation
///         stems have a <c>&lt;stem&gt;[BMFT].TXT</c> in TXT.BSA; sigil Y == BS6 <c>POSI.y</c> raw
///         3/3; HP/SP records point at the level's "gem" and "1hurt" objects 7/7; all 28 variable
///         hashes are in UESP's list, with PCMale=1 / PCFemale=0 agreeing with the character).
///     </para>
///     <para>
///         Layout: [0,791) a byte-identical copy of the tree Player record's BODY — bytes 0..790
///         match byte for byte (⛔ this does NOT decide UESP's 787 against 791: body bytes 743..790
///         are zero and [791,1051) is a zero gap, so any copy length in 743..1,051 looks the same
///         here — see <see cref="BattlespireSaveCharacter" />, the question is OPEN); [791,1051)
///         zero; [1051,1075) six u32
///         Misc: CurrentLevel (1), CurrentTimestamp (1,169,900 — >= every log timestamp), 0, 0, 4, 4;
///         [1075,2355) LogText; [2355,4492) 534 u32 (9 non-zero on the fixture — a 5-word leading
///         run of log timestamps and an undecoded quartet at 16..19) + 1 byte; [4492,5516) 128 x 8
///         ConversationMap, count @5516; [5520,12688) 128 x 56 StaticEnemy, count @12688;
///         [12692,13844) 128 x 9 HP/SPModify (⚠ IsSP is a u8, the 9th byte — UESP's u32 would make
///         the record 12), count @13844; [13848,14360) Block5 (zero), word @14360; [14364,15132)
///         64 x 12 Sigil, count @15132; [15136,15233) 97 unknown zero bytes; ⚠ u32 @15233 = the
///         GlobalVariable count, BEFORE its block unlike every other count; [15237,25989) 1,344 x 8
///         GlobalVariable, filled from the start; [25989,34693) 128 x 68 LocalVariable, filled from
///         the END (slots 126 and 127 on the fixture); [34693,34709) 16 u8 MonsterTypeCount in
///         EnemyList order; [34709,34805) 96 zero bytes.
///     </para>
///     <para>
///         ⚠ The log region is not a clean array: 15 NUL-terminated runs (each the game's message
///         MINUS its first character, as UESP says) several of which are tails of older messages, so
///         it is a ring/overwrite buffer whose write discipline is not established. ⚠⚠ The 534 words
///         at 2,355 are NOT 534 timestamps: exactly 9 are non-zero — indices 0..4 (all &lt;= the
///         clock), then a zero gap, then 1,169,900 / 127 / 127 / 0x271000 at indices 16..19, and
///         0x271000 = 2,560,000 exceeds the clock, so that quartet is a different sub-block (a write
///         cursor?), OPEN. <see cref="LogTimestamps" /> is therefore the leading non-zero run only;
///         the whole region stays available as <see cref="LogTimestampWords" />. Both regions are
///         exposed raw as well as split.
///     </para>
///     <para>
///         ⛔ <b>The five leading words are NOT in any order.</b> They read 1,166,615 / 1,146,280 /
///         1,146,280 / 1,117,955 / 1,120,830 — index 1 ties index 2 and index 3 is LESS than index 4,
///         so they are neither strictly descending nor non-increasing. An earlier revision of this
///         comment called them "descending, newest first" and used that as the RATIONALE for taking
///         the leading run ("written newest-first from index 0, so a word after a zero cannot belong
///         to the run"); the fixture's own numbers refute the rationale. The run is kept because it
///         is the conservative reading, not because the write order is known — see
///         <see cref="LogTimestamps" /> for the alternative the same bytes support equally well.
///     </para>
/// </summary>
internal sealed class BattlespireSaveVars
{
    /// <summary>The length every SAVEVARS.DAT has.</summary>
    public const int FileLength = 34_805;

    public const int PlayerOffset = 0;
    public const int MiscOffset = 1_051;
    public const int MiscCount = 6;
    public const int LogTextOffset = 1_075;
    public const int LogTextLength = 1_280;
    public const int LogTimestampsOffset = 2_355;
    public const int LogTimestampCount = 534;

    /// <summary>
    ///     How many of the 534 words <see cref="BattlespireSaveVars.LogTimestamps" /> will consider.
    ///     ⚠ CHOSEN, not measured: the fixture's stamps stop at 5 and words 16..19 are established as
    ///     something else, so this file cannot locate the boundary. 16 is the smallest bound that
    ///     holds every stamp this save writes AND cannot reach word 16; see
    ///     <see cref="BattlespireSaveVars.LogTimestamps" /> for the two readings it sits between.
    /// </summary>
    public const int LogTimestampSlotCount = 16;

    public const int ConversationMapOffset = 4_492;
    public const int ConversationMapCapacity = 128;
    public const int ConversationMapRecordLength = 8;
    public const int ConversationMapCountOffset = 5_516;
    public const int StaticEnemyOffset = 5_520;
    public const int StaticEnemyCapacity = 128;
    public const int StaticEnemyRecordLength = 56;
    public const int StaticEnemyCountOffset = 12_688;
    public const int HpSpModifyOffset = 12_692;
    public const int HpSpModifyCapacity = 128;
    public const int HpSpModifyRecordLength = 9;
    public const int HpSpModifyCountOffset = 13_844;
    public const int Block5Offset = 13_848;
    public const int Block5Length = 512;
    public const int Block5WordOffset = 14_360;
    public const int SigilOffset = 14_364;
    public const int SigilCapacity = 64;
    public const int SigilRecordLength = 12;
    public const int SigilCountOffset = 15_132;
    public const int UnknownGapOffset = 15_136;
    public const int UnknownGapLength = 97;
    public const int GlobalVariableCountOffset = 15_233;
    public const int GlobalVariableOffset = 15_237;
    public const int GlobalVariableCapacity = 1_344;
    public const int GlobalVariableRecordLength = 8;
    public const int LocalVariableOffset = 25_989;
    public const int LocalVariableCapacity = 128;
    public const int LocalVariableRecordLength = 68;
    public const int LocalVariableSlots = 8;
    public const int MonsterTypeCountOffset = 34_693;
    public const int MonsterTypeCountLength = 16;
    public const int TailOffset = 34_709;
    public const int TailLength = 96;

    private const int StaticEnemySpellCount = 5;

    private readonly byte[] _file;

    private BattlespireSaveVars(string name, byte[] file)
    {
        Name = name;
        _file = file;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>The player body copied from the tree (791 bytes at offset 0).</summary>
    public required BattlespireSaveCharacter Player { get; init; }

    /// <summary>Misc word 0: the map level (1 = L1, "The Weir Gate").</summary>
    public required uint CurrentLevel { get; init; }

    /// <summary>Misc word 1: the game clock (milliseconds by UESP's reading, unverified).</summary>
    public required uint CurrentTimestamp { get; init; }

    /// <summary>Misc words 2..5, undecoded (0, 0, 4, 4 on the fixture).</summary>
    public required IReadOnlyList<uint> MiscUnknown { get; init; }

    /// <summary>The 1,280-byte log region, raw.</summary>
    public ReadOnlyMemory<byte> LogText => new(_file, LogTextOffset, LogTextLength);

    /// <summary>The non-empty NUL-terminated runs in <see cref="LogText" />, in file order.</summary>
    public required IReadOnlyList<string> LogMessages { get; init; }

    /// <summary>The whole 534-word region at 2,355, undecoded and handed back as read.</summary>
    public required IReadOnlyList<uint> LogTimestampWords { get; init; }

    /// <summary>
    ///     ⚠ The leading run of non-zero words within the first <see cref="LogTimestampSlotCount" />
    ///     (16) — the words established as log timestamps. On the
    ///     fixture that is 5: 1,166,615 / 1,146,280 / 1,146,280 / 1,117,955 / 1,120,830, each at or
    ///     below <see cref="CurrentTimestamp" /> (1,169,900). ⛔ They are NOT ordered — index 1 ties
    ///     index 2 and index 3 is below index 4 — so no "newest first" reading is claimed and the
    ///     list is handed back in file order. What follows the run is a zero gap and then words
    ///     16..19 = 1,169,900 / 127 / 127 / 0x271000; 0x271000 = 2,560,000 EXCEEDS the clock, so
    ///     those four are NOT timestamps and are left in <see cref="LogTimestampWords" /> undecoded.
    ///     Reading the whole region as timestamps was the overclaim this replaces.
    ///     <para>
    ///         ⚠⚠ <b>Two readings fit this fixture and it cannot separate them.</b> (a) A variable
    ///         leading run over the whole 534-word region. (b) A FIXED 16-SLOT timestamp array at
    ///         2,355 with the quartet beginning exactly at slot 16 — which fits the 15 log runs into
    ///         16 slots and makes <c>words[16] == CurrentTimestamp</c> a "current" field rather than
    ///         the coincidence reading (a) has to treat it as. Slots 5..15 are zero here (non-zero
    ///         words are exactly indices 0-4 and 16-19), so both return the same five values on this
    ///         save and nothing observable separates them; a save with more than five stamps, or one
    ///         writing past slot 15, would. The boundary is therefore OPEN.
    ///     </para>
    ///     <para>
    ///         ⚠ What this property does with that: the leading non-zero run BOUNDED AT
    ///         <see cref="LogTimestampSlotCount" /> = 16 — (a)'s rule inside (b)'s boundary. The
    ///         bound is a CHOICE, not a measurement, and it is not free: on a save that wrote more
    ///         than 16 stamps under reading (a) this would truncate them. It is preferred because
    ///         the unbounded run would, on a save whose slots 5..15 are written, return words 16..19
    ///         — including 0x271000 = 2,560,000, which EXCEEDS the clock and so is established here
    ///         as not a stamp. Returning fewer stamps is a documented limitation; returning a word
    ///         measured to be something else would be a decode fault. Callers wanting the raw truth
    ///         should read <see cref="LogTimestampWords" />, which is all 534 words untouched.
    ///     </para>
    /// </summary>
    public required IReadOnlyList<uint> LogTimestamps { get; init; }

    public required BattlespireSaveVarsBlock<BattlespireSaveConversation> ConversationMap { get; init; }

    public required BattlespireSaveVarsBlock<BattlespireSaveStaticEnemy> StaticEnemies { get; init; }

    public required BattlespireSaveVarsBlock<BattlespireSaveHpSpModify> HpSpModifiers { get; init; }

    /// <summary>The 512-byte Block5, never seen populated.</summary>
    public ReadOnlyMemory<byte> Block5 => new(_file, Block5Offset, Block5Length);

    /// <summary>The word after Block5 (0 on the fixture).</summary>
    public required uint Block5Word { get; init; }

    public required BattlespireSaveVarsBlock<BattlespireSaveSigil> Sigils { get; init; }

    /// <summary>The 97 bytes before the global-variable count, all zero on the fixture.</summary>
    public ReadOnlyMemory<byte> UnknownGap => new(_file, UnknownGapOffset, UnknownGapLength);

    public required BattlespireSaveVarsBlock<BattlespireSaveVariable> GlobalVariables { get; init; }

    /// <summary>The populated local-variable slots, in slot order (retail fills from the END).</summary>
    public required IReadOnlyList<BattlespireSaveLocalVariables> LocalVariables { get; init; }

    /// <summary>Per-enemy-type counts of monsters holding a local-variable record, EnemyList order.</summary>
    public required IReadOnlyList<byte> MonsterTypeCounts { get; init; }

    /// <summary>The last 96 bytes, all zero on the fixture.</summary>
    public ReadOnlyMemory<byte> Tail => new(_file, TailOffset, TailLength);

    /// <summary>The whole file, for callers that need a region this reader does not name.</summary>
    public ReadOnlySpan<byte> RawBytes => _file;

    /// <summary>Content probe: the exact length and a printable character-name byte at offset 0.</summary>
    public static bool IsSaveVars(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length == FileLength && bytes[0] >= 0x20 && bytes[0] < 0x7F;
    }

    /// <summary>Parses the file, throwing <see cref="InvalidDataException" /> when it does not tile.</summary>
    public static BattlespireSaveVars Parse(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(name);
        if (bytes.Length != FileLength)
        {
            throw new InvalidDataException(
                $"{name}: SAVEVARS.DAT is {FileLength} bytes on every save measured, this one is {bytes.Length}.");
        }

        var b = bytes.AsSpan();
        var misc = new uint[MiscCount];
        for (var i = 0; i < MiscCount; i++)
        {
            misc[i] = U32(b, MiscOffset + i * 4);
        }

        var words = new uint[LogTimestampCount];
        for (var i = 0; i < LogTimestampCount; i++)
        {
            words[i] = U32(b, LogTimestampsOffset + i * 4);
        }

        // The leading non-zero run WITHIN THE FIRST LogTimestampSlotCount (16) WORDS. Two bounds
        // were available and neither is measured: an unbounded run over all 534 words would, on any
        // save whose slots 5..15 are written, walk straight into the undecoded quartet at 16..19 and
        // hand back 0x271000 = 2,560,000, a value this fixture establishes is PAST the clock — the
        // fixture's zeros at 5..15 are the only thing that stops it here, so that guarantee would
        // hold by data, not by construction. Bounding at 16 takes the fixed-array reading's boundary
        // instead: it can return fewer stamps than a longer save wrote, but never a word established
        // as something other than a stamp, and LogTimestampWords still exposes all 534 raw. The run
        // is NOT justified by a write order — the fixture's five words are unordered (index 3 <
        // index 4). See LogTimestamps for both readings; the boundary itself is OPEN.
        var stamps = 0;
        while (stamps < LogTimestampSlotCount && words[stamps] != 0)
        {
            stamps++;
        }

        return new BattlespireSaveVars(name, bytes)
        {
            Player = BattlespireSaveCharacter.Parse(b[..], name, BattlespireSaveCharacterKind.Player),
            CurrentLevel = misc[0],
            CurrentTimestamp = misc[1],
            MiscUnknown = misc[2..],
            LogMessages = SplitLog(b.Slice(LogTextOffset, LogTextLength)),
            LogTimestampWords = words,
            LogTimestamps = words[..stamps],
            ConversationMap = ReadBlock(b, name, "ConversationMap", ConversationMapOffset, ConversationMapCapacity,
                ConversationMapRecordLength, ConversationMapCountOffset, ReadConversation),
            StaticEnemies = ReadBlock(b, name, "StaticEnemy", StaticEnemyOffset, StaticEnemyCapacity,
                StaticEnemyRecordLength, StaticEnemyCountOffset, ReadStaticEnemy),
            HpSpModifiers = ReadBlock(b, name, "HP/SPModify", HpSpModifyOffset, HpSpModifyCapacity,
                HpSpModifyRecordLength, HpSpModifyCountOffset, ReadHpSpModify),
            Block5Word = U32(b, Block5WordOffset),
            Sigils = ReadBlock(b, name, "Sigil", SigilOffset, SigilCapacity, SigilRecordLength, SigilCountOffset,
                ReadSigil),
            GlobalVariables = ReadBlock(b, name, "GlobalVariable", GlobalVariableOffset, GlobalVariableCapacity,
                GlobalVariableRecordLength, GlobalVariableCountOffset, ReadVariable),
            LocalVariables = ReadLocalVariables(b),
            MonsterTypeCounts = b.Slice(MonsterTypeCountOffset, MonsterTypeCountLength).ToArray()
        };
    }

    private static BattlespireSaveVarsBlock<T> ReadBlock<T>(
        ReadOnlySpan<byte> file, string name, string block, int offset, int capacity, int recordLength,
        int countOffset, Func<ReadOnlySpan<byte>, T> read)
    {
        var declared = U32(file, countOffset);
        if (declared > (uint)capacity)
        {
            throw new InvalidDataException(
                $"{name}: {block} count word {declared} exceeds the block's capacity of {capacity}.");
        }

        var populated = 0;
        for (var i = 0; i < capacity; i++)
        {
            if (file.Slice(offset + i * recordLength, recordLength).IndexOfAnyExcept((byte)0) >= 0)
            {
                populated++;
            }
        }

        var records = new T[declared];
        for (var i = 0; i < records.Length; i++)
        {
            records[i] = read(file.Slice(offset + i * recordLength, recordLength));
        }

        return new BattlespireSaveVarsBlock<T>((int)declared, populated, records);
    }

    private static BattlespireSaveConversation ReadConversation(ReadOnlySpan<byte> r)
    {
        return new BattlespireSaveConversation(U32(r, 0), BattlespireSaveCharacter.ReadFixed(r, 4, 4));
    }

    private static BattlespireSaveStaticEnemy ReadStaticEnemy(ReadOnlySpan<byte> r)
    {
        var spells = new int[StaticEnemySpellCount];
        for (var i = 0; i < StaticEnemySpellCount; i++)
        {
            spells[i] = S32(r, 36 + i * 4);
        }

        return new BattlespireSaveStaticEnemy(
            U32(r, 0), S32(r, 4), S32(r, 8), S32(r, 12), S32(r, 16), S32(r, 20), S32(r, 24), S32(r, 28), U32(r, 32),
            spells);
    }

    private static BattlespireSaveHpSpModify ReadHpSpModify(ReadOnlySpan<byte> r)
    {
        return new BattlespireSaveHpSpModify(U32(r, 0), S32(r, 4), r[8] != 0);
    }

    private static BattlespireSaveSigil ReadSigil(ReadOnlySpan<byte> r)
    {
        return new BattlespireSaveSigil(U32(r, 0), S32(r, 4), U32(r, 8));
    }

    private static BattlespireSaveVariable ReadVariable(ReadOnlySpan<byte> r)
    {
        return new BattlespireSaveVariable(U32(r, 0), U32(r, 4));
    }

    /// <summary>Local variables fill from the last slot down; a slot is live when any byte is non-zero.</summary>
    private static List<BattlespireSaveLocalVariables> ReadLocalVariables(ReadOnlySpan<byte> file)
    {
        var result = new List<BattlespireSaveLocalVariables>();
        for (var slot = 0; slot < LocalVariableCapacity; slot++)
        {
            var r = file.Slice(LocalVariableOffset + slot * LocalVariableRecordLength, LocalVariableRecordLength);
            if (r.IndexOfAnyExcept((byte)0) < 0)
            {
                continue;
            }

            var variables = new List<BattlespireSaveVariable>();
            for (var i = 0; i < LocalVariableSlots; i++)
            {
                var hash = U32(r, 4 + i * 8);
                if (hash != 0)
                {
                    variables.Add(new BattlespireSaveVariable(hash, U32(r, 8 + i * 8)));
                }
            }

            result.Add(new BattlespireSaveLocalVariables(slot, U32(r, 0), variables));
        }

        return result;
    }

    private static List<string> SplitLog(ReadOnlySpan<byte> log)
    {
        var messages = new List<string>();
        var start = 0;
        while (start < log.Length)
        {
            var rest = log[start..];
            var end = rest.IndexOf((byte)0);
            var run = end < 0 ? rest : rest[..end];
            if (run.Length > 0)
            {
                messages.Add(Encoding.Latin1.GetString(run));
            }

            if (end < 0)
            {
                break;
            }

            start += end + 1;
        }

        return messages;
    }

    private static uint U32(ReadOnlySpan<byte> bytes, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    }

    private static int S32(ReadOnlySpan<byte> bytes, int offset)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
    }
}
