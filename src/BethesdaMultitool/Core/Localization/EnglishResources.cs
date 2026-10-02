using System.Resources;
using System.Xml;
using System.Xml.Linq;

namespace BethesdaMultitool.Core.Localization;

/// <summary>Reads the authored English fallback and property keys independently of WinUI.</summary>
internal static class EnglishResources
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Values = new(LoadEmbedded);

    /// <summary>Gets immutable-by-contract resource text shared by the UI lookup adapters.</summary>
    internal static IReadOnlyDictionary<string, string> All => Values.Value;

    /// <summary>Reads a bounded resource document without permitting external entities.</summary>
    /// <param name="stream">The readable resource stream, retained by the caller.</param>
    /// <returns>Distinct resource identifiers and their exact authored values.</returns>
    /// <exception cref="InvalidDataException">A resource identifier, value, or root is invalid.</exception>
    internal static IReadOnlyDictionary<string, string> Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = 4 * 1024 * 1024, CloseInput = false
        });
        var document = XDocument.Load(reader);
        if (document.Root?.Name != "root") throw new InvalidDataException("Expected a resw root element.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in document.Root.Elements("data"))
        {
            var key = (string?)item.Attribute("name");
            var value = item.Element("value");
            if (string.IsNullOrWhiteSpace(key) || value is null || item.Elements("value").Skip(1).Any() || !values.TryAdd(key, value.Value))
                throw new InvalidDataException("Resource names must be nonempty and unique, with one value.");
        }
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(values);
    }

    /// <summary>Loads the same resw source that is compiled into the GUI's PRI.</summary>
    /// <returns>The authored fallback catalog.</returns>
    private static IReadOnlyDictionary<string, string> LoadEmbedded()
    {
        using var stream = typeof(EnglishResources).Assembly.GetManifestResourceStream("BethesdaMultitool.Localization.en-US.resw")
            ?? throw new MissingManifestResourceException("The English UI resource catalog was not embedded.");
        return Read(stream);
    }
}
