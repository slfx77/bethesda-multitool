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
#   ⚠⚠ DAT1 LZSS: a RAW block's length is the LOW 15 BITS of the header word, NOT the negated word.
#     Fixed 2026-09-06. The negated reading (which the ported falltergeist reference's prose and our
#     own synthetic tests both asserted) turns an 11-byte tail into a 32,757-byte block. Proven by a
#     full-archive A/B with the shipped extractor: negated = 19,578 extracted + 206 FAILED;
#     low-15-bit = 19,784 extracted, 0 failed. ⚠ 14,271 of 14,997 compressed entries contain no raw
#     block at all, so the bad path was almost never exercised — and a control drawn from arbitrary
#     entries scores 400/400 for BOTH readings. Only the 726 entries that DO contain a raw block
#     discriminate (726/726 vs 520/726).
#   ⚠ `archive find` defaults to -l 50 and used to print the TRUNCATED count as the total ("Found
#     50" for 1,950 matches). Fixed to report the true count and warn; still never use it for a
#     census without -l.
#   (FO2 *.dat: LE, 8-byte tail footer where dataSize == file length, zlib entries) — neither has a
#   magic, so both probes are pure arithmetic and are extension-gated to .dat.
#   Steam retail disc .sim + .sid (`Core/Formats/Steam/`) — pass the **.sim**, never a .sid: only the
#     manifest says which file owns which bytes. Magic 0x3FD04C1F; the record table is 32 B x N
#     ENDING EXACTLY AT EOF and N is DERIVED (`(len - (stringTableSize + 24)) / 32`), never stored.
#     ⚠⚠ String offsets are relative to **+16**, not the file start — an absolute read yields
#     plausible but WRONG names. Payload = AES-256: key is `<16-byte LEGACY depot key> || A8194D02
#     193CD03792937D27590AECBD`, the IV is the block's first 16 bytes **ECB-decrypted** with that
#     key, body is CBC. ⚠⚠ The 16-byte LEGACY key, NOT config.vdf's 32-byte Steam3 key (different
#     generations; 0 of 43 shared depots matched). Block = u24 size (INCLUDES the 16-B IV), u8
#     padSize (17..32 = IV + 1..16 pad), u24 chunkSize, u8 **flags** (bit0 encrypted, bit1
#     compressed — NOT a "method" enum). ⚠⚠ A compressed block is a PK local file header + **RAW
#     DEFLATE**, so a zlib-magic check scores 0 on a CORRECT decrypt; expect `50 4B 03 04`.
#     Listing needs NO key; extraction reads `depot_keys.txt` beside the .sim (never checked in).
#     New Vegas retail DVD: 431/431 files exact.
#   InstallShield 5 .CAB ("ISc(") — Redguard Disc 1's DATA1.CAB is the whole install (1,664 files,
#   481 MB) including the fxart/TEXBSI 3dfx art the Steam release omits.
#   CD images too: .iso (ISO9660), .cue + .bin (redump raw 2352). Data files list under their
#   ISO9660 paths; Redbook tracks list as audio/trackNN.wav and extract as 44.1 kHz stereo WAV.
#   Redguard Disc 1 = the install tree incl. the 3dfx fxart the Steam build omits; Disc 2 = the
#   .SMK movies + 7 audio tracks; the Battlespire disc = data + 8 audio tracks. Staged under
#   the Redguard and Battlespire disc builds under Sample/Builds/ (Redguard's two discs share
#   one build, as `Disc 1 (Install)` and `Disc 2 (Play)`).
#   Plain PKZIP closes the chain (strong PK\x03\x04 magic + exact directory arithmetic): the TES
#   Travels J2ME JARs — a JAR *is* the install for those games — and Fallout Tactics .bos.
#   Dawnstar .lmp lumps: "-name-" + BE u32 offset + BE u16 length, the directory ending exactly
#   where the first payload begins and payloads tiling to EOF with no gaps.
#   Oblivion PSP GR.ARC: an OPTIONAL "A2.0" tag (the Jun 2006 beta has none), u32 count/dataStart/
#   nameTableOffset/nameTableSize, 16-byte records, 32-byte-aligned payloads, a NUL-terminated name
#   table ending exactly at EOF. ⚠ The untagged Jun 2006 revision stores payload offsets RELATIVE
#   to the data area; alignment is applied in RECORD space and the base added afterwards.
#   Arena v1.04 FLOPPY INSTALLER ("Bethesda Softworks Install Utility V2.31"): open it by ARENA.H1
#   or ARENA.1 — the reader finds the other volumes beside the anchor and, only on a miss, in the
#   sibling directories of the anchor's parent, so a one-directory-per-disk image dump works.
#   ARENA.H1..Hn are ONE 96-byte-record directory (byte 0 = 0x08 dir / 0x02 file, +1 a 13-byte
#   NUL-padded name, +14 a 13-byte parent name, +27..+91 zero, +92 an LE u32 size) whose 106 file
#   sizes sum to 20,513,662 == the LE u32 in ARENA.TDS EXACTLY; ARENA.1..n concatenate into ONE
#   11,083,470-byte stream of [u16 clen][u16 ulen] + LZHUF blocks (the repo's LzhufCodec, a FRESH
#   tree and window per block). ⚠ clen COUNTS the ulen word — stride clen+2, NOT clen+4. ⚠ clen ==
#   0xFFFF = 10,000 STORED bytes with NO ulen word (42 blocks). ⚠ a file whose size is a multiple of
#   10,000 ends with an EMPTY block (clen 3 or 4, ulen 0) carrying the encoder's flush. 2,115 blocks
#   (2,069 + 42 + 4) tile the stream exactly and no block straddles a disk boundary; 106/106 files
#   extract, 97 byte-identical to the retail install. ⚠ `archive info` reports the RELEASE's
#   11,093,746 bytes (directory + data + TDS) via IArchiveBackend.ContainerSizeBytes, not the
#   8,160-byte ARENA.H1 the file system sees — a multi-file family must override that or it prints
#   the anchor.
#   Daggerfall CD ARENA2\PACKED.DAT: ARCH3D.BSA + DAGGER.SND, PKWARE DCL ("implode") — decoder
#   Core/Compression/PkwareDclDecoder (ported from zlib contrib/blast.c, zlib licence). u32
#   entryTableOffset, u32 directoryNameOffset, blocks from +8; 25-byte entries (u32 size, u32 0,
#   13-byte NUL-PADDED name, u32 firstBlock) then a 60-byte NUL-padded destination dir ("ARENA2")
#   ending exactly at EOF (10,436,188). ⚠⚠ A block header is 36 BYTES (0x491038, 0x4D2038, usize,
#   csize, csize, usize, 0x80000, a per-block dword, 0x549754) and the DCL stream is the csize bytes
#   AFTER it. The earlier "28-byte header + payload + 8-byte trailer" reading TILES IDENTICALLY
#   (36+csize == 28+csize+8) — and taken literally it decodes NOTHING on 134/134, because the bytes
#   at +28 are that per-block dword (ARCH3D's first is 0x91596081, LE "81 60" — the very bytes once
#   recorded as "not a bare DCL header"); only "stream at +36, csize-8 bytes" starves late, 5 to
#   1,037 bytes short, median 23.5, 0 of 134 completing.
#   ⚠⚠ Each block ENDS ON THE CODEC'S OWN END-OF-STREAM CODE and consumes csize TO THE BYTE (134/134),
#   so csize is VERIFIED from the payload, not merely bounding it. TWO successive notes got the code's
#   POSITION wrong: it is not absent (the first error, "2 flush bytes left unread" — an artefact of
#   stopping the decode at the container's declared size) and it does not "consume the last two bytes
#   exactly" (the correction, true on 14 of 134). Its 16 bits (1 + seven 0 + eight 1 = length symbol
#   15 base 264, extra 255 -> 519) END on the last byte: byte-aligned on 14 blocks, spanning the last
#   THREE bytes on the other 120, 0..7 unused bits left over (0:14, 1:9, 2:18, 3:24, 4:22, 5:14, 6:15,
#   7:18). Retail tails are that code at all eight offsets and nothing else (01ff/807f/c03f/e01f/
#   f00f/f807/fc03/fe01). ⚑ 104 + 30 blocks reproduce 27,143,532 / 7,661,766 bytes, md5 26d3e935… /
#   37c10bc7… — byte-identical to the installed ARCH3D.BSA and DAGGER.SND. That md5 is what SETTLES
#   the codec: the container tiling was already exact while the payload was undecoded, so tiling could
#   not discriminate. ⛔ The per-block dword is NOT a checksum (unique on all 134; CRC32/Adler/sum/XOR
#   of both the compressed and the plain block all refuted).
#   ⚠ All 134 retail blocks open 00 06 (RAW literals), so the DCL decoder's 256-symbol CODED-literal
#   table is exercised by no shipped byte — it is pinned instead by a hand-packed vector that codes
#   all 256 symbols and must decode to the bytes 0..255.
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
#     ⚑ The HTBL chunk is a 16-STEP LINEAR LIGHT RAMP (measured 2026-09-06): 1,937 of 1,937 retail
#     entries carry exactly 16 tables of 256 15-bit colours, every one monotonic in luminance, with
#     step i at i/15 of full brightness to within a percent. ⛔ NOT 32 tables as once recorded, and
#     no longer "believed" — measured. Not used by `sprite render` (a static export has no light
#     level to pick a step); it is what a LIT renderer would index.
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
#   Fallout Tactics .zar (--game tactics|ft, auto-selected by extension; loose or as a .bos entry;
#     `Core/Formats/Tactics/TacticsZarImage`, read off BOS.exe's own ImageZAR — read FUN_006f7d50,
#     rows FUN_006f8c50, decode FUN_006f9a10/FUN_006f9cd0): `<zar>` NUL + ASCII version (2..4) NUL,
#     u32 w/h @8/@12, u8 hasPalette @16, u32 count @17 (256), count x B,G,R,x @21, u8 shadowIndex
#     ONLY when version > 3, u32 dataLength, then the RLE block (data + length == file size,
#     839/839 gui ZARs; 31,127/31,127 tile-embedded). Per row until x == width: c = u8, mode = c & 3,
#     count = c >> 2 — 0 = count TRANSPARENT, 1 = count OPAQUE indices, 2 = count (index, alpha)
#     PAIRS, 3 = count ALPHA bytes at palette[shadowIndex] (shadow / outline strokes). Runs never
#     cross rows; count may be 0 (an empty mode-2 byte precedes a row that starts in mode 3).
#     ⚠ NOT zlib. ⛔ The four codec families the board once "refuted" were all run from the WRONG
#     OFFSET (1045): the shadow byte and the u32 length move the data to 1050, and the
#     2-bit-mode / 6-bit-count family IS the codec (controls: data @1045 0/839, correct 839/839).
#     ⚠ 4,534 of the tile-embedded ZARs are v3 and carry NO shadow byte — reading one scores 0/4,534.
#   Fallout Tactics .til/.spr (--game tactics|ft, auto-selected by extension, beside the .zar the ZAR
#     track shipped). Both are containers around the same <zar> RLE codec, so both decode to RGBA.
#   .til = `<tile>` v6..10 + `<tiledata>` v1 (readers FUN_006f04c0 / FUN_006f0740 in
#     C:\dev\phoenix\map\tile.cpp; writers FUN_006f03a0 / FUN_006f0ab0). Header: u8[3]
#     bounding_box_size, i32 x,y foot_position (SIGNED), i32 w,h image size, u8 type, u8 material,
#     then a VERSION-SHAPED flag word. `<tiledata>`: u32 count, then per image an embedded <zar> +
#     8 RAW BYTES, then ONE shared palette handed to every image (FUN_006f0da0). 29,957/29,957
#     consume the file exactly; 31,127 images; each image's own palette is byte-identical to the
#     shared one 31,127/31,127. ⚠ A ZERO count returns BEFORE the palette.
#   ⚠⚠ THE FLAG WORD IS NOT A u16 ON EVERY VERSION — and v8 is NOT the legacy form. The reader takes
#     a throwaway byte on every version below 9 and THEN branches: `if (v < 8)` a byte giving bit 0,
#     on v6 only a SECOND throwaway, then a byte giving bit 1; `else if (v < 10)` a FULL u8 — the
#     branch V8 TAKES; `else` a u16. Record lengths 34/33/32/31/33 for v6/v7/v8/v9/v10; retail ships
#     all five (1,485/4,479/1,850/11,250/10,893). The throwaway is 100 on all 7,814 sub-v9 tiles,
#     v6's second is 0 on all 1,485, and v8's flag byte is 0 on all 1,850.
#   ⚠⚠ A u16 reader DESYNCHRONISES 17,214 files (v9 11,250 + v7 4,479 + v6 1,485) and SILENTLY
#     MIS-VALUES 1,850 more: v8's throwaway+u8 spans the same two bytes, so those files still tile
#     and merely read their flags as 0x0064 = 100 instead of 0. ⛔ Do not fold the two into one
#     "19,064" figure — an exact-tiling check CANNOT see the v8 error; only the flag census can.
#   ⚠⚠ FLAG BITS ARE NOT CONTIGUOUS — Ethereal 0, Window 2, Cover 3, Climbable 4, NoShadow 5,
#     Invisible 7, "No Alpha" 10, Trellis 11, each fixed by the editor widget it binds to
#     (FUN_00642910 creates + FUN_00645b30 reads back + FUN_00645d30 writes out). Retail sets exactly
#     those eight plus unnamed bit 1 and NEVER 6, 8, 9 or 12-15 — which is what refutes the obvious
#     contiguous-0..7 reading (it predicts bit 6 traffic and no 10/11; the data is the opposite).
#   ⚠⚠ SAY HOW FAR THAT CENSUS REACHES: every set bit is on a v9 tile (215, bits 0-3 only) or a v10
#     tile (4,470) — v6, v7 and v8 set NOTHING (their legacy bytes are 0, and v8's flag byte is 0 on
#     all 1,850). So the contiguous-reading refutation rests on v10 alone and NOTHING here speaks for
#     the 7,814 tiles below v9. Bit 1 likewise is a v9/v10 flag (103+51), NOT a product of the
#     `v < 8` reconstruction path, which retail never exercises with a non-zero.
#   Type (u8) 0 Wall / 1 Floor / 2 Object / 3 Stair / 4 Roof; material (u8) 0 Stone / 1 Gravel /
#     2 Metal / 3 Wood / 4 Water / 5 Snow. ⚑ CONFIRMED OUTSIDE THE BINARY by the artists' naming
#     convention Set_Type_Material_Name: 28,606/29,572 (96.7%) and 28,311/28,932 (97.9%).
#     ⚠⚠ Material 5 scores 0/808 on that oracle — every Snow tile is NAMED "Gravel" — and is settled
#     instead by its PATH: 808/808 live under a Snow directory. A control that cannot reach a
#     population says nothing about it.
#   ⚠ 382 tiles carry a type outside 0..4 and 214 a material outside 0..5; ALL are v6/v7 and the
#     game's own switch tolerates them. Surface the raw value, never clamp.
#   ⚑ The 8 bytes beside each image are a per-image DRAW OFFSET and a multi-image tile is an
#     ANIMATION: FUN_00782530 picks record `counter % count` and adds its dwords to the draw x/y.
#     The art shows it too — the 11-image Generic_Floor_Water_GoopBubCENTRE renders as a bubble cycle.
#   ⚑⚑ THE MISSION GRID'S `<tile>` RECORD IS THIS HEADER — the "33-byte record with four unexplained
#     u32s" is a v10 header (10 B framing + 23 B body) and the dwords are foot_position + image size.
#     ⚠ The mission-side ranges (-40..352, 5..507, 5..581, 5..489) are CONTAINED IN the standalone
#     corpus's (-40..352, 5..507, 2..581, 2..489), not equal — they agree exactly on foot_position
#     (119 negative x included) and differ only in lacking the smallest images.
#   .spr = `<sprite>` v4 -> u32 sequences -> u32 `<spranim>` v1 headers, each stating the ABSOLUTE
#     offset of its `<spranim_img>` block. A block is version '1' RAW or '2' u32 inflatedSize + one
#     raw zlib stream; retail ships 1,586 raw and 3,025 zlib and 810 of the 918 FILES hold at least
#     one raw block, so a zlib-only reader rejects 88% of them. Body = four LAYER palettes
#     (base/skin/hair/tcol, FUN_0070b3a0) then per (frame, direction, layer) a present byte, i32
#     ox,oy RELATIVE TO THE FRAME RECT, and a palette-less <zar>. 918/918 walk; 4,611 animations;
#     842,176 embedded ZARs.
#   ⚠⚠ v1 iterates frame/direction/layer, v2 iterates LAYER/direction/frame — reading one in the
#     other's order desynchronises after the first present flag.
#   ⚠ A v2 block's COMPRESSED length is stored nowhere, so only its inflated body can be checked.
#   ⚑⚑ ELEVEN OF THE TWELVE SEQUENCE EVENT CODES ARE SOLVED (editor dialog FUN_00449300 binds a
#     caption to a callback; each callback pushes one constant into FUN_0070b7c0(seq, pos, value)):
#     -2 Anim Delay, -3 Anim Rate, -4 Anim Repeat, -5 Anim Goto, -6 Start Overlay, -40 Left
#     Footstep, -41 Right Footstep, -42 Weapon Fire, -43 Weapon Release, -44 Sound start, -45 Pickup.
#     ⛔ CORRECTS three earlier inferences: -44 is Sound start not Weapon Fire (-42), -3 is Anim Rate
#     not Anim Goto (-5), -5 is Anim Goto not Anim Delay (-2).
#   ⛔⛔ THE TWELFTH, "Special Key" = -1, IS INFERRED, NOT READ FROM THE BYTES. Its callback
#     0x0044ab30 (a LAB_ the Ghidra dump never decompiled; disassembled from the retail exe) prompts
#     "Type in Special Key List" (0x00898bbc), splits on "," and makes its ONE append call with each
#     element's AUTHOR-TYPED value parsed by 0x007a31ba. No negative literal exists between
#     0x0044ab30 and its ret at 0x0044ad8d; the only -1 is a "nothing selected" sentinel. The button
#     is an escape hatch for typing raw entries — -1 is OUR name for a code the game never emits
#     (0 of 7,043 retail sequences). Harmless, but never quote it as measured.
#   PARAMETER COUNTS come from the player FUN_0044e9a0 ALONE: -43 cursor+4, -5 cursor read FROM the
#     next entry, -4 cursor=0, -3 cursor+1 into the field the wrap resets to 0x42=66 then falls
#     through to default +1, -2 cursor+2, default +1. (The Anim Goto callback corroborates: it
#     appends -5 and then a SECOND, atoi'd entry — code and parameter, exactly as the player eats.)
#   ⛔⛔ DO NOT CLAIM "stepping by 1+params lands on the array end on 7,043/7,043" AS A FALSIFIER — it
#     cannot discriminate. Scoring 16 tables over those sequences, 14 land exactly and 13 of those
#     are WRONG (the ALL-ZERO table included; also -43 given 0/1/2/4 and dropping -5, -3 or -2).
#     Stepping by >=1 reaches an array's end unless an assumed parameter runs off the tail; only a
#     count on a code that occurs LAST fails (-4:1 -> 6,498; -6:1 -> 7,042).
#   ⚑⚑ THE CONTROL THAT DOES DISCRIMINATE IS THE FRAME AXIS: a non-negative entry is a frame index
#     and must be < its animation's frame count. True table: 9 of 64,316 out of range. All-zero 294;
#     -43:0/1 184; -43:2 148; -44:1 188; -3 dropped 117; -2 dropped 11. ⚠ It cannot reach -5 (its
#     parameter IS a frame index) or -43 given four.
#   ⚠ Those 9 are NOT all "authored off-by-ones": 5 sit exactly one past the last frame, 4 sit
#     FURTHER past. Four sequences are off-by-ones (LeatherMale DeathFireOverlay, RaiderFemale
#     StandClimbdown, JammingTower DeathBrokenOverlay, Calc Pillar ClosingIn); Wolf DeathFireOverlay
#     runs 0..29 over 26 frames and TribalMaleLarge CrouchAttackRifleBurst carries a lone 21 over 11.
#   ⚠ Anim Rate parameters run 10..122 (111 events, default 66) and Anim Delay's two are 900 and
#     1200 — two scales for two meanings. NOTHING states a unit; do not call them milliseconds.
#   ⚑ The SPRITE material enum is 19 values, not 8 (FUN_00444fb0; names in .rdata 0x898604-0x8986a8):
#     0 concrete, 1 brick, 2 metal_thin, 3 metal_thick, 4 ceramics, 5 grass, 6 plant_hard,
#     7 plant_soft, 8 dirt, 9 gravel, 10 rock, 11 mud, 12 swamp, 13 water, 14 rubber, 15 ethereal,
#     16 carpet, 17 plastic, 18 glass. Retail uses only 3 (829) and 0 (89).
#   `sprite render <x>.spr` writes every animation as <name>_a<NN>_<Anim>_d<NN>_f<NNN>.png;
#     -a/--animation <index|name substring> narrows it. `sprite info` prints a header summary plus one
#     row per animation (.spr) or per image (.til) — it does NOT decode a .spr's pixels.
#   Brotherhood of Steel .CLP (PS2): `sprite render <FALLOUTBOS.iso> -e DATA/HUD.CLP -o <dir>`
#     decodes every .tex section in a clump through the GS layout — CLUT 16x16 PSMCT32 CSM1,
#     pixels PSMT8 stored as PSMCT32 — labelled by the asset name the executable hashes
#     (bos_logo, 2202_desert_eagle) or tag_XXXXXXXX for the ~95% with no string name. HUD 130,
#     INV_SWAP 27, SFX 25, GLOBAL 138, ARMOR 92; 2,876 across the disc.
#     ⚠ A streamed <level>_T.CLP holds texture PACKS, not single textures, and yields nothing.
#     ⚑ ALPHA IS A COVERAGE FLAG on 2,137 of the 2,873 CLUT textures — every texel they sample
#     carries a GS alpha of 0 or 1, and at the GS scale (0x80 = 1.0) an alpha of 1 is 0.8%, which
#     no blend mode could show. Those export 0/255; the other 736 carry a graded ramp and are
#     scaled a*255/128. ⛔ Scaling all of them exports 2,137 textures — the walls, floors and
#     armour — as effectively blank. A raw-GS-alpha decode therefore DISAGREES with ours on
#     exactly those, and agrees byte-for-byte elsewhere (8 of 9 spot checks identical over the
#     full RGBA buffer; the 9th differs in alpha only, 0 RGB bytes).
#     ⚠ Icons that look DOUBLED are authored variant pairs, not a swizzle fault: no decoded
#     image has an exact vertical period, and every texture uploads at least w*h pixel bytes.
#     ⚠ `loading.tex`'s mirrored plates are likewise the stored pixel order, not the decode —
#     the same decoder renders bos_logo and the PS2 legal screen unmirrored and legible.
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
#   Arena .VOC (8-bit PCM; rate = 1000000/(256-timeConstant)).
#   Fallout 1/2 .ACM (Interplay ACM — `Core/Formats/Interplay/InterplayAcmFile`, ported from libacm
#     1.5, ISC; -e takes the DAT entry path, e.g. SOUND/MUSIC/01HUB.ACM, --all sweeps an archive).
#     EVERY ACM decodes to EXACTLY the header's value total: 2,511 in FO1 MASTER.DAT, 2,473 in FO2
#     master.dat, plus the 40 loose 22,050 Hz music files. The header's u32 total counts VALUES over
#     all channels; a block carries rows * (1 << level) values and the last block only the remainder.
#   ⚠⚠ THE HEADER'S CHANNEL FIELD IS WRONG on every speech and effect file that says 2 — 1,647 of
#     FO1's and 1,551 of FO2's declare stereo and NONE is. Two oracles outside the file: every speech
#     line ships a same-stem .TXT transcript, and words per second is conversational only read as
#     MONO (2.65 median vs 5.3 as stereo, over 1,538 + 970 lines); and 1,301 + 752 "stereo" files
#     carry an ODD value total, which no interleaved stereo stream can. The music really is stereo
#     (3-4 minutes a track; 7-8 read as mono). So the reader exposes DeclaredChannels AND an inferred
#     count (`InterplayAcmChannelProbe`: odd total -> mono; lag-1/lag-2 step ratio >= 2 -> stereo;
#     L≈R pairing <= 0.5 -> stereo, which is what catches MAYBE.ACM; otherwise mono) and
#     `AsInferred()` is what a player uses. ⚠ The two WIND loops read as mono on the probe alone —
#     nothing outside the file settles them. ⚠ A test that pinned the header's own count would pass
#     a decoder that plays speech at double speed.
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
#   Brotherhood of Steel _S.CLP: `audio decode <FALLOUTBOS.iso> -e DATA/C1/BAR/BAR_S.CLP -o <dir>`
#   writes every sound as NNN_name.wav, 16-bit mono, AT THE RATE ITS OWN VAGp HEADER DECLARES
#   (big-endian +0x10 — see the BOS block above for the census; no constant is right). BAR_S gives
#   34 files, "16000 Hz x12, 18000 Hz x17, 22050 Hz x5". ⚠⚠ The export STOPS AT THE SPU2 END
#   MARKER: decoding that block appends 28 samples of a constant +28,672 (a click) to every sound,
#   and no peak/RMS/clipping/frames-%-28 check can see it. Frame counts come out as exact multiples
#   of 28. `audio info` prints the census with each sound's SPU2 pitch ((rate << 12) / 48000, the
#   engine's own formula at 0x0019AA18). ⚠ A .CLP that is not a resident bank reports the clump
#   parser's reason, never falls through to the VOC reader.
classic exe <A.EXE> [-o <out>] [--info]    # Unpack the PKLITE-compressed Arena executable
#   Retail A.EXE: 174,021 -> 304,624 bytes. Holds province/race names, item + city tables.
#   Arena saves (`Core/Formats/Arena/ArenaSaveGame` + `ArenaSaveEngine`; no CLI route yet; fixtures are the
#     real SAVEGAME/SAVEENGN.00/.01/.02 in the data root — one playthrough, .00/.01 in START.MIF, .02 in MAGE6.MIF).
#     SAVEGAME.NN = 166,631 B of six PLAINTEXT blocks tiling exactly, each anchored by an EXTERNAL oracle:
#     0x00000 64,000 screenshot (320x200 8-bit, the save-slot menu; retail P1.IMG at rows 147-199 at 94.298%) ·
#     0x0FA00 768 palette == PAL.COL[8..776] >> 2 on 768/768 (6-bit; promote << 2, 0x3F -> 0xFC) · 0x0FD00 3,559
#     state (raw residual; known: level name 0x1048C 33 B NUL-TERMINATED WITH RESIDUE — .02 reads
#     `level1\0evel 1\0`, read to the first NUL, never trim; .INF 0x104AD 13 B; 64 x 0xFF at 0x104BA; a VERBATIM
#     61-byte MHDR payload at 0x1053A parsed via ArenaMifFile; .MIF name 0x10577) · 0x10AE7 MAP1 + 0x18AE7 FLOR
#     planes (128x128 u16 LE, 256-byte pitch, ODD offsets — cells 50..127 read 0xE000 9,984/9,984 there and
#     9,984 exceptions one byte later; fills 0xE000 / 0x0000; live = MHDR width x depth, 50x50/50x50/25x25) ·
#     0x20AE7 32,768 raw DOS memory 0x0000-0x7FFF (RawMemoryImage, unparsed). Oracle: .02 == MAGE6.MIF L0
#     625/625 on BOTH planes; .00 == START.MIF L0 FLOR 2,499/2,500, MAP1 2,419/2,500 (transposed: 525/1,545).
#     ⚠ The 81 MAP1 deltas are all on 0x8000-flag cells but are NOT "door toggles preserving the low byte":
#     41 gained bit 0x0200 with the low byte kept, 31 cleared to 0, 9 appeared from 0 — a per-delta assertion of
#     the first reading failed on cell 302. ⚠ START.MIF is ONLY loose, MAGE6.MIF ONLY in GLOBAL.BSA.
#     ⛔ NOT XOR-encrypted — refuted by the external oracles above, NOT by a key scan (the k=1..512 scan cannot
#     recover a planted key on the dense regions); Arena's .INF cipher (ArenaInfFile) is unrelated.
#     SAVEENGN.NN (17,983 B): 3,664-byte head + 1,054-byte records, 13 whole slots + 617-byte zero tail;
#     populated = the 8 bytes at +40 == +48 AND non-zero (⚠ the equality alone fires 11,668x) — a whole-file
#     scan returns exactly 3,664 + k*1,054, k=0..5, in every retail file; .02's slots 6/7 hold one non-zero byte
#     each and are NOT populated. No count field, no creature ids, no 40x19 sub-array, head meaning unknown:
#     head/records/tail are raw bytes.
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
classic block export <BLOCKS.BSA|dir> -e NAME -o <dir> [--scale N]  # A TEXTURED GLB of the whole
#   block (both RMB and RDB) plus the diagnostic PNGs: RMB automap (64x64 building codes) + ground
#   grid (16x16 tile records; scenery dots); RDB top-down plan. The GLB needs ARCH3D.BSA beside
#   BLOCKS.BSA, i.e. a real ARENA2. ⚑ Unlike Battlespire's level export these are TEXTURED, because
#   TEXTURE.nnn is decoded and BSI.BSA is not (B0000002.RDB: 46/46 placements, 86 materials).
#   Retail: 920 RMB / 187 RDB / 187 RDI + "FOO" (a stray DOS dir listing). RDB width x height is
#   the number of object LISTS, not spatial cells — objects sit inside one 2,048-unit block, ⚠ with
#   7 STRAYS reaching 2,712 (measured X 0..2,064, Z 0..2,712 over all 187 retail RDB blocks; the
#   game-daggerfall board records the strays, this line used to drop them). ⚠ Y spans
#   -4,608..3,009 because dungeons DESCEND, so RMB's non-negative square does not transfer.
#   ⚑ All 22,961 modelled RDB objects resolve their ModelIndex to a numbered mesh id — used
#   DIRECTLY as a slot number into the block's 750-entry reference table, never scaled.
# ⚠⚠ ROTATIONS ARE 2,048 UNITS PER TURN (512 = 90 deg), so a stored value is DIVIDED by 5.68889.
#   DaggerfallBlockSceneAssembler MULTIPLIED until 2026-09-06 and skewed every rotated sub-block by
#   32.36x. Three retail populations settle it: the 9,005 RMB sub-block rotations take EXACTLY the
#   four values 0/512/1024/1536; 92.07% of the 236,250 per-model rotations lie in [0, 2048); 96.0%
#   of the 22,961 RDB Y rotations are multiples of 512 (X and Z zero on 97.9% — models stand up).
#   ⚑ Settled by each block's own 64x64 AUTOMAP, which is independent of the placement fields: on
#   the population rotation actually MOVES, 2,048/turn puts 32/32 models on a built cell while
#   63.28/turn manages 5/30 — WORSE than no rotation at all (17/32). ⚠ On the whole corpus the gap
#   collapses to 0.86 vs 0.80, so only the affected population discriminates. ⛔ The SIGN is NOT
#   settled by that oracle (both score 32/32) — it follows Battlespire's measured raw-angle
#   convention. ⛔ A block-containment control was tried first and CANNOT discriminate: 0.9997 for
#   both signs, and "no rotation at all" scores 0.9971 against the wrong scale's 0.9946.
# ⚑ Both block families route into the GUI 3D level pane and both are probed by CONTENT, not name:
#   an RDB states its object-root offset as EXACTLY 9,532 at +12; an RMB's sub-block sizes must tile
#   inside the payload AND its own name must read as text. 920/920 and 187/187, with zero claims on
#   the 187 RDI or FOO. ⚠ The name check is what carries the 113 RMBs declaring ZERO sub-blocks
#   (witch covens, carnivals, ruins) — real city blocks made only of loose scenery.
classic dungeon info <ARENA2|dir> [-e NAME] [-l N]  # List MAPS.BSA dungeons, or one dungeon's
#   block grid (X, Z, resolved RDB name, start flag). -e is a SUBSTRING of the location name.
classic dungeon export <ARENA2|dir> -e NAME -o <dir>  # Assemble a WHOLE dungeon into one textured
#   GLB - every block of its grid, not one block at a time. Privateer's Hold: 5 blocks, 365/365
#   placements, 478 materials, extents 6,080 x 6,016 (a 3x3 block footprint).
# ⚑ BLOCK NAMES ARE MEASURED, NOT PORTED: a dungeon block's (BlockIndex, BlockNumber) resolves to
#   letter + 7 digits + ".RDB", letters "NWLSBM" indexed by BlockIndex. Derived by comparing the
#   number SET each index uses against the set each letter ships in BLOCKS.BSA over all 62 regions
#   - 4,232 dungeons, 40,263 references, ALL resolving, and all five sets IDENTICAL (not merely
#   same-sized). ⚑ The S family settles it: its three non-contiguous strays 204/205/999 appear on
#   both sides, which chance cannot reproduce. ⚠ Index 2 ('L') is UNUSED by retail and must stay in
#   the table - dropping it shifts S/B/M down one and mis-resolves three families at once.
# ⚠⚠ A block's Z GRID INDEX IS NEGATED, its X index is not. The per-block transform already mirrors
#   z to 2048-z, so an offset composed after it must be negated to stay in that frame. Adding
#   +Z*2048 reflects the whole grid: every block internally correct, the dungeon laid out backwards.
#   Daggerfall SAVEn slots (`Core/Formats/Daggerfall/DaggerfallSaveSlot`, Bucket-B only — Steam ships
#     SAVE0..SAVE5 as autocloud stubs, so a slot is recognised by SAVETREE.DAT, never by the directory).
#     `SAVETREE.DAT` tiles EXACTLY: 19-B header (i32 version 0x126, i32 X/Y/Z, u16 index of the current
#     dungeon in its region's MAPDITEM table, u8 environment 1 outside / 2 building / 3 dungeon), an
#     i32-prefixed building block of 26-B records, then {i32 len; len bytes} records (len 0 = separator)
#     of a 71-B root + data, and a TRAILER the DFU reference never sees: u32 count, count x 39-B entries
#     keyed by record id, u32 0. ⚠⚠ DFU's "type 7 Light records are 39 x length" is a MISREADING of that
#     trailer (its first byte is a record id's low byte) and its break-on-overrun silently discards it.
#     ⚑ Root byte 35 == 0xFF marks the records that own a trailer entry — 23/23, as a two-way set equality.
#     Retail SAVE0: 64,836 B = 19 + 4 + 158 records + 2 separators + 4 + 23x39 + 4.
#   ⛔ A record id is (LocationId << 16) | serial, but the EXTERIOR LocationId is NOT among them: the
#     census is 0xC382 x125 / 0x0001 x17 / 0x0064 x11 / 0x02BC x3 / 0x5439 x1 / 0x5513 x1 = 158, and
#     0xC3810001 (the exterior) appears only in SAVEVARS +0x17AC. ⚠ One id is DUPLICATED (0xC382FA01).
#   ⚠⚠ The Character body's tail fields are at +0x1F9 (u32 51,250), +0x1FD (u32 game-time stamp ==
#     SAVEVARS gameTime) and +0x20D (gameTime - 1) — NOT +0x1F8/+0x1FC/+0x209, which read 13,120,000 /
#     134,155,008 / 0. Its +0x230 CAREER is a CLASS??.CFG image, byte-identical to CLASS12.CFG on retail
#     (runner-up 47/74 over 19 files).
#   ⛔ A Spell record's Index byte is a SPELL ID, NOT its position in SPELLS.STD and not "position + 1":
#     the six retail pairs are 10/9, 44/41, 7/6, 44/41, 29/27, 10/9 — "+1" holds on 3 of 6 — and STD id
#     58 is carried by two records. Find a spell by SEARCHING for a byte-identical 89-B record, never by
#     indexing. ⚠ Do not let the fixture mislead you: "Free Action" happens to be id 10 at position 9.
#   `SAVEVARS.DAT`: 62 x 80-B region blocks at 0x3DA, so the table ENDS AT 0x173A. ⛔⛔ 0x1738 is NOT a
#     free scalar — it is region 61's PriceAdjustment (0x3DA + 61x80 + 78), and a reader that also
#     surfaces it as "Unknown1738" is reading the same two bytes twice; we shipped that and published
#     886 as an open question. ⚑ The base is MEASURED, against the NEAR MISS: at 0x3DA the 62 price
#     adjustments are 757..1246 with region 17 = 813; at 0x3D2 (the base implied by an "end at 0x1732"
#     typo) all 62 price adjustments, legal reputations and persecuted-temple ids are 0. The only
#     genuine unknowns left are 0x1748 (8 B) and 0x1796 (525,007).
#     The file STATES its faction count (u32 @0x17CC = 366) and the 92-B records run to EOF; the reader
#     refuses a file where the stated count misses EOF (DFU derives it from the length instead).
#     ⚑ 366/366 resolve against ARENA2\FACTION.TXT — but only if you keep EVERY name an id declares and
#     TrimEnd BOTH sides. ⚑ Why: the 366 records span only 365 DISTINCT ids, because id 77 is stored
#     TWICE (slots 129 and 135), once per name FACTION.TXT declares for it ("The Guildmaster" and "The
#     Master of Initiates"). Measured over all three readings — all-names + TrimEnd: 0 mismatches;
#     first-name-wins: 1 (id 77); no trim: 1 (id 420, "The Dust Witches ", with a space the game never
#     trimmed). ⛔ "365 of 366, and the data is just like that" was published once and is WRONG.
#     gameTime is MINUTES @0x3C9 over 30-day months / 12-month years, minute 0 = 00:00 1 Morning Star
#     3E404; 524,043 = 22:03, 4 Morning Star 3E405 (`DaggerfallGameTime`).
#   `RUMOR.DAT` is a pure walk of {34-B header, textLength bytes} — the NUL terminator is INSIDE the
#     length and 0xFD is the line break. `BIO.DAT` is NUL-separated lines terminated by an EMPTY line
#     (the file's last byte is a NUL, so a naive split leaves two empties). `IMAGE.RAW` is 4,000 B =
#     80x50 8-bit indices through ARENA2\ART_PAL.COL (776 B: 8-byte header + 768 palette bytes, max
#     component 255, so FULL-RANGE 8-bit — promoting it as 6-bit VGA makes it four times too dark).
#     ⛔⛔ A HEADERLESS image's STRIDE cannot be settled by "the HUD strip is brighter than the view":
#     measured 76 -> 2.042, 78 -> 2.187, 79 -> 2.201, 80 -> 2.199, so the NEAR MISS SCORES HIGHER than
#     the truth and every stride from 76 up passes a 1.8 gate (only 40/50/64/72 -> 0.87/0.92/1.24/1.78
#     fail). ⚑ VERTICAL COHERENCE settles it, as it did for Redguard's WLD rows: the mean absolute
#     difference between vertically adjacent pixels has a UNIQUE minimum at 80 (11.600) over 60..120,
#     runners-up 81 (12.674) and 79 (12.697). ⛔ A per-column HUD-boundary standard deviation was tried
#     and PREFERS THE WRONG ANSWER (79 -> 12.62 vs 80 -> 13.98) — do not re-try it.
#     `AT<LocationId>.AMF` = u32 game-time + 10,240 B, equal to the type-0x33 DungeonAutomap record's
#     first 10,240 bytes except root bytes 39-41 and 67-69, which it zeroes.
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
#   ⚑ FLATS ARE PLACED AND TEXTURED (2026-09-06): a level's billboards (monsters, items, flames)
#   resolve their FILN sprite name through BSI.BSA, which decodes — 2,272 of 2,272 on retail, L8
#   placing 112/112. ⛔ They were recorded as blocked on the mesh-texture item; that was ASSUMED and
#   is refuted — a mesh carries an unnamed u16, a flat NAMES its sprite.
#   ⚠⚠ A raw regex scan for FILN over the file is WRONG (FILN occurs outside FLAT chunks too) and
#   reported a false 27/28 with a bogus `structs` miss; go through the PARSER.
#   ⚠ SCAL is NOT the size — 0 on 2,262 of 2,272; the quad is sized from the sprite's pixels, at a
#   STATED-ASSUMPTION 1 world unit per pixel. Built Y-DOWN (y in [-h, 0]) and double-sided.
#   The MESHES remain untextured — their u16 texture reference into BSI.BSA is undecoded.
#   Battlespire SAVEn (install root, beside GAMEDATA): SAVETREE.DAT + SAVEVARS.DAT + SAVENAME.DAT +
#     IMAGE.RAW, ALL LITTLE-ENDIAN, read by `Core/Formats/Battlespire/BattlespireSave*`. SAVETREE is a
#     u32 0x00000100 then records back to back — u32 length (excluding itself), u8 type, a 65-byte
#     common header (u16 pitch/yaw/roll, f32 X/Y-up/Z, u16 flags, u32 FileID, u32 RecordID, u32
#     ParentID at +61), then a fixed body. 373,887 B = 557 records ending at 373,700 + a 187-byte
#     trailer. ⚠⚠ WALK BY THE LENGTH WORD, NEVER BY THE TYPE: the last record has type byte 0 and is
#     a 184-byte spell tombstone, so a type-sized walk desyncs there; the walk ends at the first ZERO
#     length word and the trailer (zeros but for 0x02 at rel 10/27/97/104/160) is UNDECODED.
#     ⚑ Positions are RAW BS6 units, NOT the /256 world units Bs6File callers use (-1122.0 vs POSI
#     -1122, 125/130). Object RecordID == OBJD.IDNB (130/131) and FileID == IDFI (130/130).
#   ⚠⚠ Header offsets are counted from the LENGTH WORD, not the type byte — measuring +37/+45 from
#     the type byte yields a different, meaningless population (300+ non-zero, max 0xFFFFFFFF).
#     Correctly read, +37 is non-zero on 8 of 557 (max 2,000,001,074) and +45 on 3 (max 8,707,024),
#     both overshooting the 1,169,900 clock, so NEITHER is a clock value. ⛔ 8,707,024 is +45's
#     maximum ALONE; it was quoted as the pair's for two rounds.
#   ⚠⚠ A Player body and a Monster body are NOT interchangeable at the tail: `+803` (SAVEVARID =
#     StaticEnemy ordinal + 1, 0 = none) is a MONSTER field — 97/97 monsters hold 0..56, the player
#     holds 0xED07ED00. `BattlespireSaveCharacter` takes a `BattlespireSaveCharacterKind`.
#   ⚠ ParentID does NOT all resolve, and "resolves to a record" is a WEAK test (Objects with IDNB 0
#     and 1 exist, so the sentinels resolve by accident). Taxonomy over 557: 1 = on the map 262,
#     another record 258, player 0xC350 9, 0 on THREE (Player, Options AND one Item), DANGLING 25 —
#     20 Monsters at 0x12715 (a DYNAMIC id no record carries) and 5 Items at BS6 ids. 532/557.
#   ⚠ Item names take THREE forms against UESP's ItemList, 275 + 31 + 17 = 323: equal outright,
#     the SINGULAR of a plural entry (Greave/Greaves, Gauntlet/Gauntlets, Pauldron/Pauldrons,
#     Boot/Boots — 31 records), or id 19 Clothes named by BSI sub-type. ⚑ ITEM ids resolve through
#     that list; `BattlespireItemTable` is the ENCHANTMENT oracle (TXT.BSA, 105/105 by id AND name).
#   ⚠⚠ SAVEVARS' 534-word region at 2,355 is NOT 534 log timestamps — the non-zero words are exactly
#     indices 0-4 (1,166,615 / 1,146,280 / 1,146,280 / 1,117,955 / 1,120,830) and a quartet at 16-19
#     (1,169,900 / 127 / 127 / 0x271000; 2,560,000 is PAST the clock). ⛔ The five are UNORDERED
#     (index 3 < index 4) — "descending, newest first" is refuted by its own numbers. The reader
#     takes the leading non-zero run BOUNDED AT 16 SLOTS: unbounded, the run reaches the quartet on
#     any save whose slots 5..15 are written, and this fixture's zeros there are the ONLY thing that
#     stops it — a guarantee that holds by data is not a guarantee. ⚠ The bound is a CHOICE, the
#     boundary is OPEN, and `LogTimestampWords` hands back all 534 raw.
#     ⚑ What discriminates the SAVEVARS block layout is NOT the tiling (fixed sizes tile for any
#     partition summing to 34,805) but each count word equalling its block's populated slots —
#     21/56/7/0/3/26, 6/6 — plus the ids resolving in the tree and the level BS6.
#   ⛔⚠⚠ SAVEVARS opens with the player body and bytes 0..790 match byte for byte, but that does NOT
#     settle UESP's 787 against 791: body bytes 743..790 are ZERO and SAVEVARS 791..1050 is a zero
#     gap, so any copy length in 743..1,051 is identical here. OPEN, not refuted.
#   ⚑ IMAGE.RAW is 80x50 LE u16 x555 (R bits 10-14) — the VESA 15-bit mode PATCH.TXT names. ⛔ Do NOT
#     reuse BsiFile's HICL expansion (>>11, >>6, >>1): pixel 0x10A4 reads (4,5,4) under x555 and
#     (2,2,18) under HICL; bit 0 is set on 1,804/4,000 pixels and bit 15 on none. RGB565 is refuted
#     by channel correlation (0.06/0.25 vs 0.88/0.89). ⚠⚠ Correlation is SYMMETRIC and cannot
#     separate RGB from BGR, and ⛔ the HUD strip CANNOT either — counted, rows 44..49 are B>R on
#     52/37/41/39/51/53 vs R>B on 6/23/22/27/16/12, and a BGR render makes the HUD BEZELS brass, so
#     "the HUD is brass" selects BGR. What settles it is the SCENE: ΣR 21,750 vs ΣB 14,522, R>B on
#     2,913 pixels vs 273, floor row 35 R>B 76/80 and B>R 0, and the brightest pixel (75,3) =
#     (31,31,14), a warm lamp BGR turns cyan over a floor it turns blue.
#   ⛔ Daggerfall Unity's SaveTree framing is REFUTED for Battlespire: this file's version word is
#     0x100 where DFU demands 0x126, and read DFU's way the first "record" at 0x13 has length 0, so
#     a length-driven walk dies on its own first record. Only the record ENVELOPE and most type
#     codes are shared XnGine lineage. ⚠ "and the next a length of 655,556,608" was an invented
#     illustration — that dword lives at offset 35 and follows from no step the framing takes.
classic map info <file> [-e <entry>]       # Map dimensions, levels, .INF refs, locks/triggers
classic map export <file> -o <dir>         # Render voxel layers to PNG (--scale px/voxel)
#   Arena .MIF (LZHUF layers) and .RMD (word-RLE wilderness chunks); -e reads from GLOBAL.BSA.
#   Daggerfall WOODS.WLD (1000x500 heightmap -> greyscale PNG; 5x5 sub-grid per pixel read on
#   demand) and CLIMATE/POLITIC.PAK (1001x500 RLE overlays -> hue-per-value PNG).
#   Redguard maps\*.WLD (4 outdoor worlds, all exactly 263,432 B): 32-byte header + EIGHT layers of
#   128x256 bytes (row stride 128 by autocorrelation; the top ~192 rows carry the world) + zeros +
#   a 16-byte "TULO" trailer. Exported as <stem>_layerN.png; layers 1, 5 and 7 are smooth (height-like),
#   the rest categorical, layers 0-3 and 4-7 pair up by appearance — semantics NOT established.
#   ⚑ Measured 2026-09-06: the 32-byte header AND the 16-byte TULO trailer are BYTE-IDENTICAL
#   across all four worlds, so neither carries per-world data; header word 7 (+28) = 263,416 =
#   filesize − 16 (the payload length before TULO). ⚠ ISLAND.WLD and EXTPALAC.WLD differ by
#   exactly TWO BYTES, both in LAYER 7 at adjacent cells — four files, three worlds.
#   ⚠ The "top ~192 rows" figure above looks wrong: measured non-empty rows are 106-131.
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
# ⚑ A CONSOLE DISC IS AN INSTALL too: `stats <FALLOUTBOS.iso>` works on Fallout: Brotherhood of
#   Steel (2004, PS2). `ClassicSourceProbe.TryDetectDiscImage` mounts the ISO through the archive
#   probe chain and matches the profile markers (SYSTEM.CNF + DATA\ALL.DDF) against its ENTRY
#   NAMES, exactly as a J2ME JAR is claimed. Gated on the ISO9660 `CD001` descriptor at 0x8001 so
#   an ordinary file never pays for a mount; ⚠ that gate passes only for 2048-byte-sector .iso.
#   Yields 6,230 records: BOSD (2,243 master object definitions from DATA\ALL.DDF) + BOSL (54
#   levels with a per-class census) + BOST (3,933 dialogue/UI strings). ⚑ **The `.SDB` beside a level is the NAME TABLE for its
#   `.DDF`** — 7,383/7,383 of a level's strings name one of its records, and the union of all 56
#   databases names 42,350 of the 43,006 (98.5%). The record's leading dword is a CLASS that
#   predicts its size: 0 Actor, 1 World object, 2 Weapon, 3 Inventory item, 4 Throwable,
#   5 Particle emitter, 6 Effect, 8 Level, 9 AI behaviour, 10 Light, 11 Beam effect, 12 Debris
#   set, 13 Trap (7 unused). Those names are READ OFF THE SDB, not guessed — type 9 holds
#   "RangedBasic"/"DuckAndCover", type 0 the player characters Cyrus/Cain/Nadia.
#   ⚑ The payload ECHOES its directory key at +4 on 43,006/43,006 and the reader ENFORCES it.
#   ⚠ A per-level .DDF is 99% byte-identical to the master (40,365/40,763), so only the 2,243
#   master definitions are emitted; the 398 that differ are counted as `OverridesMaster`.
#   ⚠ Names come from the SIBLING database first — 63 hashes carry DIFFERENT text in different
#   databases and are left UNNAMED rather than resolved arbitrarily (1,524 of 2,243 named).
#   ⚑ The 3,933 SDB strings that name NO record are the game's DIALOGUE and UI prose.
#   ⚑⚑ **AUDIO: `_S.CLP` is a bank of complete Sony `.vag` files** (`BosSoundBank`) — a RESIDENT
#   (256-byte page) clump whose every section is a 48-byte `VAGp` header plus PS2 ADPCM blocks
#   (byte0 = shift|filter<<4, byte1 = flag, bytes 2-15 = 28 packed 4-bit samples). Measured over
#   all 55 banks: 3,430 sections, `VAGp` magic 3,430/3,430, `size == 48 +` the header's BIG-endian
#   data length at `+0xC` 3,430/3,430, and the clump's `+16 ==` populated slots 55/55.
#   ⚑⚑ **THE SAMPLE RATE IS IN EACH SOUND'S HEADER — BE u32 at `+0x10` — AND THE GAME READS IT**:
#   `0x0019AA18` byte-swaps it and returns `(rate << 12) / 48000`, the SPU2 pitch sent as
#   `sceSdSetParam(voice | SD_VP_PITCH)`. ⛔ NO CONSTANT IS RIGHT and the old "22,050 and 11,025
#   both fit the durations, so the reader states it as an ASSUMPTION" is REFUTED: 16,000 Hz
#   x1,466 · 18,000 x1,250 · 22,050 x567 · 14,000 x57 · 12,000 x28 · 24,000 x18 · 22,000 x11 ·
#   a tail to 1,000-32,000 (20 distinct rates), and ONE BANK MIXES THEM (BAR_S 18k x17 / 16k x12
#   / 22.05k x5). 22,050 would be right for 16.5% of the sounds and 11,025 for none.
#   ⚑ Confirmed independently by `SLUS_205.39`: 32 `.vag` path strings plus "Couldn't allocate
#   aligned VAG body copy." (the body upload at `0x0019A870`, from `+0x30`). ⚑ And by a THIRD
#   implementation: ffmpeg's PS2-VAG demuxer reads the same rate off the same field, and its
#   `adpcm_psx` decode peaks within 6 LSB of ours (oracle only — no source read, nothing ported).
#   ⛔ The earlier reading — a flat 16-byte block run from `+4096`, sounds split on terminator
#   pairs — is SUPERSEDED. It found a sound only where a block flagged 1 followed IMMEDIATELY by
#   one flagged 7 closed it (54/55 banks; GLOBAL_S's 7 version-32 loopers are the exception; ⚠
#   either flag ALONE fails — flag-7 alone 15/55, flag-1 alone 2/55), it never saw the headers,
#   and it had to leave the rate open. Keep the 1/7 pair as a sanity check, never as the locator;
#   the header walk is exact on 55/55.
#   ⚠⚠ **THE LAST BLOCK IS A MARKER, NOT AUDIO** (measured 2026-09-07): 3,423 of the 3,430 sounds
#   close with the byte-identical block `00 07 77 77 ... 77`, and the dummy nibble 7 at shift 0 with
#   no filter decodes to 28 samples of a CONSTANT +28,672 — 87% of full scale, a CLICK on the tail
#   of every export. Flag 7 appears nowhere but the last block (0 of 1,875,914) and the dummy block
#   appears nowhere but last, so stopping at it is EXACT, not a heuristic. On a short sound the
#   click was the loudest thing in the file: FS_Linoleum_1's peak fell 28,672 -> 24,383 and its RMS
#   4,306 -> 3,033 when it stopped being decoded. ⚠⚠ Peak/RMS/zero-crossing stats and a
#   "frames % 28 == 0" check CANNOT see this — the marker is itself a whole block. The 7 sounds
#   without it are GLOBAL_S's version-32 loopers (flag 3). ⚠ Every sound's FIRST block is sixteen
#   zero bytes (3,430/3,430) — the init block; it IS audio (28 samples of silence) and is kept.
#   ⚠⚠ The block-shape test ("are the 16-byte blocks VAG-shaped") CANNOT discriminate — it scores
#   99.5% on a sound bank AND 96.4% on a texture section, and was used once to publish a WRONG
#   refutation. What settles it is the zero-rate per byte position mod 16: position 1 is zero on
#   0.92-0.99 of blocks while every other position sits near 0.09 (a u16 array would make ALL odd
#   positions zero-heavy).
#   ⚑⚑ **The `.CLP` CONTAINER IS SOLVED AND ALL 227 SHIPPED CLUMPS PARSE — every family, not just
#   `_T`** (`Core/Formats/BrotherhoodOfSteel/BosClumpFile`, RE'd off the game's two loaders:
#   `0x0013F4A0` streaming, `0x0013F750` resident). LE. Header, 0x18 B: magic `CLMP` (the bytes
#   are `50 4D 4C 43` — CLMP only read LITTLE-endian) · `+4` zero · `+8` PAGE COUNT · `+12` the
#   **CRC-32 OF THE HASH-TABLE BYTES** (zlib CRC: poly 0xEDB88320 reflected, init -1, final
#   complement, built at run time by `0x0013F1F8`, taken by `0x0013F280`) · `+16` the entry count
#   `n` · `+24` an ASCII string table the game never reads. The HASH TABLE starts at
#   **`pageCount x pageLength`** and holds `1 << (1 + floor(log2(n + n/2)))` slots of **20** bytes
#   `(u32 tag, u32 startPage, u32 0 [runtime pointer], u32 size, u32 0)`; everything after it to
#   the 2048-aligned EOF is padding. Lookup: `idx = h & (slots-1)`, then `BosAssetHash.Rehash` for
#   at most 60 probes. Section offset = `startPage * pageLength`.
#   ⚠⚠ **THE PAGE UNIT IS THE LOADER'S, NOT THE FAMILY NAME'S**: 4096 B for the streaming loader
#   (the 54 `<lvl>_T.clp` plus the six CLUMP.DIR clumps va1/sfx/hud/movies/armor/sound), 256 B for
#   the resident one — 54 `<lvl>.clp`, 55 `<lvl>_S.clp`, 43 `pc\<char>\*.clp` and 15 more under
#   `DATA\` (global, global_s, inventry, inv_swap and the six character pairs).
#   ⚑ **The CRC DISCRIMINATES**: exactly one unit reproduces `+12` on each of the 227 clumps — 60
#   at 4096, 167 at 256 — never both, never neither. Gates, each **227/227**: populated slots ==
#   `+16`; and sorted by start page the sections form an UNBROKEN PAGE CHAIN from page 1 ending
#   EXACTLY at pageCount. (Re-measured independently 2026-09-07 straight off the ISO.)
#   ⛔⛔ **Three readings recorded HERE until 2026-09-07 were WRONG**, corrected above: (1) "the
#   last 2048 B are the SECTION TABLE" — the table is at `pageCount x unit` and is `slots x 20` B
#   (BAR_T's is 160 B); it merely ENDS inside the final sector on 196 of 227 (it STARTS inside on
#   only 80), which is why the reading looked settled. (2) "`+12` is a hash" — it is the CRC-32 of
#   the table, 227/227. (3) "`BAR.CLP`/`BAR_S.CLP` do not satisfy the size relation, so `+8` means
#   something else for those families and they are rejected" — same field, 256-byte pages; both
#   parse cleanly. ⛔ `len == pages*4096 + 2048` is NOT a family test either: it holds on 55 of
#   227 — the 54 `_T` **and SOUND.CLP** — and separates nothing. Use the CRC. Two further refuted
#   readings: field 2 as a "kind" (fits 50/50 — it is the START PAGE), and "sections tile in TAG
#   order" (summing page-aligned sizes is ORDER-INDEPENDENT, so that test could not show order at
#   all; 28/50).
#   ⚑ In a `_T` clump exactly ONE section is nothing but known `.SDB` hashes — the NAME TABLE,
#   54/54, 8,010 entries (`BosClumpFile.TryFindNameTable`). ⚠ Its PAGE VARIES: the four `WARE_*`
#   levels carry theirs LAST, so SEARCH the sections, never a fixed offset.
#   ⚠⚠ The string table has NO count and NO empty terminator: BAR_T follows its two paths with
#   `9B 8C 9D 8E 9F` then a UTF-16 `KERNEL32.dll`, which a naive ASCII walk reports as eleven
#   single-letter strings. The walk stops at the first non-printable byte — a heuristic bound.
#   ⚠ Section CONTENTS are typed by the `.DDF` field that references them (`0x00140548`: 1 mesh
#   `.vif`, 2 texture `.tex`, 3 sound `.vag`, 4 `.anm`, 5 level `.lmp`, 8 font `.fnt`) or by a
#   content probe (`BosTexture.IsTexture`, `BosSoundBank`); the mesh and animation payloads are
#   still undecoded.
#   ⚠ `archive list` prints only the first 100 of the disc's 352 files — NEVER use it for a
#   census (that error made every figure in an earlier BOS count wrong).
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
# Fallout 1/2 SAVE.DAT (Core/Formats/Fallout/FalloutSaveFile + FalloutSaveBody)
#   One BIG-endian header serves both games; +0x18 is 0x00010001 (FO1) or 0x00010002 (FO2).
#   ⚑ TWO oracles outside the file: the real-world date at +0x5B == the file's own mtime, and the
#     IN-GAME date at +0x65 == the game's published campaign start (FO1 5 Dec 2161, FO2 25 Jul 2241).
#   ⚠⚠ The in-game date is MONTH-then-DAY, the REVERSE of the real-world date. Only the FO2 save
#     settles it (its pair is 7 and 25; 25 cannot be a month) — FO1's 12/5 works either way round.
#   The in-game clock at +0x6B is 264,861 (FO1) and 315,657 (FO2). ⚠ Both were once published 512
#     low (0x0A9D transcribed as 0x089D) and the wrong value reached a test vector. The UNIT is not
#     established; the tail records step by 10 from 6 above it.
#   ⚠⚠ THE THREE FIXED-WIDTH NAME FIELDS (player +0x1D/32, save +0x3D/30, map +0x73/16) ARE READ TO
#     THE FIRST NUL, and whether the engine PADS or TERMINATES is NOT established — the retail bytes
#     cannot say, because every byte after the terminator is zero in all three fields of BOTH saves
#     (they end at 3/4/10 and 9/9/12). First-NUL is the CONSERVATIVE choice, not a measurement: it
#     agrees with the padded reading whenever the padding is NUL and never emits junk. ⛔ Do not
#     record it as measured (the doc did, while the code did the opposite thing) — a fixture whose
#     field carries junk after its NUL is what would settle it, and a test in each game watches for
#     exactly that. Contrast FalloutMapFile's name field, which CAVES.MAP's leftovers DO settle.
#   Preview thumbnail: 224 x 133 8-bit COLOR.PAL indices at +0x83, ending 0x74E3 in both games —
#     fixed by the file, not by arithmetic: exactly 131 NULs follow, ending one byte before the
#     globals array. ⚠ FO1's last 224-byte row is not picture (its last byte is 0); FO2's is.
#   ⚑⚑ Globals at 0x7567: N big-endian int32 in VAULT13.GAM declaration order, N = the .GAM's own
#     count (618 in FO1, from MASTER.DAT — not stated in the save). 618/618 equal its initials.
#   ⚠⚠ EXACTNESS is what pins that, NOT a score: 586 of the 618 initials are ZERO and zero is
#     endian-symmetric, so BE one byte off scores 566-586/618 (measured 571/566/586/572 at
#     0x7563/0x7566/0x7568/0x756B) and a LITTLE-ENDIAN read at the right offset scores 586/618. The
#     exact 2,472-byte BE pattern occurs at 0x7567 and 0x7F23 and nowhere else; the LE pattern
#     nowhere. ⛔ Do not say "a wrong reading scores near zero" — it survived a round in the test
#     file after being banned in the production doc, which is exactly how a weaker test gets in.
#   ⚠⚠ The array is stored TWICE and a START-OF-GAME SAVE CANNOT PROVE THAT — the control is the
#     MID-GAME FO2 save, where exactly 5 of 696 globals differ from patch000's VAULT13.GAM initials
#     (GVAR_PLAYER_REPUTATION 15, GVAR_TOWN_REP_ARROYO 65, …) and the block still appears twice.
#     Between the copies: the visited-map list (a count byte then that many NUL-terminated .SAV
#     names, which ARE the sidecars on disk; exactly one hit per file). ⚠ The count's FIELD WIDTH is
#     not settled, so the reader locates the list by its names.
#   ⚑⚑ Then a u32 = the UNCOMPRESSED AUTOMAP.SAV length: FO1 2,218 (plain file), FO2 10,449 (a
#     4,160-byte gzip that inflates to 10,449). ⚠⚠ Once recorded as REFUTED because FO2's automap
#     was measured on disk WITHOUT inflating it. INFLATE BEFORE YOU MEASURE.
#   ⚑ Player stats: 35 int32 at 0x8BF8 LOCATED BY CONTENT — eleven published derivations (HP
#     15+ST+2*EN, AP 5+AG/2, AC=AG, melee ST-5, carry 25*ST+25, sequence 2*PE, healing EN/3,
#     crit=LK, DR_EMP 100, rad 2*EN, poison 5*EN). ⚠ 38,200 candidate offsets (38,339 B, a 140-byte
#     window, bound at <= len-140), of which the primaries-in-1..10 pre-filter alone leaves TWO, so
#     the eleven separate 1 from 2 on FO1; on FO2 25 pass the pre-filter and ⛔ ZERO pass the eleven
#     — FO1-only until an FO2 fixture explains it.
#   ⚠⚠ .SAV SIDECARS: FO2 GZIPS THEM, FO1 DOES NOT. Route on the 1F 8B magic
#     (FalloutSaveFile.TryReadSidecarMap), never on the game or the extension. They are ordinary
#     Fallout MAPs (version 19 / 20) whose +4 name field equals their own file name. AUTOMAP.SAV is
#     not a map in either game and is refused on its leading dword — but it follows the same
#     compression rule as the maps beside it.
#   ⚠ +0x6F (35 / 4) is UNDECODED. FO2's DATA/MAPS.TXT gives [Map 004] map_name=arvillag against
#     that save's 4 and its ARVILLAG.sav — one agreeing point, so nothing is exposed. FO1 has no
#     oracle: no MAPS.TXT in MASTER.DAT, and FALLOUTW.EXE's only table naming V13ENT is the
#     city-entrance table, whose index 35 is HOTEL.MAP.
#   ⛔ Everything else is handed back as .Body: the byte at 0x7566, +0x61, the 35 zero int32 after
#     the stat block, skills/perks/inventory/party, and 19 twenty-byte tail records from 0x8ECC
#     (time, 3, small, PID family byte 4, 0) after which the STRIDE BREAKS. ⚠ "~89 records to EOF"
#     is refuted — 62 of those 89 have a zero first word and 3 bytes are left over.
#   Fallout Tactics saves (core\user\save\*.sav) — `Core/Formats/Tactics/TacticsSaveFile`.
#   ⚑⚑ A save is NOT one mission file, it is an ARCHIVE of the game's user/$$current$$ directory:
#     a '<saveh>' v2 header (u8 flag, FIVE wide strings — speech path, title, save name, mission
#     name, in-game date — EIGHT '<zar>' slots and SIX floats; 65,019 B on the retail fixture) then
#     '<campaign_save>' v1 = u32 count + per entry (wide path, u32 length, a whole file). Snake.sav
#     tiles 273,875/273,875 with two entries: mission01.sav 104,330 and save.cam 104,378.
#   ⚑ TWO STRING ENCODINGS SHARE ONE u32 PREFIX, split by BIT 31: set = UTF-16LE code units, clear
#     = the ASCII form the <esh> property bags use — both occur in the SAME file, so read the flag,
#     never assume (0x80000000 alone is an EMPTY wide string). ⛔ "u32 1 then a lone 0x80 byte" is
#     refuted by the second <saveh> inside the archive. ⚠⚠ The SAME flag governs a world's <mph>
#     team roster: 127 of the 128 shipped missions write those names ASCII and ONE
#     (core/editor/ambientExample.mis) writes them wide, so an ASCII-only roster reader looks right
#     on the whole campaign and silently returns an EMPTY roster on that file (fixed 2026-09-07).
#   ⚠ The '<world>' container IS the mission's (size twice + zlib) but the version is 70 (shipped
#     missions are 68/69) and the payload does NOT open with '<mph>', so TacticsMissionFile.Teams is
#     EMPTY on a save world: it opens with the mission path, <sgd> v5 (72 B), the briefing, <SSG> v1
#     (20 B), <entity_file> v3 (52 class names) and a 12-byte entity-list header, first <esh> at
#     4,820. The roster comes from the world's '<Team>' v2 chunks instead
#     (TacticsMissionFile.ScanTeams — a SCAN of the undecoded entity section, run lazily): the
#     save's nine equal mission01.mis's nine by name and flag, wide in the save, ASCII in the mission.
#   ⚑⚑ A ZLIB STREAM'S END IS NOT CHECKED BY INFLATING IT. .NET's ZLibStream stops at the end of the
#     deflate data and silently ignores trailing bytes, so "the stream consumes the file exactly" is
#     NOT proved by a successful inflate that matches a declared size. The trailing big-endian u32 IS
#     the Adler-32 of the inflated data (RFC 1950), so compare it: TacticsMissionFile.TryParse does,
#     and 129/129 real worlds pass with zero unused bytes. This trap generalises to every container
#     that hands a reader a zlib payload.
#   ⚑ THE MISSION CORPUS IS 128 SHIPPED FILES: 103 inside the seven mis-*.bos / Mis-Main_0.bos
#     archives PLUS 25 LOOSE on disk (12 campaign under core/campaigns/missions/core,
#     core/editor/ambientExample.mis, 12 multiplayer under core/missions) — versions 68 x98 / 69 x30
#     (87/16 archived, 11/14 loose), so the loose files are not a version subset and "every shipped
#     mission" must include them. ⚠⚠ Windows file patterns are CASE-INSENSITIVE: enumerating
#     "mis*.bos" and then "Mis*.bos" returns the SAME seven archives, and counting that list without
#     de-duplicating is how a bogus "206 missions / 207/207" figure reached shipped source comments
#     on 2026-09-06 (2x the truth in both version terms). Count once, and say which population.
#   ⚠⚠ The briefing text is NOT byte-verbatim in loc-mis_A.bos's MIS_01_Speech.txt — the shipped
#     file wraps lines and writes paragraph breaks as literal \n escapes; only after dropping the
#     escapes and layout whitespace do the save's 1,431 non-blank characters match its MISSION_BRIEF
#     block. ⛔ Do not assert raw containment: it fails after 308 of 1,732 characters.
#   ⚑ save.cam = '<campaign>' v21 (distinct from the shipped bos.cam v19): 65x34 grid, 95 CVARs,
#     27 locations + 30 special encounters as <esh> bags, 109 '<random_force>' v2, 232 stock rows,
#     66 recruits. ⚠ 'varTableHeader' NUL '1' NUL is a BRACKETLESS tag — match it literally.

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
                                                          #   (defaults: Sample/Full_360_Builds, Sample/MemoryDumps)
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
| SampleGenerator | Build `Sample/Builds` from the private media collection (ported from NeversoftMultitool) | `dotnet run --project tools/corpus/SampleGenerator -- --dry-run` |
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

⚠⚠ **RESTRUCTURED 2026-09-07.** `Sample/Full_Builds/` is GONE — every build moved into
`Sample/Builds/` under a computed name, and the non-build fixtures were regrouped. Generated and
regenerated by `tools/corpus/SampleGenerator` (see its README). Notes written before that date use
the old paths.

```
Sample/
├── Builds/                 # THE CORPUS: game files only. 33 builds / 112.8 GB, nothing else
│   └── Game Name (yyyy-M-d, Platform - Kind)/
├── Media/                  # original disc images + packages, per build (33.7 GB)
├── ReverseEngineering/     # binaries staged for Ghidra, COPIED from tools/GhidraProject (115 MB)
│   └── Fallout - New Vegas (PC)/FalloutNV_runtime_image.bin   # ⚑ the DRM-FREE dump
├── Catalog/                # generator output, kept OUT of Builds/
│   ├── catalog.json/.md    # census + the deliberate-exclusion list
│   └── builds/<name>.json  # per build: sources, date provenance, notes, measured exe timestamp
├── ESM/                    # Individual ESM files
│   ├── 360_final/          # Xbox 360 final ESM
│   ├── 360_proto/          # Xbox 360 prototype ESM
│   ├── fallout_3/          # Fallout 3 ESM
│   └── pc_final/           # PC final ESM
├── MemoryDumps/            # Xbox 360 crash dumps          (was MemoryDump/)
├── DebugSymbols/           # exe + .pdb/.map per title, COPIED from the builds (4.8 GB)
│   ├── Fallout - New Vegas (X360)/                         (was PDB/, the cvdump trees)
│   ├── Fallout 4 (PC)/                                     (was Sample/Fallout 4/)
│   ├── The Elder Scrolls IV - Oblivion Remastered (PC)/    (was Sample/Oblivion Remastered/)
│   └── The Elder Scrolls V - Skyrim (PC)/                  (was Sample/Skyrim/)
├── Meshes/                 # Extracted Meshes BSAs
├── Textures/               # Extracted Texture BSAs
├── Reference_Code/         # Source code from useful projects
├── Saves/                  # Save game files from 360 prototypes (for save decode testing)
├── TCRF/                   # Reference documentation for article writing
└── Unpacked_Builds/        # Full game data with BSAs extracted
    ├── 360_July_Unpacked/
    └── PC_Final_Unpacked/
```

⚠⚠ **A build directory holds the unpacked game tree and nothing else.** Disc images, floppy
images and release packages are expanded into the build and then moved to `Sample/Media/<build>/`;
symbols and executables are COPIED (originals stay) to `Sample/DebugSymbols/<build>/`; the
generator's manifests live in `Sample/Catalog/`.

⚠⚠ **Media is DECLARED per file in the catalog, never detected by extension.** An extension sweep
would gut three builds: the Battlespire and Redguard Steam installs each ship a raw-sector `.bin`
CD image that DOSBox mounts (484 MB / 709 MB, both passing a sync-pattern test), and Daggerfall's
hundreds of `.IMG` files are its TEXTURE format. The only genuine disk-image set is Arena's eight
`.ima` floppies.

⚑ **A container is unpacked; a game asset is not.** An installer `.CAB` holds the game's assets, so
it is expanded (Redguard's `DATA1.CAB` → `Disc 1 (Install)/extracted/`, cabinet left in place). A
`.BSA`/`.BA2`/`.DAT` IS a game asset the engine reads, so it stays packed — 72 GB across the corpus,
and `Sample/Unpacked_Builds/` already holds loose trees for the two builds that need them.

⚠⚠ **7z reads the Xbox 360 XGD image as `Udf` and returns the WRONG partition** — 13 files / 90 MB
instead of the game partition at `0xFD90000`. It succeeds while being wrong, so both XGD images are
`KeepPacked`. ⚠ 7z also exits 2 ("Headers Error") on the PS3 UDF Blu-ray while extracting every
file, so success there is judged by comparing the extracted count against the listing.

⚠⚠ **`Steam` and `PC` are DIFFERENT platforms here.** A Steam install is not a dated release — it
is whatever the depot last shipped, and it changes under you (Skyrim Special Edition was patched
2026-08-31; the four classic re-releases were repackaged the same day). So a Steam build is dated
from Steam's own `appmanifest_*.acf` `LastUpdated`, with `buildid` in its manifest, and the
generator re-reads both every run and flags drift. Retail media keeps `PC`.
⚠ An executable's COFF stamp is when it was COMPILED, not what shipped beside it — Oblivion stamps
2007-04-16 against a 2022-06-18 depot. ⚠ More than one appmanifest can name one install directory
(this machine has both `Fallout New Vegas` and `fallout new vegas`), so the LATEST wins.

⚑ **No build carries a user modification** (verified 2026-09-07 after fresh redownloads). Two
Steam releases BUNDLE extras and `Catalog/catalog.md` reports those separately: Fallout and
Fallout 2 ship a root `ddraw.dll` DirectDraw wrapper, Fallout 2 also `sfall-readme.txt`. ⚑ Proven
shipped, not user-added: a completely fresh redownload still carried them at identical buildids.
That is why the **original Interplay CDs are separate builds** — they are the only true vanilla
reference for the isometric Fallouts.

⛔ **Two mod markers that CANNOT discriminate, both learned the hard way:**
- The **LAA bit** — every 64-bit exe sets it and some 32-bit builds ship it (Fallout 3's 2021
  repack, Skyrim 1.9). Flagging it marked three vanilla builds. Recorded, never treated as a mod.
  It only settled New Vegas as a direct A/B against that title's own clean install (0x0122/0x0102).
- **`ddraw.dll` by name** — the 1997/1998 Interplay CDs ship the bundled DirectX 3/5 redistributable
  at `DIRECTX3\DIRECTX\DDRAW.DLL` (161,280 / 270,848 B), a different file from the Steam game-root
  wrapper (90,112 / 393,216 B). ⚑ The discriminator is LOCATION: root-only, not a tree search.

⚑ New Vegas was re-sourced from Steam 2026-09-07 after the staged copy was found carrying xNVSE and
an LAA-patched exe while its ESM and BSAs were already byte-identical to vanilla — the kind of
difference that invalidates a parity measurement while every data file checks out.

⚑⚑ **The New Vegas PC retail DVD is a STEAM retail disc, and its payload DECRYPTS** (solved
2026-09-08 — earlier notes calling it unopenable are superseded). It ships `SteamService.exe`,
`SteamInstall_English.msi` and a `.sis` naming appID 22380 + its depots; the five `.sid` files
(6.38 GB) are AES-256-CBC Steam depot data. All **431 files extract exactly**, giving the only
true October-2010 **1.0** tree in the corpus — `Data/FalloutNV.esm` at 245,491,701 B and a
`FalloutNV.exe` (16,397,640 B, COFF 2010-09-16) that exists on **no** Steam depot. Platform token
is `Steam Disc`, not `PC`. Reader: `Core/Formats/Steam/` (see the `archive` section above).
⚠⚠ **The build directory holds ONLY the 431 decrypted game files; the installer — `.sim`, the five
`.sid`, `Setup.exe`, `resources/` — is the PACKAGE and lives in `Sample/Media/<build>/disc/`**,
beside the ISO it came from. Do not put them back: a `.sid` next to the game tree is what
"contaminated build" looks like.
⚠ Extraction needs the per-depot **legacy 16-byte** key, read from `depot_keys.txt` beside the
`.sim` and deliberately NOT compiled into this repo. Listing needs no key at all.

⚠ **Fallout Tactics Disc 3 is `MODE2/2352`** while discs 1 and 2 are `MODE1/2352` — its ISO 9660
descriptor is at sector offset **+24, not +16**, and a MODE1 reader reports "no CD001" as if the
disc were corrupt.

Build naming is `Game Name (yyyy-M-d, Platform - Kind)`, matching the sibling NeversoftMultitool
corpus; a build with no defensible date drops it (`Game Name (Platform - Kind)`). ⚠ **Dates are
MEASURED** — ISO 9660 volume descriptors, COFF header timestamps, or the leak's own label — and each
`build.json` says which. Two measurements are JUNK and are not used: `Morrowind.exe` stamps
`2030-10-02` and the Brotherhood of Steel Xbox disc's XDVDFS descriptor reads `1601-01-03`; both
entries fall back to the documented release date and say so. A self-test rejects any catalog date
outside 1993-2027.

⚠ PC titles after Skyrim Special Edition are DELIBERATELY excluded (Fallout 4, Fallout 76,
Starfield, Oblivion Remastered — 403 GB). They stay installed and resolve through the test suite's
Steam probes. The exclusion is listed in `catalog.md`, not silently absent.

⚑ Tests still written against `Sample/Full_Builds/…` keep working: `SampleCorpus` (in
`tests/…/Helpers/`) reads the `LegacySources` recorded in `Catalog/catalog.json` and rewrites the
old path onto its new home, so the rename table is never duplicated. Prefer the new path in new
code. ⚠ The rewrite maps onto `Sample/Builds/` only — a legacy path naming original media (a JAR,
a disc image) now lives under `Sample/Media/`, and `RealAssetPaths` names those directly.

### Full Game Builds (for rendering — needs BSAs + textures)

- **Xbox 360 final**: `Sample/Builds/Fallout - New Vegas (2010-10-19, X360 - Final)/Data/`
  (the XGD2 disc image sits beside it in `disc/`; there is no XDVDFS reader here, so it is kept, not expanded)
- **Xbox 360 Aug 2010**: `Sample/Builds/Fallout - New Vegas (2010-8-22, X360 - Prototype)/Diskuild_1.0.0.252/Data/`
- **Xbox 360 July 2010**: `Sample/Builds/Fallout - New Vegas (2010-7-21, X360 - Prototype)/FalloutNV/Data/`
- **PC final**: `Sample/Builds/Fallout - New Vegas (2022-5-24, Steam - Final)/Data/` (patch 1.4)
- **PC retail DVD**: `Sample/Builds/Fallout - New Vegas (2010-9-16, PC - Final)/` (ISO9660, mountable in place)
- **PC install**: `E:\SteamLibrary\SteamApps\common\Fallout New Vegas\Data\`

### Classic Game Installs (pre-Morrowind catalog)

All under `E:\SteamLibrary\SteamApps\common\`. Data root is what `ClassicGameLocator`
detects; pass either it or the install root to `stats`/`archive`/`sprite`.

| Game | Install subdir | Data root | Notes |
|---|---|---|---|
| Arena | `The Elder Scrolls Arena` | `ARENA` | `GLOBAL.BSA` has NO magic — probed by exact directory tiling |
| Daggerfall | `The Elder Scrolls Daggerfall` | `DF\DAGGER\ARENA2` | ⚠ NOT `Daggerfall` — the Steam folder is the full title. `DFCD` beside it IS the retail 1.0 CD (Build #165, md5-identical to the redump minus 2 files) and is NOT recognised as an install — its ARCH3D.BSA is imploded inside PACKED.DAT |
| Battlespire | `An Elder Scrolls Legend Battlespire` | `GAMEDATA` | ⚠ NOT `Battlespire` — the Steam folder is the full title. `3D.BS6` is a BSA despite the extension |
| Redguard | `The Elder Scrolls Adventures Redguard` | `Redguard` | ⚠ NOT `Redguard` — the Steam folder is the full title (the DATA root inside it IS `Redguard`). Movies + music live only inside the CUE/BIN CD image |
| Fallout | `Fallout` | install root | Loose `DATA\` overrides the DATs; install is MODDED (Hi-Res patch) |
| Fallout 2 | `Fallout 2` | install root | Precedence: loose > `f2_res` > `patch*` > `critter` > `master`; MODDED (sfall + Killap UP) |
| Fallout Tactics | `Fallout Tactics` | `core` | 40 `.bos` archives, all plain PKZIP |

Because the two Fallout installs are modded, real-asset tests assert **structure only**,
never content counts. ⚑ SAVES EXIST for all five classic titles since 2026-09-07 (the user played
each): resolve them through `RealAssetPaths.Classics.{Fallout1,Fallout2,Daggerfall,Battlespire}SaveSlot()`
and `FalloutTacticsSave()`. ⚠ Daggerfall and Battlespire ship EVERY `SAVEn` directory and an unplayed
one holds only a Steam autocloud stub — probe for `SAVETREE.DAT`, never for the directory. ⚠
Battlespire's `SAVEn` sit under the INSTALL ROOT, not under `GAMEDATA`; Arena's saves are loose files
(`SAVEGAME.NN`, `SAVEENGN.NN`) in its data root.
⚠ The Fallout 1 and Fallout 2 SAVE slots resolve from the STAGED BUILDS, not only from Steam:
  Sample/Builds/Fallout (2023-6-13, Steam - Final)/DATA/SAVEGAME/SLOT01 (SAVE.DAT 38,339 B) and
  Sample/Builds/Fallout 2 (2022-7-1, Steam - Final)/data/SAVEGAME/SLOT01 (62,871 B).
  RealAssetPaths.Classics.Fallout1SaveSlot()/Fallout2SaveSlot() probe the install first and the
  staged build second. ⛔ Both live Steam DATA\SAVEGAME trees emptied themselves on 2026-09-07
  (only SAVEGAME — art/maps/proto survived — which reads as a cloud sync); an empty install is
  NOT a missing fixture, and 15 retail tests were left unverified for a day on that assumption.

Original media is now a first-class part of the corpus (measured 2026-09-07; details on the Arena
and Daggerfall boards under "Original media"): `The Elder Scrolls - Arena (1994-10-18, PC - Final)`
(the v1.07 CD — its Arena data is
BYTE-IDENTICAL to the current Steam root, which Steam's 2026-08-31 depot update populated from it;
the disc adds only two non-Arena demos — so there is no separate Steam entry),
`The Elder Scrolls - Arena (PC Floppy - v1.04)` (8 FAT12 images; the installer
container is solved — split 96-byte directory + one LZHUF block stream — and the v1.04 `A.EXE` is a
second exe build with identical string pools at shifted offsets) and
`The Elder Scrolls II - Daggerfall (1996-9-6, PC - Final)` (the 1.0 press,
Build #165: `ARCH3D.BSA` + `DAGGER.SND` exist only imploded inside `PACKED.DAT`, and `DF\DFCD` is
this disc minus two files).

The five TES Travels titles are NOT Steam installs — each is its own build directory under
`Sample/Builds/` and resolves in tests through `RealAssetPaths.Travels.*`:

| Game | Build directory | Root | Notes |
|---|---|---|---|
| Stormhold | `The Elder Scrolls Travels - Stormhold (J2ME - Variants)` | the .jar itself | BIG-endian; 9 loose `.dat` tables, 37 `.cus`, 16 standard PNG; 3 JARs + the release zip |
| Dawnstar | `The Elder Scrolls Travels - Dawnstar (J2ME - Variants)` | the .jar itself | BIG-endian; tables in `datfiles.lmp`, 43 PNG in `imgfiles.lmp` |
| Shadowkey | `The Elder Scrolls Travels - Shadowkey (N-Gage - Final)` | `system\apps\6R51` | **LITTLE-endian**; 21 zones x 12 per-zone formats |
| Oblivion mobile | `The Elder Scrolls Travels - Oblivion (J2ME - Final)` | the .jar itself | BIG-endian; `.jtm`/`.cml`/`.scr` + `lang_N.txt` |
| Oblivion PSP | `The Elder Scrolls IV - Oblivion (PSP - Prototypes)/<build>` | `PSP_GAME\USRDIR` | cancelled; 7 dated UMD builds; one `GR.ARC` pack each |

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

- **PDB symbols**: `Sample/DebugSymbols/Fallout - New Vegas (X360)/`
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

### A green build can still leave bin\ stale

⚠⚠ A GREEN build can still leave bin\ STALE. When a sibling agent's test or CLI process holds
bin\Release\net10.0\BethesdaMultitool.Tests.exe (or BethesdaMultitool.dll/.exe), MSBuild degrades
the copy to *warning* MSB3026 ("The file is locked by: BethesdaMultitool.Tests (<pid>)") and can
still print "Build succeeded, 0 Errors" while the old binary stays in place — deliberately wrong
assertions then PASS. Checking the DLL's mtime is NOT enough: COUNT the MSB3026 lines in the
captured build output and compare obj\ against bin\ timestamps (and bin\BethesdaMultitool.dll
against src\...\bin\BethesdaMultitool.dll) before believing a test run.
⚠ Capture build.ps1 to a FILE, not through a pipe. `build.ps1 ... | tail -n` buffers until the
process exits, so a build that is queued on the machine-wide mutex (observed at ~50 minutes on
2026-09-07) looks like a hung command with an empty log; redirecting to a log file shows the
"Waiting for machine-wide build lock..." line immediately.
⚠ Run `tools/scripts/build.ps1` under `pwsh` (PowerShell 7), not `powershell`: the latter is Windows
PowerShell 5.1 here and the script fails on `[SHA256]::HashData` before it compiles anything.

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

**A control must be able to DISCRIMINATE** — two lessons from the Tactics work (2026-09-07): (a) "the walk
consumed the array exactly" on a variable-stride walk is nearly free — 14 of 16 candidate stride tables
passed it, the do-nothing table included — so it is a consistency check, never a falsifier, unless the
mis-stride would run past the end; (b) a wrong field SHAPE that spans the same number of bytes is invisible
to exact tiling — Tactics' v8 tile flags are throwaway+u8, where a u16 reader stays in step and merely reads
100 instead of 0. Quote "desynchronised" and "silently mis-valued" as SEPARATE numbers; folding them
overstates what a tiling test can see.

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
