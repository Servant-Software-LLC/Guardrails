using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #557 — the JIT wave-breakdown agent had write authority over the ENTIRE plan folder while the
/// revert machinery that exists to undo it is scoped to ONE WAVE.
///
/// <para>
/// <b>The mismatch.</b> <see cref="WaveBreakdownInvoker"/> runs the agent with <c>workingDirectory</c> and
/// <c>planDirectory</c> both at the plan folder, <c>PermissionMode = "acceptEdits"</c>, and
/// Read/Write/Edit/Bash/Grep/Glob. It calls the runner directly, so the worktree containment hook is never
/// injected and there is no <c>writeScope</c> on this path at all. The prompt <i>asks</i> it to break down
/// one named wave; nothing enforced that. Meanwhile <see cref="BreakdownInventory"/> covers
/// <c>tasks</c>/<c>guardrails</c>/<c>preflights</c> of a single wave — deliberately, because that scope is
/// what makes its byte-identity property provable.
/// </para>
///
/// <para>
/// <b>The failure.</b> A breakdown authoring wave 3 edits wave 1's <c>tasks/04-…/task.json</c> — plausibly
/// with good intent. The edit is outside the inventory, so no snapshot exists; if the breakdown is then
/// rejected, the revert restores only wave 3 and <b>the wave-1 edit survives a rejected breakdown</b>. If
/// wave 1's task 04 already succeeded, #556 applies and no drift fires; if it has not run, it runs a
/// definition no human wrote and no review saw. This is the harness's own agent, with <c>acceptEdits</c>,
/// running unattended, per wave, with no human between invocations.
/// </para>
///
/// <para>
/// The regression pin the issue asks for is the first test below: an invocation that writes outside its
/// wave must either be REFUSED or leave that file restorable. It was neither.
/// </para>
/// </summary>
public sealed class BreakdownScopeEscapeTests
{
    private const string Wave1 = "wave-01-scaffold";
    private const string Wave2 = "wave-02-build";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// <b>The pin.</b> A breakdown authoring wave 2 reaches into wave 1 and rewrites an already-authored
    /// task. The gate must REJECT — and the halt must NAME the file, because the revert cannot put it back
    /// and an operator who is not told cannot undo it either.
    /// </summary>
    [Fact]
    public async Task ABreakdownThatWritesIntoAnotherWave_IsRefused_AndTheFileIsNamed()
    {
        (WavePlanBuilder b, PlanDefinition plan) = TwoWavePlan();
        using WavePlanBuilder _ = b;

        string victim = Path.Combine(b.PlanDir, Wave1, "tasks", "01-config", "task.json");
        string before = File.ReadAllText(victim);

        var runner = new StubBreakdownRunner((inv, call) =>
        {
            if (call != 1)
            {
                return;
            }

            // A perfectly good wave-2 breakdown...
            AuthorTask(inv.WorkingDirectory, "01-compile");

            // ...that also "helpfully" rewrites wave 1's task 04 so it declares an output wave 2 wants.
            File.WriteAllText(victim,
                """{ "description": "01-config", "writeScope": [], "dependsOn": [], "tier": "heavy" }""");
        });

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        RunReport report = await NewScheduler(plan, journal, new WaveBreakdownInvoker(runner)).RunAsync(plan, Ct);

        Assert.NotNull(report.WaveHalt);
        Assert.NotEqual(WaveHaltKind.BreakdownComplete, report.WaveHalt!.Kind);

        string decision = ReadGateDecision(b.PlanDir);
        Assert.Contains("OUT-OF-WAVE WRITES (#557)", decision, StringComparison.Ordinal);
        Assert.Contains($"{Wave1}/tasks/01-config/task.json (modified)", decision, StringComparison.Ordinal);

        // And the halt is honest about what the quarantine did NOT do: the edit is still on disk, because
        // the wave-scoped revert has no snapshot of it. Saying so is the whole point — the alternative is
        // an operator who believes the rejection undid everything.
        Assert.NotEqual(before, File.ReadAllText(victim));
    }

    /// <summary>
    /// The control that keeps the witness from becoming a tripwire on ordinary work: a breakdown that stays
    /// inside its own wave triggers nothing. Without this, "detect writes" could be satisfied by a check
    /// that fires on every invocation — which would be worse than no check, because an operator learns to
    /// dismiss it and then dismisses the real one.
    /// </summary>
    [Fact]
    public async Task ABreakdownThatStaysInItsOwnWave_TriggersNothing()
    {
        (WavePlanBuilder b, PlanDefinition plan) = TwoWavePlan();
        using WavePlanBuilder _ = b;

        var runner = new StubBreakdownRunner((inv, call) =>
        {
            if (call == 1)
            {
                AuthorTask(inv.WorkingDirectory, "01-compile");
            }
        });

        RunJournal journal = RunJournal.LoadOrCreate(plan);
        await NewScheduler(plan, journal, new WaveBreakdownInvoker(runner)).RunAsync(plan, Ct);

        Assert.DoesNotContain("OUT-OF-WAVE WRITES", ReadGateDecision(b.PlanDir), StringComparison.Ordinal);
    }

    // ---- the witness itself -------------------------------------------------------------------

    /// <summary>
    /// The harness's own mutable roots are not escapes. <c>logs/</c> and <c>state/</c> change on every
    /// invocation by construction — the journal is rewritten, the breakdown's own stream is teed — so a
    /// witness that counted them would fire on every single breakdown and mean nothing.
    /// </summary>
    [Fact]
    public void HarnessOwnedRoots_AreNotEscapes()
    {
        string root = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "logs", "run-1"));
            Directory.CreateDirectory(Path.Combine(root, "state"));
            Directory.CreateDirectory(Path.Combine(root, Wave2));
            File.WriteAllText(Path.Combine(root, "guardrails.json"), "{}");

            var before = BreakdownScopeWitness.Capture(root, Path.Combine(root, Wave2));

            File.WriteAllText(Path.Combine(root, "logs", "run-1", "events.jsonl"), "{}\n");
            File.WriteAllText(Path.Combine(root, "state", "run.json"), "{}");

            Assert.Empty(BreakdownScopeWitness.Escapes(
                before, BreakdownScopeWitness.Capture(root, Path.Combine(root, Wave2))));
        }
        finally { Delete(root); }
    }

    /// <summary>
    /// Content hashes, not timestamps. A breakdown that rewrites a file with IDENTICAL bytes has not
    /// escaped its wave in any sense that matters, and reporting it would train an operator to ignore the
    /// halt — which is how a real escape gets waved through later.
    /// </summary>
    [Fact]
    public void ARewriteWithIdenticalBytes_IsNotAnEscape()
    {
        string root = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, Wave2));
            string file = Path.Combine(root, "guardrails.json");
            File.WriteAllText(file, "{ \"version\": 1 }");

            var before = BreakdownScopeWitness.Capture(root, Path.Combine(root, Wave2));
            File.WriteAllText(file, "{ \"version\": 1 }");   // same bytes, new timestamp

            Assert.Empty(BreakdownScopeWitness.Escapes(
                before, BreakdownScopeWitness.Capture(root, Path.Combine(root, Wave2))));
        }
        finally { Delete(root); }
    }

    /// <summary>
    /// A capture that FAILED must never be reported as the agent deleting the plan. An empty "before" means
    /// no witness was taken, which is a different thing from "there was nothing there" — and the difference
    /// is the whole of a false accusation that would halt a healthy run.
    /// </summary>
    [Fact]
    public void AFailedCapture_ReportsNothing_RatherThanEverything()
    {
        var after = new Dictionary<string, string>(StringComparer.Ordinal) { ["a.json"] = "HASH" };

        Assert.Empty(BreakdownScopeWitness.Escapes(
            new Dictionary<string, string>(StringComparer.Ordinal), after));
    }

    // ---- fixtures ---------------------------------------------------------------------------------

    private static (WavePlanBuilder Builder, PlanDefinition Plan) TwoWavePlan()
    {
        var b = new WavePlanBuilder();
        b.Task(Wave1, "01-config");
        b.WaveStub(Wave2);
        b.WaveBrief(Wave2, "# wave-02-build\n- compile\n");

        PlanDefinition plan = b.Load().Plan!;
        return (b, plan with { Config = plan.Config with { AutonomyPolicy = AutonomyPolicy.Auto } });
    }

    private static Scheduler NewScheduler(PlanDefinition plan, RunJournal journal, WaveBreakdownInvoker invoker) =>
        new(plan, new GreenExecutor(), journal,
            worktreeProvider: new RecordingWorktreeProvider(), observer: IRunObserver.Null, maxParallelism: 4,
            reVerifier: null, breakdownInvoker: invoker, breakdownConfirmations: null);

    private static string ReadGateDecision(string planDir)
    {
        string[] found = Directory.GetFiles(planDir, "gate-decision.txt", SearchOption.AllDirectories);
        Assert.True(found.Length > 0, "the post-breakdown gate wrote no gate-decision.txt");
        return File.ReadAllText(found[^1]);
    }

    private static void AuthorTask(string planDir, string folder)
    {
        string taskDir = Path.Combine(planDir, Wave2, "tasks", folder);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "{{folder}}", "writeScope": [] }""");
        File.WriteAllText(Path.Combine(taskDir, "action.sh"), "#!/bin/sh\necho hi\n");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.sh"), "#!/bin/sh\nexit 0\n");
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gr557-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Delete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class GreenExecutor : ITaskExecutor
    {
        public Task<TaskResult> ExecuteAsync(TaskNode task, WorktreeHandle worktree, CancellationToken ct) =>
            Task.FromResult(new TaskResult
            {
                TaskId = task.Id,
                Outcome = TaskOutcome.Succeeded,
                Summary = "scripted success",
                DeferredSettle = true
            });
    }

    private sealed class StubBreakdownRunner(Action<PromptInvocation, int> author) : IPromptRunner
    {
        private int _invocations;

        public string Name => "breakdown";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            author(invocation, ++_invocations);
            return Task.FromResult(new PromptResult
            {
                Completed = true,
                IsError = false,
                ResultText = "authored the wave",
                CostUsd = 0.10m,
                Summary = "breakdown authored the wave"
            });
        }
    }
}
