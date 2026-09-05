using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.Execution;

/// <summary>
/// The unit-level contract of <see cref="LivePlanEditWatch"/> (plan 31 §5.2): the watch recomputes the
/// per-FILE definition surface of every task, reports what changed since the last call, and RE-BASELINES —
/// so an operator edit made during a live run is reported ONCE and then stays quiet.
///
/// <para>Two of these pins are about what the watch must NOT see, and both are load-bearing for a
/// reason that is easy to lose:</para>
/// <list type="bullet">
///   <item><b>The editor-artifact ignore list lives HERE, not in <c>HashText</c></b> (§5.2). A stray
///     <c>.DS_Store</c> / <c>Thumbs.db</c> / <c>*.swp</c> / <c>*.orig</c> / <c>*.rej</c> under a task's
///     <c>guardrails/</c> IS part of that task's <see cref="TaskDefinitionHash"/> — and must stay that way.
///     Moving the filter into <c>HashText</c> would move every recorded definition hash in every plan, and
///     a moved definition hash is a definition-drift HALT on the next resume. <see cref="Poll"/>-quiet +
///     hash-changed is therefore asserted TOGETHER in <c>U7</c>: the watch is strictly quieter than the
///     hash, never noisier.</item>
///   <item><b><c>logs/</c> and <c>state/</c> are outside <see cref="TaskDefinitionFiles.Enumerate"/></b>,
///     which is the whole reason the harness's own constant writes into the plan folder cannot fire an
///     advisory aimed at HUMAN edits (§5.3 — an advisory that fires on the harness's own writes stops
///     being read).</item>
/// </list>
///
/// <para>Definition files only — nothing here executes a script, so the fixture uses <c>.ps1</c> on every
/// platform and the tests are byte-level and deterministic.</para>
/// </summary>
public sealed class LivePlanEditWatchTests : IDisposable
{
    private const string TaskA = "01-first";
    private const string TaskB = "02-second";

    /// <summary>A task folder the HARNESS authors mid-run (a JIT wave's breakdown output, #568) — absent
    /// from the plan the watch was constructed from, and therefore absent from its baseline.</summary>
    private const string JitTask = "03-jit-authored";

    private const string GuardrailLabel = "guardrails/01-check.ps1";

    private readonly string _planDir;

    public LivePlanEditWatchTests()
    {
        _planDir = Path.Combine(Path.GetTempPath(), "gr-lpew-" + Guid.NewGuid().ToString("N"));
        WriteTaskFolder(TaskA);
        WriteTaskFolder(TaskB);
    }

    // ── U1 ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Nothing touched between two polls ⇒ the second poll reports nothing.</summary>
    [Fact]
    public void Poll_WithNothingChanged_ReturnsEmpty()
    {
        var watch = new LivePlanEditWatch(Plan());

        watch.Poll();

        Assert.Empty(watch.Poll());
    }

    // ── U2 ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The positive unit pin: a guardrail script edited between two polls is reported as exactly one
    /// <see cref="PlanEdit"/> naming THAT task, and its <see cref="PlanEdit.Files"/> names THAT file with
    /// <see cref="PlanEditKind.Modified"/> — the per-file breakdown §5.2 buys over a whole-task hash.
    /// </summary>
    [Fact]
    public void Poll_AfterAGuardrailScriptIsModified_ReportsThatTaskAndThatFile()
    {
        var watch = new LivePlanEditWatch(Plan());
        watch.Poll(); // baseline

        ModifyGuardrail(TaskB);

        PlanEdit edit = Assert.Single(watch.Poll());
        Assert.Equal(TaskB, edit.TaskId);
        Assert.NotEqual(edit.OldHash, edit.NewHash);

        PlanEditedFile file = Assert.Single(edit.Files);
        Assert.Equal(TaskB, file.TaskId);
        Assert.Equal(GuardrailLabel, Normalize(file.Label));
        Assert.Equal(PlanEditKind.Modified, file.Kind);
    }

    // ── U3 ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The re-baselining half of <see cref="LivePlanEditWatch.Poll"/>'s contract — "return what changed
    /// since the last call, AND re-baseline". Report once, then stay silent: without this an edit made
    /// early in a long run would be re-reported at every subsequent scheduler boundary.
    /// </summary>
    [Fact]
    public void Poll_ReBaselines_SoASecondPollAfterOneEditIsEmpty()
    {
        var watch = new LivePlanEditWatch(Plan());
        watch.Poll();

        ModifyGuardrail(TaskB);

        Assert.Single(watch.Poll());
        Assert.Empty(watch.Poll());
    }

    // ── U4 ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The PLAN-WIDE form §5.3 requires after each of the five harness writers (JIT wave breakdown,
    /// <c>BreakdownInventory.Revert</c>, <c>SweepIncompleteTrailingTaskFolders</c>,
    /// <c>QuarantineWholeTasksFolder</c>, and a <c>TryResolveDrift</c> that resolved). Plan-wide rather
    /// than per-task because three of the five have authority over files outside the unit they nominally
    /// act on, so a per-task re-baseline would leave the watch reporting the harness's own writes as
    /// operator edits.
    /// </summary>
    [Fact]
    public void Rebaseline_WithNoIds_SilencesTheWholePlan()
    {
        var watch = new LivePlanEditWatch(Plan());
        watch.Poll();

        ModifyGuardrail(TaskA);
        ModifyGuardrail(TaskB);

        watch.Rebaseline();

        Assert.Empty(watch.Poll());
    }

    // ── U5 ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// "An unknown task id is a no-op" (§5.2) — in BOTH directions. It must not throw, and it must not be
    /// treated as "no known id, therefore re-baseline everything": the pending edit to a real task is
    /// still reported by the next poll.
    /// </summary>
    [Fact]
    public void Rebaseline_WithAnUnknownTaskId_IsANoOp()
    {
        var watch = new LivePlanEditWatch(Plan());
        watch.Poll();

        ModifyGuardrail(TaskB);
        watch.Rebaseline("99-no-such-task");

        PlanEdit edit = Assert.Single(watch.Poll());
        Assert.Equal(TaskB, edit.TaskId);
    }

    // ── U6 ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// §5.2: "Never throws: an unreadable file is skipped." The watch runs on the SCHEDULER's own thread at
    /// task dispatch and task settle, so a transient share-lock on one definition file (an editor, an
    /// indexer, antivirus) must not take a healthy run down — and must not swallow the rest of the poll
    /// either: the readable edit on the OTHER task is still reported.
    ///
    /// <para>Reproduced cross-platform the way the shipped <c>DefinitionDriftReadFailureTests</c> does — a
    /// <c>FileShare.None</c> handle on Windows, <c>chmod 000</c> on Unix. Where the environment can still
    /// read the file (e.g. running as root) the test does NOT skip: it degrades to asserting the poll
    /// works, which is the weaker half of the same sentence, rather than reporting no coverage at all.</para>
    /// </summary>
    [Fact]
    public void Poll_WithAnUnreadableFile_DoesNotThrow()
    {
        var watch = new LivePlanEditWatch(Plan());
        watch.Poll();

        ModifyGuardrail(TaskB);

        using IDisposable restore = MakeUnreadable(GuardrailPath(TaskA));

        IReadOnlyList<PlanEdit> edits = watch.Poll();

        Assert.Contains(edits, e => e.TaskId == TaskB);
    }

    // ── U7 ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The §5.2 ignore list, applied in the WATCH and NOT in <c>HashText</c>. Both halves are asserted
    /// together, because either alone is satisfied by the wrong fix: the watch stays SILENT on the five
    /// editor artifacts, while <see cref="TaskDefinitionHash"/> over the very same folder still CHANGES.
    /// Pushing the filter down into <c>HashText.EnumerateFolderFiles</c> would make the second assertion
    /// fail — and would move every recorded definition hash in every plan, turning the next resume of each
    /// into a definition-drift halt.
    /// </summary>
    [Fact]
    public void Poll_IgnoresEditorArtifacts_DsStoreThumbsDbSwpOrigRej()
    {
        var watch = new LivePlanEditWatch(Plan());
        watch.Poll();

        string hashBefore = TaskDefinitionHash.Compute(Task(TaskB));

        string guardrailsDir = Path.Combine(TaskDir(TaskB), "guardrails");
        File.WriteAllText(Path.Combine(guardrailsDir, ".DS_Store"), "mac finder junk\n");
        File.WriteAllText(Path.Combine(guardrailsDir, "Thumbs.db"), "windows explorer junk\n");
        File.WriteAllText(Path.Combine(guardrailsDir, "01-check.ps1.swp"), "vim swap\n");
        File.WriteAllText(Path.Combine(guardrailsDir, "01-check.ps1.orig"), "merge leftover\n");
        File.WriteAllText(Path.Combine(guardrailsDir, "01-check.ps1.rej"), "merge reject\n");

        Assert.Empty(watch.Poll());

        // The other half: the HASH still counts them. The watch is strictly quieter than the hash BY
        // DESIGN — anything the hash sees and the watch ignores is a pre-existing drift condition that the
        // resume-time check already owns.
        Assert.NotEqual(hashBefore, TaskDefinitionHash.Compute(Task(TaskB)));
    }

    // ── U8 ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>logs/</c> and <c>state/</c> are not in <see cref="TaskDefinitionFiles.Enumerate"/> — the
    /// structural reason (§5.2) the harness's own constant writes into the plan folder cannot trigger a
    /// watch that exists to report HUMAN edits. Asserted at both levels: the enumeration itself yields no
    /// such label, and a poll after writing into all four locations is empty.
    /// </summary>
    [Fact]
    public void Poll_IgnoresLogsAndState_TheHarnessOwnWritesUnderThePlanFolder()
    {
        var watch = new LivePlanEditWatch(Plan());
        watch.Poll();

        // The harness's own writes: the plan-level run journal + log site, and (the sharper case) the same
        // two folder NAMES directly inside a task folder, which the enumeration must still not reach —
        // only task.json, the resolved action, guardrails/** and preflights/** define a task.
        Write(Path.Combine(_planDir, "state", "run.json"), "{ \"runId\": \"r\" }\n");
        Write(Path.Combine(_planDir, "logs", "2026-08-31T00-00-00Z-abcd", "run.log"), "harness output\n");
        Write(Path.Combine(TaskDir(TaskB), "state", "fragment.json"), "{ }\n");
        Write(Path.Combine(TaskDir(TaskB), "logs", "attempt-1.log"), "attempt output\n");

        IReadOnlyList<string> labels = TaskDefinitionFiles.Enumerate(Task(TaskB))
            .Select(f => Normalize(f.Label))
            .ToList();
        Assert.DoesNotContain(labels, l => l.StartsWith("logs/", StringComparison.Ordinal));
        Assert.DoesNotContain(labels, l => l.StartsWith("state/", StringComparison.Ordinal));

        Assert.Empty(watch.Poll());
    }

    // ── U8..U11 — issue #568: a wave the run JIT-AUTHORED mid-run ────────────────────────────────

    /// <summary>
    /// The DEFECT, stated as a pin. A JIT wave's tasks are not in the plan the watch was constructed from,
    /// and both <see cref="LivePlanEditWatch.Poll"/> and <see cref="LivePlanEditWatch.Rebaseline"/> iterate
    /// that plan's task list — so an operator edit inside the freshly-authored folder is not merely missed,
    /// it is UNREACHABLE, and no amount of re-baselining changes that. This is why issue #568's candidate
    /// fix "re-baseline the watch" could not have worked, and why <see cref="LivePlanEditWatch.Rebase"/>
    /// replaces the plan instead.
    /// </summary>
    [Fact]
    public void WithoutRebase_AnEditInsideAJitAuthoredFolderIsUnreachable_NotMerelyMissed()
    {
        var watch = new LivePlanEditWatch(Plan()); // the plan as LOADED: TaskA + TaskB only
        watch.Poll();

        WriteTaskFolder(JitTask);   // the harness's own breakdown output, mid-run
        ModifyGuardrail(JitTask);   // …and then an operator edit inside it

        Assert.Empty(watch.Poll());

        // Re-baselining is not the missing step: it walks the same unreachable list.
        watch.Rebaseline();
        Assert.Empty(watch.Poll());
        watch.Rebaseline(JitTask);
        Assert.Empty(watch.Poll());
    }

    /// <summary>
    /// The fix, driven in the order the Scheduler drives it: rebase at the splice, ADOPT at the next task
    /// dispatch, then report the operator's edit at the boundary after that. Exactly the sequence a real run
    /// produces — the splice is followed immediately by the wave's first dispatch, milliseconds later.
    /// </summary>
    [Fact]
    public void AfterRebase_AnOperatorEditToANewlyCoveredTaskIsReported()
    {
        var watch = new LivePlanEditWatch(Plan());
        watch.Poll();

        WriteTaskFolder(JitTask);            // the harness authors the wave
        watch.Rebase(PlanWith(JitTask));     // Scheduler, at the splice
        watch.Poll();                        // the wave's first task dispatch — adopts the new folder

        ModifyGuardrail(JitTask);            // NOW the operator edits

        PlanEdit edit = Assert.Single(watch.Poll());
        Assert.Equal(JitTask, edit.TaskId);
        Assert.Equal(GuardrailLabel, Normalize(Assert.Single(edit.Files).Label));
    }

    /// <summary>
    /// The stated cost, pinned so nobody later "fixes" it as a bug: between <see cref="LivePlanEditWatch.Rebase"/>
    /// and the next <see cref="LivePlanEditWatch.Poll"/> there is a ONE-POLL blind window, and an edit landing
    /// inside it is folded into the adoption. That window is correct to be blind — the harness itself has been
    /// writing that folder for the last thirty minutes — and it is milliseconds wide in a real run, because
    /// the wave the splice just supplied is about to drain.
    /// </summary>
    [Fact]
    public void Rebase_HasAOnePollBlindWindow_AndAnEditInsideItIsFoldedIntoTheAdoption()
    {
        var watch = new LivePlanEditWatch(Plan());
        watch.Poll();

        WriteTaskFolder(JitTask);
        watch.Rebase(PlanWith(JitTask));
        ModifyGuardrail(JitTask);            // inside the window

        Assert.Empty(watch.Poll());

        // …and the watch is live from the very next boundary on.
        ModifyGuardrail(JitTask);
        Assert.Equal(JitTask, Assert.Single(watch.Poll()).TaskId);
    }

    /// <summary>
    /// The BASELINE is left alone, so the newly-covered task's own freshly-authored files are adopted
    /// SILENTLY through the no-baseline branch that already existed for exactly this case. That branch had
    /// no producer in production until now; snapshotting inside <see cref="LivePlanEditWatch.Rebase"/>
    /// instead would leave it dead code AND duplicate the snapshot logic.
    /// </summary>
    [Fact]
    public void Rebase_AdoptsTheHarnessOwnBreakdownOutputSilently_RatherThanBlamingTheOperatorForIt()
    {
        var watch = new LivePlanEditWatch(Plan());
        watch.Poll();

        WriteTaskFolder(JitTask);
        watch.Rebase(PlanWith(JitTask));

        Assert.Empty(watch.Poll());
    }

    /// <summary>
    /// A pending edit to an ALREADY-covered task survives the rebase. Rebase must not act as a plan-wide
    /// re-baseline: an operator edit that landed before the splice is still the next poll's to report.
    /// </summary>
    [Fact]
    public void Rebase_DoesNotSwallowAPendingEditToAnAlreadyCoveredTask()
    {
        var watch = new LivePlanEditWatch(Plan());
        watch.Poll();

        ModifyGuardrail(TaskB);     // the operator edits, before the splice
        WriteTaskFolder(JitTask);
        watch.Rebase(PlanWith(JitTask));

        PlanEdit edit = Assert.Single(watch.Poll());
        Assert.Equal(TaskB, edit.TaskId);
    }

    [Fact]
    public void Rebase_RejectsANullPlan() =>
        Assert.Throws<ArgumentNullException>(() => new LivePlanEditWatch(Plan()).Rebase(null!));

    // ── fixture ──────────────────────────────────────────────────────────────────────────────────

    private string TaskDir(string id) => Path.Combine(_planDir, "tasks", id);

    private string GuardrailPath(string id) => Path.Combine(TaskDir(id), "guardrails", "01-check.ps1");

    private void WriteTaskFolder(string id)
    {
        Write(Path.Combine(TaskDir(id), "task.json"),
            $"{{ \"description\": \"{id}\", \"writeScope\": [], \"dependsOn\": [] }}\n");
        Write(Path.Combine(TaskDir(id), "action.ps1"), "exit 0\n");
        Write(GuardrailPath(id), "# catches: nothing, this is a fixture\nexit 0\n");
    }

    /// <summary>Append a line to a task's guardrail script — a byte change that leaves it still passing.</summary>
    private void ModifyGuardrail(string id) =>
        File.AppendAllText(GuardrailPath(id), "# operator edit during the run\n");

    private TaskNode Task(string id) => new()
    {
        Id = id,
        Directory = TaskDir(id),
        Description = id,
        DependsOn = [],
        Action = new ActionDefinition { Path = Path.Combine(TaskDir(id), "action.ps1"), Kind = ActionKind.Script },
        Guardrails =
        [
            new GuardrailDefinition
            {
                Name = "01-check",
                Path = GuardrailPath(id),
                Kind = ActionKind.Script
            }
        ]
    };

    private PlanDefinition Plan() => new()
    {
        PlanDirectory = _planDir,
        Workspace = _planDir,
        Config = new RunConfig { Version = 1 },
        Tasks = [Task(TaskA), Task(TaskB)]
    };

    /// <summary>The plan AFTER a mid-run splice: the loaded tasks plus a JIT-authored one (#568).</summary>
    private PlanDefinition PlanWith(string jitTaskId) => Plan() with
    {
        Tasks = [Task(TaskA), Task(TaskB), Task(jitTaskId)]
    };

    private static void Write(string path, string content)
    {
        // Always re-create the parent: a fixture folder can have been pruned by an earlier step.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Normalize(string label) => label.Replace('\\', '/');

    /// <summary>Make <paramref name="path"/> unreadable; the returned handle restores it.</summary>
    private static IDisposable MakeUnreadable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        }

        UnixFileMode original = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, UnixFileMode.None);
        return new RestoreMode(path, original);
    }

    private sealed class RestoreMode(string path, UnixFileMode mode) : IDisposable
    {
        public void Dispose()
        {
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(path, mode); }
                catch (IOException) { /* best-effort */ }
            }
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_planDir, recursive: true); }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }
}
