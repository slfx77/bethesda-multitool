namespace BethesdaMultitool.CLI.Shared;

/// <summary>Finish serialization before touching stdout, so serialization failures leave no partial document.</summary>
internal static class CliJsonDocumentWriter
{
    internal static async Task WriteAsync(Stream output, Action<Stream> writeDocument,
        CancellationToken cancellationToken)
    {
        using var document = new MemoryStream();
        writeDocument(document);
        document.Write("\n"u8);
        document.Position = 0;
        await document.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }
}
