using System.Xml.Linq;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

/// <summary>Guards Windows native dependency admission without loading WinUI in the portable test host.</summary>
public sealed class NativeSceneProjectTopologySourceContractTests
{
    /// <summary>The shared native presenter stays in the GUI graph and cannot inherit the application's publish globals.</summary>
    [Fact]
    public void NativePresenterReferenceIsWindowsOnlyAndIsolatesApplicationGlobals()
    {
        var project = XDocument.Parse(SourceContract.ReadSource("src", "BethesdaMultitool", "BethesdaMultitool.csproj"));
        var reference = Assert.Single(project.Descendants("ProjectReference"), element =>
            ((string?)element.Attribute("Include"))?.EndsWith(
                "/Slfx77.Multitool.WinUI.Direct3D12.csproj", StringComparison.Ordinal) == true);
        Assert.NotNull(reference.Parent);
        Assert.Equal("ItemGroup", reference.Parent.Name.LocalName);
        Assert.Equal("$(TargetFramework.Contains('windows'))", (string?)reference.Parent.Attribute("Condition"));

        var properties = ((string?)reference.Attribute("AdditionalProperties") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
        Assert.Equal(6, properties.Count);
        Assert.Equal("net10.0-windows10.0.19041.0", properties["TargetFramework"]);
        Assert.Empty(properties["TargetFrameworks"]);
        Assert.Empty(properties["RuntimeIdentifier"]);
        Assert.Empty(properties["RuntimeIdentifiers"]);
        Assert.Equal("false", properties["SelfContained"]);
        Assert.Equal("false", properties["PublishSingleFile"]);
        var removed = ((string?)reference.Attribute("GlobalPropertiesToRemove") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(properties.Count, removed.Count);
        foreach (var property in properties.Keys) Assert.Contains(property, removed);

        // The native adapter/helper stay excluded alongside the rest of App from the portable compilation.
        var appExclusion = Assert.Single(project.Descendants("Compile"), element =>
            ((string?)element.Attribute("Remove"))?.Replace('\\', '/') == "App/**/*.cs");
        Assert.Equal("!$(TargetFramework.Contains('windows'))", (string?)appExclusion.Parent?.Attribute("Condition"));
    }
}
