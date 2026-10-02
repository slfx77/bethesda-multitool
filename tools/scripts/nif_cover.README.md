# nif_cover.py

Per-key joint cover of NIF block types and probe tag values, for the cut-1a sample of
`docs/design/model-document-design-20260923.md` section 7.1. Pure Python 3 (numpy + scipy for the exact
cover; without scipy the tool falls back to greedy and says so). It does not import or call anything from
BMT: the inputs are JSONL written by the independent probe, and the tool only chooses files.

```
python tools/scripts/nif_cover.py cover <probe.jsonl...> --out manifest.json --report report.txt
        [--milp-seconds 120] [--key 20.2.0.7/11/34/LE ...] [--tie-order sha|input]
        [--include-errors] [--include-declined] [--no-floor] [--merge-keys] [--source-map census_spec.json]
python tools/scripts/nif_cover.py keys <probe.jsonl...>
python tools/scripts/nif_cover.py selftest
```

## What it computes

For every version key `(header string, file version, user version, BS version, endianness)`:

1. **Items** are the distinct block-type names (`type:<name>`) and the distinct tag strings (`tag:<string>`)
   seen in that key. Every distinct tag string is one item, so a probe that emits
   `"blend=SRC_ALPHA/ONE"` and `"blend=ONE/ONE"` asks for both to be covered.
2. **Candidates** are the unique files of the key. Identity is the SHA-256 when the input carries one;
   the census format carries none, so there identity is `(entry path after the first "meshes/", size)`,
   the seed's approximation (first occurrence wins). Every manifest file and every `alsoIn` entry says
   which rule produced it (`identity: "sha256" | "path+size"`). Files with identical item sets collapse
   to the smallest of them, since no cover ever prefers a larger file with the same items.
3. **Exact cover**, solved lexicographically with `scipy.optimize.milp` (HiGHS): stage 1 minimizes the
   file COUNT with unit costs; stage 2 minimizes BYTES with the count fixed at the proved minimum by an
   equality constraint. Both stages run with `mip_rel_gap = 0` (the objectives are integers, so HiGHS's
   default absolute gap of 1e-6 cannot admit a suboptimal solution either) under `--milp-seconds` each.
   The manifest records `provedOptimal` (stage 1 proved the count), `bytesProved` (stage 2 proved the
   bytes), both stages' status, message, `mip_gap` and dual bound. When stage 1 stops on the limit with
   a feasible cover, the better of that cover and the pruned greedy cover is used and the method is
   written as `milp-feasible`; such a key is NOT counted in `exactTotal`.
   A single objective `1 + size/total` cannot enforce the byte order: the whole secondary term is
   smaller than HiGHS's default gap, so the first release returned any minimum-count cover while
   reporting `mip_gap 0` (measured 25 B above the byte minimum on bs34 BE and 24 B on bs83).
4. **Greedy with reverse-delete pruning** is always computed (and used when the MILP proves nothing):
   pick the file with the most new items, then the fewest bytes, then the lowest SHA-256 (or entry
   path then source when there is no SHA-256); then drop, largest file first, any member whose items the
   rest still cover. `--tie-order input` replaces the third tie-break with first-seen input order,
   which is what the seed `cover_analysis.py` did; use it only to reproduce the seed numbers.
5. **Floor**: one file per `(game, platform)` that ships the key. A cover file counts for every
   `(game, platform)` that holds a copy of the same identity (`alsoIn`). A `(game, platform)` with no
   cover file gets its file with the most items, then the fewest bytes, then the SHA-256 order
   (role `floor`). A `(game, platform)` whose every record carried probe errors is listed under
   `floorUnavailable`. Floor coverage through `alsoIn` is only as strong as the identity rule: trust it
   for `sha256`, treat it as a path-and-size match for `path+size`.
6. **Declined records**: a record the probe DECLINED (`declined` non-null: a version or BS stream outside
   its walk) carries the header's block types but no feature parse. It is never a candidate (count
   `recordsDeclined` per key, `skippedDeclined` in the input totals). A key whose every record is
   declined gets exactly one file with role `declined-control` (the smallest by `(size, sha)`), which is
   what the design's decline controls are, instead of a block-type "cover" of unparsed files.
   `--include-declined` restores the census-style block-type-only view.

Records whose probe reported errors are not candidates unless `--include-errors` is given: the sample
exists for oracle A1 (probe values against typed fields), and a file the probe could not read cannot be
compared. They still count toward `records` and the `(game, platform)` census.

The output is deterministic for a given input: no timestamps, all lists sorted, candidates presented
to the solver in `(size, tie)` order.

## Input contract

One JSON object per line, UTF-8 with or without a BOM (a PowerShell redirection writes one). Two formats
are accepted and may be mixed across files (not within a key's identity rule: a key fed from both
formats mixes SHA-256 and path identities).

### Probe format (`nif_feature_probe.py`)

This is what the probe's `census` subcommand writes (`fnv_loose_v6.jsonl` line 1 is the reference):

```
{"sha256": "...", "size": 3912,
 "key": {"headerString": "Gamebryo File Format, Version 20.2.0.7", "version": "20.2.0.7",
         "userVersion": 11, "bsVersion": 34, "bigEndian": false},
 "keyString": "20.2.0.7/uv11/bs34/LE", "blockCount": 5,
 "blockTypes": {"BSFadeNode": 1, "NiTriStrips": 1, ...}, "tags": ["clamp=No/WRAP_S_WRAP_T", ...],
 "details": {...}, "errors": [], "declined": null, "ms": 0.4,
 "path": "architecture/westside/craftsmanwindowext.nif",
 "source": "C:/.../PC_Final_Unpacked/Data/meshes", "platform": "pc"}
```

| Field | Required | Meaning |
|---|---|---|
| `key` | yes | What identifies a probe record. A dict `{headerString, version, userVersion, bsVersion, bigEndian}` (also accepted: `header`/`user`/`bs`/`endian`/`endianness`/`be`), or the string `"20.2.0.7/11/34/LE"` (`uv`/`bs` prefixes optional) with the header in a top-level `header` field. A missing header is synthesized from the version. |
| `path` | yes | RELATIVE to `source` when `source` is present (directories included, used verbatim as the entry). Without `source`: a loose path, `<archive>::<entry>`, or `<x>.bsa/<entry>` / `<x>.ba2/<entry>`; a loose path is split at `/meshes/`. |
| `source` | recommended | The tree root or archive the file was read from; the manifest's `source`. |
| `archive`, `entry` | no | Explicit source and entry; override every split. |
| `sha256` | yes | Lower-case hex; the file identity and the last tie-break. |
| `size` | recommended | Bytes; the "fewest bytes" tie-break and the MILP secondary objective. `details.size` / `details.bytes` are read as a fallback; absent means 0. |
| `blockTypes` | yes | `{name: count}` as the probe writes it, or a list of names; the names are the items. |
| `tags` | yes | A list of strings, each one item; or a dict: `{"k": "v"}` gives `k=v`, `{"k": [..]}` gives one `k=v` per element, `{"k": true}` gives `k`, `false`/`null` gives nothing. |
| `errors` | yes | List of strings (empty when the probe succeeded). A string is accepted as a one-element list. A record with errors may omit `blockTypes` and `tags`. |
| `declined` | yes | `null`, or the probe's reason for not walking the file (see rule 6). |
| `details` | no | Free-form; ignored by the cover. |
| `platform` | no | `x360` / `ps3` / `pc` as the probe writes it (its `--platform` or the source-path rule); `unknown`/`auto` fall back to the path mapping below. |
| `game` | no | Override the path-derived game. |

### Census format (`nif_census.py`, `census.jsonl`)

`{hs, ver, uv, bs, be, nb, types, tableOnly, src, path, size, [heur], [err]}`; `types` are the items,
`src` is the source label, `path` the entry. No SHA-256 and no tags, so this format validates the tool
on block types alone. An error record is `{err, src, path, size}` with no header: it is counted under
its version key when `hs`/`ver` are present, else under the key `unreadable`, and never crashes the run.
`--source-map census_spec.json` (the census's own input list) resolves each `src` label to the archive
or tree path it was read from and writes it as `sourcePath`.

### Game / platform mapping

Explicit tables in the script (`CENSUS_SOURCE_MAP` by `src` prefix; `PATH_GAME_MAP` by path substring,
where a `Sample/Builds` directory name such as `Fallout - New Vegas (2010-8-22, X360 - Final)` supplies
the platform token). Steam and Steam Disc builds map to `PC`: the sampling axis is the format a
platform ships, not the distribution channel. Anything unmatched is `unknown/unknown` and listed in
the report under `UNMAPPED`.

## Output

`manifest.json` (`manifestSchema` 2; schema 1 lacked `identity`, `recordsDeclined`, the
`declined-control` role, `bytesProved`, `exactUnprovedKeys` and `skippedDeclined`):

```
{ "tool", "manifestSchema", "inputs": [...], "options": {...},
  "input": {"records", "filteredOut", "skippedErrors", "skippedDeclined", "unmappedSources": {label: n}},
  "totals": {"keys", "coverFiles", "floorFiles", "declinedControlFiles", "files", "bytes",
             "greedyTotal", "greedyPrunedTotal", "exactTotal" (proved keys only), "exactUnprovedKeys",
             "provedOptimalKeys", "bytesProvedKeys"},
  "keys": [ {
     "key": "20.2.0.7/uv11/bs34/LE", "merged", "header", "version", "versionInt", "userVersion", "bsVersion",
     "endianness", "records", "recordsWithErrorsSkipped", "recordsDeclined", "uniqueFiles", "distinctItemSets",
     "items": {"blockTypes", "tags", "total"},
     "gamePlatforms": {"FNV/PC": n, ...}, "sources": {label: n, ...},
     "cover": {"method": "milp" | "milp-feasible" | "greedy+prune", "count", "bytes",
               "greedy": {"count", "bytes"}, "greedyPruned": {"count", "bytes"},
               "milp": {"available", "status", "message", "provedOptimal", "bytesProved", "candidates",
                        "timeLimitSeconds", "mip_gap", "mip_dual_bound", "mip_node_count",
                        "countStage": {...}, "bytesStage": {...}, "count", "bytes"}},
     "floor": {"FNV/X360": "<entry added>" | null (already covered)}, "floorUnavailable": [...], "floorFiles",
     "declinedControl": "<entry>" | null,
     "files": [ {"role": "cover" | "floor" | "declined-control", "source", "entry", "sha256",
                 "identity": "sha256" | "path+size", "size", "gamePlatforms": [...],
                 "itemCount", "blockTypes": [...], "tags": [...], "identicalItemSetFiles",
                 "alsoIn": [{"source", "entry", "gamePlatform", "identity"}], ["sourcePath"], ["floorFor"]} ] } ] }
```

`report.txt`: the totals, one line per key (records, declined, unique files, distinct item sets, block
types, tags, greedy / pruned / exact counts — `feasible N` when the MILP did not prove the count —,
method, proof (`yes` = count and bytes proved, `count` = count only), floor files, `(game, platform)`
census), then every chosen file per key with its role, size, item count, platforms, source, entry and
identity rule.

`--merge-keys` ignores the version key and covers every record as one universe (label `merged`). It exists
for cross-key comparisons such as the validation below; the design's sample is per key.

`selftest` writes synthetic fixtures to a temp directory and checks: the real probe shape keeps
directory-bearing entries; a byte-identical X360 copy is credited through `alsoIn` with `identity`
`sha256`; a wholly declined key yields one `declined-control` and no cover; an error record without
`blockTypes`/`tags` and a census error record are counted, not fatal; a BOM-prefixed input parses; a
greedy-3 / exact-2 instance is proved on both stages; two runs give identical manifests.

## Validation (2026-09-23, re-run after the review fixes)

Manifests, reports and logs are under the session scratch directory `cut1a-prep/fix/`
(`census_v2_run{1,2}_manifest.json`, `probe_v6_manifest.json`, `probe_v6_report.txt`); the first-release
artifacts stay under `cut1a-prep/cover/` and the reviewer's under `cut1a-prep/verify-cover/`.

1. **Block types only.** `census.jsonl` (683,036 records, 30 keys, 305 MB) against the seed
   `cover_full.json`: exact **302** with all 30 keys proved optimal on BOTH stages (count and bytes),
   greedy **322**, greedy+prune **319** - the seed's three totals. Per key, unique files, distinct item
   sets, block-type counts, exact, greedy and pruned counts agree on 30 of 30 keys. Exact file sets are
   identical to the seed's on 25 of 30 keys: bs34 BE and bs83 are now the BYTE-MINIMAL minimum-count
   covers (7,967,088 B against the seed's 7,967,113; 2,678,779 against 2,678,803 - exactly the 25 B and
   24 B the review's lexicographic re-solve found), and 10.2.0.0/bs9, bs173 and bs175 are alternative
   optima with the same count and bytes picked from a differently ordered candidate list. The floor adds
   5 files (Oblivion 4.0.0.2 `marker_temple.nif`; FNV 20.0.0.4/uv11 `collisionboxstatic.nif`; FNV/PS3
   `terminalinterface01.nif` and Skyrim LE `2handsword.nif` on bs34 LE; Skyrim SE
   `artrigpressureplate01.nif` on bs83). Two runs give byte-identical manifest and report (SHA-256
   compared). 12-13 s and 237 MB peak working set per run.
   ⚠ In census mode FNV/X360 and FO3 on bs34 LE are credited to the cover through `alsoIn` copies that
   are PATH-AND-SIZE matches, not byte identity: the review hashed the 12 FO3 copies the bs34 LE cover
   claims straight out of the FO3 BSAs and found 5 byte-identical, 7 DIFFERENT at the same path and
   size (`verify-cover/alsoin_bytecheck.py`; `creatures/protectron/skeleton.nif`,
   `dlc03/effects/dlc03flamerexplosion.nif`, `characters/_male/skeleton.nif` among the 7). So the census
   manifest's `FO3/PC: null (already covered)` is a path-level statement, marked `identity: path+size`;
   only the probe's SHA-256 identity settles it, which is why the real sample uses probe input.
2. **Joint cover, real probe output.** The probe censuses of the FNV loose tree (25,307 files) and the
   X360 Final `Fallout - Meshes.bsa` (19,279 entries), 44,586 records: 15 keys, cover **211** files, all
   15 keys proved on both stages (bs34 LE: 28,405 records, 24,407 unique by SHA-256, 157 block types +
   296 tags -> exact 105, pruned 113, greedy 113; bs34 BE: 14,896 records, 150 + 279 -> exact 94, pruned
   101, greedy 104; bs14 3, bs21 4, bs26 2, bs32 3), plus **9** `declined-control` files, one per wholly
   declined key (20.0.0.4/uv10, 20.0.0.4/uv11, bs24, bs25, bs27, bs28, bs30, bs31, bs33; 929 declined
   records skipped), floor 0 (every X360 LE copy is SHA-identical to a loose file). Entries keep their
   directories (`meshes/characters/_male/idleanims/talk_handsatside_still2.kf`,
   `architecture/westside/craftsmanwindowext.nif`). 3.1 s, 114 MB peak.
3. **Seed joint cover (first release, unchanged).** The FNV loose tree read by the scratch emitter into
   probe-format JSONL with the 26 `field_features.py` values: `--merge-keys` greedy 43 (the published
   figure), pruned 42, exact 40; per key bs34 LE exact 39, plus bs14 (2), bs21 (2), bs26 (1) and
   20.0.0.4/uv11 (1). Superseded for the sample by item 2, which carries every section 7.1 tag.

## Limits

- The census identity `(entry after "meshes/", size)` can merge two different files that share a path
  and size across sources (measured: 7 of 12 FO3 claims on bs34 LE); the probe's SHA-256 does not. Cover
  counts are not affected (the first occurrence is the candidate); `alsoIn` and `gamePlatforms` carry
  the identity rule so a consumer can tell.
- MILP ties beyond (count, bytes) are resolved by the solver; determinism comes from the canonical
  candidate order, not from a third objective.
- `--key` filters records before pass 1, so `records` and the `(game, platform)` census cover only the
  selected keys.
- The tool streams its inputs twice (pass 2 collects the sources of the chosen identities), so inputs
  must be files, not pipes.
- `tools/scripts/*` is ignored by the repository `.gitignore` (line 128) with a per-script allow-list;
  `nif_cover.py`, this README and `nif_feature_probe.py` need allow-list lines or an owner decision that
  corpus tools stay untracked.
