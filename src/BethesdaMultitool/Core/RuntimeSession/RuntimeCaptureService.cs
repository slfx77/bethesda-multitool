using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BethesdaMultitool.Core.RuntimeSession;

public sealed record RuntimeCaptureResult(string Status, long Events, ulong Dropped, long Snapshots, long Errors,
    long ControllerResults = 0, long ControllerErrors = 0, long MissingSequences = 0);

/// <summary>Captures actual bridge events. A console result is never promoted to an observed outcome.</summary>
public static class RuntimeCaptureService
{
    public static async Task<RuntimeCaptureResult> CaptureAsync(RuntimeConnection connection, Stream destination,
        TimeSpan duration, IReadOnlyList<RuntimeAction>? actions = null, CancellationToken cancellationToken = default,
        RuntimeScriptTraceOptions? scriptTrace = null, IReadOnlyList<RuntimeMilestone>? milestones = null,
        IProgress<string>? progress = null)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(duration), "Capture duration must be between zero and one hour.");
        actions ??= [];
        if (actions.Count > 128) throw new ArgumentException("A scenario can contain at most 128 actions.", nameof(actions));
        // Validate every action before starting the engine session.
        foreach (var action in actions) (action ?? throw new ArgumentException("Action cannot be null.")).Validate();
        milestones ??= [];
        var monitor = new RuntimeMilestoneMonitor(milestones, actions.Count);
        foreach (var action in actions)
        {
            RuntimeGameSettingAction.RequireCapability(connection.Identity, action.Kind);
            RuntimeOwnerConditionAction.RequireCapability(connection.Identity, action.Kind);
            RuntimeReferenceVariableAction.RequireCapability(connection.Identity, action.Kind);
        }
        foreach (var milestone in milestones)
            if (milestone.Probe is { } probe)
            {
                RuntimeGameSettingAction.RequireCapability(connection.Identity, probe.Kind);
                RuntimeOwnerConditionAction.RequireCapability(connection.Identity, probe.Kind);
                RuntimeReferenceVariableAction.RequireCapability(connection.Identity, probe.Kind);
            }
        var hasMilestones = milestones.Count > 0;
        if (milestones.Any(item => item.Probe == null && item.AfterActionIndex >= 0))
        {
            if (!connection.Identity.TryGetProperty("capabilities", out var available) ||
                !available.TryGetProperty("actionBoundaries", out var boundary) || boundary.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException("Passive action milestones require game-thread action boundaries.");
            if (milestones.Any(item => item.Probe == null && item.AfterActionIndex >= 0 && actions[item.AfterActionIndex].Kind == "wait-message-state"))
                throw new ArgumentException("Host menu waits require a read-only milestone probe.");
        }
        var reportedMilestones = new HashSet<string>(StringComparer.Ordinal);
        var waitsForMenu = actions.Any(action => action.Kind == "wait-message-state");
        var hasGamepads = actions.Any(action => action.Kind == "gamepad-pulse");
        var hasCombatLease = actions.Any(action => action.Kind == "start-combat-leased");
        if (hasCombatLease) connection.RequireCombatCleanupLease();
        var combatCleanup = new RuntimeCombatCleanupValidator();
        var gamepad = new RuntimeGamepadTraceValidator();
        if (hasGamepads) connection.ValidateGamepadScenario(new RuntimeScenario(actions,
            checked((int)duration.TotalMilliseconds), scriptTrace, milestones));
        if (waitsForMenu && (!connection.Identity.TryGetProperty("capabilities", out var capabilities) ||
            !capabilities.TryGetProperty("messageStateAvailability", out var availability) || availability.ValueKind != JsonValueKind.True))
            throw new InvalidOperationException("This runtime backend does not support menu availability observations.");
        var session = Guid.NewGuid().ToString("N");
        var startPayload = connection.StartPayload(session, scriptTrace);
        using var captureLease = connection.EnterCapture();
        using var writer = new StreamWriter(destination, new UTF8Encoding(false), 8192, leaveOpen: true);
        var eventLoss = false;
        await Write(new JsonObject { ["kind"] = "capture-header", ["schema"] = "bmt/runtime-trace", ["version"] = hasMilestones ? 3 : waitsForMenu || hasGamepads || hasCombatLease ? 2 : 1,
            ["session"] = session, ["startedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["identity"] = JsonNode.Parse(connection.Identity.GetRawText()),
            ["scenario"] = hasMilestones || hasGamepads || hasCombatLease ? JsonSerializer.SerializeToNode(new RuntimeScenario(actions,
                checked((int)duration.TotalMilliseconds), scriptTrace, milestones), RuntimeJsonContext.Default.RuntimeScenario) : null });
        long events = 0, snapshots = 0, errors = 0;
        long controllerResults = 0, controllerErrors = 0;
        ulong dropped = 0;
        var status = "disconnected";
        var captureEnded = false;
        var stopRequested = 0;
        JsonElement? lastResponse = null;
        Stopwatch? captureWatch = null;
        var captureIdentity = connection.Identity;
        var sequence = connection.Identity.TryGetProperty("sequence", out var initialSequence) ? initialSequence.GetUInt64() : 0;
        long missingSequences = 0;
        CancellationTokenSource? milestoneStop = null;
        Task milestoneWatch = Task.CompletedTask;
        Task<JsonElement?>? pendingRead = null;
        if (cancellationToken.IsCancellationRequested) return await Finish("cancelled-before-start");
        using var readDeadline = new CancellationTokenSource(hasGamepads ? TimeSpan.FromSeconds(135) : duration + TimeSpan.FromSeconds(10));
        using var controllerStop = new CancellationTokenSource();
        var bindingReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task control = Task.CompletedTask;
        try
        {
            var start = await connection.SendAsync(RuntimeRequestKind.Start, startPayload, readDeadline.Token);
            captureWatch = Stopwatch.StartNew();
            control = StopAtDeadline(); // Cancellation cannot overtake the initial start frame.
            if (!await AwaitResponse(start, RuntimeRequestKind.Start)) return await Finish(status);
            if (hasGamepads)
            {
                if (lastResponse is { } startResponse && Text(startResponse, "kind") == "error" ||
                    cancellationToken.IsCancellationRequested || Volatile.Read(ref stopRequested) != 0)
                { await RequestCaptureEnd(); return await DrainAndFinish(); }
                progress?.Report("Binding isolated guest profile.");
                var bind = await connection.TryBindGuestRunAsync(session,
                    () => !cancellationToken.IsCancellationRequested && Volatile.Read(ref stopRequested) == 0, readDeadline.Token);
                if (bind is null || !await AwaitResponse(bind.Value, RuntimeRequestKind.GuestRunBind) ||
                    lastResponse is not { } bound || Text(bound, "kind") == "error" || eventLoss ||
                    cancellationToken.IsCancellationRequested || Volatile.Read(ref stopRequested) != 0)
                { await RequestCaptureEnd(); return await DrainAndFinish(); }
                connection.AcceptGuestRunBinding(bound, session);
                progress?.Report("Guest profile bound; running scenario.");
                captureWatch.Restart();
                readDeadline.CancelAfter(duration + TimeSpan.FromSeconds(10));
                bindingReady.TrySetResult();
            }
            await BeginMilestones(-1);
            if (!await AwaitMilestones()) return await DrainAndFinish();
            for (var actionIndex = 0; actionIndex < actions.Count; ++actionIndex)
            {
                var action = actions[actionIndex];
                await BeginMilestones(actionIndex);
                if (monitor.Active.Any(item => item.Failure != null))
                {
                    await AwaitMilestones();
                    break;
                }
                if (!CanSendScenarioAction())
                {
                    await PendingActionStopped(action, actionIndex);
                    if (hasCombatLease) await RequestCaptureEnd();
                    break;
                }
                if (action.Kind == "wait-message-state")
                {
                    await ActionRequested(action, actionIndex, null);
                    if (!await WaitForMenu(action, actionIndex)) break;
                    if (!await AwaitMilestones()) break;
                    continue;
                }
                if (action.Kind == "start-combat-leased") RuntimeCombatCleanupValidator.RequireTargets(action, captureIdentity);
                var id = await connection.TrySendActionAsync(action, CanSendScenarioAction, readDeadline.Token);
                if (id is null) { await PendingActionStopped(action, actionIndex); break; }
                foreach (var pending in monitor.Active) pending.ActionRequest = id;
                await ActionRequested(action, actionIndex, id);
                if (action.Kind == "gamepad-pulse")
                {
                    if (!await WaitForGamepad(action, actionIndex, id.Value)) break;
                    if (!await AwaitMilestones()) break;
                    continue;
                }
                if (!await AwaitResponse(id.Value, action.RequestKind, action.Kind == "read-message-state", action.Kind))
                {
                    await PendingActionStopped(action, actionIndex);
                    if (hasCombatLease && !captureEnded)
                    {
                        await RequestCaptureEnd();
                        return await DrainAndFinish();
                    }
                    return await Finish(status);
                }
                if ((hasMilestones || hasCombatLease) && lastResponse is { } response &&
                    (Text(response, "kind") == "error" || response.TryGetProperty("accepted", out var accepted) && accepted.ValueKind == JsonValueKind.False))
                {
                    foreach (var pending in monitor.Active) pending.Fail("native-error");
                    await AwaitMilestones();
                    await RequestCaptureEnd();
                    break;
                }
                if (!await AwaitMilestones()) break;
            }
            while (!captureEnded)
            {
                var observed = await Read();
                if (observed is null) break;
                if (observed.Value.GetProperty("kind").GetString() == "capture-end")
                {
                    status = observed.Value.GetProperty("status").GetString() ?? "failed";
                    captureEnded = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException) { status = cancellationToken.IsCancellationRequested ? "cancel-timeout" : "stop-timeout"; }
        catch (InvalidDataException) { status = "protocol-error"; }
        catch (IOException) { status = "disconnected"; }
        catch (JsonException) { status = "protocol-error"; }
        finally
        {
            if ((hasGamepads || hasCombatLease) && !captureEnded)
            {
                try
                {
                    await RequestCaptureEnd();
                    if (hasCombatLease)
                    {
                        while (!captureEnded && !readDeadline.IsCancellationRequested)
                        {
                            var cleanup = await Read();
                            if (cleanup is null) break;
                            if (Text(cleanup.Value, "kind") == "capture-end") captureEnded = true;
                        }
                    }
                }
                catch (Exception error) when (error is InvalidDataException or IOException or OperationCanceledException or JsonException)
                {
                    // Keep the original failure; the footer records missing cleanup evidence.
                }
            }
            await StopMilestoneWatch();
            await controllerStop.CancelAsync();
            try { await control; } catch (OperationCanceledException) { } catch (IOException) { }
        }
        return await Finish(status);

        async Task StopAtDeadline()
        {
            using var trigger = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, controllerStop.Token);
            try
            {
                if (hasGamepads) await bindingReady.Task.WaitAsync(TimeSpan.FromSeconds(125), trigger.Token);
                await Task.Delay(duration, trigger.Token);
            }
            catch (TimeoutException) { }
            catch (OperationCanceledException) when (!controllerStop.IsCancellationRequested) { }
            if (controllerStop.IsCancellationRequested) return;
            await RequestCaptureEnd();
        }
        async Task RequestCaptureEnd()
        {
            if (captureEnded) return;
            if (Interlocked.CompareExchange(ref stopRequested, 1, 0) != 0) return;
            // Stop/cancel is queued on the game thread. Bound the wait for its acknowledgement.
            readDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            using var writeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await connection.SendAsync(cancellationToken.IsCancellationRequested ? RuntimeRequestKind.Cancel : RuntimeRequestKind.Stop,
                "", writeDeadline.Token);
        }
        bool CanSendScenarioAction() => !cancellationToken.IsCancellationRequested && Volatile.Read(ref stopRequested) == 0 &&
            captureWatch is not null && captureWatch.Elapsed < duration &&
            (!(hasMilestones || hasGamepads || hasCombatLease) || !eventLoss && !monitor.Active.Any(item => item.Failure != null));
        async Task PendingActionStopped(RuntimeAction action, int index)
        {
            if (!waitsForMenu && !hasMilestones && !hasGamepads && !hasCombatLease) return; // Legacy schema1 retains its existing accounting.
            var outcome = "capture-stopping";
            if (cancellationToken.IsCancellationRequested) outcome = "cancelled";
            else if (captureEnded && status == "disconnected") outcome = "disconnected";
            ++controllerResults;
            if (outcome == "capture-stopping") { ++errors; ++controllerErrors; }
            await Write(new JsonObject { ["kind"] = "scenario-result", ["origin"] = "controller", ["actionIndex"] = index,
                ["actionKind"] = action.Kind, ["status"] = outcome, ["elapsedMilliseconds"] = captureWatch?.Elapsed.TotalMilliseconds });
        }
        Task ActionRequested(RuntimeAction action, int index, ulong? requestId) => Write(new JsonObject
        {
            ["kind"] = "action-request", ["actionIndex"] = index, ["requestId"] = requestId,
            ["action"] = JsonSerializer.SerializeToNode(action, RuntimeJsonContext.Default.RuntimeAction)
        });
        async Task<bool> AwaitResponse(ulong id, RuntimeRequestKind requestKind, bool inspectMenu = false, string? actionKind = null,
            TimeSpan? responseTimeout = null)
        {
            lastResponse = null;
            var responseWatch = Stopwatch.StartNew();
            while (true)
            {
                if (responseTimeout is { } maximum)
                {
                    var remaining = maximum - responseWatch.Elapsed;
                    if (remaining <= TimeSpan.Zero) return false;
                    try { await PendingRead().WaitAsync(remaining, readDeadline.Token); }
                    catch (TimeoutException) { return false; }
                }
                var observed = await Read();
                if (observed is null) { captureEnded = true; return false; }
                var kind = observed.Value.GetProperty("kind").GetString();
                if (kind == "capture-end")
                {
                    status = observed.Value.GetProperty("status").GetString() ?? "failed";
                    captureEnded = true;
                    return false;
                }
                if (observed.Value.GetProperty("requestId").GetUInt64() == id &&
                    (kind == "error" || kind == RuntimeResponse.Kind(requestKind, inspectMenu ? "read-message-state" : actionKind)))
                { lastResponse = observed; return true; }
                if (requestKind == RuntimeRequestKind.GamepadPulse && eventLoss) return false;
            }
        }
        async Task<bool> WaitForGamepad(RuntimeAction action, int index, ulong id)
        {
            var watch = Stopwatch.StartNew();
            var outcome = "timeout";
            try
            {
                var responded = await AwaitResponse(id, RuntimeRequestKind.GamepadPulse,
                    responseTimeout: TimeSpan.FromMilliseconds(action.Gamepad!.DeadlineMilliseconds + 1000));
                outcome = cancellationToken.IsCancellationRequested ? "cancelled" :
                    eventLoss ? "mismatch" :
                    responded && lastResponse is { } row && Text(row, "kind") == "error" ? "native-error" :
                    responded ? gamepad.Result(id).Status == "Observed" ? "matched" : "mismatch" :
                    captureEnded ? status == "disconnected" ? "disconnected" : "capture-ended" :
                    Volatile.Read(ref stopRequested) != 0 ? "capture-stopping" : "timeout";
            }
            catch (OperationCanceledException) { outcome = cancellationToken.IsCancellationRequested ? "cancelled" : "timeout"; }
            catch (IOException) { outcome = "disconnected"; }
            ++controllerResults;
            if (RuntimeMilestoneMonitor.IsFailure(outcome, false)) { ++controllerErrors; ++errors; }
            var result = new JsonObject { ["kind"] = "scenario-result", ["origin"] = "controller",
                ["actionIndex"] = index, ["actionKind"] = "gamepad-pulse", ["status"] = outcome,
                ["elapsedMilliseconds"] = watch.Elapsed.TotalMilliseconds };
            if (lastResponse is { } observed)
            {
                result["observedRequestId"] = observed.GetProperty("requestId").GetUInt64();
                result["observedSequence"] = observed.GetProperty("sequence").GetUInt64();
            }
            await Write(result);
            if (outcome == "matched") return true;
            await RequestCaptureEnd();
            return false;
        }
        async Task<bool> WaitForMenu(RuntimeAction action, int actionIndex)
        {
            lastResponse = null; // A wait cannot inherit the preceding action's observation identity.
            var watch = Stopwatch.StartNew();
            var timeout = TimeSpan.FromMilliseconds(action.TimeoutMilliseconds!.Value);
            var interval = TimeSpan.FromMilliseconds(action.PollMilliseconds!.Value);
            using var waitStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, readDeadline.Token);
            var outcomeWritten = false;
            var waitResolution = 0; // 0 pending, 1 terminal observation, 2 deadline admitted.
            var expiry = EndWaitAtDeadline();
            try
            {
                while (true)
                {
                    if (cancellationToken.IsCancellationRequested) return await Outcome("cancelled");
                    if (watch.Elapsed >= timeout) return await Fail("timeout");
                    var id = await connection.TrySendActionAsync(action, CanSendScenarioAction, readDeadline.Token);
                    if (id is null) return await Outcome(cancellationToken.IsCancellationRequested ? "cancelled" : "capture-stopping");
                    if (!await AwaitResponse(id.Value, RuntimeRequestKind.MessageMenu, inspectMenu: true))
                    {
                        if (cancellationToken.IsCancellationRequested) return await Outcome("cancelled");
                        if (status == "disconnected") return await Outcome("disconnected");
                        return await Outcome(watch.Elapsed >= timeout ? "timeout" : "capture-ended");
                    }
                    if (cancellationToken.IsCancellationRequested) return await Outcome("cancelled");
                    // The caller's monotonic deadline wins over a response received after it.
                    if (watch.Elapsed >= timeout) return await Fail("timeout");
                    var observed = lastResponse!.Value;
                    if (observed.GetProperty("kind").GetString() == "error") return await Fail("native-error");
                    var inspection = RuntimeMenuInspection.Classify(observed);
                    if (inspection == RuntimeMenuInspectionState.Invalid) return await Fail("mismatch");
                    if (inspection == RuntimeMenuInspectionState.Unavailable)
                    {
                        var remaining = timeout - watch.Elapsed;
                        if (remaining <= TimeSpan.Zero) return await Fail("timeout");
                        using var delayStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, readDeadline.Token);
                        try { await Task.Delay(remaining < interval ? remaining : interval, delayStop.Token); }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return await Outcome("cancelled"); }
                        continue; // Only the read-only inspection is repeated.
                    }
                    var owner = action.Target == "probe" ? "dedicated-probe" : "explicit-visible-text";
                    var matches = Text(observed, "status") == "visible" && Text(observed, "text") == action.Message &&
                        Text(observed, "buttonLabel") == action.Button && Text(observed, "owner") == owner &&
                        observed.TryGetProperty("buttonIndex", out var button) && button.TryGetInt32(out var index) && index == action.ButtonIndex;
                    if (!matches) return await Fail("mismatch");
                    if (!CanSendScenarioAction()) return await Outcome(cancellationToken.IsCancellationRequested ? "cancelled" : "capture-stopping");
                    return await Outcome("matched");
                }
            }
            catch (OperationCanceledException)
            {
                if (!outcomeWritten) await Outcome(cancellationToken.IsCancellationRequested ? "cancelled" : "timeout");
                throw;
            }
            catch (IOException)
            {
                if (!outcomeWritten) await Outcome("disconnected");
                throw;
            }
            finally
            {
                await waitStop.CancelAsync();
                try { await expiry; } catch (OperationCanceledException) { } catch (IOException) { }
            }

            async Task EndWaitAtDeadline()
            {
                await Task.Delay(timeout, waitStop.Token);
                if (cancellationToken.IsCancellationRequested || captureEnded ||
                    Interlocked.CompareExchange(ref waitResolution, 2, 0) != 0) return;
                await RequestCaptureEnd();
            }

            async Task<bool> Fail(string outcome)
            {
                if (cancellationToken.IsCancellationRequested) return await Outcome("cancelled");
                await Outcome(outcome);
                // Abort remaining scenario actions; retain the engine's normal stop acknowledgement.
                await controllerStop.CancelAsync();
                await RequestCaptureEnd();
                return false;
            }
            async Task<bool> Outcome(string outcome)
            {
                if (outcome == "matched")
                {
                    if (Interlocked.CompareExchange(ref waitResolution, 1, 0) != 0) outcome = "timeout";
                }
                else Interlocked.Exchange(ref waitResolution, 1);
                Volatile.Write(ref outcomeWritten, true);
                ++controllerResults;
                if (outcome is "mismatch" or "timeout" or "capture-ended" or "capture-stopping") { ++errors; ++controllerErrors; }
                var row = new JsonObject { ["kind"] = "scenario-result", ["origin"] = "controller", ["actionIndex"] = actionIndex,
                    ["actionKind"] = action.Kind, ["status"] = outcome, ["elapsedMilliseconds"] = watch.Elapsed.TotalMilliseconds };
                if (lastResponse is { } response)
                {
                    row["observedRequestId"] = response.GetProperty("requestId").GetUInt64();
                    row["observedSequence"] = response.GetProperty("sequence").GetUInt64();
                    if (response.TryGetProperty("frame", out var frame)) row["observedFrame"] = frame.GetUInt64();
                }
                await Write(row);
                return outcome == "matched";
            }
        }
        async Task BeginMilestones(int actionIndex)
        {
            await StopMilestoneWatch();
            monitor.Begin(actionIndex, captureIdentity);
            if (eventLoss) foreach (var pending in monitor.Active) pending.Fail("event-loss");
            if (monitor.Active.Count == 0) return;
            milestoneStop = new CancellationTokenSource();
            var token = milestoneStop.Token;
            milestoneWatch = Watch();
            async Task Watch()
            {
                while (true)
                {
                    var pending = monitor.Active.Where(item => !item.Matched && item.Failure == null).ToArray();
                    if (pending.Length == 0) return;
                    var delay = pending.Min(item => item.Remaining);
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
                    if (token.IsCancellationRequested) return;
                    if (pending.Any(item => item.Expire())) { await RequestCaptureEnd(); return; }
                }
            }
        }
        async Task StopMilestoneWatch()
        {
            if (milestoneStop == null) return;
            await milestoneStop.CancelAsync();
            try { await milestoneWatch; } catch (OperationCanceledException) { } catch (IOException) { }
            milestoneStop.Dispose();
            milestoneStop = null;
        }
        async Task<bool> AwaitMilestones()
        {
            while (monitor.Active.Any(item => !item.Reported))
            {
                foreach (var item in monitor.Active.Where(item => !item.Reported))
                {
                    if (cancellationToken.IsCancellationRequested) item.Fail("cancelled");
                    else if (eventLoss) item.Fail("event-loss");
                    else if (captureEnded && !item.Matched) item.Fail(status == "disconnected" ? "disconnected" : "capture-ended");
                    else if (Volatile.Read(ref stopRequested) != 0 && !item.Matched && item.Failure == null) item.Fail("capture-stopping");
                    item.Expire();
                    if (item.Matched || item.Failure != null)
                        await MilestoneOutcome(item, item.Failure ?? "matched");
                }
                if (monitor.Active.Any(item => item.Failure != null))
                {
                    await RequestCaptureEnd();
                    return false;
                }
                var waiting = monitor.Active.Where(item => !item.Reported).ToArray();
                if (waiting.Length == 0) break;
                // Keep one pending read while polling; passive observations and other probes continue to arrive.
                if (PendingRead().IsCompleted) { await ObservePending(); continue; }
                var probe = waiting.FirstOrDefault(item => item.Specification.Probe != null && !item.ProbeAwaitingResponse &&
                    (item.LastProbe == 0 || Stopwatch.GetElapsedTime(item.LastProbe).TotalMilliseconds >= item.Specification.PollMilliseconds));
                if (probe != null)
                {
                    var id = await connection.TrySendActionAsync(probe.Specification.Probe!, CanSendScenarioAction, readDeadline.Token);
                    if (id == null) { probe.Fail(cancellationToken.IsCancellationRequested ? "cancelled" : "capture-stopping"); continue; }
                    probe.ProbeRequest = id;
                    probe.ProbeAwaitingResponse = true;
                    probe.LastProbe = Stopwatch.GetTimestamp();
                    await Write(new JsonObject { ["kind"] = "milestone-probe", ["milestoneId"] = probe.Specification.Id,
                        ["requestId"] = id, ["action"] = JsonSerializer.SerializeToNode(probe.Specification.Probe, RuntimeJsonContext.Default.RuntimeAction) });
                    continue;
                }
                else
                {
                    var due = waiting.Where(item => item.Specification.Probe != null && !item.ProbeAwaitingResponse).ToArray();
                    if (due.Length == 0) await ObservePending();
                    else
                    {
                        var delay = due.Min(item => Math.Max(1,
                            item.Specification.PollMilliseconds - Stopwatch.GetElapsedTime(item.LastProbe).TotalMilliseconds));
                        using var wake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        var timer = Task.Delay(TimeSpan.FromMilliseconds(delay), wake.Token);
                        if (await Task.WhenAny(PendingRead(), timer) == pendingRead) await ObservePending();
                        await wake.CancelAsync();
                    }
                }
            }
            await StopMilestoneWatch();
            return true;
        }
        Task<JsonElement?> PendingRead() => pendingRead ??= connection.ReadAsync(readDeadline.Token);
        async Task ObservePending()
        {
            var row = await Read();
            if (row == null) { captureEnded = true; status = "disconnected"; }
            else if (Text(row.Value, "kind") == "capture-end")
            { captureEnded = true; status = Text(row.Value, "status") ?? "failed"; }
        }
        async Task MilestoneOutcome(RuntimeMilestoneMonitor.Pending item, string outcome)
        {
            if (!reportedMilestones.Add(item.Specification.Id)) return;
            item.Reported = true;
            ++controllerResults;
            if (RuntimeMilestoneMonitor.IsFailure(outcome, true)) { ++controllerErrors; ++errors; }
            var row = new JsonObject { ["kind"] = "scenario-result", ["origin"] = "controller", ["milestoneId"] = item.Specification.Id,
                ["actionIndex"] = item.Specification.AfterActionIndex, ["actionKind"] = "milestone:" + item.Specification.Id,
                ["status"] = outcome, ["elapsedMilliseconds"] = item.Elapsed.TotalMilliseconds };
            if (item.Observation is { } observation)
            {
                row["observedRequestId"] = observation.GetProperty("requestId").GetUInt64();
                row["observedSequence"] = observation.GetProperty("sequence").GetUInt64();
                row["matchedElapsedMilliseconds"] = item.ObservedMilliseconds;
                if (observation.TryGetProperty("frame", out var frame)) row["observedFrame"] = frame.GetUInt64();
            }
            await Write(row);
        }
        async Task<RuntimeCaptureResult> DrainAndFinish()
        {
            while (!captureEnded)
            {
                var row = await Read();
                if (row == null) { captureEnded = true; status = "disconnected"; break; }
                if (Text(row.Value, "kind") == "capture-end")
                { status = Text(row.Value, "status") ?? "failed"; captureEnded = true; }
            }
            return await Finish(status);
        }
        static string? Text(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
        async Task<JsonElement?> Read()
        {
            JsonElement? observed;
            try { observed = await PendingRead(); }
            finally { pendingRead = null; } // A consumed/faulted frame cannot poison the cleanup drain.
            if (observed is null) return null;
            ++events;
            var nextSequence = observed.Value.GetProperty("sequence").GetUInt64();
            if (nextSequence <= sequence) throw new InvalidDataException("Event sequence is duplicated or out of order.");
            if (sequence != 0 && nextSequence != sequence + 1) missingSequences += checked((long)(nextSequence - sequence - 1));
            if (sequence != 0 && nextSequence != sequence + 1 || observed.Value.GetProperty("dropped").GetUInt64() != 0) eventLoss = true;
            sequence = nextSequence;
            dropped = Math.Max(dropped, observed.Value.GetProperty("dropped").GetUInt64());
            var kind = observed.Value.GetProperty("kind").GetString();
            if (kind == "guest-run-bind-progress")
                progress?.Report("Guest binding: " + (Text(observed.Value, "phase") ?? "verifying") +
                    (observed.Value.TryGetProperty("filesVerified", out var verified) ? " (" + verified.GetRawText() + " files)" : ""));
            if (kind == "capture-start") observed = await connection.EnrichCaptureStartAsync(observed.Value, readDeadline.Token);
            if (kind == "capture-start" && observed.Value.TryGetProperty("identity", out var boundIdentity)) captureIdentity = boundIdentity;
            monitor.Observe(observed.Value, eventLoss);
            gamepad.Observe(observed.Value, eventLoss: eventLoss);
            combatCleanup.Observe(observed.Value, eventLoss);
            if (kind == "snapshot") ++snapshots;
            if (kind is "error" or "script-error" || kind == "action-result" &&
                observed.Value.TryGetProperty("accepted", out var accepted) && accepted.ValueKind == JsonValueKind.False) ++errors;
            await writer.WriteLineAsync(observed.Value.GetRawText());
            await writer.FlushAsync(CancellationToken.None);
            return observed;
        }
        async Task Write(JsonObject value)
        {
            using var json = JsonDocument.Parse(value.ToJsonString());
            gamepad.Observe(json.RootElement, eventLoss: eventLoss);
            combatCleanup.Observe(json.RootElement, eventLoss);
            await writer.WriteLineAsync(value.ToJsonString());
            await writer.FlushAsync(CancellationToken.None);
        }
        async Task<RuntimeCaptureResult> Finish(string finalStatus)
        {
            foreach (var item in monitor.Active.Where(item => !item.Reported))
                await MilestoneOutcome(item, item.Failure ?? (cancellationToken.IsCancellationRequested ? "cancelled" :
                    finalStatus == "disconnected" ? "disconnected" : "capture-ended"));
            foreach (var milestone in milestones.Where(item => !reportedMilestones.Contains(item.Id)))
                await MilestoneOutcome(new RuntimeMilestoneMonitor.Pending(milestone, new Dictionary<string, uint>()), "not-run");
            var cleanupDiagnostics = combatCleanup.GetDiagnostics();
            errors += cleanupDiagnostics.Count;
            var result = new RuntimeCaptureResult(finalStatus, events, dropped, snapshots, errors, controllerResults, controllerErrors, missingSequences);
            await Write(new JsonObject { ["kind"] = "capture-footer", ["status"] = finalStatus,
                ["events"] = events, ["dropped"] = dropped, ["snapshots"] = snapshots, ["errors"] = errors,
                ["controllerResults"] = controllerResults, ["controllerErrors"] = controllerErrors, ["missingSequences"] = missingSequences,
                ["combatCleanupDiagnostics"] = combatCleanup.HasRequests
                    ? new JsonArray(cleanupDiagnostics.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()) : null });
            return result;
        }
    }
}
