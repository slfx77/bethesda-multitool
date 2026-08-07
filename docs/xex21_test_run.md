# xex21 — Test Run

> **v143 UPDATE (2026-08-05):** the zeroed-XTEL defect below (P1.3, Nipton `getpos z`) is **FIXED
> in `TestOutput/xex21.v143.esm`** — the arrival transform was in the dump all along; the runtime
> parser read only 4 of DoorTeleportData's 32 bytes. v143: **0 of 83 XTELs zeroed** (was 80),
> oracle-verified 3/3 within 50 units of retail's authored arrival points. **Test v143, not v142**
> (same file swap everywhere below). Everything else in this plan is unchanged: the v142 ASPC/XRDO
> analysis holds, and the door round-trips in P1.3 should now also land you at the correct arrival
> point instead of the cell origin — `getpos z` ≈ 8256 at Nipton is now the EXPECTED result.

**Build under test:** `TestOutput/xex21.v142.esm` (Feb 2010 capture)
**What is actually new vs v141:** exactly **53 record bodies** — 52 ASPC (acoustic space re-layout) + 1 REFR (`01001C48` gained XRDO). Zero records added, zero removed. QUST/DIAL/INFO/WEAP/all 55,686 placed refs are **byte-identical** to v141.

> **Read this first:** neither v142 fix is passively observable. The 52 ASPCs are referenced by nothing (0 of 104 XCAS links point at them), and the one XRDO sits on an initially-disabled ref whose base activator has no sound data. Pass 2 therefore requires an FNVEdit edit and an error-log diff, not a listening tour. Everything else in this plan is *regression surface* — proving v142 didn't break what v141 fixed.

---

## 0. Setup

### 0.1 Files

| Step | Action |
|---|---|
| 1 | Keep a **pristine backup** of `xex21.v142.esm` — Pass 2B edits the file in place. |
| 2 | Copy `xex21.v142.esm` into `E:\SteamLibrary\SteamApps\common\Fallout New Vegas\Data\` |
| 3 | **Disable / remove `xex44.v140.esm`** (currently in Data). It is a different capture and it shifts your load index. |
| 4 | Add `xex21.v142.esm` as the last line of `plugins.txt` |
| 5 | Optionally copy `xex21.v141.esm` in too (needed only for the Pass 2B error-log A/B and the Pass 1 crash A/B) |

### 0.2 The BSA decision — make it now

38 placed plugin-new base objects reference meshes that exist **only** in `xex44.v140 - Main.bsa` (verified absent from `Fallout - Meshes.bsa`). That archive sits in Data but is **not** in `SArchiveList`.

- **Recommended:** append `xex44.v140 - Main.bsa, xex44.v140 - Textures.bsa` to `SArchiveList` in `Documents\My Games\FalloutNV\Fallout.ini`.
- With them loaded, a yellow missing-mesh marker becomes a **real finding**. Without them, it is meaningless noise. Do not file geometry bugs until you've settled this.
- Caveat: that archive is from the April capture, so coverage is not guaranteed even when loaded.

### 0.3 Find your load index — do not skip

Every FormID below written as `XX……` is plugin space. Every FormID written `00……` is master space and stays as-is.

```
help TheStripWorld 4
```

This prints WRLD `XX003056`. **That leading byte is your `XX`.** With the plugins.txt above it will be `0A` (10 retail plugins precede it), not `01`. If this command returns nothing, the plugin is not loading and nothing below is valid.

### 0.4 Console/game prerequisites

- **Subtitles ON** (Settings → Display). Pass 4A is unreadable without them.
- Delete or rename `falloutnv_error.log` in the game root before your first launch.
- **New game required** for Pass 3B (enable-state is only evaluated at game start).
- Make a clean save right after leaving Doc Mitchell's. Pass 4B uses `setstage`, which fires result scripts — use a throwaway.

---

## 1. Pass 1 — Crash surface (~40 min)

Ordered as a route. Stop and report at the first hard CTD.

### P1.1 — Does it load at all `CRITICAL`
Launch → main menu → new game. The plugin carries 22 empty exterior cells (`XX001B5A`–`XX001B6F`) misfiled under the interior CELL group with no worldspace parent, and its HEDR record count (55,079) disagrees with the actual 74,494.

- **Good:** main menu, new game starts. Both anomalies are inert.
- **Bad:** engine refuses the plugin, or hangs during load. If you reach the main menu, this target is closed.

### P1.2 — TheStripWorld: navmesh + mis-parented persistents `CRITICAL`
The highest-risk area in the build. WRLD `XX003056` "The Strip" — 40 cells, grid X −2..3 / Y −3..5, 2,717 REFR, 39 LAND, 46 NAVM. This repo has two crash notes filed against it.

**Three refs that v141's fix *created* a new hazard for:** master persistent refs parked in the `CellPersistent` subgroup of *ordinary* exterior grid cells. FalloutNV.esm has **zero** instances of this pattern across 467,217 records; v140 had zero, v141 and v142 have three.

```
cow TheStripWorld 0 0
```
Then walk on foot (−2,1) → (0,2) → (1,2) → (1,4). Cross seams by walking, not teleporting.

| Sub-step | Commands | Expect |
|---|---|---|
| Follower stress | `prid XX0025BA` → `moveto player`, then run across 3 cell seams | Securitron paths and follows; no freeze at seams |
| Un-navmeshed column | `cow TheStripWorld 3 2` with follower | Follower stops pathing; **game stays up** |
| Victor entrance | `cow TheStripWorld 0 1` → `prid 0013BB18` → `moveto player` | Resolves, teleports |
| Embassy guard | `cow TheStripWorld 2 2` → `prid 0013BAC1` → `getdisabled` → `enable` → `moveto player` | `getdisabled` returns **1** (correct — disabled in master), then enable works |
| Les Fretwell | `cow TheStripWorld 2 3` → `prid 0013BAF4` → `getdisabled` → `enable` → `moveto player` | Same |
| Grid churn | `cow TheStripWorld 3 4`, walk back, save + reload on the spot | No CTD |

- **Good:** solid terrain everywhere, all three refs resolve, `getdisabled`=1 on the two disabled ones, no crash on grid attach/detach.
- **Bad (hard):** CTD on `cow`, on a cell boundary, or when a follower paths into the x=3 column.
- **Bad (soft, and the more likely real defect):** `prid 0013BB18` **fails until you have physically stood in grid (0,1)**. Persistent refs are meant to be resident from game load; these are not. Any retail script reaching for Victor before you've been there will no-op.

### P1.3 — Strip exit doors return you to the *retail* Strip `CRITICAL`
**21 of the 27** teleport doors in the Strip persistent cell (`XX001BAE`) lead into master interiors whose return door points at the **master's** `TheStripWorldNew` persistent cell (`0013B310`), not ours. Only **2** round-trip correctly.

Separately: in v142, **80 of 83 XTEL arrival transforms were zeroed (0,0,0)** (retail: 0 of 1108). **v143 fixes this** — the transform was unread dump data, not lost data. On v143 an arrival at (0,0,0) or under the floor is a REAL finding again.

```
coc TOPSCasino
```
Walk out the front doors (`XX001C9A` / `XX001C9D` → `XX0023A6` / `XX0023A7`). Immediately:
```
getpos x
getpos y
getpos z
```

| Observation | Meaning |
|---|---|
| z ≈ 1000–1100, standing on the Strip street | **Good** — this pair is the correct control |
| x=0 y=0 z=0, or ~1000 units under the street, or falling | **Bad** — zeroed XTEL confirmed |

Then from the prototype Strip, walk **into and straight back out of** the Gomorrah door, the Lucky 38 door, and an Ultra-Luxe dome door.

- **Good:** you come back out onto the same unfinished prototype Strip.
- **Bad:** you exit into the visibly finished **retail** Strip and the prototype worldspace is unreachable without retyping `cow TheStripWorld 0 0`. Expect this on 21 of 23 doors — this is the single most likely real defect in the build.

*(While in TOPSCasino, also do P4J — same cell, no extra travel.)*

### P1.4 — Nipton duplicate cell `CRITICAL`
CELL `XX001B88` is a **plugin-new** interior whose EditorID `NiptonTownHallInteriorFloor2` collides with master cell `0015182B`. It's the only CELL EditorID collision in the file. Ours holds **6 actors, 0 navmesh, 0 statics, no XCLL/LTMP/XCAS** — no lighting, no floor.

**Do not use `coc NiptonTownHallInteriorFloor2`** — ambiguous.

```
coc NiptonTownHallInterior
```
Take either stair door (`XX001D46` / `XX001D47`). They return correctly, so you can always get back down.

- Discriminator: location name **"Nipton Town Hall"** = ours; **"Town Hall Assembly Floor"** = master's won the lookup.
- `getpos z` on arrival — expect ~8256; 0 means zeroed XTEL again.
- Then `prid 0013BF5B` → `moveto player` (Vulpes), and shoot one Legionary to force an AI update with no navmesh.

- **Good:** you arrive, actors exist, stairs work, game stays up.
- **Bad:** CTD on cell load or on the first AI tick after combat starts. Also bad: unlit black void with no floor (predicted from the record contents — report it, but it's a data gap not a crash).

### P1.5 — Zero-navmesh actor cells `HIGH`
```
coc FreesideNPCDump      (34 actors, no navmesh in master OR plugin — highest risk)
coc TestDance            (7 new actors, no navmesh)
coc TestFancy
coc TestNuka
```
Wait 30 s in each for AI packages to tick, then fire a shot. Exit with `coc GSDocMitchellHouse`.

- **Good:** loads, actors idle or freeze, you can leave.
- **Bad:** CTD on load, or a freeze seconds in when a patrol package tries to path.

### P1.6 — Two dialogue dead-ends `HIGH` *(reload-recoverable, not a CTD)*
Both INFOs force a follow-up to an INFO that does not exist anywhere.

| Speaker | Route | Topic to pick |
|---|---|---|
| Loyal (`000FED42`) | `cow WastelandNV 8 34` | "Oh. Does it have a dial or something?" |
| Joana (`0010C6F6`) | `coc Gomorrah01` → `prid 0010C6F6` → `moveto player` | "Would you give me your story for 25 caps?" |

- **Good:** NPC answers, you return to the topic list.
- **Bad:** subtitle reads literally `(NOT FOUND IN CRASH DUMP)` — that confirms you hit the record — and then the camera locks with no topics and no exit. Requires a reload.

---

## 2. Pass 2 — The two v142 fixes (~50 min, half of it out of game)

### 2A. XRDO on REFR `XX001C48` `HIGH`

**Set expectations before you test.** This is a 16-byte subrecord on one ref, and it is *correct*:
- Fallout3.esm REFR `000C4994` — same base TACT `000C4991`, same cell — carries a **byte-identical** XRDO (radius 0.0, type 4, static 0.0, null ref). Type 4 is authentic FO3 data, not an out-of-range value we invented.
- Base TACT `000C4991 HD00HouseRadio` has only EDID/OBND/FULL/MODL/MODT — **no SNAM, no RNAM**. It cannot emit audio or a radio station under any circumstance.
- It also carries the **Non-Pipboy** flag, so it can never appear in Pip-Boy → DATA → RADIO.
- The ref is **initially disabled** (flags 0xC00, no XESP), one of 35 disabled refs among the 40 the plugin injects into this cell.
- The converter's own encoder emits a "not placed in an exterior" warning when XRDO is *missing* (defaults to type 0, needs an anchor). v142 **removes** that exposure; v141 was the exposed build.

```
coc MegatonPlayerHouse
tcl                       <-- do this immediately; master ships this cell with ZERO children, there is no floor
prid XX001C48
getdisabled               <-- expect 1
enable
moveto player
```

| | |
|---|---|
| **Good** | Cell loads. `getdisabled` = **1**. `enable` makes the object appear. **Silence is correct.** No new error-log line. |
| **Bad** | CTD on `coc` that v141 does not reproduce; CTD or a new error-log line at the moment you `enable`; or audio that plays worldspace-wide and never attenuates. |
| **Not a bug** | No radio audio. No Pip-Boy station. Nothing visible before `enable`. Bare furniture floating in a void. |

**Negative control (2 min):** check Pip-Boy → DATA → RADIO in Goodsprings, Primm and Novac. Confirm **no** entry named "House Radio for Jukebox" ever appears, and that **Mojave Music Radio** (REFR `0016B74C`, the only major station shipping enabled) works normally. Do *not* score Black Mountain Radio (`000E6188`) or Radio New Vegas (`0014DF03`) as failures for being absent early — both are initially disabled in the master by design.

**The real verification is out of game:** open the plugin in FNVEdit, find the REFR under `MegatonPlayerHouse → Persistent` whose base is `HD00HouseRadio`, and read the Radio Data struct: Range Radius 0.0, Broadcast Range Type "Current Cell Only", Static Percentage 0.0, Position Reference NULL. Load Fallout3.esm and compare against REFR `000C4994` — identical apart from the missing EditorID.

---

### 2B. ASPC — the acoustic space re-layout `CRITICAL`

**What changed.** v141 wrote 31 REGN FormIDs into SNAM slot index 3 (a *sound* slot xEdit labels "Night") — a hard type error. v142: SNAM[0] non-null on 39/52, **39/39 resolve to SOUN**; the 31 REGN values moved into a new RDAT subrecord, **31/31 resolve to REGN**; WNAM corrected from a junk `9` to `0` (matching all 113 retail ASPCs); ANAM populated on 46/52; OBND added on 32. Oracle: 51 of 52 EditorIDs exist verbatim in Fallout3.esm, RDAT 51/51 and ANAM 51/51 exact.

**Do these four steps in order.** Steps 1–3 need no plugin editing.

#### Step 1 — Error-log A/B `HIGH` (free, highest signal)
The previous session logged 34 `Could not find pNightSound … for activator (010061xx)` warnings on xex44 — the exact family this fix targets.

1. Delete `falloutnv_error.log`. Launch **v141**, reach main menu, `coc` into two or three cells, quit.
2. Grep the log for `pNightSound`, `for activator (XX006F`, `(XX00700`.
3. Repeat with **v142**.

- **Good:** v141 shows ~31 such lines; **v142 shows zero**. That is the fix landing.
- **Bad:** same or more on v142, or a new warning class naming `XX006FDE`–`XX007011` or a dangling SOUN/REGN — the RDAT/SNAM split put values in the wrong lanes.
- If the log is empty or absent, this test yields *nothing* rather than a pass.

#### Step 2 — XCAS regression sweep `MEDIUM` (free, do on the pristine plugin)
84 of the plugin's master-cell overrides carry XCAS; 74 are byte-identical to retail and 10 lack it in both — 84/84 agreement, unchanged between v141 and v142. This exercises the CELL encoder, not the ASPC reader.

```
coc Lucky38CasinoFloor01     -> low casino hum
coc ULBath                   -> wet, echoey bathhouse
coc HooverDamIntIntakeTower01-> heavy turbine drone
coc OVWestSewers03           -> arena rumble (The Thorn)
coc SecuritronVault          -> machine hum
coc TechatticupMineInterior
coc Vault22c
coc RocketLabBasement
```
Stand still 15 s in each. A/B against the same `coc` with the plugin disabled.

- **Good:** indistinguishable from plugin-off. Expected result.
- **Bad:** any room goes silent or plays a different loop — CELL encoder corrupted XCAS despite byte-identical data.

#### Step 3 — Hear the fix (requires FNVEdit edit) `HIGH`
This is the only way to make the fix audible. **Run both halves in the same session — neither alone carries information.**

In FNVEdit, open `xex21.v142.esm`, go to CELL `Lucky38CasinoFloor01` (`0010D512`, verified interior), and set **Acoustic Space (XCAS)**:

| Run | Set XCAS to | Expect |
|---|---|---|
| **A (positive)** | `IntTenpennyLobby` **[XX007003]** — SNAM[0] = `MUSTenpenny01LP` [`0006F687`], `fx\mus\tenpenny\mus_tenpenny_01_lp`, **confirmed present in retail Fallout - Sound.bsa** | Tenpenny Tower lounge **music** replaces the casino roomtone. It is literally music — unmistakable — and should loop. |
| **B (negative)** | `Silent` **[XX006FF1]** — SNAM[0] genuinely NULL, no RDAT, ANAM=1 | The Lucky 38 casino loop **disappears entirely**. Footsteps, voices, weapons only. |

`coc Lucky38CasinoFloor01`, stand still, no music playing, listen ~20 s each.

**Interpretation table — use this, do not read either result alone:**

| Tenpenny | Silent | Verdict |
|---|---|---|
| Music | Quiet | ✅ Fix works end to end |
| Silent | Quiet | XCAS applies but our ASPCs are **rejected**. Prime suspect: **INAM=0** (see below), not SNAM |
| Unchanged | Unchanged | The edit never landed — both results meaningless |

> **The INAM confound — read before drawing any conclusion.** All 52 of our ASPCs carry `INAM = 0`, including the 49 with `Int*` EditorIDs. In retail FalloutNV.esm, **all 14 ASPCs with INAM=0 are exteriors** (ExtDesertDefault, EXTTheStrip, ExtHooverDam…) and all 99 with INAM=1 are interiors. So INAM=0 does not read as "unfilled" — it reads as **"this is an exterior space."** Fallout3.esm's ASPC struct has no INAM field at all, so the Feb-2010 source genuinely has nothing to read and `0` is honest output — but it may be the wrong default for FNV, and it may also neuter RDAT entirely (xEdit names that field "Use Sound from Region **(Interiors Only)**"). Whether the converter should default INAM=1 for this struct era is **your decision, not a bug to blind-fix.**

**Only run the Tenpenny loop.** Three ASPC loops are absent from every archive on this machine (`IntVault87Default01`, `IntTent01`, `IntPittSteelMill01`) and nine more resolve *only* out of `xex44.v140 - Sounds.bsa`. Picking one of those would give a false negative.

#### Step 4 — Optional ANAM reverb A/B `MEDIUM`
Only if Step 3 showed the ASPC is being applied at all. These two share an **identical SNAM[0] (`000AF607`) and identical RDAT (`000AF614`)**, so ANAM is the only variable:

- `IntCaveDefault01` **[XX00700F]** ANAM = 20 (Quarry)
- `IntCaveDefault01Reverb` **[XX006FE4]** ANAM = 9 (Concerthall)

Wire each in turn, `coc Lucky38CasinoFloor01`, fire a 10 mm pistol several times from a fixed spot, listen to the tail.

- **Good:** audible difference in reverb tail; Concerthall wetter and longer. Room tone identical in both.
- **Bad but inconclusive:** identical dry tail. 10 of our 17 ANAM values (1, 3, 4, 5, 9, 12, 14, 17, 18, 20) never appear in retail FalloutNV.esm at all. They're legal FO3 values on a shared engine, but whether FNV's DSP table honours them could not be verified from the files. Treat a null result as inconclusive, not as proof of a defect.

#### Step 5 — FNVEdit structural check `HIGH` (2 min, free)
Right-click the plugin's **Acoustic Space** group → **Check for Errors**. Then `Referenced By` on `XX006FF5`, `XX00700F`, `XX007001`.

- **Good:** clean. 39/39 SNAM → SOUN, 31/31 RDAT → REGN, 0 type errors. `Referenced By` **empty** on every one (confirms the fix is latent and cannot regress anything audible).
- **Bad:** any "Found a REGN reference, expected: SOUN" (that's what v141 looks like), or any referrer at all — that cell becomes a live audible test and jumps the queue.

---

## 3. Pass 3 — v141 regression checks (~25 min)

All three are **structurally impossible to regress** in v142 — the v141→v142 diff touches 52 ASPC + 1 REFR and nothing else; all 274 WEAP and all 55,686 placed refs are byte-identical. Run them to confirm, and know that if you *do* see a defect the cause is not in the plugin bytes.

### 3A. cow-Strip crash `HIGH`
Covered by **P1.2/P1.3**. Add the control:
```
cow TheStripWorldNew 0 0
```
xex21.v142 contributes **nothing** to this master worldspace — no WRLD, no persistent cell, and all three refs blamed for the v141 crash (`00116834`, `0013BB19`, `0013BB1A` — all master records) are absent.
- **Good:** loads identically to no-plugin vanilla.
- **Bad:** a CTD here would mean the v141 diagnosis was wrong entirely. High diagnostic value, low probability.

### 3B. Powder Gangers / Goodsprings enable state `HIGH` — **NEW GAME REQUIRED**
The v141 defect was runtime enable-state clobbering the master's 0x800 bit on 105 refs. All 51,196 master-override placed refs now carry **zero** header-flag differences from FalloutNV.esm.

```
prid 000F1971   getdisabled     (ChavezPowderGanger02Ref)
prid 000F1972   getdisabled     (ChavezPowderGanger01Ref)
prid 000F1973   getdisabled     (ChavezPowderGanger03Ref)
prid 00104F06   getdisabled     (GoodspringsSettler02Ref)
prid 00105D4C   getdisabled     (GoodspringsPowderGangMarker)
```
All five are in the WastelandNV persistent cell, so `prid` works from anywhere.

- **Good:** `getdisabled` returns **1** on all five. Goodsprings at game start is vanilla — no armed Powder Gangers in town.
- **Bad:** any returns 0, or Gangers are live the moment you step outside Doc Mitchell's.
- Note: Joe Cobb (`00104C68`) and Trudy (`00104C6D`) are not present in *any* xex21 build — the master governs them completely.

### 3C. WEAP attack animation `HIGH`
The v141 fix clamped 83 weapons from `attackAnim = 0` to `255` (Default) at DNAM offset 41 — offset verified empirically (260/260 retail WEAPs hold a valid sparse-enum value there vs 0–9% at neighbouring offsets).

**The literal reported defect first:**
```
player.additem XX006F42 1 ; player.additem XX0032DB 200   (Atomic Baby Launcher)
player.additem XX006F41 1 ; player.additem 0007EA26 200   (Atomic Baby Machinegun)
```
Equip, hold fire several seconds in 1st **and** 3rd person. Both are animType 8 (Handle2Hand), which retail *does* pair with 255 — these have precedent.

**Then the risk subset.** 26 of the 83 have animTypes that retail **never** pairs with 255. Do these four first — they cover the two largest risk buckets:

| Weapon | Ammo |
|---|---|
| `XX006F2A` WeapAssaultRifle | `0006B53D` Ammo5mm |
| `XX006F24` WeapChineseAssaultRifle | `00004240` Ammo556mm |
| `XX006F33` DemoLaserRifle | `00004485` AmmoMicroFusionCell |
| `XX006F44` HVSimWeapLaserRifle | `00004485` |

If time allows: `XX006F49` MQ11AutumnLaserPistol / `00020772`, `XX006F74` WeapNVDoubleBarrelShotgun / `0008ECF5`, `XX006F38` + `XX006F37` rail cannons / `XX0032DA`.

- **Good:** each raises, plays a fire animation, consumes ammo, spawns a projectile, completes a reload.
- **Bad:** equips but never fires (ammo frozen), fires with no animation, or the reload locks.
- **Do not count:** `WeapRailwayRifle` (`XX006F28`), `MQ04Mine`, `HVSimWeapMineFrag` — no ammo record linked at all, so a failure there is unrelated. Skip the 11 turret/creature weapons and `XX006F4A` ("GUN THAT SHOULD BE REMOVED").

---

## 4. Pass 4 — Content spot-checks (~45 min, stop anywhere)

### 4A. Unconditioned global combat barks `CRITICAL` — the highest-value content finding
The plugin injects **25 combat/death bark INFOs with ZERO CTDA conditions** at quest priorities 50–60. `GenericPowderGanger01` (`XX004FB9`, **priority 60**, start-game-enabled) supplies unconditioned `StartCombatResponse` and `DeathResponse` lines. Every master INFO on those topics is conditioned and the master's highest priority is **55**. Three FO3-leftover quests (`GenericEnclave`, `GenericSlaver`, `GenericRaider`, all priority 10) add 14 more.

Subtitles on. Then start a fight with **non-Powder-Ganger** enemies:
```
coc CampForlornHopeCommandCenter ; prid 00120DDA   (NCR Tech Sergeant)
```
Kill one member of a group and read what survivors say.

- **Good:** faction-appropriate retail barks. No actor outside the Powder Gangers says any of the strings below.
- **Bad:** NCR troopers / Khans / ghouls / Securitrons shouting **"Oh-ho, you're fucking dead!"**, **"Come on, let's go!"**, **"All right, let's go!"** on combat start, or **"Ah, shit!"** / **"Fuck!"** on a nearby death. Also watch for flat settler lines ("Handling it.", "No problem.", "Aw, hell.") and for Enclave/Slaver/Raider barks ("Ten-four! Going weapons hot!", "Woohoo! I love this shit!", "It's go time!").
- This is a static-priority prediction, not proven runtime behaviour. The in-game test is what settles it.

### 4B. Prototype quest titles `HIGH` — also your "is the plugin winning?" canary
Across all 175 overridden QUSTs the **only** field that differs from retail is FULL, on 20 of them. 155 are byte-identical.

On a **throwaway save**:
```
setstage VMS20 10
setstage VMQ05 10
setstage VMS06 10
```
Open Pip-Boy → Data → Quests.

| Ours (expected) | Retail |
|---|---|
| Boulder City Blues | Boulder City Showdown |
| Moore Hoover Dam Stuff | For the Republic, Part 2 |
| Organically Grown | There Stands the Grass |

- **Good:** the prototype titles appear. That's correct Feb-2010 content *and* proves the QUST overrides are loading.
- **Bad:** retail titles appear — the overrides are not winning, which invalidates every other quest observation in this plan.

### 4C. Ulysses `HIGH`
NPC_ `XX004EA8`, ACHR `XX001F6B` — persistent and **enabled**, full FaceGen, quest `VDialogueUlysses` `XX004FBB`.
```
prid XX001F6B ; moveto player ; startconversation player
```
- **Good:** he spawns with a real head, blank greeting line, exactly 3 topics. Topic000/Topic002 print `(NOT FOUND IN CRASH DUMP)`. Goodbye exits cleanly on "Custom goodbye."
- **Bad:** `prid` fails, headless/T-posing despite FaceGen data, or the greeting opens with an empty topic list and no Goodbye (camera lock).
- **Not a bug:** generic clothing. All four Ulysses outfit records are xex44-only.

### 4D. Master refs repointed onto plugin NPCs `HIGH` (cheapest high-signal test in the build)
The only two such repoints in the file. Both persistent and enabled — no travel needed.
```
player.moveto 0012583B    (was LvlNCRRangerCivilian -> now plugin NPC_ XX004E97)
player.moveto 0012643B    (was RSEchoRangerGhoul01  -> now plugin NPC_ XX004E9A)
```
Console-click each, read the name; `openactorcontainer 1`.
- **Good:** an NCR Ranger, clothed, armed, alive, named, with inventory.
- **Bad:** nude/bald/invisible/floating head/empty inventory — a plugin-new NPC_ isn't complete enough to back a retail reference.

### 4E. NPC with no DATA subrecord `HIGH`
`XX004E77` 1ESquatterAAMOld — one of only two new NPCs missing DATA (base health + 7 SPECIAL). Retail has **0 of 3,816** NPC_ missing it, and there's no TPLT to inherit from.
```
player.moveto XX001F52 ; getav health ; getav strength ; getav endurance
```
Compare against a healthy sibling: `player.placeatme XX004E6C 1` (1EWrench, DATA present).
- **Bad:** dead on arrival, `getav health` returns 0 or 1, all SPECIAL zero, or a CTD initialising the actor.

### 4F. Terminals with no MODL `MEDIUM`
Six new TERMs, all placed, all persistent and enabled. Retail has MODL on 310/342.
```
coc RSCharlieInterior     coc RSDeltaInterior     coc RSFoxtrotInterior
player.moveto XX001E84    player.moveto XX001EBB  player.moveto XX001EDA
```
- **Bad:** nothing visible but an activation prompt fires when you point at empty space; or a missing-mesh marker.

### 4G. Items and creatures `MEDIUM`
```
player.additem XX006F76 1    Eagle Flag Poll
player.additem XX006F36 1    Stealth Pistol
player.additem XX004CF1 1    Vulpes' Locker Key
player.additem XX003324 1    Lyons' Pride Power Armor
player.additem XX00338A 1    Advanced power armor
```
**Drop each on the ground** — that's what exercises the missing OBND (83/83 new WEAP, 124/180 new ARMO, 17/17 NOTE lack it).

FO3 creature spawns (`coc TestQAItems` first, one at a time):
```
player.placeatme XX0033DB 1   Liberty Prime
player.placeatme XX0033AF 1   Super Mutant Behemoth
player.placeatme XX0033C3 1   Mirelurk King
player.placeatme XX0033DD 1   Dogmeat
```
- **Good:** all render with real geometry — `Fallout - Meshes.bsa` **does** contain `libertyprime`, `smbehemoth`, `mirelurkking`, `zaxeye`, `powerarmorcomplete`.
- **Bad:** missing-mesh cross (art is confirmed present, so this indicts the record), CTD on `placeatme`, or a dropped item falling through the floor.
- **Not a bug:** all three FO3 power armors looking identical to plain T-45d — they share one MODL with no texture swap.

### 4H. Map markers `MEDIUM`
Open the Pip-Boy world map on a fresh game.

- **Canary:** master marker `0010C6CC` is **renamed** by the plugin from "Deserted Shack" to "Wastelander Shack" (+XSCL). Two "Wastelander Shack" entries should exist, at grid (6,6) and (6,10). This is a guaranteed visible change — use it to confirm the plugin is loaded.
- **Duplicates:** three markers all named **"Cerulean Robotics"** at the identical position, one each in FreesideWorld / FreesideNorthWorld / FreesideFortWorld.
- **Likely mid-air:** `cow WastelandNV -24 22` (Griffith Peak, z=12544), `cow WastelandNV -18 -10` (Devil Peak, z=12600). Also `cow WastelandNV -16 -18` (Calada), `cow WastelandNV 21 1` (Camp Willow).
- **Bad:** floating/buried markers, fast-travel dropping you through the world, or a CTD when the map draws one.

### 4I. Dialogue volume `LOW`
- **Placeholder flood:** `coc CampForlornHopeCommandCenter` → `prid 00120DDA` → Tech Sergeant Reyes. All **108** of his INFOs read `(NOT FOUND IN CRASH DUMP)`. Walk the tree; Goodbye should always exit.
- **Dead-end topics:** `coc Lucky38Penthouse` → `prid 001161E8` (Mr. House). Six of his choice topics have zero INFOs in *both* the plugin and the master. **Good:** they never appear in the menu (engine filters them). **Bad:** one appears, you select it, and nothing is spoken.
- **Custom goodbye:** `prid 000E701A` → `moveto player` (QJHawkins). Exhaust 6 topics, then Goodbye. **Bad:** a line plays but the camera stays locked — the classic non-terminating goodbye, the highest-value softlock signal in this build.

### 4J. Aces Theater actors moved to the Tops `MEDIUM` — do while you're in TOPSCasino (P1.3)
Four master persistent ACHRs moved out of `TOPSAcesTheater` into `TOPSCasino` by the plugin, **keeping their Aces-Theater coordinates**: `0016616E` TommyToriniREF, `0016616F/70/71` RadPack1–3REF. The plugin does not override the Aces Theater cell at all.
```
coc TOPSAcesTheater      <-- stage should be conspicuously empty of its act
coc TOPSCasino           <-- Tommy + Rad Pack standing on the main floor
prid 0016616E ; getpos x ; getpos y ; getpos z    (expect 4312 / 2632 / 8448)
```
- **Bad:** they render in **both** cells — the engine kept the master's registration alongside ours, the same failure mode as the v141 crash. Or a CTD on either `coc`. Clipping into furniture is expected given the unchanged coordinates.

### 4K. Two more cross-cell actor moves `LOW`
```
coc RSFoxtrotInterior                              (Lenk, 00152E90, enabled — should be indoors)
coc Vault34a ; prid 00138AAD ; enable ; moveto player   (v34Overseer, moved from Vault34b, initially disabled)
```

---

## 5. Expectation-setting — what will look wrong but is NOT a bug

| Observation | Why it's correct |
|---|---|
| No radio audio, no Pip-Boy station from the v142 XRDO | Base TACT has no SNAM/RNAM and carries the Non-Pipboy flag. Physically impossible. |
| Broadcast type 4 | Fallout3.esm ships a byte-identical record. Authentic FO3 data. |
| Nothing visible when you `coc` somewhere | **493 of 55,686 placed refs are initially disabled** — 281 plugin-new, 212 overrides. The 212 match the master exactly (no enable-state clobbering). `getdisabled` first, `enable` second. |
| MegatonPlayerHouse has no floor | Master ships this cell with **zero** children. The plugin adds only persistent markers/furniture/activators — no architecture. `tcl` on arrival. |
| `(NOT FOUND IN CRASH DUMP)` in 68% of dialogue (1,486 of 2,187 INFOs) | Response text wasn't resident in the memory capture. Whether to emit the placeholder or drop the record is a **converter-policy call for you**, not a parse failure. |
| A weapon named literally "GUN THAT SHOULD BE REMOVED" | Authentic Feb-2010 FULL on `XX006F4A`. |
| Fallout 3 content everywhere — metro/vault/Rivet City ASPCs, 117 of 139 CREA, 52 ARMO, Megaton, Tenpenny | Feb-2010 FNV prototype still carrying FO3 base data. xex44 (Apr 2010) purged CREA hard (117→11) but kept ACTI 484 / DOOR 231 / LIGH 140 as FO3-only too. **Do not say "xex44 removed the FO3 content."** |
| Three FO3 power armors look identical to T-45d | One shared MODL, no texture swap. |
| Quests with no stages; `VFreeformRSFoxtrot` with one script variable | Verified authored state, not a drop. `sqv` prints running state / current stage / script vars — it does **not** enumerate stages. |
| Ulysses in generic clothing | All four Ulysses outfit records are xex44-only. |
| Outer Strip cells at x=3 near-empty (LAND + 1–4 REFR) | Thin edges are correct. |
| Cass appears normally | `RoseofSharonCassidy` `00133FDD` is present in **both** builds. Only the variant `RoseofSharonCassidyOld` is xex44-only. |
| Missing-mesh crosses with the prototype BSAs unloaded | See §0.2. Resolve the archive question before filing anything. |
| Local map blank in the new worldspaces (0×0 usable dimensions) | FalloutNV.esm itself ships `FFEncounterWorld`, `FXInvertedDaylightWorld` and `TestMap01` with byte-identical all-zero MNAM. Cosmetic. |
| `TheStripWorld` persistent cell stamped XCLC (0,0) alongside a real (0,0) cell | 13 of 13 master worldspaces with a persistent cell do exactly this. Standard engine practice. |
| Overridden dialogue text differing from retail | Genuine draft-vs-final editorial change ("I was already bored before you showed up" vs "This job's boring enough…"). Carries no defect signal. |

---

## 6. What this test CANNOT cover

- **The v142 ASPC fix as shipped.** Zero references to any of the 52 ASPCs exist in the file; all 104 XCAS links point into master space. In FNV a CELL's XCAS is the *only* path from an ASPC to audible output. Without the manual FNVEdit wiring in Pass 2B Step 3, nothing changed that a player can hear. The fix is **correct and inert**.
- **Which of the two possible ASPC failures you're seeing.** A silent result in the Tenpenny test cannot distinguish "SNAM[0] not reaching the engine" from "ASPC rejected because INAM=0 flags it exterior." Only the paired experiment narrows it, and even then INAM remains a decision for you.
- **Engine semantics for INAM, ANAM and null SNAM slots.** Every claim about what the audio engine does with those is inference from data + xEdit field definitions. **Not confirmed by decompilation.**
- **Whether the engine honours our 10 non-retail ANAM values** (1, 3, 4, 5, 9, 12, 14, 17, 18, 20). Legal FO3 enum values on a shared engine, but FNV's DSP preset table was not verifiable from the files.
- **What a zeroed XTEL actually does.** Proven: retail never ships one, and the arrival marker sits a median 85 units from the door so the data is genuinely lost. Whether FNV falls back to the door position or the worldspace origin was **not** confirmed by decompilation — the in-game test is the arbiter.
- **Whether the 3 mis-parented Strip persistent refs are load-order-hazardous.** The structure is unprecedented (0 instances across 467,217 master records) but the engine may simply tolerate it.
- **The 163 placed refs the v141 fix dropped.** With no override, the master's placement governs — indistinguishable in-game from correct behaviour. You cannot tell whether the fix restored correctness or silently discarded proto-specific placements.
- **Capture-specific enable state.** All 51,196 master-override refs now carry bit-identical header flags to the master, so the plugin can no longer express *any* enable state that differs from retail. If the Feb-2010 dump genuinely had something toggled differently, that information is gone. Only a comparison against the dump itself can check this.
- **~17,000 of the 18,780 new records.** Only 167 distinct plugin-new base objects are placed anywhere. 7,512 of 7,553 new STAT, 565 of 631 ACTI, all 92 MSTT / 293 SOUN / 33 MUSC / 49 VTYP / 124 ECZN are placed by nothing — and the non-placeable types can't even be console-spawned.
- **295 of the 296 new player-facing items** have no placed object. The single exception you can find by exploring is NOTE `XX004E3C` in `RocketLabBasement` (`coc RocketLabBasement` → `player.moveto XX001D59`).
- **Whether an absent record was unauthored in Feb 2010 or merely not resident in that dump.** Presence is strong evidence; absence is weak.
- **Voice audio / lip-sync.** INFO records carry no voice paths. A silent line does not distinguish "blank NAM1" from "missing .ogg/.lip asset" — which is exactly why subtitles must be on for Pass 4A.
- **Terrain height under the new markers.** LAND exists at every landing cell checked, but VHGT was not decoded — floating vs buried vs on-the-ground is genuinely unknown from the file.
- **Whether xex21's Strip layout is "correct" for Feb 2010.** No oracle exists: `TheStripWorld` is absent from retail and xex44 has only a 1-cell stub. Only internal consistency is checkable, and on that score the data is sound (complete LAND on all 39 cells, coherent NAVI at master 4,782 + exactly 46).
- **The HEDR record count reading 55,079 against an actual 74,494.** Not chased, not part of the v142 delta, not ruled harmless.
- **xex44.v142 anything.** It has its own independent `0x01` FormID space (`XX004FB6` is a QUST in xex21 and a STAT in xex44). Never carry a console ID between builds.

---

### Time-boxed? Stop here.

If you have **20 minutes**: §0 setup → **P1.2** (Strip cow + persistents) → **P1.3** (exit doors) → **Pass 2B Step 1** (error log) → **4B** (quest titles, proves overrides load).

If you have **one hour**: add **P1.4** (Nipton), **3B** (Powder Gangers, new game), **4A** (combat barks), **4D** (repointed rangers).

The full Pass 2B Step 3 (FNVEdit wiring) is the only thing that actually *tests* the headline v142 fix. Everything else in Pass 2 either confirms it structurally or rules out collateral damage.