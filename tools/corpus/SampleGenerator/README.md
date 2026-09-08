# SampleGenerator

Builds the repository's gitignored `Sample/Builds` corpus from a private media collection, and
regroups the non-build fixtures under `Sample/`.

Ported from the sibling [NeversoftMultitool](https://github.com/slfx77/NeversoftMultitool)
`tools/corpus/SampleGenerator` (MIT), which solves the same problem for that project's console
builds. Naming, path-safety handling and the mirror/repopulate model come from there; the catalog,
the multi-part build shape and the migration are specific to this collection.

## Naming

```
Sample/Builds/Game Name (yyyy-M-d, Platform - Kind)/
```

matching the sibling corpus, so the two read alike. `Kind` is `Final`, `Prototype`, `Prototypes`,
`Demo`, `Beta` or `Variants`. A build with no defensible date drops it and reads
`Game Name (Platform - Kind)` — the convention that repo already uses for undated cartridge and
mobile variants.

The name is *computed* from the catalog fields by `SampleGeneratorCatalog.DirectoryName`, so the
convention lives in one function: change it there and the whole corpus renames consistently on the
next run.

### Dates are measured, not assumed

Every date was read off the media, and each build's `Catalog/builds/<build>.json` records
which method was used:

| Provenance | Source | Example |
| --- | --- | --- |
| `VolumeDescriptor` | ISO 9660 primary volume descriptor creation record | Daggerfall CD, `1996-09-06 01:03:28` |
| `ExecutableTimestamp` | COFF header timestamp of the build's main executable | `Oblivion.exe`, `2007-04-16` (patch 1.2) |
| `BuildLabel` | The date the leaked build is named for | the Xbox 360 New Vegas prototypes |
| `ReleaseDate` | Documented release date — only where nothing on the media carries a usable one | see below |

⚠ **Two measurements were junk and are not used.** `Morrowind.exe` stamps `2030-10-02`, which is
impossible for a 2002 title; the Brotherhood of Steel Xbox disc's XDVDFS volume descriptor reads
`1601-01-03`, a near-zero FILETIME. Both entries fall back to the release date and say so in their
`Notes`. A self-test rejects any catalog date outside 1993–2027 so a junk timestamp cannot reach a
directory name unnoticed.

For entries dated from an executable, the generator re-measures that executable on every run and
records `ExecutableTimestampMatchesName` in the manifest — a Steam patch that moves the build on
shows up as a mismatch instead of silently leaving a stale name.

## Scope

PC titles after Skyrim Special Edition are deliberately excluded: Fallout 4, Fallout 76, Starfield
and Oblivion Remastered, 403 GB between them. They stay installed and are reached through the test
suite's Steam probes. The exclusion is recorded in `SampleGeneratorCatalog.Excluded` and echoed into
`catalog.md`, so it reads as a decision rather than an oversight. Console and prototype builds are
in scope regardless of era, because nothing else in the collection holds them.

## Usage

```powershell
dotnet run --project tools/corpus/SampleGenerator -- --dry-run          # plan, write nothing
dotnet run --project tools/corpus/SampleGenerator -- --migrate-layout   # generate + regroup
dotnet run --project tools/corpus/SampleGenerator -- --build Redguard   # one build
dotnet run --project tools/corpus/SampleGenerator -- --self-test        # no media needed
```

| Option | Environment variable | Purpose |
| --- | --- | --- |
| `--media-root <path>` (repeatable) | `BETHESDA_MEDIA_ROOTS` (`;`-separated) | Search roots for disc images and drops. |
| `--sample-root <path>` | `BETHESDA_SAMPLE_ROOT` | The repository's `Sample` directory. |
| `--research-root <path>` | `BETHESDA_RESEARCH_ROOT` | Cache for expanded `.7z`/`.zip` media. |
| `--build <text>` | | Only builds whose directory name contains this text. |
| `--repopulate` | | Rebuild matching builds that are already present. |
| `--migrate-layout` | | Also regroup `MemoryDumps`/`DebugSymbols` and retire `Sample/Full_Builds`. |
| `--dry-run` | | Report what would happen; write nothing. |
| `--self-test` | | Path, catalog and mirror checks, no media required. |

Media roots default to `%USERPROFILE%\Downloads`, `%USERPROFILE%\Documents\MEGA downloads` and each
fixed drive's `PS2` folder, and are searched three levels deep for the file a catalog entry names.
Expanding `.7z`/`.zip` media needs `7z` on `PATH`.

Unlike the sibling generator, which reads every build from one `--media-root/<build name>/`, this
collection is scattered across a Downloads folder, a MEGA sync folder, a PS2 dump folder, several
Steam libraries and the repository's own `Sample/` tree — so each catalog entry names its sources
explicitly and they are located by search.

## What lands where

A build directory holds the unpacked game tree and nothing else.

| Directory | Contents |
| --- | --- |
| `Sample/Builds/<build>/` | the game tree, unpacked |
| `Sample/Media/<build>/` | the original disc images, floppy images and release packages |
| `Sample/DebugSymbols/<build>/` | `.pdb`/`.map`/`.xex` plus the catalog's `MainExecutable`, **copied** (originals stay in the build) |
| `Sample/Catalog/` | `catalog.json`, `catalog.md`, and `builds/<build>.json` |

### Media is declared, never sniffed

Each catalog entry lists its media explicitly as `MediaItem`s. ⚠⚠ Detecting media by file extension
instead would relocate real game content out of three builds:

- `Battlespire.bin` (484 MB) and `Redguard\REDGUARD.bin` (709 MB) are raw-sector CD images that ship
  **inside their Steam installs** — DOSBox mounts them. Both pass a sync-pattern test.
- Daggerfall ships hundreds of `.IMG` files that are its **texture format**, not disk images.

The only genuine disk-image set in the corpus is Arena's eight `.ima` floppies.

### A container is unpacked; a game asset is not

An installer `.CAB` is a container holding the game's assets, so it is expanded — Redguard's
`DATA1.CAB` (the whole 1,664-file install) becomes `Disc 1 (Install)/extracted/`, with the cabinet
left in the disc tree where it shipped. A `.BSA`/`.BA2`/`.DAT` is itself a game asset that the
engine reads, so it stays packed: 72 GB across the corpus, and `Sample/Unpacked_Builds/` already
holds loose trees for the two builds that want them.

### Extraction routes

| Mode | Used for | Reader |
| --- | --- | --- |
| `RawDisc` | redump `.cue` + `.bin` (Arena, Daggerfall, Battlespire, Redguard) | this repo's CLI — **7z cannot read raw 2352-byte sectors at all** |
| `Package` | ISO 9660, UDF, FAT12 floppies, ZIP/JAR | 7z |
| `ExpandInPlace` | installer cabinets | this repo's CLI (InstallShield `ISc(`) |
| `KeepPacked` | XGD images, packages whose tree already exists | nothing |

⚠⚠ **7z reads the Xbox 360 XGD image as type `Udf` and succeeds — returning 13 files and 90 MB
from a secondary partition instead of the game partition at `0xFD90000`.** A silent wrong answer, so
XGD images are never handed to it. ⚠ 7z also exits 2 (`Headers Error`) on the PS3 UDF Blu-ray while
extracting every file, so success there is judged by comparing the extracted file count against
`7z l`, not by the exit code.

Extraction always runs **before** relocation, and a failed extraction leaves the media in the build
rather than moving it away.

## How a build is populated

A catalog entry has one or more `SourcePart`s, each landing at a named subdirectory of the build.
That is how a build keeps its disc image beside the tree extracted from it (Brotherhood of Steel),
or its two discs side by side (Redguard).

| `SourceKind` | Behaviour |
| --- | --- |
| `SteamGame` | Copy the install, resolved by folder name under any drive's `steamapps\common`. |
| `StagedTree` / `StagedFile` | **Move** from `Sample/` — the migration relocates data already in the repo, and on one volume a rename is instant. |
| `MediaTree` / `MediaFile` | Copy from a media root. |
| `MediaArchive` | Expand with `7z` into the research cache, then copy — so a corpus rebuild does not repeat a multi-gigabyte extraction. |

A build that already has a `build.json` is left alone, except that **parts whose destination is
absent are still added** — a catalog entry that grows a new part does not need a full rebuild.

⚠ `--repopulate` **refuses** a populated build whose sources no longer all resolve. That is the
normal state after migration: a `StagedTree` source under `Full_Builds` is gone once it has been
moved into the corpus, and rebuilding would delete the migrated tree while restoring only the parts
that still resolve.

## Disc images

Images our `DiscImageBackend` can mount — ISO 9660 `.iso` and redump `.cue`/`.bin` — are kept as
images; the tool reads them in place. Xbox/Xbox 360 XGD and PS3 UDF images cannot be mounted here
(this repository has no XDVDFS or UDF reader, unlike its Neversoft sibling), so those are mirrored
as images beside whatever tree has already been extracted from them.

## Safety

Every destination and every recursive delete goes through `SampleGeneratorPathSafety`, which rejects
rooted paths, paths that escape their root after normalization, and any reparse point in the
ancestor chain or inside a tree being deleted — so a junction planted in the output tree cannot
redirect a delete into a live game install. `--self-test` exercises those rejections, the catalog's
well-formedness, the mirror/move primitives and the COFF reader against a hand-built PE, without
needing any media.

## Output

- `Sample/Catalog/builds/<build>.json` — sources, date provenance, notes, measured executable
  stamp, and the `Sample/`-relative paths the build was migrated from.
- `Sample/Catalog/catalog.json` and `catalog.md` — the corpus census plus the exclusion list.

The test suite reads the `LegacySources` recorded in `Catalog/catalog.json` to map pre-migration
`Sample/Full_Builds/…` paths onto their new homes (`tests/…/Helpers/SampleCorpus.cs`), rather than
keeping a second copy of the rename table that could drift from this one.
