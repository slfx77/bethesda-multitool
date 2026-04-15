using System.Xml;
using System.Xml.Linq;

namespace FalloutXbox360Utils.Tests.App.Accessibility;

/// <summary>
///     Static-analysis pass over the WinUI 3 XAML files under <c>src/FalloutXbox360Utils/App/</c>.
///     Finds interactive controls whose accessible name is likely empty to a screen reader.
///
///     A control is considered "has a name" if any of these holds:
///     <list type="bullet">
///         <item><c>AutomationProperties.Name</c> is set (literal or bound).</item>
///         <item><c>AutomationProperties.LabeledBy</c> is set.</item>
///         <item><c>x:Uid</c> is set — resource-backed name/content from Resources.resw.</item>
///         <item>
///             Content-bearing controls (Button, CheckBox, ToggleButton, RadioButton) have
///             <c>Content=</c> set to a literal string, a <c>{x:Bind}</c> expression bound to a
///             plausibly-text property, or a nested element like <c>&lt;TextBlock Text="…"/&gt;</c>.
///         </item>
///     </list>
///     False positives are expected; this is a best-effort static scan designed to be the
///     low-water mark for a ratchet test, not a full AutomationPeer simulation.
/// </summary>
internal static class XamlAccessibilityScanner
{
    private static readonly XNamespace WinUiNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    ///     Interactive control element names we want every instance of to have an accessible name.
    ///     Non-interactive presentation elements (TextBlock, Border, Image, Grid, …) are
    ///     intentionally excluded — they don't need <c>AutomationProperties.Name</c>.
    /// </summary>
    internal static readonly HashSet<string> InteractiveControls = new(StringComparer.Ordinal)
    {
        "Button",
        "HyperlinkButton",
        "RepeatButton",
        "ToggleButton",
        "DropDownButton",
        "SplitButton",
        "CheckBox",
        "RadioButton",
        "ToggleSwitch",
        "TextBox",
        "PasswordBox",
        "AutoSuggestBox",
        "RichEditBox",
        "ComboBox",
        "ListView",
        "GridView",
        "TreeView",
        "Slider",
        "NumberBox",
        "DatePicker",
        "TimePicker",
        "FlipView"
    };

    internal sealed record Gap(string File, int LineNumber, string ControlType, string? LocalIdentifier);

    /// <summary>Scan every <c>*.xaml</c> file under <paramref name="appDirectory" /> and return gaps.</summary>
    internal static IReadOnlyList<Gap> Scan(string appDirectory)
    {
        var gaps = new List<Gap>();
        foreach (var file in Directory.EnumerateFiles(appDirectory, "*.xaml", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(appDirectory, file).Replace('\\', '/');
            XDocument doc;
            try
            {
                doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            }
            catch (XmlException)
            {
                continue;
            }

            foreach (var element in doc.Descendants())
            {
                if (!InteractiveControls.Contains(element.Name.LocalName))
                    continue;

                if (HasAccessibleName(element))
                    continue;

                var lineInfo = (IXmlLineInfo)element;
                var identifier = element.Attribute(XamlNs + "Name")?.Value
                                 ?? element.Attribute("Name")?.Value;
                gaps.Add(new Gap(relativePath, lineInfo.LineNumber, element.Name.LocalName, identifier));
            }
        }

        return gaps;
    }

    private static bool HasAccessibleName(XElement element)
    {
        // Attached properties appear as elements with the fully-qualified local name
        // "AutomationProperties.Name" / "AutomationProperties.LabeledBy" when XDocument
        // does not know the namespace mapping — inspect both attribute and child forms.
        if (HasAttribute(element, "AutomationProperties.Name")) return true;
        if (HasAttribute(element, "AutomationProperties.LabeledBy")) return true;
        if (element.Elements().Any(e => e.Name.LocalName is "AutomationProperties.Name" or "AutomationProperties.LabeledBy"))
            return true;

        if (HasAttribute(element, "x:Uid")) return true;

        // Content-bearing controls can derive their accessible name from Content.
        if (element.Name.LocalName is "Button" or "CheckBox" or "ToggleButton" or "RadioButton"
            or "HyperlinkButton" or "DropDownButton" or "SplitButton" or "RepeatButton")
        {
            if (HasContentWithText(element)) return true;
        }

        // Container controls (ListView/TreeView/GridView/FlipView/ComboBox) with Header set
        // can also expose a name via the header text. Keep strict for now — containers with
        // meaningful headers are rare in this codebase.
        return false;
    }

    private static bool HasAttribute(XElement element, string name)
    {
        foreach (var attr in element.Attributes())
        {
            if (attr.Name.LocalName.Equals(name, StringComparison.Ordinal))
                return true;
            // Handle cases like `{http://…}x:Uid` vs raw `x:Uid`.
            if (attr.IsNamespaceDeclaration) continue;
            var rendered = attr.Name.NamespaceName.Length == 0
                ? attr.Name.LocalName
                : $"{GetPrefix(attr.Name.Namespace)}:{attr.Name.LocalName}";
            if (rendered.Equals(name, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string GetPrefix(XNamespace ns)
    {
        if (ns == XamlNs) return "x";
        return ns.NamespaceName;
    }

    private static bool HasContentWithText(XElement element)
    {
        var contentAttr = element.Attribute("Content");
        if (contentAttr != null)
        {
            var value = contentAttr.Value;
            // A bound content ({x:Bind SomeProperty} or {Binding Path=...}) may or may not
            // surface as accessible text. Treat bindings that obviously reference a name-like
            // property as OK; otherwise require an explicit Name.
            if (!value.StartsWith('{'))
                return true;
            if (value.Contains("Name", StringComparison.Ordinal) ||
                value.Contains("Title", StringComparison.Ordinal) ||
                value.Contains("DisplayName", StringComparison.Ordinal) ||
                value.Contains("Label", StringComparison.Ordinal) ||
                value.Contains("Text", StringComparison.Ordinal))
                return true;
            return false;
        }

        // <Button><TextBlock Text="Something" /></Button> pattern.
        foreach (var child in element.Elements())
        {
            if (child.Name.LocalName == "TextBlock")
            {
                var textAttr = child.Attribute("Text");
                if (textAttr != null && !string.IsNullOrEmpty(textAttr.Value))
                    return true;
                if (child.Attribute(XamlNs + "Uid") != null)
                    return true;
            }
        }

        return false;
    }
}
