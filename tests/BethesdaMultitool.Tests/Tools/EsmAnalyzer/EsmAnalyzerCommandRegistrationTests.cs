using System.Buffers.Binary;
using System.CommandLine;
using System.Security.Cryptography;
using System.Text;
using BethesdaMultitool.Tests.Helpers;
using EsmAnalyzer.Commands;
using EsmAnalyzer.Commands.Records;
using Xunit;

namespace BethesdaMultitool.Tests.Tools.EsmAnalyzer;

public sealed class EsmAnalyzerCommandRegistrationTests
{
    [Fact]
    public void Program_registers_each_previously_dead_command_once_and_lists_it_in_default_help()
    {
        var source = SourceContract.ReadSource("tools", "EsmAnalyzer", "Program.cs");
        var registrations = new[]
        {
            "rootCommand.Subcommands.Add(HashCommands.CreateHashCommand());",
            "rootCommand.Subcommands.Add(HashCommands.CreateHashCompareCommand());",
            "rootCommand.Subcommands.Add(QuestCommands.CreateCompareQuestLinksCommand());",
            "rootCommand.Subcommands.Add(RecordSchemaCommands.CreateValidateSubrecordsCommand());"
        };

        foreach (var registration in registrations)
        {
            Assert.Equal(1, SourceContract.CountOccurrences(source, registration));
        }

        Assert.Contains("[cyan]hash[/]", source, StringComparison.Ordinal);
        Assert.Contains("[cyan]hash-compare[/]", source, StringComparison.Ordinal);
        Assert.Contains("[cyan]compare-quest-links[/]", source, StringComparison.Ordinal);
        Assert.Contains("[cyan]validate-subrecords[/]", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("hash", "Compute a file hash", "--algo")]
    [InlineData("hash-compare", "Compare hashes of two files", "--algo")]
    [InlineData("compare-quest-links", "Compare QUST link data", "--formid")]
    [InlineData("validate-subrecords", "Validate subrecords against known schemas", "--types")]
    public void Command_help_is_parseable_and_describes_its_arguments(
        string commandName,
        string description,
        string option)
    {
        var command = CreateCommand(commandName);
        var root = new RootCommand("Test root");
        root.Subcommands.Add(command);
        using var output = new StringWriter();
        var parseResult = root.Parse([commandName, "--help"]);

        Assert.Empty(parseResult.Errors);
        Assert.Equal(0, parseResult.Invoke(new InvocationConfiguration
        {
            Output = output,
            Error = output,
            EnableDefaultExceptionHandler = false
        }));

        var help = output.ToString();
        Assert.Contains(description, help, StringComparison.Ordinal);
        Assert.Contains(option, help, StringComparison.Ordinal);
        Assert.All(command.Arguments,
            argument => Assert.Contains($"<{argument.Name}>", help, StringComparison.Ordinal));
    }

    [Fact]
    public void Hash_command_writes_the_sha256_of_a_synthetic_file()
    {
        var directory = CreateTempDirectory();
        try
        {
            var inputPath = Path.Combine(directory, "input.bin");
            var outputPath = Path.Combine(directory, "input.sha256");
            byte[] input = [0x00, 0x01, 0xFE, 0xFF, 0x42];
            File.WriteAllBytes(inputPath, input);

            var exitCode = Invoke(CreateCommand("hash"), inputPath, "--output", outputPath);

            Assert.Equal(0, exitCode);
            var expectedHash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            Assert.Equal($"SHA256 {expectedHash}  input.bin\n", File.ReadAllText(outputPath));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Compare_quest_links_command_runs_against_synthetic_esms()
    {
        var directory = CreateTempDirectory();
        try
        {
            var leftPath = Path.Combine(directory, "left.esm");
            var samePath = Path.Combine(directory, "same.esm");
            var differentPath = Path.Combine(directory, "different.esm");
            var left = BuildQuestEsm(10);
            File.WriteAllBytes(leftPath, left);
            File.WriteAllBytes(samePath, left);
            File.WriteAllBytes(differentPath, BuildQuestEsm(11));

            Assert.Equal(0, Invoke(CreateCommand("compare-quest-links"),
                leftPath, samePath, "--formid", "0x00001000"));
            Assert.Equal(1, Invoke(CreateCommand("compare-quest-links"),
                leftPath, differentPath, "--formid", "0x00001000"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Validate_subrecords_command_distinguishes_known_and_unknown_synthetic_subrecords()
    {
        var directory = CreateTempDirectory();
        try
        {
            var knownPath = Path.Combine(directory, "known.esm");
            var unknownPath = Path.Combine(directory, "unknown.esm");
            File.WriteAllBytes(knownPath, BuildSchemaTestEsm(false));
            File.WriteAllBytes(unknownPath, BuildSchemaTestEsm(true));

            Assert.Equal(0, Invoke(CreateCommand("validate-subrecords"), knownPath, "--types", "QUST"));
            Assert.Equal(1, Invoke(CreateCommand("validate-subrecords"), unknownPath, "--types", "QUST"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static Command CreateCommand(string commandName)
    {
        return commandName switch
        {
            "hash" => HashCommands.CreateHashCommand(),
            "hash-compare" => HashCommands.CreateHashCompareCommand(),
            "compare-quest-links" => QuestCommands.CreateCompareQuestLinksCommand(),
            "validate-subrecords" => RecordSchemaCommands.CreateValidateSubrecordsCommand(),
            _ => throw new ArgumentOutOfRangeException(nameof(commandName), commandName, null)
        };
    }

    private static int Invoke(Command command, params string[] arguments)
    {
        var root = new RootCommand("Test root");
        root.Subcommands.Add(command);
        var parseResult = root.Parse([command.Name, .. arguments]);
        Assert.Empty(parseResult.Errors);
        return parseResult.Invoke(new InvocationConfiguration
        {
            Output = TextWriter.Null,
            Error = TextWriter.Null,
            EnableDefaultExceptionHandler = false
        });
    }

    private static byte[] BuildQuestEsm(int objectiveIndex)
    {
        var scri = UInt32Bytes(0x00002000);
        var qobj = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(qobj, objectiveIndex);
        var qsta = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(qsta, 0x00003000);
        qsta[4] = 1;

        var script = EsmTestFileBuilder.BuildRecord("SCPT", 0x00002000, 0,
            ("EDID", NullTerminated("SyntheticQuestScript")));
        var quest = EsmTestFileBuilder.BuildRecord("QUST", 0x00001000, 0,
            ("EDID", NullTerminated("SyntheticQuest")),
            ("SCRI", scri),
            ("QOBJ", qobj),
            ("QSTA", qsta));

        return new EsmTestFileBuilder()
            .AddTopLevelGrup("SCPT", script)
            .AddTopLevelGrup("QUST", quest)
            .Build();
    }

    private static byte[] BuildSchemaTestEsm(bool includeUnknown)
    {
        var subrecords = new List<(string sig, byte[] data)>
        {
            ("EDID", NullTerminated("SyntheticQuest"))
        };
        if (includeUnknown)
        {
            subrecords.Add(("ZZZZ", [0x01, 0x02, 0x03]));
        }

        var quest = EsmTestFileBuilder.BuildRecord("QUST", 0x00001000, 0, subrecords.ToArray());
        return new EsmTestFileBuilder().AddTopLevelGrup("QUST", quest).Build();
    }

    private static byte[] UInt32Bytes(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] NullTerminated(string value)
    {
        return Encoding.ASCII.GetBytes(value + '\0');
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"esm-analyzer-commands-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}