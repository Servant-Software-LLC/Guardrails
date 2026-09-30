using System.Text;
using Guardrails.Cli.Commands;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Prompts;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #816, the adversarial review's findings on the serial-mode snapshot, pinned against real git: the
/// stderr-pipe deadlock under <c>core.autocrlf=true</c>, byte-exact reverts whatever the line-ending config and
/// attributes say, non-ASCII paths, the operator's own index and HEAD left untouched, loud "not checked" reporting,
/// the halts that now revert in serial mode, and the resume that reconciles an attempt that never ended.
/// </summary>
public sealed partial class WriteScopeRunTests
{
    // ── BLOCKER 1: the stderr pipe deadlock ─────────────────────────────────────────────────────

    [Fact]
    public void Snapshot_AndCheck_OfManyLfFiles_UnderAutocrlfTrue_Finish_Issue816()
    {
        // Under core.autocrlf=true, `git add` prints a ~110-byte line-ending warning per LF file. Reading stdout to its
        // end before stderr deadlocked once ~35 of them filled the stderr pipe; 60 untracked files hung the snapshot
        // forever. 100 here, on a dedicated thread with a bound, so a regression FAILS instead of hanging the suite.
        using var repo = new TempGitRepo();
        TempGitRepo.Git(repo.RepoPath, "config", "core.autocrlf", "true");
        TempGitRepo.Git(repo.RepoPath, "config", "core.safecrlf", "warn");

        Exception? failure = null;
        int offending = -1;
        CancellationToken token = Ct;
        var worker = new Thread(() =>
        {
            try
            {
                using ScopeDiffBase snapshot = ScopeDiffBase.TryCaptureSerial(repo.RepoPath, repo.RepoPath, out string? why, token)
                    ?? throw new InvalidOperationException($"snapshot failed: {why}");
                for (int i = 0; i < 100; i++)
                {
                    WriteBytes(repo.RepoPath, $"gen/file{i:D3}.txt", "line one\nline two\n");
                }

                WriteScopeCheckResult check = WriteScopeCheck.Check(snapshot, ["src/**"], token);
                offending = check.OffendingPaths.Count;
                WriteScopeCheck.ScopedRevert(snapshot, check.OffendingPaths, token);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        worker.Start();

        Assert.True(worker.Join(TimeSpan.FromMinutes(3)), "the snapshot/check/revert of 100 LF files hung (stderr pipe deadlock)");
        Assert.Null(failure);
        Assert.Equal(100, offending);
        Assert.False(Directory.Exists(Path.Combine(repo.RepoPath, "gen")) &&
                     Directory.EnumerateFiles(Path.Combine(repo.RepoPath, "gen")).Any());
    }

    // ── line endings: normal-mode snapshot, byte-exact restore (#816 second review) ─────────────────
    //
    // Every fixture file is made NON-RACY — its mtime set an hour back BEFORE git first sees it — so git's index
    // holds a clean stat entry and a stat-clean file is NOT re-hashed at snapshot time. That is exactly the case the
    // first raw-bytes design got wrong (a normalised seeded blob beside raw re-hashed ones); a fixture that rewrites
    // the file just before the snapshot is stat-dirty and cannot see it.

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATrackedCrlfFile_TouchedWithIdenticalBytes_IsNotAnOffense_Issue816(bool eolAttributes)
    {
        using var repo = LineEndingRepo(eolAttributes, ("docs/crlf.txt", "line1\r\nline2\r\n"));
        using ScopeDiffBase snapshot = ScopeDiffBase.TryCaptureSerial(repo.RepoPath, repo.RepoPath, out string? why, Ct)
            ?? throw new InvalidOperationException(why);

        WriteBytes(repo.RepoPath, "docs/crlf.txt", "line1\r\nline2\r\n"); // re-saved: same bytes, new mtime

        Assert.Empty(WriteScopeCheck.Check(snapshot, ["src/**"], Ct).OffendingPaths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATrackedCrlfFile_EditedOutOfScope_IsRevertedToItsCrlfBytes_Issue816(bool eolAttributes)
    {
        using var repo = LineEndingRepo(eolAttributes, ("docs/crlf.txt", "line1\r\nline2\r\n"));
        using ScopeDiffBase snapshot = ScopeDiffBase.TryCaptureSerial(repo.RepoPath, repo.RepoPath, out string? why, Ct)
            ?? throw new InvalidOperationException(why);

        WriteBytes(repo.RepoPath, "docs/crlf.txt", "rewritten\r\n");
        WriteScopeCheckResult check = WriteScopeCheck.Check(snapshot, ["src/**"], Ct);
        Assert.Equal(["docs/crlf.txt"], check.OffendingPaths.Select(o => o.Path));
        WriteScopeCheck.ScopedRevert(snapshot, check.OffendingPaths, Ct);

        Assert.Equal(Encoding.UTF8.GetBytes("line1\r\nline2\r\n"), File.ReadAllBytes(Path.Combine(repo.RepoPath, "docs", "crlf.txt")));
    }

    [Theory]
    [InlineData(false, "alpha\nbeta\n")]
    [InlineData(true, "alpha\nbeta\n")]
    [InlineData(false, "alpha\r\nbeta\r\n")]
    public void AnUntrackedFile_EditedOutOfScope_IsRevertedToItsRawBytes_Issue816(bool eolAttributes, string original)
    {
        using var repo = LineEndingRepo(eolAttributes);
        WriteNonRacy(repo.RepoPath, "scratch/untracked.txt", original); // never added: untracked in the real index
        using ScopeDiffBase snapshot = ScopeDiffBase.TryCaptureSerial(repo.RepoPath, repo.RepoPath, out string? why, Ct)
            ?? throw new InvalidOperationException(why);

        WriteBytes(repo.RepoPath, "scratch/untracked.txt", "rewritten by the attempt\n");
        WriteScopeCheckResult check = WriteScopeCheck.Check(snapshot, ["src/**"], Ct);
        Assert.Equal(["scratch/untracked.txt"], check.OffendingPaths.Select(o => o.Path));
        WriteScopeCheck.ScopedRevert(snapshot, check.OffendingPaths, Ct);

        Assert.Equal(Encoding.UTF8.GetBytes(original), File.ReadAllBytes(Path.Combine(repo.RepoPath, "scratch", "untracked.txt")));
    }

    /// <summary>A repo under <c>core.autocrlf=true</c> (optionally also <c>* text=auto eol=crlf</c>) with committed, NON-RACY files.</summary>
    private static TempGitRepo LineEndingRepo(bool eolAttributes, params (string Path, string Content)[] tracked)
    {
        var repo = new TempGitRepo();
        TempGitRepo.Git(repo.RepoPath, "config", "core.autocrlf", "true");
        var paths = new List<string>();
        if (eolAttributes)
        {
            WriteNonRacy(repo.RepoPath, ".gitattributes", "* text=auto eol=crlf\n");
            paths.Add(".gitattributes");
        }

        foreach ((string path, string content) in tracked)
        {
            WriteNonRacy(repo.RepoPath, path, content);
            paths.Add(path);
        }

        if (paths.Count > 0)
        {
            TempGitRepo.Git(repo.RepoPath, ["add", "--", .. paths]);
            TempGitRepo.Git(repo.RepoPath, "commit", "-m", "seed");
        }

        return repo;
    }

    private static void WriteNonRacy(string root, string relativePath, string content)
    {
        WriteBytes(root, relativePath, content);
        File.SetLastWriteTimeUtc(
            Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)), DateTime.UtcNow.AddHours(-1));
    }
    // ── WEAK 4: non-ASCII paths ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ANonAsciiPath_IsJudgedByItsRealName_InScopeAndOut_Issue816()
    {
        using var repo = new TempGitRepo();
        repo.Commit("docs/résumé.md", "original");
        using ScopeDiffBase snapshot = ScopeDiffBase.TryCaptureSerial(repo.RepoPath, repo.RepoPath, out string? why, Ct)
            ?? throw new InvalidOperationException(why);

        WriteBytes(repo.RepoPath, "docs/café.md", "in scope");
        WriteBytes(repo.RepoPath, "notes/café.md", "out of scope");
        WriteBytes(repo.RepoPath, "docs/résumé.md", "edited in scope");

        WriteScopeCheckResult check = WriteScopeCheck.Check(snapshot, ["docs/"], Ct);

        Assert.Equal(["notes/café.md"], check.OffendingPaths.Select(o => o.Path));
        Assert.Contains("docs/café.md", check.InScopePaths);
        Assert.Contains("docs/résumé.md", check.InScopePaths);

        WriteScopeCheck.ScopedRevert(snapshot, check.OffendingPaths, Ct);
        Assert.False(File.Exists(Path.Combine(repo.RepoPath, "notes", "café.md")));
        Assert.Equal("in scope", File.ReadAllText(Path.Combine(repo.RepoPath, "docs", "café.md")));

        // And an out-of-scope MODIFY of a non-ASCII tracked file is restored, not left behind.
        using ScopeDiffBase second = ScopeDiffBase.TryCaptureSerial(repo.RepoPath, repo.RepoPath, out why, Ct)
            ?? throw new InvalidOperationException(why);
        WriteBytes(repo.RepoPath, "docs/résumé.md", "edited out of scope");
        WriteScopeCheckResult outOfScope = WriteScopeCheck.Check(second, ["src/"], Ct);
        Assert.Equal(["docs/résumé.md"], outOfScope.OffendingPaths.Select(o => o.Path));
        WriteScopeCheck.ScopedRevert(second, outOfScope.OffendingPaths, Ct);
        Assert.Equal("edited in scope", File.ReadAllText(Path.Combine(repo.RepoPath, "docs", "résumé.md")));
    }

    // ── the operator's index and HEAD are never touched ──────────────────────────────────────────

    [Fact]
    public void SnapshotCheckAndRevert_LeaveTheRealIndexBytesAndHeadUnchanged_Issue816()
    {
        using var repo = new TempGitRepo();
        repo.Commit("src/Tracked.cs", "tracked");
        // The operator has something STAGED of their own — the index is not merely HEAD's.
        WriteBytes(repo.RepoPath, "src/Staged.cs", "staged by the operator");
        TempGitRepo.Git(repo.RepoPath, "add", "--", "src/Staged.cs");

        string indexPath = Path.Combine(repo.RepoPath, ".git", "index");
        byte[] indexBefore = File.ReadAllBytes(indexPath);
        string headBefore = TempGitRepo.Git(repo.RepoPath, "rev-parse", "HEAD").Trim();

        using (ScopeDiffBase snapshot = ScopeDiffBase.TryCaptureSerial(repo.RepoPath, repo.RepoPath, out string? why, Ct)
            ?? throw new InvalidOperationException(why))
        {
            WriteBytes(repo.RepoPath, "src/Tracked.cs", "changed");
            WriteBytes(repo.RepoPath, "stray.txt", "stray");
            WriteScopeCheckResult check = WriteScopeCheck.Check(snapshot, ["lib/"], Ct);
            WriteScopeCheck.CaptureOffendingPatch(snapshot, check.OffendingPaths);
            WriteScopeCheck.ScopedRevert(snapshot, check.OffendingPaths, Ct);
        }

        Assert.Equal(indexBefore, File.ReadAllBytes(indexPath));
        Assert.Equal(headBefore, TempGitRepo.Git(repo.RepoPath, "rev-parse", "HEAD").Trim());
        Assert.Equal("tracked", File.ReadAllText(Path.Combine(repo.RepoPath, "src", "Tracked.cs")));
        Assert.False(File.Exists(Path.Combine(repo.RepoPath, "stray.txt")));
    }

    // ── Q1: serial wording does not flatly blame the agent ───────────────────────────────────────

    [Fact]
    public async Task Serial_TheRevertReport_SaysTheChangeMayNotBeTheAgents_AndTheRepeatHaltToo_Issue816()
    {
        using var repo = new TempGitRepo();
        repo.Commit(UpstreamTest, "original upstream test");
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 3, new TaskSpec("02-implement", ["src/Impl.cs"]));
        var agent = new ScriptedAgent(
            (_, call, invocation) => WriteFile(invocation.WorkingDirectory, UpstreamTest, $"changed on call {call}"),
            outcome: (_, _) => PromptFailureKind.MaxTurns);

        RunReport report = await RunSerialAsync(planDir, agent);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.NeedsHuman, task.Outcome);
        string retry = File.ReadAllText(Path.Combine(AttemptDir(planDir, "02-implement", 1), "feedback.md"));
        Assert.Contains("changed during this attempt", retry);
        Assert.Contains("may include edits made outside the agent", retry);
        string halt = File.ReadAllText(Path.Combine(AttemptDir(planDir, "02-implement", 2), "feedback.md"));
        Assert.Contains("may include edits made outside the agent", halt);
    }

    [Fact]
    public async Task Serial_TheRevertSummaryClause_SaysChangedDuringTheAttempt_Issue816()
    {
        using var repo = new TempGitRepo();
        repo.Commit(UpstreamTest, "original upstream test");
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 0, new TaskSpec("02-implement", ["src/Impl.cs"]));
        var agent = new ScriptedAgent(
            (_, _, invocation) => WriteFile(invocation.WorkingDirectory, UpstreamTest, "changed"),
            outcome: (_, _) => PromptFailureKind.MaxTurns);

        TaskResult task = Assert.Single((await RunSerialAsync(planDir, agent)).Tasks);

        Assert.Contains($"changed during the attempt outside writeScope, reverted: {UpstreamTest}", task.Summary);
        Assert.DoesNotContain(".;", task.Summary);
    }

    [Fact]
    public void SerialRunStartNote_WarnsThatEditsDuringTheRunAreReverted_Issue816()
    {
        string note = RunCommand.SerialWriteScopeNote(unavailableReason: null);
        Assert.Contains("Do not edit it while the run is going", note);
        Assert.Contains("REVERTED", note);
        Assert.Contains("out-of-scope.patch", note);
        Assert.Contains("will NOT be checked", RunCommand.SerialWriteScopeNote("the workspace 'x' is not a git work tree"));
    }

    // ── Q2a: serial halts revert too, and name what they reverted ────────────────────────────────

    [Fact]
    public async Task Serial_ANeedsHumanEscalation_RevertsOutOfScopeChanges_AndNamesThem_Issue816()
    {
        using var repo = new TempGitRepo();
        repo.Commit(UpstreamTest, "original upstream test");
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 2, new TaskSpec("02-implement", ["src/Impl.cs"]));
        var agent = new ScriptedAgent((_, _, invocation) =>
        {
            WriteFile(invocation.WorkingDirectory, "src/Impl.cs", "partial");
            WriteFile(invocation.WorkingDirectory, UpstreamTest, "weakened upstream test");
            string stateOut = invocation.Environment["GUARDRAILS_STATE_OUT"];
            Directory.CreateDirectory(Path.GetDirectoryName(stateOut)!);
            File.WriteAllText(stateOut, """{ "needsHuman": { "question": "the tests cannot pass", "kind": "blocked-work" } }""");
        });

        RunReport report = await RunSerialAsync(planDir, agent);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.NeedsHuman, task.Outcome);
        Assert.Contains(UpstreamTest, task.Summary);
        Assert.Equal("original upstream test", File.ReadAllText(Path.Combine(repo.RepoPath, "tests", "UpstreamTests.cs")));
        Assert.Equal("partial", File.ReadAllText(Path.Combine(repo.RepoPath, "src", "Impl.cs")));
        string escalation = File.ReadAllText(Path.Combine(AttemptDir(planDir, "02-implement", 1), "feedback.md"));
        Assert.Contains(RevertedHeading, escalation);
        Assert.Contains($"`{UpstreamTest}`", escalation);
    }

    // ── WEAK B: an attempt that never ended is REPORTED at run start, never reverted ──────────────

    [Fact]
    public async Task RunStart_AnInterruptedSerialAttempt_IsReportedNotReverted_AndHandedToTheTasksFirstAttempt_Issue816()
    {
        using var repo = new TempGitRepo();
        repo.Commit(UpstreamTest, "original upstream test");
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 0, new TaskSpec("02-implement", ["src/Impl.cs"]));

        // Run 1: the attempt writes out of scope, then the harness dies under it (an exception out of the runner is an
        // infrastructure fault that aborts the run) — its end-of-attempt check never runs.
        var dying = new ScriptedAgent((_, _, invocation) =>
        {
            WriteFile(invocation.WorkingDirectory, "src/Impl.cs", "partial");
            WriteFile(invocation.WorkingDirectory, UpstreamTest, "weakened upstream test");
            throw new InvalidOperationException("simulated harness death mid-attempt");
        });
        await RunSerialAsync(planDir, dying);
        Assert.False(string.IsNullOrEmpty(
            JournalReader.Read(RunJournal.PathFor(planDir)).Tasks["02-implement"].ScopeSnapshotTree));

        // Run 2: before ANY dispatch, the difference is reported — and left in place.
        var observer = new ScopeEventObserver();
        string? testSeen = null;
        string? previousFeedback = null;
        var resumed = new ScriptedAgent((_, _, invocation) =>
        {
            observer.Events.Add("attempt");
            testSeen = File.ReadAllText(Path.Combine(invocation.WorkingDirectory, "tests", "UpstreamTests.cs"));
            previousFeedback = invocation.Environment.GetValueOrDefault("GUARDRAILS_FEEDBACK") is { } fb && File.Exists(fb)
                ? File.ReadAllText(fb)
                : null;
            WriteFile(invocation.WorkingDirectory, "src/Impl.cs", "done");
        });
        (TaskExecutor executor, RunJournal journal, Core.Model.PlanDefinition plan) = BuildExecutor(planDir, resumed, observer);
        RunReport report = await new Scheduler(plan, executor, journal).RunAsync(plan, Ct);

        Assert.Equal(TaskOutcome.Succeeded, Assert.Single(report.Tasks).Outcome);
        Assert.Equal(
            ["interrupted:tests/UpstreamTests.cs", "attempt"],
            observer.Events.Where(e => !e.StartsWith("finished:", StringComparison.Ordinal)));
        Assert.Equal("weakened upstream test", testSeen); // NOT reverted: it may be a human's fix since
        Assert.NotNull(previousFeedback);
        Assert.Contains("was interrupted and never checked", previousFeedback);
        Assert.Contains($"`{UpstreamTest}`", previousFeedback);
        Assert.Contains("Do NOT build on them", previousFeedback);
        Assert.Contains("weakened upstream test", File.ReadAllText(observer.PatchPath!));
        Assert.Null(JournalReader.Read(RunJournal.PathFor(planDir)).Tasks["02-implement"].ScopeSnapshotTree);
    }

    [Fact]
    public void RunStart_AStaleSnapshotInWorktreeMode_IsClearedWithoutAnyReport_Issue816()
    {
        using var repo = new TempGitRepo();
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 0, new TaskSpec("01-impl", ["src/Impl.cs"]));
        var observer = new ScopeEventObserver();
        (TaskExecutor executor, RunJournal journal, _) = BuildExecutor(planDir, new ScriptedAgent((_, _, _) => { }), observer);
        journal.SetScopeSnapshotTree("01-impl", "4b825dc642cb6eb9a060e54bf8d69288fbee4904");

        executor.PrepareRun(worktreeMode: true);

        Assert.Empty(observer.Events);
        Assert.Null(JournalReader.Read(RunJournal.PathFor(planDir)).Tasks["01-impl"].ScopeSnapshotTree);
    }

    [Fact]
    public void RunStart_AnUnreadableStaleSnapshot_IsLoud_AndCleared_Issue816()
    {
        using var repo = new TempGitRepo();
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 0, new TaskSpec("01-impl", ["src/Impl.cs"]));
        var observer = new ScopeEventObserver();
        (TaskExecutor executor, RunJournal journal, _) = BuildExecutor(planDir, new ScriptedAgent((_, _, _) => { }), observer);
        journal.SetScopeSnapshotTree("01-impl", "0123456789012345678901234567890123456789");

        executor.PrepareRun(worktreeMode: false);

        Assert.Contains(observer.Events, e => e.StartsWith("not-checked:", StringComparison.Ordinal));
        Assert.Null(JournalReader.Read(RunJournal.PathFor(planDir)).Tasks["01-impl"].ScopeSnapshotTree);
    }

    [Fact]
    public async Task ASettledAttempt_NeverCarriesTheSnapshotField_Issue816()
    {
        // DECLARED CONTROL: an ordinary completed run leaves no snapshot behind, so a resume has nothing to report.
        using var repo = new TempGitRepo();
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 0, new TaskSpec("01-impl", ["src/Impl.cs"]));
        var agent = new ScriptedAgent((_, _, invocation) => WriteFile(invocation.WorkingDirectory, "src/Impl.cs", "done"));

        Assert.Equal(TaskOutcome.Succeeded, Assert.Single((await RunSerialAsync(planDir, agent)).Tasks).Outcome);
        Assert.Null(JournalReader.Read(RunJournal.PathFor(planDir)).Tasks["01-impl"].ScopeSnapshotTree);
        Assert.DoesNotContain("scopeSnapshotTree", File.ReadAllText(RunJournal.PathFor(planDir)));
    }

    // ── WEAK A: the facts ride the attempt RECORD, not only text written after it ─────────────────

    [Fact]
    public async Task TheAttemptRecord_CarriesTheRevertedPaths_ForAnUnfinishedAttemptAndASerialHalt_Issue816()
    {
        using var repo = new TempGitRepo();
        repo.Commit(UpstreamTest, "original upstream test");
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 1, new TaskSpec("02-implement", ["src/Impl.cs"]));
        var observer = new ScopeEventObserver();
        var agent = new ScriptedAgent(
            (_, call, invocation) =>
            {
                WriteFile(invocation.WorkingDirectory, call == 1 ? UpstreamTest : "docs/stray.md", "out of scope");
                if (call == 2)
                {
                    string stateOut = invocation.Environment["GUARDRAILS_STATE_OUT"];
                    Directory.CreateDirectory(Path.GetDirectoryName(stateOut)!);
                    File.WriteAllText(stateOut, """{ "needsHuman": "stuck" }""");
                }
            },
            outcome: (_, call) => call == 1 ? PromptFailureKind.MaxTurns : PromptFailureKind.None);
        (TaskExecutor executor, RunJournal journal, Core.Model.PlanDefinition plan) = BuildExecutor(planDir, agent, observer);

        await new Scheduler(plan, executor, journal).RunAsync(plan, Ct);

        IReadOnlyList<AttemptRecord> attempts = JournalReader.Read(RunJournal.PathFor(planDir)).Tasks["02-implement"].Attempts;
        Assert.Equal([UpstreamTest], attempts[0].ScopeRevertedPaths);
        Assert.Equal(AttemptOutcome.NeedsHuman, attempts[1].Outcome);
        Assert.Equal(["docs/stray.md"], attempts[1].ScopeRevertedPaths);
        // And the live AttemptFinished event saw the same record, not a copy built before the revert.
        Assert.Equal([$"finished:1:{UpstreamTest}", "finished:2:docs/stray.md"],
            observer.Events.Where(e => e.StartsWith("finished:", StringComparison.Ordinal)));
    }

    /// <summary>Records the #816 observer events in order (and each attempt's reverted paths as AttemptFinished saw them).</summary>
    private sealed class ScopeEventObserver : IRunObserver
    {
        public List<string> Events { get; } = [];

        public string? PatchPath { get; private set; }

        public void TaskStarting(Core.Model.TaskNode task) { }

        public void TaskFinished(TaskResult result) { }

        public void GuardrailFinished(Core.Model.TaskNode task, GuardrailResult result) { }

        public void AttemptFinished(Core.Model.TaskNode task, AttemptRecord record) =>
            Events.Add($"finished:{record.Attempt}:{string.Join(",", record.ScopeRevertedPaths ?? [])}");

        public void InterruptedAttemptChangesFound(
            Core.Model.TaskNode task, IReadOnlyList<WriteScopeOffense> paths, string? patchPath)
        {
            PatchPath = patchPath;
            Events.Add("interrupted:" + string.Join(",", paths.Select(p => p.Path)));
        }

        public void WriteScopeNotChecked(Core.Model.TaskNode task, int attempt, string reason) =>
            Events.Add("not-checked:" + reason);
    }

    // ── WEAK 6: "not checked" is loud ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Serial_AnEndOfAttemptGitError_IsLoud_InTheSummaryAndTheAttemptLog_Issue816()
    {
        // The attempt breaks the repository's config (every later git call then fails), and ends on its turn cap — so
        // the end-of-attempt check cannot run. That must be SAID, not swallowed.
        using var repo = new TempGitRepo();
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 0, new TaskSpec("01-impl", ["src/Impl.cs"]));
        var agent = new ScriptedAgent(
            (_, _, invocation) => File.AppendAllText(
                Path.Combine(invocation.WorkingDirectory, ".git", "config"), "[core]\n\tfilemode = notabool\n"),
            outcome: (_, _) => PromptFailureKind.MaxTurns);

        RunReport report = await RunSerialAsync(planDir, agent);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Contains("write scope NOT checked this attempt", task.Summary);
        string log = File.ReadAllText(Path.Combine(AttemptDir(planDir, "01-impl", 1), "write-scope-check.log"));
        Assert.Contains("NOT checked", log);
        Assert.Contains("git error", log);
        // And run.json's attempt record carries it — it was known before the journaler built the record.
        Assert.Contains("git error",
            JournalReader.Read(RunJournal.PathFor(planDir)).Tasks["01-impl"].Attempts[0].WriteScopeNotChecked);
    }

    [Fact]
    public void AppendClause_NeverJoinsAFullStopToASemicolon_Issue816()
    {
        Assert.Equal("guardrails skipped; x", TaskExecutor.AppendClause("guardrails skipped", "x"));
        Assert.Equal("If it recurs: lower the context; x", TaskExecutor.AppendClause("If it recurs: lower the context.", "x"));
        Assert.Equal("x", TaskExecutor.AppendClause(null, "x"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void WriteBytes(string root, string relativePath, string content)
    {
        string full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, Encoding.UTF8.GetBytes(content));
    }
}
