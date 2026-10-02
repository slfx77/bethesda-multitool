using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace BethesdaMultitool.Core.Formats.Dialogue.CreationKit;

/// <summary>Reads optional Creation Kit DialogueViews sidecars without modifying plugin records.</summary>
public static class DialogueViewReader
{
    private const long MaximumDocumentCharacters = 16 * 1024 * 1024;
    private const int MaximumDocuments = 4096;
    private const long MaximumCollectionBytes = 128L * 1024 * 1024;
    private static readonly Regex FormIdPattern = new(@"(?<![0-9A-Za-z])(?:0x)?([0-9A-Fa-f]{8})(?![0-9A-Za-z])",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>Reads one XML diagram with external entities disabled and a bounded document size.</summary>
    /// <param name="input">The XML stream, retained by the caller.</param>
    /// <param name="source">The file or archive-entry identity for diagnostics.</param>
    /// <returns>The diagram, including uninterpreted original XML and unresolved links.</returns>
    public static DialogueViewDocument Read(Stream input, string source)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumDocumentCharacters,
            CloseInput = false
        });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        var root = document.Root;
        if (root is null || root.Name.LocalName != "Diagram")
        {
            throw new InvalidDataException("A DialogueViews sidecar must contain a Diagram root.");
        }

        var nodes = new List<DialogueViewNode>();
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in Child(root, "Nodes")?.Elements() ?? [])
        {
            if (element.Name.LocalName != "Node") { continue; }
            var id = RequiredId(element);
            if (!nodeIds.Add(id)) { throw new InvalidDataException($"Duplicate diagram node ID: {id}"); }
            var tooltip = Child(element, "ToolTip")?.Value ?? string.Empty;
            var formIds = FormIdPattern.Matches(tooltip).Select(match =>
                uint.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).Distinct().ToArray();
            var text = string.Join(Environment.NewLine, element.Descendants()
                .Where(item => item.Name.LocalName is "Text" or "Caption")
                .Select(item => item.Value).Where(value => !string.IsNullOrWhiteSpace(value)));
            nodes.Add(new DialogueViewNode(id, element.Attribute("Class")?.Value ?? string.Empty,
                text, tooltip, ParseBounds(Child(element, "Bounds")?.Value), formIds));
        }

        var links = new List<DialogueViewLink>();
        var linkIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in Child(root, "Links")?.Elements() ?? [])
        {
            if (element.Name.LocalName != "Link") { continue; }
            var id = RequiredId(element);
            if (!linkIds.Add(id)) { throw new InvalidDataException($"Duplicate diagram link ID: {id}"); }
            links.Add(new DialogueViewLink(id, Child(element, "Origin")?.Attribute("Id")?.Value,
                Child(element, "Destination")?.Attribute("Id")?.Value, Child(element, "Text")?.Value ?? string.Empty,
                Child(element, "Points")?.Elements().Where(point => point.Name.LocalName == "Point")
                    .Select(point => point.Value).ToArray() ?? []));
        }

        return new DialogueViewDocument(source, root.Attribute("Version")?.Value ?? string.Empty,
            nodes, links, document.ToString(SaveOptions.DisableFormatting));
    }

    /// <summary>Reads one XML file, a selected folder, or DialogueViews entries in a ZIP sidecar.</summary>
    /// <param name="path">The file, folder, or ZIP path.</param>
    /// <param name="cancellationToken">Cancels between documents.</param>
    /// <returns>The diagrams in ordinal source-path order.</returns>
    public static IReadOnlyList<DialogueViewDocument> ReadPath(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        var results = new List<DialogueViewDocument>();
        long collectionBytes = 0;
        if (Directory.Exists(path))
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            var files = Directory.EnumerateFiles(path, "*", options)
                .Where(file => Path.GetExtension(file).Equals(".xml", StringComparison.OrdinalIgnoreCase))
                .Take(MaximumDocuments + 1).Order(StringComparer.Ordinal).ToArray();
            if (files.Length > MaximumDocuments) { throw new InvalidDataException("Too many dialogue-view documents."); }
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var stream = File.OpenRead(file);
                collectionBytes = CheckCollectionSize(collectionBytes, stream.Length);
                results.Add(Read(stream, file));
            }
        }
        else if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(path);
            var entries = zip.Entries.Where(entry => IsDialogueViewEntry(entry.FullName))
                .OrderBy(entry => entry.FullName, StringComparer.Ordinal).Take(MaximumDocuments + 1).ToArray();
            if (entries.Length > MaximumDocuments) { throw new InvalidDataException("Too many dialogue-view documents."); }
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Length > MaximumDocumentCharacters * 4) { throw new InvalidDataException("Dialogue-view entry exceeds the size limit."); }
                collectionBytes = CheckCollectionSize(collectionBytes, entry.Length);
                using var stream = entry.Open();
                results.Add(Read(stream, path + "!/" + entry.FullName));
            }
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(path);
            results.Add(Read(stream, path));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return results;
    }

    /// <summary>Bounds retained XML across a folder or ZIP before parsing another document.</summary>
    private static long CheckCollectionSize(long current, long next)
    {
        if (next < 0 || next > MaximumCollectionBytes - current)
        {
            throw new InvalidDataException("Dialogue-view collection exceeds the 128 MiB size limit.");
        }
        return current + next;
    }

    /// <summary>Finds a direct child by local name, allowing namespace-qualified diagrams.</summary>
    private static XElement? Child(XElement parent, string name) => parent.Elements().FirstOrDefault(element => element.Name.LocalName == name);

    /// <summary>Reads a required nonempty diagram-local identifier.</summary>
    private static string RequiredId(XElement element)
        => element.Attribute("Id")?.Value is { Length: > 0 } id ? id : throw new InvalidDataException("Diagram item has no ID.");

    /// <summary>Parses invariant finite geometry while retaining missing geometry as absent.</summary>
    private static DialogueViewBounds? ParseBounds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) { return null; }
        var parts = value.Split(',');
        if (parts.Length != 4) { throw new InvalidDataException("Invalid dialogue-view bounds."); }
        var numbers = new double[4];
        for (var index = 0; index < numbers.Length; index++)
        {
            if (!double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[index]) || !double.IsFinite(numbers[index]))
            {
                throw new InvalidDataException("Invalid dialogue-view coordinate.");
            }
        }

        if (numbers[2] < 0 || numbers[3] < 0) { throw new InvalidDataException("Dialogue-view dimensions cannot be negative."); }
        return new DialogueViewBounds(numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    /// <summary>Matches root XML files or XML files inside a DialogueViews directory in a ZIP.</summary>
    private static bool IsDialogueViewEntry(string name)
    {
        var normalized = name.Replace('\\', '/');
        return normalized.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
               (!normalized.Contains('/') || normalized.Split('/').Contains("DialogueViews", StringComparer.OrdinalIgnoreCase));
    }
}
