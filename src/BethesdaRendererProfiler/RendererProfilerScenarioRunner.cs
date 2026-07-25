using System.Diagnostics;

namespace BethesdaRendererProfiler;

internal sealed class RendererProfilerScenarioRunner
{
    private readonly IRendererProfilerScenarioEventSink _events;
    private readonly IRendererProfilerScenarioHost _host;

    internal RendererProfilerScenarioRunner(
        IRendererProfilerScenarioHost host,
        IRendererProfilerScenarioEventSink events)
    {
        _host = host;
        _events = events;
    }

    internal async Task<RendererProfilerScenarioRunResult> RunAsync(
        RendererProfilerScenarioPlan plan,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var timer = Stopwatch.StartNew();
        _events.ScenarioStarted(plan, output);

        var duplicateStep = plan.Steps.GroupBy(static step => step.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateStep is not null)
        {
            return CompleteFailure(
                "scenario.unique-step-ids",
                "case-insensitively unique step IDs",
                duplicateStep.Key,
                "Scenario execution was rejected before renderer state changed.",
                "scenario-invalid-plan");
        }

        var results = new List<RendererProfilerScenarioStepResult>(plan.Steps.Count);
        try
        {
            await _host.PrepareAsync(plan, cancellationToken);
            for (var i = 0; i < plan.Steps.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var step = plan.Steps[i];
                _events.StepStarted(step, i, timer.ElapsedMilliseconds);
                var result = await _host.ExecuteStepAsync(plan, step, i, cancellationToken);
                results.Add(result);
                _events.StepCompleted(result, i, timer.ElapsedMilliseconds);
            }
        }
        catch (Exception ex) when (ex is not StackOverflowException and not OutOfMemoryException)
        {
            var assertion = new RendererProfilerScenarioAssertion(
                ex is OperationCanceledException ? "scenario.cancelled" : "scenario.execution",
                false,
                results.Count < plan.Steps.Count ? results.Count : null,
                results.Count < plan.Steps.Count ? plan.Steps[results.Count].Id : null,
                "step completes without exception",
                ex.GetType().Name,
                ex.Message);
            _events.AssertionCompleted(assertion, timer.ElapsedMilliseconds);
            var failed = new RendererProfilerScenarioRunResult(
                false, 1, results.Count, 1, 1,
                ex is OperationCanceledException ? "scenario-cancelled" : "scenario-exception",
                results, [assertion]);
            _events.ScenarioCompleted(failed, timer.ElapsedMilliseconds);
            return failed;
        }

        var assertions = RendererProfilerScenarioAssertions.Evaluate(plan, results);
        foreach (var assertion in assertions)
        {
            _events.AssertionCompleted(assertion, timer.ElapsedMilliseconds);
        }

        var failedCount = assertions.Count(static assertion => !assertion.Passed);
        var passed = failedCount == 0;
        var completed = new RendererProfilerScenarioRunResult(
            passed,
            passed ? 0 : 1,
            results.Count,
            assertions.Count,
            failedCount,
            passed ? "scenario-complete" : "scenario-assertion-failed",
            results,
            assertions);
        _events.ScenarioCompleted(completed, timer.ElapsedMilliseconds);
        return completed;

        RendererProfilerScenarioRunResult CompleteFailure(
            string assertionId,
            object? expected,
            object? actual,
            string details,
            string reason)
        {
            var assertion = new RendererProfilerScenarioAssertion(
                assertionId, false, null, null, expected, actual, details);
            _events.AssertionCompleted(assertion, timer.ElapsedMilliseconds);
            var result = new RendererProfilerScenarioRunResult(
                false, 1, 0, 1, 1, reason, [], [assertion]);
            _events.ScenarioCompleted(result, timer.ElapsedMilliseconds);
            return result;
        }
    }
}
