using System.Text.Json;
using BethesdaMultitool.CLI.Shared;
using Xunit;

namespace BethesdaMultitool.Tests.CLI;

public sealed class CliJsonDocumentWriterTests
{
    [Fact]
    public async Task SerializationFailureLeavesOutputEmptyEvenWhenJsonWriterDisposalFlushes()
    {
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidOperationException>(() => CliJsonDocumentWriter.WriteAsync(output, stream =>
        {
            using var writer = new Utf8JsonWriter(stream);
            writer.WriteStartObject();
            writer.WriteString("partial", "must not reach stdout");
            throw new InvalidOperationException("fixture serialization failure");
        }, TestContext.Current.CancellationToken));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task CompleteDocumentIsCopiedWithOneNewline()
    {
        using var output = new MemoryStream();
        await CliJsonDocumentWriter.WriteAsync(output, stream => stream.Write("{\"ok\":true}"u8),
            TestContext.Current.CancellationToken);
        Assert.Equal("{\"ok\":true}\n"u8.ToArray(), output.ToArray());
    }
}
