using System.Security.Cryptography;
using System.Text;
using Guardrails.Core.Execution;
using Guardrails.Core.Hashing;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

// Deliberately NOT nested as `Guardrails.Core.Tests.Journal`, despite this file living under Journal/ —
// exactly as the siblings `Journal/JudgeSpendRecordingTests.cs:9-14` and
// `Journal/ExecutedDefinitionHashTests.cs:9-14` already record. Declaring that nested namespace ANYWHERE
// in this assembly introduces a `Journal` member under `Guardrails.Core.Tests` which then WINS the
// enclosing-namespace walk over the production `Guardrails.Core.Journal` for every unqualified
// `Journal.X` reference in the project (C# resolves a member of an enclosing namespace before a
// `using`-imported one). `OverwatchNoVerdictTests.cs:355`'s `Journal.TaskStatus.Running`, the shared
// `WavePlanBuilder.cs`, and `JudgeSpendRecordingTests.cs` itself then fail with CS0234 — all three
// outside this task's write scope to fix. Folder and namespace are decoupled here on purpose.
namespace Guardrails.Core.Tests;

/// <summary>
/// Plan 32 (#556) <b>milestone B — the wave twin</b>, §5.4/§5.8's P7. Milestone A pins the TASK level: the
/// four task-level write sites stamp the definition hash captured when the loader read the bytes, so a
/// task edited mid-run records its <b>pre-edit</b> definition. The wave level does not move with it —
/// <see cref="WaveDefinitionHash.Compute(WaveNode)"/> re-reads <c>TaskDefinitionHash.Compute(task)</c> from
/// CURRENT DISK inside its own fold (<c>WaveDefinitionHash.cs:44-49</c>) and walks the wave's
/// <c>guardrails/**</c> / <c>preflights/**</c> from current disk too (<c>:51-53</c>), and W5
/// (<c>Scheduler.cs:689</c>) stamps that value into <c>waves[].definitionHash</c> at wave completion.
///
/// <para><b>Why B is not optional.</b> SSOT §7.2/§14.5 already assert that the wave hash changes <i>iff</i>
/// a constituent task hash changes — <i>"the levels cannot drift apart."</i> Shipping A alone makes that
/// sentence FALSE: on an edited run each task's stamped hash describes the pre-edit bytes while the wave's
/// describes the post-edit ones, and the two levels disagree about the same tasks in the same journal.
/// Worse, A alone makes the disagreement HARDER to notice than it is today, because today both levels are
/// consistently wrong (§5.4).</para>
///
/// <para><b>No assertion here names an API member this plan has not written yet.</b>
/// <c>WaveNode.DefinitionHashAtLoad</c> and the pinned wave fold are stage 9's deliverables and do not
/// exist; <c>src/**</c> is outside this task's write scope. Every pin below is stated on the JOURNAL's
/// recorded wave hash (<see cref="WaveJournalEntry.DefinitionHash"/>) and its recorded per-task hashes
/// (<see cref="RunJournal.RecordedDefinitionHash"/>), both of which exist today. That constraint is also
/// what protects the anti-echo rule below: a test that cannot NAME the production pinned fold cannot
/// compute its expectation with it.</para>
///
/// <para><b>The echo-judge rule, and the trade it costs (§5.8).</b> <i>"Neither leg may compute its expected
/// value by calling the production pinned function — that is an echo-judge, green by construction. The test
/// reconstructs the fold independently, separators and labels included."</i> So <see cref="FoldWave"/> and
/// <see cref="WaveGateSegment"/> below RE-STATE <c>WaveDefinitionHash.cs:41-67</c>'s framing — the
/// <c>task:&lt;wave-relative folder&gt;</c> label, <see cref="HashText.UnitSeparator"/>,
/// <see cref="HashText.RecordSeparator"/>, then <c>guardrails/**</c>, then <c>preflights/**</c>, then the
/// optional <c>brief.md</c>, then <c>sha256:</c> + lowercase hex. <c>WaveDefinitionHash.Compute(wave)</c> is
/// NOT called for an expected value either: it is the DISK form, so on an edited fixture it returns the
/// post-edit value and P7b would be green today and red after milestone B — inverted. This duplicates
/// production logic, which is its own hazard; §5.8 names it as a deliberate trade rather than leaving it
/// for the implementer to discover, and the cost is that a reconstruction with the wrong separator is red
/// before AND after. Reading the two side by side is a human job.</para>
///
/// <para><b>Serial / shared-workspace mode.</b> Both runs are <c>maxParallelism: 1</c> with NO worktree
/// provider. W5 does not vary by mode — the marker commit is skipped without a provider but
/// <c>RecordWaveCompleted</c> stamps the same hash either way (<c>Scheduler.cs:689-693</c>) — so the
/// cheaper mode proves the same write. The mid-run edit is sequenced by the DAG rather than by a timer, the
/// way <c>PlanEditedDuringRunTests.CreateMidRunEditPlan</c> and the sibling
/// <see cref="ExecutedDefinitionHashTests"/> both do it: a task's ACTION performs the edit by absolute
/// path, out-of-band, exactly like an operator's editor. <b>The edit IS the fixture</b> — it is never made
/// conditional, retimed or removed.</para>
///
/// <para><b>TDD red — both pins, no declared exemptions.</b> Both fail on today's tree, for two DIFFERENT
/// halves of the same defect. An implementation that folds <c>task.DefinitionHashAtLoad</c> for the task
/// half but still walks the wave's gate folders from current disk passes
/// <see cref="TheWaveHashChanges_IffAConstituentTaskHashChanges"/> exactly while leaving the wave-level half
/// intact; <see cref="TheStampedWaveHash_IsUnmoved_WhenAWaveGateFileIsEditedMidRun"/> is the only thing
/// that separates them.</para>
///
/// <para><b>Why neither run asserts <c>report.AllSucceeded</c>.</b> Milestone C blocks DELIVERY on a run
/// carrying a mid-run definition edit while preserving the settle unconditionally ("record the success,
/// block the delivery", §6.4), so both runs here are EXPECTED to lose <c>AllSucceeded</c> at that stage.
/// Both take their positive control from the durable surfaces that are stable across every milestone: the
/// task's journal entry reads <c>succeeded</c>, and the wave's reads <c>completed</c> with a stamped
/// hash.</para>
/// </summary>
public sealed class WaveExecutedDefinitionHashTests : IDisposable
{
    /// <summary>The single wave both fixtures use (the loader's <c>^wave-([0-9]+)-[a-z0-9-]+$</c> shape).</summary>
    private const string Wave = "wave-01-scaffold";

    /// <summary>P7a's first task: its action edits <see cref="Target"/>'s <c>task.json</c> mid-run.</summary>
    private const string Editor = "01-editor";

    /// <summary>P7a's second task: the constituent whose definition moves under it while it is in flight.</summary>
    private const string Target = "02-target";

    /// <summary>P7b's only task: its action edits the WAVE's exit-gate file mid-run.</summary>
    private const string GateEditor = "01-gate-editor";

    /// <summary>The description P7a's mid-run edit writes into the target's <c>task.json</c>.</summary>
    private const string EditedMidRun = "edited mid-run";

    /// <summary>The comment P7b's mid-run edit appends to the wave's exit-gate script.</summary>
    private const string GateEditedMidRun = "# the wave exit gate, edited mid-run";

    /// <summary>The <c>sha256:</c> framing prefix every member of the plan-hash family carries.</summary>
    private const string Prefix = "sha256:";

    private const string GuardrailsDirName = "guardrails";
    private const string PreflightsDirName = "preflights";

    private static readonly bool Ps = OperatingSystem.IsWindows();

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gr32-wedh-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string PlanDir => Path.Combine(_root, "plan");

    public WaveExecutedDefinitionHashTests() => Directory.CreateDirectory(_root);

    // ── P7a — the TASK fold: the wave hash moves iff a constituent task hash moves ────────────────

    /// <summary>
    /// §5.8's P7a. Editing one constituent task's <c>task.json</c> mid-run changes the wave's recorded hash
    /// <b>iff</b> it changes that task's recorded hash.
    ///
    /// <para><b>How the "iff" is stated so it can bite.</b> The antecedent is established first, as a
    /// positive control: milestone A already holds, so the edited task's RECORDED hash is the pre-edit pin
    /// and did NOT move. The consequent is then the whole assertion — the wave's recorded hash must be
    /// exactly the fold OVER THOSE RECORDED VALUES, and must NOT be the fold over the post-edit bytes now on
    /// disk. Both halves are checked, because "the wave hash changed" is true today with the defect fully
    /// intact and would pin nothing.</para>
    ///
    /// <para><b>Red today.</b> W5 stamps <c>WaveDefinitionHash.Compute(wave)</c>, whose task fold re-reads
    /// <c>TaskDefinitionHash.Compute(task)</c> from current disk — the POST-edit bytes — while the task
    /// level records the PRE-edit pin. The two levels disagree about the same task in the same journal,
    /// which is precisely the state §14.5 asserts cannot happen.</para>
    /// </summary>
    [Fact]
    public async Task TheWaveHashChanges_IffAConstituentTaskHashChanges()
    {
        string targetTaskJson = Path.Combine(PlanDir, Wave, "tasks", Target, "task.json");

        WriteConfig();
        // The editor runs FIRST and writes into the REAL plan folder by absolute path — genuinely
        // out-of-band. The target depends on it, so the DAG (not a timer) guarantees the write lands
        // after the plan is loaded and before the wave completes.
        WriteWaveTask(Editor, "edit the target's task.json mid-run", dependsOn: null,
            actionExtra: OverwriteFileLine(targetTaskJson, TaskJson(EditedMidRun, dependsOn: Editor)));
        WriteWaveTask(Target, "the constituent whose definition moves under it", dependsOn: Editor);

        PlanDefinition plan = Load();
        WaveNode wave = Assert.Single(plan.Waves);
        TaskNode target = wave.Tasks.Single(t => t.Id == Qualified(Target));

        // The definition the harness LOADED and is therefore about to EXECUTE, captured before the edit.
        string targetHashBefore = TaskDefinitionHash.Compute(target);

        (RunReport report, RunJournal journal) = await RunSerialAsync(plan);

        // ── positive controls: the scenario really happened ─────────────────────────────────────
        AssertSettledSucceeded(journal, report, Qualified(Editor));
        AssertSettledSucceeded(journal, report, Qualified(Target));

        Assert.Contains(EditedMidRun, File.ReadAllText(targetTaskJson), StringComparison.Ordinal);

        // The same node, re-hashed from CURRENT disk: the edit genuinely moved the bytes.
        string targetHashOnDisk = TaskDefinitionHash.Compute(target);
        Assert.NotEqual(targetHashBefore, targetHashOnDisk);

        // The ANTECEDENT of the iff: milestone A holds, so the constituent's RECORDED hash did NOT move.
        Assert.Equal(targetHashBefore, journal.RecordedDefinitionHash(Qualified(Target)));

        WaveJournalEntry waveEntry = AssertWaveCompleted(journal, report);

        // No wave-gate file was touched here, so the gate half of the fold is the same before and after —
        // which is what isolates this pin to the TASK half.
        string gates = WaveGateSegment(wave);
        string foldOfRecordedTaskHashes = FoldWave(wave, t => RequireRecorded(journal, t), gates);
        string foldOfDiskTaskHashes = FoldWave(wave, TaskDefinitionHash.Compute, gates);

        // The fixture is not vacuous: the two candidate wave values genuinely differ, so the equality and
        // the inequality below are two independent claims rather than one restated.
        Assert.NotEqual(foldOfRecordedTaskHashes, foldOfDiskTaskHashes);

        // ── the pin ─────────────────────────────────────────────────────────────────────────────
        Assert.Equal(foldOfRecordedTaskHashes, waveEntry.DefinitionHash);
        Assert.NotEqual(foldOfDiskTaskHashes, waveEntry.DefinitionHash);
    }

    // ── P7b — the WAVE-GATE fold: a mid-run gate edit leaves the stamped wave hash unmoved ───────

    /// <summary>
    /// §5.8's P7b. An implementation that folds <c>task.DefinitionHashAtLoad</c> for the task half but still
    /// calls <c>AppendFolder(builder, wave.Directory, "guardrails")</c> from CURRENT DISK
    /// (<c>WaveDefinitionHash.cs:51-52</c>) passes P7a <b>exactly</b>, while leaving the wave-level half of
    /// the defect intact. So this leg edits a wave GATE file mid-run and asserts the stamped wave hash is
    /// UNMOVED.
    ///
    /// <para><b>Deliberately no task edit.</b> Every <c>task.json</c> here is untouched, and that is
    /// asserted: the task half of the fold agrees between pin and disk, so this test can only be red for
    /// the wave-gate half. Without that isolation a single fixture would prove one thing twice.</para>
    ///
    /// <para><b>Why the pre-edit gate bytes are captured before the run rather than reconstructed after.</b>
    /// A gate file's PRE-edit content is gone from disk once the edit lands, and re-deriving it after the
    /// fact would mean writing down the expected bytes twice. <see cref="WaveGateSegment"/> is therefore
    /// evaluated once against the loaded folder (which is still the load-time state) and once after the
    /// run; the first is the expected fold input, the second is what today's W5 folds.</para>
    ///
    /// <para><b>Why the edit APPENDS a comment.</b> The wave exit gate is a live artifact
    /// (<c>Scheduler.RunWaveExitGateAsync</c>) — the edit must move its bytes without making it a gate that
    /// fails, or the wave never completes and there is no stamped hash for this pin to be about.</para>
    ///
    /// <para><b>Red today</b> for the same structural reason as P7a, one level up: W5 stamps a value walked
    /// from current disk, so the post-edit gate bytes are what reaches <c>waves[].definitionHash</c> — a
    /// certificate for a gate surface that is not the one this run loaded. §5.4: the wave gate folders are
    /// pinned too, even though the gate scripts are re-read at execution, for the same reason §5.6 gives
    /// for the action file — a mid-run edit makes ANY single recorded hash a lie, and the only choice is
    /// which lie fails loud.</para>
    /// </summary>
    [Fact]
    public async Task TheStampedWaveHash_IsUnmoved_WhenAWaveGateFileIsEditedMidRun()
    {
        string waveGateFile = Path.Combine(PlanDir, Wave, GuardrailsDirName, Script("01-exit"));

        WriteConfig();
        WriteWaveExitGate("01-exit");
        WriteWaveTask(GateEditor, "edit the wave's exit gate mid-run", dependsOn: null,
            actionExtra: AppendCommentLine(waveGateFile, GateEditedMidRun));

        PlanDefinition plan = Load();
        WaveNode wave = Assert.Single(plan.Waves);

        // The wave's OWN gate surface as the run LOADED it — the one half of the fold this test moves.
        string gatesBefore = WaveGateSegment(wave);

        (RunReport report, RunJournal journal) = await RunSerialAsync(plan);

        // ── positive controls: the scenario really happened ─────────────────────────────────────
        AssertSettledSucceeded(journal, report, Qualified(GateEditor));

        Assert.Contains(GateEditedMidRun, File.ReadAllText(waveGateFile), StringComparison.Ordinal);

        string gatesAfter = WaveGateSegment(wave);
        Assert.NotEqual(gatesBefore, gatesAfter);

        // ISOLATION: no task.json moved, so the TASK half of the fold is identical whichever source it is
        // taken from, and the only thing this pin can be red for is the wave-GATE half.
        foreach (TaskNode task in wave.Tasks)
        {
            Assert.Equal(TaskDefinitionHash.Compute(task), RequireRecorded(journal, task));
        }

        WaveJournalEntry waveEntry = AssertWaveCompleted(journal, report);

        string foldWithLoadedGates = FoldWave(wave, t => RequireRecorded(journal, t), gatesBefore);
        string foldWithEditedGates = FoldWave(wave, t => RequireRecorded(journal, t), gatesAfter);

        // The fixture is not vacuous: the gate edit really does move the fold.
        Assert.NotEqual(foldWithLoadedGates, foldWithEditedGates);

        // ── the pin ─────────────────────────────────────────────────────────────────────────────
        Assert.Equal(foldWithLoadedGates, waveEntry.DefinitionHash);
        Assert.NotEqual(foldWithEditedGates, waveEntry.DefinitionHash);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // The fold, RECONSTRUCTED — §5.8's anti-echo requirement, mirroring WaveDefinitionHash.cs:41-67
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuild one wave's definition hash from values this test already holds: a per-task hash supplied by
    /// <paramref name="taskHash"/> (the JOURNAL's recorded value, or a disk recompute, depending on which
    /// claim is being made) and a <paramref name="gateSegment"/> from <see cref="WaveGateSegment"/>.
    ///
    /// <para>The framing is <c>WaveDefinitionHash.cs:43-49</c>'s, restated rather than called: each
    /// constituent's <c>task:&lt;wave-relative folder&gt;</c> label, a <see cref="HashText.UnitSeparator"/>,
    /// the task hash VALUE, a <see cref="HashText.RecordSeparator"/> — in wave-relative folder-name ORDINAL
    /// order — then the wave's own gate segment, then SHA-256 over the UTF-8 bytes with the <c>sha256:</c>
    /// prefix and lowercase hex.</para>
    /// </summary>
    private static string FoldWave(WaveNode wave, Func<TaskNode, string> taskHash, string gateSegment)
    {
        var builder = new StringBuilder();

        // 1. Each constituent task's hash VALUE, in wave-relative folder-name order.
        foreach (TaskNode task in wave.Tasks.OrderBy(WaveRelativeFolder, StringComparer.Ordinal))
        {
            builder.Append("task:").Append(WaveRelativeFolder(task)).Append(HashText.UnitSeparator);
            builder.Append(taskHash(task));
            builder.Append(HashText.RecordSeparator);
        }

        // 2-4. The wave's own gate + brief segment.
        builder.Append(gateSegment);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Prefix + Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// The wave's own half of the fold AS IT STANDS ON DISK RIGHT NOW, as the raw labeled-segment text that
    /// <see cref="FoldWave"/> appends: <c>guardrails/**</c>, then <c>preflights/**</c> (each recursive,
    /// sorted, newline-normalized), then the OPTIONAL <c>brief.md</c> folded only when present —
    /// <c>WaveDefinitionHash.cs:51-63</c>. The shared plan-root <c>guardrails.json</c> is excluded, as it is
    /// there (Open Decision C).
    /// </summary>
    private static string WaveGateSegment(WaveNode wave)
    {
        var builder = new StringBuilder();
        AppendFolder(builder, wave.Directory, GuardrailsDirName);
        AppendFolder(builder, wave.Directory, PreflightsDirName);

        string briefPath = Path.Combine(wave.Directory, WaveNode.BriefFileName);
        if (File.Exists(briefPath))
        {
            HashText.AppendFile(builder, WaveNode.BriefFileName, briefPath);
        }

        return builder.ToString();
    }

    private static void AppendFolder(StringBuilder builder, string waveDirectory, string folderName)
    {
        foreach ((string Label, string AbsolutePath) file in
                 HashText.EnumerateFolderFiles(waveDirectory, Path.Combine(waveDirectory, folderName)))
        {
            HashText.AppendFile(builder, file.Label, file.AbsolutePath);
        }
    }

    /// <summary>The task's wave-relative folder name — <c>WaveDefinitionHash.cs:79-82</c>'s label source.</summary>
    private static string WaveRelativeFolder(TaskNode task) =>
        task.WaveDir is { } wave && task.Id.StartsWith(wave + "/", StringComparison.Ordinal)
            ? task.Id[(wave.Length + 1)..]
            : task.Id;

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Drivers + positive controls
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
    /// <c>maxParallelism: 1</c>. W5 (<c>Scheduler.cs:689</c>) stamps the wave hash into the journal either
    /// way — only the <c>Guardrails-Wave:</c> marker commit needs a provider — so this is the cheap mode
    /// that proves the same write.
    /// <para>The <see cref="PlanDefinition"/> is passed IN rather than re-loaded, because the whole point is
    /// that the run executes the definition the caller already hashed.</para>
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
    /// The task-level positive control: the constituent reached a SUCCESSFUL SETTLE, so it has a recorded
    /// definition hash for the wave fold to be reconstructed from.
    /// <para>Asserted on the JOURNAL entry rather than on <c>report.AllSucceeded</c>: milestone C blocks
    /// delivery on a run carrying a mid-run definition edit while preserving the settle itself, so
    /// <c>AllSucceeded</c> is expected to go false on both of these runs while <c>status: succeeded</c> is
    /// required to stay.</para>
    /// </summary>
    private static void AssertSettledSucceeded(RunJournal journal, RunReport report, string taskId)
    {
        Assert.True(journal.Document.Tasks.TryGetValue(taskId, out TaskJournalEntry? entry),
            $"'{taskId}' has no journal entry at all; outcomes: {Outcomes(report)}");

        Assert.True(entry!.Status == JournalTaskStatus.Succeeded,
            $"'{taskId}' must have SETTLED for the wave to complete and stamp a hash, but its journal " +
            $"status is '{entry.Status}'; outcomes: {Outcomes(report)}");
    }

    /// <summary>
    /// The wave-level positive control: the wave reached completion and W5 stamped a hash. Without this a
    /// run that halted before <c>Scheduler.cs:689</c> would leave the pin comparing against null, which is
    /// a pass-by-absence rather than a finding.
    /// </summary>
    private static WaveJournalEntry AssertWaveCompleted(RunJournal journal, RunReport report)
    {
        WaveJournalEntry? entry = journal.WaveEntryOf(Wave);
        Assert.True(entry is not null,
            $"wave '{Wave}' has no journal entry at all — the run never reached wave completion, so " +
            $"nothing stamped a wave definition hash; outcomes: {Outcomes(report)}");

        Assert.True(entry!.Status == WaveStatus.Completed,
            $"wave '{Wave}' must have COMPLETED for its stamped hash to mean anything, but its journal " +
            $"status is '{entry.Status}'; outcomes: {Outcomes(report)}");

        Assert.True(entry.DefinitionHash is not null,
            $"wave '{Wave}' completed without stamping a definition hash at all; outcomes: {Outcomes(report)}");

        return entry;
    }

    /// <summary>
    /// The journal's recorded hash for <paramref name="task"/>, failing loudly rather than folding a hole:
    /// a null silently reconstructs a DIFFERENT fold that could agree with the stamped value for the wrong
    /// reason.
    /// </summary>
    private static string RequireRecorded(RunJournal journal, TaskNode task)
    {
        string? recorded = journal.RecordedDefinitionHash(task.Id);
        Assert.True(recorded is not null,
            $"'{task.Id}' recorded no definition hash, so the wave fold would be reconstructed from a hole");
        return recorded!;
    }

    private static string Outcomes(RunReport report) =>
        string.Join(", ", report.Tasks.Select(t => $"{t.TaskId}={t.Outcome}"));

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Fixture: a real, loadable WAVED plan folder on disk
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The wave-qualified <see cref="TaskNode.Id"/> the journal keys a wave task's entry by.</summary>
    private static string Qualified(string folder) => Wave + "/" + folder;

    private void WriteConfig() =>
        Write(Path.Combine(PlanDir, "guardrails.json"),
            """
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 0,
              "maxParallelism": 1
            }
            """);

    private static string TaskJson(string description, string? dependsOn)
    {
        string depends = dependsOn is null ? "[]" : $"[\"{dependsOn}\"]";
        return $$"""{ "writeScope": [], "description": "{{description}}", "dependsOn": {{depends}} }""";
    }

    /// <summary>
    /// A green script task at <c>&lt;plan&gt;/&lt;wave&gt;/tasks/&lt;folder&gt;/</c>.
    /// <paramref name="actionExtra"/> is one extra line the action runs before <c>exit 0</c> — the mid-run
    /// edit. <paramref name="dependsOn"/> is a WAVE-RELATIVE folder name; the loader qualifies it.
    /// </summary>
    private void WriteWaveTask(string folder, string description, string? dependsOn, string? actionExtra = null)
    {
        string taskDir = Path.Combine(PlanDir, Wave, "tasks", folder);
        Write(Path.Combine(taskDir, "task.json"), TaskJson(description, dependsOn));

        WriteExecutable(Path.Combine(taskDir, Script("action")),
            Shebang + (actionExtra is null ? "" : actionExtra + "\n") + "exit 0\n");

        WriteExecutable(Path.Combine(taskDir, GuardrailsDirName, Script("01-check")),
            Shebang + $"# catches: nothing - a fixture gate for '{folder}'\nexit 0\n");
    }

    /// <summary>The wave's EXIT gate (SSOT §14.3) — P7b's edited surface, and the reason its wave has one.</summary>
    private void WriteWaveExitGate(string stem) =>
        WriteExecutable(Path.Combine(PlanDir, Wave, GuardrailsDirName, Script(stem)),
            Shebang + $"# catches: nothing - a fixture wave EXIT gate '{stem}'\nexit 0\n");

    /// <summary>One line that overwrites <paramref name="path"/> with <paramref name="content"/>.</summary>
    private static string OverwriteFileLine(string path, string content) => Ps
        ? $"Set-Content -NoNewline -Path '{path}' -Value '{content}'"
        : $"printf '%s' '{content}' > '{path}'";

    /// <summary>
    /// One line that APPENDS <paramref name="comment"/> to <paramref name="path"/> — the byte-moving edit
    /// that still leaves a wave gate script green, so the wave completes and stamps a hash.
    /// </summary>
    private static string AppendCommentLine(string path, string comment) => Ps
        ? $"Add-Content -Path '{path}' -Value '{comment}'"
        : $"printf '%s\\n' '{comment}' >> '{path}'";

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
