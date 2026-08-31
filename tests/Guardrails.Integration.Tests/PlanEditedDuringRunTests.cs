using System.CommandLine;
using System.Diagnostics;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Plan 31 §5.5 — the mid-run plan-folder edit advisory (#545 part 3), on REAL runs over REAL git
/// repositories. The feature's entire value is that it reports HUMAN edits, so three of these five pins are
/// about the boundary between "an operator edited the plan folder during my run" and "the harness wrote to
/// its own plan folder, as it does constantly".
///
/// <list type="bullet">
///   <item><b>P1</b> — the positive pin: a guardrail script edited mid-run emits exactly ONE
///     <c>DecisionRecorded</c> call and exactly ONE <c>decisions[]</c> entry with
///     <c>boundary: "plan-edit"</c> / <c>decision: "observed"</c>, naming that task and that file.</item>
///   <item><b>P2</b> — the negative pin, rewritten against a REACHABLE state (§5.3). The real mid-run
///     definition writer is JIT wave breakdown: <c>WaveBreakdownInvoker</c> runs a subprocess rooted at the
///     PLAN directory with <c>Write</c>/<c>Edit</c>/<c>Bash</c> at <c>acceptEdits</c> and no containment
///     hook, and <c>BreakdownInventory.Revert</c> then moves its output to <c>rejected/</c>. Neither may
///     fire the watch. (The earlier revision's pin was written against an overwatcher FIX — which
///     <c>OverwatchFixClassifier</c> marks <c>v1-inert</c>, so it tested an unreachable state and would
///     have passed with the whole feature absent.)</item>
///   <item><b>P3</b> — outcome inertness, asserted on the EXIT CODE and the DELIVERY RECORD rather than on
///     the <c>SuppressesDelivery</c> predicate: <c>observed</c> is neither <c>proceeded-best-guess</c> nor
///     <c>proceeded-unreviewed</c>, so a run carrying only an observation still delivers and still exits
///     <c>0</c>, not <c>5</c>.</item>
///   <item><b>P4</b> — the ignore-list pin, both halves in ONE run: the watch is silent on a stray
///     <c>.DS_Store</c> while that same run's recorded <c>TaskDefinitionHash</c> still CHANGES. That is
///     what makes the watch quieter than the hash BY DESIGN rather than by accident.</item>
///   <item><b>P5</b> — the rendered text carries all three §5.1 consequences. Asserted on the string,
///     because this is the one place a half-true message actively misleads: "your edit was ignored" is
///     FALSE (prompts and guardrail scripts ARE re-read per attempt).</item>
/// </list>
///
/// <para><b>P2 and P4 are DECLARED EXEMPTIONS from the red census.</b> Both assert an ABSENCE that is
/// trivially true while the watch is inert — nothing emits a <c>plan-edit</c> entry at all — so a CORRECT
/// test is GREEN on the stub tree and demanding red would demand a correct implementation fail. Each
/// therefore carries a POSITIVE assertion that its scenario actually HAPPENED (the breakdown really was
/// invoked and reverted; the stray file really was created and really did move the hash), so neither can
/// pass by never reaching the state it is about. Their job is to stay green after the wiring lands.</para>
/// </summary>
public sealed class PlanEditedDuringRunTests : IClassFixture<HostRepoCleanlinessGuard>
{
    private const string Editor = "01-edit";
    private const string Target = "02-target";
    private const string Wave1 = "wave-01-scaffold";
    private const string Wave2 = "wave-02-build";

    /// <summary>The literal §5.4 headline the end-of-run rendering opens with.</summary>
    private const string RenderedHeadline = "PLAN FOLDER EDITED";

    private static readonly bool Ps = OperatingSystem.IsWindows();

    private static string Script(string stem) => Ps ? stem + ".ps1" : stem + ".sh";

    /// <summary>The guardrail file the operator edits mid-run, as a task-relative <c>/</c>-normalized label.</summary>
    private static string GuardrailLabel => "guardrails/" + Script("01-check");

    // ── P1 — the positive pin ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AGuardrailEditedMidRun_EmitsExactlyOneObservedPlanEditDecision()
    {
        using var repo = new TempGitRepo("gr-pedr-p1");
        string planDir = CreateMidRunEditPlan(repo.RepoPath, MidRunWrite.ModifyTargetGuardrail);

        (RunReport report, RunJournal journal, RecordingObserver observer) =
            await RunWorktreeAsync(planDir, repo, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded,
            "the mid-run edit appends a comment AFTER `exit 0`, so every task must still be green; got " +
            string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));

        // The edit really landed on the real plan folder (not on a segment worktree's copy of it).
        Assert.Contains("operator edit during the run",
            File.ReadAllText(Path.Combine(planDir, "tasks", Target, "guardrails", Script("01-check"))));

        // ── LIVE surface: exactly ONE DecisionRecorded call at the plan-edit boundary ────────────
        // Filtered to boundary == "plan-edit": a run produces other decisions, so counting decisions of
        // ANY boundary would be a different (and much weaker) assertion.
        DecisionEntry live = Assert.Single(observer.Decisions, d => d.Boundary == "plan-edit");
        Assert.Equal("observed", live.Decision);

        // ── DURABLE surface: exactly ONE decisions[] entry, naming that TASK and that FILE ──────
        DecisionEntry entry = Assert.Single(PlanEditEntries(journal));
        Assert.Equal("observed", entry.Decision);
        Assert.Contains(Target, entry.Subject);
        Assert.Contains(GuardrailLabel, entry.Detail.Replace('\\', '/'));
        Assert.False(string.IsNullOrWhiteSpace(entry.Headline));
    }

    // ── P2 — the negative pin, against a REACHABLE state (DECLARED EXEMPTION) ────────────────────

    [Fact]
    public async Task AJitWaveBreakdownFollowedByRevert_EmitsZeroPlanEditEntries()
    {
        using var repo = new TempGitRepo("gr-pedr-p2");
        string planDir = CreateWavedJitPlan(repo.RepoPath);

        var stub = new InvalidWaveAuthoringRunner();
        (RunReport report, RunJournal journal, _) =
            await RunWorktreeAsync(planDir, repo, TestContext.Current.CancellationToken,
                breakdownInvoker: new WaveBreakdownInvoker(stub));

        // ── The scenario really HAPPENED: this is what keeps the absence below meaningful ────────
        // The JIT breakdown ran a subprocess rooted at the PLAN directory and authored task folders
        // there, and the gate then rejected them, so BreakdownInventory.Revert moved what it wrote to
        // rejected/. Without these, "zero plan-edit entries" would pass with the JIT path never reached.
        Assert.Equal(1, stub.Invocations);
        Assert.NotNull(report.WaveHalt);
        Assert.Equal(WaveHaltKind.BreakdownFailed, report.WaveHalt!.Kind);
        Assert.Equal(Wave2, report.WaveHalt.WaveDir);

        string[] rejected = Directory.GetDirectories(
            Path.Combine(planDir, "logs"), "rejected", SearchOption.AllDirectories);
        Assert.Contains(rejected, r => File.Exists(Path.Combine(r, "tasks", "01-bad", "task.json")));

        // The wave is back to its empty JIT stub — the revert restored it, so the plan stays loadable.
        string wave2Tasks = Path.Combine(planDir, Wave2, "tasks");
        Assert.True(Directory.Exists(wave2Tasks));
        Assert.Empty(Directory.GetFileSystemEntries(wave2Tasks));

        // ── The pin: the harness's OWN plan-folder writes are NOT operator edits ─────────────────
        // §5.3's rule — the Scheduler re-baselines PLAN-WIDE after a JIT breakdown attempt and after
        // BreakdownInventory.Revert — because an advisory that fires on the harness's own writes stops
        // being read (#229).
        Assert.Empty(PlanEditEntries(journal));
    }

    // ── P3 — outcome inertness, on the exit code and the delivery record ─────────────────────────

    [Fact]
    public async Task ARunCarryingOnlyAPlanEditObservation_FastForwardsAndExitsZero()
    {
        using var repo = new TempGitRepo("gr-pedr-p3");
        string initialHead = repo.HeadSha();
        string originalBranch = repo.CurrentBranch();
        string planDir = CreateMidRunEditPlan(repo.RepoPath, MidRunWrite.ModifyTargetGuardrail);

        (int exit, _) = await RunViaCliAsync("run", planDir, "--no-ui", "--no-log-server");

        // ── The observation is CREATED by this run (which is what makes the pin red on the stubs) ─
        IReadOnlyList<DecisionEntry> decisions = DecisionsOf(planDir);
        DecisionEntry observation = Assert.Single(decisions, d => d.Boundary == "plan-edit");
        Assert.Equal("observed", observation.Decision);

        // "…and nothing else": no other decision was recorded, so the outcome below is attributable to
        // the plan-edit observation alone rather than to some other boundary having stayed silent.
        Assert.DoesNotContain(decisions, d => d.Boundary != "plan-edit");

        // ── Inert on the OUTCOME: exit 0, not 5 ─────────────────────────────────────────────────
        // Asserted on the exit code, not on RunOutcomePolicy.SuppressesDelivery — the predicate is the
        // implementation of the claim, not the claim.
        Assert.Equal(ExitCodes.Success, exit);
        Assert.NotEqual(ExitCodes.ProceededUnreviewed, exit);

        // ── Inert on DELIVERY: the run still fast-forwards ───────────────────────────────────────
        DeliverySection? delivery = JournalOf(planDir).Delivery;
        Assert.NotNull(delivery);
        Assert.True(delivery!.Delivered,
            "a plan-edit observation must not suppress delivery; reason recorded: " + delivery.Reason);
        Assert.Equal(DeliveryOutcome.FastForwarded, delivery.Outcome);
        Assert.NotEqual(initialHead, repo.HeadSha());
        Assert.Equal(originalBranch, repo.CurrentBranch());
    }

    // ── P4 — quieter than the hash BY DESIGN (DECLARED EXEMPTION) ────────────────────────────────

    [Fact]
    public async Task AStrayDsStoreMidRun_EmitsNothingWhileTheDefinitionHashStillChanges()
    {
        using var repo = new TempGitRepo("gr-pedr-p4");
        string planDir = CreateMidRunEditPlan(repo.RepoPath, MidRunWrite.StrayDsStoreInTargetGuardrails);

        // The definition hash of the task that is about to gain a .DS_Store, as of the run's start.
        PlanLoadResult before = new PlanLoader().Load(planDir);
        Assert.False(before.HasErrors, string.Join("\n", before.Diagnostics));
        string hashAtStart = TaskDefinitionHash.Compute(before.Plan!.Tasks.Single(t => t.Id == Target));

        (RunReport report, RunJournal journal, RecordingObserver observer) =
            await RunWorktreeAsync(planDir, repo, TestContext.Current.CancellationToken);

        Assert.True(report.AllSucceeded,
            "a stray editor artifact must not fail the run; got " +
            string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));

        // The stray file really was created mid-run, under the WATCHED guardrails/ folder.
        Assert.True(
            File.Exists(Path.Combine(planDir, "tasks", Target, "guardrails", ".DS_Store")),
            "the fixture's mid-run write did not land — the two halves below would then be vacuous.");

        // ── Half 1: the watch is SILENT (the §5.2 ignore list, applied in the watch) ─────────────
        Assert.Empty(PlanEditEntries(journal));
        Assert.DoesNotContain(observer.Decisions, d => d.Boundary == "plan-edit");

        // ── Half 2: the same run's recorded TaskDefinitionHash STILL CHANGED ─────────────────────
        // HashText enumerates "*" and filters nothing, so the artifact IS part of the definition — and
        // must stay that way. Moving the ignore list into HashText would move every recorded definition
        // hash in every plan, and a moved definition hash is a drift HALT on the next resume.
        string? recorded = journal.RecordedDefinitionHash(Target);
        Assert.NotNull(recorded);
        Assert.NotEqual(hashAtStart, recorded);
    }

    // ── P5 — the rendered text carries all three §5.1 consequences ───────────────────────────────

    [Fact]
    public async Task TheRenderedText_CarriesAllThreeSection51Consequences()
    {
        using var repo = new TempGitRepo("gr-pedr-p5");
        string planDir = CreateMidRunEditPlan(repo.RepoPath, MidRunWrite.ModifyTargetGuardrail);

        (_, string output) = await RunViaCliAsync("run", planDir, "--no-ui", "--no-log-server");

        int start = output.IndexOf(RenderedHeadline, StringComparison.OrdinalIgnoreCase);
        Assert.True(start >= 0,
            $"the end-of-run report never rendered the plan-edit advisory ('{RenderedHeadline}'). Output:\n"
            + output);

        // Every assertion below is scoped to the advisory's own text, so a phrase appearing elsewhere in
        // the run output cannot stand in for a consequence the message failed to state.
        string advisory = output[start..];

        // The task and the file the operator actually edited.
        Assert.Contains(Target, advisory, StringComparison.Ordinal);
        Assert.Contains(GuardrailLabel, advisory.Replace('\\', '/'), StringComparison.Ordinal);

        // ── Consequence 1 — what the edit REACHES (§5.1 rows 1-2) ───────────────────────────────
        // Action prompts and guardrail scripts are re-read PER ATTEMPT, so the edit applies from the next
        // attempt onward. This is why "your edit was ignored" would be false.
        Assert.Contains("re-read", advisory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("attempt", advisory, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("your edit was ignored", advisory, StringComparison.OrdinalIgnoreCase);

        // ── Consequence 2 — what it does NOT reach (§5.1 row 3) ─────────────────────────────────
        // task.json (writeScope, dependsOn, retries, maxTurns) and the DAG were loaded at run start.
        Assert.Contains("task.json", advisory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("writeScope", advisory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DAG", advisory, StringComparison.Ordinal);

        // ── Consequence 3 — the POST-edit hash is recorded at settle (§5.1 row 4) ────────────────
        // So a later resume will NOT flag this as drift — the quiet false green #556 owns, which the
        // message must disclose rather than leave the operator to discover.
        Assert.Contains("post-edit", advisory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("resume", advisory, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("drift", advisory, StringComparison.OrdinalIgnoreCase);

        // Never a halt: the workflow this advisory exists to protect is "fix a defective guardrail while
        // the rest of the DAG runs" (§5.4).
        Assert.Contains("Nothing was halted", advisory, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Reading the two decision surfaces
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<DecisionEntry> PlanEditEntries(RunJournal journal) =>
        (journal.Document.Decisions ?? []).Where(d => d.Boundary == "plan-edit");

    /// <summary>Read <c>state/run.json</c> from disk WITHOUT the resume normalization a reload applies.</summary>
    private static JournalDocument JournalOf(string planDir) =>
        JournalReader.Read(RunJournal.PathFor(planDir));

    private static IReadOnlyList<DecisionEntry> DecisionsOf(string planDir) =>
        JournalOf(planDir).Decisions ?? [];

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Drivers
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The REAL <c>run</c> command in-process, over a per-invocation console (parallel-safe).</summary>
    private static async Task<(int ExitCode, string Output)> RunViaCliAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("plan-edit-during-run test root");
        root.Add(RunCommand.Create(io));
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    /// <summary>
    /// A worktree-mode run over a REAL <see cref="GitWorktreeProvider"/>, with a recording observer so the
    /// LIVE <c>DecisionRecorded</c> surface can be counted alongside the durable <c>decisions[]</c> one.
    /// </summary>
    private static async Task<(RunReport Report, RunJournal Journal, RecordingObserver Observer)> RunWorktreeAsync(
        string planDir, TempGitRepo repo, CancellationToken ct, WaveBreakdownInvoker? breakdownInvoker = null)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));
        PlanDefinition plan = load.Plan!;

        var stateManager = new StateManager(plan.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        var registry = PromptRunnerRegistry.Build(plan.Config,
            _ => throw new InvalidOperationException("no real prompt runner in these tests"));
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), plan.Config.Interpreters);
        var observer = new RecordingObserver();
        var executor = new TaskExecutor(
            plan, new ProcessRunner(), interpreterMap, stateManager, journal, observer, registry);

        var scheduler = new Scheduler(
            plan, executor, journal,
            worktreeProvider: new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot),
            observer: observer,
            reVerifier: new GuardrailReVerifier(new ProcessRunner(), interpreterMap),
            breakdownInvoker: breakdownInvoker);

        RunReport report = await scheduler.RunAsync(plan, ct);
        return (report, journal, observer);
    }

    private sealed class RecordingObserver : IRunObserver
    {
        public List<DecisionEntry> Decisions { get; } = [];

        public void DecisionRecorded(DecisionEntry entry)
        {
            lock (Decisions) { Decisions.Add(entry); }
        }

        public void TaskStarting(TaskNode task) { }

        public void TaskFinished(TaskResult result) { }

        public void GuardrailFinished(TaskNode task, GuardrailResult result) { }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Fixture 1 — a plain two-task plan whose FIRST task edits the SECOND task's definition mid-run
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    private enum MidRunWrite
    {
        /// <summary>Append a comment to the target task's guardrail script — a real operator edit.</summary>
        ModifyTargetGuardrail,

        /// <summary>Drop a stray <c>.DS_Store</c> into the target task's <c>guardrails/</c> folder.</summary>
        StrayDsStoreInTargetGuardrails
    }

    /// <summary>
    /// Build <c>&lt;repo&gt;/plan</c>: <c>workspace: ".."</c> + <c>maxParallelism: 2</c> (worktree mode),
    /// <c>mergeOnSuccess</c> OMITTED so delivery is ON by default (#340). <see cref="Editor"/> runs first
    /// and writes into the REAL plan folder by absolute path — the plan folder is untracked, so it is not
    /// in any segment worktree and this is genuinely an out-of-band write, exactly like an operator's
    /// editor. <see cref="Target"/> depends on it, so the write always lands before the target settles.
    /// </summary>
    private static string CreateMidRunEditPlan(string repoPath, MidRunWrite write)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));

        Write(Path.Combine(planDir, "guardrails.json"),
            """
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 0,
              "maxParallelism": 2
            }
            """);

        string targetGuardrailsDir = Path.Combine(planDir, "tasks", Target, "guardrails");
        string targetGuardrail = Path.Combine(targetGuardrailsDir, Script("01-check"));

        WriteScriptTask(
            Path.Combine(planDir, "tasks", Editor),
            "first.txt",
            dependsOn: null,
            midRunLine: MidRunLine(write, targetGuardrail, targetGuardrailsDir));

        WriteScriptTask(
            Path.Combine(planDir, "tasks", Target),
            "second.txt",
            dependsOn: Editor,
            midRunLine: null);

        return planDir;
    }

    /// <summary>The one line of the editor task's action that writes into the live plan folder.</summary>
    private static string MidRunLine(MidRunWrite write, string guardrailPath, string guardrailsDir) =>
        (write, Ps) switch
        {
            (MidRunWrite.ModifyTargetGuardrail, true) =>
                $"Add-Content -Path '{guardrailPath}' -Value '# operator edit during the run'",
            (MidRunWrite.ModifyTargetGuardrail, false) =>
                $"printf '%s\\n' '# operator edit during the run' >> '{guardrailPath}'",
            (MidRunWrite.StrayDsStoreInTargetGuardrails, true) =>
                $"Set-Content -NoNewline -Path '{Path.Combine(guardrailsDir, ".DS_Store")}' -Value 'mac finder junk'",
            _ =>
                $"printf '%s' 'mac finder junk' > '{Path.Combine(guardrailsDir, ".DS_Store")}'"
        };

    /// <summary>
    /// A green script task that writes <c>src/&lt;file&gt;</c> into its segment worktree (its whole
    /// <c>writeScope</c>) and optionally performs one extra out-of-band write into the live plan folder.
    /// </summary>
    private static void WriteScriptTask(string taskDir, string file, string? dependsOn, string? midRunLine)
    {
        string depends = dependsOn is null ? "[]" : $"[\"{dependsOn}\"]";
        Write(Path.Combine(taskDir, "task.json"),
            $$"""
            { "description": "{{Path.GetFileName(taskDir)}}", "writeScope": ["src/{{file}}"], "dependsOn": {{depends}} }
            """);

        string extra = midRunLine is null ? "" : midRunLine + "\n";
        string action = Ps
            ? "New-Item -ItemType Directory -Force -Path \"$env:GUARDRAILS_WORKSPACE\\src\" | Out-Null\n"
              + $"Set-Content -NoNewline -Path \"$env:GUARDRAILS_WORKSPACE\\src\\{file}\" -Value 'written'\n"
              + extra
              + "exit 0\n"
            : "#!/usr/bin/env bash\n"
              + "mkdir -p \"$GUARDRAILS_WORKSPACE/src\"\n"
              + $"printf '%s' 'written' > \"$GUARDRAILS_WORKSPACE/src/{file}\"\n"
              + extra
              + "exit 0\n";
        WriteExecutable(Path.Combine(taskDir, Script("action")), action);

        // `exit 0` is the FIRST statement after the catches declaration, so appending an operator's comment
        // to this file changes its bytes (and its definition hash) without changing its verdict.
        string gate = Ps
            ? $"# catches: src/{file} missing from the workspace\nexit 0\n"
            : $"#!/usr/bin/env bash\n# catches: src/{file} missing from the workspace\nexit 0\n";
        WriteExecutable(Path.Combine(taskDir, "guardrails", Script("01-check")), gate);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Fixture 2 — a WAVED plan that actually reaches a JIT breakdown checkpoint (P2)
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// wave-01 is an authored green script task; wave-02 is an EMPTY JIT stub carrying the opt-in
    /// <c>brief.md</c>, so an <c>auto</c> policy run auto-invokes the breakdown at the between-wave
    /// checkpoint. Mirrors the shipped <c>WaveBreakdownRunTests</c> / <c>WaveJitCheckpointRunTests</c>
    /// fixtures — a plain repo cannot produce a JIT checkpoint at all.
    /// </summary>
    private static string CreateWavedJitPlan(string repoPath)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));

        Write(Path.Combine(planDir, "guardrails.json"),
            """
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 0,
              "maxParallelism": 2,
              "autonomyPolicy": "auto"
            }
            """);

        WriteScriptTask(Path.Combine(planDir, Wave1, "tasks", "01-config"), "config.txt",
            dependsOn: null, midRunLine: null);

        Directory.CreateDirectory(Path.Combine(planDir, Wave2, "tasks")); // the empty JIT stub
        Write(Path.Combine(planDir, Wave2, "brief.md"),
            $"# {Wave2}\nBuild the compiled artifact from {Wave1}'s config.\n");

        return planDir;
    }

    /// <summary>
    /// A stub breakdown runner standing in for the Claude subprocess (NO real Claude call). It authors an
    /// INVALID task folder into its working directory — which the invoker sets to the PLAN directory, with
    /// <c>Write</c>/<c>Edit</c>/<c>Bash</c> at <c>acceptEdits</c> and no containment hook — so the
    /// deterministic validate gate rejects it and <c>BreakdownInventory.Revert</c> moves what it wrote to
    /// <c>rejected/</c>. Invalid = a COMPLETE folder by the sweep's predicate (task.json + a resolved
    /// action, so the incomplete-trailing sweep leaves it alone) but carrying NO <c>guardrails/</c>, which
    /// is a validation error — the revert path, not the sweep path, is what disposes of it.
    /// </summary>
    private sealed class InvalidWaveAuthoringRunner : IPromptRunner
    {
        public int Invocations { get; private set; }

        public string Name => "breakdown";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            Invocations++;

            string taskDir = Path.Combine(invocation.WorkingDirectory, Wave2, "tasks", "01-bad");
            Write(Path.Combine(taskDir, "task.json"),
                """{ "description": "bad - authored with no guardrails", "writeScope": [], "dependsOn": [] }""");
            WriteExecutable(Path.Combine(taskDir, Script("action")),
                Ps ? "exit 0\n" : "#!/usr/bin/env bash\nexit 0\n");
            // deliberately NO guardrails/ folder -> `guardrails validate` fails (zero guardrails).

            return Task.FromResult(new PromptResult
            {
                Completed = true,
                IsError = false,
                ResultText = "authored " + Wave2,
                CostUsd = 0.1m,
                Summary = "breakdown authored " + Wave2
            });
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // File helpers
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Write <paramref name="content"/>, re-creating a parent directory a prune may have removed.</summary>
    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void WriteExecutable(string path, string content)
    {
        Write(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Windows-safe temp git repo (issue #116)
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A throwaway git repo under the system temp root — never this task's own worktree, so nothing these
    /// tests write can reach the checkout hosting the run (#253). Teardown strips read-only attributes
    /// FIRST: git marks loose objects under <c>.git/objects</c> read-only on Windows and
    /// <see cref="Directory.Delete(string, bool)"/> then throws <see cref="UnauthorizedAccessException"/>
    /// — which is NOT an <see cref="IOException"/>, so the usual catch does not catch it.
    /// <c>core.autocrlf=false</c> keeps fixture content bytes (and therefore definition hashes) identical
    /// across platforms.
    /// </summary>
    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;

        public string RepoPath { get; }

        public string WorktreeRoot { get; }

        public TempGitRepo(string prefix)
        {
            _root = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            WorktreeRoot = Path.Combine(_root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            Git(RepoPath, "config", "core.autocrlf", "false");
            Write(Path.Combine(RepoPath, "README.md"), "# plan-edited-during-run test\n");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        public string CurrentBranch() => Git(RepoPath, "rev-parse", "--abbrev-ref", "HEAD").Trim();

        public string HeadSha() => Git(RepoPath, "rev-parse", "HEAD").Trim();

        public static string Git(string workingDir, params string[] args)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);
            using Process proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git {string.Join(" ", args)} (in {workingDir}) exited {proc.ExitCode}: {stderr.Trim()}");
            }

            return stdout;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    foreach (string f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(f, FileAttributes.Normal);
                    }

                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
                // best-effort teardown
            }
        }
    }
}
