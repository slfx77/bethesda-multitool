using System.Globalization;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Units;
using BethesdaMultitool.Core.Modeling.Xngine;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Catalog;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Xngine;

/// <summary>
///     The XnGine reader's static catalog description: it constructs inside Shared's bounds, declares the guard row as
///     its default units, states every unit row and the identification chain in its unit policy, names one variant per
///     tag and plane-header size in the probe's evidence spelling, and carries the D7/D8 rules with their diagnostics.
/// </summary>
public class XnGineModelFormatMetadataTests
{
    [Fact]
    public void Description_Constructs_WithTheGuardRowAsDefaultUnits()
    {
        var description = XnGineModelFormatMetadata.Description;

        Assert.Equal("bmt.xngine.3d", XnGineModelFormatMetadata.FormatId);
        Assert.StartsWith("XnGine .3D mesh (v2.5, v2.6, v2.7", description.DisplayName, StringComparison.Ordinal);
        Assert.Equal(1.0, description.DefaultUnits.MetersPerUnit);
        Assert.Equal(SceneValueProvenance.Unknown, description.DefaultUnits.Provenance);
        Assert.Equal(ClassicModelUnits.UnknownEvidence, description.DefaultUnits.Evidence);
        Assert.Equal(16 * 1024 * 1024, XnGineModelFormatMetadata.MaximumSourceBytes);
    }

    [Fact]
    public void Variants_AreOnePerTagAndPlaneHeaderSize_InTheProbeSpelling()
    {
        var variants = XnGineModelFormatMetadata.Description.SupportedVariants;

        Assert.Equal(6, variants.Count);
        Assert.Equal("XnGine v2.5 mesh, 8-byte plane headers", variants[0]);
        Assert.Equal("XnGine v2.5 mesh, 10-byte plane headers", variants[1]);
        Assert.Equal("XnGine v2.7 mesh, 10-byte plane headers", variants[5]);
        Assert.Equal(variants.Count, variants.Distinct(StringComparer.Ordinal).Count());

        // The probe evidence is the variant plus the counts, so a variant is the evidence's prefix and not the evidence.
        var evidence = XnGineModelFormatMetadata.Describe("v2.7", 8, 48, 34);
        Assert.Equal("XnGine v2.7 mesh, 8-byte plane headers, 48 points, 34 planes", evidence);
        Assert.Contains(variants, v => evidence.StartsWith(v + ", ", StringComparison.Ordinal));
        Assert.DoesNotContain(evidence, variants);
    }

    [Fact]
    public void UnitPolicy_StatesEveryRowAndTheChain()
    {
        var policy = XnGineModelFormatMetadata.Description.UnitPolicy;

        foreach (var row in ClassicModelUnits.Rows)
        {
            Assert.Contains(row.Name, policy, StringComparison.Ordinal);
            Assert.Contains(row.MetersPerUnit.ToString("R", CultureInfo.InvariantCulture), policy,
                StringComparison.Ordinal);
        }

        Assert.Contains(BethesdaModelRegistration.GameOption, policy, StringComparison.Ordinal);
        Assert.Contains(BethesdaModelRegistration.ClassicGameOption, policy, StringComparison.Ordinal);
        Assert.Contains(XnGineGameIdentity.AmbiguousDiagnostic, policy, StringComparison.Ordinal);
        Assert.Contains(XnGineGameIdentity.StepRefutedDiagnostic, policy, StringComparison.Ordinal);
        Assert.Contains(ClassicModelUnits.ActorScaleDiagnostic, policy, StringComparison.Ordinal);
        Assert.Contains("header +20", policy, StringComparison.Ordinal);
        Assert.True(policy.Length <= ModelStaticAdmissionRule.MaximumTextLength);
    }

    [Fact]
    public void Rules_HaveUniqueIds_AndCarryTheDecisions()
    {
        var rules = XnGineModelFormatMetadata.Description.AdmissionRules;

        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(new[]
            {
                "recognition", "supported-key", "3dc-shape", "fxart-tags", "not-a-model", "game-identification",
                "game-unknown", "units", "budgets", "corrupt-input"
            },
            rules.Select(r => r.Id).ToArray());

        var fxart = Assert.Single(rules, r => r.Id == "fxart-tags");
        Assert.Contains(XnGineModelFormatMetadata.FxartUnsupportedReason, fxart.Handling, StringComparison.Ordinal);
        Assert.Equal("Redguard 3dfx mesh (10-byte plane header, point indices) not decoded: later cut",
            XnGineModelFormatMetadata.FxartUnsupportedReason);

        var unknown = Assert.Single(rules, r => r.Id == "game-unknown");
        Assert.Contains(XnGineGameIdentity.AmbiguousDiagnostic, unknown.Handling, StringComparison.Ordinal);
        Assert.Contains(ClassicModelUnits.DaggerfallMetersPerUnit.ToString("R", CultureInfo.InvariantCulture),
            unknown.Handling, StringComparison.Ordinal);

        var identification = Assert.Single(rules, r => r.Id == "game-identification");
        Assert.Contains(XnGineGameIdentity.ContainerDisagreesDiagnostic, identification.Handling,
            StringComparison.Ordinal);
        Assert.Contains(BethesdaModelRegistration.ClassicGameOption, identification.Handling, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeVariant_FormatsTagAndHeaderSize()
    {
        Assert.Equal("XnGine v2.6 mesh, 10-byte plane headers", XnGineModelFormatMetadata.DescribeVariant("v2.6", 10));
        Assert.Throws<ArgumentNullException>(() => XnGineModelFormatMetadata.DescribeVariant(null!, 8));
    }
}
