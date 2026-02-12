using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

internal static class CsvItemWriter
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
                Fmt.FId(w.FormId),
                Fmt.CsvEscape(w.EditorId),
                Fmt.CsvEscape(w.FullName),
                ((int)w.WeaponType).ToString(),
                Fmt.CsvEscape(w.WeaponTypeName),
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
                Fmt.FIdN(w.CriticalEffectFormId),
                w.Value.ToString(),
                w.Weight.ToString("F2"),
                w.Health.ToString(),
                Fmt.FIdN(w.AmmoFormId),
                Fmt.Resolve(w.AmmoFormId ?? 0, lookup),
                Fmt.FIdN(w.ProjectileFormId),
                Fmt.Resolve(w.ProjectileFormId ?? 0, lookup),
                Fmt.FIdN(w.ImpactDataSetFormId),
                Fmt.Resolve(w.ImpactDataSetFormId ?? 0, lookup),
                w.ActionPoints.ToString("F1"),
                Fmt.CsvEscape(w.ModelPath),
                Fmt.FIdN(w.PickupSoundFormId),
                Fmt.Resolve(w.PickupSoundFormId ?? 0, lookup),
                Fmt.FIdN(w.PutdownSoundFormId),
                Fmt.Resolve(w.PutdownSoundFormId ?? 0, lookup),
                Fmt.FIdN(w.FireSound3DFormId),
                Fmt.Resolve(w.FireSound3DFormId ?? 0, lookup),
                Fmt.FIdN(w.FireSoundDistFormId),
                Fmt.Resolve(w.FireSoundDistFormId ?? 0, lookup),
                Fmt.FIdN(w.FireSound2DFormId),
                Fmt.Resolve(w.FireSound2DFormId ?? 0, lookup),
                Fmt.FIdN(w.DryFireSoundFormId),
                Fmt.Resolve(w.DryFireSoundFormId ?? 0, lookup),
                Fmt.FIdN(w.IdleSoundFormId),
                Fmt.Resolve(w.IdleSoundFormId ?? 0, lookup),
                Fmt.FIdN(w.EquipSoundFormId),
                Fmt.Resolve(w.EquipSoundFormId ?? 0, lookup),
                Fmt.FIdN(w.UnequipSoundFormId),
                Fmt.Resolve(w.UnequipSoundFormId ?? 0, lookup),
                w.ProjectileData?.Speed.ToString("F1") ?? "",
                w.ProjectileData?.Gravity.ToString("F4") ?? "",
                w.ProjectileData?.Range.ToString("F0") ?? "",
                w.ProjectileData?.Force.ToString("F1") ?? "",
                Fmt.FIdN(w.ProjectileData?.ExplosionFormId),
                Fmt.Resolve(w.ProjectileData?.ExplosionFormId ?? 0, lookup),
                Fmt.FIdN(w.ProjectileData?.ActiveSoundLoopFormId),
                Fmt.Resolve(w.ProjectileData?.ActiveSoundLoopFormId ?? 0, lookup),
                Fmt.CsvEscape(w.ProjectileData?.ModelPath),
                Fmt.Endian(w.IsBigEndian),
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
                Fmt.FId(m.FormId),
                Fmt.CsvEscape(m.EditorId),
                Fmt.CsvEscape(m.FullName),
                Fmt.CsvEscape(m.Description),
                m.Value.ToString(),
                m.Weight.ToString("F2"),
                Fmt.CsvEscape(m.ModelPath),
                Fmt.Endian(m.IsBigEndian),
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
                Fmt.FId(a.FormId),
                Fmt.CsvEscape(a.EditorId),
                Fmt.CsvEscape(a.FullName),
                a.DamageThreshold.ToString("F1"),
                a.DamageResistance.ToString(),
                a.Value.ToString(),
                a.Weight.ToString("F2"),
                a.Health.ToString(),
                Fmt.CsvEscape(a.ModelPath),
                Fmt.Endian(a.IsBigEndian),
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
                Fmt.FId(a.FormId),
                Fmt.CsvEscape(a.EditorId),
                Fmt.CsvEscape(a.FullName),
                a.Speed.ToString("F2"),
                a.Value.ToString(),
                a.Weight.ToString("F2"),
                a.ClipRounds.ToString(),
                a.Flags.ToString(),
                Fmt.FIdN(a.ProjectileFormId),
                Fmt.Resolve(a.ProjectileFormId ?? 0, lookup),
                Fmt.CsvEscape(a.ModelPath),
                Fmt.CsvEscape(a.ProjectileModelPath),
                Fmt.Endian(a.IsBigEndian),
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
                Fmt.FId(c.FormId),
                Fmt.CsvEscape(c.EditorId),
                Fmt.CsvEscape(c.FullName),
                c.Value.ToString(),
                c.Weight.ToString("F2"),
                Fmt.FIdN(c.AddictionFormId),
                Fmt.Resolve(c.AddictionFormId ?? 0, lookup),
                c.AddictionChance.ToString("F2"),
                Fmt.CsvEscape(c.ModelPath),
                Fmt.Endian(c.IsBigEndian),
                c.Offset.ToString(),
                "", ""));

            foreach (var effectId in c.EffectFormIds)
            {
                sb.AppendLine(string.Join(",",
                    "EFFECT",
                    Fmt.FId(c.FormId),
                    "", "", "", "", "", "", "", "",
                    "", "",
                    Fmt.FId(effectId),
                    Fmt.Resolve(effectId, lookup)));
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
                Fmt.FId(m.FormId),
                Fmt.CsvEscape(m.EditorId),
                Fmt.CsvEscape(m.FullName),
                m.Value.ToString(),
                m.Weight.ToString("F2"),
                Fmt.CsvEscape(m.ModelPath),
                Fmt.Endian(m.IsBigEndian),
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
                Fmt.FId(k.FormId),
                Fmt.CsvEscape(k.EditorId),
                Fmt.CsvEscape(k.FullName),
                k.Value.ToString(),
                k.Weight.ToString("F2"),
                Fmt.CsvEscape(k.ModelPath),
                Fmt.Endian(k.IsBigEndian),
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
                Fmt.FId(c.FormId),
                Fmt.CsvEscape(c.EditorId),
                Fmt.CsvEscape(c.FullName),
                c.Respawns.ToString(),
                Fmt.CsvEscape(c.ModelPath),
                Fmt.FIdN(c.Script),
                Fmt.Endian(c.IsBigEndian),
                c.Offset.ToString(),
                "", "", ""));

            foreach (var item in c.Contents)
            {
                sb.AppendLine(string.Join(",",
                    "ITEM",
                    Fmt.FId(c.FormId),
                    "", "", "", "", "",
                    "", "",
                    Fmt.FId(item.ItemFormId),
                    Fmt.Resolve(item.ItemFormId, lookup),
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
                Fmt.FId(b.FormId),
                Fmt.CsvEscape(b.EditorId),
                Fmt.CsvEscape(b.FullName),
                b.Value.ToString(),
                b.Weight.ToString("F2"),
                b.TeachesSkill.ToString(),
                b.SkillTaught.ToString(),
                Fmt.CsvEscape(b.Text),
                Fmt.CsvEscape(b.ModelPath),
                Fmt.Endian(b.IsBigEndian),
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
                Fmt.FId(r.FormId),
                Fmt.CsvEscape(r.EditorId),
                Fmt.CsvEscape(r.FullName),
                r.RequiredSkill.ToString(),
                r.RequiredSkillLevel.ToString(),
                Fmt.Resolve(r.CategoryFormId, lookup),
                r.Ingredients.Count.ToString(),
                r.Outputs.Count.ToString(),
                Fmt.Endian(r.IsBigEndian),
                r.Offset.ToString()));
        }

        return sb.ToString();
    }
}
