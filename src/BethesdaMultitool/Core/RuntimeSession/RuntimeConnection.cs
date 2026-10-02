using System.IO.Pipes;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.Json;

namespace BethesdaMultitool.Core.RuntimeSession;

/// <summary>One local engine connection. No engine behavior is simulated here.</summary>
public sealed class RuntimeConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private ulong _requestId;
    private int _captureActive;
    private int _captureStopping;
    private RuntimePluginNamespace? _pluginNamespace;
    private RuntimeGuestRunAdmission? _guestAdmission;
    private RuntimeScenario? _guestScenario;
    private RuntimeGuestRunBinding? _guestBinding;
    public JsonElement Identity { get; private set; }

    internal RuntimeConnection(Stream stream, JsonElement identity = default) { _stream = stream; Identity = identity; }

    public static async Task<RuntimeConnection> ConnectAsync(int processId, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The local runtime bridge uses Windows named pipes.");
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        var pipe = new NamedPipeClientStream(".", $"BMT.Runtime.{processId}", PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(5000, token);
            var connection = new RuntimeConnection(pipe);
            var id = await connection.SendAsync(RuntimeRequestKind.Hello, "", token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var hello = await connection.ReadAsync(timeout.Token) ?? throw new IOException("Bridge disconnected before handshake.");
            if (hello.GetProperty("kind").GetString() != "hello" || hello.GetProperty("requestId").GetUInt64() != id ||
                hello.GetProperty("protocol").GetInt32() != RuntimeProtocol.Version ||
                hello.GetProperty("processId").GetInt32() != processId)
                throw new InvalidDataException("Bridge handshake identity mismatch.");
            using var process = Process.GetProcessById(processId);
            var executable = process.MainModule ?? throw new IOException("Cannot identify the engine or emulator executable.");
            var executableIdentity = await RuntimeModuleIdentity.ExecutableIdentityAsync(executable.FileName,
                RuntimeModuleIdentity.MappedFile(process, executable), token);
            var identity = JsonNode.Parse(hello.GetRawText())!.AsObject();
            foreach (var field in executableIdentity) identity[field.Key] = field.Value?.DeepClone();
            identity["processStartedUtc"] = process.StartTime.ToUniversalTime().ToString("O");
            identity["activePluginIdentityStatus"] = "unavailable";
            identity["activePlugins"] = new JsonArray();
            var modules = new JsonArray();
            try
            {
                foreach (ProcessModule module in process.Modules)
                {
                    if (!module.ModuleName.Contains("nvse", StringComparison.OrdinalIgnoreCase)) continue;
                    var mappedFile = RuntimeModuleIdentity.MappedFile(process, module);
                    await using var moduleFile = File.OpenRead(RuntimeModuleIdentity.ReadableMappedPath(mappedFile));
                    modules.Add(new JsonObject { ["path"] = module.FileName, ["mappedFile"] = mappedFile,
                        ["sha256"] = Convert.ToHexStringLower(await SHA256.HashDataAsync(moduleFile, token)),
                        ["imageBase"] = module.BaseAddress.ToInt64(), ["imageSize"] = module.ModuleMemorySize,
                        ["hashScope"] = "mapped-module-backing-file-on-disk" });
                }
                identity["loadedRuntimeModules"] = modules;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
            {
                identity["runtimeModuleInspection"] = "unavailable: " + ex.Message;
            }
            using var identityJson = JsonDocument.Parse(identity.ToJsonString());
            connection.Identity = identityJson.RootElement.Clone();
            return connection;
        }
        catch { await pipe.DisposeAsync(); throw; }
    }

    internal async Task AttachIsolationEvidenceAsync(RuntimeRunProfile profile, string manifestPath, CancellationToken token)
    {
        var statePath = Path.Combine(profile.Root, RuntimeIsolationService.StateFileName);
        var manifestHash = await RuntimeIsolationService.HashShared(manifestPath, token);
        var stateText = await RuntimeIsolationService.ReadSharedText(statePath, token);
        using var state = JsonDocument.Parse(stateText);
        var identity = JsonNode.Parse(Identity.GetRawText())!.AsObject();
        identity["isolation"] = new JsonObject
        {
            ["mode"] = profile.Activation, ["profilePath"] = Path.GetFullPath(manifestPath),
            ["profileSha256"] = manifestHash, ["statePath"] = statePath,
            ["stateSha256"] = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(stateText))),
            ["observation"] = JsonNode.Parse(state.RootElement.GetRawText())
        };
        using var json = JsonDocument.Parse(identity.ToJsonString());
        Identity = json.RootElement.Clone();
        _pluginNamespace = new RuntimePluginNamespace(profile);
    }

    public async Task AttachGuestManifestAsync(string path, CancellationToken token = default)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is 0 or > 1024 * 1024) throw new InvalidDataException("Guest manifest size is invalid.");
        var bytes = new byte[checked((int)file.Length)];
        await file.ReadExactlyAsync(bytes, token);
        var manifest = JsonSerializer.Deserialize(bytes, RuntimeJsonContext.Default.RuntimeGuestManifest)
            ?? throw new InvalidDataException("Guest manifest is empty.");
        await manifest.ValidateAsync(Identity, token);
        var identity = JsonNode.Parse(Identity.GetRawText())!.AsObject();
        identity["guestManifest"] = new JsonObject
        {
            ["path"] = Path.GetFullPath(path),
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            ["status"] = "validated-file-pins",
            ["preparedUtc"] = manifest.PreparedUtc
        };
        using var json = JsonDocument.Parse(identity.ToJsonString());
        Identity = json.RootElement.Clone();
        _pluginNamespace = new RuntimePluginNamespace(manifest);
    }

    public async Task AttachGuestRunProfileAsync(RuntimeGuestRunProfile profile, string scenarioSha256,
        CancellationToken token = default)
    {
        if (Volatile.Read(ref _captureActive) != 0) throw new InvalidOperationException("A capture is already active.");
        _guestAdmission = null;
        _guestScenario = null;
        _guestBinding = null;
        var admission = await profile.ValidateForRunAsync(Identity, scenarioSha256, token);
        var scenarioBytes = await File.ReadAllBytesAsync(profile.Scenario.CopyPath, token);
        if (scenarioBytes.Length > 65536 || Convert.ToHexStringLower(SHA256.HashData(scenarioBytes)) != scenarioSha256)
            throw new InvalidDataException("Prepared guest scenario changed.");
        var scenario = JsonSerializer.Deserialize(scenarioBytes, RuntimeJsonContext.Default.RuntimeScenario)
            ?? throw new InvalidDataException("Prepared guest scenario is empty.");
        await AttachGuestManifestAsync(profile.GuestManifestFile.CopyPath, token);
        var identity = JsonNode.Parse(Identity.GetRawText())!.AsObject();
        identity["guestRunProfile"] = new JsonObject
        {
            ["path"] = admission.ProfilePath, ["sha256"] = admission.ProfileSha256,
            ["status"] = "validated-file-pins", ["scenarioSha256"] = scenarioSha256
        };
        using var json = JsonDocument.Parse(identity.ToJsonString());
        Identity = json.RootElement.Clone();
        _guestAdmission = admission;
        _guestScenario = scenario;
    }

    internal void ValidateGamepadScenario(RuntimeScenario scenario)
    {
        if (_guestAdmission is null || _guestScenario is null ||
            !Identity.TryGetProperty("capabilities", out var capabilities) ||
            !capabilities.TryGetProperty("gamepadBinding", out var available) || available.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("Controller actions require an isolated guest profile and native binding support.");
        static RuntimeScenario Normalize(RuntimeScenario value) => value with
            { Milestones = value.Milestones is { Count: > 0 } ? value.Milestones : null };
        if (!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(Normalize(scenario), RuntimeJsonContext.Default.RuntimeScenario),
                JsonSerializer.SerializeToNode(Normalize(_guestScenario), RuntimeJsonContext.Default.RuntimeScenario)))
            throw new InvalidOperationException("Controller scenario does not match the prepared guest run.");
    }

    internal async Task<ulong?> TryBindGuestRunAsync(string session, Func<bool> maySend, CancellationToken token)
    {
        await _writeLock.WaitAsync(token);
        try
        {
            if (!maySend()) return null;
            if (Volatile.Read(ref _captureActive) == 0 || _guestAdmission is null)
                throw new InvalidOperationException("Guest binding requires an active isolated capture.");
            var id = ++_requestId;
            await RuntimeProtocol.WriteAsync(_stream, new(RuntimeRequestKind.GuestRunBind, id,
                RuntimeGuestRunBinding.Payload(_guestAdmission, session)), token);
            return id;
        }
        finally { _writeLock.Release(); }
    }

    internal void AcceptGuestRunBinding(JsonElement row, string session)
    {
        if (Volatile.Read(ref _captureActive) == 0 || _guestAdmission is null)
            throw new InvalidOperationException("Guest binding has no active capture.");
        if (Volatile.Read(ref _captureStopping) != 0)
            throw new InvalidDataException("Guest binding arrived after capture stop.");
        _guestBinding = RuntimeGuestRunBinding.Validate(row, _guestAdmission, session);
        // Stop may have acquired the writer lock while validation was in progress.
        if (Volatile.Read(ref _captureStopping) != 0)
        {
            _guestBinding = null;
            throw new InvalidDataException("Guest binding arrived after capture stop.");
        }
    }

    internal string StartPayload(string session, RuntimeScriptTraceOptions? scriptTrace = null)
    {
        var capabilities = Identity.TryGetProperty("capabilities", out var found) ? found : default;
        var payload = capabilities.ValueKind == JsonValueKind.Object &&
            capabilities.TryGetProperty("activePluginQueries", out var available) && available.ValueKind == JsonValueKind.True
            ? _pluginNamespace?.StartPayload(session) ?? session : session;
        if (scriptTrace is null) return payload;
        if (capabilities.ValueKind != JsonValueKind.Object ||
            !capabilities.TryGetProperty("scriptTraceFilters", out var supported) || supported.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("This runtime backend does not support script trace filters.");
        return payload + "\n" + scriptTrace.StartMetadata();
    }

    internal async Task<JsonElement> EnrichCaptureStartAsync(JsonElement observed, CancellationToken token)
    {
        if (_pluginNamespace is null) return observed;
        var node = JsonNode.Parse(observed.GetRawText())!.AsObject();
        node["identity"] = await _pluginNamespace.EnrichAsync(Identity,
            observed.TryGetProperty("pluginNamespace", out var query) ? query : null, token);
        using var json = JsonDocument.Parse(node.ToJsonString());
        return json.RootElement.Clone();
    }

    public async Task<ulong> SendAsync(RuntimeRequestKind kind, string payload, CancellationToken token = default)
    {
        await _writeLock.WaitAsync(token);
        try
        {
            if (kind is RuntimeRequestKind.Stop or RuntimeRequestKind.Cancel)
            {
                Volatile.Write(ref _captureStopping, 1);
                _guestBinding = null;
            }
            var id = ++_requestId;
            await RuntimeProtocol.WriteAsync(_stream, new(kind, id, payload), token);
            return id;
        }
        finally { _writeLock.Release(); }
    }

    public async Task<ulong> SendActionAsync(RuntimeAction action, CancellationToken token = default)
        => await TrySendActionAsync(action, static () => true, token) ?? throw new InvalidOperationException("Runtime action was not admitted.");

    internal void RequireCombatCleanupLease()
    {
        if (!Identity.TryGetProperty("capabilities", out var capabilities) ||
            !capabilities.TryGetProperty("combatCleanupLease", out var available) || available.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("This runtime backend does not support combat cleanup leases.");
    }

    /// <summary>Checks scenario admission while holding the same lock used to queue stop/cancel.</summary>
    internal async Task<ulong?> TrySendActionAsync(RuntimeAction action, Func<bool> maySend, CancellationToken token = default)
    {
        await _writeLock.WaitAsync(token);
        try
        {
            if (!maySend()) return null;
            RuntimeGameSettingAction.RequireCapability(Identity, action.Kind);
            RuntimeOwnerConditionAction.RequireCapability(Identity, action.Kind);
            RuntimeReferenceVariableAction.RequireCapability(Identity, action.Kind);
            if ((action.Kind is "read-quest-conditions" or "read-reference-variable" or "read-reference-variable-sdk") && (Volatile.Read(ref _captureActive) == 0 ||
                Volatile.Read(ref _captureStopping) != 0))
                throw new InvalidOperationException("Typed condition and reference-local reads require an active capture that is not stopping.");
            if (action.Kind == "start-combat-leased")
            {
                RequireCombatCleanupLease();
                if (Volatile.Read(ref _captureActive) == 0 || Volatile.Read(ref _captureStopping) != 0)
                    throw new InvalidOperationException("Leased combat requires an active capture that is not stopping.");
            }
            if (action.Kind == "gamepad-pulse" && (Volatile.Read(ref _captureActive) == 0 ||
                Volatile.Read(ref _captureStopping) != 0 || _guestBinding is null))
                throw new InvalidOperationException("Controller action has no native guest binding.");
            var id = ++_requestId;
            await RuntimeProtocol.WriteAsync(_stream, action.ToFrame(id, _guestBinding?.ManifestSha256), token);
            return id;
        }
        finally { _writeLock.Release(); }
    }

    public async Task<JsonElement?> ReadAsync(CancellationToken token = default)
    {
        var frame = await RuntimeProtocol.ReadAsync(_stream, token);
        if (frame is null) return null;
        if (frame.Kind != RuntimeRequestKind.Event) throw new InvalidDataException("Expected bridge event.");
        try
        {
            using var json = JsonDocument.Parse(frame.Payload);
            var value = json.RootElement;
            if (value.GetProperty("protocol").GetInt32() != RuntimeProtocol.Version ||
                value.GetProperty("requestId").GetUInt64() != frame.RequestId)
                throw new InvalidDataException("Event envelope mismatch.");
            _ = value.GetProperty("kind").GetString();
            _ = value.GetProperty("sequence").GetUInt64();
            _ = value.GetProperty("dropped").GetUInt64();
            return value.Clone();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("Malformed runtime event.", ex);
        }
    }

    internal IDisposable EnterCapture()
    {
        if (Interlocked.CompareExchange(ref _captureActive, 1, 0) != 0)
            throw new InvalidOperationException("A capture is already active on this connection.");
        _guestBinding = null;
        Volatile.Write(ref _captureStopping, 0);
        return new CaptureLease(this);
    }

    private sealed class CaptureLease(RuntimeConnection owner) : IDisposable
    {
        public void Dispose()
        {
            owner._guestBinding = null;
            Volatile.Write(ref owner._captureStopping, 1);
            Interlocked.Exchange(ref owner._captureActive, 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _guestBinding = null;
        await _stream.DisposeAsync();
        _writeLock.Dispose();
    }
}
