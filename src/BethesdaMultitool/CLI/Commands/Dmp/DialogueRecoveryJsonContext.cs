using System.Text.Json.Serialization;

namespace BethesdaMultitool.CLI.Commands.Dmp;

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(DmpDialogueRecoveryCommand.CorpusManifest), TypeInfoPropertyName = "CorpusManifest")]
[JsonSerializable(typeof(DmpDialogueRecoveryCommand.SourceReport), TypeInfoPropertyName = "SourceReport")]
internal partial class DialogueRecoveryJsonContext : JsonSerializerContext;
