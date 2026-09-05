using System.CommandLine;
using System.Reflection;
using System.Text.Json;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Cli.Ui;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests.RunEvents;

/// <summary>
/// The exit-path matrix for <c>run-finished</c> (plan 34, issue #585 layer 3): every way a
/// <c>guardrails run</c> can end must still raise <see cref="IRunObserver.RunFinished"/> on the REAL
/// composed observer chain, or an unattended supervisor tailing <c>events.jsonl</c> cannot tell a run
/// that ended badly from one that is merely still running.
///
/// <para>Tests 1–3 drive the real CLI (<see cref="RunCommand.Create"/>) over real script-based plans —
/// the green path, a needs-human halt, and a terminal-gate failure whose returned exit code DIFFERS
/// from what <c>Finish</c> itself computes (the DAG drained green, so <c>Finish</c>'s own value is
/// <see cref="ExitCodes.Success"/>; the terminal-gate-failure branch overrides it to
/// <see cref="ExitCodes.TaskFailed"/> afterward — a row that carries <c>Finish</c>'s return value rather
/// than the code the process actually exits with cannot tell the two apart).</para>
///
/// <para>Tests 4–6 force an unhandled throw out of <c>RunCommand.ExecuteAsync</c> — the private method
/// (reached only via reflection; it is not part of this task's writable surface) that builds the
/// <see cref="Guardrails.Core.Execution.Scheduler"/> and drives it. A validated plan can never make the
/// Scheduler itself throw (every internal fault is deliberately converted to an honest-halt
/// <c>Abort</c>, issue #150), so these three tests instead hand <c>ExecuteAsync</c> a plan with a genuine
/// dependency cycle, bypassing <c>PlanProbe</c>/<c>PlanValidator</c> (GR2007) entirely — exactly the
/// scenario <see cref="Scheduler.RunAsync"/>'s own cycle guard documents itself as existing for
/// ("keeps the scheduler safe when embedded directly"). One task's id is a fake-secret-shaped string,
/// so it lands verbatim in the thrown exception's <c>Message</c> (the cycle path is interpolated into
/// it) — the concrete secret tests 5/6 prove never reaches disk.</para>
///
/// <para>Test 7 is the composition-root proof: a real run whose <c>run-finished</c> row must arrive
/// through the SAME chain <see cref="RunCommand.BuildObserverChain"/> assembles for every task/attempt
/// event in the run — never a hand-rolled <see cref="RunEventStream"/> a fix might construct on the
/// side, which would satisfy a narrow unit test while the composed chain still swallows the event.</para>
///
/// <para>Written to FAIL right now: nothing in <c>RunCommand.cs</c> calls <c>RunFinished</c> on any exit
/// path yet (task 11's job). Do not touch production code from this file.</para>
/// </summary>
public sealed class RunFinishedExitPathTests
{
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // CLI + journal helpers (tests 1–3, 7)
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<int> InvokeAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = CommandFactory.BuildRootCommand(io);
        return await root.Parse(args).InvokeAsync();
    }

    private static string RunIdOf(string planDir) => JournalReader.Read(RunJournal.PathFor(planDir)).RunId;

    private static string EventsPathFor(string planDir) =>
        Path.Combine(planDir, "logs", RunIdOf(planDir), "events.jsonl");

    /// <summary>The single <c>run-finished</c> row in <paramref name="eventsPath"/>, or a failing assertion.</summary>
    private static JsonElement SingleRunFinishedRow(string eventsPath)
    {
        Assert.True(File.Exists(eventsPath), $"run-finished never reached events.jsonl at '{eventsPath}'.");

        List<JsonElement> rows =
        [
            .. File.ReadAllLines(eventsPath)
                .Select(line => JsonDocument.Parse(line).RootElement.Clone())
                .Where(e => e.GetProperty("kind").GetString() == "run-finished")
        ];

        return Assert.Single(rows);
    }

    private static void WriteTerminalGate(string planDir, bool passes)
    {
        string dir = Path.Combine(planDir, "guardrails");
        Directory.CreateDirectory(dir);

        bool ps = OperatingSystem.IsWindows();
        string path = Path.Combine(dir, ps ? "01-terminal.ps1" : "01-terminal.sh");
        const string catches = "# catches: a terminal-gate failure that should still stamp run-finished (GR2027 requires this line)";
        string body = ps
            ? $"{catches}\n{(passes ? "exit 0" : "exit 1")}\r\n"
            : $"#!/usr/bin/env bash\n{catches}\n{(passes ? "exit 0" : "exit 1")}\n";

        File.WriteAllText(path, body);
        if (!ps)
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    private static void WriteNeedsHumanAction(ScriptPlanBuilder plan, string taskId, string question)
    {
        string fragment = "{\"needsHuman\": \"" + question + "\"}";
        string body = OperatingSystem.IsWindows()
            ? $"Set-Content -NoNewline -Path $env:GUARDRAILS_STATE_OUT -Value '{fragment}'\r\nexit 0\r\n"
            : $"#!/usr/bin/env bash\nprintf '%s' '{fragment}' > \"$GUARDRAILS_STATE_OUT\"\nexit 0\n";
        File.WriteAllText(plan.ActionPath(taskId), body);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Fixtures for the cyclic plan (tests 4–6)
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class TempPlanRoot : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gr-run-finished-exit-" + Guid.NewGuid().ToString("N"));

        public TempPlanRoot() => Directory.CreateDirectory(Root);

        public string Dir(params string[] parts)
        {
            string path = Path.Combine([Root, .. parts]);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (Exception) { /* best effort */ }
        }
    }

    private static TaskNode CycleTask(string id, string dependsOnId) => new()
    {
        Id = id,
        Directory = $"/fake/plan/tasks/{id}",
        Description = $"fixture — {id}",
        DependsOn = [dependsOnId],
        Action = new ActionDefinition { Path = "action.sh", Kind = ActionKind.Script },
        Guardrails = [new GuardrailDefinition { Name = "01-check", Path = "01-check.sh", Kind = ActionKind.Script }]
    };

    /// <summary>
    /// A two-task plan whose tasks depend on each other — a real cycle, rejected by GR2007 before any
    /// normal <c>run</c> ever reaches the scheduler. <paramref name="planDirectory"/> must be a real,
    /// existing directory: <c>SchedulerFactory.Create</c> runs BEFORE the cycle guard fires (it seeds
    /// <c>run.json</c> via <c>RunJournal.LoadOrCreate</c>), so a fake path would throw the wrong
    /// exception for the wrong reason. One task's id is the fake-secret string, so
    /// <c>Scheduler.RunAsync</c>'s cycle exception message — built by interpolating the cycle's task
    /// ids — carries it verbatim.
    /// </summary>
    private static PlanDefinition CyclicPlanCarryingSecret(string planDirectory, out string secret)
    {
        secret = "sk-FAKESECRET-1234567890abcdef";
        TaskNode taskA = CycleTask(secret, "01-b");
        TaskNode taskB = CycleTask("01-b", secret);

        return new PlanDefinition
        {
            PlanDirectory = planDirectory,
            Workspace = planDirectory,
            Config = new RunConfig { Version = 1 },
            Tasks = [taskA, taskB]
        };
    }

    /// <summary>
    /// Reflectively invoke <c>RunCommand.ExecuteAsync</c> (private — it is not part of this task's
    /// writable surface, and its signature/visibility are task 11's to decide) with a plan that makes
    /// the real <see cref="Scheduler.RunAsync"/> throw before any task ever runs, and return the
    /// exception that propagates out. An async method never throws synchronously from the call itself —
    /// the fault is captured on the returned <see cref="Task"/> — so awaiting it is what surfaces it.
    /// </summary>
    /// <summary>
    /// The worktree-mode answer these plans actually have. None of them sets <c>maxParallelism</c>, so
    /// <see cref="WorktreeModeReason.SerialByConfiguration"/> is the truthful resolution rather than a
    /// demotion (#596), and the git probe is never run. The value cannot affect what these tests assert —
    /// they fault before any task is scheduled — but it still has to be HONEST: a
    /// <see cref="WorktreeModeReason.WorkspaceNotAGitRepository"/> here would read as a probe result the
    /// run never obtained.
    /// </summary>
    private static readonly WorktreeModeResolution SerialByConfiguration =
        new() { Enabled = false, Reason = WorktreeModeReason.SerialByConfiguration };

    private static async Task<Exception> CaptureExecuteAsyncThrowAsync(PlanDefinition plan, IRunObserver observer)
    {
        MethodInfo method = typeof(RunCommand).GetMethod("ExecuteAsync", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("RunCommand.ExecuteAsync was not found by reflection.");

        object?[] arguments =
            [plan, observer, null, null, null, null, SerialByConfiguration, CancellationToken.None];

        // A reflective call is not compile-checked, so a signature change surfaces here as
        // TargetParameterCountException — "Parameter count mismatch", naming neither the method nor what
        // moved. #596 added the worktreeMode parameter and that is exactly what CI reported, on every OS,
        // with nothing pointing at the cause. This guard turns the next one into a one-line answer.
        ParameterInfo[] parameters = method.GetParameters();
        if (parameters.Length != arguments.Length)
        {
            throw new InvalidOperationException(
                $"RunCommand.ExecuteAsync now takes {parameters.Length} parameters, not the " +
                $"{arguments.Length} this helper passes: " +
                string.Join(", ", parameters.Select(p => $"{p.ParameterType.Name} {p.Name}")) +
                ". Update the argument list above to match.");
        }

        var task = (Task)method.Invoke(null, arguments)!;

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new InvalidOperationException("RunCommand.ExecuteAsync did not throw for a plan carrying a dependency cycle.");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 1. The green run.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Fact]
    public async Task RunFinished_FiresOnAGreenRun()
    {
        using var plan = new ScriptPlanBuilder()
            .AddTask("01-first")
            .AddTask("02-second", dependsOn: "01-first");

        int exit = await InvokeAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");
        Assert.Equal(ExitCodes.Success, exit);

        JsonElement row = SingleRunFinishedRow(EventsPathFor(plan.PlanDir));
        Assert.Equal(ExitCodes.Success, row.GetProperty("exitCode").GetInt32());
        Assert.False(row.TryGetProperty("faultKind", out _), "a green run must carry no faultKind.");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 2. A needs-human halt.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Fact]
    public async Task RunFinished_FiresOnANeedsHumanHalt()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-escalates");
        WriteNeedsHumanAction(plan, "01-escalates", "why does this need a human");

        int exit = await InvokeAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");
        Assert.Equal(ExitCodes.TaskFailed, exit);

        JsonElement row = SingleRunFinishedRow(EventsPathFor(plan.PlanDir));
        Assert.Equal(ExitCodes.TaskFailed, row.GetProperty("exitCode").GetInt32());
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 3. A terminal-gate failure — the exit code Finish() itself computed is NOT what the row must carry.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Fact]
    public async Task RunFinished_FiresOnATerminalGateFailure()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");
        WriteTerminalGate(plan.PlanDir, passes: false);

        var io = new StringConsoleIo();
        var root = CommandFactory.BuildRootCommand(io);
        int exit = await root.Parse(["run", plan.PlanDir, "--no-ui", "--no-log-server"])
            .InvokeAsync(configuration: null, TestContext.Current.CancellationToken);
        Assert.True(exit == ExitCodes.TaskFailed, $"exit={exit}\n{io.OutText}");

        // The DAG drained green, so Finish()'s OWN computed value here is ExitCodes.Success — the
        // terminal-gate-failure branch overrides it to TaskFailed afterward. A row stamped with
        // Finish()'s return value rather than the code the run actually exited with would read 0 here.
        JsonElement row = SingleRunFinishedRow(EventsPathFor(plan.PlanDir));
        Assert.Equal(ExitCodes.TaskFailed, row.GetProperty("exitCode").GetInt32());
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 4. ExecuteAsync throws — null exitCode, faultKind is the exception's type name.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Fact]
    public async Task RunFinished_FiresWhenExecuteAsyncThrows_WithNullExitCodeAndTheTypeName()
    {
        using var tree = new TempPlanRoot();
        string logsRoot = tree.Dir("logs", "throws-run");
        PlanDefinition plan = CyclicPlanCarryingSecret(tree.Root, out _);

        OnTheFlyDiagramObserver observer = RunCommand.BuildObserverChain(
            IRunObserver.Null, logsRoot, "throws-run", plan, logUrlForTask: null, diagramSeed: null);

        await CaptureExecuteAsyncThrowAsync(plan, observer);

        JsonElement row = SingleRunFinishedRow(Path.Combine(logsRoot, "events.jsonl"));
        Assert.False(
            row.TryGetProperty("exitCode", out _),
            "the run never reached a verdict — a fabricated exit code would be a lie.");
        Assert.Equal(nameof(InvalidOperationException), row.GetProperty("faultKind").GetString());
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 5. The fault's row never carries the exception's message — a security property, not tidiness.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Fact]
    public async Task RunFinished_OnAFault_CarriesNoExceptionMessage()
    {
        using var tree = new TempPlanRoot();
        string logsRoot = tree.Dir("logs", "no-leak-run");
        PlanDefinition plan = CyclicPlanCarryingSecret(tree.Root, out string secret);

        OnTheFlyDiagramObserver observer = RunCommand.BuildObserverChain(
            IRunObserver.Null, logsRoot, "no-leak-run", plan, logUrlForTask: null, diagramSeed: null);

        Exception thrown = await CaptureExecuteAsyncThrowAsync(plan, observer);
        Assert.Contains(secret, thrown.Message, StringComparison.Ordinal); // the fixture really carries it.

        string eventsPath = Path.Combine(logsRoot, "events.jsonl");
        Assert.True(File.Exists(eventsPath), $"run-finished never reached events.jsonl at '{eventsPath}'.");
        string raw = await File.ReadAllTextAsync(eventsPath, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(secret, raw, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 6. The catch that records faultKind must rethrow BARE — throw ex; would reset the stack trace.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Fact]
    public async Task TheThrownExceptionStillPropagates_Unchanged()
    {
        using var tree = new TempPlanRoot();
        string logsRoot = tree.Dir("logs", "propagates-run");
        PlanDefinition plan = CyclicPlanCarryingSecret(tree.Root, out string secret);

        OnTheFlyDiagramObserver observer = RunCommand.BuildObserverChain(
            IRunObserver.Null, logsRoot, "propagates-run", plan, logUrlForTask: null, diagramSeed: null);

        Exception thrown = await CaptureExecuteAsyncThrowAsync(plan, observer);

        // Proves the catch actually ran (not merely that the raw, un-intercepted exception passed through).
        JsonElement row = SingleRunFinishedRow(Path.Combine(logsRoot, "events.jsonl"));
        Assert.Equal(nameof(InvalidOperationException), row.GetProperty("faultKind").GetString());

        Assert.IsType<InvalidOperationException>(thrown);
        Assert.Contains(secret, thrown.Message, StringComparison.Ordinal);

        // throw ex; resets StackTrace to the rethrow site, discarding the original throwing frame; only a
        // bare throw; keeps Scheduler.RunAsync's cycle-check frame in it.
        Assert.Contains("Scheduler.RunAsync", thrown.StackTrace ?? string.Empty, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // 7. The composition-root proof — the row must arrive through the REAL chain, never a hand-rolled one.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    [Trait("Category", "RunEvents")]
    [Fact]
    public async Task BuildObserverChain_WiresTheEventStream_SoRunFinishedReachesEventsJsonl()
    {
        using var plan = new ScriptPlanBuilder()
            .AddTask("01-first")
            .AddTask("02-second", dependsOn: "01-first");

        int exit = await InvokeAsync("run", plan.PlanDir, "--no-ui", "--no-log-server");
        Assert.Equal(ExitCodes.Success, exit);

        string eventsPath = EventsPathFor(plan.PlanDir);
        string runId = RunIdOf(plan.PlanDir);

        List<JsonElement> allRows =
        [
            .. File.ReadAllLines(eventsPath).Select(line => JsonDocument.Parse(line).RootElement.Clone())
        ];
        Assert.NotEmpty(allRows); // task/attempt lifecycle rows exist — this is the REAL chain, not a stub.

        JsonElement runFinished = SingleRunFinishedRow(eventsPath);
        Assert.Equal(runId, runFinished.GetProperty("runId").GetString());
        Assert.False(runFinished.TryGetProperty("taskId", out _), "run-finished is run-scoped, never task-scoped.");

        // The run-finished row must be the LAST thing the composed chain wrote — proof it travelled
        // through the SAME live stream every other event in this run used, in the SAME order, rather
        // than being appended by some separately-constructed writer on the side.
        int maxOtherSeq = allRows
            .Where(e => e.GetProperty("kind").GetString() != "run-finished")
            .Select(e => e.GetProperty("seq").GetInt32())
            .Max();
        Assert.True(
            runFinished.GetProperty("seq").GetInt32() > maxOtherSeq,
            "run-finished's seq must be the highest in the file — it settled the run after every other event.");
    }
}
