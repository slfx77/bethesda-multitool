using System.Text;
using System.Xml;
using BethesdaMultitool.Core.Localization;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Localization;

/// <summary>Protects live display-message ownership and the unchanged machine-culture boundary.</summary>
public sealed class LocalizedTextValueTests
{
    /// <summary>Rejects unsafe XML and ambiguous keys while leaving caller stream ownership intact.</summary>
    [Fact]
    public void ResourceReaderRejectsEntitiesAndDuplicateIdentifiers()
    {
        using var duplicate = new MemoryStream(Encoding.UTF8.GetBytes("<root><data name='Same'><value>A</value></data><data name='Same'><value>B</value></data></root>"));
        Assert.Throws<InvalidDataException>(() => EnglishResources.Read(duplicate));
        Assert.True(duplicate.CanRead);
        using var entities = new MemoryStream(Encoding.UTF8.GetBytes("<!DOCTYPE root [<!ENTITY x SYSTEM 'file:///private'>]><root><data name='Bad'><value>&x;</value></data></root>"));
        Assert.Throws<XmlException>(() => EnglishResources.Read(entities));
    }

    /// <summary>Checks the embedded fallback actually contains the live-settings and compound accessibility resources.</summary>
    [Fact]
    public void EmbeddedFallbackContainsRuntimeAndAccessibilityContracts()
    {
        Assert.Contains("layout", EnglishResources.All["Settings_Pseudo.Content"], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("restart", EnglishResources.All["Settings_LanguageHelp.Text"], StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(EnglishResources.All["Explore_SourcePath.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name"]);
        Assert.NotEmpty(EnglishResources.All["Viewer_SelectNpc"]);
        Assert.NotEmpty(EnglishResources.All["ShadowkeyNative_CloseBlocked.Text"]);
        Assert.NotEmpty(EnglishResources.All["Shadowkey_Slots.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name"]);
        Assert.NotEmpty(EnglishResources.All["Shadowkey_Frame.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name"]);
        Assert.NotEmpty(EnglishResources.All["Shadowkey_Skin.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name"]);
        Assert.NotEmpty(EnglishResources.All["Shadowkey_Status.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name"]);
    }

}
