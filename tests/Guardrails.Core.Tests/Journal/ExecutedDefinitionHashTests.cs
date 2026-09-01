using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

// Deliberately NOT nested as `Guardrails.Core.Tests.Journal`, despite this file living under Journal/ —
// exactly as the sibling `Journal/JudgeSpendRecordingTests.cs:9-14` already records. Declaring that
// nested namespace ANYWHERE in this assembly shadows the production `Guardrails.Core.Journal` namespace
// for every unqualified `Journal.X` reference elsewhere in `Guardrails.Core.Tests` (C# resolves a member
// of the enclosing namespace before a `using`-imported one), and OverwatchNoVerdictTests.cs:355's
// `Journal.TaskStatus.Running` then fails to compile — a file outside this task's write scope to fix.
namespace Guardrails.Core.Tests;

/// <summary>
/// Plan 32 (#556) — <b>the executed-definition hash</b>. The definition hash stamped into the journal at
/// settle is computed from the bytes <b>on disk at settle</b>, not the bytes the attempt <b>executed</b>,
/// so a mid-run <c>task.json</c> edit yields a silent false green that no later resume can detect: the
/// harness certifies the old definition, records the new one, and every downstream mechanism that trusts
/// the record agrees nothing is wrong.
///
/// <para><b>Serial / shared-workspace mode only.</b> Every run below is <c>maxParallelism: 1</c> with NO
/// worktree provider, which is the mode write site <b>W1</b>
/// (<c>AttemptJournaler.CompleteSucceededOrInvalidFragment</c>, <c>AttemptJournaler.cs:91</c>) governs.
/// The worktree-mode halves (W2/W3) are a later stage's, on a real git segment — §8: a design that proved
/// this only in serial mode would have proved it in the mode plan 28 did not use.</para>
///
/// <para><b>No assertion here names an API member this plan has not written yet</b> (§15 row 1). Each pin
/// is stated on an OBSERVABLE artifact — the journal's recorded <c>definitionHash</c>, and the output of
/// the already-public <see cref="TaskDefinitionHash.Compute(TaskNode)"/>. That is deliberate: it is what
/// lets this file compile against today's assemblies and fail for the right reason with no stub stage in
/// front of it, and it is what lets the implementation stages legitimately carry no <c>tests/**</c> path.</para>
///
/// <para><b>Sequencing the mid-run edit.</b> The edit must land after the plan loads and before the task
/// settles, which is a timing problem. It is solved the way
/// <c>Guardrails.Integration.Tests/PlanEditedDuringRunTests.CreateMidRunEditPlan</c> solves it — by the
/// DAG rather than by a timer. P1's edit is performed by a FIRST task's action writing into a SECOND
/// task's folder; P14's is performed by the task's own guardrail script on its first (failing) invocation,
/// so the edit is sequenced between attempt 1 and attempt 2. The edit IS the fixture: it is never made
/// conditional, retimed or removed.</para>
///
/// <para><b>TDD red, and two DECLARED EXEMPTIONS.</b>
/// <see cref="TheRecordedHash_IsThePreEditPin_WhenTaskJsonIsEditedMidRun_Serial"/> (P1) and
/// <see cref="TheRecordedHash_IsTheRunStartValue_WhenTaskJsonIsEditedBetweenAttempts"/> (P14) FAIL on
/// today's tree — today the settle stamps the post-edit disk hash, which is the defect.
/// <see cref="AnUneditedRun_RecordsAHashIdenticalToAPostRunRecompute"/> (P5, §5.5's no-op property) and
/// <see cref="TaskDefinitionHashCompute_OutputHasNotMoved_OnAPinnedFixtureFolder"/> (P8, §5.8's byte-pin)
/// assert properties that are true today and must STAY true, so a correct test is GREEN on today's tree
/// and demanding red would demand a correct implementation fail. They are REGRESSION pins, not defect
/// pins, and their job is to stay green: P5 is what proves no definition-hash migration wave is owed, and
/// P8 is the tripwire on any later "simplification" of the hashed file set or its framing.</para>
///
/// <para><b>Why no run here asserts <c>report.AllSucceeded</c> except P5's.</b> A later milestone adds a
/// settle-time divergence gate whose whole purpose is to make a run carrying a mid-run definition edit
/// stop delivering — so P1's and P14's runs are EXPECTED to lose <c>AllSucceeded</c> once that lands,
/// while the settle itself is preserved unconditionally ("record the success, block the delivery"). Both
/// therefore take their positive control from the DURABLE surface that is stable across every milestone:
/// the task's journal entry reads <c>succeeded</c>. P5's run carries no edit at all, so its
/// <c>AllSucceeded</c> is stable and is asserted.</para>
/// </summary>
public sealed class ExecutedDefinitionHashTests : IDisposable
{
    /// <summary>P1's first task: its action edits <see cref="Target"/>'s <c>task.json</c> mid-run.</summary>
    private const string Editor = "01-editor";

    /// <summary>P1's second task: the one whose definition moves under it while it is in flight.</summary>
    private const string Target = "02-target";

    /// <summary>The single-task id used by P5 and P14.</summary>
    private const string Solo = "01-solo";

    /// <summary>The description P1's mid-run edit writes into the target's <c>task.json</c>.</summary>
    private const string EditedMidRun = "edited mid-run";

    /// <summary>The description P14's between-attempts edit writes into the task's <c>task.json</c>.</summary>
    private const string EditedBetweenAttempts = "edited between attempts";

    /// <summary>
    /// P8's byte-pin: <see cref="TaskDefinitionHash.Compute(TaskNode)"/> over
    /// <see cref="WritePinnedFixture"/>'s fixed definition surface. Read off the failing assertion once
    /// and written down here — deliberately NOT recomputed at assertion time, which would be an echo
    /// judge, green by construction.
    /// </summary>
    private const string PinnedDefinitionHash =
        "sha256:bc973f30e1d3f6bbfbaa5cbb57317a4c0c72924ebd3574e2a721bd82e3603d6a";

    private static readonly bool Ps = OperatingSystem.IsWindows();

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gr32-edh-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string PlanDir => Path.Combine(_root, "plan");

    /// <summary>Outside the plan folder on purpose: a marker under a task folder would itself move that
    /// task's definition hash, which is the one variable these tests control.</summary>
    private string ScratchDir => Path.Combine(_root, "scratch");

    public ExecutedDefinitionHashTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(ScratchDir);
    }

    // ── P1 — the recorded hash is the PRE-EDIT pin (serial, W1) ──────────────────────────────────

    /// <summary>
    /// §5.8's acceptance criterion, stated verbatim: a task whose <c>task.json</c> is modified on disk
    /// AFTER the run loads it and BEFORE it settles must not record a <c>succeeded</c> whose stored
    /// <c>definitionHash</c> equals the post-edit bytes. The PRE-EDIT hash is recorded, so the next resume
    /// flags drift.
    ///
    /// <para><b>Both halves fail today, and that is the point.</b> Today the settle recomputes from
    /// current disk, so the recorded hash equals the post-edit value and differs from the pre-edit one —
    /// exactly inverted from what is asserted here. The assertion is an EQUALITY against a value captured
    /// BEFORE the edit; "the hash is non-null" and "the hash changed" are both true with the defect fully
    /// intact and would pin nothing.</para>
    /// </summary>
    [Fact]
    public async Task TheRecordedHash_IsThePreEditPin_WhenTaskJsonIsEditedMidRun_Serial()
    {
        string targetTaskJson = Path.Combine(PlanDir, "tasks", Target, "task.json");

        WriteConfig(defaultRetries: 0);
        // The editor runs FIRST and writes into the REAL plan folder by absolute path — genuinely
        // out-of-band, exactly like an operator's editor. The target depends on it, so the DAG (not a
        // timer) guarantees the write lands before the target settles.
        WriteTask(Editor, "edit the target's task.json mid-run", dependsOn: null,
            actionExtra: OverwriteFileLine(targetTaskJson, TaskJson(EditedMidRun, dependsOn: Editor)));
        WriteTask(Target, "the task whose definition moves under it", dependsOn: Editor);

        PlanDefinition plan = Load();
        TaskNode target = plan.Tasks.Single(t => t.Id == Target);

        // The definition the harness LOADED and is therefore about to EXECUTE, captured before the run.
        string hashBefore = TaskDefinitionHash.Compute(target);

        (RunReport report, RunJournal journal) = await RunSerialAsync(plan);

        // ── positive controls: the scenario really happened ─────────────────────────────────────
        AssertSettledSucceeded(journal, report, Target);

        Assert.Contains(EditedMidRun, File.ReadAllText(targetTaskJson), StringComparison.Ordinal);

        // The same node, re-hashed from CURRENT disk: this is what today's settle stamps.
        string hashAfterEdit = TaskDefinitionHash.Compute(target);
        Assert.NotEqual(hashBefore, hashAfterEdit);

        // ── the pin ─────────────────────────────────────────────────────────────────────────────
        string? recorded = journal.RecordedDefinitionHash(Target);
        Assert.Equal(hashBefore, recorded);
        Assert.NotEqual(hashAfterEdit, recorded);
    }

    // ── P14 — the pin is captured at LOAD, not at attempt start ──────────────────────────────────

    /// <summary>
    /// §6.7's discriminator between a LOAD-time pin and an ATTEMPT-START one. §5.7 rejects candidate (2)
    /// — re-read <c>task.json</c> per attempt — in prose, and nothing else pins the rejection: an
    /// implementation capturing the hash at attempt start passes every other behavioural pin in this plan,
    /// because a single mid-run edit lands after both capture points.
    ///
    /// <para><b>The discriminator is a RETRY.</b> The task's guardrail fails on its first invocation and
    /// edits <c>task.json</c> as it goes, so the edit is sequenced strictly between attempt 1 and attempt
    /// 2; the second invocation passes and the task settles. A load-time pin records the RUN-START value
    /// and passes. An attempt-start capture re-reads at the head of attempt 2 — after the edit — records
    /// the post-edit hash, and fails. Today's settle also records the post-edit hash, which is why this is
    /// red on today's tree.</para>
    /// </summary>
    [Fact]
    public async Task TheRecordedHash_IsTheRunStartValue_WhenTaskJsonIsEditedBetweenAttempts()
    {
        string soloTaskJson = Path.Combine(PlanDir, "tasks", Solo, "task.json");
        string marker = Path.Combine(ScratchDir, "attempt-1-ran.txt");

        WriteConfig(defaultRetries: 2);
        WriteTask(Solo, "fails once, then succeeds", dependsOn: null,
            guardrailBody: FailOnceAndEditLines(marker, soloTaskJson,
                TaskJson(EditedBetweenAttempts, dependsOn: null)));

        PlanDefinition plan = Load();
        TaskNode solo = plan.Tasks.Single();

        // The definition as of RUN START. An attempt-start capture would take a different value for
        // attempt 2, which is the whole discrimination.
        string hashAtRunStart = TaskDefinitionHash.Compute(solo);

        (RunReport report, RunJournal journal) = await RunSerialAsync(plan);

        // ── positive controls: there really were two attempts, and the second one settled ────────
        TaskJournalEntry entry = AssertSettledSucceeded(journal, report, Solo);
        Assert.True(entry.Attempts.Count >= 2,
            "the retry never happened, so nothing was sequenced BETWEEN attempts and this pin would be " +
            $"vacuous; recorded attempts: {entry.Attempts.Count}");

        Assert.Contains(EditedBetweenAttempts, File.ReadAllText(soloTaskJson), StringComparison.Ordinal);
        Assert.NotEqual(hashAtRunStart, TaskDefinitionHash.Compute(solo));

        // ── the pin ─────────────────────────────────────────────────────────────────────────────
        Assert.Equal(hashAtRunStart, journal.RecordedDefinitionHash(Solo));
    }

    // ── P5 — the no-op property (DECLARED EXEMPTION: green today, must STAY green) ───────────────

    /// <summary>
    /// §5.5: on every run in which nobody edits the plan folder mid-run, this change is a no-op down to
    /// the recorded bytes — the pin is <see cref="TaskDefinitionHash.Compute(TaskNode)"/> evaluated at
    /// load, today's value is the same function over the same file set evaluated at settle, and if disk
    /// did not move in between they are byte-identical.
    ///
    /// <para><b>DECLARED EXEMPTION from the red census.</b> This is a REGRESSION pin, not a defect pin: it
    /// passes on today's tree by construction and its job is to keep passing afterwards. It is the pin
    /// that proves no definition-hash migration wave is owed — no plan resumes into a drift halt on
    /// upgrade, no <c>Guardrails-Task-Hash:</c> trailer already on a plan branch becomes uncorroborated,
    /// and Part C's safe-suffix rule 3 keeps resolving <c>Safe</c> for every legitimate modern settle.</para>
    /// </summary>
    [Fact]
    public async Task AnUneditedRun_RecordsAHashIdenticalToAPostRunRecompute()
    {
        WriteConfig(defaultRetries: 0);
        WriteTask(Solo, "an ordinary task nobody touches", dependsOn: null);

        PlanDefinition plan = Load();
        TaskNode solo = plan.Tasks.Single();

        (RunReport report, RunJournal journal) = await RunSerialAsync(plan);

        // Nothing was edited, so no divergence gate can fire and this run's AllSucceeded is stable across
        // every milestone of this plan — which is exactly what "a no-op down to the recorded bytes" means.
        Assert.True(report.AllSucceeded,
            "an unedited run must stay wholly green; outcomes: " +
            string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));

        // The recompute is taken AFTER the run, over the same untouched folder.
        Assert.Equal(TaskDefinitionHash.Compute(solo), journal.RecordedDefinitionHash(Solo));
    }

    // ── P8 — the byte-pin (DECLARED EXEMPTION: green today, must STAY green) ─────────────────────

    /// <summary>
    /// §5.8's byte-pin. This plan changes <b>when</b> the definition hash is computed and never <b>what</b>
    /// it is computed over (§4.4: <c>HashText.EnumerateFolderFiles</c>'s file set is untouched,
    /// deliberately — changing it would move EVERY recorded definition hash in every plan and turn the
    /// next resume of each into a drift halt). A later task that "simplifies" the file set or the framing
    /// to make something pass would trigger exactly that repo-wide drift wave, and this is the tripwire.
    ///
    /// <para><b>DECLARED EXEMPTION from the red census</b>, same shape as P5: true before, and required to
    /// be true after.</para>
    ///
    /// <para>The expected value is a hard-coded literal read off the failing assertion once, NEVER
    /// recomputed by calling the function under test at assertion time — that would be an echo judge,
    /// green by construction. The fixture is built from literal strings in this file so no shared helper
    /// can move it, and it is loaded through the real <see cref="PlanLoader"/> so the pin also covers the
    /// loader's action resolution (the <c>action:&lt;rel&gt;</c> label is part of the hashed framing).</para>
    /// </summary>
    [Fact]
    public void TaskDefinitionHashCompute_OutputHasNotMoved_OnAPinnedFixtureFolder()
    {
        string pinnedPlanDir = WritePinnedFixture();

        PlanLoadResult load = new PlanLoader().Load(pinnedPlanDir);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));
        TaskNode task = Assert.Single(load.Plan!.Tasks);

        Assert.Equal(PinnedDefinitionHash, TaskDefinitionHash.Compute(task));
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Drivers
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    private PlanDefinition Load()
    {
        PlanLoadResult load = new PlanLoader().Load(PlanDir);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));
        Assert.NotNull(load.Plan);
        return load.Plan!;
    }

    /// <summary>
    /// A SERIAL / shared-workspace run of <paramref name="plan"/>: no worktree provider and
    /// <c>maxParallelism: 1</c>, so every successful settle goes through
    /// <c>AttemptJournaler.CompleteSucceededOrInvalidFragment</c> — write site <b>W1</b>, the one the
    /// issue names and the one this file is scoped to.
    /// <para>The <see cref="PlanDefinition"/> is passed IN rather than re-loaded, because the whole point
    /// is that the run executes the definition the caller already hashed: re-loading here would re-read
    /// <c>task.json</c> and destroy the load-vs-settle distinction under test.</para>
    /// </summary>
    private static async Task<(RunReport Report, RunJournal Journal)> RunSerialAsync(PlanDefinition plan)
    {
        var stateManager = new StateManager(plan.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(plan);

        var registry = PromptRunnerRegistry.Build(plan.Config,
            _ => throw new InvalidOperationException("every fixture action here is a script"));
        var interpreterMap = new InterpreterMap(new PathExecutableProbe(), plan.Config.Interpreters);
        var executor = new TaskExecutor(
            plan, new ProcessRunner(), interpreterMap, stateManager, journal, IRunObserver.Null, registry);

        var scheduler = new Scheduler(plan, executor, journal, maxParallelism: 1);
        RunReport report = await scheduler.RunAsync(plan, Ct);
        return (report, journal);
    }

    /// <summary>
    /// The positive control shared by P1 and P14: the task reached a SUCCESSFUL SETTLE, so there is a
    /// recorded definition hash for the pin to be about.
    /// <para>Deliberately asserted on the JOURNAL entry rather than on <c>report.AllSucceeded</c>: a later
    /// milestone blocks DELIVERY on a run carrying a mid-run definition edit while preserving the settle
    /// itself ("record the success, block the delivery"), so <c>AllSucceeded</c> is expected to go false
    /// on these two runs while <c>status: succeeded</c> is required to stay.</para>
    /// </summary>
    private static TaskJournalEntry AssertSettledSucceeded(RunJournal journal, RunReport report, string taskId)
    {
        Assert.True(journal.Document.Tasks.TryGetValue(taskId, out TaskJournalEntry? entry),
            $"'{taskId}' has no journal entry at all; outcomes: " +
            string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));

        Assert.True(entry!.Status == JournalTaskStatus.Succeeded,
            $"'{taskId}' must have SETTLED for its recorded definition hash to mean anything, but its " +
            $"journal status is '{entry.Status}'; outcomes: " +
            string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}")));

        return entry;
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Fixture: a real, loadable plan folder on disk
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    private void WriteConfig(int defaultRetries) =>
        Write(Path.Combine(PlanDir, "guardrails.json"),
            $$"""
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": {{defaultRetries}},
              "maxParallelism": 1
            }
            """);

    private static string TaskJson(string description, string? dependsOn)
    {
        string depends = dependsOn is null ? "[]" : $"[\"{dependsOn}\"]";
        return $$"""{ "writeScope": [], "description": "{{description}}", "dependsOn": {{depends}} }""";
    }

    /// <summary>
    /// A green script task. <paramref name="actionExtra"/> is one extra line the action runs before
    /// <c>exit 0</c>; <paramref name="guardrailBody"/> REPLACES the default always-green gate body.
    /// </summary>
    private void WriteTask(
        string id, string description, string? dependsOn,
        string? actionExtra = null, string? guardrailBody = null)
    {
        string taskDir = Path.Combine(PlanDir, "tasks", id);
        Write(Path.Combine(taskDir, "task.json"), TaskJson(description, dependsOn));

        WriteExecutable(Path.Combine(taskDir, Script("action")),
            Shebang + (actionExtra is null ? "" : actionExtra + "\n") + "exit 0\n");

        WriteExecutable(Path.Combine(taskDir, "guardrails", Script("01-check")),
            Shebang + $"# catches: nothing - a fixture gate for '{id}'\n" + (guardrailBody ?? "exit 0\n"));
    }

    /// <summary>One line that overwrites <paramref name="path"/> with <paramref name="content"/>.</summary>
    private static string OverwriteFileLine(string path, string content) => Ps
        ? $"Set-Content -NoNewline -Path '{path}' -Value '{content}'"
        : $"printf '%s' '{content}' > '{path}'";

    /// <summary>
    /// A guardrail body that fails ONCE — editing <paramref name="taskJsonPath"/> on the way out — and
    /// passes on every later invocation, keyed off <paramref name="markerPath"/>. That is what sequences
    /// P14's edit strictly between attempt 1 and attempt 2 without a timer.
    /// </summary>
    private static string FailOnceAndEditLines(string markerPath, string taskJsonPath, string taskJson) => Ps
        ? $"if (Test-Path '{markerPath}') {{ exit 0 }}\n"
          + $"Set-Content -NoNewline -Path '{markerPath}' -Value 'attempt 1 ran'\n"
          + OverwriteFileLine(taskJsonPath, taskJson) + "\n"
          + "exit 1\n"
        : $"if [ -f '{markerPath}' ]; then exit 0; fi\n"
          + $"printf '%s' 'attempt 1 ran' > '{markerPath}'\n"
          + OverwriteFileLine(taskJsonPath, taskJson) + "\n"
          + "exit 1\n";

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Fixture: P8's FIXED definition surface — every byte a literal in this file
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build the pinned fixture and return its plan directory. Deliberately does NOT reuse
    /// <see cref="WriteTask"/> or <see cref="Script"/>: this surface must be identical on every OS and
    /// must not move when a shared helper does. Nothing here is ever executed — only hashed — so the
    /// <c>.sh</c> extension is a byte, not a platform choice.
    /// </summary>
    private string WritePinnedFixture()
    {
        string planDir = Path.Combine(_root, "pinned-plan");
        string taskDir = Path.Combine(planDir, "tasks", "01-pinned");

        Write(Path.Combine(planDir, "guardrails.json"), "{\n  \"version\": 1\n}\n");
        Write(Path.Combine(taskDir, "task.json"),
            "{\n  \"writeScope\": [],\n  \"description\": \"pinned\",\n  \"dependsOn\": []\n}\n");
        Write(Path.Combine(taskDir, "action.sh"), "#!/usr/bin/env bash\nexit 0\n");
        // ASCII only, and LF only: the two things a byte-pin must not be able to drift on.
        Write(Path.Combine(taskDir, "guardrails", "01-check.sh"),
            "#!/usr/bin/env bash\n# catches: nothing - a pinned fixture gate\nexit 0\n");

        return planDir;
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // File helpers
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    private static string Script(string stem) => Ps ? stem + ".ps1" : stem + ".sh";

    private static string Shebang => Ps ? "" : "#!/usr/bin/env bash\n";

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

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }
}
