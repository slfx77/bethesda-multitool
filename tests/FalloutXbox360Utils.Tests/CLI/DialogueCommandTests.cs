using FalloutXbox360Utils.CLI.Commands.Dialogue;
using Xunit;

namespace FalloutXbox360Utils.Tests.CLI;

public sealed class DialogueCommandTests
{
    [Fact]
    public void DialogueCommand_ParsesProvenanceCommand()
    {
        var command = DialogueCommand.Create();
        var parseResult = command.Parse([
            "provenance",
            "sample.dmp",
            "0x00146E1C"
        ]);

        Assert.Empty(parseResult.Errors);
    }

    [Fact]
    public void DialogueCommand_ParsesProvenanceCommandWithHex()
    {
        var command = DialogueCommand.Create();
        var parseResult = command.Parse([
            "provenance",
            "sample.dmp",
            "0x00146E1C",
            "--hex"
        ]);

        Assert.Empty(parseResult.Errors);
    }
}