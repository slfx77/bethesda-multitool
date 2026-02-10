using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

public static partial class CsvReportGenerator
{
    public static string GenerateWeaponsCsv(List<WeaponRecord> weapons, Dictionary<uint, string> lookup)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,WeaponType,WeaponTypeName,Damage,DPS,FireRate,ClipSize,MinRange,MaxRange,Spread,MinSpread,Drift,StrReq,SkillReq,CritDamage,CritChance,CritEffectFormID,Value,Weight,Health,AmmoFormID,AmmoName,ProjectileFormID,ProjectileName,ImpactDataSetFormID,ImpactDataSetName,APCost,ModelPath,PickupSoundFormID,PickupSoundName,PutdownSoundFormID,PutdownSoundName,FireSound3DFormID,FireSound3DName,FireSoundDistFormID,FireSoundDistName,FireSound2DFormID,FireSound2DName,DryFireSoundFormID,DryFireSoundName,IdleSoundFormID,IdleSoundName,EquipSoundFormID,EquipSoundName,UnequipSoundFormID,UnequipSoundName,ProjSpeed,ProjGravity,ProjRange,ProjForce,ProjExplosionFormID,ProjExplosionName,ProjInFlightSoundFormID,ProjInFlightSoundName,ProjModelPath,Endianness,Offset");

        foreach (var w in weapons.OrderBy(w => w.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "WEAPON",
                FId(w.FormId),
                E(w.EditorId),
                E(w.FullName),
                ((int)w.WeaponType).ToString(),
                E(w.WeaponTypeName),
                w.Damage.ToString(),
                w.DamagePerSecond.ToString("F1"),
                w.ShotsPerSec.ToString("F2"),
                w.ClipSize.ToString(),
                w.MinRange.ToString("F0"),
                w.MaxRange.ToString("F0"),
                w.Spread.ToString("F2"),
                w.MinSpread.ToString("F2"),
                w.Drift.ToString("F2"),
                w.StrengthRequirement.ToString(),
                w.SkillRequirement.ToString(),
                w.CriticalDamage.ToString(),
                w.CriticalChance.ToString("F2"),
                FIdN(w.CriticalEffectFormId),
                w.Value.ToString(),
                w.Weight.ToString("F2"),
                w.Health.ToString(),
                FIdN(w.AmmoFormId),
                Resolve(w.AmmoFormId ?? 0, lookup),
                FIdN(w.ProjectileFormId),
                Resolve(w.ProjectileFormId ?? 0, lookup),
                FIdN(w.ImpactDataSetFormId),
                Resolve(w.ImpactDataSetFormId ?? 0, lookup),
                w.ActionPoints.ToString("F1"),
                E(w.ModelPath),
                FIdN(w.PickupSoundFormId),
                Resolve(w.PickupSoundFormId ?? 0, lookup),
                FIdN(w.PutdownSoundFormId),
                Resolve(w.PutdownSoundFormId ?? 0, lookup),
                FIdN(w.FireSound3DFormId),
                Resolve(w.FireSound3DFormId ?? 0, lookup),
                FIdN(w.FireSoundDistFormId),
                Resolve(w.FireSoundDistFormId ?? 0, lookup),
                FIdN(w.FireSound2DFormId),
                Resolve(w.FireSound2DFormId ?? 0, lookup),
                FIdN(w.DryFireSoundFormId),
                Resolve(w.DryFireSoundFormId ?? 0, lookup),
                FIdN(w.IdleSoundFormId),
                Resolve(w.IdleSoundFormId ?? 0, lookup),
                FIdN(w.EquipSoundFormId),
                Resolve(w.EquipSoundFormId ?? 0, lookup),
                FIdN(w.UnequipSoundFormId),
                Resolve(w.UnequipSoundFormId ?? 0, lookup),
                w.ProjectileData?.Speed.ToString("F1") ?? "",
                w.ProjectileData?.Gravity.ToString("F4") ?? "",
                w.ProjectileData?.Range.ToString("F0") ?? "",
                w.ProjectileData?.Force.ToString("F1") ?? "",
                FIdN(w.ProjectileData?.ExplosionFormId),
                Resolve(w.ProjectileData?.ExplosionFormId ?? 0, lookup),
                FIdN(w.ProjectileData?.ActiveSoundLoopFormId),
                Resolve(w.ProjectileData?.ActiveSoundLoopFormId ?? 0, lookup),
                E(w.ProjectileData?.ModelPath),
                Endian(w.IsBigEndian),
                w.Offset.ToString()));
        }

        return sb.ToString();
    }

    public static string GenerateWeaponModsCsv(List<WeaponModRecord> mods)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RowType,FormID,EditorID,Name,Description,Value,Weight,ModelPath,Endianness,Offset");

        foreach (var m in mods.OrderBy(m => m.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "IMOD",
                FId(m.FormId),
                E(m.EditorId),
                E(m.FullName),
                E(m.Description),
                m.Value.ToString(),
                m.Weight.ToString("F2"),
                E(m.ModelPath),
                Endian(m.IsBigEndian),
                m.Offset.ToString()));
        }

        return sb.ToString();
    }

    public static string GenerateArmorCsv(List<ArmorRecord> armor)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RowType,FormID,EditorID,Name,DT,DR,Value,Weight,Health,ModelPath,Endianness,Offset");

        foreach (var a in armor.OrderBy(a => a.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "ARMOR",
                FId(a.FormId),
                E(a.EditorId),
                E(a.FullName),
                a.DamageThreshold.ToString("F1"),
                a.DamageResistance.ToString(),
                a.Value.ToString(),
                a.Weight.ToString("F2"),
                a.Health.ToString(),
                E(a.ModelPath),
                Endian(a.IsBigEndian),
                a.Offset.ToString()));
        }

        return sb.ToString();
    }

    public static string GenerateAmmoCsv(List<AmmoRecord> ammo, Dictionary<uint, string> lookup)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,Speed,Value,Weight,ClipRounds,Flags,ProjectileFormID,ProjectileName,ModelPath,ProjectileModelPath,Endianness,Offset");

        foreach (var a in ammo.OrderBy(a => a.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "AMMO",
                FId(a.FormId),
                E(a.EditorId),
                E(a.FullName),
                a.Speed.ToString("F2"),
                a.Value.ToString(),
                a.Weight.ToString("F2"),
                a.ClipRounds.ToString(),
                a.Flags.ToString(),
                FIdN(a.ProjectileFormId),
                Resolve(a.ProjectileFormId ?? 0, lookup),
                E(a.ModelPath),
                E(a.ProjectileModelPath),
                Endian(a.IsBigEndian),
                a.Offset.ToString()));
        }

        return sb.ToString();
    }

    public static string GenerateConsumablesCsv(List<ConsumableRecord> consumables,
        Dictionary<uint, string> lookup)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,Value,Weight,AddictionFormID,AddictionName,AddictionChance,ModelPath,Endianness,Offset,EffectFormID,EffectName");

        foreach (var c in consumables.OrderBy(c => c.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "CONSUMABLE",
                FId(c.FormId),
                E(c.EditorId),
                E(c.FullName),
                c.Value.ToString(),
                c.Weight.ToString("F2"),
                FIdN(c.AddictionFormId),
                Resolve(c.AddictionFormId ?? 0, lookup),
                c.AddictionChance.ToString("F2"),
                E(c.ModelPath),
                Endian(c.IsBigEndian),
                c.Offset.ToString(),
                "", ""));

            foreach (var effectId in c.EffectFormIds)
            {
                sb.AppendLine(string.Join(",",
                    "EFFECT",
                    FId(c.FormId),
                    "", "", "", "", "", "", "", "",
                    "", "",
                    FId(effectId),
                    Resolve(effectId, lookup)));
            }
        }

        return sb.ToString();
    }

    public static string GenerateMiscItemsCsv(List<MiscItemRecord> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RowType,FormID,EditorID,Name,Value,Weight,ModelPath,Endianness,Offset");

        foreach (var m in items.OrderBy(m => m.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "MISC",
                FId(m.FormId),
                E(m.EditorId),
                E(m.FullName),
                m.Value.ToString(),
                m.Weight.ToString("F2"),
                E(m.ModelPath),
                Endian(m.IsBigEndian),
                m.Offset.ToString()));
        }

        return sb.ToString();
    }

    public static string GenerateKeysCsv(List<KeyRecord> keys)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RowType,FormID,EditorID,Name,Value,Weight,ModelPath,Endianness,Offset");

        foreach (var k in keys.OrderBy(k => k.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "KEY",
                FId(k.FormId),
                E(k.EditorId),
                E(k.FullName),
                k.Value.ToString(),
                k.Weight.ToString("F2"),
                E(k.ModelPath),
                Endian(k.IsBigEndian),
                k.Offset.ToString()));
        }

        return sb.ToString();
    }

    public static string GenerateContainersCsv(List<ContainerRecord> containers, Dictionary<uint, string> lookup)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,Respawns,ModelPath,ScriptFormID,Endianness,Offset,ItemFormID,ItemName,Count");

        foreach (var c in containers.OrderBy(c => c.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "CONTAINER",
                FId(c.FormId),
                E(c.EditorId),
                E(c.FullName),
                c.Respawns.ToString(),
                E(c.ModelPath),
                FIdN(c.Script),
                Endian(c.IsBigEndian),
                c.Offset.ToString(),
                "", "", ""));

            foreach (var item in c.Contents)
            {
                sb.AppendLine(string.Join(",",
                    "ITEM",
                    FId(c.FormId),
                    "", "", "", "", "",
                    "", "",
                    FId(item.ItemFormId),
                    Resolve(item.ItemFormId, lookup),
                    item.Count.ToString()));
            }
        }

        return sb.ToString();
    }

    public static string GenerateBooksCsv(List<BookRecord> books)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,Value,Weight,TeachesSkill,SkillTaught,Text,ModelPath,Endianness,Offset");

        foreach (var b in books.OrderBy(b => b.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "BOOK",
                FId(b.FormId),
                E(b.EditorId),
                E(b.FullName),
                b.Value.ToString(),
                b.Weight.ToString("F2"),
                b.TeachesSkill.ToString(),
                b.SkillTaught.ToString(),
                E(b.Text),
                E(b.ModelPath),
                Endian(b.IsBigEndian),
                b.Offset.ToString()));
        }

        return sb.ToString();
    }

    public static string GenerateRecipesCsv(List<RecipeRecord> recipes,
        Dictionary<uint, string> lookup)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,RequiredSkill,RequiredLevel,Category,IngredientCount,OutputCount,Endianness,Offset");

        foreach (var r in recipes.OrderBy(r => r.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "RCPE",
                FId(r.FormId),
                E(r.EditorId),
                E(r.FullName),
                r.RequiredSkill.ToString(),
                r.RequiredSkillLevel.ToString(),
                Resolve(r.CategoryFormId, lookup),
                r.Ingredients.Count.ToString(),
                r.Outputs.Count.ToString(),
                Endian(r.IsBigEndian),
                r.Offset.ToString()));
        }

        return sb.ToString();
    }
}
