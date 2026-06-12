using System.Runtime.InteropServices;

namespace FalloutXbox360Utils.Core.Orchestration;

/// <summary>
///     Blocks until tasks complete WITHOUT entering a managed wait. On an STA thread (the XAML UI
///     thread) every managed blocking wait — <see cref="Task.Wait()" />, <c>WaitHandle.WaitOne</c>,
///     <c>Task.WaitAll</c>, <c>GetAwaiter().GetResult()</c> — becomes a COM pumping wait
///     (<c>CoWaitForMultipleHandles</c>). The pump can dispatch a pending input message back into
///     XAML while the dispatcher is inside a reentrancy-protected callback (render tick, event
///     handler), and <c>CXcpDispatcher::CheckReentrancy</c> fail-fasts the process with a stowed
///     exception (0xc000027b). Use this for the rare UI-thread paths that must block (teardown
///     drains, one-shot resource loads); it polls completion with a non-alertable native sleep,
///     which never pumps. Polling granularity is ~1 ms — irrelevant for those paths.
///     <para>
///         Unlike <see cref="Task.WaitAll(Task[])" /> this never throws on faulted tasks — callers
///         must observe <see cref="Task.Exception" /> themselves.
///     </para>
/// </summary>
public static class NonPumpingWait
{
    /// <summary>Blocks until <paramref name="task" /> completes. Returns false on timeout.</summary>
    public static bool Wait(Task task, TimeSpan? timeout = null) => WaitAll([task], timeout);

    /// <summary>Blocks until every task completes. Returns false on timeout.</summary>
    public static bool WaitAll(IReadOnlyList<Task> tasks, TimeSpan? timeout = null)
    {
        var deadline = timeout.HasValue
            ? Environment.TickCount64 + (long)timeout.Value.TotalMilliseconds
            : long.MaxValue;

        while (true)
        {
            var allDone = true;
            for (var i = 0; i < tasks.Count; i++)
            {
                if (!tasks[i].IsCompleted)
                {
                    allDone = false;
                    break;
                }
            }

            if (allDone)
            {
                return true;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            if (OperatingSystem.IsWindows())
            {
                // Non-alertable native sleep: guaranteed not to pump messages or run APCs.
                // (Return value is only meaningful for alertable sleeps — safe to discard.)
                _ = SleepEx(1, bAlertable: false);
            }
            else
            {
                // No STA/COM pumping exists off Windows; a plain sleep is equivalent there.
                Thread.Sleep(1);
            }
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint SleepEx(uint dwMilliseconds, bool bAlertable);
}
