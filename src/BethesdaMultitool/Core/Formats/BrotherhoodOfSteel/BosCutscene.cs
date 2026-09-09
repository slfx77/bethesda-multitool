using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;

/// <summary>Which of the three command spaces a <see cref="BosCutsceneRecord" /> is addressed to.</summary>
internal enum BosCutsceneTargetKind
{
    /// <summary>Target <c>-99</c>: the scene itself (fades, tint, cut scripts, audio).</summary>
    Scene,

    /// <summary>Target <c>-100</c>: the cutscene camera.</summary>
    Camera,

    /// <summary>Any other target: the entity whose id it is, looked up in the object table.</summary>
    Actor
}

/// <summary>
///     Scene commands, from the switch at <c>default.xbe</c> <c>0x0006CB80</c>. ⚠ Not exhaustive —
///     an unlisted code is legal and <see cref="BosCutsceneRecord.CommandName" /> falls back to
///     <c>cmd&lt;n&gt;</c> rather than guessing.
/// </summary>
internal enum BosSceneCommand
{
    /// <summary>Tear the player down and return — the one command every shipped file carries exactly once.</summary>
    End = 11,

    /// <summary>
    ///     <c>FUN_0008F0B0(0x80, 0x80, 0x80, 0, 4)</c> — drive the screen tint to the neutral grey
    ///     that the skip path also restores when it hands control back to gameplay.
    /// </summary>
    FadeIn = 12,

    /// <summary><c>FUN_0008F0B0(0, 0, 0, 0, 4)</c> — drive the same tint to black.</summary>
    FadeOut = 13,

    /// <summary><c>FUN_0008F080(p0)</c> — lower a fade-block global to <c>min(current, 0x80 − p0)</c>.</summary>
    SetBrightnessLimit = 14,

    /// <summary>
    ///     Writes <c>p0 × 0.01</c> to <c>+0x3C</c> of the object whose <c>+0x0C</c> the same function
    ///     reads as the frame delta — the clock, so this is the time scale (retail uses 50 then 100).
    /// </summary>
    SetTimeScale = 15,

    /// <summary><c>FUN_00031570(p0 × 0.1, −1)</c> — an amplitude plus three fresh random per-axis offsets: camera shake.</summary>
    SetCameraShake = 16,

    /// <summary><c>FUN_000731A0(p0, p1, p2)</c> — writes the three bytes into two RGBA globals: a light/tint colour.</summary>
    SetLightColour = 17,

    /// <summary>
    ///     <c>FUN_00056A20((&amp;PTR_s_cutscript_A_000EFE18)[p0])</c> — runs <c>cutscript_A</c>… through the script
    ///     parser.
    /// </summary>
    RunCutScript = 18,

    /// <summary>The pre-pass at the top of <c>0x0006CB80</c>: stream the audio whose hash is <c>p0</c>.</summary>
    PrefetchAudio = 21
}

/// <summary>
///     Camera commands, from the same function: 6 and 8 are applied in the record switch, 7, 9 and
///     10 in the interpolation block at its end, which looks each one up with
///     <c>FUN_0006BEF0</c>/<c>FUN_0006BE40(−100, cmd, time, …)</c>.
///     <para>
///         ⛔ <b>CORRECTION 2026-09-08 — the orientation commands carry TWO components, not one.</b>
///         8 and 9 were named <c>SetYaw</c>/<c>KeyYaw</c> and documented as a single angle in
///         <c>p0</c>. The handler reads <c>p1</c> as well. At command 8 the game does
///         <c>FUN_00067B60((int) *(short *) &amp;p0);</c> and then
///         <c>*(u16 *)(DAT_001CC430 + 0xBA) = *(u16 *)(recordBase + 0x10);</c> — record + 0x10 is
///         <c>p1</c>. The command-9 block interpolates the two symmetrically: it forms a slope for
///         <c>*(short *)(rec + 3 dwords)</c> and one for <c>*(short *)(rec + 4 dwords)</c>,
///         evaluates both through <c>FUN_0006F300</c>, and sends the first to <c>FUN_00067B60</c>
///         and the second to that same <c>+0xBA</c> field. Both are the LOW SHORT of their dword.
///         ⚑ The disc agrees, with a control: over the <b>302</b> orientation records (48 of command
///         8, 254 of command 9) <b>300 carry a non-zero <c>p1</c></b> while
///         <b>
///             0 carry a non-zero
///             <c>p2</c>
///         </b>
///         — so <c>p1</c> is live authored data and <c>p2</c> really is the dead lane
///         the old naming implied it all was. In <c>res_1_door.cut</c> the SEVEN orientation
///         records (2 of command 8, 5 of command 9) are (p0, p1) = (−12918, 32294),
///         (−8939, 16136), (−8938, 16136) twice, (−7896, 2402) THREE times — at t = 5.34 (key),
///         5.59 (set) and 6.08 (key). ⛔ An earlier revision of this sentence said "six" and
///         "(−7896, 2402) twice"; the record dump has seven, and it was miscounted in two places.
///         ⚠ WHICH two angles they are — yaw and pitch, or some axis pair — is NOT established, so
///         they are named <see cref="SetOrientation" />/<see cref="KeyOrientation" /> and the
///         components stay raw.
///     </para>
/// </summary>
internal enum BosCameraCommand
{
    /// <summary><c>FUN_00067610</c> — set the camera transform outright (a hard cut).</summary>
    SetPosition = 6,

    /// <summary>A position KEY: the engine interpolates between the surrounding pair instead of snapping.</summary>
    KeyPosition = 7,

    /// <summary>
    ///     Set the camera orientation outright, from TWO components:
    ///     <c>FUN_00067B60((short) p0)</c> and <c>camera + 0xBA = (short) p1</c>.
    /// </summary>
    SetOrientation = 8,

    /// <summary>
    ///     An orientation KEY, interpolated like <see cref="KeyPosition" /> — and like
    ///     <see cref="SetOrientation" /> it carries both <c>p0</c> and <c>p1</c>, which the
    ///     interpolation block slopes and evaluates symmetrically.
    /// </summary>
    KeyOrientation = 9,

    /// <summary><c>FUN_00086960(p0 × 12)</c> — the far clip distance.</summary>
    SetFarClip = 10
}

/// <summary>
///     Actor commands. The full set is reached only for the scripted-actor class the switch gates on
///     (<c>0x2F82891D</c>); an ordinary entity understands a subset and ignores the rest.
/// </summary>
internal enum BosActorCommand
{
    /// <summary><c>FUN_0002AC00(&amp;position, 1)</c> — walk to <c>(p0, p1, p2)</c>.</summary>
    MoveWalk = 0,

    /// <summary><c>FUN_0002AB80(p0)</c> — play animation <c>p0</c>.</summary>
    PlayAnimation = 1,

    /// <summary><c>FUN_0002AC00(&amp;position, 2)</c> — the same move at the second gait.</summary>
    MoveRun = 2,

    /// <summary><c>FUN_0002AB80(0x30)</c> (generic path: animations 5 then 6) — die.</summary>
    Die = 3,

    /// <summary><c>FUN_0002AC80(FUN_00056C70(p0))</c> — target the entity whose id is <c>p0</c>.</summary>
    SetTarget = 4,

    /// <summary><c>FUN_0002AC40(&amp;position)</c> — teleport to <c>(p0, p1, p2)</c>.</summary>
    SetPosition = 5,

    /// <summary>Vtable <c>+0x50</c> against the player — the editor menu's "poke"/attack.</summary>
    Attack = 0x13,

    /// <summary>Wait for the streamed <c>.anm</c> and play it: <c>p0</c> is its asset hash.</summary>
    CutAnimation = 0x15,

    /// <summary><c>FUN_0006BFE0(p0, −1)</c> — arm effect <c>p0</c> on the actor.</summary>
    Effect = 0x16
}

/// <summary>
///     One 40-byte entry of a cutscene's object table.
/// </summary>
/// <param name="Index">Position in the table.</param>
/// <param name="RuntimePointer">
///     <c>+0</c>: a leftover heap address from the authoring session (values such as
///     <c>0x00DE5980</c>). Meaningless on disc; the loader overwrites it.
/// </param>
/// <param name="EntityId">
///     <c>+4</c>: the entity id the records address. ⚑ 636 of the 643 shipped actor records name an
///     id that is in this table.
/// </param>
/// <param name="StartPosition"><c>+8</c>: three floats — the editor's "set start pos".</param>
/// <param name="Unknown1"><c>+0x14</c>: undecoded.</param>
/// <param name="SecondPosition">
///     <c>+0x18</c>: three more floats. ⚠ NOT a duplicate of <paramref name="StartPosition" /> —
///     of the 263 objects in the 29 tiling files 225 are equal but <b>38 differ</b> (in
///     <c>Scene_Mov1A</c> 14, <c>Scene_Mov1B</c> 10, <c>Scene_Mov1E</c> 6, <c>Scene_Mov3K</c> 6,
///     <c>res1_piss</c> 2), and every one of those 38 is a distinct non-zero point rather than a
///     cleared field. So it carries information; what it means is undecoded.
/// </param>
/// <param name="Unknown2"><c>+0x24</c>: undecoded.</param>
internal readonly record struct BosCutsceneObject(
    int Index,
    uint RuntimePointer,
    int EntityId,
    Vector3 StartPosition,
    uint Unknown1,
    Vector3 SecondPosition,
    uint Unknown2);

/// <summary>One 24-byte keyframe record: a time, a target, a command and three integer parameters.</summary>
/// <param name="Index">Position in the record array.</param>
/// <param name="Time">Seconds from the start of the cutscene.</param>
/// <param name="Target"><c>-99</c> the scene, <c>-100</c> the camera, otherwise an entity id.</param>
/// <param name="Command">The raw command code — never narrowed to an enum, so an unnamed code survives.</param>
/// <param name="P0">First parameter (a coordinate, an animation number, an asset hash, …).</param>
/// <param name="P1">Second parameter.</param>
/// <param name="P2">Third parameter.</param>
internal readonly record struct BosCutsceneRecord(
    int Index,
    float Time,
    int Target,
    int Command,
    int P0,
    int P1,
    int P2)
{
    /// <summary>Which command space <see cref="Command" /> lives in.</summary>
    public BosCutsceneTargetKind Kind => Target switch
    {
        BosCutscene.SceneTarget => BosCutsceneTargetKind.Scene,
        BosCutscene.CameraTarget => BosCutsceneTargetKind.Camera,
        _ => BosCutsceneTargetKind.Actor
    };

    /// <summary>The command as a scene command, or null when the record is not addressed to the scene or the code is unnamed.</summary>
    public BosSceneCommand? Scene =>
        Kind == BosCutsceneTargetKind.Scene && Enum.IsDefined((BosSceneCommand)Command)
            ? (BosSceneCommand)Command
            : null;

    /// <summary>The command as a camera command, or null.</summary>
    public BosCameraCommand? Camera =>
        Kind == BosCutsceneTargetKind.Camera && Enum.IsDefined((BosCameraCommand)Command)
            ? (BosCameraCommand)Command
            : null;

    /// <summary>The command as an actor command, or null.</summary>
    public BosActorCommand? Actor =>
        Kind == BosCutsceneTargetKind.Actor && Enum.IsDefined((BosActorCommand)Command)
            ? (BosActorCommand)Command
            : null;

    /// <summary>
    ///     The command's name when the game's own code names it, else <c>cmd&lt;n&gt;</c>. ⚠ The
    ///     retail executable also carries the cutscene editor's menu labels at <c>0xEFC28</c>…, but
    ///     they are NOT a 1:1 map onto these codes (the code shows 17 taking an RGB triple and 18
    ///     running a script, where the menu order would make them "Green" and "Blue"), so no name
    ///     here comes from that table alone.
    /// </summary>
    public string CommandName => Kind switch
    {
        BosCutsceneTargetKind.Scene => Scene?.ToString() ?? Raw(Command),
        BosCutsceneTargetKind.Camera => Camera?.ToString() ?? Raw(Command),
        _ => Actor?.ToString() ?? Raw(Command)
    };

    /// <summary>Who the record addresses, for a dump: <c>scene</c>, <c>camera</c> or <c>actor 0x…</c>.</summary>
    public string TargetName => Kind switch
    {
        BosCutsceneTargetKind.Scene => "scene",
        BosCutsceneTargetKind.Camera => "camera",
        _ => string.Create(CultureInfo.InvariantCulture, $"actor 0x{(uint)Target:X8}")
    };

    /// <summary>One line of a human-readable dump.</summary>
    public string Describe()
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"t={Time,8:F2}  {TargetName,-16} {CommandName,-18} {P0}, {P1}, {P2}");
    }

    private static string Raw(int command)
    {
        return string.Create(CultureInfo.InvariantCulture, $"cmd{command}");
    }
}

/// <summary>
///     A <c>.CUT</c> cutscene script from Fallout: Brotherhood of Steel. Original RE 2026-09-07/08
///     against both shipped discs and the game's own player; no reference exists for this format.
///     <para>
///         ⚑⚑ <b>The layout, as the loader reads it</b> (<c>default.xbe</c> <c>0x0006CAB0</c>, which
///         does exactly <c>records = base + *(int*)(base + 8)</c>,
///         <c>
///             objects = base +
///             *(int*)(base + 0x10)
///         </c>
///         , <c>recordCount = *(int*)(base + 0x0C)</c>,
///         <c>
///             objectCount =
///             *(int*)(base + 0x14)
///         </c>
///         ): a 24-byte header — <c>+0</c> and <c>+4</c> undecoded, <c>+8</c>
///         the record offset, <c>+0x0C</c> the record count, <c>+0x10</c> the object-table offset
///         (24 on every shipped file), <c>+0x14</c> the object count — then <c>objectCount</c>
///         entries of 40 bytes, then <c>recordCount</c> records of 24. The update loop
///         <c>0x0006CB80</c> walks the records at a stride of <c>0x18</c> and its two seek helpers
///         (<c>0x0006BEF0</c> back, <c>0x0006BE40</c> forward) step the same array by six floats,
///         confirming both the stride and that the array is meant to be TIME-SORTED.
///     </para>
///     <para>
///         ⚑⚑ <b>THE GATE — the file must tile exactly</b>: <c>+0x10 == 24</c>, the object table ends
///         precisely where the records begin (<c>24 + 40 × objects == recordOffset</c>), and the
///         records end precisely at the end of the file (
///         <c>
///             recordOffset + 24 × records ==
///             length
///         </c>
///         ). Measured: <b>29 of the 31 loose <c>.CUT</c> files</b> on the Xbox disc tile
///         (<c>Scene_startTown</c> and <c>scene_townkill</c> declare <c>+8 == 32</c> and are an older
///         layout that appears in no clump). ⚑ The same 29 sit BYTE-IDENTICAL inside <c>sfx.clp</c>
///         on BOTH discs under <see cref="BosAssetHash" /> of <c>&lt;name&gt;.cut</c> — 29/29 on
///         Xbox, 29/29 on PS2. ⛔ The control is the rest of those clumps: of the 3,170 Xbox and
///         3,178 PS2 sections, exactly 29 each tile as a cutscene, so the gate is not passing
///         arbitrary bytes.
///     </para>
///     <para>
///         ⚠ <b>Not established.</b> Header <c>+0</c> takes arbitrary values (<c>0xFFF0FFFC</c>,
///         <c>0x00000035</c>; zero on 15 of the 29 files) and is genuinely never read — the loader
///         starts at <c>+4</c>. ⛔ <c>+4</c>, however, <b>IS read</b>, and this comment said
///         otherwise until 2026-09-08: <c>0x0006CAB0</c> does
///         <c>
///             in_ECX[0x15] = *(int *)(param_1 +
///             4)
///         </c>
///         , and the first block of the update <c>0x0006CB80</c> tests that same field —
///         <c>(DAT_001cca58 == 0 &amp;&amp; in_ECX[0x15] != 0 &amp;&amp; in_ECX[0x16] != 0)</c> —
///         to gate an early-out on <c>FUN_00080D90(0)</c> followed by
///         <c>FUN_0008F0B0(0x80,0x80,0x80,0,8)</c> and <c>FUN_000813F0(0)</c>. (That the two
///         functions share one object is not assumed: the update walks <c>in_ECX[0xE]</c> with the
///         count <c>in_ECX[0x12]</c>, the very fields the loader filled with the record array and
///         its count.) So <c>+4</c> is a per-cutscene flag — 0 on 12 files, 1 on 16, 2 on 1 — that
///         makes the scene WAIT: <c>FUN_00080D90</c> scans an eight-slot table for a bit-3 flag and
///         the update returns early until it reports ready. Which subsystem that table belongs to
///         is undecoded; what is settled is that the field is read, not that it is inert.
///         The object entries' 40-byte shape is decoded but their id space is
///         not: an id matches 2 of 105 <c>.DDF</c> and 2 of 105 <c>.SDB</c> keys, which is chance,
///         so they are presumably <c>.lmp</c> placement ids. 7 of 643 actor records name an id that
///         is NOT in their file's object table. 26 of the 29 files are time-sorted; three are not.
///     </para>
/// </summary>
internal sealed class BosCutscene
{
    /// <summary>Bytes of header.</summary>
    public const int HeaderLength = 24;

    /// <summary>Bytes per object-table entry.</summary>
    public const int ObjectLength = 40;

    /// <summary>Bytes per record — the <c>local += 0x18</c> of the update loop.</summary>
    public const int RecordLength = 24;

    /// <summary>The target value meaning "the scene itself".</summary>
    public const int SceneTarget = -99;

    /// <summary>The target value meaning "the cutscene camera".</summary>
    public const int CameraTarget = -100;

    private BosCutscene(
        string name,
        uint header0,
        uint header4,
        IReadOnlyList<BosCutsceneObject> objects,
        IReadOnlyList<BosCutsceneRecord> records)
    {
        Name = name;
        Header0 = header0;
        Header4 = header4;
        Objects = objects;
        Records = records;
    }

    /// <summary>Source name, for messages.</summary>
    public string Name { get; }

    /// <summary>Header <c>+0</c>, undecoded and unread by the loader.</summary>
    public uint Header0 { get; }

    /// <summary>
    ///     Header <c>+4</c>: read by the loader into the player's field <c>0x15</c> and tested in
    ///     the first block of the update, where non-zero makes the scene wait. 0 on 12 of the 29
    ///     shipped files, 1 on 16, 2 on 1; the meaning of the flag itself is undecoded.
    /// </summary>
    public uint Header4 { get; }

    /// <summary>The object table, in file order.</summary>
    public IReadOnlyList<BosCutsceneObject> Objects { get; }

    /// <summary>The keyframe records, in file order.</summary>
    public IReadOnlyList<BosCutsceneRecord> Records { get; }

    /// <summary>True when every record's time is at least the previous one's — what the seek helpers assume.</summary>
    public bool IsTimeSorted
    {
        get
        {
            for (var i = 1; i < Records.Count; i++)
            {
                if (Records[i].Time < Records[i - 1].Time)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Content probe: does this byte range tile as a cutscene?</summary>
    public static bool IsCutscene(ReadOnlySpan<byte> bytes)
    {
        return TryParse(bytes, "probe", out _, out _);
    }

    /// <summary>Parses a cutscene, throwing <see cref="InvalidDataException" /> when it does not tile.</summary>
    public static BosCutscene Parse(ReadOnlySpan<byte> bytes, string name)
    {
        if (!TryParse(bytes, name, out var cutscene, out var error))
        {
            throw new InvalidDataException(error);
        }

        return cutscene;
    }

    /// <summary>Parses a cutscene, reporting why rather than throwing.</summary>
    public static bool TryParse(ReadOnlySpan<byte> bytes, string name, out BosCutscene cutscene, out string error)
    {
        cutscene = null!;
        if (bytes.Length < HeaderLength)
        {
            error = $"{name}: {bytes.Length} bytes is shorter than the {HeaderLength}-byte header.";
            return false;
        }

        var header0 = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var header4 = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        var recordOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        var recordCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]);
        var objectOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]);
        var objectCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]);

        if (objectOffset != HeaderLength)
        {
            error =
                $"{name}: the object table starts at {objectOffset} rather than straight after the {HeaderLength}-byte header.";
            return false;
        }

        if (objectCount < 0 || recordCount <= 0)
        {
            error = $"{name}: {objectCount} objects / {recordCount} records are not a cutscene.";
            return false;
        }

        // ⚑ THE TILING GATE: the two arrays must meet exactly and end exactly at the file end.
        if (HeaderLength + (long)objectCount * ObjectLength != recordOffset)
        {
            error = $"{name}: {objectCount} objects from {HeaderLength} end at "
                    + $"{HeaderLength + (long)objectCount * ObjectLength} rather than at the records ({recordOffset}).";
            return false;
        }

        if (recordOffset + (long)recordCount * RecordLength != bytes.Length)
        {
            error = $"{name}: {recordCount} records from {recordOffset} end at "
                    + $"{recordOffset + (long)recordCount * RecordLength} rather than at the {bytes.Length}-byte end of the file.";
            return false;
        }

        var objects = new BosCutsceneObject[objectCount];
        for (var i = 0; i < objectCount; i++)
        {
            var at = HeaderLength + i * ObjectLength;
            objects[i] = new BosCutsceneObject(
                i,
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]),
                BinaryPrimitives.ReadInt32LittleEndian(bytes[(at + 4)..]),
                ReadVector3(bytes, at + 8),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 20)..]),
                ReadVector3(bytes, at + 24),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 36)..]));
        }

        var records = new BosCutsceneRecord[recordCount];
        for (var i = 0; i < recordCount; i++)
        {
            var at = recordOffset + i * RecordLength;
            records[i] = new BosCutsceneRecord(
                i,
                BinaryPrimitives.ReadSingleLittleEndian(bytes[at..]),
                BinaryPrimitives.ReadInt32LittleEndian(bytes[(at + 4)..]),
                BinaryPrimitives.ReadInt32LittleEndian(bytes[(at + 8)..]),
                BinaryPrimitives.ReadInt32LittleEndian(bytes[(at + 12)..]),
                BinaryPrimitives.ReadInt32LittleEndian(bytes[(at + 16)..]),
                BinaryPrimitives.ReadInt32LittleEndian(bytes[(at + 20)..]));
        }

        cutscene = new BosCutscene(name, header0, header4, objects, records);
        error = string.Empty;
        return true;
    }

    /// <summary>Renders the whole script as text — the object table, then every record in file order.</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{Name}: {Objects.Count} objects, {Records.Count} records");
        text.Append(CultureInfo.InvariantCulture, $" (header +0 0x{Header0:X8}, +4 {Header4})").AppendLine();

        foreach (var entry in Objects)
        {
            text.Append(CultureInfo.InvariantCulture, $"  object {entry.Index,-3} id 0x{(uint)entry.EntityId:X8}")
                .Append(CultureInfo.InvariantCulture,
                    $" start ({entry.StartPosition.X:F1}, {entry.StartPosition.Y:F1}, {entry.StartPosition.Z:F1})")
                .AppendLine();
        }

        foreach (var record in Records)
        {
            text.Append("  ").AppendLine(record.Describe());
        }

        return text.ToString();
    }

    private static Vector3 ReadVector3(ReadOnlySpan<byte> bytes, int at)
    {
        return new Vector3(
            BinaryPrimitives.ReadSingleLittleEndian(bytes[at..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[(at + 4)..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[(at + 8)..]));
    }
}
