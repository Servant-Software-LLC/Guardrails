using System.Diagnostics;
using System.Text.Json.Nodes;
using Guardrails.Cli;
using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;

namespace Guardrails.Integration.Tests.RunEvents;

/// <summary>
/// <c>guardrails attach &lt;plan-folder&gt;</c> (plan 34 §5, issue #560) — a second terminal tails a run's
/// <c>observer.jsonl</c> (task 08's projection) and replays it into a REAL <see cref="LiveRunObserver"/> in
/// its OWN terminal, without touching the run itself.
///
/// <para><b>The design constraint that decides this whole file:</b> the attached view must be driven by the
/// SHIPPED renderer, never by a reimplementation of it — a second table drawing the same data will drift
/// from the first, and "the familiar console table" quietly stops being familiar. So
/// <see cref="Attach_DrivesTheRealLiveRunObserver_FromObserverJsonl"/> and
/// <see cref="Attach_ReplaysTheRecordedCallSequence_InOrder"/> replay their fixture events straight into a
/// genuine <see cref="LiveRunObserver"/> constructed here — never a fake <see cref="IRunObserver"/> that
/// renders a table of its own and gets asserted against instead.</para>
///
/// <para>Every test also drives <c>guardrails attach</c> itself, through the same real composition root
/// (<see cref="CommandFactory.BuildRootCommand"/>) every other <c>*CliTests.cs</c> file in this project
/// uses. <b>These are written to FAIL right now: <c>attach</c> does not exist yet</b> (task 10) — the verb
/// is unregistered, so parsing it fails before any command body runs, at exit code
/// <see cref="ExitCodes.HarnessError"/>. <c>ObserverProjection</c> (task 07's stub, task 08's job) does not
/// write <c>observer.jsonl</c> from a real run yet either, so every fixture here hand-writes the file
/// directly, in the line schema task 07 pinned: one JSON object per line, a <c>member</c> field naming the
/// <see cref="IRunObserver"/> member, plus its arguments as named fields.</para>
/// </summary>
[Collection(LiveDisplayCollection.Name)]
public sealed class AttachReplayTests
{
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // CLI plumbing — the SAME in-process pattern LogsCliTests / CliExitCodeTests use.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<(int ExitCode, string Output, string Error)> InvokeAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = CommandFactory.BuildRootCommand(io);
        int exit = await root.Parse(args).InvokeAsync(configuration: null, TestContext.Current.CancellationToken);
        return (exit, io.OutText, io.ErrorText);
    }

    /// <summary>
    /// Run a real (trivial) plan to completion and return its <c>logs/&lt;runId&gt;/</c> directory —
    /// the SAME tree the executor writes attempt logs under, so a fixture written into it looks exactly
    /// like what a real run leaves behind. The run itself must genuinely succeed; a fixture built on a
    /// failed setup run would prove nothing about <c>attach</c>.
    /// </summary>
    private static async Task<string> RunToCompletionAsync(ScriptPlanBuilder plan)
    {
        (int exit, _, _) = await InvokeAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");
        Assert.Equal(ExitCodes.Success, exit);

        JournalDocument document = JournalReader.Read(RunJournal.PathFor(plan.PlanDir));
        string logsDir = Path.Combine(plan.PlanDir, "logs", document.RunId);
        Directory.CreateDirectory(logsDir);
        return logsDir;
    }

    /// <summary>
    /// TWO genuinely separate OS processes running <c>guardrails attach</c> — the literal #560 acceptance
    /// ("a second terminal … twice concurrently"). Driving two attachments IN-PROCESS instead would
    /// collide on Spectre's own process-wide live-display exclusivity lock (see
    /// <see cref="LiveDisplayCollection"/>'s remarks) the instant a correct <c>attach</c> constructs a
    /// second <see cref="LiveRunObserver"/> — a false failure about this test's own plumbing, not about
    /// whether two watchers can safely coexist. A real second terminal has its own process and its own
    /// Spectre state, so this is also the more faithful reproduction, not merely a workaround.
    /// </summary>
    private static async Task<(int ExitCode, string Output, string Error)> InvokeAttachOutOfProcessAsync(string planDir)
    {
        string appHost = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Guardrails.Cli.exe" : "Guardrails.Cli");
        ProcessStartInfo psi = File.Exists(appHost)
            ? new ProcessStartInfo(appHost)
            : new ProcessStartInfo("dotnet");
        if (!File.Exists(appHost))
        {
            psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Guardrails.Cli.dll"));
        }

        psi.ArgumentList.Add("attach");
        psi.ArgumentList.Add(planDir);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;

        using Process process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{psi.FileName}'.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await stdout, await stderr);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Fixtures — a plan task and the observer.jsonl call sequence a run over it would produce.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static TaskNode FlatTask(string folder) => new()
    {
        Id = folder,
        Directory = $"/fake/plan/tasks/{folder}",
        Description = $"fixture — {folder}",
        Action = new ActionDefinition { Path = "action.sh", Kind = ActionKind.Script },
        Guardrails = [new GuardrailDefinition { Name = "01-check", Path = "01-check.sh", Kind = ActionKind.Script }]
    };

    /// <summary>
    /// One observed call: the member name (for the census), the exact <c>observer.jsonl</c> line it
    /// projects to, and the delegate that drives the SAME call against a real <see cref="IRunObserver"/>
    /// — so a fixture and the real renderer it feeds are always built from one source, never two
    /// independently-typed copies that could quietly drift apart.
    /// </summary>
    private sealed record ObservedCall(string Member, string JsonLine, Action<IRunObserver> Invoke);

    private static ObservedCall TaskStartingCall(TaskNode task) => new(
        "TaskStarting",
        $$"""{"member":"TaskStarting","taskId":"{{task.Id}}"}""",
        o => o.TaskStarting(task));

    private static ObservedCall AttemptFinishedCall(TaskNode task, int attempt, AttemptOutcome outcome) => new(
        "AttemptFinished",
        $$"""{"member":"AttemptFinished","taskId":"{{task.Id}}","attempt":{{attempt}},"outcome":"{{outcome}}"}""",
        o => o.AttemptFinished(task, attempt, outcome));

    private static ObservedCall TaskFinishedCall(TaskNode task, TaskOutcome outcome, string summary) => new(
        "TaskFinished",
        $$"""{"member":"TaskFinished","taskId":"{{task.Id}}","outcome":"{{outcome}}","summary":"{{summary}}"}""",
        o => o.TaskFinished(new TaskResult { TaskId = task.Id, Outcome = outcome, Summary = summary }));

    /// <summary>The straight-line sequence a single successful task's run leaves in observer.jsonl.</summary>
    private static ObservedCall[] ReplaySequence(TaskNode task) =>
    [
        TaskStartingCall(task),
        AttemptFinishedCall(task, 1, AttemptOutcome.Succeeded),
        TaskFinishedCall(task, TaskOutcome.Succeeded, "ok"),
    ];

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Tests
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Fact]
    public async Task Attach_DrivesTheRealLiveRunObserver_FromObserverJsonl()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        string logsDir = await RunToCompletionAsync(plan);

        TaskNode task = FlatTask("01-first");
        ObservedCall[] calls = ReplaySequence(task);
        File.WriteAllLines(Path.Combine(logsDir, "observer.jsonl"), calls.Select(c => c.JsonLine));

        // The fixture is not a fantasy shape: fed straight into the SHIPPED renderer, in the exact
        // order attach would read it off disk, it must not throw. `attach` has no excuse to invent a
        // parallel table — the real one already accepts this call sequence.
        await using (var realRenderer = new LiveRunObserver([task]))
        {
            Exception? ex = Record.Exception(() =>
            {
                foreach (ObservedCall call in calls)
                {
                    call.Invoke(realRenderer);
                }
            });
            Assert.Null(ex);
        }

        // `guardrails attach` does not exist yet (task 10) — the verb is unregistered, so it never gets
        // as far as reading the fixture at all.
        (int exit, _, _) = await InvokeAsync("attach", plan.PlanDir);
        Assert.Equal(ExitCodes.Success, exit);
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public async Task Attach_ReplaysTheRecordedCallSequence_InOrder()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first").AddTask("02-second", dependsOn: "01-first");
        string logsDir = await RunToCompletionAsync(plan);

        TaskNode taskA = FlatTask("01-first");
        TaskNode taskB = FlatTask("02-second");

        // Genuinely INTERLEAVED across two tasks — not grouped per task — so a replay that batches by
        // task, or otherwise reorders, is distinguishable from one that walks the file top to bottom.
        ObservedCall[] calls =
        [
            TaskStartingCall(taskA),
            TaskStartingCall(taskB),
            AttemptFinishedCall(taskA, 1, AttemptOutcome.Succeeded),
            TaskFinishedCall(taskA, TaskOutcome.Succeeded, "ok"),
            AttemptFinishedCall(taskB, 1, AttemptOutcome.Succeeded),
            TaskFinishedCall(taskB, TaskOutcome.Succeeded, "ok"),
        ];

        string observerJsonlPath = Path.Combine(logsDir, "observer.jsonl");
        File.WriteAllLines(observerJsonlPath, calls.Select(c => c.JsonLine));

        // Driven against the REAL renderer in exactly this interleaved order, it must not throw.
        await using (var realRenderer = new LiveRunObserver([taskA, taskB]))
        {
            Exception? ex = Record.Exception(() =>
            {
                foreach (ObservedCall call in calls)
                {
                    call.Invoke(realRenderer);
                }
            });
            Assert.Null(ex);
        }

        // The fixture file on disk reproduces that exact interleaved order — the sequence `attach`
        // must walk top to bottom, never grouped or reordered by task.
        string[] lines = File.ReadAllLines(observerJsonlPath);
        Assert.Equal(calls.Length, lines.Length);
        for (int i = 0; i < calls.Length; i++)
        {
            JsonNode? line = JsonNode.Parse(lines[i]);
            Assert.Equal(calls[i].Member, line?["member"]?.GetValue<string>());
        }

        (int exit, _, _) = await InvokeAsync("attach", plan.PlanDir);
        Assert.Equal(ExitCodes.Success, exit);
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public async Task Attach_OnAFinishedRun_ReplaysToCompletion()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        string logsDir = await RunToCompletionAsync(plan); // the run is ALREADY over before attach starts

        TaskNode task = FlatTask("01-first");
        ObservedCall[] calls = ReplaySequence(task);
        File.WriteAllLines(Path.Combine(logsDir, "observer.jsonl"), calls.Select(c => c.JsonLine));

        // No cancellation is supplied — a correct implementation must notice the run already ended and
        // return on its own; it must not behave like `tail -f` and wait for lines that will never
        // arrive. This is exactly what makes an overnight escalation diagnosable: an operator does not
        // have to guess how long to wait, or send Ctrl-C, before the replay is done.
        Task<(int ExitCode, string Output, string Error)> attaching = InvokeAsync("attach", plan.PlanDir);
        Task completed = await Task.WhenAny(
            attaching, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Same(attaching, completed);

        Assert.Equal(ExitCodes.Success, (await attaching).ExitCode);
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public async Task TwoConcurrentAttachments_BothReplayEveryEvent_AndNeitherWritesToTheRun()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        string logsDir = await RunToCompletionAsync(plan);

        TaskNode task = FlatTask("01-first");
        ObservedCall[] calls = ReplaySequence(task);
        string observerJsonlPath = Path.Combine(logsDir, "observer.jsonl");
        File.WriteAllLines(observerJsonlPath, calls.Select(c => c.JsonLine));

        byte[] observerBefore = File.ReadAllBytes(observerJsonlPath);
        byte[] journalBefore = File.ReadAllBytes(RunJournal.PathFor(plan.PlanDir));

        Task<(int ExitCode, string Output, string Error)> first = InvokeAttachOutOfProcessAsync(plan.PlanDir);
        Task<(int ExitCode, string Output, string Error)> second = InvokeAttachOutOfProcessAsync(plan.PlanDir);
        (int ExitCode, string Output, string Error)[] results = await Task.WhenAll(first, second);

        // A watcher that perturbs the run is worse than no watcher — this must hold even before
        // `attach` exists (and today, trivially does, because nothing ran at all).
        Assert.Equal(observerBefore, File.ReadAllBytes(observerJsonlPath));
        Assert.Equal(journalBefore, File.ReadAllBytes(RunJournal.PathFor(plan.PlanDir)));

        // Both attachments must replay every event to completion — today NEITHER can: the verb is
        // unregistered in this worktree's CLI.
        Assert.All(results, r => Assert.Equal(ExitCodes.Success, r.ExitCode));
    }

    [Trait("Category", "RunEvents")]
    [Fact]
    public async Task Attach_OnAMissingObserverJsonl_FailsWithAnActionableMessage()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        string logsDir = await RunToCompletionAsync(plan);

        // Make the missing-file condition DELIBERATE rather than assumed. This test originally relied on a
        // real run leaving no observer.jsonl — true only while ObserverProjection was task 07's throwing
        // stub. Task 08 implemented it and task 15 wired it into the production observer chain, so a real
        // run now DOES write the file: attach correctly FOUND it and exited Success, and this test failed
        // on `Expected: 1, Actual: 0`. The premise was wrong, not the feature. Every other fixture in this
        // class controls observer.jsonl explicitly (see the class remarks); this one now does too.
        // File.Delete is a no-op when the path is absent, so this holds on either tree.
        File.Delete(Path.Combine(logsDir, "observer.jsonl"));

        (int exit, string output, string error) = await InvokeAsync("attach", plan.PlanDir);
        string combined = output + error;

        Assert.Equal(ExitCodes.HarnessError, exit);
        // Actionable, not a stack trace: no raw .NET exception noise, and it names the missing file.
        Assert.DoesNotContain("Exception", combined, StringComparison.Ordinal);
        Assert.DoesNotContain(" at Guardrails.", combined, StringComparison.Ordinal);
        Assert.Contains("observer.jsonl", combined, StringComparison.OrdinalIgnoreCase);
    }
}
