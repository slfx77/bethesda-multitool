using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace BethesdaMultitool.Core.Formats.Tactics;

/// <summary>
///     The surface a <c>&lt;sprite&gt;</c> declares (its u16 <c>material</c>, object <c>+0x17</c>).
///     Read off the sprite editor's config importer <c>FUN_00444fb0</c>
///     (<c>C:\dev\bos\sprite\wsprite.cpp</c>), which compares the config's <c>material</c> string
///     against these names in this order and falls back with "Failed to parse material, using
///     metal_thick".
///     <para>
///         ⚠ NOT the tile material enum — see <see cref="TacticsTileMaterial" />. Retail uses only
///         two of these: <see cref="MetalThick" /> on 829 of the 918 sprites and
///         <see cref="Concrete" /> on the other 89.
///     </para>
/// </summary>
internal enum TacticsSpriteMaterial
{
    Concrete = 0,
    Brick = 1,
    MetalThin = 2,
    MetalThick = 3,
    Ceramics = 4,
    Grass = 5,
    PlantHard = 6,
    PlantSoft = 7,
    Dirt = 8,
    Gravel = 9,
    Rock = 10,
    Mud = 11,
    Swamp = 12,
    Water = 13,
    Rubber = 14,
    Ethereal = 15,
    Carpet = 16,
    Plastic = 17,
    Glass = 18
}

/// <summary>
///     The negative entries a <see cref="TacticsSpriteSequence" /> can carry.
///     <para>
///         ⚑ <b>ELEVEN of the twelve are PROVEN and not a guess.</b> The sprite editor's event
///         dialog <c>FUN_00449300</c> creates one button per event, pairing a caption with a
///         callback: "Left Footstep" with <c>FUN_0044a570</c>, "Right Footstep"
///         <c>FUN_0044a640</c>, "Sound start" <c>FUN_0044a710</c>, "Weapon Fire"
///         <c>FUN_0044a7e0</c>, "Weapon Release" <c>0x0044a8b0</c>, "Anim Repeat"
///         <c>FUN_0044ad90</c>, "Anim Goto" <c>0x0044ae60</c>, "Anim Delay" <c>0x0044b000</c>,
///         "Anim Rate" <c>0x0044b1a0</c>, "Start Overlay" <c>FUN_0044a3d0</c>, "Pickup"
///         <c>FUN_0044a4a0</c>, "Special Key" <c>0x0044ab30</c>. Each of the first ELEVEN pushes
///         exactly one constant into the append routine
///         <c>
///             FUN_0070b7c0(sequence, position,
///             value)
///         </c>
///         , read out of BOS.exe's bytes: -40, -41, -44, -42, -43, -4, -5, -2, -3, -6,
///         -45 in that order.
///     </para>
///     <para>
///         ⛔ <b><see cref="SpecialKey" /> = -1 is INFERRED, not read from the binary</b> —
///         corrected 2026-09-07, having been stated three times as if it were measured. The
///         "Special Key" callback at <c>0x0044ab30</c> (a <c>LAB_</c> in the dump, disassembled
///         from the retail exe) prompts "Type in Special Key List", splits the reply on ",",
///         and over the resulting array makes its ONE call to <c>0x0070b7c0</c> with the value
///         <c>0x007a31ba</c> parsed out of each element — an author-typed number, appended
///         verbatim. There is no negative literal anywhere between <c>0x0044ab30</c> and its
///         <c>ret</c> at <c>0x0044ad8d</c>; the only -1 in the function is the
///         <c>cmp ebx, -1</c> "nothing selected" sentinel <c>FUN_006cde70</c> returns. So that
///         button is an ESCAPE HATCH for typing raw entries, and -1 is a name this reader gives a
///         code the game never emits. No behaviour rests on it: 0 of the 7,043 retail sequences
///         contain -1, and <see cref="TacticsSpriteSequence.ParameterCount" /> gives it 0, which
///         is the player's default branch for any unknown negative anyway.
///     </para>
///     <para>
///         ⚑ The editor's sequence PLAYER <c>FUN_0044e9a0</c> confirms it independently through the
///         parameter counts it consumes: -43 advances the cursor by FOUR (the event plus a
///         three-component release point, which <c>FUN_0044f650</c> reads back), -5 sets the cursor
///         FROM the next entry (a jump — Anim Goto), -4 sets it to 0 (restart — Anim Repeat), -3
///         takes one entry into the playback field the player resets to 66 on wrap (a rate), -2
///         takes one entry the preview ignores (a delay), and every other code advances by one.
///     </para>
///     <para>
///         ⚑ The retail census then checks it from outside: <see cref="LeftFootstep" /> occurs 367
///         times and <see cref="RightFootstep" /> 365 — a balance no arbitrary assignment produces.
///         <see cref="SoundStart" /> is the commonest (2,706), <see cref="WeaponRelease" /> 686 and
///         always followed by at least three entries, <see cref="AnimRepeat" /> 545 and always last.
///     </para>
///     <para>
///         ⛔ Three earlier INFERENCES this refutes: -44 is <b>Sound start</b>, not "Weapon Fire"
///         (that is -42); -3 is <b>Anim Rate</b>, not "Anim Goto" (that is -5); -5 is
///         <b>
///             Anim
///             Goto
///         </b>
///         , not "Anim Delay" (that is -2).
///     </para>
///     <para>
///         ⚑ The parameter VALUES, measured over all 918 retail sprites, are on two different
///         scales, which is what a rate and a delay should look like: <see cref="AnimRate" /> has
///         111 events whose parameters run <b>10..122</b> (18 distinct values; the player's own
///         reset default is 66), while <see cref="AnimDelay" />'s two are 900 and 1200.
///         <see cref="AnimGoto" />'s 24 parameters run 2..25 — frame indices, as a jump target
///         should be. ⚠ Nothing states a UNIT for the first two; do not call them milliseconds.
///     </para>
///     <para>
///         ⚠ <see cref="SpecialKey" /> (-1) has a button but appends no constant of its own (see
///         above) and is used by NO retail sequence.
///     </para>
/// </summary>
internal enum TacticsSpriteEvent
{
    SpecialKey = -1,
    AnimDelay = -2,
    AnimRate = -3,
    AnimRepeat = -4,
    AnimGoto = -5,
    StartOverlay = -6,
    LeftFootstep = -40,
    RightFootstep = -41,
    WeaponFire = -42,
    WeaponRelease = -43,
    SoundStart = -44,
    Pickup = -45
}

/// <summary>
///     One named playback sequence over an animation's frame axis.
///     <para>
///         Reader <c>FUN_0070c920</c>, writer <c>FUN_0070ca80</c>: <c>u32 n</c>, then <c>i16[n]</c>
///         entries, then <c>f32[n]</c> values, then the name, then a <c>u16</c> animation index.
///     </para>
///     <para>
///         ⚠ An entry is SIGNED — <c>FUN_0070c0b0</c> returns <c>(int)(short)</c>. A non-negative
///         entry is a frame index on the animation's frame axis; a negative one is an event code.
///         Reading the array as <c>u16</c> turns every event into a number near 65,536.
///     </para>
/// </summary>
internal sealed record TacticsSpriteSequence(
    string Name,
    IReadOnlyList<short> Entries,
    IReadOnlyList<float> Values,
    int AnimationIndex)
{
    /// <summary>
    ///     How many entries follow this one as its parameters, from the editor's player
    ///     <c>FUN_0044e9a0</c>: <see cref="TacticsSpriteEvent.WeaponRelease" /> takes three (the
    ///     release point <c>FUN_0044f650</c> reads back), <see cref="TacticsSpriteEvent.AnimGoto" />,
    ///     <see cref="TacticsSpriteEvent.AnimRate" /> and <see cref="TacticsSpriteEvent.AnimDelay" />
    ///     one each, and every other entry — event or frame index — none.
    ///     <para>
    ///         ⚑ THE EVIDENCE IS THE DECOMPILED PLAYER, NOT A WALK. <c>FUN_0044e9a0</c> switches on
    ///         the entry: <c>0xffffffd5</c> (-43) advances the cursor by 4, <c>0xfffffffb</c> (-5)
    ///         takes the next cursor FROM the following entry, <c>0xfffffffd</c> (-3) consumes one
    ///         entry and then falls into the default +1, <c>0xfffffffe</c> (-2) advances by 2, and
    ///         every other code takes the default +1.
    ///     </para>
    ///     <para>
    ///         ⛔ Stepping by <c>1 + ParameterCount</c> to the exact end of the entry array is NOT a
    ///         falsifier for these counts and must never be quoted as one. Measured over the 7,043
    ///         retail sequences, <b>14 of the 16</b> tables scored land 7,043/7,043 — 13 of them
    ///         WRONG, including the ALL-ZERO table (no event takes a parameter), -43 given 0, 1, 2
    ///         or 4, and the tables that drop -5, -3 or -2 — because stepping by at least one
    ///         array trivially reaches its end unless an assumed parameter runs off the tail. Only
    ///         a count on a code that occurs LAST discriminates there (-4 given one parameter
    ///         scores 6,498/7,043, -6 given one scores 7,042/7,043).
    ///     </para>
    ///     <para>
    ///         ⚑ What DOES discriminate on the data is the frame axis: a non-negative entry is a
    ///         frame index and has to be less than its animation's <c>FrameCount</c>. Of the 64,316
    ///         entries this walk reads as frames, exactly <b>9</b> exceed it, over six sequences.
    ///         ⚠ NOT all of them are the authored 1-based off-by-one this doc used to blame for the
    ///         lot (corrected 2026-09-07): 5 of the 9 sit exactly one past the last frame and 4 sit
    ///         FURTHER past. Four of the six sequences overrun by exactly one entry and do read as
    ///         an off-by-one — <c>LeatherMale DeathFireOverlay</c> 30 over 30 frames,
    ///         <c>RaiderFemale StandClimbdown</c> counting 14..3 over 14,
    ///         <c>
    ///             JammingTower
    ///             DeathBrokenOverlay
    ///         </c>
    ///         12 over 12, <c>Calc Pillar ClosingIn</c> 23 over 23. The other
    ///         two do not: <c>Wolf DeathFireOverlay</c> runs 0..29 over a 26-frame animation (four
    ///         entries past the end) and <c>TribalMaleLarge CrouchAttackRifleBurst</c> carries a
    ///         lone 21 over an 11-frame animation. Those are unexplained authoring residue; the
    ///         walk's score is 9 either way, and the explanation covers only part of it.
    ///         Mis-read a parameter as a frame and that explodes: all-zero 294, -43
    ///         given 0 or 1 → 184, given 2 → 148, -44 given 1 → 188, -3 dropped → 117, -2 dropped
    ///         → 11. ⚠ It still cannot reach -5 (its parameter IS a frame index, so it is in range
    ///         by construction) nor -43 given FOUR; those two rest on the player alone.
    ///     </para>
    ///     <para>
    ///         The counts do correct the naive census: reading every negative entry as an event
    ///         over-reports <see cref="TacticsSpriteEvent.AnimDelay" /> as 5 when only 2 are events
    ///         and 3 are values inside a preceding <see cref="TacticsSpriteEvent.WeaponRelease" />
    ///         triple — the only 3 negatives among its 2,058 parameter values, so that census pin
    ///         is thin, not a second independent proof.
    ///     </para>
    /// </summary>
    public static int ParameterCount(short entry)
    {
        return (TacticsSpriteEvent)entry switch
        {
            TacticsSpriteEvent.WeaponRelease => 3,
            TacticsSpriteEvent.AnimGoto or TacticsSpriteEvent.AnimRate or TacticsSpriteEvent.AnimDelay => 1,
            _ => 0
        };
    }
}

/// <summary>
///     The screen extent of one (frame, direction) pair, as the animation header stores it:
///     <c>i32 x0, y0, x1, y1</c>. <c>FUN_00715e20</c> rejects the file with "Sprite with bad screen
///     extents" when <c>x1 &lt; x0</c> or <c>y1 &lt; y0</c>; 0 of the 4,611 retail animations do.
/// </summary>
internal readonly record struct TacticsSpriteRect(int X0, int Y0, int X1, int Y1)
{
    /// <summary>Width of the composited frame, i.e. <c>x1 - x0</c> — no inclusive +1.</summary>
    public int Width => X1 - X0;

    /// <summary>Height of the composited frame.</summary>
    public int Height => Y1 - Y0;
}

/// <summary>
///     One animation header — <c>&lt;spranim&gt;</c> version 1, reader <c>FUN_00715e20</c>, writer
///     <c>FUN_007161b0</c>.
/// </summary>
/// <param name="Name">The animation's name, e.g. "PA Stand Unarmed".</param>
/// <param name="ImageBlockOffset">
///     The ABSOLUTE file offset of this animation's <c>&lt;spranim_img&gt;</c> block (object
///     <c>+0x20c</c>). The writer patches it in a second pass from <c>tell()</c>; the lazy loader
///     <c>FUN_0044db10</c> seeks straight to it.
/// </param>
/// <param name="FrameCount">The A axis — frames. Zero is legal, and then no B and no rects follow.</param>
/// <param name="DirectionCount">
///     The B axis — directions. Settled by the in-game consumer <c>FUN_005e3450</c>, which reduces
///     the actor's direction byte MOD B and takes the heading as <c>dir * 2π / B</c>. B is 8 on
///     2,907 of the 4,611 retail animations; A never clusters at 8.
/// </param>
/// <param name="Rects">A x B screen extents, FRAME-MAJOR (frame outer, direction inner).</param>
internal sealed record TacticsSpriteAnimation(
    string Name,
    int ImageBlockOffset,
    int FrameCount,
    int DirectionCount,
    IReadOnlyList<TacticsSpriteRect> Rects)
{
    /// <summary>The extent of one (frame, direction) pair.</summary>
    public TacticsSpriteRect Rect(int frame, int direction)
    {
        return Rects[frame * DirectionCount + direction];
    }
}

/// <summary>
///     One layer image of one (frame, direction): the offset the editor draws it at and the
///     palette-less <c>&lt;zar&gt;</c> that holds its pixels.
/// </summary>
/// <param name="OffsetX">
///     ⚠ RELATIVE to the frame's rect origin, not absolute and not the origin itself: measured over
///     all 842,176 retail layer images the image lies inside the rect at (ox, oy) 842,176/842,176
///     times, equals the rect origin on 25 and is inside the rect in ABSOLUTE coordinates on 437.
///     The editor's frame view <c>FUN_0044f0d0</c> draws at <c>rect.x0 + ox</c>.
/// </param>
/// <param name="OffsetY">The vertical half of the same offset.</param>
/// <param name="Image">The layer's pixels; it carries no palette and indexes the block's layer palette.</param>
internal sealed record TacticsSpriteLayer(int OffsetX, int OffsetY, TacticsZarImage Image);

/// <summary>
///     A Fallout Tactics <c>.spr</c> — the character, creature and vehicle art, 918 files across the
///     20 <c>spr-*.bos</c> archives. Original RE 2026-09-07 from BOS.exe's own reader/writer pair;
///     every other Tactics reference is GPL, so nothing is ported.
///     <para>
///         ⚑ <b>Layout</b> (all little-endian; tag framing <c>'&lt;tag&gt;' NUL digits NUL</c>,
///         string = <c>u32 length + bytes</c>). Reader <c>FUN_0070af30</c>, writer
///         <c>FUN_0070acc0</c>, version must be 4:
///         <list type="number">
///             <item>
///                 <c>u8[3] bounding_box_size</c>, <c>i32 x, i32 y foot_position</c>,
///                 <c>u16 material</c>, <c>u8</c> literal 100 — the first three named by the sprite
///                 editor's config importer <c>FUN_00444fb0</c>, the last written as a literal
///                 (100 on 918/918; its meaning is NOT established).
///             </item>
///             <item><c>u32</c> sequence count, then that many <see cref="TacticsSpriteSequence" />.</item>
///             <item><c>u32</c> animation count, then that many <c>&lt;spranim&gt;</c> v1 headers.</item>
///             <item>
///                 one <c>&lt;spranim_img&gt;</c> block per animation, each at the ABSOLUTE offset
///                 its header states.
///             </item>
///         </list>
///     </para>
///     <para>
///         ⚠⚠ <b>An image block is raw OR zlib and retail ships both.</b> The version digit after
///         <c>&lt;spranim_img&gt;</c> is '1' (raw body) on 1,586 of the 4,611 retail blocks and '2'
///         (compressed) on 3,025 — 810 of the 918 FILES contain at least one raw block, so a
///         zlib-only reader rejects 88% of the corpus. <c>FUN_00716390</c> accepts 1..2;
///         <c>FUN_00716840</c> only ever writes 2.
///     </para>
///     <para>
///         ⚠⚠ <b>The zlib stream does not start after the version NUL</b> — a <c>u32</c> inflated
///         size precedes it (<c>FUN_00728e30</c> reads four bytes after <c>inflateInit_</c>, zlib
///         1.1.3). 0 of 108 sampled v2 files inflate without skipping it ("incorrect header check");
///         with it, the prefix equals the inflated length on 3,025/3,025. The COMPRESSED length is
///         never stored — the stream's own end is the only terminator, which is why blocks are
///         addressed absolutely rather than walked.
///     </para>
///     <para>
///         ⚠⚠
///         <b>
///             v1 and v2 bodies iterate the (frame, direction, layer) triple in DIFFERENT
///             orders
///         </b>
///         : v1 is frame outer, direction, layer inner; v2 is LAYER outer, direction,
///         frame inner (both branches of <c>FUN_00716390</c>). Reading a v2 body in v1 order
///         desynchronises after the first present flag.
///     </para>
///     <para>
///         ⚑ <b>The four palettes are four LAYERS, and the binary names them</b>:
///         <c>FUN_0070b3a0</c> maps "base" to 0, "skin" to 1, "hair" to 2, "tcol" to 3. Each layer
///         has its own palette AND its own sub-image (layer dimensions equal the base layer's on
///         only ~0.2% of frames), and the editor composites them in that order
///         (<c>FUN_0044f0d0</c>), tinting each palette through <c>FUN_007098d0</c>. That is what
///         the "4-palette team colouring" actually is.
///     </para>
///     <para>
///         ⚑ Retail: 918 files (4,390 B .. 33,000,947 B), 4,611 animations, 7,043 sequences,
///         842,176 embedded ZARs — every one v4 with no palette of its own, and every one tiling by
///         the row rule. Every image block begins exactly where the previous ends, the first at the
///         end of the last header and the last at EOF.
///     </para>
/// </summary>
internal sealed class TacticsSpriteFile
{
    /// <summary>The tag a sprite file opens with.</summary>
    public const string Tag = "sprite";

    /// <summary>The only <c>&lt;sprite&gt;</c> version <c>FUN_0070af30</c> accepts.</summary>
    public const int Version = 4;

    /// <summary>The tag of one animation header.</summary>
    public const string AnimationTag = "spranim";

    /// <summary>The only <c>&lt;spranim&gt;</c> version <c>FUN_00715e20</c> accepts.</summary>
    public const int AnimationVersion = 1;

    /// <summary>The tag of one animation's image block.</summary>
    public const string ImageBlockTag = "spranim_img";

    /// <summary>Layers per frame, and palettes per image block.</summary>
    public const int LayerCount = 4;

    /// <summary>The literal <c>FUN_00444fb0</c> writes at object <c>+0x19</c>; 100 on 918/918.</summary>
    public const byte HeaderLiteral = 100;

    /// <summary>
    ///     The one event code with a PROVEN meaning: the editor's "Anim Repeat" button
    ///     (<c>FUN_0044ad90</c>) appends <c>0xFFFC</c> to a sequence, and it is the last entry with
    ///     no parameters on 545 of 545 retail occurrences.
    /// </summary>
    public const short AnimRepeatEvent = -4;

    /// <summary>The game's own error text for a degenerate rect (<c>FUN_00715e20</c>).</summary>
    public const string BadExtentsMessage = "Sprite with bad screen extents";

    private const int MaximumSequences = 1 << 16;
    private const int MaximumAnimations = 1 << 16;
    private const int MaximumAxis = 1 << 12;

    /// <summary>The layer names <c>FUN_0070b3a0</c> maps, in index order.</summary>
    public static readonly IReadOnlyList<string> LayerNames = ["base", "skin", "hair", "tcol"];

    private readonly ReadOnlyMemory<byte> _bytes;
    private readonly string _name;

    private TacticsSpriteFile(
        ReadOnlyMemory<byte> bytes,
        string name,
        byte boundingBoxX,
        byte boundingBoxY,
        byte boundingBoxZ,
        int footPositionX,
        int footPositionY,
        TacticsSpriteMaterial material,
        byte literal,
        IReadOnlyList<TacticsSpriteSequence> sequences,
        IReadOnlyList<TacticsSpriteAnimation> animations,
        int headerLength)
    {
        _bytes = bytes;
        _name = name;
        BoundingBoxX = boundingBoxX;
        BoundingBoxY = boundingBoxY;
        BoundingBoxZ = boundingBoxZ;
        FootPositionX = footPositionX;
        FootPositionY = footPositionY;
        Material = material;
        HeaderLiteralValue = literal;
        Sequences = sequences;
        Animations = animations;
        HeaderLength = headerLength;
    }

    public byte BoundingBoxX { get; }

    public byte BoundingBoxY { get; }

    public byte BoundingBoxZ { get; }

    public int FootPositionX { get; }

    public int FootPositionY { get; }

    public TacticsSpriteMaterial Material { get; }

    /// <summary>The <c>u8</c> at object <c>+0x19</c>; 100 on every retail sprite, meaning unknown.</summary>
    public byte HeaderLiteralValue { get; }

    public IReadOnlyList<TacticsSpriteSequence> Sequences { get; }

    public IReadOnlyList<TacticsSpriteAnimation> Animations { get; }

    /// <summary>Bytes consumed by the header, sequences and animation headers — where the first image block sits.</summary>
    public int HeaderLength { get; }

    /// <summary>Content probe: the family framing with the <c>sprite</c> tag.</summary>
    public static bool IsSprite(ReadOnlySpan<byte> bytes)
    {
        return TacticsTagChunk.Is(bytes, Tag);
    }

    /// <summary>
    ///     Reads the header, sequences and animation headers. The image blocks are NOT read here —
    ///     they are addressed absolutely and one of them can be tens of megabytes, so
    ///     <see cref="ReadImageBlock" /> fetches them on demand, exactly as the game's own lazy
    ///     loader <c>FUN_0044db10</c> does.
    /// </summary>
    public static TacticsSpriteFile Parse(ReadOnlyMemory<byte> bytes, string name)
    {
        var cursor = new TacticsCursor(bytes, name);
        var start = cursor.Position;
        var chunk = cursor.Tag(Tag);
        if (!TryVersion(chunk.Version, out var version) || version != Version)
        {
            throw cursor.Fail(start, $"'<{Tag}>' version '{chunk.Version}' is not {Version}");
        }

        var boxX = cursor.U8();
        var boxY = cursor.U8();
        var boxZ = cursor.U8();
        var footX = cursor.I32();
        var footY = cursor.I32();
        var material = cursor.U16();
        var literal = cursor.U8();

        var sequenceCount = cursor.Count(MaximumSequences, "sequence");
        var sequences = new List<TacticsSpriteSequence>(sequenceCount);
        for (var i = 0; i < sequenceCount; i++)
        {
            var entryCount = cursor.Count(MaximumAxis * MaximumAxis, $"sequence {i} entry");
            var entries = new short[entryCount];
            var entryBytes = cursor.Bytes(entryCount * 2, $"sequence {i}'s entries").Span;
            for (var e = 0; e < entryCount; e++)
            {
                entries[e] = BinaryPrimitives.ReadInt16LittleEndian(entryBytes[(e * 2)..]);
            }

            var values = new float[entryCount];
            var valueBytes = cursor.Bytes(entryCount * 4, $"sequence {i}'s values").Span;
            for (var e = 0; e < entryCount; e++)
            {
                values[e] = BinaryPrimitives.ReadSingleLittleEndian(valueBytes[(e * 4)..]);
            }

            sequences.Add(new TacticsSpriteSequence(cursor.AsciiString(), entries, values, cursor.U16()));
        }

        var animationCount = cursor.Count(MaximumAnimations, "animation");
        var animations = new List<TacticsSpriteAnimation>(animationCount);
        for (var i = 0; i < animationCount; i++)
        {
            animations.Add(ReadAnimationHeader(cursor, i));
        }

        return new TacticsSpriteFile(
            bytes, name, boxX, boxY, boxZ, footX, footY,
            (TacticsSpriteMaterial)material, literal, sequences, animations, cursor.Position - start);
    }

    /// <summary>Reads a sprite, reporting why rather than throwing.</summary>
    public static bool TryParse(ReadOnlyMemory<byte> bytes, string name, out TacticsSpriteFile sprite, out string error)
    {
        try
        {
            sprite = Parse(bytes, name);
            error = string.Empty;
            return true;
        }
        catch (InvalidDataException e)
        {
            sprite = null!;
            error = e.Message;
            return false;
        }
    }

    /// <summary>
    ///     Reads one animation's <c>&lt;spranim_img&gt;</c> block from the offset its header states,
    ///     inflating it first when the block is version 2.
    /// </summary>
    public TacticsSpriteImageBlock ReadImageBlock(int animationIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(animationIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(animationIndex, Animations.Count);

        var animation = Animations[animationIndex];
        var offset = animation.ImageBlockOffset;
        if (offset < HeaderLength || offset >= _bytes.Length)
        {
            throw new InvalidDataException(
                $"{_name}: animation {animationIndex} '{animation.Name}' states an image block at {offset}, outside the {_bytes.Length}-byte file (headers end at {HeaderLength}).");
        }

        var cursor = new TacticsCursor(_bytes, _name, offset);
        var start = cursor.Position;
        var chunk = cursor.Tag(ImageBlockTag);
        if (!TryVersion(chunk.Version, out var version) || version is < 1 or > 2)
        {
            throw cursor.Fail(start, $"'<{ImageBlockTag}>' version '{chunk.Version}' is not 1 or 2");
        }

        if (version == 1)
        {
            var raw = ReadImageBody(cursor, animation, version, out var consumed);
            return new TacticsSpriteImageBlock(
                version, raw.Palettes, raw.Layers, offset, consumed, cursor.Position - start);
        }

        var inflatedSize = cursor.Count(int.MaxValue, "inflated size");
        var inflated = Inflate(cursor.Position, inflatedSize, animationIndex, animation.Name);
        var inner = new TacticsCursor(inflated, $"{_name} (animation {animationIndex}, inflated)");
        var body = ReadImageBody(inner, animation, version, out var inflatedConsumed);
        inner.RequireEnd($"<{ImageBlockTag}> v2 body");
        return new TacticsSpriteImageBlock(
            version, body.Palettes, body.Layers, offset, inflatedConsumed, null);
    }

    private static TacticsSpriteAnimation ReadAnimationHeader(TacticsCursor cursor, int index)
    {
        var start = cursor.Position;
        var chunk = cursor.Tag(AnimationTag);
        if (!TryVersion(chunk.Version, out var version) || version != AnimationVersion)
        {
            throw cursor.Fail(start,
                $"animation {index}: '<{AnimationTag}>' version '{chunk.Version}' is not {AnimationVersion}");
        }

        var imageBlockOffset = cursor.Count(int.MaxValue, $"animation {index}'s image block offset");
        var name = cursor.AsciiString();
        var frames = cursor.Count(MaximumAxis, $"animation {index}'s frame");
        var directions = 0;
        var rects = Array.Empty<TacticsSpriteRect>();
        if (frames != 0)
        {
            directions = cursor.Count(MaximumAxis, $"animation {index}'s direction");
            rects = new TacticsSpriteRect[frames * directions];
            var raw = cursor.Bytes(rects.Length * 16, $"animation {index}'s screen extents").Span;
            for (var r = 0; r < rects.Length; r++)
            {
                var at = raw[(r * 16)..];
                var rect = new TacticsSpriteRect(
                    BinaryPrimitives.ReadInt32LittleEndian(at),
                    BinaryPrimitives.ReadInt32LittleEndian(at[4..]),
                    BinaryPrimitives.ReadInt32LittleEndian(at[8..]),
                    BinaryPrimitives.ReadInt32LittleEndian(at[12..]));
                if (rect.Width < 0 || rect.Height < 0)
                {
                    throw cursor.Fail(start, $"{BadExtentsMessage}: animation {index} rect {r} is {rect}");
                }

                rects[r] = rect;
            }
        }

        return new TacticsSpriteAnimation(name, imageBlockOffset, frames, directions, rects);
    }

    private static (ReadOnlyMemory<byte>[] Palettes, TacticsSpriteLayer?[] Layers) ReadImageBody(
        TacticsCursor cursor, TacticsSpriteAnimation animation, int version, out int consumed)
    {
        var start = cursor.Position;
        var palettes = new ReadOnlyMemory<byte>[LayerCount];
        for (var k = 0; k < LayerCount; k++)
        {
            var entries = cursor.Count(TacticsZarImage.RetailPaletteEntries * 16, $"layer {k}'s palette");
            palettes[k] = cursor.Bytes(entries * 4, $"layer {k}'s palette");
        }

        var frames = animation.FrameCount;
        var directions = animation.DirectionCount;
        var layers = new TacticsSpriteLayer?[Math.Max(frames * directions * LayerCount, 0)];

        // FUN_00716390: the v1 branch iterates frame outer / direction / layer inner; the v2 branch
        // iterates LAYER outer / direction / frame inner. Same slots, opposite nesting.
        if (version == 1)
        {
            for (var a = 0; a < frames; a++)
            {
                for (var b = 0; b < directions; b++)
                {
                    for (var k = 0; k < LayerCount; k++)
                    {
                        layers[SlotIndex(a, b, k, directions)] = ReadSlot(cursor);
                    }
                }
            }
        }
        else
        {
            for (var k = 0; k < LayerCount; k++)
            {
                for (var b = 0; b < directions; b++)
                {
                    for (var a = 0; a < frames; a++)
                    {
                        layers[SlotIndex(a, b, k, directions)] = ReadSlot(cursor);
                    }
                }
            }
        }

        consumed = cursor.Position - start;
        return (palettes, layers);
    }

    private static int SlotIndex(int frame, int direction, int layer, int directions)
    {
        return (frame * directions + direction) * LayerCount + layer;
    }

    private static TacticsSpriteLayer? ReadSlot(TacticsCursor cursor)
    {
        var at = cursor.Position;
        var present = cursor.U8();
        if (present == 0)
        {
            return null;
        }

        if (present != 1)
        {
            throw cursor.Fail(at, $"layer present flag {present} is neither 0 nor 1");
        }

        var offsetX = cursor.I32();
        var offsetY = cursor.I32();
        return new TacticsSpriteLayer(offsetX, offsetY, TacticsZarImage.Read(cursor));
    }

    private static bool TryVersion(string text, out int version)
    {
        version = 0;
        return text.Length <= 4
               && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }

    private byte[] Inflate(int at, int inflatedSize, int animationIndex, string animationName)
    {
        var body = new byte[inflatedSize];
        try
        {
            using var source = SliceAsStream(at);
            using var zlib = new ZLibStream(source, CompressionMode.Decompress);
            zlib.ReadExactly(body, 0, inflatedSize);
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
        {
            throw new InvalidDataException(
                $"{_name}: animation {animationIndex} '{animationName}' declares {inflatedSize} inflated bytes at {at} but its zlib stream does not produce them ({e.Message}).",
                e);
        }

        return body;
    }

    private MemoryStream SliceAsStream(int at)
    {
        // A sprite is up to 33 MB and every animation would otherwise copy the tail, so use the
        // caller's array in place when the memory is array-backed (it always is off a file read).
        if (MemoryMarshal.TryGetArray(_bytes, out var segment) && segment.Array is not null)
        {
            return new MemoryStream(segment.Array, segment.Offset + at, segment.Count - at, false);
        }

        return new MemoryStream(_bytes[at..].ToArray(), false);
    }
}
