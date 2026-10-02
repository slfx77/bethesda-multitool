using System.Globalization;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Localization;
using Microsoft.Windows.ApplicationModel.Resources;
using Slfx77.Multitool.Core.Localization;

namespace BethesdaMultitool.Localization;

/// <summary>Snapshots explicitly qualified MRT resources so background messages do not share a mutable resource context.</summary>
internal static class MrtStringCatalog
{
    /// <summary>Creates a display catalog without changing any process or thread culture.</summary>
    /// <param name="culture">The requested display culture.</param>
    /// <param name="pseudoLocalize">Whether to expand the final display text for layout testing.</param>
    /// <returns>Selected resource strings with the embedded English fallback.</returns>
    internal static IStringCatalog Create(CultureInfo culture, bool pseudoLocalize)
    {
        var manager = new ResourceManager(ResourceLoader.GetDefaultResourceFilePath());
        var context = manager.CreateResourceContext();
        context.QualifierValues["Language"] = culture.Name.Length == 0 ? "en-US" : culture.Name;
        var map = manager.MainResourceMap.GetSubtree("Resources");
        var selected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, fallback) in EnglishResources.All)
        {
            try { selected[key] = map.GetValue(key.Replace('.', '/'), context).ValueAsString; }
            catch (COMException) { selected[key] = fallback; }
        }
        var catalogs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["en-US"] = EnglishResources.All
        };
        if (culture.Name.Length > 0) catalogs[culture.Name] = selected;
        return new StringCatalog(catalogs, culture, pseudoLocalize);
    }
}
