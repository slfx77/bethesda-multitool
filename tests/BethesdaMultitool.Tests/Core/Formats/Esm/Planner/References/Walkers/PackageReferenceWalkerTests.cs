using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Planner.References;
using BethesdaMultitool.Core.Formats.Esm.Planner.References.Walkers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.References.Walkers;

public sealed class PackageReferenceWalkerTests
{
    [Fact]
    public void Pldt_Type0_Yields_Union_With_Pldt_Container_Signature()
    {
        var pack = new PackageRecord
        {
            FormId = 0x000ABCDE,
            Location = new PackageLocation { Type = 0, Union = 0x000ABCDF }
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
            Location = new PackageLocation { Type = 2, Union = 0x000ABCDF } // NearCurrentLocation.
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
            Target = new PackageTarget { Type = 0, FormIdOrType = 0x000ABCDF }
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
            Target = new PackageTarget { Type = 2, FormIdOrType = 17 } // Object type enum.
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
            CombatStyleFormId = 0x000ABCD0
        };
        var walker = new PackageReferenceWalker();

        var refs = walker.Walk(pack).ToList();
        var cnam = Assert.Single(refs, r => r.FieldPath == "CNAM");

        Assert.Equal(0x000ABCD0u, cnam.FormId);
    }

    [Fact]
    public void Ctda_Reference_Yields_Only_Semantic_Fnv_Slots()
    {
        var pack = new PackageRecord
        {
            FormId = 0x000ABCDE,
            Conditions =
            [
                new DialogueCondition { RunOn = 2, Reference = 0x000ABCD0 },
                new DialogueCondition { RunOn = 2, Reference = 0 },
                new DialogueCondition { RunOn = 4, Reference = 0x000ABCD1 },
                new DialogueCondition { FunctionIndex = 0x006A, RunOn = 2, Reference = 0x000ABCD2 },
                new DialogueCondition { RunOn = 2, Reference = 0x000ABCD3 }
            ]
        };
        var walker = new PackageReferenceWalker();

        var refs = walker.Walk(pack).ToList();

        Assert.Contains(refs, r => r.FieldPath == "CTDA[0].Reference" && r.FormId == 0x000ABCD0);
        Assert.Contains(refs, r => r.FieldPath == "CTDA[4].Reference" && r.FormId == 0x000ABCD3);
        Assert.DoesNotContain(refs, r => r.FieldPath == "CTDA[1].Reference");
        Assert.DoesNotContain(refs, r => r.FieldPath == "CTDA[2].Reference");
        Assert.DoesNotContain(refs, r => r.FieldPath == "CTDA[3].Reference");
    }

    [Fact]
    public void Event_Actions_Yield_Idle_And_Topic_References_In_Block_Order()
    {
        var pack = new PackageRecord
        {
            FormId = 0x000ABCDE,
            OnBegin = new PackageEventAction
            {
                IdleFormId = 0x000A0001,
                TopicFormId = 0x000A0002
            },
            OnEnd = new PackageEventAction
            {
                IdleFormId = 0x000A0003,
                TopicFormId = 0x000A0004
            },
            OnChange = new PackageEventAction
            {
                IdleFormId = 0x000A0005,
                TopicFormId = 0x000A0006
            }
        };

        var refs = new PackageReferenceWalker().Walk(pack).ToList();

        Assert.Collection(
            refs,
            reference => AssertReference(reference, "OnBegin.INAM", 0x000A0001),
            reference => AssertReference(reference, "OnBegin.TNAM", 0x000A0002),
            reference => AssertReference(reference, "OnEnd.INAM", 0x000A0003),
            reference => AssertReference(reference, "OnEnd.TNAM", 0x000A0004),
            reference => AssertReference(reference, "OnChange.INAM", 0x000A0005),
            reference => AssertReference(reference, "OnChange.TNAM", 0x000A0006));
    }

    [Fact]
    public void Event_Scripts_Yield_Ordered_Scro_References_And_Ignore_Scrv_Locals()
    {
        var pack = new PackageRecord
        {
            FormId = 0x000ABCDE,
            OnBegin = new PackageEventAction
            {
                Scripts =
                [
                    new DialogueResultScript
                    {
                        ReferencedObjects = [0x000A0010, 0x80000007, 0x000A0011]
                    },
                    new DialogueResultScript
                    {
                        ReferencedObjects = [0x80000008, 0x000A0012]
                    }
                ]
            },
            OnEnd = new PackageEventAction
            {
                Scripts = [new DialogueResultScript { ReferencedObjects = [0x000A0013] }]
            },
            OnChange = new PackageEventAction
            {
                Scripts = [new DialogueResultScript { ReferencedObjects = [0x000A0014] }]
            }
        };

        var refs = new PackageReferenceWalker().Walk(pack).ToList();

        Assert.Collection(
            refs,
            reference => AssertReference(reference, "OnBegin.Scripts[0].SCRO[0]", 0x000A0010),
            reference => AssertReference(reference, "OnBegin.Scripts[0].SCRO[2]", 0x000A0011),
            reference => AssertReference(reference, "OnBegin.Scripts[1].SCRO[1]", 0x000A0012),
            reference => AssertReference(reference, "OnEnd.Scripts[0].SCRO[0]", 0x000A0013),
            reference => AssertReference(reference, "OnChange.Scripts[0].SCRO[0]", 0x000A0014));
        Assert.DoesNotContain(refs, reference => (reference.FormId & 0x80000000u) != 0);
    }

    private static void AssertReference(
        RawReference reference,
        string fieldPath,
        uint formId)
    {
        Assert.Equal(fieldPath, reference.FieldPath);
        Assert.Equal(formId, reference.FormId);
    }
}