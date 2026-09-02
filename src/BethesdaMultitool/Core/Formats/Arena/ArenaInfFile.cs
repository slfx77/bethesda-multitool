// Ported from OpenTESArena (MIT License), https://github.com/afritz1/OpenTESArena
//   OpenTESArena/src/Assets/INFFile.cpp / INFFile.h — the .INF decryption and the
//   @FLOORS/@WALLS/@FLATS/@SOUND/@TEXT line grammar. License texts are collected centrally
//   in THIRD_PARTY_LICENSES.
//
// Deliberate divergences from the reference, all verified against the retail data
// (93 .INF files in GLOBAL.BSA + 5 loose ones, 2026-09-01):
//   * The reference discards *DOOR, *TRANS, *TRANSWALKTHRU and *WALKTHRU because its renderer
//     takes those facts from voxel data instead. This is a data browser, so they are kept:
//     *DOOR alone accounts for 1,367 authored lines.
//   * The reference tracks engine-side index bookkeeping (box-cap arrays, a single resolved
//     ceiling texture index, chasm fallbacks). None of that is reproduced — each texture entry
//     simply carries the directives that were authored against it.

using System.Text;

namespace BethesdaMultitool.Core.Formats.Arena;

/// <summary>
///     A parsed Arena <c>.INF</c> — the per-level definition file that names an interior's floor,
///     wall and flat textures, its sound effects, and its on-screen lore text, riddles and door
///     keys. Plain text once decrypted, in five <c>@</c> sections whose lines are modified by
///     <c>*</c> directives that apply to the texture line following them.
///     <para>
///         Encryption is decided by RESIDENCY, not by content: the copies inside GLOBAL.BSA are
///         XOR-encrypted, loose copies in the data directory are not. Three of the five loose
///         files also differ in content from their archived namesakes, so loose-over-archive
///         precedence is a real content decision here, not a formality.
///     </para>
/// </summary>
internal sealed class ArenaInfFile
{
    private ArenaInfFile(
        string name,
        IReadOnlyList<ArenaInfVoxelTexture> floors,
        IReadOnlyList<ArenaInfVoxelTexture> walls,
        IReadOnlyList<ArenaInfFlat> flats,
        IReadOnlyList<ArenaInfSound> sounds,
        IReadOnlyList<ArenaInfText> texts,
        bool flatsNoShow)
    {
        Name = name;
        Floors = floors;
        Walls = walls;
        Flats = flats;
        Sounds = sounds;
        Texts = texts;
        FlatsNoShow = flatsNoShow;
    }

    /// <summary>Logical file name this document was parsed from (e.g. <c>AGTEMPL.INF</c>).</summary>
    public string Name { get; }

    /// <summary>@FLOORS entries, in file order.</summary>
    public IReadOnlyList<ArenaInfVoxelTexture> Floors { get; }

    /// <summary>@WALLS entries, in file order.</summary>
    public IReadOnlyList<ArenaInfVoxelTexture> Walls { get; }

    /// <summary>@FLATS entries (sprites placed in the level), in file order.</summary>
    public IReadOnlyList<ArenaInfFlat> Flats { get; }

    /// <summary>@SOUND entries: a .VOC name and the id the level data references it by.</summary>
    public IReadOnlyList<ArenaInfSound> Sounds { get; }

    /// <summary>@TEXT entries, ordered by id. Each may carry lore text, a riddle and/or a door key.</summary>
    public IReadOnlyList<ArenaInfText> Texts { get; }

    /// <summary>True when the section header was <c>@FLATS NOSHOW</c>.</summary>
    public bool FlatsNoShow { get; }

    /// <summary>The first @FLOORS ceiling definition, if the file declares one.</summary>
    public ArenaInfCeiling? Ceiling => Floors.Select(f => f.Ceiling).FirstOrDefault(c => c is not null);

    /// <summary>
    ///     The repeating XOR key. Each byte is combined with a counter that advances once per byte
    ///     and wraps every 256, so the effective keystream repeats only every 2,048 bytes.
    /// </summary>
    private static ReadOnlySpan<byte> EncryptionKey => [0xEA, 0x7B, 0x4E, 0xBD, 0x19, 0xC9, 0x38, 0x99];

    /// <summary>
    ///     Undoes the .INF cipher: <c>plain[i] = cipher[i] ^ (byte)(i + key[i % 8])</c>. It is an
    ///     involution, so the same routine encrypts.
    /// </summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> encrypted)
    {
        var key = EncryptionKey;
        var result = new byte[encrypted.Length];
        for (var i = 0; i < encrypted.Length; i++)
        {
            result[i] = (byte)(encrypted[i] ^ (byte)(i + key[i % key.Length]));
        }

        return result;
    }

    /// <summary>
    ///     Heuristic for a file of unknown provenance — an .INF the user extracted from GLOBAL.BSA
    ///     to disk, say, where residency no longer says whether it is enciphered. Decrypted .INF
    ///     content is entirely printable ASCII plus CR/LF/tab; ciphertext is not. Residency remains
    ///     authoritative wherever the caller knows it.
    /// </summary>
    public static bool IsProbablyEncrypted(ReadOnlySpan<byte> bytes)
    {
        var sampled = Math.Min(bytes.Length, 256);
        if (sampled == 0)
        {
            return false;
        }

        var printable = 0;
        for (var i = 0; i < sampled; i++)
        {
            var b = bytes[i];
            if (b is (>= 0x20 and <= 0x7E) or 0x09 or 0x0A or 0x0D)
            {
                printable++;
            }
        }

        return printable * 100 < sampled * 95;
    }

    /// <summary>Decrypts if needed, then parses. <paramref name="encrypted" /> is the residency answer.</summary>
    public static ArenaInfFile Parse(ReadOnlySpan<byte> bytes, string name, bool encrypted)
    {
        var plain = encrypted ? Decrypt(bytes) : bytes.ToArray();
        return ParseText(Encoding.Latin1.GetString(plain), name);
    }

    /// <summary>Parses already-decrypted .INF text.</summary>
    public static ArenaInfFile ParseText(string text, string name)
    {
        ArgumentNullException.ThrowIfNull(text);

        var floors = new List<ArenaInfVoxelTexture>();
        var walls = new List<ArenaInfVoxelTexture>();
        var flats = new List<ArenaInfFlat>();
        var sounds = new List<ArenaInfSound>();
        var textBuilders = new Dictionary<int, ArenaInfTextBuilder>();
        var flatsNoShow = false;

        var floorState = new FloorState();
        var wallState = new WallState();
        var flatState = new FlatState();
        var textState = new TextState();

        // DAGOTH1.INF and DAGOTH2.INF (the final staff-piece dungeon) open straight into *BOXCAP
        // with no @FLOORS header, so @FLOORS is the starting section rather than "none".
        var section = ArenaInfSection.Floors;

        foreach (var line in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            if (line.Length == 0)
            {
                // Blank lines separate groups, and clear any directive waiting for a texture line.
                // A riddle body may legitimately contain them, so those are kept verbatim.
                if (textState.Riddle is { Mode: RiddleMode.Riddle } riddle)
                {
                    riddle.Body.Append('\n');
                }
                else
                {
                    FlushAll();
                }

                continue;
            }

            if (line[0] == '@')
            {
                var sectionToken = FirstToken(line);
                var next = sectionToken.ToUpperInvariant() switch
                {
                    "@FLOORS" => ArenaInfSection.Floors,
                    "@WALLS" => ArenaInfSection.Walls,
                    "@FLATS" => ArenaInfSection.Flats,
                    "@SOUND" => ArenaInfSection.Sound,
                    "@TEXT" => ArenaInfSection.Text,
                    _ => throw new InvalidDataException(
                        $"Unrecognized .INF section '{sectionToken}' in '{name}'.")
                };

                if (next == ArenaInfSection.Flats &&
                    line.Contains("NOSHOW", StringComparison.OrdinalIgnoreCase))
                {
                    flatsNoShow = true;
                }

                FlushAll();
                section = next;
                continue;
            }

            switch (section)
            {
                case ArenaInfSection.Floors:
                    ParseFloorLine(line);
                    break;
                case ArenaInfSection.Walls:
                    ParseWallLine(line);
                    break;
                case ArenaInfSection.Flats:
                    ParseFlatLine(line);
                    break;
                case ArenaInfSection.Sound:
                    ParseSoundLine(line);
                    break;
                default:
                    ParseTextLine(line);
                    break;
            }
        }

        FlushAll();

        var texts = textBuilders.Values
            .Select(b => b.Build())
            .OrderBy(t => t.Id)
            .ToList();

        return new ArenaInfFile(name, floors, walls, flats, sounds, texts, flatsNoShow);

        void FlushAll()
        {
            floorState.Clear();
            wallState.Clear();
            flatState.Clear();
            FlushText();
            textState.Clear();
        }

        void ParseFloorLine(string line)
        {
            if (line[0] == '*')
            {
                var tokens = SplitWhitespace(line);
                var directive = tokens[0][1..].ToUpperInvariant();
                switch (directive)
                {
                    case "BOXCAP":
                        floorState.BoxCapId = ParseLeadingInt(tokens.ElementAtOrDefault(1));
                        break;
                    case "CEILING":
                        floorState.Ceiling = new ArenaInfCeiling(
                            ParseLeadingInt(tokens.ElementAtOrDefault(1)),
                            ParseLeadingInt(tokens.ElementAtOrDefault(2)),
                            tokens.Count > 3 && tokens[3] == "1");
                        break;
                    case "TOP":
                        // Only in LABRNTH{1,2}.INF; its meaning is undocumented. Recorded, not acted on.
                        floorState.Top = true;
                        break;
                    default:
                        throw new InvalidDataException(
                            $"Unrecognized @FLOORS directive '*{directive}' in '{name}'.");
                }

                return;
            }

            var (fileName, setSize) = SplitTextureLine(line);
            floors.Add(new ArenaInfVoxelTexture
            {
                FileName = fileName,
                SetSize = setSize,
                BoxCapIds = floorState.BoxCapId is { } cap ? [cap] : [],
                Ceiling = floorState.Ceiling,
                Flags = floorState.Top ? ArenaInfVoxelFlags.Top : ArenaInfVoxelFlags.None
            });
            floorState.Clear();
        }

        void ParseWallLine(string line)
        {
            if (line[0] == '*')
            {
                var tokens = SplitWhitespace(line);
                var directive = tokens[0][1..].ToUpperInvariant();
                var argument = ParseLeadingInt(tokens.ElementAtOrDefault(1));
                switch (directive)
                {
                    case "BOXCAP":
                        AddIfPresent(wallState.BoxCapIds, argument);
                        break;
                    case "BOXSIDE":
                        AddIfPresent(wallState.BoxSideIds, argument);
                        break;
                    case "DOOR":
                        AddIfPresent(wallState.DoorIds, argument);
                        break;
                    case "MENU":
                        wallState.MenuId = argument;
                        break;
                    case "DRYCHASM":
                        wallState.Flags |= ArenaInfVoxelFlags.DryChasm;
                        break;
                    case "WETCHASM":
                        wallState.Flags |= ArenaInfVoxelFlags.WetChasm;
                        break;
                    case "LAVACHASM":
                        wallState.Flags |= ArenaInfVoxelFlags.LavaChasm;
                        break;
                    case "LEVELUP":
                        wallState.Flags |= ArenaInfVoxelFlags.LevelUp;
                        break;
                    case "LEVELDOWN":
                        wallState.Flags |= ArenaInfVoxelFlags.LevelDown;
                        break;
                    case "TRANS":
                        wallState.Flags |= ArenaInfVoxelFlags.Transparent;
                        break;
                    case "TRANSWALKTHRU":
                        wallState.Flags |= ArenaInfVoxelFlags.TransparentWalkThrough;
                        break;
                    case "WALKTHRU":
                        wallState.Flags |= ArenaInfVoxelFlags.WalkThrough;
                        break;
                    default:
                        throw new InvalidDataException(
                            $"Unrecognized @WALLS directive '*{directive}' in '{name}'.");
                }

                return;
            }

            var (fileName, setSize) = SplitTextureLine(line);
            walls.Add(new ArenaInfVoxelTexture
            {
                FileName = fileName,
                SetSize = setSize,
                BoxCapIds = [.. wallState.BoxCapIds],
                BoxSideIds = [.. wallState.BoxSideIds],
                DoorIds = [.. wallState.DoorIds],
                MenuId = wallState.MenuId,
                Flags = wallState.Flags
            });
            wallState.Clear();
        }

        void ParseFlatLine(string line)
        {
            if (line[0] == '*')
            {
                var tokens = SplitWhitespace(line);
                var directive = tokens[0][1..].ToUpperInvariant();
                if (directive != "ITEM")
                {
                    throw new InvalidDataException(
                        $"Unrecognized @FLATS directive '*{directive}' in '{name}'.");
                }

                flatState.ItemId = ParseLeadingInt(tokens.ElementAtOrDefault(1));
                return;
            }

            // Modifiers are "F:1", "S:3", "Y:12" after the texture name, separated by tabs or
            // spaces. A line with no ':' is a bare name that may itself contain spaces
            // (*ITEM 55 in CRYSTAL3.INF), so it must not be split.
            var flat = new ArenaInfFlat { TextureName = string.Empty, ItemId = flatState.ItemId };
            if (!line.Contains(':', StringComparison.Ordinal))
            {
                flat = flat with { TextureName = NormalizeFlatName(line.Trim(), out var bare), LeadingDash = bare };
            }
            else
            {
                var tokens = SplitWhitespace(line);
                flat = flat with { TextureName = NormalizeFlatName(tokens[0], out var dash), LeadingDash = dash };
                for (var i = 1; i < tokens.Count; i++)
                {
                    var modifier = tokens[i];
                    var separator = modifier.IndexOf(':', StringComparison.Ordinal);
                    if (separator <= 0)
                    {
                        continue;
                    }

                    var value = ParseLeadingInt(modifier[(separator + 1)..]);
                    flat = char.ToUpperInvariant(modifier[0]) switch
                    {
                        'F' => flat with { Properties = value },
                        'S' => flat with { LightIntensity = value },
                        'Y' => flat with { YOffset = value },
                        _ => throw new InvalidDataException(
                            $"Unrecognized @FLATS modifier '{modifier}' in '{name}'.")
                    };
                }
            }

            flats.Add(flat);
            flatState.Clear();
        }

        void ParseSoundLine(string line)
        {
            var tokens = SplitWhitespace(line);
            if (tokens.Count < 2)
            {
                throw new InvalidDataException($"Malformed @SOUND line '{line}' in '{name}'.");
            }

            var id = ParseLeadingInt(tokens[1]);
            if (id is null)
            {
                throw new InvalidDataException($"Malformed @SOUND id in '{line}' ('{name}').");
            }

            sounds.Add(new ArenaInfSound(id.Value, tokens[0].ToUpperInvariant()));
        }

        void ParseTextLine(string line)
        {
            switch (line[0])
            {
                case '*':
                {
                    var tokens = SplitWhitespace(line);
                    var id = ParseLeadingInt(tokens.ElementAtOrDefault(1));
                    FlushText();
                    textState.Clear();
                    textState.Id = id ?? -1;
                    return;
                }

                case '+':
                {
                    textState.Mode = TextMode.Key;
                    textState.KeyId = ParseLeadingInt(line[1..]);
                    return;
                }

                case '^':
                {
                    var numbers = SplitWhitespace(line[1..]);
                    textState.Mode = TextMode.Riddle;
                    textState.Riddle = new RiddleState(
                        ParseLeadingInt(numbers.ElementAtOrDefault(0)) ?? 0,
                        ParseLeadingInt(numbers.ElementAtOrDefault(1)) ?? 0);
                    return;
                }

                case '~':
                {
                    textState.Mode = TextMode.Text;
                    textState.DisplayedOnce = true;
                    textState.Body.Append(line[1..]).Append('\n');
                    return;
                }
            }

            if (textState.Mode == TextMode.Riddle && textState.Riddle is { } riddle)
            {
                switch (line[0])
                {
                    case ':':
                        riddle.Answers.Add(line[1..]);
                        return;
                    case '`':
                        riddle.Mode = line[1..].Trim().ToUpperInvariant() switch
                        {
                            "CORRECT" => RiddleMode.Correct,
                            "WRONG" => RiddleMode.Wrong,
                            _ => riddle.Mode
                        };
                        return;
                }

                var target = riddle.Mode switch
                {
                    RiddleMode.Correct => riddle.Correct,
                    RiddleMode.Wrong => riddle.Wrong,
                    _ => riddle.Body
                };
                target.Append(line).Append('\n');
                return;
            }

            if (textState.Mode is TextMode.None or TextMode.Key)
            {
                // A key line may be followed by plain lore text (AGTEMPL.INF): bank the key,
                // then continue as text under the same *TEXT id.
                if (textState.Mode == TextMode.Key)
                {
                    Builder(textState.Id).SetKey(textState.KeyId);
                    textState.KeyId = null;
                }

                textState.Mode = TextMode.Text;
            }

            textState.Body.Append(line).Append('\n');
        }

        void FlushText()
        {
            switch (textState.Mode)
            {
                case TextMode.Key:
                    Builder(textState.Id).SetKey(textState.KeyId);
                    break;
                case TextMode.Riddle when textState.Riddle is { } riddle:
                    Builder(textState.Id).SetRiddle(riddle.Build());
                    break;
                case TextMode.Text:
                    Builder(textState.Id).SetText(textState.Body.ToString(), textState.DisplayedOnce);
                    break;
                default:
                    break;
            }
        }

        ArenaInfTextBuilder Builder(int id)
        {
            if (!textBuilders.TryGetValue(id, out var builder))
            {
                builder = new ArenaInfTextBuilder(id);
                textBuilders[id] = builder;
            }

            return builder;
        }
    }

    private static void AddIfPresent(List<int> target, int? value)
    {
        if (value is { } present)
        {
            target.Add(present);
        }
    }

    /// <summary>Strips the undocumented leading '-' some flat names carry, and upper-cases the rest.</summary>
    private static string NormalizeFlatName(string token, out bool leadingDash)
    {
        leadingDash = token.StartsWith('-');
        return (leadingDash ? token[1..] : token).ToUpperInvariant();
    }

    /// <summary>
    ///     A texture line is either a plain file name or <c>name.set #N</c>, where N is how many
    ///     64x64 tiles the .SET holds.
    /// </summary>
    private static (string FileName, int? SetSize) SplitTextureLine(string line)
    {
        var hash = line.IndexOf('#', StringComparison.Ordinal);
        return hash < 0
            ? (line.Trim(), null)
            : (line[..hash].Trim(), ParseLeadingInt(line[(hash + 1)..]));
    }

    private static string FirstToken(string line)
    {
        var tokens = SplitWhitespace(line);
        return tokens.Count > 0 ? tokens[0] : line;
    }

    private static List<string> SplitWhitespace(string line)
    {
        return [.. line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>
    ///     Reads a leading decimal integer the way the reference's <c>std::stoi</c> does — leading
    ///     whitespace and an optional sign, then digits, with any trailing characters ignored.
    ///     Returns null where stoi would have thrown.
    /// </summary>
    private static int? ParseLeadingInt(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var index = 0;
        while (index < token.Length && char.IsWhiteSpace(token[index]))
        {
            index++;
        }

        var negative = false;
        if (index < token.Length && token[index] is '-' or '+')
        {
            negative = token[index] == '-';
            index++;
        }

        var start = index;
        long value = 0;
        while (index < token.Length && char.IsAsciiDigit(token[index]))
        {
            value = (value * 10) + (token[index] - '0');
            if (value > int.MaxValue)
            {
                return null;
            }

            index++;
        }

        return index == start ? null : (int)(negative ? -value : value);
    }

    private enum ArenaInfSection
    {
        Floors,
        Walls,
        Flats,
        Sound,
        Text
    }

    private enum TextMode
    {
        None,
        Key,
        Riddle,
        Text
    }

    private enum RiddleMode
    {
        Riddle,
        Correct,
        Wrong
    }

    private sealed class FloorState
    {
        public int? BoxCapId { get; set; }

        public ArenaInfCeiling? Ceiling { get; set; }

        public bool Top { get; set; }

        public void Clear()
        {
            BoxCapId = null;
            Ceiling = null;
            Top = false;
        }
    }

    private sealed class WallState
    {
        public List<int> BoxCapIds { get; } = [];

        public List<int> BoxSideIds { get; } = [];

        public List<int> DoorIds { get; } = [];

        public int? MenuId { get; set; }

        public ArenaInfVoxelFlags Flags { get; set; }

        public void Clear()
        {
            BoxCapIds.Clear();
            BoxSideIds.Clear();
            DoorIds.Clear();
            MenuId = null;
            Flags = ArenaInfVoxelFlags.None;
        }
    }

    private sealed class FlatState
    {
        public int? ItemId { get; set; }

        public void Clear()
        {
            ItemId = null;
        }
    }

    private sealed class RiddleState
    {
        public RiddleState(int firstNumber, int secondNumber)
        {
            FirstNumber = firstNumber;
            SecondNumber = secondNumber;
        }

        public int FirstNumber { get; }

        public int SecondNumber { get; }

        public RiddleMode Mode { get; set; } = RiddleMode.Riddle;

        public StringBuilder Body { get; } = new();

        public StringBuilder Correct { get; } = new();

        public StringBuilder Wrong { get; } = new();

        public List<string> Answers { get; } = [];

        public ArenaInfRiddle Build()
        {
            return new ArenaInfRiddle(
                FirstNumber,
                SecondNumber,
                Body.ToString().TrimEnd('\n'),
                Answers,
                Correct.ToString().TrimEnd('\n'),
                Wrong.ToString().TrimEnd('\n'));
        }
    }

    private sealed class TextState
    {
        public int Id { get; set; } = -1;

        public TextMode Mode { get; set; } = TextMode.None;

        public int? KeyId { get; set; }

        public bool DisplayedOnce { get; set; }

        public RiddleState? Riddle { get; set; }

        public StringBuilder Body { get; } = new();

        public void Clear()
        {
            Id = -1;
            Mode = TextMode.None;
            KeyId = null;
            DisplayedOnce = false;
            Riddle = null;
            Body.Clear();
        }
    }

    /// <summary>
    ///     Accumulates the payloads authored under one *TEXT id. An id can carry both a door key
    ///     and lore text; each payload is first-wins, matching the reference's use of
    ///     <c>std::map::emplace</c>.
    /// </summary>
    private sealed class ArenaInfTextBuilder
    {
        private readonly int _id;
        private bool _displayedOnce;
        private ArenaInfRiddle? _riddle;
        private string? _text;
        private int? _keyId;

        public ArenaInfTextBuilder(int id)
        {
            _id = id;
        }

        public void SetKey(int? keyId)
        {
            _keyId ??= keyId;
        }

        public void SetRiddle(ArenaInfRiddle riddle)
        {
            _riddle ??= riddle;
        }

        public void SetText(string text, bool displayedOnce)
        {
            if (_text is not null)
            {
                return;
            }

            _text = text.TrimEnd('\n');
            _displayedOnce = displayedOnce;
        }

        public ArenaInfText Build()
        {
            return new ArenaInfText
            {
                Id = _id,
                Text = _text,
                DisplayedOnce = _displayedOnce,
                KeyId = _keyId,
                Riddle = _riddle
            };
        }
    }
}

/// <summary>Directive flags an .INF author attached to a floor or wall texture.</summary>
[Flags]
internal enum ArenaInfVoxelFlags
{
    None = 0,
    DryChasm = 1 << 0,
    WetChasm = 1 << 1,
    LavaChasm = 1 << 2,
    LevelUp = 1 << 3,
    LevelDown = 1 << 4,
    Transparent = 1 << 5,
    TransparentWalkThrough = 1 << 6,
    WalkThrough = 1 << 7,

    /// <summary>*TOP — occurs only in LABRNTH{1,2}.INF and has no documented meaning.</summary>
    Top = 1 << 8
}

/// <summary>
///     A <c>*CEILING</c> declaration: ceiling height, box scale, and whether the level is an
///     outdoor dungeon. Any of the three may be omitted by the author.
/// </summary>
internal sealed record ArenaInfCeiling(int? Height, int? BoxScale, bool OutdoorDungeon);

/// <summary>
///     One @FLOORS or @WALLS entry: a texture file plus every directive authored against it.
///     <see cref="SetSize" /> is non-null for a <c>.SET</c> line (<c>name.set #N</c>), meaning the
///     file holds N 64x64 tiles.
/// </summary>
internal sealed record ArenaInfVoxelTexture
{
    public required string FileName { get; init; }

    public int? SetSize { get; init; }

    public IReadOnlyList<int> BoxCapIds { get; init; } = [];

    public IReadOnlyList<int> BoxSideIds { get; init; } = [];

    /// <summary>*DOOR ids. The reference discards these; a data browser keeps them.</summary>
    public IReadOnlyList<int> DoorIds { get; init; } = [];

    /// <summary>*MENU id — an exterior/interior transition (shop and building entrances).</summary>
    public int? MenuId { get; init; }

    public ArenaInfCeiling? Ceiling { get; init; }

    public ArenaInfVoxelFlags Flags { get; init; }
}

/// <summary>
///     One @FLATS entry — a sprite placed in the level. <see cref="Properties" /> is the raw
///     <c>F:</c> bitfield; the named booleans decode it. Creature flats (item ids 32..54) ignore
///     these modifiers at runtime and take their values from the executable's creature tables.
/// </summary>
internal sealed record ArenaInfFlat
{
    public required string TextureName { get; init; }

    /// <summary>The <c>*ITEM</c> id this flat was declared under, if any.</summary>
    public int? ItemId { get; init; }

    /// <summary>Raw <c>F:</c> modifier value.</summary>
    public int? Properties { get; init; }

    /// <summary>Raw <c>S:</c> modifier — light range in voxels.</summary>
    public int? LightIntensity { get; init; }

    /// <summary>Raw <c>Y:</c> modifier — world Y offset (flying entities, hanging chains).</summary>
    public int? YOffset { get; init; }

    /// <summary>Whether the authored name carried the undocumented leading '-'.</summary>
    public bool LeadingDash { get; init; }

    public bool Collider => HasProperty(0);

    public bool Puddle => HasProperty(1);

    public bool LargeScale => HasProperty(2);

    public bool Dark => HasProperty(3);

    public bool Transparent => HasProperty(4);

    public bool Ceiling => HasProperty(5);

    public bool MediumScale => HasProperty(6);

    private bool HasProperty(int bit)
    {
        return Properties is { } value && (value & (1 << bit)) != 0;
    }
}

/// <summary>An @SOUND entry: the .VOC file and the id level data references it by.</summary>
internal readonly record struct ArenaInfSound(int Id, string FileName);

/// <summary>
///     A riddle guarding a passage: the question, the accepted answers, and the responses shown
///     for a correct and an incorrect reply. The two leading numbers are the author's parameters
///     (attempt count and a text id in the reference's reading).
/// </summary>
internal sealed record ArenaInfRiddle(
    int FirstNumber,
    int SecondNumber,
    string Riddle,
    IReadOnlyList<string> Answers,
    string Correct,
    string Wrong);

/// <summary>
///     One @TEXT id. An id carries lore text, a riddle, a door key — or, in a few files, both a
///     key and text.
/// </summary>
internal sealed record ArenaInfText
{
    public int Id { get; init; }

    /// <summary>Displayed lore text, if this id has any.</summary>
    public string? Text { get; init; }

    /// <summary>True when the text was authored with a leading '~' (shown only once).</summary>
    public bool DisplayedOnce { get; init; }

    /// <summary>Door-key id authored as <c>+N</c>.</summary>
    public int? KeyId { get; init; }

    public ArenaInfRiddle? Riddle { get; init; }
}
