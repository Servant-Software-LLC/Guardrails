using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Plan 32 (#556) §5.8 — <b>the executed-definition hash, on a REAL git segment</b>. The definition hash
/// stamped at settle is computed from the bytes on disk <b>at settle</b> rather than the bytes the attempt
/// <b>executed</b>, so a mid-run edit yields a silent false green no later resume can detect. The Core
/// suite pins that in SERIAL mode (write site <b>W1</b>); this file pins the half that mode cannot reach.
///
/// <para><b>Why integration, and why worktree mode.</b> §8: <i>"a design that proved this only in serial
/// mode would have proved it in the mode plan 28 did not use"</i> — the default for a real run is worktree
/// mode, whose settle is <c>Scheduler.SettleAsync</c> (<b>W2</b>, the deferred B1 settle) and which stamps
/// <b>two</b> durable surfaces from one value: the journal entry AND the integration commit's
/// <c>Guardrails-Task-Hash:</c> trailer (§4.2). #382's lesson is that a fake-masked unit guardrail
/// certifies green while the real composition-root path is broken, so every run below drives a real
/// <see cref="GitWorktreeProvider"/> over a real repository.</para>
///
/// <list type="bullet">
///   <item><b>P2</b> — <see cref="TheRecordedHash_IsThePreEditPin_WhenTaskJsonIsEditedMidRun_Worktree"/>:
///     the issue's own pin, asserted in worktree mode against BOTH surfaces W2 stamps. §5.8: <i>"without
///     this, an implementation that fixes <c>AttemptJournaler.cs</c> alone passes the issue's own pin while
///     leaving the default execution mode broken."</i> That implementation is exactly what the tree carries
///     right now, which is why this one is <b>RED</b> and the other three are not.</item>
///   <item><b>P3</b> — <see cref="TheTrailerAgreesWithTheJournal_OnARealGitSegment"/>: the trailer equals
///     the journal, on a real segment. DECLARED EXEMPTION.</item>
///   <item><b>P6a</b> — <see cref="TheDriftPrePass_SeesThePostEditHash_WithoutAReload"/>: the resume drift
///     pre-pass recomputes from CURRENT DISK. DECLARED EXEMPTION.</item>
///   <item><b>P6b</b> — <see cref="AnEarlierRunsSettledTask_StillHaltsOnDrift_WhenEditedAfterThisRunsLoad"/>:
///     the same property in the reachable production shape — waved, two runs. DECLARED EXEMPTION.</item>
/// </list>
///
/// <para><b>THREE DECLARED EXEMPTIONS, and the reason is structural rather than convenient.</b> P3, P6a and
/// P6b assert properties that are true today and must STAY true — the "nothing else moved" half of
/// milestone A, not defect pins. P3: today the trailer and the journal are stamped from the same
/// settle-time recompute so they already agree; after the fix both come from the same pin, so they still
/// agree — a CORRECT test is GREEN on both sides, and demanding red would demand a correct implementation
/// fail. P6a/P6b: the READ sites recompute from disk today and must KEEP doing so. §11: <i>"No task may pin
/// the READ sites. Pinning R1 would make P1 pass and silence definition drift entirely — a strictly worse
/// product than today."</i> These two are what make that implementation fail. They stay in the file rather
/// than being dropped, because a dropped row and an oversight look identical from the outside.</para>
///
/// <para><b>P6 was RESPECIFIED because the obvious form is a tautology.</b> An earlier draft asked for
/// <i>"after a between-runs edit, the resume still halts with DefinitionDrift"</i> — which passes with the
/// read sites fully pinned: a between-runs edit is on disk BEFORE run N+1's load, so the pin computed at
/// that load already equals the post-edit bytes, the pre-pass mismatches against the RECORDED hash either
/// way, and the substitution is unobservable. Both replacements therefore edit strictly <b>after</b> this
/// run's load, which is the only sequencing that separates a pinned read site from a disk one at all.
/// P6b's own earlier form (drift on an EARLIER WAVE's settled task within one run) was unsatisfiable:
/// <c>DrainAsync</c> is called per wave with that wave's tasks only and <c>DetectDefinitionDrift</c>
/// iterates exactly that list, so nothing re-checks an earlier wave within one run.</para>
///
/// <para><b>Sequencing the mid-run edit deterministically.</b> P2's and P3's edit must land after the plan
/// loads and before the target settles, which is a timing problem. Plan 31 already shipped the answer and
/// this file reuses the mechanism rather than inventing a second one (§8): the FIRST task's action writes
/// into the SECOND task's folder by absolute path, so the edit is sequenced by the <b>DAG</b> rather than
/// by a timer, exactly as <c>PlanEditedDuringRunTests.CreateMidRunEditPlan</c> does. The edit IS the
/// fixture (§11): it is never made conditional, retimed or removed.</para>
///
/// <para><b>Why no pin here reads <c>report.AllSucceeded</c>.</b> Milestone C adds a settle-time
/// divergence gate whose whole purpose is to stop a run carrying a mid-run definition edit from
/// delivering, while preserving the settle unconditionally (§6.4: "record the success, block the
/// delivery"). P2's and P3's runs are therefore EXPECTED to lose <c>AllSucceeded</c> once that lands. Both
/// take their positive control from the durable surface that is stable across every milestone instead: the
/// task's journal entry reads <c>succeeded</c>.</para>
/// </summary>
public sealed class MidRunDefinitionEditTests : IClassFixture<HostRepoCleanlinessGuard>
{
    /// <summary>P2/P3's first task: its action edits <see cref="Target"/>'s <c>task.json</c> mid-run.</summary>
    private const string Editor = "01-edit";

    /// <summary>P2/P3's second task: the one whose definition moves under it while it is in flight.</summary>
    private const string Target = "02-target";

    /// <summary>P6a's flat plan: the already-succeeded task whose definition is edited after the load.</summary>
    private const string First = "01-first";

    private const string Second = "02-second";

    private const string Wave1 = "wave-01-scaffold";

    private const string Wave2 = "wave-02-build";

    /// <summary>P6b's wave-N task — settled green in run 1, edited after run 2's load. Ids are wave-qualified.</summary>
    private const string WavedTarget = Wave2 + "/02-target";

    /// <summary>P6b's same-wave blocker: it always fails its gate, so wave-02 never records COMPLETE.</summary>
    private const string WavedBlocker = Wave2 + "/03-blocker";

    /// <summary>The description P2/P3's mid-run edit writes into the target's <c>task.json</c>.</summary>
    private const string EditedMidRun = "edited mid-run";

    /// <summary>The description P6a/P6b's post-load edit writes into the target's <c>task.json</c>.</summary>
    private const string EditedAfterLoad = "edited after this run's load";

    private static readonly bool Ps = OperatingSystem.IsWindows();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── P2 — the recorded hash is the PRE-EDIT pin, in WORKTREE mode (W2/W3) ─────────────────────

    /// <summary>
    /// §5.8's acceptance criterion in the mode a real run actually uses: a task whose <c>task.json</c> is
    /// modified on disk AFTER the run loads it and BEFORE it settles must not record a <c>succeeded</c>
    /// whose stored <c>definitionHash</c> equals the post-edit bytes. The PRE-EDIT hash is recorded, so the
    /// next resume flags drift.
    ///
    /// <para><b>Both durable surfaces are asserted, and that is the point of putting this in worktree
    /// mode.</b> W2 (<c>Scheduler.SettleAsync</c>) computes ONE value and stamps it onto BOTH the journal
    /// entry and the integration commit's <c>Guardrails-Task-Hash:</c> trailer, so a pin asserted only on
    /// the journal cannot see half of what W2 writes. W3 (<c>SettleGreenIfWorktreeAsync</c>'s non-deferred
    /// branch) stamps the trailer ALONE and leaves the journal to W1 — and it is <b>not reachable behind a
    /// real provider</b>: <c>TaskExecutor.cs:1325</c> routes every success whose segment is a real directory
    /// to <c>ValidateFragmentForSettle</c>, which sets <c>DeferredSettle = true</c>, so a real git segment
    /// always takes W2. W3 is therefore covered by §4.3's one-rule requirement (and by §9's call-site
    /// anchor), never by this run — which is exactly why the trailer is asserted here rather than assumed to
    /// follow from the journal.</para>
    ///
    /// <para><b>RED on this tree, and for the right reason.</b> The serial write sites are already pinned
    /// and the worktree ones are not, so today's settle recomputes from current disk and records the
    /// POST-edit value — exactly inverted from what is asserted below. The assertion is an EQUALITY against
    /// a value captured BEFORE the edit; "the hash is non-null" and "the hash changed" are both true with
    /// the defect fully intact and would pin nothing.</para>
    /// </summary>
    [Fact]
    public async Task TheRecordedHash_IsThePreEditPin_WhenTaskJsonIsEditedMidRun_Worktree()
    {
        using var repo = new TempGitRepo("gr32-mrde-p2");
        string planDir = CreateMidRunEditPlan(repo.RepoPath);
        string targetTaskJson = Path.Combine(planDir, "tasks", Target, "task.json");

        PlanDefinition plan = Load(planDir);
        TaskNode target = plan.Tasks.Single(t => t.Id == Target);

        // The definition the harness LOADED and is therefore about to EXECUTE, captured before the run.
        string hashBefore = TaskDefinitionHash.Compute(target);

        (RunReport report, RunJournal journal) = await RunWorktreeAsync(plan, repo);

        // ── positive controls: the scenario really happened ─────────────────────────────────────
        AssertSettledSucceeded(journal, report, Target);
        Assert.Contains(EditedMidRun, File.ReadAllText(targetTaskJson), StringComparison.Ordinal);

        // The same node, re-hashed from CURRENT disk: this is what today's settle stamps.
        string hashAfterEdit = TaskDefinitionHash.Compute(target);
        Assert.NotEqual(hashBefore, hashAfterEdit);

        // ── the pin, surface 1: the journal's executed-definition record ────────────────────────
        string? recorded = journal.RecordedDefinitionHash(Target);
        Assert.Equal(hashBefore, recorded);
        Assert.NotEqual(hashAfterEdit, recorded);

        // ── the pin, surface 2: the Guardrails-Task-Hash: trailer on the real integration commit ─
        IReadOnlyDictionary<string, string> trailers = TaskHashTrailers(repo, planDir);
        Assert.True(trailers.TryGetValue(Target, out string? trailer),
            $"'{Target}' has no Guardrails-Task-Hash: trailer on the plan branch, so the trailer half of " +
            "the worktree write surface was never reached; trailers found: " +
            string.Join(", ", trailers.Keys));
        Assert.Equal(hashBefore, trailer);
        Assert.NotEqual(hashAfterEdit, trailer);
    }

    // ── P3 — the trailer agrees with the journal (DECLARED EXEMPTION: green today, must STAY green) ──

    /// <summary>
    /// §5.8's P3: the <c>Guardrails-Task-Hash:</c> trailer on a task's integration commit equals the hash
    /// the journal recorded at that same settle, asserted on a REAL git segment. This is what keeps Part
    /// C's rule-3 corroboration sound — the safe-suffix rewind refuses to remove a commit whose trailer the
    /// journal never recorded (<c>SafeSuffixEvaluator.cs:163-165</c>), so a pin that reached one surface and
    /// not the other would turn every legitimate settle into an uncorroborated commit and make the
    /// remediation path strictly worse than the bug.
    ///
    /// <para><b>DECLARED EXEMPTION from the red census.</b> Today both values come from the single
    /// settle-time recompute in <c>Scheduler.SettleAsync</c>, so they already agree; after the fix both come
    /// from the same pin, so they still agree. A CORRECT test is GREEN on today's tree and demanding red
    /// would demand a correct implementation fail. Its job is to stay green ACROSS the change.</para>
    ///
    /// <para><b>Asserted on the edited fixture on purpose.</b> A quiet run cannot tell "one shared value"
    /// from "two values that happen to match", because with nothing moving under the run every plausible
    /// implementation agrees. Running this on the fixture whose definition DOES move mid-run makes the two
    /// surfaces genuinely capable of disagreeing — which is the only way this row can fail an implementation
    /// that pins one of them and forgets the other.</para>
    /// </summary>
    [Fact]
    public async Task TheTrailerAgreesWithTheJournal_OnARealGitSegment()
    {
        using var repo = new TempGitRepo("gr32-mrde-p3");
        string planDir = CreateMidRunEditPlan(repo.RepoPath);

        PlanDefinition plan = Load(planDir);
        (RunReport report, RunJournal journal) = await RunWorktreeAsync(plan, repo);

        // ── positive controls: two real segments settled, and the definition really did move ─────
        AssertSettledSucceeded(journal, report, Editor);
        AssertSettledSucceeded(journal, report, Target);
        Assert.Contains(EditedMidRun,
            File.ReadAllText(Path.Combine(planDir, "tasks", Target, "task.json")), StringComparison.Ordinal);

        IReadOnlyDictionary<string, string> recorded = journal.RecordedDefinitionHashes();
        Assert.Equal(2, recorded.Count);

        IReadOnlyDictionary<string, string> trailers = TaskHashTrailers(repo, planDir);

        // ── the pin: the two surfaces carry the SAME set of tasks and the SAME hash for each ─────
        // Asserted over the WHOLE recorded set rather than one task: a per-task spot check is satisfied by
        // an implementation that stamps the trailer for the first task it settles and stops.
        Assert.Equal(recorded.Count, trailers.Count);
        foreach (KeyValuePair<string, string> pair in recorded)
        {
            Assert.True(trailers.TryGetValue(pair.Key, out string? trailer),
                $"the journal recorded a definition hash for '{pair.Key}' but its integration commit " +
                "carries no Guardrails-Task-Hash: trailer — Part C rule 3 cannot corroborate it; trailers " +
                "found: " + string.Join(", ", trailers.Keys));
            Assert.Equal(pair.Value, trailer);
        }
    }

    // ── P6a — the drift pre-pass reads DISK (DECLARED EXEMPTION: green today, must STAY green) ───

    /// <summary>
    /// §5.8's P6a, the respecified form. Load a plan, capture the pin, mutate <c>task.json</c> on disk, then
    /// invoke the drift pre-pass <b>without re-loading</b>: it must see the <b>post-edit</b> hash. This is a
    /// direct assertion that the READ site recomputes, and it is the only form that separates a pinned read
    /// site from a disk one at all — the edit lands strictly BETWEEN this run's load and its drain, which is
    /// the one window in which the pin and the disk bytes differ.
    ///
    /// <para><b>DECLARED EXEMPTION from the red census.</b> §11: <i>"No task may pin the READ sites. Pinning
    /// R1 would make P1 pass and silence definition drift entirely — a strictly worse product than
    /// today."</i> This is the pin that makes that implementation fail, so it is green before the change and
    /// required to be green after it.</para>
    ///
    /// <para>The plan object from this run's load is passed to the Scheduler directly — never re-loaded —
    /// because a re-load would recapture the pin from the post-edit bytes and destroy the very distinction
    /// under test.</para>
    /// </summary>
    [Fact]
    public async Task TheDriftPrePass_SeesThePostEditHash_WithoutAReload()
    {
        using var repo = new TempGitRepo("gr32-mrde-p6a");
        string planDir = CreateLinearPlan(repo.RepoPath);
        // Commit the plan folder so the old definition bytes stay recoverable at the task's commit — the
        // dogfood shape #274 was reported on, and what lets the drift report degrade gracefully rather than
        // by accident.
        repo.CommitAll("add plan");
        string firstTaskJson = Path.Combine(planDir, "tasks", First, "task.json");

        // ── run 1: a clean, unedited run. Its settle is what the pre-pass compares against ───────
        (RunReport run1, RunJournal journal1) = await RunWorktreeAsync(Load(planDir), repo);
        Assert.True(run1.AllSucceeded,
            "run 1 carries no edit at all and must be wholly green; outcomes: " +
            string.Join(", ", run1.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));
        Assert.Null(run1.DefinitionDrift);

        // ── THIS RUN'S LOAD. Nothing below re-loads the plan ────────────────────────────────────
        PlanDefinition plan2 = Load(planDir);
        TaskNode first = plan2.Tasks.Single(t => t.Id == First);

        string pinAtLoad = TaskDefinitionHash.Compute(first);
        Assert.Equal(journal1.RecordedDefinitionHash(First), pinAtLoad);

        // ── the operator's edit, AFTER the load ─────────────────────────────────────────────────
        File.WriteAllText(firstTaskJson, TaskJson(EditedAfterLoad, "src/" + First + ".txt", dependsOn: null));
        string postEdit = TaskDefinitionHash.Compute(first);
        Assert.NotEqual(pinAtLoad, postEdit);

        (RunReport run2, _) = await RunWorktreeAsync(plan2, repo);

        // ── the pin: the pre-pass saw CURRENT DISK, not the pin the load captured ────────────────
        // A pinned read site computes pinAtLoad here, matches the recorded hash, reports NO drift at all,
        // and this run goes quietly green over a definition nobody executed.
        Assert.NotNull(run2.DefinitionDrift);
        DriftedTask drifted = Assert.Single(run2.DefinitionDrift!.Tasks);
        Assert.Equal(First, drifted.TaskId);
        Assert.Equal(pinAtLoad, drifted.OldHash);
        Assert.Equal(postEdit, drifted.NewHash);
    }

    // ── P6b — the same property, waved and two-run (DECLARED EXEMPTION) ──────────────────────────

    /// <summary>
    /// §5.8's P6b: the reachable production shape of P6a. A task in <b>wave N</b>, settled green in a
    /// PREVIOUS run, whose definition is edited after THIS run's load and before wave N's drain. Its pin and
    /// its recorded hash are both the pre-edit value, so a pinned read site sees a match and waves it
    /// through, while a disk read halts.
    ///
    /// <para><b>Why the wave must be INCOMPLETE, and why that is not a contrivance.</b> A COMPLETE wave is
    /// collapsed at the top of the wave loop and never drains, so its tasks never reach
    /// <c>DetectDefinitionDrift</c> at all (a completed wave is guarded by the wave-level drift check
    /// instead, §14.6 — a different mechanism with a different halt). The fixture therefore gives wave-02 a
    /// second task that always fails its gate: the target settles green and is durably on the plan branch,
    /// the wave halts at the hard barrier, and run 2 genuinely re-drains wave-02 — which is exactly the
    /// state an operator resumes into after a wave halts overnight.</para>
    ///
    /// <para><b>DECLARED EXEMPTION from the red census</b>, same structural reason as P6a: green before,
    /// required green after.</para>
    /// </summary>
    [Fact]
    public async Task AnEarlierRunsSettledTask_StillHaltsOnDrift_WhenEditedAfterThisRunsLoad()
    {
        using var repo = new TempGitRepo("gr32-mrde-p6b");
        string planDir = CreateWavedPlan(repo.RepoPath);
        repo.CommitAll("add plan");
        string targetTaskJson = Path.Combine(planDir, Wave2, "tasks", "02-target", "task.json");

        // ── run 1: wave-01 completes; wave-02's target settles GREEN, its blocker fails ──────────
        (RunReport run1, RunJournal journal1) = await RunWorktreeAsync(Load(planDir), repo);
        Assert.False(run1.AllSucceeded,
            "wave-02's blocker must fail so the wave never records COMPLETE; outcomes: " +
            string.Join(", ", run1.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));
        AssertSettledSucceeded(journal1, run1, WavedTarget);
        Assert.Equal(WaveStatus.Completed, journal1.WaveEntryOf(Wave1)!.Status);
        Assert.NotEqual(WaveStatus.Completed, journal1.WaveEntryOf(Wave2)!.Status);

        // ── THIS RUN'S LOAD. Nothing below re-loads the plan ────────────────────────────────────
        PlanDefinition plan2 = Load(planDir);
        TaskNode target = plan2.Tasks.Single(t => t.Id == WavedTarget);

        string pinAtLoad = TaskDefinitionHash.Compute(target);
        Assert.Equal(journal1.RecordedDefinitionHash(WavedTarget), pinAtLoad);

        // ── the operator's edit, AFTER this run's load and before wave-02's drain ────────────────
        File.WriteAllText(targetTaskJson, TaskJson(EditedAfterLoad, "src/target.txt", dependsOn: null));
        string postEdit = TaskDefinitionHash.Compute(target);
        Assert.NotEqual(pinAtLoad, postEdit);

        (RunReport run2, _) = await RunWorktreeAsync(plan2, repo);

        // ── the pin: wave-02's drain re-read the definition from CURRENT DISK and HALTED ─────────
        // Scoped to the TASK-level drift report: a wave-level halt would be a different mechanism
        // answering, and this row is about the task pre-pass inside DrainAsync.
        Assert.Null(run2.WaveHalt);
        Assert.NotNull(run2.DefinitionDrift);
        DriftedTask drifted = Assert.Single(run2.DefinitionDrift!.Tasks);
        Assert.Equal(WavedTarget, drifted.TaskId);
        Assert.Equal(pinAtLoad, drifted.OldHash);
        Assert.Equal(postEdit, drifted.NewHash);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Drivers
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    private static PlanDefinition Load(string planDir)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));
        Assert.NotNull(load.Plan);
        return load.Plan!;
    }

    /// <summary>
    /// A WORKTREE-mode run over a REAL <see cref="GitWorktreeProvider"/> — <c>maxParallelism: 2</c> plus a
    /// real provider, so every green result takes the B1 DEFERRED settle (<c>Scheduler.SettleAsync</c>,
    /// write site W2), which is the settle a real run uses.
    /// <para>The <see cref="PlanDefinition"/> is passed IN rather than re-loaded, because the whole point is
    /// that the run executes the definition the caller already hashed: re-loading here would re-read
    /// <c>task.json</c> and destroy the load-vs-settle distinction under test.</para>
    /// </summary>
    private static async Task<(RunReport Report, RunJournal Journal)> RunWorktreeAsync(
        PlanDefinition plan, TempGitRepo repo)
    {
        var stateManager = new StateManager(plan.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        var registry = PromptRunnerRegistry.Build(plan.Config,
            _ => throw new InvalidOperationException("every fixture action here is a script"));
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), plan.Config.Interpreters);
        var executor = new TaskExecutor(
            plan, new ProcessRunner(), interpreterMap, stateManager, journal, IRunObserver.Null, registry);

        var scheduler = new Scheduler(
            plan, executor, journal,
            worktreeProvider: new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot),
            reVerifier: new GuardrailReVerifier(new ProcessRunner(), interpreterMap));

        RunReport report = await scheduler.RunAsync(plan, Ct);
        return (report, journal);
    }

    /// <summary>
    /// The positive control shared by P2 and P3: the task reached a SUCCESSFUL SETTLE, so there is a
    /// recorded definition hash for the pin to be about.
    /// <para>Deliberately asserted on the JOURNAL entry rather than on <c>report.AllSucceeded</c>: milestone
    /// C blocks DELIVERY on a run carrying a mid-run definition edit while preserving the settle itself
    /// (§6.4), so <c>AllSucceeded</c> is expected to go false on these runs while <c>status: succeeded</c>
    /// is required to stay.</para>
    /// </summary>
    private static void AssertSettledSucceeded(RunJournal journal, RunReport report, string taskId)
    {
        Assert.True(journal.Document.Tasks.TryGetValue(taskId, out TaskJournalEntry? entry),
            $"'{taskId}' has no journal entry at all; outcomes: " +
            string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));

        Assert.True(entry!.Status == JournalTaskStatus.Succeeded,
            $"'{taskId}' must have SETTLED for its recorded definition hash to mean anything, but its " +
            $"journal status is '{entry.Status}'; outcomes: " +
            string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // The plan branch's Guardrails-Task-Hash: trailers, parsed independently of production
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The literal that separates one commit's message from the next in the log below.</summary>
    private const string CommitMarker = "@@gr-commit@@";

    /// <summary>
    /// <c>task id → Guardrails-Task-Hash:</c> read straight off the plan branch's commit messages. Parsed
    /// here rather than through <c>GitWorktreeProvider.ReconcileFromPlanBranch</c> on purpose: asking
    /// production to read back what production wrote is an echo, and P3's whole claim is that the bytes on
    /// the branch corroborate the bytes in the journal.
    /// <para><c>git log</c> is newest-first and the MOST RECENT integration per task wins, mirroring
    /// <c>GitWorktreeProvider.cs:864</c> — a task re-integrated by a later run must not be read at its stale
    /// first commit.</para>
    /// </summary>
    private static IReadOnlyDictionary<string, string> TaskHashTrailers(TempGitRepo repo, string planDir)
    {
        string log = TempGitRepo.Git(repo.RepoPath,
            "log", "--format=" + CommitMarker + "%n%B", "guardrails/" + Path.GetFileName(planDir));

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string message in log.Split(CommitMarker, StringSplitOptions.RemoveEmptyEntries))
        {
            string? taskId = null;
            string? hash = null;
            foreach (string raw in message.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("Guardrails-Task: ", StringComparison.Ordinal))
                {
                    taskId = line["Guardrails-Task: ".Length..];
                }
                else if (line.StartsWith("Guardrails-Task-Hash: ", StringComparison.Ordinal))
                {
                    hash = line["Guardrails-Task-Hash: ".Length..];
                }
            }

            if (taskId is not null && hash is not null && !map.ContainsKey(taskId))
            {
                map[taskId] = hash;
            }
        }

        return map;
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Fixture 1 (P2, P3) — a flat plan whose FIRST task edits the SECOND task's task.json mid-run
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build <c>&lt;repo&gt;/plan</c>: <c>workspace: ".."</c> + <c>maxParallelism: 2</c> (worktree mode).
    /// <see cref="Editor"/> runs first and overwrites <see cref="Target"/>'s <c>task.json</c> in the REAL
    /// plan folder by absolute path — the plan folder is untracked here, so it is in no segment worktree and
    /// this is genuinely an out-of-band write, exactly like an operator's editor. <see cref="Target"/>
    /// depends on it, so the DAG (not a timer) guarantees the write lands before the target settles.
    /// </summary>
    private static string CreateMidRunEditPlan(string repoPath)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        Write(Path.Combine(planDir, "guardrails.json"), Config);

        string targetTaskJson = Path.Combine(planDir, "tasks", Target, "task.json");

        WriteScriptTask(Path.Combine(planDir, "tasks", Editor), "first.txt", dependsOn: null,
            actionExtra: OverwriteFileLine(
                targetTaskJson, TaskJson(EditedMidRun, "src/second.txt", dependsOn: Editor)));
        WriteScriptTask(Path.Combine(planDir, "tasks", Target), "second.txt", dependsOn: Editor);

        return planDir;
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Fixture 2 (P6a) — a plain flat plan, run clean once and edited after the second run's load
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    private static string CreateLinearPlan(string repoPath)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        Write(Path.Combine(planDir, "guardrails.json"), Config);

        WriteScriptTask(Path.Combine(planDir, "tasks", First), First + ".txt", dependsOn: null);
        WriteScriptTask(Path.Combine(planDir, "tasks", Second), Second + ".txt", dependsOn: First);

        return planDir;
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Fixture 3 (P6b) — a WAVED plan whose wave-02 settles one task green and then halts
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// wave-01 is a single green task, so it COMPLETES in run 1 and is collapsed on resume. wave-02 holds
    /// the target (green — it settles and lands on the plan branch) plus a blocker that depends on it and
    /// always fails its gate, so wave-02 halts at the hard barrier and is NOT recorded complete. That is
    /// what makes run 2 re-drain wave-02, which is where the task-level drift pre-pass lives.
    /// </summary>
    private static string CreateWavedPlan(string repoPath)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        Write(Path.Combine(planDir, "guardrails.json"), Config);

        WriteScriptTask(Path.Combine(planDir, Wave1, "tasks", "01-scaffold"), "config.txt", dependsOn: null);
        WriteScriptTask(Path.Combine(planDir, Wave2, "tasks", "02-target"), "target.txt", dependsOn: null);
        WriteScriptTask(Path.Combine(planDir, Wave2, "tasks", "03-blocker"), "blocker.txt",
            dependsOn: "02-target", gatePasses: false);

        return planDir;
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Fixture building blocks
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>maxParallelism: 2</c> is what puts every run in this file into worktree mode; <c>mergeOnSuccess</c>
    /// is OMITTED so delivery stays ON by default (#340) and nothing here is proven in a mode a real run
    /// does not use.
    /// </summary>
    private const string Config =
        """
        {
          "version": 1,
          "guardrailMode": "failFast",
          "workspace": "..",
          "defaultRetries": 0,
          "maxParallelism": 2
        }
        """;

    private static string TaskJson(string description, string writeScope, string? dependsOn)
    {
        string depends = dependsOn is null ? "[]" : $"[\"{dependsOn}\"]";
        return $$"""{ "description": "{{description}}", "writeScope": ["{{writeScope}}"], "dependsOn": {{depends}} }""";
    }

    /// <summary>
    /// A script task that writes <c>src/&lt;file&gt;</c> into its segment worktree (its whole
    /// <c>writeScope</c>). <paramref name="actionExtra"/> is one extra line the action runs before
    /// <c>exit 0</c> — the out-of-band write that sequences a mid-run edit by the DAG.
    /// <paramref name="gatePasses"/> false gives the task a gate that can never pass.
    /// </summary>
    private static void WriteScriptTask(
        string taskDir, string file, string? dependsOn, string? actionExtra = null, bool gatePasses = true)
    {
        Write(Path.Combine(taskDir, "task.json"),
            TaskJson(Path.GetFileName(taskDir), "src/" + file, dependsOn));

        string extra = actionExtra is null ? "" : actionExtra + "\n";
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

        WriteExecutable(Path.Combine(taskDir, "guardrails", Script("01-check")),
            Shebang
            + $"# catches: src/{file} missing from the workspace\n"
            + (gatePasses ? "exit 0\n" : "exit 1\n"));
    }

    /// <summary>One line that overwrites <paramref name="path"/> with <paramref name="content"/>.</summary>
    private static string OverwriteFileLine(string path, string content) => Ps
        ? $"Set-Content -NoNewline -Path '{path}' -Value '{content}'"
        : $"printf '%s' '{content}' > '{path}'";

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // File helpers
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    private static string Script(string stem) => Ps ? stem + ".ps1" : stem + ".sh";

    private static string Shebang => Ps ? "" : "#!/usr/bin/env bash\n";

    /// <summary>Write <paramref name="content"/>, creating the parent directory as needed.</summary>
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
    /// <para>Copy-pasted rather than shared: <c>TempGitRepo</c> is a private nested helper in ~32 files of
    /// this project (this is the house style), and extracting one is a different change touching files
    /// outside this task's write scope.</para>
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
            Write(Path.Combine(RepoPath, "README.md"), "# mid-run-definition-edit test\n");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        public void CommitAll(string message)
        {
            Git(RepoPath, "add", "-A");
            Git(RepoPath, "commit", "-m", message);
        }

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
