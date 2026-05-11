using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Export;

public class ParityAuditCoreTests
{
    [Fact]
    public void Compare_BothFilledAgree_CountsAgreeOnly()
    {
        var (esm, dmp) = MakeWeaponPair(
            esm: w => w with { Damage = 50, Value = 100 },
            dmp: w => w with { Damage = 50, Value = 100 });

        var result = ParityAuditCore.Compare("esm", esm, FormIdResolver.Empty, "dmp", dmp, FormIdResolver.Empty);

        var weapon = GetType("Weapon", result);
        Assert.Equal(1, weapon.MatchedRecordCount);
        var damage = weapon.Fields.Single(f => f.FieldName == "Damage");
        Assert.Equal(0, damage.EsmOnly);
        Assert.Equal(0, damage.DmpOnly);
        Assert.Equal(0, damage.Disagree);
        Assert.Equal(1, damage.Agree);
    }

    [Fact]
    public void Compare_BothFilledDifferent_CountsDisagree()
    {
        var (esm, dmp) = MakeWeaponPair(
            esm: w => w with { Damage = 50 },
            dmp: w => w with { Damage = 75 });

        var result = ParityAuditCore.Compare("esm", esm, FormIdResolver.Empty, "dmp", dmp, FormIdResolver.Empty);

        var damage = GetType("Weapon", result).Fields.Single(f => f.FieldName == "Damage");
        Assert.Equal(1, damage.Disagree);
        Assert.Equal(0, damage.Agree);

        var ex = Assert.Single(damage.Examples);
        Assert.Equal(FieldStatus.Disagree, ex.Status);
        Assert.Equal("50", ex.EsmValue);
        Assert.Equal("75", ex.DmpValue);
    }

    [Fact]
    public void Compare_OnlyEsmFilled_CountsEsmOnly()
    {
        var (esm, dmp) = MakeWeaponPair(
            esm: w => w with { ModelPath = "weapons/rifle.nif" },
            dmp: w => w with { ModelPath = null });

        var result = ParityAuditCore.Compare("esm", esm, FormIdResolver.Empty, "dmp", dmp, FormIdResolver.Empty);

        var modelPath = GetType("Weapon", result).Fields.Single(f => f.FieldName == "ModelPath");
        Assert.Equal(1, modelPath.EsmOnly);
        Assert.Equal(0, modelPath.DmpOnly);

        var ex = Assert.Single(modelPath.Examples);
        Assert.Equal(FieldStatus.EsmOnly, ex.Status);
        Assert.Equal("weapons/rifle.nif", ex.EsmValue);
        Assert.Equal("", ex.DmpValue);
    }

    [Fact]
    public void Compare_OnlyDmpFilled_CountsDmpOnly()
    {
        var (esm, dmp) = MakeWeaponPair(
            esm: w => w with { ModelPath = null },
            dmp: w => w with { ModelPath = "weapons/rifle.nif" });

        var result = ParityAuditCore.Compare("esm", esm, FormIdResolver.Empty, "dmp", dmp, FormIdResolver.Empty);

        var modelPath = GetType("Weapon", result).Fields.Single(f => f.FieldName == "ModelPath");
        Assert.Equal(0, modelPath.EsmOnly);
        Assert.Equal(1, modelPath.DmpOnly);
    }

    [Fact]
    public void Compare_BothEmptyOrZero_FieldOmittedFromReport()
    {
        // Both sides leave ModelPath null and Damage zero — these fields
        // should not appear in the field list (no signal worth reporting).
        var (esm, dmp) = MakeWeaponPair(
            esm: w => w with { ModelPath = null, Damage = 0 },
            dmp: w => w with { ModelPath = null, Damage = 0 });

        var result = ParityAuditCore.Compare("esm", esm, FormIdResolver.Empty, "dmp", dmp, FormIdResolver.Empty);

        var weapon = GetType("Weapon", result);
        Assert.DoesNotContain(weapon.Fields, f => f.FieldName == "ModelPath");
        Assert.DoesNotContain(weapon.Fields, f => f.FieldName == "Damage");
    }

    [Fact]
    public void Compare_RecordOnlyInOneSide_CountedAtRecordLevelNotFieldLevel()
    {
        var esm = new RecordCollection
        {
            Weapons =
            [
                new WeaponRecord { FormId = 0x100, Damage = 50, EditorId = "WeapEsmOnly" },
                new WeaponRecord { FormId = 0x200, Damage = 25, EditorId = "WeapBoth" }
            ]
        };
        var dmp = new RecordCollection
        {
            Weapons =
            [
                new WeaponRecord { FormId = 0x200, Damage = 25, EditorId = "WeapBoth" },
                new WeaponRecord { FormId = 0x300, Damage = 75, EditorId = "WeapDmpOnly" }
            ]
        };

        var result = ParityAuditCore.Compare("esm", esm, FormIdResolver.Empty, "dmp", dmp, FormIdResolver.Empty);

        var weapon = GetType("Weapon", result);
        Assert.Equal(2, weapon.EsmRecordCount);
        Assert.Equal(2, weapon.DmpRecordCount);
        Assert.Equal(1, weapon.MatchedRecordCount);
        Assert.Equal(1, weapon.EsmOnlyRecordCount);
        Assert.Equal(1, weapon.DmpOnlyRecordCount);

        // Field counts only count the matched record (0x200), not the unmatched ones.
        var damage = weapon.Fields.Single(f => f.FieldName == "Damage");
        Assert.Equal(1, damage.Agree);
        Assert.Equal(0, damage.EsmOnly);
        Assert.Equal(0, damage.DmpOnly);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("0.0")]
    [InlineData("0.00")]
    [InlineData("0.000")]
    [InlineData("0.0000")]
    [InlineData("False")]
    [InlineData("None")]
    [InlineData("-")]
    [InlineData("0x00000000")]
    public void IsDefaultLike_TreatsTheseAsEmpty(string value)
    {
        Assert.True(ParityAuditCore.IsDefaultLike(value));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0.1")]
    [InlineData("True")]
    [InlineData("path/to/model.nif")]
    [InlineData("0x00000001")]
    public void IsDefaultLike_TreatsTheseAsFilled(string value)
    {
        Assert.False(ParityAuditCore.IsDefaultLike(value));
    }

    [Fact]
    public void Compare_ExamplesCappedAtConfiguredLimit()
    {
        // 7 weapons all show EsmOnly on ModelPath; with examplesPerField=3, only 3 are kept.
        var esmList = new List<WeaponRecord>();
        var dmpList = new List<WeaponRecord>();
        for (uint i = 1; i <= 7; i++)
        {
            esmList.Add(new WeaponRecord { FormId = i, ModelPath = $"path{i}.nif" });
            dmpList.Add(new WeaponRecord { FormId = i, ModelPath = null });
        }

        var esm = new RecordCollection { Weapons = esmList };
        var dmp = new RecordCollection { Weapons = dmpList };

        var result = ParityAuditCore.Compare(
            "esm", esm, FormIdResolver.Empty,
            "dmp", dmp, FormIdResolver.Empty,
            examplesPerField: 3);

        var modelPath = GetType("Weapon", result).Fields.Single(f => f.FieldName == "ModelPath");
        Assert.Equal(7, modelPath.EsmOnly);
        Assert.Equal(3, modelPath.Examples.Count);
    }

    private static (RecordCollection esm, RecordCollection dmp) MakeWeaponPair(
        Func<WeaponRecord, WeaponRecord> esm,
        Func<WeaponRecord, WeaponRecord> dmp)
    {
        var baseRecord = new WeaponRecord { FormId = 0xDEAD, EditorId = "WeapTest" };
        return (
            new RecordCollection { Weapons = [esm(baseRecord)] },
            new RecordCollection { Weapons = [dmp(baseRecord)] });
    }

    private static RecordTypeParity GetType(string typeName, ParityAuditResult result)
    {
        return result.RecordTypes.Single(t => t.TypeName == typeName);
    }
}
