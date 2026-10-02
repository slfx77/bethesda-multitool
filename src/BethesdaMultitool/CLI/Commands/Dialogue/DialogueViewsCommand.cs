using System.CommandLine;
using System.Text.Json;
using BethesdaMultitool.Core.Formats.Dialogue.CreationKit;

namespace BethesdaMultitool.CLI.Commands.Dialogue;

/// <summary>Inspects optional Creation Kit diagrams without treating their content as plugin records.</summary>
internal static class DialogueViewsCommand
{
    /// <summary>Creates the dialogue views command for XML, folder, and ZIP inputs.</summary>
    /// <returns>The registered command.</returns>
    internal static Command Create()
    {
        var command = new Command("views", "Inspect Creation Kit DialogueViews XML sidecars");
        var input = new Argument<string>("input") { Description = "XML file, DialogueViews folder, or Scripts.zip" };
        var json = new Option<bool>("--json") { Description = "Write invariant JSON including authored layout and unresolved references" };
        command.Arguments.Add(input);
        command.Options.Add(json);
        command.SetAction((result, cancellationToken) =>
        {
            try
            {
                var documents = DialogueViewReader.ReadPath(result.GetValue(input)!, cancellationToken);
                if (result.GetValue(json))
                {
                    using var output = Console.OpenStandardOutput();
                    using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
                    WriteJson(writer, documents);
                }
                else
                {
                    foreach (var document in documents)
                    {
                        Console.WriteLine($"{document.Source}: {document.Nodes.Count} nodes, {document.Links.Count} links (diagram {document.Version})");
                        foreach (var node in document.Nodes)
                        {
                            Console.WriteLine($"  {node.Id}: {node.Text.ReplaceLineEndings(" | ")}");
                        }
                    }
                }

                return Task.FromResult(0);
            }
            catch (OperationCanceledException) { return Task.FromResult(130); }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException or ArgumentException)
            {
                Console.Error.WriteLine(exception.Message);
                return Task.FromResult(1);
            }
        });
        return command;
    }

    /// <summary>Writes a reflection-free machine-readable projection of authored diagram content.</summary>
    /// <param name="writer">The caller-owned JSON writer.</param>
    /// <param name="documents">The loaded sidecars.</param>
    internal static void WriteJson(Utf8JsonWriter writer, IReadOnlyList<DialogueViewDocument> documents)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", 1);
        writer.WriteStartArray("diagrams");
        foreach (var document in documents)
        {
            writer.WriteStartObject();
            writer.WriteString("source", document.Source);
            writer.WriteString("version", document.Version);
            writer.WriteStartArray("nodes");
            foreach (var node in document.Nodes)
            {
                writer.WriteStartObject();
                writer.WriteString("id", node.Id);
                writer.WriteString("kind", node.Kind);
                writer.WriteString("text", node.Text);
                writer.WriteString("toolTip", node.ToolTip);
                if (node.Bounds is { } bounds)
                {
                    writer.WriteStartObject("bounds");
                    writer.WriteNumber("x", bounds.X);
                    writer.WriteNumber("y", bounds.Y);
                    writer.WriteNumber("width", bounds.Width);
                    writer.WriteNumber("height", bounds.Height);
                    writer.WriteEndObject();
                }
                writer.WriteStartArray("formIds");
                foreach (var formId in node.FormIds) { writer.WriteStringValue($"{formId:X8}"); }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("links");
            foreach (var link in document.Links)
            {
                writer.WriteStartObject();
                writer.WriteString("id", link.Id);
                writer.WriteString("originId", link.OriginId);
                writer.WriteString("destinationId", link.DestinationId);
                writer.WriteString("text", link.Text);
                writer.WriteStartArray("points");
                foreach (var point in link.Points) { writer.WriteStringValue(point); }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteString("xml", document.OriginalXml);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
