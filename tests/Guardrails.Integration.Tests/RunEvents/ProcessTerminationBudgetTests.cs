using Guardrails.Cli;
using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests.RunEvents;

/// <summary>
/// Issue #603 — the process budget every teardown on the cancelled path has to fit inside, and the one
/// piece of teardown that was budgeted correctly and then never ran.
///
/// <para><b>The defect.</b> <c>Program.cs</c> passed no <c>InvocationConfiguration</c>, so
/// System.CommandLine's default <c>ProcessTerminationTimeout</c> of 2 seconds governed every command.
/// <see cref="LogServer.ShutdownDrainTimeout"/> alone is 5 seconds — 2.5x the entire process budget —
/// and that drain is precisely the mechanism (PR #599) that makes a parked <c>GET /events</c> subscriber
/// receive the terminal <c>run-finished</c> row. So on Ctrl-C the delivery guarantee could not complete,
/// silently, on the one path an operator invokes deliberately.</para>
///
/// <para><b>Why nothing caught it.</b> Every existing shutdown test drives <c>DisposeAsync</c> DIRECTLY
/// (see <see cref="EventsStreamShutdownTests"/>), which is the right unit-level shape and gives the
/// method its full budget inside a test host that keeps living regardless. Nothing exercised the path
/// where the PROCESS is being torn down around it. A teardown guarantee verified only by calling the
/// teardown method is not verified against the thing that calls it in anger.</para>
///
/// <para><b>What is NOT tested here, plainly.</b> That a real SIGINT against a real <c>guardrails</c>
/// process honours the new ceiling. Delivering a genuine console interrupt to a child without also
/// signalling the test runner is not portable across Windows/Linux/macOS, and a test that spawned one
/// and asserted a wall-clock would be measuring the machine (the lesson #566 just paid for). What IS
/// pinned is everything below the signal: the ceiling is set deliberately, every bounded budget on the
/// cancelled path fits inside it, and the deferred listener teardown is on a thread the runtime will not
/// exit out from under.</para>
/// </summary>
public sealed class ProcessTerminationBudgetTests
{
    [Trait("Category", "RunEvents")]
    [Fact]
    public void TheProcessTerminationCeilingIsChosen_NotInherited()
    {
        // Null is not a simplification of the default — it disables process-termination handling
        // outright, so Ctrl-C would kill the process with no token cancellation and no teardown at all.
        Assert.NotNull(CliInvocation.Create().ProcessTerminationTimeout);
        Assert.Equal(CliInvocation.ProcessTerminationTimeout, CliInvocation.Create().ProcessTerminationTimeout);
        Assert.NotEqual(CliInvocation.LibraryDefaultProcessTerminationTimeout, CliInvocation.ProcessTerminationTimeout);
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public void EveryBoundedTeardownBudgetOnTheCancelledPathFitsInsideTheProcessCeiling()
    {
        // The positive control, and the whole of #603 in one line: the drain the terminal-row delivery
        // depends on ALREADY exceeded the budget the process was actually running under. Without this
        // assertion the one below degenerates into "the number we picked is bigger than the numbers we
        // picked", which would have been just as true on the day the bug shipped.
        Assert.True(
            LogServer.ShutdownDrainTimeout > CliInvocation.LibraryDefaultProcessTerminationTimeout,
            "the log server's drain no longer exceeds System.CommandLine's default — if that is now the "
            + "configured ceiling, this test has stopped describing why a deliberate value is needed");

        // The cancelled teardown of a `run`, in the order RunCommand unwinds: the webhook sink's dispose
        // (an `await using` inside the run body), then the log server's (the outermost finally). These are
        // spent IN SERIES, so what has to fit is the SUM — pure arithmetic over declared constants, with
        // no machine in it.
        TimeSpan webhookCancelled =
            WebhookEventSink.BacklogDrainBudgetCancelled
            + WebhookEventSink.TerminalDeliveryTimeoutCancelled
            + WebhookEventSink.PumpShutdownGraceCancelled;

        TimeSpan logServerTeardown = LogServer.ShutdownDrainTimeout + LogServer.ListenerTeardownLinger;

        TimeSpan boundedTeardown = webhookCancelled + logServerTeardown;

        Assert.True(
            boundedTeardown < CliInvocation.ProcessTerminationTimeout,
            $"the bounded cancelled-teardown budget is {boundedTeardown}, which does not fit inside the "
            + $"{CliInvocation.ProcessTerminationTimeout} the process gets after SIGINT (#603). Raising a "
            + "teardown budget means raising this ceiling with it — or the row never reaches the wire.");

        // ...and with room to spare, because the sum above is not the whole cost. The scheduler's unwind
        // kills a process TREE per in-flight task and drains its readers, the journal is written, and the
        // worktree exit sweep runs — none of them bounded by a constant this test can read. Half the
        // ceiling is reserved for them; a change that eats into it should have to say so here.
        Assert.True(
            boundedTeardown < CliInvocation.ProcessTerminationTimeout / 2,
            $"the bounded budgets ({boundedTeardown}) now consume more than half of "
            + $"{CliInvocation.ProcessTerminationTimeout}, leaving too little for the scheduler unwind, the "
            + "journal write and the worktree exit sweep, which are unbounded and share the same ceiling");
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public async Task TheDeferredListenerTeardownRunsOnAThreadTheProcessCannotExitOutFrom()
    {
        // ListenerTeardownLinger is the subscriber's only window to read an already-flushed final row
        // before the listener's teardown resets the connection. In the shipped CLI, DisposeAsync is the
        // LAST thing a run does, so a pooled continuation racing process exit loses that window entirely
        // — and process exit resets the connection exactly as hard as Stop() would have, so the linger
        // bought nothing. IsBackground = false is the fix, in full: the runtime does not exit while a
        // foreground thread is running.
        using var temp = new TempPlan();
        LogServer server = Start(temp.Dir);

        Assert.Null(server.DeferredTeardownThread); // nothing deferred before dispose

        await server.DisposeAsync();

        Thread? teardown = server.DeferredTeardownThread;
        Assert.NotNull(teardown);
        Assert.False(
            teardown!.IsBackground,
            "the deferred listener teardown is on a background thread again — process exit will abandon it "
            + "mid-linger and the terminal row will be reset off the wire (#603)");

        // And it does finish, promptly, rather than holding the process open indefinitely: only the
        // linger and the listener stop run on it, with the unbounded accept-loop join handed to the pool.
        Assert.True(
            teardown.Join(LogServer.ListenerTeardownLinger + TimeSpan.FromSeconds(10)),
            "the foreground teardown thread did not finish — a foreground thread that hangs hangs the CLI");
    }

    // The complementary property — that DisposeAsync still RETURNS before the listener is torn down,
    // which PR #599 established and this change must not undo — is deliberately NOT re-asserted here.
    // EventsStreamShutdownTests.ASubscriberReceivesARowAppendedJustBeforeShutdown already fails if it
    // regresses: it reads its terminal row after DisposeAsync returns, so a dispose that awaited the
    // teardown would land the reset first and the row would never arrive. Restating it here could only be
    // done as a wall-clock bound on DisposeAsync against a 250 ms linger — a timing assertion on a
    // machine that, under the whole-solution CI job (#566), is by definition oversubscribed.

    // --- helpers ----------------------------------------------------------------------------

    private static LogServer Start(string planDir)
    {
        TaskNode[] tasks =
        [
            new()
            {
                Id = "01-alpha",
                Directory = "01-alpha",
                Description = "First",
                Action = new ActionDefinition { Path = "action.ps1", Kind = ActionKind.Script },
                Guardrails = [new GuardrailDefinition { Name = "01-x", Path = "01-x.ps1", Kind = ActionKind.Script }]
            }
        ];

        LogServer? server = LogServer.TryStart(planDir, TempPlan.RunId, tasks, port: 0, TextWriter.Null);
        Assert.NotNull(server); // a normal host can bind a loopback ephemeral port
        return server!;
    }

    /// <summary>A throwaway plan directory under the temp path; cleaned up on dispose.</summary>
    private sealed class TempPlan : IDisposable
    {
        public const string RunId = "test-run";

        public string Dir { get; } =
            Path.Combine(Path.GetTempPath(), "gr-term-budget-" + Guid.NewGuid().ToString("N"));

        public TempPlan() => Directory.CreateDirectory(Dir);

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch (Exception) { /* best effort */ }
        }
    }
}
