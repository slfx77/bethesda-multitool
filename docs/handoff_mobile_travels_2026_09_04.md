# Handoff — TES Travels / mobile block (C4b–C4f)

Written 2026-09-04 for a second session taking the mobile games off the classic-catalogue program.
The originating session keeps Redguard (C4), the Fallout block (C5–C7) and the GUI track (G3–G8).

**Your scope, five titles:**

| # | Title | Platform | Fixture |
|---|---|---|---|
| C4b | TES Travels: Stormhold (2003) | J2ME / N-Gage | `test_stormhold_176x208_eng.jar` |
| C4c | TES Travels: Dawnstar (2004) | J2ME | `test_dawnstar_176x208_eng.jar` |
| C4d | TES Travels: Shadowkey (2004) | N-Gage (Symbian/ARM) | `The-Elder-Scrolls-Travels-Shadowkey_N-Gage_EN.zip` |
| C4e | TES Travels: Oblivion — mobile (2006) | J2ME | `oblivion-repaired.jar` |
| C4f | TES Travels: Oblivion — PSP (cancelled) | PSP UMD ISO | `Elder Scrolls travels game files.zip` → `Oblivion mobile betas/` |

All fixtures verified present on 2026-09-04 (then under `Sample/Full_Builds/`; migrated to `Sample/Builds/` on 2026-09-07). The governing plan is
`~/.claude/plans/i-d-like-to-begin-tingly-cat.md`; these five are in its "SCOPE CORRECTION —
extended catalogue" section, which is the authoritative statement of the 13-title goal.

---

## 1. Read these first

- `CLAUDE.md` — build/test commands, the classic CLI surface as it stands, test discipline.
- The plan file's Track 3 (**C0 foundations**) — the seams you will plug into already exist and
  are described there. Do not re-derive them.
- `docs/backlog/game-daggerfall.md` and `game-battlespire.md` — the two completed verticals. They
  show the expected depth and the "documented-unknown stop" convention.
- Project memory `classic_backlog_program_2026_08_31.md` and `classic_catalogue_full_scope.md`.

## 2. Licence gate — this block is harsher than the others

⛔ **Every reference for these five titles is clean-room only.** Checked via the GitHub API on
2026-09-04:

| Resource | Licence | Verdict |
|---|---|---|
| `Fire-Head/TESTShadowkeyMapViewer` (JS) | **none** | ⛔ no porting — ideas only |
| `fire-head.github.io/testshadowkeyviewer` | hosted build of the above | ✅ **oracle** — compare our output against it |
| `bigianb/frostbite`, `bigianb/jbgda` | none / GPL-3.0 | ⛔ (they belong to C7b anyway) |

The completed verticals could port DaggerfallConnect, OpenTESArena and battlespire-tools. You get
none of that. Everything must be written from prose specs plus bytes you verify locally, with the
hosted Shadowkey viewer used only to check results.

⚠ `oblivion-repaired.jar` contains **21 `.java` files that are decompiler output of the game's own
code**, duplicated at two paths (`class_1.java` and `oblivion/class_1.java`, near-identical sizes).
Read them the way this repo reads its Ghidra output — as a behaviour oracle — and never paste from
them. They are Bethesda/Vir2L's code, not a licensed reference.

## 3. Coordination — what is yours, what is mine, what is shared

Concurrent sessions on this repo collide on a known set of files. See project memory
`concurrent_session_edit_hazards.md` before any commit round.

**Yours outright:** `Core/Formats/Travels/` (or per-game dirs under it), your CLI arms, your tests,
`docs/backlog/game-{stormhold,dawnstar,shadowkey,oblivion-mobile,oblivion-psp}.md`.

**Mine right now:** `Core/Formats/Redguard/`, `Core/Formats/Archives/RedguardRob*`, the Fallout
dirs when they land, `App/Tabs/AssetBrowser/`, `Core/AssetBrowse/`, `Core/Rendering/Level2D/`.

**Shared — announce before editing:**
`Core/Games/BethesdaGame.cs`, `Core/Games/GameProfiles.cs`, `Core/Games/ClassicGameLocator.cs`,
`Core/Formats/Classic/ClassicGameAnalyzer.cs`, `Core/Formats/Archives/ArchiveProbe.cs`,
`Core/Formats/FileTypeDetector.cs`, `CLAUDE.md`, `MEMORY.md`.

Registration rules that the test suite enforces:
- Append `BethesdaGame` members **after** `FalloutTactics`. Never reorder — persisted values.
- A new enum member and its `GameProfiles` entry must land in the **same commit**;
  `Registry_CoversEveryEnumValueExceptUnknown` fails otherwise.
- Classic profiles use `Engine = EngineFamily.None`, `RecordHeaderSize = 0`, `GroupHeaderSize = 0`,
  `HasRecordVersionTrailer = false`, plus `InstallMarkers`. Copy the `BethesdaGame.Redguard` entry
  (`GameProfiles.cs:281`) as your template.

### Reserved synthetic-FormID domains — take these, they are yours

`ClassicFormIdScheme.Compose(domain, index)` builds `(domain << 24) | stableIndex`. Domains 0x00 and
0xFF are rejected by the scheme. In use today: Arena `0x01–0x04`, Daggerfall `0x10–0x19`,
Battlespire `0x20–0x22`, Redguard `0x30`.

| Range | Title |
|---|---|
| `0x40–0x43` | Stormhold |
| `0x44–0x47` | Dawnstar |
| `0x48–0x4B` | Shadowkey |
| `0x4C–0x4E` | Oblivion mobile |
| `0x4F` | Oblivion PSP |

`0x50` and up is reserved for the Fallout block — do not take it. The index must come from **source
identity** (a zone name hash, a lump-directory position, a table row id), never enumeration order,
so a diff between two builds stays meaningful.

### One shared component to build — please own it

Both tracks need a **plain-PKZIP archive backend**: your J2ME JARs are PKZIP, and Fallout Tactics'
`.bos` archives are verified plain PKZIP too. Build `Core/Formats/Archives/ZipArchiveBackend.cs`
behind the existing `IArchiveBackend` seam and I will consume it for C7 rather than writing a
second one. Contract to honour (from `IArchiveBackend`'s doc comment): immutable after construction,
and every member safe for unsynchronised concurrent use — so extract with a per-call
`FileStream` + `DeflateStream`, because `ZipArchive` is not concurrency-safe.

Add it to `ArchiveProbe.Open` **after** the exact-arithmetic classic probes and before the BSA
fallback. `PK\x03\x04` is a strong magic, so placement is not delicate, but the chain's rule stands:
every probe is exact, never fuzzy.

## 4. Measured footholds — start from these, they are verified bytes

Everything below was measured on 2026-09-04 from the fixtures named above. Independent Python walks;
no code written against them yet.

### ⚠⚠ Endianness splits the block in two

- **J2ME (Stormhold, Dawnstar, Oblivion mobile) is BIG-endian** — Java's `DataInputStream` is BE and
  the data files follow. `npcstrings.dat` opens `00 00 00 03 | 00 36 | "Welcome! Please peruse…"` =
  BE u32 record count, then per record a BE u16 length and its text.
- **Shadowkey (N-Gage, Symbian on ARM) is LITTLE-endian.** Proven across its per-zone files: zone
  counts read as 0/1/3/5/8/14/17/27/31/34 little-endian and as 0/256/768/1280/2048/3584/4352/6912/
  7936/8704 big-endian.

Getting this backwards costs days. It is the first thing to pin in each reader's doc comment.

### C4c Dawnstar — the `.lmp` container is already solved

`imgfiles.lmp` (87,961 B) and `datfiles.lmp` (11,217 B) are text-delimited directories:
`-<filename>-` followed by a 6-byte record = **BE u32 offset + BE u16 length**. Payloads tile
contiguously; verified on the first entries of both lumps:

```
-baglarge.png-  00 00 03 AC  01 F3     offset    940, length 499
-bagmid.png-    00 00 05 9F  01 22     offset   1439, length 290    (940 + 499 = 1439 ✓)
-charin.dat-    00 00 00 AD  04 EC     offset    173, length 1260
-droppeditemsin.dat- 00 00 05 99 00 DB offset   1433, length  219   (173 + 1260 = 1433 ✓)
```

Make the exact-tiling walk the probe, exactly as `XnGineBsaParser` and `RedguardRobParser` do.

⚑ **Dawnstar and Stormhold almost certainly share their data-table formats.** Stormhold ships
`charin.dat` loose at **1,260 bytes**; Dawnstar's lump declares `charin.dat` at **1,260 bytes**.
Write one table reader and serve both games — that is the cheapest win in this block.

### C4b Stormhold — loose tables and a custom sprite format

13 `.class`, 16 `.png` (**standard PNG — no decoder needed**), 37 `.cus` (179,961 B total; the
custom sprite/animation format, and the only real decode job), and 9 `.dat` tables whose names give
the record types away: `charin.dat`, `itemsin.dat`, `spellsin.dat`, `monstersin.dat`,
`monsterfilenamesin.dat`, `droppeditemsin.dat`, `geo*`, `npcstrings.dat`.

Suggested order: `.dat` tables → records (`stats`/`list`/`show` light up immediately) → `.cus`
sprites → `sprite render`.

### C4e Oblivion mobile — the easiest of the five

32 `.scr` (scripts), 26 `.png` (**standard PNG again**), 21 `.cml`, 17 `.jtm` (tile maps, by name
and by the `l01_1`/`l03_1` level-stem pattern), 15 `lang_N.txt` (localisation, plain text), and
`eso.ver`. Level stems `l01`…`l12` line the `.scr`/`.cml`/`.jtm` families up one-to-one, so the
level is the natural record unit.

### C4d Shadowkey — the big one: 21 zones × 12 formats

1,967 entries. Per-zone families, all 21 stems complete in all 12:

| Ext | Total | Shape measured |
|---|---|---|
| `.ztx` | 2,790,619 B | largest — textures |
| `.zlu` | 982,656 B | 40,871–47,708 B, only 7 distinct values — a near-fixed lookup table |
| `.zmp` | 612,746 B | 21 distinct — the map |
| `.ent` | 594,660 B | 21 distinct — entities |
| `.zcp` | 422,669 B | 21 distinct |
| `.zfg` | 365,424 B | 17,188–18,070 B, 5 distinct — near-fixed table |
| `.zsk` | 151,831 B | 7 distinct — sky |
| `.zon` | 18,474 B | u16 LE count, then u16 fields |
| `.pal` | 16,128 B | **exactly 768 B every time** |
| `.sur` | 2,829 B | 57–217 B, byte count + two bytes, variable records |
| `.stn` | 1,407 B | u16 LE count then ASCII names; 2 B when the count is 0 |
| `.pth` | 1,206 B | u16 LE count then ASCII names; 2 B when the count is 0 |

Plus 1,533 `.s` (1.06 MB), 70 `.txt` (191 KB), 38 `.wav`, 6 `.ogg` (1.6 MB music), 19 `.dll` + 1
`.exe` (Symbian ARM binaries — your decompile targets if a format resists).

Two immediate wins:
- ⚑ **`.pal` needs no new code.** 768 bytes = 256 × 3, which is the raw-`.PAL` path
  `Core/Imaging/Palette` already has, including the range sniff that promotes 6-bit VGA components
  (all ≤ 63) and leaves 8-bit ones alone. Pin it with a test rather than writing a decoder — that is
  what was done for Redguard's `.COL`, see `RedguardRetailTests`.
- ⚑ **The 2-byte files are the format telling you its framing.** `.pth` and `.stn` are exactly 2
  bytes on the zones that have none, so both are u16-count-prefixed lists. `.pth` bodies also
  divide evenly by 4 (`326-2 = 324 = 81 × 4`, `318-2 = 316 = 79 × 4`, `254-2 = 252 = 63 × 4`), but
  the bytes after the count are ASCII (`"UmbraK…"`, `"resi…"`), so treat the stride as a lead and
  confirm by walking names, not by arithmetic alone.

The 21-way regularity is the lever for the whole game: any hypothesis that does not hold for all 21
zones is wrong. Use that as the acceptance test the way the other verticals used exact tiling.

### C4f Oblivion PSP — staging first

`Elder Scrolls travels game files.zip` (703,910,184 B) holds `Oblivion mobile betas/` = **6 dated
PSP ISOs** (`iND-TESTO090606.iso` … Jun 2006 → Apr 2007) plus a `Modified` set.

⚠ Do **not** write another ISO extractor. Since 2026-09-09 the corpus is built by the sibling
`C:\Users\mmc99\source\repos\CorpusTool` repo from `tools/corpus/corpus.json` (each disc declared,
stored as CHD, expanded through 7z or this repo's own `archive extract`; the six PSP UMDs are already
in). Stage the extracted trees under
`Sample/Builds/` and only then start format work. Six dated builds means this title also has
cross-build diff value once anything parses.

## 5. Repo conventions you must follow

- **No partial classes.** File-scoped namespaces, `_camelCase` private fields, nullable enabled,
  `Async` suffix, braces on control flow.
- **Logic goes in `Core/`.** `App/**` is `Compile Remove`d from the `net10.0` TFM, so anything in
  `App/` is unreachable from tests. Source-contract tests are a last resort, not a first move — see
  the CLAUDE.md section on that.
- **Tests are synthetic by default.** Real-asset tests are opt-in behind `RUN_BUCKET_B=1` via
  `BucketBTestGuard.SkipUnlessEnabled()`, and must carry BOTH the guard and
  `[Trait("Category", BucketBTestGuard.Category)]` plus
  `[Collection(SequentialIntegrationGroup.Name)]`. A guard without its trait is silently skipped by
  targeted runs, and `TestCategoryConsistencyTests` enforces the pairing.
- **A test must be able to fail.** Never `return;` on a missing fixture — that records a pass. Use
  `Assert.SkipWhen` / `Assert.SkipUnless`.
- Resolve fixtures through `tests/Helpers/RealAssetPaths.cs`; add a `Travels` group there rather
  than hardcoding paths.
- Build: `dotnet build -c Release -p:BuildTestsOnly=true -p:SkipAnalyzers=true` (~25 s) while
  iterating, but a **full build of both TFMs before reporting anything green** — `BuildTestsOnly`
  skips the WinUI TFM. Analyzers are skipped by that flag too, so do one analyzer-enabled build
  before you call a batch done.
- Work stays **uncommitted** unless the user asks. "Commit" means local only — never push.

## 6. Definition of done, per the plan

A game is done when all four demonstrate: `stats <root>` returns synthesized records,
`archive list <archive>` lists its container, `sprite render` produces PNGs verified by eye, and the
GUI Asset Browser opens it. Add a `docs/backlog/game-<name>.md` board (**open work only** — that is
the convention; finished items move to a Done section), and record documented-unknown stops
explicitly rather than leaving them implied.

For records: synthesize `GenericEsmRecord`s through a new arm in
`Core/Formats/Classic/ClassicGameAnalyzer.cs`'s switch (`:53`), matching
`RedguardRecordSource`/`DaggerfallRecordSource`. That one hook is what makes `stats`, `list`, `show`
and `diff` work for a game with no plugin file.

## 7. Do not re-derive

- The endianness split in §4. It is measured, not inferred from platform folklore.
- `.pal` at 768 bytes is the existing shared palette path. Do not write a Shadowkey palette decoder.
- The `.lmp` directory layout. It tiles exactly on both Dawnstar lumps.
- Standard `.png` in Stormhold and Oblivion mobile. Do not write an image decoder for those two.
- The ISO extractor. It exists and is the user's own tool.
