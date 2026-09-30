using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Prompts;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #816, end to end: writeScope is enforced at the end of EVERY attempt — not only after an action that
/// succeeded — and in serial mode as well as worktree mode.
///
/// <para>The dogfood run behind it: an implement task edited its upstream task's test files across attempts that
/// each ended in a timeout or a turn cap. The write-scope check ran only after a SUCCESSFUL action, so it never
/// saw those edits; the retry feedback said the partial work was preserved and to continue from it; and the run
/// was serial (<c>maxParallelism: 1</c>), where no write-scope check ran at all. The implementer ended up grading
/// itself against tests it had rewritten.</para>
/// </summary>
public sealed partial class WriteScopeRunTests
{
    private const string UpstreamTest = "tests/UpstreamTests.cs";
    private const string RevertedHeading = "## Out-of-scope writes were reverted";

    [Theory]
    [InlineData(PromptFailureKind.MaxTurns)]
    [InlineData(PromptFailureKind.Timeout)]
    public async Task Serial_AnUnfinishedAttemptThatWroteOutOfScope_IsRevertedBeforeTheRetry_AndNamedInItsFeedback_Issue816(
        PromptFailureKind failure)
    {
        using var repo = new TempGitRepo();
        repo.Commit(UpstreamTest, "original upstream test");
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 1, new TaskSpec("02-implement", ["src/Impl.cs"]));

        string? testSeenByRetry = null;
        string? implSeenByRetry = null;
        var agent = new ScriptedAgent(
            (_, call, invocation) =>
            {
                if (call == 1)
                {
                    WriteFile(invocation.WorkingDirectory, "src/Impl.cs", "partial implementation");
                    WriteFile(invocation.WorkingDirectory, UpstreamTest, "weakened upstream test");
                    return;
                }

                testSeenByRetry = File.ReadAllText(Path.Combine(invocation.WorkingDirectory, "tests", "UpstreamTests.cs"));
                implSeenByRetry = File.ReadAllText(Path.Combine(invocation.WorkingDirectory, "src", "Impl.cs"));
                WriteFile(invocation.WorkingDirectory, "src/Impl.cs", "finished implementation");
            },
            outcome: (_, call) => call == 1 ? failure : PromptFailureKind.None);

        RunReport report = await RunSerialAsync(planDir, agent);

        Assert.Equal(TaskOutcome.Succeeded, Assert.Single(report.Tasks).Outcome);
        // Before the retry ran, the out-of-scope edit was gone and the in-scope partial work was still there —
        // serial mode carries the partial work forward, now minus what the scope forbids.
        Assert.Equal("original upstream test", testSeenByRetry);
        Assert.Equal("partial implementation", implSeenByRetry);

        var entry = JournalReader.Read(RunJournal.PathFor(planDir)).Tasks["02-implement"];
        Assert.Equal(
            failure == PromptFailureKind.MaxTurns ? AttemptOutcome.MaxTurns : AttemptOutcome.Timeout,
            entry.Attempts[0].Outcome);

        string attempt1 = AttemptDir(planDir, "02-implement", 1);
        string feedback = File.ReadAllText(Path.Combine(attempt1, "feedback.md"));
        string section = feedback[feedback.IndexOf(RevertedHeading, StringComparison.Ordinal)..];
        Assert.Contains($"`{UpstreamTest}`", section);
        Assert.Contains("outside this task's writeScope", section, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["src/Impl.cs"], BulletPathsAfter(section, AllowedPathsLead));
        Assert.DoesNotContain("`src/Impl.cs` (", section); // the in-scope partial work is never named as reverted

        // #705: the reverted bytes were kept, for a human.
        string kept = File.ReadAllText(Path.Combine(attempt1, "out-of-scope.patch"));
        Assert.Contains("weakened upstream test", kept);
        Assert.Equal("original upstream test", File.ReadAllText(Path.Combine(repo.RepoPath, "tests", "UpstreamTests.cs")));
    }

    [Fact]
    public async Task Worktree_AMaxTurnsAttemptThatWroteOutOfScope_IsNamedInItsFeedback_AndKeptOutOfItsSalvage_Issue816()
    {
        // Worktree mode resets the whole segment between attempts, so the edit never reached the next attempt's
        // TREE — but it reached its SALVAGE: the stash of the rolled-back attempt carried the rewritten test, and
        // the feedback pointed the agent at that stash to recover its work.
        using var repo = new TempGitRepo();
        repo.Commit(UpstreamTest, "original upstream test");
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 1, new TaskSpec("02-implement", ["src/Impl.cs"]));
        var agent = new ScriptedAgent(
            (_, call, invocation) =>
            {
                WriteFile(invocation.WorkingDirectory, "src/Impl.cs", $"implementation {call}");
                if (call == 1)
                {
                    WriteFile(invocation.WorkingDirectory, UpstreamTest, "weakened upstream test");
                }
            },
            outcome: (_, call) => call == 1 ? PromptFailureKind.MaxTurns : PromptFailureKind.None);

        (RunReport report, _) = await RunWorktreeAsync(planDir, repo, agent);

        Assert.Equal(TaskOutcome.Succeeded, Assert.Single(report.Tasks).Outcome);
        string attempt1 = AttemptDir(planDir, "02-implement", 1);
        string feedback = File.ReadAllText(Path.Combine(attempt1, "feedback.md"));
        Assert.Contains(RevertedHeading, feedback);
        Assert.Contains($"`{UpstreamTest}`", feedback[feedback.IndexOf(RevertedHeading, StringComparison.Ordinal)..]);

        // The salvage still offers the in-scope work, and only that.
        string salvage = File.ReadAllText(Path.Combine(attempt1, "prior-attempt.patch"));
        Assert.Contains("src/Impl.cs", salvage);
        Assert.DoesNotContain(UpstreamTest, salvage);
        Assert.Contains("weakened upstream test", File.ReadAllText(Path.Combine(attempt1, "out-of-scope.patch")));
    }

    [Fact]
    public async Task Serial_TheSameUpstreamFileWrittenOnTwoUnfinishedAttempts_HaltsNeedsHuman_InsteadOfRetrying_Issue816()
    {
        // #707's repeat rule, now reachable from attempts that never finished: the second write to the same
        // out-of-scope file is a scope gap no retry can clear, so the task halts rather than burning its budget.
        using var repo = new TempGitRepo();
        repo.Commit(UpstreamTest, "original upstream test");
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 3, new TaskSpec("02-implement", ["src/Impl.cs"]));
        var agent = new ScriptedAgent(
            (_, call, invocation) => WriteFile(invocation.WorkingDirectory, UpstreamTest, $"weakened on call {call}"),
            outcome: (_, _) => PromptFailureKind.MaxTurns);

        RunReport report = await RunSerialAsync(planDir, agent);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.NeedsHuman, task.Outcome);
        Assert.Contains(UpstreamTest, task.Summary);

        var entry = JournalReader.Read(RunJournal.PathFor(planDir)).Tasks["02-implement"];
        Assert.Equal(JournalTaskStatus.NeedsHuman, entry.Status);
        Assert.Equal([AttemptOutcome.MaxTurns, AttemptOutcome.WriteScopeViolation], entry.Attempts.Select(a => a.Outcome));
        Assert.Equal("original upstream test", File.ReadAllText(Path.Combine(repo.RepoPath, "tests", "UpstreamTests.cs")));
    }

    [Theory]
    [InlineData("thrash", false)]
    [InlineData("exhausted", false)]
    [InlineData("silent", false)]
    [InlineData("config", false)]
    [InlineData("thrash", true)]
    [InlineData("exhausted", true)]
    [InlineData("silent", true)]
    [InlineData("config", true)]
    public async Task TheUnfinishedActionHalts_ReportTheOutOfScopeWritesTheyReverted_InBothModes_Issue817(
        string kind, bool worktreeMode)
    {
        // The #817 repeated-thrash halt, the #800 repeated-exhaustion halt, the #815 repeated-silent-stall halt and the
        // #767 runner-configuration halt (which settles on its FIRST attempt) settle an action that never finished, so
        // it never reaches the phase-1 write-scope check. In BOTH modes they run the end-of-attempt check themselves:
        // the halting attempt's out-of-scope write is kept in out-of-scope.patch, recorded as scopeRevertedPaths, named
        // once in its feedback and in its summary.
        (string summaryNames, string feedbackHeading) = kind switch
        {
            "thrash" => ("CONTEXT THRASH (9 compactions in 76 turns)", "halted: its context ran out twice in a row"),
            "exhausted" => ("second consecutive attempt that ran out of context", "halted: its context ran out twice in a row"),
            "silent" => ("runner produced no output at all", "halted: the runner produced nothing, twice"),
            _ => ("a runner-configuration fault no retry can clear", "halted: the prompt runner's configuration cannot do this work")
        };
        int haltAttempt = kind == "config" ? 1 : 2;
        using var repo = new TempGitRepo();
        repo.Commit(UpstreamTest, "original upstream test");
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 3, new TaskSpec("02-implement", ["src/Impl.cs"]));
        var agent = new ScriptedAgent(
            (_, call, invocation) =>
            {
                WriteFile(invocation.WorkingDirectory, "src/Impl.cs", $"implementation {call}");
                if (call == haltAttempt)
                {
                    WriteFile(invocation.WorkingDirectory, UpstreamTest, "weakened upstream test");
                }
            },
            outcome: (_, _) => kind switch
            {
                "thrash" => PromptFailureKind.MaxTurns,
                "exhausted" => PromptFailureKind.ContextExhausted,
                "config" => PromptFailureKind.RunnerConfiguration,
                _ => PromptFailureKind.Stalled
            },
            // A runner-REPORTED turn count: a thrash verdict on an estimate would be advisory and never halt.
            compactions: (_, _) => kind == "thrash" ? new CompactionCounts(9, 0) : null,
            turns: (_, _) => kind == "thrash" ? 76 : null);

        RunReport report = worktreeMode
            ? (await RunWorktreeAsync(planDir, repo, agent)).Report
            : await RunSerialAsync(planDir, agent);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.NeedsHuman, task.Outcome);
        Assert.Contains(summaryNames, task.Summary);
        Assert.Contains(UpstreamTest, task.Summary);
        Assert.DoesNotContain(".;", task.Summary);

        var attempts = JournalReader.Read(RunJournal.PathFor(planDir)).Tasks["02-implement"].Attempts;
        Assert.Equal(haltAttempt, attempts.Count);
        Assert.Contains(UpstreamTest, attempts[^1].ScopeRevertedPaths ?? []);

        string attemptDir = AttemptDir(planDir, "02-implement", haltAttempt);
        string feedback = File.ReadAllText(Path.Combine(attemptDir, "feedback.md"));
        Assert.Contains(feedbackHeading, feedback);
        int section = feedback.IndexOf(RevertedHeading, StringComparison.Ordinal);
        Assert.True(section >= 0, feedback);
        Assert.Equal(section, feedback.LastIndexOf(RevertedHeading, StringComparison.Ordinal)); // named once, not twice
        Assert.Contains($"`{UpstreamTest}`", feedback[section..]);
        Assert.Contains("weakened upstream test", File.ReadAllText(Path.Combine(attemptDir, "out-of-scope.patch")));
        Assert.Equal("original upstream test", File.ReadAllText(Path.Combine(repo.RepoPath, "tests", "UpstreamTests.cs")));
    }

    [Fact]
    public async Task Serial_ASucceededActionThatWroteOutOfScope_FailsTheWriteScopeCheck_AndIsReverted_Issue816()
    {
        // Serial mode used to run no write-scope check at all: the same write that fails a worktree attempt
        // went green here.
        using var repo = new TempGitRepo();
        repo.Commit(UpstreamTest, "original upstream test");
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 0, new TaskSpec("02-implement", ["src/Impl.cs"]));
        var agent = new ScriptedAgent((_, _, invocation) =>
        {
            WriteFile(invocation.WorkingDirectory, "src/Impl.cs", "implementation");
            WriteFile(invocation.WorkingDirectory, UpstreamTest, "weakened upstream test");
        });

        RunReport report = await RunSerialAsync(planDir, agent);

        Assert.NotEqual(TaskOutcome.Succeeded, Assert.Single(report.Tasks).Outcome);
        var entry = JournalReader.Read(RunJournal.PathFor(planDir)).Tasks["02-implement"];
        Assert.Equal([AttemptOutcome.WriteScopeViolation], entry.Attempts.Select(a => a.Outcome));
        Assert.Equal("original upstream test", File.ReadAllText(Path.Combine(repo.RepoPath, "tests", "UpstreamTests.cs")));
        // Only the offending path was reverted: the in-scope work survives in the operator's checkout.
        Assert.Equal("implementation", File.ReadAllText(Path.Combine(repo.RepoPath, "src", "Impl.cs")));
    }

    [Fact]
    public async Task Serial_EachAttemptIsJudgedAgainstTheTreeItStartedFrom_NotAgainstEarlierTasksWork_Issue816()
    {
        // CONTROL: serial tasks write into the operator's checkout without committing, so the diff base is a
        // snapshot taken as each attempt starts. An earlier task's uncommitted in-scope work — and the harness's
        // own logs/ and state/ under the plan folder, which sits inside this workspace — must never read as an
        // offense for the task after it.
        using var repo = new TempGitRepo();
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 0,
            new TaskSpec("01-first", ["src/First.cs"]),
            new TaskSpec("02-second", ["src/Second.cs"], DependsOn: ["01-first"]));
        var agent = new ScriptedAgent((taskId, _, invocation) =>
            WriteFile(invocation.WorkingDirectory, taskId == "01-first" ? "src/First.cs" : "src/Second.cs", taskId));

        RunReport report = await RunSerialAsync(planDir, agent);

        Assert.All(report.Tasks, t => Assert.Equal(TaskOutcome.Succeeded, t.Outcome));
        Assert.Equal("01-first", File.ReadAllText(Path.Combine(repo.RepoPath, "src", "First.cs")));
        Assert.Equal("02-second", File.ReadAllText(Path.Combine(repo.RepoPath, "src", "Second.cs")));
    }

    [Fact]
    public async Task Serial_ACancelledAttemptThatWroteOutOfScope_IsRevertedBeforeItIsJournaledForResume_Issue816()
    {
        // A cancelled attempt is journaled back to pending and a later resume continues from the serial workspace
        // as it stands, so its out-of-scope writes must not be left there for the resumed attempt to inherit.
        using var repo = new TempGitRepo();
        repo.Commit(UpstreamTest, "original upstream test");
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 1, new TaskSpec("02-implement", ["src/Impl.cs"]));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var agent = new ScriptedAgent((_, _, invocation) =>
        {
            WriteFile(invocation.WorkingDirectory, "src/Impl.cs", "partial implementation");
            WriteFile(invocation.WorkingDirectory, UpstreamTest, "weakened upstream test");
            cts.Cancel();
        });

        (TaskExecutor executor, RunJournal journal, Core.Model.PlanDefinition plan) = BuildExecutor(planDir, agent);
        await new Scheduler(plan, executor, journal).RunAsync(plan, cts.Token);

        Assert.Equal("original upstream test", File.ReadAllText(Path.Combine(repo.RepoPath, "tests", "UpstreamTests.cs")));
        Assert.Equal("partial implementation", File.ReadAllText(Path.Combine(repo.RepoPath, "src", "Impl.cs")));
        var entry = JournalReader.Read(RunJournal.PathFor(planDir)).Tasks["02-implement"];
        Assert.NotEqual(JournalTaskStatus.Succeeded, entry.Status);
    }

    [Fact]
    public async Task Serial_ComposedPrompt_ListsTheEnforcedScope_Issue816()
    {
        // Supersedes #706's serial control: serial mode now enforces the scope, so the agent is shown it.
        using var repo = new TempGitRepo();
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 0, new TaskSpec("01-read", ["src/Read.cs"]));
        var agent = new ScriptedAgent((_, _, _) => { });

        RunReport report = await RunSerialAsync(planDir, agent);

        TaskResult only = Assert.Single(report.Tasks);
        Assert.True(only.Outcome == TaskOutcome.Succeeded, $"{only.Outcome}: {only.Summary} {report.Abort?.Headline}");
        string composed = File.ReadAllText(Path.Combine(AttemptDir(planDir, "01-read", 1), "composed-prompt.md"));
        Assert.Equal(["src/Read.cs"], ListedScope(composed));
    }

    [Fact]
    public void SerialSnapshot_IsAvailableOnlyAtAGitTopLevel_Issue816()
    {
        using var repo = new TempGitRepo();
        string sub = Path.Combine(repo.RepoPath, "nested");
        Directory.CreateDirectory(sub);

        Assert.Null(ScopeDiffBase.SerialUnavailableReason(repo.RepoPath));
        Assert.Contains("not the top level", ScopeDiffBase.SerialUnavailableReason(sub));
    }

    [Fact]
    public async Task Serial_OutsideAGitWorkTree_TheScopeIsNotCheckedRetrospectively_AndTheAttemptSaysSo_Issue816()
    {
        // The limit, made loud: with no git work tree there is nothing to diff against. The run still goes (the
        // write-time hook still refuses an out-of-scope file edit on a hook-capable runner), and each attempt's
        // log dir records that the retrospective check did not run and why.
        string root = Path.Combine(Path.GetTempPath(), "gr-wsrun-nogit-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            string planDir = WritePlan(root, defaultRetries: 0, new TaskSpec("01-impl", ["src/Impl.cs"]));
            var agent = new ScriptedAgent((_, _, invocation) =>
                WriteFile(invocation.WorkingDirectory, UpstreamTest, "unchecked"));

            RunReport report = await RunSerialAsync(planDir, agent);

            Assert.Equal(TaskOutcome.Succeeded, Assert.Single(report.Tasks).Outcome);
            string note = File.ReadAllText(Path.Combine(AttemptDir(planDir, "01-impl", 1), "write-scope-check.log"));
            Assert.Contains("NOT checked", note);
            Assert.Contains("not a git work tree", note);
            string composed = File.ReadAllText(Path.Combine(AttemptDir(planDir, "01-impl", 1), "composed-prompt.md"));
            Assert.DoesNotContain("## Write scope (harness-enforced)", composed);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { /* best-effort */ }
        }
    }
}
