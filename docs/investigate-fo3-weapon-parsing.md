# Investigation: Fallout 3 Weapon Parsing — Missing Projectile Physics Section

## Problem

When comparing Fallout 3 weapons against Fallout New Vegas weapons in the cross-build HTML report, the FO3 weapon records are missing the "Projectile Physics" section that FNV weapons display. All FO3 weapons also show as "Hand to Hand" skill type, suggesting the weapon data subrecord (DNAM) isn't being parsed correctly for FO3's format.

## Context

- FO3 uses HEDR version 0.94; FNV uses 1.32–1.35 (detected via `EsmFileHeader.DetectedGame`)
- FO3 ESMs are little-endian (PC); FNV Xbox ESMs are big-endian
- The cross-build comparison tool uses `--base "Sample/ESM/fallout_3"` to include FO3 as the baseline
- FO3 has 6 ESMs: `Fallout3.esm` + 5 DLC ESMs, all merged via `RecordCollection.MergeWith()`

## Likely Root Cause

The weapon DNAM subrecord has different layouts between FO3 and FNV:

- **FO3 DNAM**: 200 bytes — different field ordering, no weapon mods, different skill indices
- **FNV DNAM**: 204 bytes — extended with mod slots, different skill enum values

The weapon parser likely hardcodes the FNV DNAM layout and doesn't handle FO3's shorter/different format.

### Skill Index Difference

FO3 skill indices differ from FNV:
- FO3: Big Guns = 1, Small Guns = 9, Energy Weapons = 2 (13 skills total)
- FNV: Guns = 9 (replaces Small Guns), Energy Weapons = 2, no Big Guns (renamed to Guns)

If the parser reads the skill index from the wrong offset in the DNAM, it may read 0 (Hand to Hand/Unarmed) for all weapons.

## Where to Investigate

1. **`src/FalloutXbox360Utils/Core/Formats/Esm/Parsing/Handlers/WeaponRecordHandler.cs`**
   - Find the DNAM subrecord parsing
   - Check if it handles FO3's 200-byte layout vs FNV's 204-byte layout
   - The `DetectedGame` property on `EsmFileHeader` can be used to branch

2. **`src/FalloutXbox360Utils/Core/Formats/Esm/Models/Records/Item/WeaponRecord.cs`**
   - Check which fields are populated — Projectile, ProjectileCount, etc.
   - FO3 weapons should have projectile data (most guns fire projectiles)

3. **`src/FalloutXbox360Utils/Core/Formats/Esm/Export/GeckItemDetailWriter.cs`**
   - The "Projectile Physics" section rendering — check what fields it requires
   - If the parser doesn't populate those fields for FO3, the section is skipped

4. **Reference**: FO3 DNAM layout can be found in:
   - xEdit source code (TES5Edit/FO3Edit)
   - UESP wiki: https://en.uesp.net/wiki/Fallout3:Mod_File_Format/WEAP

## Verification

1. Pick a known FO3 weapon (e.g., `WeapAssaultRifle` FormID `0x0000434F`)
2. Run: `dotnet run --project src/FalloutXbox360Utils -f net10.0 -- show "Sample/ESM/fallout_3/Fallout3.esm" 0x0000434F`
3. Check if Skill, Projectile, and other DNAM fields are populated
4. Compare against xEdit's display of the same record
