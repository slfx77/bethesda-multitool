# Bethesda Multitool - AI Assistant Instructions

## Project Overview

.NET 10.0 application for analyzing and converting Bethesda game data across The Elder Scrolls and Fallout titles (Morrowind through Starfield), on PC and console. Covers Xbox 360 memory dump analysis, file carving, and format conversion. Features WinUI 3 GUI (Windows), cross-platform CLI, and companion apps (BethesdaAudioTranscriber). Includes 8+ standalone tool projects (EsmAnalyzer, NifAnalyzer, PdbAnalyzer, RttiScanner, etc.).

## Critical Rules

### Tool Usage - NEVER use PowerShell for binary operations

- **NIF files**: Use `dotnet run --project tools/NifAnalyzer -f net10.0 -- <command> <file>`
- **ESM files**: Use main app CLI or EsmAnalyzer (see command reference below)
- **Never use** `2>&1` in PowerShell - breaks Spectre.Console ANSI output

## DO NOT RE-INVESTIGATE

These have been thoroughly investigated. Do not spend time re-researching them:

- **Split INFO records**: Xbox has MORE INFO records than PC (37,525 vs 23,247). Expected — the converter merges them.
- **Use MemDebug XEX, not ReleaseBeta PE.** The ReleaseBeta PE (`PowerPC:BE:32:default`, 85K functions) produces `halt_baddata()` stubs due to VMX instructions lacking pcode semantics, overlapping functions, wrong SLEIGH spec.
- **The MemDebug project has NO PDB symbols loaded** (all functions are `Function_XXXXXXXX`). Name-based lookup won't work. Use address-based lookup with cvdump-extracted addresses.
- **PPC thunk detection**: `mfspr r12, LR` = bytes `7D 80 42 A6` (NOT `7C 6C 02 A6`). Use Ghidra instruction API (`mfspr` + `bl` mnemonic check).
- **globals.txt module offsets** (`S_PROCREF: module, offset`) do NOT linearly map to VAs. Use `cvdump -s` output (`S_GPROC32: [section:offset]`) instead.

## Main App CLI Commands (btool)

```bash
# Run main app
dotnet run --project src/BethesdaMultitool -f net10.0 -- <command> <args>

# Format-agnostic commands (auto-detect file type: ESM, DMP, ESP)
search text <file-or-dir> <pattern>    # Text search in any binary file
search hex <file-or-dir> <hex-pattern> # Hex byte search (e.g., "6B F8 11 00")
stats <file>                           # Record type statistics (categorized table)
list <file> [-t TYPE] [-f FILTER]      # Browse reconstructed records
show <file> <formid-or-editorid>       # Inspect record detail (NPC, quest, etc.)
diff <fileA> <fileB> [-t TYPE] [-f ID] # Compare records between any two files
analyze <file>                         # Analyze memory dump structure
world <file>                           # World/terrain analysis

# ESM commands
esm <file>                      # Default ESM analysis
esm stats <file>                # Record type statistics
esm dump <file> <type>          # Dump records of type
esm trace <file> -o <offset>    # Trace structure at offset
esm convert <file>              # Convert Xbox 360 ESM to PC format
esm reports <input> -o <dir>    # Emit GECK-style CSV/TXT per record type
                                  #   (includes persistent_objects.csv, non_persistent_objects.csv
                                  #    with InstanceEditorID columns, plus per-type reports)
esm semdiff <f1> <f2>           # Semantic field-by-field diff
esm diff --xbox <f> --converted <f> --pc <f>  # Unified 2/3-way diff
esm cell objects <file> <cell>  # List placed objects in a cell
esm cell npc-trace <file> <id>  # Trace NPC from FormID to cell

# Archive commands (format-agnostic: Gamebryo .bsa, .ba2, and classic families, detected by
#   an ordered exact-arithmetic probe chain — several classic formats have weak or NO magic.
#   Redguard .ROB joins the chain: chunked "OARC"/"OARD" + an "END " terminator, segments tiling
#   the OARD body exactly; entries surface as NAME.3D and nothing in one is compressed.)
#   Fallout DAT1 (FO1 MASTER/CRITTER.DAT: BE, directory-first, 0x20 stored / 0x40 LZSS) and DAT2
#   (FO2 *.dat: LE, 8-byte tail footer where dataSize == file length, zlib entries) — neither has a
#   magic, so both probes are pure arithmetic and are extension-gated to .dat.
#   InstallShield 5 .CAB ("ISc(") — Redguard Disc 1's DATA1.CAB is the whole install (1,664 files,
#   481 MB) including the fxart/TEXBSI 3dfx art the Steam release omits.
#   CD images too: .iso (ISO9660), .cue + .bin (redump raw 2352). Data files list under their
#   ISO9660 paths; Redbook tracks list as audio/trackNN.wav and extract as 44.1 kHz stereo WAV.
#   Redguard Disc 1 = the install tree incl. the 3dfx fxart the Steam build omits; Disc 2 = the
#   .SMK movies + 7 audio tracks; the Battlespire disc = data + 8 audio tracks. Staged under
#   Sample/Full_Builds/{Redguard_Disc1,Redguard_Disc2,Battlespire_Disc}/ from the redump zips.
#   Plain PKZIP closes the chain (strong PK\x03\x04 magic + exact directory arithmetic): the TES
#   Travels J2ME JARs — a JAR *is* the install for those games — and Fallout Tactics .bos.
#   Dawnstar .lmp lumps: "-name-" + BE u32 offset + BE u16 length, the directory ending exactly
#   where the first payload begins and payloads tiling to EOF with no gaps.
#   Oblivion PSP GR.ARC: an OPTIONAL "A2.0" tag (the Jun 2006 beta has none), u32 count/dataStart/
#   nameTableOffset/nameTableSize, 16-byte records, 32-byte-aligned payloads, a NUL-terminated name
#   table ending exactly at EOF. ⚠ The untagged Jun 2006 revision stores payload offsets RELATIVE
#   to the data area; alignment is applied in RECORD space and the base added afterwards.
#   `archive` is canonical; `bsa` and `ba2` are deprecated aliases of the same group.
archive list <file>                 # List files in an archive
archive extract <file> -o <dir>     # Extract archive contents (-c/--convert: BSA Xbox->PC only)
archive info <file>                 # Archive statistics (BSA flags or BA2 header, format-aware)
archive find <file> <pattern>       # Find files matching pattern
archive inspect <file> <name>       # Inspect one file's metadata + leading bytes
archive rawdump <file> <off> <len>  # Raw hex dump at offset
archive file-compare <a> <f> <e>    # Compare an archived file vs an extracted copy
# BSA/Xbox-360-only (emit a graceful note when handed a BA2 — BA2 is PC-only, no writer):
archive convert <file>              # Convert Xbox 360 BSA -> PC BSA (extract, convert, repack)
archive validate <file>             # Validate BSA round-trip (extract -> repack -> compare)
archive compare <fileA> <fileB>     # Compare two BSA headers/folder hashes

# Sprite commands (classic-game 2D art -> PNG; the `render` sibling for palettized formats)
sprite render <file> -o <dir>              # Decode a loose image to PNG frames
sprite render <archive> -e <entry> -o <d>  # ...or an entry inside an archive
sprite info <file> [-e <entry>]            # Frame count/sizes/offsets without writing PNGs
#   -g/--game auto|arena|daggerfall|battlespire: Arena and Daggerfall SHARE .IMG/.CIF/.RCI with different
#     codecs. auto sniffs the palette beside the source (ART_PAL.COL = Daggerfall, else Arena).
#   Palette resolution: embedded > --palette <file> > the format's own routing (DF IMG names
#     FMAP_PAL/NIGHTSKY/DANKBMAP/MAP.PAL for special screens) > PAL.COL > ART_PAL.COL beside the source.
#   Battlespire .BSI: chunked images (BIG-endian chunk lengths) that carry their OWN palettes —
#     rendered through the 256-colour CMAP, since HICL fills only the 128 even slots and retail art
#     indexes the whole range. Multi-image files are labelled iNN_fMM.
#   Redguard fxart\TEXBSI.###: the 3dfx texture sets (Disc 1 only — extract DATA1.CAB first).
#     Images run to a nine-NUL terminator; each is a 9-byte name + LE u32 body + BE-length
#     subrecords BSIF/BHDR/DATA/"END " (a still, DATA == width*height) or IFHD/BHDR/CMAP/DATA/"END "
#     (animated: DATA opens with height*frameCount LE u32 ROW offsets, so frames share rows).
#     415 files, 5,602 images, 55 animated; only those 56 carry a palette (6-bit VGA CMAP).
#   Redguard .GXA: menus/HUD/location art. IFF chunks (BE lengths) BMHD/BPAL/BBMP + "END "; BMHD
#     carries a LE u16 frame count, BPAL is a 6-bit VGA palette, BBMP is N frames of an 18-byte
#     header (form, width, height) + raw 8-bit pixels. 60 of 65 retail files decode (320 frames);
#     the other 5 use a compressed frame form flagged at header +10 and are reported, not guessed.
#     Two files (GXICONS, gui) write BBMP's length as the ABSOLUTE offset of "END " — accepted exactly.
#   Fallout .FRM (and the .FR0-.FR5 direction siblings): BIG-endian; the header is 0x3E bytes,
#     NOT the 0x3A its last field starts at - u32 version, u16 fps, u16 actionFrame, u16 framesPerDir,
#     i16 xShift[6]@0x0A, i16 yShift[6]@0x16, u32 dirOffset[6]@0x22, u32 frameAreaSize@0x3A, frames
#     from 0x3E. Frame = u16 w, u16 h, u32 size (== w*h), i16 xOff, i16 yOff, then the indices.
#     Directions sharing artwork share an offset, so only distinct runs render (labelled dN_fMM).
#     Version is 4 on all 4,928 retail sprites but ART\INVEN\OKNIFE.FRM, which says 3 and is laid
#     out identically - do NOT dispatch on the version word.
#   ⚠⚠ Fallout's FULL-SCREEN SLIDES SHIP THEIR OWN PALETTE, named by stem beside the art:
#     DEATH.FRM takes DEATH.PAL, the ending takes SEQ*.PAL. 17 of MASTER.DAT's 18 per-image PALs
#     pair with a same-stem FRM in the same directory (the 18th, ART\CUTS\SUBTITLE.PAL, overlays
#     cutscene video). COLOR.PAL is only the DEFAULT, for the 4,911 FRMs that ship no palette.
#     Rendering a slide through COLOR.PAL does not just shift hues, it SPECKLES: DEATH.FRM's sky
#     dithers between indices 104/118/201/239/244, which DEATH.PAL maps to five near-identical
#     pale blues but COLOR.PAL maps to two blues and three cycling whites. Mean difference between
#     adjacent pixels: 12.6 correct vs 99.0 through COLOR.PAL. A palette file is recognised by its
#     SHAPE (33,536 B = 768 + a 32x32x32 lookup cube), not by the name COLOR.PAL.
#   Fallout palettes are 6-BIT: the 255s are SENTINELS, so a range sniff calls them 8-bit and
#     renders everything 4x too dark. COLOR.PAL has 28 (index 0 = transparent, 229-255 = runtime
#     colour-cycling); a slide palette has just ONE, index 0. Cycled entries stay sentinel white:
#     0.21% of pixel bytes across the FRMs that actually use COLOR.PAL, 61 of them (1.2%) visibly.
#     ⚠ Do NOT count indices >=229 in the slides toward that - there they are real colours.
#   Palette resolution for archive sources: the image's own <STEM>.PAL, then the format default,
#     each looked up inside the source archive, then beside it on disk, then in the install's
#     SIBLING archives - Fallout ships one COLOR.PAL, in MASTER.DAT, so a CRITTER.DAT sprite can
#     only ever resolve it from a sibling.
#   Today: Arena IMG/MNU/SET/CIF/DFA/CFA; Daggerfall TEXTURE.nnn, IMG (headered + 72 headerless
#     by SIZE table), CIF (plain + weapon animations), RCI. Multi-record output is labelled rNN[_fMM].
#   Stormhold .cus (--game stormhold|sh, auto-selected by extension): the mobile block's only custom
#     image format. BE int32 w, int32 h, u8 key flag, u16 key colour, u8 palette count, then a 0RGB
#     4:4:4 palette and one byte per pixel; 12 + 2N + w*h == file length on all 37 retail sprites.
#     ⚠ The transparent slot is the FIRST palette entry equal to the key colour and is NOT always
#     slot 0 (it is 4 in two files, 2 in one). ⚠ wardenheads4bit.cus is 8 bits per pixel like every
#     other .cus — the name describes its colour budget. Multi-pose art is one horizontal strip
#     whose frame count lives in the game's tables, not the file.
#   Raw 768-byte .PAL files are range-sniffed: all components <= 63 = 6-bit VGA (promote), else 8-bit.

# Classic (pre-Morrowind) commands
classic text <install-dir>                 # Dump authored text from a classic install
classic text <TEMPLATE.DAT|file.INF>       # ...or from one file (also TEXT.RSC / BOKnnnnn.TXT)
#   -s/--source template|inf|text|books|quests|all, -f/--filter <substr>, -l/--limit N
#   Arena: TEMPLATE.DAT strings + .INF on-screen text, riddles and door keys.
#   Daggerfall (pass DF\DAGGER or ARENA2): TEXT.RSC (1,408 records; 0xFF-separated variants
#   printed as "#id N variant(s)") + BOOKS (91 books, page by page; DOS code page 437 text —
#   BOK10000 is German with ü/ä/ö/ß) + quests (.QRC message text; the .QBN half is undecoded).
#   Redguard (pass the install root or ENGLISH.RTX): every line of dialogue/examine/menu text keyed
#   by the 4-char tag the scripts use (4,866 records, 3,933 voiced); -f matches tag or text.
video info <file> [-e <entry>]             # Geometry, frame count, fps, palette switches/blocks
video export <file> -o <dir> [--every N]   # Render frames to PNG (VID also writes its audio WAV)
#   Arena .FLC/.CEL (Autodesk FLIC, magic 0xAF12). A FLIC stores header+1 frame blocks — the
#   extra one loops back to frame 0 and is dropped, so decoded count == declared count.
#   Daggerfall .VID: 17 movies, 320x200 (DAG2.VID is 256x200), 4,661 frames total. Frames paint
#   onto ONE persistent canvas; a full frame's run carries a value byte, an incremental frame's
#   run instead SKIPS pixels. Interleaved audio is 11,025 Hz 8-bit unsigned mono and exports as
#   <name>.wav beside the PNGs.
audio decode <file> -o <dir>               # Decode classic audio to WAV
audio decode <archive> --all -o <dir>      #   ...or every supported file in an archive (-e for one)
audio info <file> [-e <entry>]             # Sample rate, depth, duration, loop/text metadata
#   Arena .VOC (8-bit PCM; rate = 1000000/(256-timeConstant)). ACM lands per game.
#   Arena .XMI/.XFM (XMIDI) -> standard MIDI: 33 of each in GLOBAL.BSA, the SAME tracks in
#     General-MIDI and FM-synth arrangements, so output stems keep the source extension.
#     FORM/XDIR + CAT/XMID + FORM/XMID with TIMB + EVNT. 3 traps: a delay is an ADDITIVE RUN of
#     bytes <0x80 (NOT a VLQ - two 0x7F means 254, not 16,383); NOTE ON carries a duration and
#     there are ZERO NOTE OFFs, so releases are scheduled by the converter; and DO NOT impose a
#     fixed tempo - 209 source tempo events span 57-240 BPM, so use division 60 and pass them
#     through. SUNNYDAY ends with a 9,398-byte delay run (~2h46m silence) that is VALID.
#   Daggerfall DAGGER.SND: -e takes a SOUND ID, --all writes every record (459 records, one
#   empty, id 220 used twice so those two files carry an index suffix). Headerless 11,025 Hz
#   8-bit unsigned mono; `audio info` with no -e prints the archive census.
#   Daggerfall MIDI.BSA: `audio info -e NAME.HMI` reads the HMI container (tag, track table).
#   The HMI event stream is NOT converted to a MIDI file — no spec for its extensions here.
#   Redguard ENGLISH.RTX: -e is a 4-CHAR TAG (e.g. zbza), --all writes every voiced line as
#   rtx_NNNN_tag.wav (tag punctuation replaced). Same 27-byte PCM header as MAIN.SFX per line.
#   Redguard sound/MAIN.SFX: -e is a 0-BASED INDEX (the bank stores no names), --all writes every
#   sound as sfx_NNN.wav. Same IFF house style as ROB/GXA (BE chunk lengths, "END " terminator):
#   FXHD = 32-char banner + LE u32 count (118), FXDT = records of a 27-byte header (rate at +8,
#   length at the UNALIGNED +22, depth flag at +4) + samples. Mono; depth is PER RECORD — 105 are
#   16-bit SIGNED (unlike every other DOS game here), 13 are 8-bit unsigned — at 11,025 or 22,050 Hz.
classic exe <A.EXE> [-o <out>] [--info]    # Unpack the PKLITE-compressed Arena executable
#   Retail A.EXE: 174,021 -> 304,624 bytes. Holds province/race names, item + city tables.
classic mesh info <archive|.3D|dir> [-e X] # Mesh archive summary, or one mesh's
                                           #   points/planes/triangles/textures/polygon census
#   Redguard .ROB: -e is a SEGMENT NAME (with or without the .3D the archive views append). A ROB
#   is one location's objects — 41 retail archives, 5,870 segments (1,203 empty placeholders),
#   4,667 uncompressed .3D meshes. Read with Redguard's 8-byte plane header.
classic mesh export <archive|.3D|dir> -e X -o <dir>  # One mesh -> GLB (Y flipped to glTF Y-up;
#   one primitive per texture). Daggerfall ARCH3D.BSA: -e is an OBJECT ID; textured when ARENA2's
#   TEXTURE.nnn + ART_PAL.COL sit beside the archive. Retail: 10,251 records (10,109 v2.7 / 134
#   v2.6 / 8 v2.5), 10 ids repeat.
#   Battlespire 3D.BSA / 3D.BS6 / loose .3D: -e is an ENTRY NAME (omit for a loose file). Those
#   meshes use a 10-BYTE plane header though they also say "v2.7" — the layout follows the game,
#   not the tag. Untextured for now (Battlespire textures live in the undecoded BSI.BSA).
#   Redguard .3DC (147 loose files): an ANIMATED mesh — same 64-byte header and plane list as a .3D,
#     but the geometry is a STACK OF POSES named by a frame table, so it is routed BEFORE the .3D
#     path. ⚠ A .3DC parses as a .3D with no error and returns the WRONG points: the header's
#     +48/+52/+24 offsets are frame 1's, not the mesh's. Header +20 gives the frame block (64 on
#     147/147) whose dword 0 is the table offset (88) and dword 2 the length of one unaccounted
#     region; the table holds nframes records of 3 dwords (110 files) or 4 (37) —
#     (vertexOffset, normalOffset, planeDataOffset[, unknown]). Frame 0 is an int32 KEYFRAME; later
#     frames are int32 poses (the same 37) or int16 deltas ADDED TO THE KEYFRAME (110, never
#     accumulated). Accepted on EXACT TILING, never on the header Radius — a 228-pose mesh matches
#     that by chance. Info prints the frame count and kind; export writes the keyframe, with normals
#     computed from the geometry (a .3DC stores none a .3D reader could use).
classic block info <BLOCKS.BSA|dir> [-e NAME]   # Daggerfall block archive census, or one block:
#   RMB = sub-blocks (building type/quality/position/rotation, exterior+interior object sets),
#   RDB = object lists (models/flats/lights/actions) + model references.
classic block export <BLOCKS.BSA|dir> -e NAME -o <dir> [--scale N]  # Diagnostic PNGs: RMB automap
#   (64x64 building codes) + ground grid (16x16 tile records; scenery dots); RDB top-down plan.
#   Retail: 920 RMB / 187 RDB / 187 RDI + "FOO" (a stray DOS dir listing). RDB width x height is
#   the number of object LISTS, not spatial cells — every object sits inside one 2,048-unit block.
classic level info <BS6.BSA|.BS6|dir> [-e NAME]  # Battlespire level census, or one level: its MESH
#   LIST (LFIL) plus the placements that index it (OBJD.IDFI + POSI/ANGS), lights, flats, bounds and
#   the authoring directory. Retail: 47 entries = 45 levels, 3,500 listed meshes, 7,428 placements
#   (7,426 resolving), 1,499 lights, 2,272 flats; ADR.TXT and the entry "C" are not levels.
classic level export <BS6.BSA|.BS6|dir> -e NAME -o <dir>  # Assemble one level into a single GLB,
#   each listed mesh placed at its POSI/ANGS (meshes resolve through 3D.BSA, 3D.BS6, then loose .3D).
#   POSI is in WORLD units (mesh native / 256), SECOND component vertical. ANGS is 2048-per-turn,
#   components = X/Y/Z, UNNEGATED, applied Y then X then Z. Component 0 is the pitch that stands
#   flat-authored geometry up (51% of flat-in-Y placements carry +/-90 deg, 72% of already-tall ones
#   carry none); component 1 is the yaw (dominant on upright meshes 1,286 vs 657); component 2 the
#   rare roll. Sign fixed by L8's 7volc ring: raw = 14/14 panels face inward, negated = 8/14.
#   Untextured — BSI.BSA's 15-bit palette tables are undecoded.
classic map info <file> [-e <entry>]       # Map dimensions, levels, .INF refs, locks/triggers
classic map export <file> -o <dir>         # Render voxel layers to PNG (--scale px/voxel)
#   Arena .MIF (LZHUF layers) and .RMD (word-RLE wilderness chunks); -e reads from GLOBAL.BSA.
#   Daggerfall WOODS.WLD (1000x500 heightmap -> greyscale PNG; 5x5 sub-grid per pixel read on
#   demand) and CLIMATE/POLITIC.PAK (1001x500 RLE overlays -> hue-per-value PNG).
#   Redguard maps\*.WLD (4 outdoor worlds, all exactly 263,432 B): 32-byte header + EIGHT layers of
#   128x256 bytes (row stride 128 by autocorrelation; the top ~192 rows carry the world) + zeros +
#   a 16-byte "TULO" trailer. Exported as <stem>_layerN.png; layers 1, 5 and 7 are smooth (height-like),
#   the rest categorical, layers 0-3 and 4-7 pair up by appearance — semantics NOT established.
#   Layer colours are DIAGNOSTIC (stable hue per voxel id, 0 = black), not the game's textures:
#   resolving real textures needs the level .INF plus tables still inside the packed A.EXE.
#   Fallout .MAP (72 in FO1's MASTER.DAT at version 19, 155 in FO2's at version 20 — the layout is
#     otherwise IDENTICAL between the games; BIG-endian): `classic map info` prints the header
#     and a per-elevation tile census; `classic map export` writes <stem>_elevN_{floor,roof}.png.
#     ⚠ Those colours are DIAGNOSTIC (hue per tile id, ids 0-1 = empty = black), NOT the game's art —
#     real terrain needs each id resolved through TILES.LST to a prototype and its ART\TILES frame,
#     composited on the isometric grid. A floor authored as two alternating tile variants therefore
#     reads as a checkerboard here; that is the palette, not a decode fault. Header is 236 B — 60 named + 44 UNUSED DWORDS — then 100x100
#     cells per present elevation, two u16 each (roof, floor), 40,000 B apiece. ⚠⚠ Those unused
#     dwords are ZEROS and zero passes a "valid tile id" range test, so scanning for the first
#     plausible grid finds 56 and is wrong by exactly 180 bytes; the grid then ends 180 B early
#     leaving 45 orphaned empty-tile pairs. ⚑ Elevation flags @+40: a SET bit means the elevation is
#     ABSENT, and the CLEAR bits say WHICH elevations the consecutive grids are — FO2 ships 2 maps
#     with flags 0x2, where elevation 0 is missing and the first stored grid is elevation 1, so
#     numbering grids by position mislabels them (FO1 uses only 0x0=3, 0x8=2, 0xC=1, which all
#     start at elevation 0, so the bug is invisible there). The header name
#     @+4 equals the file name on 72/72 — read it to its NUL TERMINATOR, not by trimming trailing
#     NULs: CAVES.MAP has authoring leftovers after the terminator and a trim yields "CAVES.MAP AP".
#     ⚠⚠ A fixed-width string field is NUL-PADDED or NUL-TERMINATED and you must measure WHICH —
#     the two want opposite code and both fail invisibly on a console. Redguard's MAIN.SFX banner is
#     the padded case (TrimEnd() does NOT strip NULs, so it needs TrimEnd('\0')); this is the
#     terminated case, where TrimEnd('\0') is exactly wrong. Both bugs were found on 2026-09-06. Tile ids index PROTO\TILES\TILES.LST 1-based; 70 of 72 maps
#     71 of 72 maps are fully in range; LAGUNRUN has 257 stray roof words (data, not decode). Grid ORIENTATION is not established. The script/object sections after the grids
#     are NOT decoded — their bytes are handed back, not dropped.
#   Daggerfall SKYnn.DAT (32 sets, 64 frames each, one palette PER FRAME) render via `sprite render`;
#   SKYPAL.DAT beside them is a palette pair, not a sky set.
# ⚑ A J2ME title IS its JAR: `stats <file>.jar` works on Stormhold/Dawnstar/Oblivion mobile because
#   ClassicGameLocator matches the install markers against the archive's ENTRY NAMES, and the
#   analyzer mounts a file source as an archive and a directory through the profile — so a JAR and
#   the directory it was unpacked into behave identically. Shadowkey's root is the Symbian
#   application directory (…\system\apps\6R51); an Oblivion PSP root is one extracted UMD tree.
#   Travels record synthesis (measured 2026-09-05): Stormhold 440 (S* — classes, races, skills,
#   items, spells, monsters, sprite families, drops, dungeons, NPC strings), Dawnstar 482 (D*, the
#   SAME table readers over its .lmp lumps plus helptext), Oblivion mobile 616 (OMAP levels, OATL
#   atlases, OMSC scripts, OMTX lang strings), Shadowkey 9,365 (SZON zones, SENT placements, STRG
#   triggers, SSUR surfaces, SMSH meshes, SSPR sprites) and Oblivion PSP 87 (OPAK pack entries,
#   keyed by a NAME hash so a cross-build diff over the seven dated betas compares like with like).
# stats/list/show also accept a classic INSTALL DIRECTORY (those games have no single plugin
#   file). Arena synthesizes ATPL (TEMPLATE.DAT strings), AINF (.INF level definitions),
#   ALOC (CITYDATA world-map locations) and APRV (provinces) — 1,186 records on a retail install.
#   Daggerfall (pass DF\DAGGER, the dir holding ARENA2) synthesizes DREG (62 regions), DLOC
#   (15,251 MAPS.BSA locations: type, map pixel, buildings, dungeon blocks), DTXT (1,408 TEXT.RSC
#   strings with their variants), DBOK (91 books with page text), DMSH (10,251 ARCH3D meshes:
#   version, points, planes, triangles, textures, size), DBLK (1,295 BLOCKS.BSA entries: RMB
#   building/object census, RDB object-list census), DQST (234 quests: .QRC messages plus the
#   .QBN size/header words), DSND (459 sounds), DMUS (131 HMI songs) and DVID (17 movies)
#   — 29,199 records.
#   Redguard (pass the install root) synthesizes RWLD (29 WORLD.INI worlds), RMAP (27 maps\*.RGM
#   databases: placement/static/light/flat/marker/rope/nav-graph counts, bounds, entries+exits,
#   ROB resolution) and ROBJ (1,664 script objects: label, script alias, instances, script bytes,
#   animation meshes, first placement + mesh) and RTXT (4,866 ENGLISH.RTX lines: text, voiced,
#   rate/depth/duration). Map identity is a NAME HASH (`ClassicNameHash`) —
#   WORLD.INI registers ISLAND three times and HIDEOUT never, so the world index cannot key a map.
#   Format spec: docs/research/redguard_rgm_format.md (every chunk tiles 27/27).
#   Battlespire (pass the install root or GAMEDATA) synthesizes BMSH (2,640 meshes: 3D.BSA minus
#   its 5 non-mesh strays, plus 245 loose .3D), BTXT (254 TXT.BSA entries) and BLVL (45 BS6 levels:
#   mesh list/placement/light/flat counts, bounds, most-placed meshes) — 2,939 records.
#   Fallout 1 AND 2 (pass the INSTALL root — assets are in the DATs, not loose) synthesize FPRO, one
#   per prototype: FO1 4,306 (242 items / 312 critters / 908 scenery / 1,176 walls / 1,622 tiles /
#   46 misc), FO2 7,650 (531/483/1,851/1,633/3,102/50). Each carries its name and description
#   resolved from the family's PRO_*.MSG, plus art
#   FID, light, flags, script, subtype and material. FormIDs need no hashing — domain 0x50 + the
#   family byte, index = the PID's own 24-bit index, unique by construction.
#   ⚠ The critter record is 412 B in FO1 and 416 (0x1A0) in FO2 — but TWO of FO2's own critters
#   are still 412, so the size is a SET, never a per-game constant. Every other size matches.
#   ⚠⚠ A prototype is addressed through its family's .LST, NEVER by file name: the PID's low 24 bits
#   are a 1-BASED LINE NUMBER there and that line names the file. `<pid>.PRO` is wrong for 1,151 of
#   the 4,306 (886 scenery alone). Resolution goes through the layered mount, so loose DATA\ shadows
#   CRITTER.DAT shadows MASTER.DAT exactly as the game resolves it.
#   ⚠ .MSG ids are NOT unique — 14 are duplicated with different text across the 27 game files, and
#   the LAST occurrence wins (PRO_SCEN 85400 "Sign" + 85401 twice settles it: only the second 85401
#   reads as Sign's description). Prototype name is text id N, description N+1.

# Render commands (output: PNG sprites)
render <path> -o <dir>                     # Render single NIF to PNG
render <dir> -o <dir>                      # Batch render all NIFs in directory
render <prefix> --bsa <bsa> -o <dir>       # Batch render NIFs from BSA by prefix
render npc <meshes-bsa> --esm <e> -o <dir>  # NPC head sprites (auto-detects texture BSAs)
                                            #   --dmp <dump>: runtime FaceGen + weapon state
                                            #   --dmp-equip: + worn armor from runtime biped slots
                                            #   (player = --npc 0x14; any loaded actor works)

# Export commands (output: GLB/glTF models)
export nif <path> -o <dir>                  # Export NIF model to GLB
export npc <meshes-bsa> --esm <e> -o <dir>  # Export NPC with FaceGen morphs + equipment
                                            #   (--dmp / --dmp-equip work here too)

# DMP commands
dmp to-esm <file> -o <out>      # Rebuild a loadable ESM plugin from a dump. Output is ESM-FLAGGED
                                #   (TES4 flags 0x00000001) — name it .esm, never .esp. The old
                                #   'to-esp' alias was removed 2026-08-30: it created an ESM anyway.
                                #   Planner-only since 2026-08-11: the legacy emission path and its
                                #   --planner-types opt-in are gone. Every decision is settled by
                                #   EsmPlanner before PlanWriter/PlanCellSectionBuilder emit a byte.
dmp modules <file>              # List loaded modules
dmp regions <file>              # List memory regions
dmp va2offset <file> <address>  # Convert VA to file offset
dmp hexdump <file> <address>    # Hex dump at address
dmp analyze <directory>          # Unified DMP analysis (persistent refs, map markers, runtime structs)
dmp buffers <file>              # Memory buffer analysis
dmp coverage <file>             # Runtime structure coverage analysis
dmp compare <f1> <f2>           # Cross-build HTML/JSON/CSV diff (weapons, NPCs, map markers, …)
dmp formtype-census <file>      # FormType distribution analysis
dmp game-time <file-or-dir>     # In-game date/time (game-clock GLOBs) per dump, sorted by build date
dmp rtti <file>                 # RTTI structure scanning

# Report validation commands (sanity-check generated reports)
report validate <input>                    # Check every field value against declared domains
report consistency <inputs...>             # Pairwise cross-build diff (loads ESM/DMP directly)
report consistency --from-html <compare>   #   …or reuse an existing dmp-compare HTML directory

# Dialogue commands
dialogue stats <file>           # Dialogue record statistics
dialogue tree <file>            # Display dialogue tree structure
dialogue verify <file>          # Verify dialogue structure
dialogue debug <file>           # Debug dialogue parsing
dialogue provenance <file>      # Track dialogue record origins
dialogue unattributed <file>    # Find dialogue without speaker attribution

# Save game commands
save decode <file>              # Decode Xbox 360 save game file
save report <file>              # Generate save game analysis report

# Version tracking commands
version-track inventory [--builds <dir>] [--dumps <dir>]  # Build timeline w/ PE-timestamp dates
                                                          #   (defaults: Sample/Full_360_Builds, Sample/MemoryDump)
version-track extract <file>    # Full snapshot of one ESM/DMP (slow: scans textures/geometry/runtime)
version-track report            # Cross-build comparison report
```

### EsmAnalyzer Commands (niche debugging)

```bash
# Run EsmAnalyzer (fast build, niche commands)
dotnet run --project tools/EsmAnalyzer -c Release -- <command> <args>

# Structure analysis
grups <file>                    # GRUP structure, nesting, duplicates
toft <file>                     # Xbox 360 TOFT streaming cache

# Land/export
land-summary <file>             # LAND subrecord summary
export-land <file>              # Export LAND as images/JSON
worldmap <file>                 # Generate worldspace heightmap

# Comparison
compare land <f1> <f2>          # Compare land records
compare cells <f1> <f2>         # Compare cell records
compare heightmaps <f1> <f2>    # Compare heightmap data

# Search/validate
search text <file> <pattern>    # ASCII text search
search hex <file> <offset>      # Hex dump at offset
search locate <file> <offset>   # Locate record at offset
validate structure <file>       # Validate record structure
validate deep <file>            # Deep record validation

# DMP (niche — scripts, rendering, module extraction)
dmp scripts list <file>         # List scripts in DMP
dmp scripts show <file> <id>    # Show script details
dmp scripts compare <file>      # Compare SCTX vs SCDA
dmp scripts crossrefs <file>    # Cross-reference chain diagnostics
dmp render-map <directory>      # Render map marker overlay PNGs
dmp extract-module <file>       # Extract game exe for Ghidra

# Other niche
gen-facegen <ctl-file>          # Generate C# code from si.ctl
worldmap-diag <file>            # World map category diagnostics
category-audit <file>           # Map category audit
orphan-refs <file>              # Find orphaned FormID references
```

### Semantic Diff (semdiff) - Primary debugging tool

```bash
# Compare specific FormID between converted and PC reference
dotnet run --project src/BethesdaMultitool -f net10.0 -- esm semdiff <converted.esm> <pc_reference.esm> -f 0x0017B37C

# Compare all records of a type
dotnet run --project src/BethesdaMultitool -f net10.0 -- esm semdiff <converted.esm> <pc_reference.esm> -t PROJ --limit 50

# Show all fields, not just differences
dotnet run --project src/BethesdaMultitool -f net10.0 -- esm semdiff <file1> <file2> -f 0x12345678 --all
```

## Tool Projects

| Tool | Purpose | Run command |
|---|---|---|
| EsmAnalyzer | Niche ESM/DMP debugging (~60 commands) | `dotnet run --project tools/EsmAnalyzer -c Release -- <cmd>` |
| NifAnalyzer | NIF structure analysis (blocks, geometry, skin, havok) | `dotnet run --project tools/NifAnalyzer -f net10.0 -- <cmd>` |
| PdbAnalyzer | PDB symbol extraction, struct layout generation | `dotnet run --project tools/PdbAnalyzer -- <cmd>` |
| RttiScanner | RTTI + operator new extraction from raw binaries | `dotnet run --project tools/RttiScanner -- <cmd>` |
| EgtAnalyzer | FaceGen EGT texture analysis | `dotnet run --project tools/EgtAnalyzer -- <cmd>` |
| TextureAnalyzer | DDX/DDS texture analysis and conversion | `dotnet run --project tools/TextureAnalyzer -- <cmd>` |
| SignatureScanner | File signature matching in memory dumps | `dotnet run --project tools/SignatureScanner -- <cmd>` |
| TerrainAnalyzer | Heightmap/terrain analysis | `dotnet run --project tools/TerrainAnalyzer -- <cmd>` |
| EsmSchemaGen | Generate per-game C# record schemas from xEdit wbDefinitions | `dotnet run --project tools/EsmSchemaGen -- <cmd>` |
| ShaderProbe | Extract/probe FNV shaderpackage.sdp for renderer parity | `dotnet run --project tools/ShaderProbe -- <cmd>` |

## Key Source Directories

```
src/BethesdaMultitool/
├── CLI/
│   ├── Commands/
│   │   ├── Analysis/       # search, stats, list, show, diff, compare, world, analyze
│   │   ├── Esm/            # esm convert/stats/dump/diff(5 variants)/semdiff/cell
│   │   ├── Bsa/            # bsa list/extract/convert/find/validate/debug
│   │   ├── Dmp/            # dmp modules/regions/buffers/coverage/compare/rtti/formtype-census
│   │   ├── Dialogue/       # dialogue stats/tree/verify/debug/provenance/unattributed
│   │   ├── Export/         # export nif/npc (GLB model export)
│   │   ├── Save/           # save decode/report
│   │   └── Version/        # version extract/inventory/report/track
│   ├── Rendering/          # Pipeline implementations for render/export commands
│   │   ├── Nif/            # NifExportPipeline.cs (NIF→PNG/GLB)
│   │   ├── Npc/            # NpcRenderPipeline.cs + NpcExportPipeline.cs (head+body+equipment)
│   │   └── Gltf/           # GLB validation
│   ├── Formatters/         # Semdiff formatting, diff resolution
│   ├── Show/               # Record display renderers (Actor, Item, Quest, Misc, Generic, Magic, WorldObject)
│   └── Shared/             # CLI helpers, progress bars, table builders
├── Core/
│   ├── Semantic/           # SemanticFileLoader (format-agnostic ESM/DMP/ESP loading)
│   ├── Formats/
│   │   ├── Esm/            # ESM parsing, conversion, runtime reading, export
│   │   │   ├── Conversion/ # Xbox→PC converter engine (see ESM Conversion section)
│   │   │   ├── Runtime/    # PDB-based DMP struct readers (Readers/Generic/ + 24 specialized)
│   │   │   ├── Export/     # CSV, GECK reports, cross-dump comparison
│   │   │   ├── Parsing/    # ESM record/subrecord parsing
│   │   │   ├── Analysis/   # Semantic analysis
│   │   │   ├── FaceGen/    # FaceGen coefficient handling
│   │   │   ├── Script/     # Script bytecode parsing
│   │   │   ├── Records/    # Per-record-type models (split from Models/)
│   │   │   ├── Subrecords/ # Per-subrecord parsers
│   │   │   ├── Presentation/ # Display/formatting helpers
│   │   │   ├── Enums/      # ESM enum definitions
│   │   │   └── Models/     # Shared record data models
│   │   ├── Nif/            # NIF mesh format
│   │   │   ├── Rendering/  # Rasterizer, FaceGen morpher, GPU sprites, NPC assembly
│   │   │   │   └── Npc/Composition/  # NPC + Creature render/export composition planners
│   │   │   ├── Skinning/   # Skin data/partition parsing + LBS/DQS
│   │   │   ├── Geometry/   # Packed geometry, topology
│   │   │   ├── Conversion/ # NIF endian conversion
│   │   │   └── Schema/     # NIF format schema (nif.xml)
│   │   ├── SaveGame/       # Changed form decoder (ACHR, ACRE, REFR, QUEST, etc.)
│   │   ├── Travels/        # TES Travels mobile: Stormhold/, Dawnstar/, OblivionMobile/,
│   │   │                   #   Shadowkey/, OblivionPsp/ (J2ME is BIG-endian, Shadowkey LITTLE)
│   │   ├── Zip/            # PkZipParser — plain PKZIP for the J2ME JARs and Tactics .bos
│   │   ├── Bsa/            # BSA archive parsing + extraction
│   │   └── ...             # Dds, Ddx, Bik, Lip, Xma, Xdbf, Png, Subtitles
│   ├── Minidump/           # DMP parser, RTTI reader, FormType census, module extraction
│   ├── RuntimeBuffer/      # Runtime string extraction, pointer analysis, ownership
│   ├── Pdb/                # PDB global resolver
│   ├── Carving/            # Memory carver (file signature extraction)
│   ├── Coverage/           # DMP structure coverage analysis
│   └── Utils/              # General utilities
├── App/                    # WinUI 3 GUI (net10.0-windows TFM only)
│   ├── Controls/           # XAML user controls (WorldMapControl, etc.)
│   ├── Tabs/               # GUI tabs (SingleFileTab, BatchModeTab)
│   ├── Helpers/            # EsmBrowserTreeBuilder, EsmPropertyFormatter
│   └── HexViewer/          # Virtual-scrolling hex editor
└── Repack/                 # Memory region repacking

src/BethesdaMultitool/Core/Formats/Esm/Conversion/
├── EsmConverter.cs                     # Main conversion orchestrator
├── EsmConverterConstants.cs            # Conversion constants
├── EsmEndianHelpers.cs                 # Endian swap utilities
├── EsmHelpers.cs                       # Compression, general utilities
├── Indexing/
│   ├── EsmConversionIndexBuilder.cs    # Pre-scan index for merging
│   └── EsmConversionStats.cs           # Conversion statistics
├── Processing/
│   ├── EsmGrupWriter.cs               # GRUP record writing
│   ├── EsmInfoMerger.cs               # Split INFO record merging
│   ├── EsmRecordWriter.cs             # Record writing
│   ├── EsmSubrecordConverter.cs        # Subrecord byte-swapping
│   └── EsmSubrecordConverter.Helpers.cs
└── Schema/
    ├── SubrecordSchemaRegistry.cs      # Field type definitions (+ partial files)
    ├── SubrecordSchema.cs              # Schema structures
    ├── SubrecordSchemaProcessor.cs     # Schema application logic
    └── SubrecordFieldType.cs           # Field type enum

tools/EsmAnalyzer/                      # Niche debugging commands (fast build)
├── Commands/                           # ~56 command files
└── GlobalUsings.cs                     # References main project namespaces
```

## Xbox 360 ESM Conversion

### Hybrid Endianness

Xbox 360 ESM uses mixed endianness:

- Record/subrecord headers: Big-endian
- Most data: Big-endian (FormIDs, floats, integers)
- Some fields: Already little-endian (e.g., INDX quest stage indices)

The `SubrecordSchemaRegistry` defines field types:

- `UInt16` / `UInt32` / `Float` - Big-endian, byte-swapped
- `UInt16LittleEndian` / `FormIdLittleEndian` - Preserved as-is

### Platform-Specific Subrecords

- **PNAM** in INFO records: Present on Xbox, stripped during conversion

### Known Content Differences (NOT conversion bugs)

Many records differ between Xbox and PC due to genuine content differences, not conversion issues:

- **LSCR LNAM order**: the Xbox master lists a load screen's LNAM (location) subrecords in the opposite order to PC (2 retail records affected; every other LSCR byte-identical after conversion) — an unordered list, genuine content, not a converter bug (measured 2026-09-02 three-way)
- **LVLO padding bytes**: Xbox has `FA 06`, PC has `15 06` - both are valid, semantically equivalent
- **AIDT unused bytes**: Xbox has zeros, PC has non-zero values - likely PC-only data
- **Various counts**: Xbox has more/fewer records in some categories (REFR +2369, etc.)

When debugging, focus on fields showing **DIFF** in semantic comparison, not just byte differences in padding.

## Standard File Paths

### Sample Directory Layout

```
Sample/
├── ESM/                    # Individual ESM files
│   ├── 360_final/          # Xbox 360 final ESM
│   ├── 360_proto/          # Xbox 360 prototype ESM
│   ├── fallout_3/          # Fallout 3 ESM
│   └── pc_final/           # PC final ESM
├── Full_Builds/            # Full game data (BSAs + ESMs + textures)
│   ├── Fallout New Vegas (360 Final)/Data/
│   ├── Fallout New Vegas (Aug 22, 2010)/Data/
│   ├── Fallout New Vegas (July 21, 2010)/Data/
│   └── Fallout New Vegas (PC Final)/Data/
├── Meshes/                 # Extracted Meshes BSAs
├── Textures/               # Extracted Texture BSAs
├── MemoryDump/             # Xbox 360 crash dumps
├── PDB/                    # Extracted PDB info from cvdump
├── Reference_Code/         # Source code from useful projects
├── Saves/                  # Save game files from 360 prototypes (for save decode testing)
├── TCRF/                   # Reference documentation for article writing
└── Unpacked_Builds/        # Full game data with BSAs extracted
    ├── 360_July_Unpacked/
    └── PC_Final_Unpacked/
```

### Full Game Builds (for rendering — needs BSAs + textures)

- **Xbox 360 final**: `Sample/Full_Builds/Fallout New Vegas (360 Final)/Data/`
- **Xbox 360 Aug 2010**: `Sample/Full_Builds/Fallout New Vegas (Aug 22, 2010)/Data/`
- **Xbox 360 July 2010**: `Sample/Full_Builds/Fallout New Vegas (July 21, 2010)/Data/`
- **PC final**: `Sample/Full_Builds/Fallout New Vegas (PC Final)/Data/`
- **PC install**: `E:\SteamLibrary\SteamApps\common\Fallout New Vegas\Data\`

### Classic Game Installs (pre-Morrowind catalog)

All under `E:\SteamLibrary\SteamApps\common\`. Data root is what `ClassicGameLocator`
detects; pass either it or the install root to `stats`/`archive`/`sprite`.

| Game | Install subdir | Data root | Notes |
|---|---|---|---|
| Arena | `The Elder Scrolls Arena` | `ARENA` | `GLOBAL.BSA` has NO magic — probed by exact directory tiling |
| Daggerfall | `Daggerfall` | `DF\DAGGER\ARENA2` | `DFCD` is a duplicate mirror — ignore it |
| Battlespire | `Battlespire` | `GAMEDATA` | `3D.BS6` is a BSA despite the extension |
| Redguard | `Redguard` | `Redguard` | Movies + music live only inside the CUE/BIN CD image |
| Fallout | `Fallout` | install root | Loose `DATA\` overrides the DATs; install is MODDED (Hi-Res patch) |
| Fallout 2 | `Fallout 2` | install root | Precedence: loose > `f2_res` > `patch*` > `critter` > `master`; MODDED (sfall + Killap UP) |
| Fallout Tactics | `Fallout Tactics` | `core` | 40 `.bos` archives, all plain PKZIP |

Because the two Fallout installs are modded, real-asset tests assert **structure only**,
never content counts. Daggerfall/Battlespire save dirs are empty until the user plays.

The five TES Travels titles are NOT Steam installs — they are staged under `Sample/Full_Builds/`
and resolve in tests through `RealAssetPaths.Travels.*`:

| Game | Fixture | Root | Notes |
|---|---|---|---|
| Stormhold | `test_stormhold_176x208_eng.jar` | the .jar itself | BIG-endian; 9 loose `.dat` tables, 37 `.cus`, 16 standard PNG |
| Dawnstar | `test_dawnstar_176x208_eng.jar` | the .jar itself | BIG-endian; tables in `datfiles.lmp`, 43 PNG in `imgfiles.lmp` |
| Shadowkey | `Shadowkey (N-Gage)/…` | `system\apps\6R51` | **LITTLE-endian**; 21 zones x 12 per-zone formats |
| Oblivion mobile | `oblivion-repaired.jar` | the .jar itself | BIG-endian; `.jtm`/`.cml`/`.scr` + `lang_N.txt` |
| Oblivion PSP | `Oblivion PSP (cancelled betas)/<build>` | `PSP_GAME\USRDIR` | cancelled; 7 dated UMD builds; one `GR.ARC` pack each |

⚠⚠ The endianness split runs through the middle of this block: the three J2ME titles are BIG-endian
(Java `DataInputStream`) and Shadowkey, on N-Gage/Symbian ARM, is LITTLE-endian. Getting it
backwards costs days; state it in every reader's doc comment.
⚠ Every reference for these five is unlicensed — CLEAN ROOM only. Decompiled output (javap on the
JARs, the 21 `.java` files inside `oblivion-repaired.jar`) is a BEHAVIOUR ORACLE, read the way this
repo reads Ghidra output; never transliterated.

### ESM Conversion Testing

- **Xbox 360 source**: `Sample/ESM/360_final/FalloutNV.esm`
- **Converted output**: `TestOutput/FalloutNV.pc.esm` (standard location, overwritten during testing)
- **PC reference**: `Sample/ESM/pc_final/FalloutNV.esm`

### Three-Way Diff (Primary Debugging Tool)

```bash
# Compare all three files for a record type (via main app)
dotnet run --project src/BethesdaMultitool -f net10.0 -- esm diff \
     --xbox "Sample/ESM/360_final/FalloutNV.esm" \
     --converted "TestOutput/FalloutNV.pc.esm" \
     --pc "Sample/ESM/pc_final/FalloutNV.esm" \
     -t ALCH --semantic -l 5

# Compare specific FormID across all three
dotnet run --project src/BethesdaMultitool -f net10.0 -- esm diff \
     --xbox ... --converted ... --pc ... -f 0x0017B37C --semantic
```

### Reference Materials

- **PDB symbols**: `Sample/PDB/`
- **MemDebug PDB**: `tools/GhidraProject/Fallout_Release_MemDebug.pdb` (100 MB, loaded into Ghidra)
- **Decompiled output**: `tools/GhidraProject/savegame_decompiled.txt` (save game functions)

## Ghidra Decompilation

### Setup

- **Ghidra**: `C:/Tools/ghidra_12.0.2_PUBLIC`
- **Project**: `tools/GhidraProject/XEX360Project/FalloutNV_MemDebug` (the MemDebug XEX)
- **Binary**: `Fallout_Release_MemDebug.xex`
- **Language**: `PowerPC:BE:64:Xenon` (VMX128 SLEIGH defs for Xbox 360 Xenon)
- **TEXT_BASE**: `0x82250000` (MemDebug .text section base)

### Running Decompilation

```bash
# Extract function addresses from MemDebug PDB
tools/microsoft-pdb/cvdump/cvdump.exe -s tools/GhidraProject/Fallout_Release_MemDebug.pdb | grep S_GPROC32

# Run Ghidra headless (from tools/GhidraProject/)
"C:/Tools/ghidra_12.0.2_PUBLIC/support/analyzeHeadless.bat" \
    XEX360Project FalloutNV_MemDebug \
    -process Fallout_Release_MemDebug.xex \
    -noanalysis \
    -postScript DecompileSaveTargets.java \
    -scriptPath .
```

### Adding New Decompilation Targets

1. Find the function in the PDB: `cvdump -s Fallout_Release_MemDebug.pdb | grep "FunctionName"`
2. Extract `[0004:XXXXXXXX], Cb: YYYYYYYY` → section 4 offset and size
3. Add to `TARGETS` array in the script: `{0xOFFSET, 0xSIZE, "Class::Method", tier}`
4. VA = `TEXT_BASE (0x82250000) + offset`
5. Exclude tiny stubs (Cb <= 0x10) — those are vtable redirectors

### Key Scripts

46 Java + ~190 Python scripts total. Most `run_decompile_*.py` are targeted single-function decompilations via PyGhidra.

| Script | Purpose | Status |
|---|---|---|
| `DecompileSaveTargets.java` | Save game functions | 58/58 GOOD |
| `DecompileSkeletonPipeline.java` | NIF loading, transforms, scene graph | 36/37 GOOD |
| `DecompileSkinningMemDebug.java` | Skinning/bone matrices | 20/20 GOOD |
| `DecompileWeaponAttachment.java` | BipedAnim weapon attachment | 8/11 GOOD |
| `DecompileAnimationPipeline.java` | Animation system | |
| `DecompileHairTint.java` | Hair tint rendering | |
| `run_decompile_facegen.py` | FaceGen morphing (PyGhidra) | 28/28 GOOD |
| `ExtractRttiStructSizes.java` | RTTI struct size extraction | |

## Build & CI

```bash
# Build EsmAnalyzer
dotnet build tools/EsmAnalyzer -c Release

# Run EsmAnalyzer
dotnet run --project tools/EsmAnalyzer -c Release -- <command> <args>

# Build main project (both CLI + Windows GUI TFMs, ~2:40)
dotnet build -c Release

# Fast build — CLI only, no analyzers (~25s)
dotnet build -c Release -p:BuildTestsOnly=true -p:SkipAnalyzers=true

# Run tests — fast (CLI-only build, no analyzers, ~1 min total)
dotnet test -p:BuildTestsOnly=true -p:SkipAnalyzers=true

# Run tests — full build (both TFMs, with analyzers)
dotnet test

# Run a subset (MTP runner args go after --; also works on the built exe directly)
dotnet test -p:BuildTestsOnly=true -p:SkipAnalyzers=true -- --filter-class Full.Class.Name
tests/BethesdaMultitool.Tests/bin/Release/net10.0/BethesdaMultitool.Tests.exe --filter-class Full.Class.Name

# TRX report (what CI uses)
dotnet test --project tests/BethesdaMultitool.Tests/BethesdaMultitool.Tests.csproj -c Release --no-build -- --report-xunit-trx --report-xunit-trx-filename test-results.trx --results-directory ./TestResults
```

### Build Flags

- `BuildTestsOnly=true` — Skips `net10.0-windows` TFM (WinUI 3 GUI). Saves ~2 min. Safe for test runs since the test project only targets `net10.0`. Also skips the standalone WinUI app projects (`BethesdaAudioTranscriber`, `BethesdaRendererProfiler`, `BethesdaMap2DProfiler`) — they depend on the dropped GUI TFM, so each no-ops its `Build` under this flag via `eng/SkipBuildInTestsOnly.targets` (wired in through a `BuildTestsOnly`-gated `CustomAfterMicrosoftCommonTargets`). Build them with a normal (no-flag) build.
- `SkipAnalyzers=true` — Disables SonarAnalyzer + Roslynator during build. Saves 5-15s. Use for fast iteration; omit for CI/lint passes.

### Test Discipline: Synthetic Data Only

Tests use synthetic byte fixtures by default. Tests that require real game data (retail masters, BSAs, dumps) are OPT-IN behind `RUN_BUCKET_B=1` via `BucketBTestGuard.SkipUnlessEnabled()` — never auto-enabled by detecting installed games. The default suite must stay fast (~1-2 min) everywhere. Two more opt-in gates follow the same pattern: `RUN_GPU_TESTS=1` (`GpuTestGuard`, tests that create D3D devices) and `RUN_SHADER_COMPILE_TESTS=1` (`ShaderCompileTestGuard`, tests that invoke the real D3D shader compiler).

Every real-asset test class additionally MUST carry `[Collection(SequentialIntegrationGroup.Name)]` and load masters via `RealAssetEsmCache.LoadAsync` (never dispose the returned result — the cache owns it). Without this, xUnit runs the multi-GB parses in parallel and the suite thrashes RAM until it never finishes. A full `RUN_BUCKET_B=1` sweep takes ~7 min and is for when the relevant code (semantic loader, schema decode, profiles) changes.

Classic pre-Morrowind installs resolve through `RealAssetPaths.Classics.*` (data *roots*, not
masters — those games have no plugin file). Resolve retail masters through `RealAssetPaths.Masters.*` — never a hand-rolled probe. `Masters.FalloutNv()` / `.Fallout3()` deliberately resolve the **installed** master and do NOT fall back to `Sample/ESM/…`: the two are different files (measured 2026-08-21: 266,840,039 vs 245,650,747 bytes, different md5), so substituting one produces field-level "failures" that have nothing to do with the code under test.

**A test method taking an `internal` type must itself be `internal`** (CS0051 otherwise — "inconsistent accessibility"). This bites `[Theory]` methods whose parameter is one of the internal format enums Track B keeps adding (three separate occurrences on 2026-09-06 alone: `FalloutProType`, `GpuTexturePayloadFormat`, `RwPspPixelFormat`). Mark the method `internal` rather than widening a production API or passing ints and casting — **xUnit v3 discovers and runs non-public test methods** (measured on 3.2.2, internal `[Theory]` included). The test *class* must still be public.

Each opt-in guard must be paired with its `[Trait("Category", TestCategories.X)]` — the guard does the skipping, the trait is what `--filter-trait` selects, and a class with the guard but no trait is silently omitted from targeted runs. `TestCategoryConsistencyTests` enforces the pairing; `TestCategories` also defines `Benchmark` (measurement, not a correctness gate) and `Tool` (generator that asserts nothing).

**A test must be able to fail.** Two patterns that look green but assert nothing, both previously present and now removed:
- Bailing out with `return;` when a fixture is missing. That is recorded as a **pass**. Use `Assert.SkipWhen` / `Assert.SkipUnless` so an unavailable fixture reports *skipped*.
- Asserting one production method equals another that delegates to it (e.g. `Assert.Equal(ReadUInt32BE(s), ReadUInt32(s, 0, true))` — the flag overload *calls* `ReadUInt32BE`). Pin an independently-known expected value instead; only then is an agreement check meaningful.

### Source-contract tests: last resort, not first

~76 test files assert on production **source text** (`Helpers/SourceContract.cs`) rather than behaviour. This exists only because `App/**` is `Compile Remove`d from the `net10.0` TFM (csproj line ~165) and ~30 `Core/` files sit behind `#if WINDOWS_GUI`, leaving that code unreachable from the test project. The cost is real: the 2026-08-20 ReSharper sweep produced 9 test failures, 8 of them source pins broken by pure style churn with zero behaviour change.

Before adding one, apply the rule the csproj already states (line ~162): **if the logic is platform-neutral, move it to `Core/` and test it for real.** `Core/Formats/Nif/NifHeaderFormat.cs` and `Core/Formats/Ddx/DdxHeaderFormat.cs` are the worked example — the NIF/DDX header classifiers used to be private copies in `App/Tabs/`, which forced the test file to keep its own duplicate of the logic and assert against itself. A source-contract test is acceptable only when the behaviour genuinely cannot run headless (D3D12 call ordering, HLSL text, XAML markup, decompile-derived constants).

### Code Coverage

Opt-in only, via `tools/scripts/coverage.ps1` (coverlet static IL instrumentation, scoped to `[BethesdaMultitool]*`; full default suite ≈ 2 min; baseline 2026-07-20: 64% line). Coverage never runs in the default test path. Do NOT use the Microsoft dynamic-instrumentation engine — VS coverage, `dotnet-coverage`, the MTP CodeCoverage extension, and VSTest `--collect:"XPlat Code Coverage"` all deadlock instrumenting `BethesdaMultitool.dll` (collector burns 200-300s CPU to ~8 GB with the test host frozen; reproduced 2026-07-19 on a single test class). The old `-p:CollectCoverage=false` flag is gone (it bound to nothing).

### CI/CD

CI: `.github/workflows/build-and-test.yml` — builds Release + runs tests (MTP runner args after `--`; TRX artifact, no coverage) on Windows, then cross-platform CLI build on Ubuntu.

## Accessibility

The WinUI 3 GUI in `src/BethesdaMultitool/App/` has a strict accessibility regression gate. `XamlAccessibilityRatchetTests` (under `tests/BethesdaMultitool.Tests/App/Accessibility/`) scans every `*.xaml` file and asserts every interactive control (Button, TextBox, ComboBox, ListView, TreeView, Slider, NumberBox, CheckBox, ToggleButton, DropDownButton, etc.) has an accessible name via one of:

- `AutomationProperties.Name` (literal or bound — `{x:Bind}` to a Name/Title/DisplayName/Label/Text-like property).
- `AutomationProperties.LabeledBy` (pointing to a sibling `TextBlock` with `x:Name`).
- `x:Uid` (localized Resources.resw entry).
- Content-bearing controls: literal `Content=` string, or a nested `<TextBlock Text="…" />` / `<TextBlock x:Uid="…" />`.

The test runs in strict mode — there is no baseline of known gaps. Any new control that lacks one of the above fails the test with the offending file + control type + `x:Name` + XAML line number.

When adding a new interactive control: either set `AutomationProperties.Name` inline, give the control an `x:Uid`, or link it to a visible label via `LabeledBy`. Icon-only buttons should have both a tooltip (visual) and `AutomationProperties.Name` (screen-reader). Headings (`<TextBlock Style="{StaticResource SubtitleTextBlockStyle}" />`) should also get `AutomationProperties.HeadingLevel="Level1"` (page) / `Level2` / `Level3`.

Keyboard shortcuts are declared via XAML `<KeyboardAccelerator>` — WinUI auto-decorates tooltips with the shortcut hint. Add new shortcuts to `KeyboardShortcutRegistry.All` (`src/BethesdaMultitool/Core/Ui/KeyboardShortcutRegistry.cs` — deliberately in Core/ so it is unit-testable; consumed by `App/Dialogs/KeyboardShortcutsDialog`) so the F1 cheat-sheet lists them.

## Code Style

- File-scoped namespaces: `namespace Foo;`
- Private fields: `_camelCase`
- Nullable reference types: Enabled
- Prefer braces for control flow
- Async methods: suffix with `Async`

## Key Dependencies

| Package | Purpose |
|---|---|
| `Vortice.Direct3D12` / `Vortice.DXGI` | GPU rendering (Direct3D 12, headless sprite generation + 3D viewer) |
| `SharpGLTF.Toolkit` | GLB/glTF model export |
| `Magick.NET-Q16-AnyCPU` | Image processing (textures, sprites) |
| `System.CommandLine` | CLI argument parsing |
| `Spectre.Console` | Rich terminal output (tables, progress bars) |
| `BCnEncoder.Net.ImageSharp` | DDS/BC texture compression (DDXConv) |
