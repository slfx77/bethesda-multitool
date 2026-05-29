using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.References.Walkers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.References.Walkers;

public sealed class PackageReferenceWalkerTests
{
    [Fact]
    public void Pldt_Type0_Yields_Union_With_Pldt_Container_Signature()
    {
        var pack = new PackageRecord
        {
            FormId = 0x000ABCDE,
            Location = new PackageLocation { Type = 0, Union = 0x000ABCDF },
        };
        var walker = new PackageReferenceWalker();

        var refs = walker.Walk(pack).ToList();
        var pldt = Assert.Single(refs, r => r.FieldPath == "PLDT.Union");

        Assert.Equal(0x000ABCDFu, pldt.FormId);
        Assert.Equal("PLDT", pldt.ContainerSignature);
    }

    [Fact]
    public void Pldt_Non_Form_Id_Type_Yields_Nothing_For_Location()
    {
        var pack = new PackageRecord
        {
            FormId = 0x000ABCDE,
            Location = new PackageLocation { Type = 2, Union = 0x000ABCDF }, // NearCurrentLocation.
        };
        var walker = new PackageReferenceWalker();

        var refs = walker.Walk(pack).ToList();

        Assert.DoesNotContain(refs, r => r.FieldPath == "PLDT.Union");
    }

    [Fact]
    public void Ptdt_Type0_Yields_Form_Id()
    {
        var pack = new PackageRecord
        {
            FormId = 0x000ABCDE,
            Target = new PackageTarget { Type = 0, FormIdOrType = 0x000ABCDF },
        };
        var walker = new PackageReferenceWalker();

        var refs = walker.Walk(pack).ToList();
        var ptdt = Assert.Single(refs, r => r.FieldPath == "PTDT.FormIdOrType");

        Assert.Equal(0x000ABCDFu, ptdt.FormId);
        Assert.Equal("PTDT", ptdt.ContainerSignature);
    }

    [Fact]
    public void Ptdt_Object_Type_Skips_Form_Id_Field()
    {
        var pack = new PackageRecord
        {
            FormId = 0x000ABCDE,
            Target = new PackageTarget { Type = 2, FormIdOrType = 17 }, // Object type enum.
        };
        var walker = new PackageReferenceWalker();

        var refs = walker.Walk(pack).ToList();

        Assert.DoesNotContain(refs, r => r.FieldPath == "PTDT.FormIdOrType");
    }

    [Fact]
    public void Cnam_Yielded_When_Combat_Style_Set()
    {
        var pack = new PackageRecord
        {
            FormId = 0x000ABCDE,
            CombatStyleFormId = 0x000ABCD0,
        };
        var walker = new PackageReferenceWalker();

        var refs = walker.Walk(pack).ToList();
        var cnam = Assert.Single(refs, r => r.FieldPath == "CNAM");

        Assert.Equal(0x000ABCD0u, cnam.FormId);
    }

    [Fact]
    public void Ctda_Reference_Yields_Per_Condition_Path()
    {
        var pack = new PackageRecord
        {
            FormId = 0x000ABCDE,
            Conditions =
            [
                new DialogueCondition { Reference = 0x000ABCD0 },
                new DialogueCondition { Reference = 0 }, // No ref → not yielded.
                new DialogueCondition { Reference = 0x000ABCD1 },
            ],
        };
        var walker = new PackageReferenceWalker();

        var refs = walker.Walk(pack).ToList();

        Assert.Contains(refs, r => r.FieldPath == "CTDA[0].Reference" && r.FormId == 0x000ABCD0);
        Assert.Contains(refs, r => r.FieldPath == "CTDA[2].Reference" && r.FormId == 0x000ABCD1);
        Assert.DoesNotContain(refs, r => r.FieldPath == "CTDA[1].Reference");
    }
}
