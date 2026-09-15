using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// End-to-end runs of the write-scope contract as the AGENT meets it, through the real composition root:
/// a real git repo, the real <see cref="GitWorktreeProvider"/>, the real <see cref="TaskExecutor"/> attempt
/// loop, and an in-process <see cref="IPromptRunner"/> standing in for the model (no tokens, no fake CLI
/// script). Every assertion reads an artifact a run leaves on disk — <c>composed-prompt.md</c>,
/// <c>feedback.md</c>, <c>run.json</c> — because the defects these pin were all invisible to a unit test of
/// one collaborator: plan 40's task 20 spent four attempts against a scope it was never shown (#706).
/// </summary>
public sealed class WriteScopeRunTests
{
    /// <summary>The lead-in line the violation feedback's allowed-path list follows (#706).</summary>
    private const string AllowedPathsLead = "This task's writeScope allows changes ONLY to:";

    private const string WriteScopeHeading = "## Write scope (harness-enforced)";

    // ── #706: the agent is shown the scope it is judged by ──────────────────────────────────────

    [Fact]
    public async Task Worktree_ComposedPrompt_ListsTheEnforcedScope_IncludingStagingDestinations_Issue706()
    {
        // The section must be generated from the ENFORCED array — after the implicit stagingOutputs
        // destinations are folded in — not from task.json's writeScope alone. A staging task is the case
        // that tells the two apart: its `.claude/` destination is writable without being declared.
        using var repo = new TempGitRepo();
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 0,
            new TaskSpec("01-skill", ["src/Skill.cs"], StagingTo: ".claude/skills/demo/"));
        var agent = new ScriptedAgent((_, _, invocation) =>
            WriteFile(invocation.Environment["GUARDRAILS_STAGING_DIR"], "skill/SKILL.md", "staged"));

        (RunReport report, _) = await RunWorktreeAsync(planDir, repo, agent);

        Assert.Equal(TaskOutcome.Succeeded, Assert.Single(report.Tasks).Outcome);
        string composed = File.ReadAllText(Path.Combine(AttemptDir(planDir, "01-skill", 1), "composed-prompt.md"));
        Assert.Equal(
            ["src/Skill.cs", ".claude/skills/demo/**", ".guardrails-staging/**"],
            ListedScope(composed));
    }

    [Fact]
    public async Task Serial_ComposedPrompt_HasNoWriteScopeSection_BecauseNothingEnforcesIt_Issue706()
    {
        // DECLARED CONTROL for the gating — green before #706 and after. No write-scope check runs in
        // serial mode, so a section headed "harness-enforced" there would be a false claim.
        using var repo = new TempGitRepo();
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 0, new TaskSpec("01-read", ["src/Read.cs"]));
        var agent = new ScriptedAgent((_, _, _) => { });

        RunReport report = await RunSerialAsync(planDir, agent);

        Assert.Equal(TaskOutcome.Succeeded, Assert.Single(report.Tasks).Outcome);
        string composed = File.ReadAllText(Path.Combine(AttemptDir(planDir, "01-read", 1), "composed-prompt.md"));
        Assert.Contains("## Output contract", composed); // not vacuous: this IS the composed prompt
        Assert.DoesNotContain("## Write scope", composed);
    }

    [Fact]
    public async Task Worktree_ViolationFeedback_ListsTheEnforcedAllowedPaths_Issue706()
    {
        // The retry feedback named the offense but never what was allowed. Attempt 1 writes a stray file;
        // attempt 2 writes only in scope and goes green — so this also stays a control once a REPEATED
        // offending path halts (#707): a different path on each attempt must keep retrying.
        using var repo = new TempGitRepo();
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 1, new TaskSpec("01-impl", ["src/Impl.cs"]));
        var agent = new ScriptedAgent((_, call, invocation) =>
        {
            if (call == 1)
            {
                WriteFile(invocation.WorkingDirectory, "docs/notes.md", "stray");
            }
            else
            {
                WriteFile(invocation.WorkingDirectory, "src/Impl.cs", "implemented");
            }
        });

        (RunReport report, _) = await RunWorktreeAsync(planDir, repo, agent);

        Assert.Equal(TaskOutcome.Succeeded, Assert.Single(report.Tasks).Outcome);
        string feedback = File.ReadAllText(Path.Combine(AttemptDir(planDir, "01-impl", 1), "feedback.md"));
        Assert.Contains("`docs/notes.md`", feedback);
        Assert.Equal(["src/Impl.cs"], BulletPathsAfter(feedback, AllowedPathsLead));
    }

    // ── #707: a scope gap the PLAN caused is halted, not retried to exhaustion ─────────────────

    [Fact]
    public async Task RepeatedOutOfScopePath_HaltsNeedsHumanOnTheSecondAttempt_NamingThePathAndTheFix_Issue707()
    {
        // Plan 40's task 20 wrote OverwatchDecision.cs out of scope on attempt 1 AND attempt 2 — unambiguous
        // after the second — and the run retried twice more. The stub here is seeded by a plain commit with no
        // Guardrails-Task: trailer, so the first-occurrence upstream rule cannot fire: this pins the REPEAT rule.
        using var repo = new TempGitRepo();
        repo.Commit("src/Stub.cs", "stub");
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 3, new TaskSpec("01-implement", ["src/Impl.cs"]));
        var agent = new ScriptedAgent((_, call, invocation) =>
            WriteFile(invocation.WorkingDirectory, "src/Stub.cs", $"implemented on call {call}"));

        (RunReport report, _) = await RunWorktreeAsync(planDir, repo, agent);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.NeedsHuman, task.Outcome);
        Assert.StartsWith("needs human: ", task.Summary);
        Assert.Contains("src/Stub.cs", task.Summary);

        var entry = JournalReader.Read(RunJournal.PathFor(planDir)).Tasks["01-implement"];
        Assert.Equal(JournalTaskStatus.NeedsHuman, entry.Status);
        Assert.Equal(
            [AttemptOutcome.WriteScopeViolation, AttemptOutcome.WriteScopeViolation],
            entry.Attempts.Select(a => a.Outcome));

        string halt = File.ReadAllText(Path.Combine(AttemptDir(planDir, "01-implement", 2), "feedback.md"));
        Assert.Contains("\"src/Stub.cs\"", halt);
        Assert.Contains(Path.Combine(planDir, "tasks", "01-implement", "task.json").Replace('\\', '/'), halt);
        // Attempt 1 was an ordinary retry: one write outside scope is not yet evidence of a plan gap.
        string retry = File.ReadAllText(Path.Combine(AttemptDir(planDir, "01-implement", 1), "feedback.md"));
        Assert.StartsWith("# Attempt 1 of task '01-implement' failed", retry);
    }

    [Fact]
    public async Task DifferentOutOfScopePathOnEachAttempt_KeepsRetrying_Issue707()
    {
        // CONTROL: the halt keys on an IDENTICAL path. Two violations on two different paths are ordinary retries,
        // and the third attempt converges.
        using var repo = new TempGitRepo();
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 2, new TaskSpec("01-implement", ["src/Impl.cs"]));
        var agent = new ScriptedAgent((_, call, invocation) =>
            WriteFile(invocation.WorkingDirectory, call switch { 1 => "docs/a.md", 2 => "docs/b.md", _ => "src/Impl.cs" }, "work"));

        (RunReport report, _) = await RunWorktreeAsync(planDir, repo, agent);

        Assert.Equal(TaskOutcome.Succeeded, Assert.Single(report.Tasks).Outcome);
        Assert.Equal(3, JournalReader.Read(RunJournal.PathFor(planDir)).Tasks["01-implement"].Attempts.Count);
    }

    [Fact]
    public async Task UpstreamAuthoredPath_WithNoInScopeChange_HaltsOnTheFirstAttempt_NamingTheUpstreamTask_Issue707()
    {
        // The plan-40 shape: the authoring task put the stub in a file the implementing task may not write. The
        // evidence is deterministic after ONE attempt — the path was last committed by a task this one depends
        // on, and this attempt changed nothing inside its own scope.
        using var repo = new TempGitRepo();
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 3,
            new TaskSpec("01-author", ["src/Stub.cs"]),
            new TaskSpec("02-implement", ["src/Impl.cs"], DependsOn: ["01-author"]));
        var agent = new ScriptedAgent((taskId, _, invocation) =>
            WriteFile(invocation.WorkingDirectory, "src/Stub.cs", taskId == "01-author" ? "stub" : "implemented"));

        (RunReport report, _) = await RunWorktreeAsync(planDir, repo, agent);

        Assert.Equal(TaskOutcome.Succeeded, report.Tasks.Single(t => t.TaskId == "01-author").Outcome);
        TaskResult implement = report.Tasks.Single(t => t.TaskId == "02-implement");
        Assert.Equal(TaskOutcome.NeedsHuman, implement.Outcome);
        Assert.Contains("01-author", implement.Summary);
        Assert.Contains("src/Stub.cs", implement.Summary);

        AttemptRecord only = Assert.Single(JournalReader.Read(RunJournal.PathFor(planDir)).Tasks["02-implement"].Attempts);
        Assert.Equal(AttemptOutcome.WriteScopeViolation, only.Outcome);
        string halt = File.ReadAllText(Path.Combine(AttemptDir(planDir, "02-implement", 1), "feedback.md"));
        Assert.Contains("`01-author`", halt);
        Assert.Contains("\"src/Stub.cs\"", halt);

        // #705: the halt is where a human widens the scope, so the work the revert took is kept for them.
        string outOfScope = Path.Combine(AttemptDir(planDir, "02-implement", 1), "out-of-scope.patch");
        Assert.True(File.Exists(outOfScope), "the out-of-scope work must be kept for the human the halt asks");
        Assert.Contains("implemented", File.ReadAllText(outOfScope));
        Assert.Contains(outOfScope.Replace('\\', '/'), halt);
    }

    [Fact]
    public async Task UpstreamAuthoredPath_WithInScopeWorkToo_IsRetriedFirst_ThenHaltsOnTheRepeat_Issue707()
    {
        // CONTROL for the first-occurrence rule's in-scope condition. An implementing task that did real in-scope
        // work AND edited an upstream-authored file looks exactly like one editing a protected upstream TEST
        // file — a mistake a retry corrects. So it is retried; only the repeat halts.
        using var repo = new TempGitRepo();
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 3,
            new TaskSpec("01-author", ["src/Stub.cs"]),
            new TaskSpec("02-implement", ["src/Impl.cs"], DependsOn: ["01-author"]));
        var agent = new ScriptedAgent((taskId, _, invocation) =>
        {
            if (taskId == "01-author")
            {
                WriteFile(invocation.WorkingDirectory, "src/Stub.cs", "stub");
                return;
            }

            WriteFile(invocation.WorkingDirectory, "src/Impl.cs", "in-scope work");
            WriteFile(invocation.WorkingDirectory, "src/Stub.cs", "and an edit to the upstream file");
        });

        (RunReport report, _) = await RunWorktreeAsync(planDir, repo, agent);

        Assert.Equal(TaskOutcome.NeedsHuman, report.Tasks.Single(t => t.TaskId == "02-implement").Outcome);
        Assert.Equal(2, JournalReader.Read(RunJournal.PathFor(planDir)).Tasks["02-implement"].Attempts.Count);
    }

    [Fact]
    public async Task PathLastCommittedByATaskThisOneDoesNotDependOn_IsNotAPlanGap_Issue707()
    {
        // CONTROL for the ancestry condition, varying ONLY ancestry. The stub is committed under a real trailer
        // naming 01-other with 01-other's own loaded definition hash — everything the rule trusts, except that
        // 01-other is not a dependency of 02. (Two independent tasks cannot set this up through a run: an
        // independent task forks from the run's base, not from a sibling's commit.) Retried first, halted on the repeat.
        using var repo = new TempGitRepo();
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 3,
            new TaskSpec("01-other", ["src/Other.cs"]),
            new TaskSpec("02-implement", ["src/Impl.cs"]));
        repo.Commit("src/Stub.cs", "stub",
            $"stub\n\nGuardrails-Task: 01-other\nGuardrails-Run: earlier\nGuardrails-Task-Hash: {LoadedDefinitionHash(planDir, "01-other")}");
        var agent = ImplementerEditingTheStub(out Func<bool> sawTheStub);

        (RunReport report, _) = await RunWorktreeAsync(planDir, repo, agent);

        Assert.True(sawTheStub(), "not vacuous: 02-implement must MODIFY a committed src/Stub.cs, not add one");
        Assert.Equal(TaskOutcome.NeedsHuman, report.Tasks.Single(t => t.TaskId == "02-implement").Outcome);
        Assert.Equal(2, JournalReader.Read(RunJournal.PathFor(planDir)).Tasks["02-implement"].Attempts.Count);
    }

    // ── #705: out-of-scope work stays recoverable, and salvage says only what is true ──────────

    [Fact]
    public async Task OutOfScopeWork_IsKeptAsAPatch_AndTheFeedbackNeverClaimsItWasSaved_Issue705()
    {
        // Plan 40, reproduced. Attempt 1 puts ALL of its work in a file outside the scope. The revert takes it, yet
        // the salvage snapshot taken afterwards is not empty: that snapshot stages into a fresh throwaway index,
        // which re-hashes every file, so a CRLF-committed file under core.autocrlf reads as changed there and
        // nowhere else. The feedback then said "SAVED, not lost" over a patch of line-ending churn. The seeded lock
        // file recreates exactly that condition, on every OS.
        using var repo = new TempGitRepo();
        repo.Commit("src/Stub.cs", "stub");
        repo.Commit("data/packages.lock.json", "{\r\n  \"version\": 1\r\n}\r\n");
        TempGitRepo.Git(repo.RepoPath, "config", "core.autocrlf", "true");
        AssertASnapshotOfTheUntouchedTreeIsNotEmpty(repo); // not vacuous: plan 40's salvage condition is present

        string planDir = WritePlan(repo.RepoPath, defaultRetries: 1, new TaskSpec("01-implement", ["src/Impl.cs"]));
        var agent = new ScriptedAgent((_, call, invocation) =>
            WriteFile(invocation.WorkingDirectory,
                call == 1 ? "src/Stub.cs" : "src/Impl.cs",
                call == 1 ? "RESOLVE-BODY: the working implementation" : "implemented in scope"));

        (RunReport report, _) = await RunWorktreeAsync(planDir, repo, agent);

        Assert.Equal(TaskOutcome.Succeeded, Assert.Single(report.Tasks).Outcome);
        string attempt1 = AttemptDir(planDir, "01-implement", 1);

        // Item 1: the working implementation survives the revert, as a patch beside the attempt's other artifacts.
        string outOfScope = Path.Combine(attempt1, "out-of-scope.patch");
        Assert.True(File.Exists(outOfScope), "the out-of-scope bytes must be kept before the revert destroys them");
        Assert.Contains("RESOLVE-BODY: the working implementation", File.ReadAllText(outOfScope));

        // Item 2: nothing in scope changed, so nothing is offered as salvage and nothing is claimed saved.
        string feedback = File.ReadAllText(Path.Combine(attempt1, "feedback.md"));
        Assert.DoesNotContain("SAVED, not lost", feedback);
        Assert.DoesNotContain("## Prior attempt work is salvageable", feedback);
        Assert.False(File.Exists(Path.Combine(attempt1, "prior-attempt.patch")),
            "a snapshot holding no in-scope work must not be offered as salvage");
        Assert.Contains(outOfScope.Replace('\\', '/'), feedback);
    }

    [Fact]
    public async Task InScopeWorkBesideAStrayWrite_IsStillSaved_AndOnlyTheStrayIsKeptAsOutOfScope_Issue705()
    {
        // CONTROL: in-scope work in a violating attempt is still salvaged and still offered, and the out-of-scope
        // copy holds the stray write only — never the in-scope work the retry may recover.
        using var repo = new TempGitRepo();
        string planDir = WritePlan(repo.RepoPath, defaultRetries: 1, new TaskSpec("01-implement", ["src/Impl.cs"]));
        var agent = new ScriptedAgent((_, call, invocation) =>
        {
            WriteFile(invocation.WorkingDirectory, "src/Impl.cs", call == 1 ? "partial in-scope work" : "finished");
            if (call == 1)
            {
                WriteFile(invocation.WorkingDirectory, "docs/notes.md", "a stray note");
            }
        });

        (RunReport report, _) = await RunWorktreeAsync(planDir, repo, agent);

        Assert.Equal(TaskOutcome.Succeeded, Assert.Single(report.Tasks).Outcome);
        string attempt1 = AttemptDir(planDir, "01-implement", 1);
        string feedback = File.ReadAllText(Path.Combine(attempt1, "feedback.md"));
        Assert.Contains("SAVED, not lost", feedback);
        Assert.Contains("## Prior attempt work is salvageable", feedback);
        Assert.Contains("partial in-scope work", File.ReadAllText(Path.Combine(attempt1, "prior-attempt.patch")));

        string outOfScope = Path.Combine(attempt1, "out-of-scope.patch");
        Assert.True(File.Exists(outOfScope), "the stray write must be kept as out-of-scope work");
        Assert.Contains("a stray note", File.ReadAllText(outOfScope));
        Assert.DoesNotContain("partial in-scope work", File.ReadAllText(outOfScope));
    }

    /// <summary>
    /// The precondition that made plan 40's salvage lie: a salvage snapshot of a tree nobody has touched is NOT
    /// empty. Taken before the plan folder is written, so the only thing it can show is the seeded lock file.
    /// </summary>
    private static void AssertASnapshotOfTheUntouchedTreeIsNotEmpty(TempGitRepo repo)
    {
        const string probeRef = "refs/guardrails/probe/attempt-1";
        string head = TempGitRepo.Git(repo.RepoPath, "rev-parse", "HEAD").Trim();
        GitWorktreeProvider.PreserveAttemptToRef(repo.RepoPath, probeRef);
        string stat = GitWorktreeProvider.DiffStatAgainstBase(repo.RepoPath, head, probeRef);
        TempGitRepo.Git(repo.RepoPath, "update-ref", "-d", probeRef);
        Assert.Contains("data/packages.lock.json", stat);
    }

    /// <summary>
    /// An agent for which every task but <c>02-implement</c> writes nothing, and <c>02-implement</c> edits ONLY
    /// <c>src/Stub.cs</c>. <paramref name="sawTheStub"/> reports whether that file already existed when it did —
    /// the proof a control is exercising a MODIFIED upstream file rather than an added one.
    /// </summary>
    private static ScriptedAgent ImplementerEditingTheStub(out Func<bool> sawTheStub)
    {
        bool saw = false;
        sawTheStub = () => saw;
        return new ScriptedAgent((taskId, _, invocation) =>
        {
            if (taskId != "02-implement")
            {
                return;
            }

            saw |= File.Exists(Path.Combine(invocation.WorkingDirectory, "src", "Stub.cs"));
            WriteFile(invocation.WorkingDirectory, "src/Stub.cs", "implemented");
        });
    }

    /// <summary>The definition hash a run of <paramref name="planDir"/> loads for <paramref name="taskId"/>.</summary>
    private static string LoadedDefinitionHash(string planDir, string taskId)
    {
        string? hash = new PlanLoader().Load(planDir).Plan!.Tasks.Single(t => t.Id == taskId).DefinitionHashAtLoad;
        Assert.False(string.IsNullOrEmpty(hash), $"the loader must pin a definition hash for {taskId}");
        return hash!;
    }

    [Fact]
    public void LastCommitTaskTrailer_ReadsTheLatestCommitTouchingThePath_AndOnlyARealTrailerBlock_Issue707()
    {
        // The fact the first-occurrence rule reads: WHICH task last committed a path, from the commit's own
        // trailer block — the same attribution discipline the resume pre-pass uses.
        using var repo = new TempGitRepo();
        repo.Commit("src/Stub.cs", "stub",
            "author the stub\n\nGuardrails-Task: 01-author\nGuardrails-Run: run-1\nGuardrails-Task-Hash: sha256:abc");
        repo.Commit("src/Other.cs", "other", "unrelated\n\nGuardrails-Task: 03-other\nGuardrails-Run: run-1");
        string head = TempGitRepo.Git(repo.RepoPath, "rev-parse", "HEAD").Trim();

        Assert.Equal(("01-author", "sha256:abc"),
            GitWorktreeProvider.LastCommitTaskTrailer(repo.RepoPath, head, "src/Stub.cs"));

        // Controls: a hand fix that merely MENTIONS a trailer in prose is not attribution, and a path no commit
        // ever touched has no author at all.
        repo.Commit("src/Stub.cs", "hand fix", "hand fix; see Guardrails-Task: 01-author for context");
        head = TempGitRepo.Git(repo.RepoPath, "rev-parse", "HEAD").Trim();
        Assert.Equal(((string?)null, (string?)null),
            GitWorktreeProvider.LastCommitTaskTrailer(repo.RepoPath, head, "src/Stub.cs"));
        Assert.Equal(((string?)null, (string?)null),
            GitWorktreeProvider.LastCommitTaskTrailer(repo.RepoPath, head, "src/Never.cs"));
    }

    // ── fixture: plan, agent, runs ─────────────────────────────────────────────────────────────

    /// <summary>One prompt task: its id, declared writeScope, dependencies, and an optional staging destination.</summary>
    private sealed record TaskSpec(string Id, string[] WriteScope, string[]? DependsOn = null, string? StagingTo = null);

    /// <summary>
    /// Stands in for the model. <see cref="_act"/> receives the task id (read from the invocation's own
    /// <c>GUARDRAILS_TASK_ID</c>), the 1-based invocation count for THAT task, and the invocation itself —
    /// whose <see cref="PromptInvocation.WorkingDirectory"/> is the effective workspace, so a write there is
    /// exactly what an agent's file-editing tool would have produced. Writes no state fragment.
    /// </summary>
    private sealed class ScriptedAgent(Action<string, int, PromptInvocation> act) : IPromptRunner
    {
        private readonly Action<string, int, PromptInvocation> _act = act;
        private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);

        public string Name => "scripted-agent";

        public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken)
        {
            string taskId = invocation.Environment["GUARDRAILS_TASK_ID"];
            int call;
            lock (_calls)
            {
                call = _calls[taskId] = _calls.GetValueOrDefault(taskId) + 1;
            }

            _act(taskId, call, invocation);
            return Task.FromResult(new PromptResult { Completed = true, IsError = false, Summary = "scripted agent done" });
        }
    }

    /// <summary>
    /// A plan of prompt tasks inside <paramref name="repoPath"/>, each guarded by one always-pass script
    /// (the write-scope check runs BEFORE guardrails, so it — not a guardrail — is what these tests observe).
    /// </summary>
    private static string WritePlan(string repoPath, int defaultRetries, params TaskSpec[] tasks)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));

        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            $$"""
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": {{defaultRetries}},
              "maxParallelism": 1,
              "promptRunners": {
                "default": "scripted",
                "scripted": { "command": "scripted-agent", "maxTurns": 3 }
              }
            }
            """);

        foreach (TaskSpec spec in tasks)
        {
            string taskDir = Path.Combine(planDir, "tasks", spec.Id);
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

            string scopeJson = string.Join(", ", spec.WriteScope.Select(p => $"\"{p}\""));
            string dependsJson = string.Join(", ", (spec.DependsOn ?? []).Select(d => $"\"{d}\""));
            string stagingJson = spec.StagingTo is null
                ? ""
                : $",\n  \"stagingOutputs\": [ {{ \"from\": \"skill/**\", \"to\": \"{spec.StagingTo}\" }} ]";
            File.WriteAllText(Path.Combine(taskDir, "task.json"),
                $$"""
                {
                  "description": "scripted prompt task {{spec.Id}}",
                  "dependsOn": [{{dependsJson}}],
                  "writeScope": [{{scopeJson}}]{{stagingJson}}
                }
                """);
            File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Do the work inside your write scope.\n");

            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-ok.ps1"), "exit 0\n");
            }
            else
            {
                string guardrail = Path.Combine(taskDir, "guardrails", "01-ok.sh");
                File.WriteAllText(guardrail, "#!/usr/bin/env bash\nexit 0\n");
                File.SetUnixFileMode(guardrail,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
        }

        return planDir;
    }

    private static async Task<(RunReport Report, RunJournal Journal)> RunWorktreeAsync(
        string planDir, TempGitRepo repo, IPromptRunner agent)
    {
        (TaskExecutor executor, RunJournal journal, Core.Model.PlanDefinition plan) = BuildExecutor(planDir, agent);
        var scheduler = new Scheduler(plan, executor, journal,
            worktreeProvider: new GitWorktreeProvider(repo.RepoPath, repo.WorktreeRoot),
            reVerifier: new AlwaysPassReVerifier());
        return (await scheduler.RunAsync(plan, TestContext.Current.CancellationToken), journal);
    }

    private static async Task<RunReport> RunSerialAsync(string planDir, IPromptRunner agent)
    {
        (TaskExecutor executor, RunJournal journal, Core.Model.PlanDefinition plan) = BuildExecutor(planDir, agent);
        return await new Scheduler(plan, executor, journal).RunAsync(plan, TestContext.Current.CancellationToken);
    }

    private static (TaskExecutor, RunJournal, Core.Model.PlanDefinition) BuildExecutor(string planDir, IPromptRunner agent)
    {
        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        Core.Model.PlanDefinition plan = load.Plan!;
        var stateManager = new StateManager(plan.PlanDirectory);
        stateManager.Initialize();
        RunJournal journal = RunJournal.LoadOrCreate(plan);
        var executor = new TaskExecutor(
            plan, new ProcessRunner(), new InterpreterMap(new PathExecutableProbe(), plan.Config.Interpreters),
            stateManager, journal, IRunObserver.Null, PromptRunnerRegistry.Build(plan.Config, _ => agent));
        return (executor, journal, plan);
    }

    private sealed class AlwaysPassReVerifier : IReVerifier
    {
        public Task<ReVerifyResult> ReVerifyAsync(
            string worktreePath,
            IReadOnlyList<Core.Model.GuardrailDefinition> guardrails,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReVerifyResult { Passed = true });
    }

    // ── fixture: reading what the run left behind ──────────────────────────────────────────────

    /// <summary>The absolute log dir of <paramref name="taskId"/>'s attempt <paramref name="attempt"/>, as journaled.</summary>
    private static string AttemptDir(string planDir, string taskId, int attempt)
    {
        AttemptRecord record = JournalReader.Read(RunJournal.PathFor(planDir)).Tasks[taskId].Attempts[attempt - 1];
        return Path.Combine(planDir, record.LogDir.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>The backticked entries listed under the composed prompt's write-scope section (fails when absent).</summary>
    private static List<string> ListedScope(string composed)
    {
        int start = composed.IndexOf(WriteScopeHeading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"expected a '{WriteScopeHeading}' section:\n{composed}");
        int next = composed.IndexOf("\n## ", start + WriteScopeHeading.Length, StringComparison.Ordinal);
        string section = next < 0 ? composed[start..] : composed[start..next];
        return section.Replace("\r\n", "\n").Split('\n')
            .Where(line => line.StartsWith("- `", StringComparison.Ordinal))
            .Select(line => line[3..line.IndexOf('`', 3)])
            .ToList();
    }

    /// <summary>The backticked paths of the bullet run that immediately follows <paramref name="lead"/>.</summary>
    private static List<string> BulletPathsAfter(string text, string lead)
    {
        int at = text.IndexOf(lead, StringComparison.Ordinal);
        Assert.True(at >= 0, $"expected '{lead}' in:\n{text}");
        return text[(at + lead.Length)..].Replace("\r\n", "\n").Split('\n')
            .SkipWhile(line => line.Length == 0)
            .TakeWhile(line => line.StartsWith("- `", StringComparison.Ordinal))
            .Select(line => line[3..line.IndexOf('`', 3)])
            .ToList();
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        string full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;
        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-wsrun-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            WorktreeRoot = Path.Combine(_root, "worktrees");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(WorktreeRoot);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            // Pinned so a segment worktree checks out exactly the committed bytes on every OS.
            Git(RepoPath, "config", "core.autocrlf", "false");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# write-scope run test");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        /// <summary>Commit <paramref name="content"/> at <paramref name="relativePath"/> on the checked-out branch.</summary>
        public void Commit(string relativePath, string content, string message = "seed")
        {
            WriteFile(RepoPath, relativePath, content);
            Git(RepoPath, "add", "--", relativePath);
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
            using var proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return proc.ExitCode == 0
                ? stdout
                : throw new InvalidOperationException(
                    $"git {string.Join(" ", args)} (in {workingDir}) exited {proc.ExitCode}: {stderr.Trim()}");
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    foreach (string f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                        File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch { /* best-effort teardown */ }
        }
    }
}
