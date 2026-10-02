using System.Text.Json.Serialization;

namespace BethesdaMultitool.Core.RuntimeSession;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true)]
[JsonSerializable(typeof(RuntimeAction))]
[JsonSerializable(typeof(RuntimeScenario))]
[JsonSerializable(typeof(RuntimeCaptureResult))]
[JsonSerializable(typeof(RuntimeTraceSummary))]
[JsonSerializable(typeof(RuntimeScriptCallReport))]
[JsonSerializable(typeof(RuntimeCommandTraceReport))]
[JsonSerializable(typeof(RuntimeRunProfile))]
[JsonSerializable(typeof(RuntimeIsolationState))]
[JsonSerializable(typeof(RuntimeLiveLaunchState))]
[JsonSerializable(typeof(RuntimeGuestManifest))]
[JsonSerializable(typeof(RuntimeGuestRunProfile))]
[JsonSerializable(typeof(RuntimeGuestRunAdmission))]
[JsonSerializable(typeof(RuntimeGamepadTraceReport))]
[JsonSerializable(typeof(RuntimeGuestRunLaunchResult))]
internal partial class RuntimeJsonContext : JsonSerializerContext { }
