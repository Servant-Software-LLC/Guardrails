using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests;

/// <summary>
/// End-to-end tests for retry salvage (issues #195 / #306): a worktree-mode task's rolled-back
/// non-final attempt is STASHED to an inspectable git ref + an applyable patch file BEFORE the F2
/// <c>reset --hard</c> discards it, and the NEXT attempt's <c>feedback.md</c> exposes the stash (ref +
/// patch + <c>git diff --stat</c> summary) so the retry can pull all/some/none. Issue #306 supersedes
/// #195's scope guard: salvage now fires for EVERY non-final worktree failure — including the
/// <b>guardrail-fail</b> path (with per-guardrail verdicts), not only <c>max-turns</c>/<c>output-cap</c>.
/// Covers that stash is opt-out (<c>preserveAttemptsForSalvage: false</c>), that a salvaged
/// out-of-writeScope file is still caught by the (unchanged) write-scope check, and ref pruning on task
/// settle-succeeded / <c>--fresh</c>.
/// </summary>
/// <remarks>
/// <para><b>Issue #253 — this class WAS the confirmed leaker.</b> A real dogfood run saw
/// <c>outside.txt</c> and <c>src/output.txt</c> attributed to unrelated tasks with zero trace in their
/// own transcripts; those are the exact literals <see cref="WriteFakeCli"/> writes. Rooting every
/// fixture path under <see cref="Path.GetTempPath"/> was NOT sufficient, because the fake CLI resolved
/// its write targets from the <b>environment</b> (<c>$GUARDRAILS_WORKSPACE</c>, <c>$GUARDRAILS_STATE_OUT</c>)
/// and its <b>cwd</b> — both of which the harness sets per invocation, but which a child INHERITS from
/// the enclosing process whenever the harness does not. <c>NeedsHumanTriage</c> invokes the prompt
/// runner with a deliberately EMPTY env, so when this suite runs inside an outer <c>guardrails run</c>
/// (a plan preflight doing <c>dotnet test</c>) the triage invocation of the fake CLI inherited the OUTER
/// run's <c>GUARDRAILS_WORKSPACE</c> — its <c>_integration</c> worktree — and wrote the fixture files
/// there. The outer write-scope check's <c>git add -A</c> then blamed whichever agent ran next.</para>
/// <para>The fix is in <see cref="WriteFakeCli"/>: the fake writes only when it can positively prove it
/// is THIS fixture's task action. Regression proofs:
/// <see cref="FakeCli_WritesNothingOutsideThisFixturesAction_Issue253"/> (red before the fix) and
/// <see cref="FakeCli_StillWritesForThisFixturesAction_Issue253"/> (guards against "fixing" the leak by
/// neutering the fake).</para>
/// <para><see cref="HostRepoCleanlinessGuard"/> (an <see cref="IClassFixture{T}"/>) remains as the
/// belt-and-braces tripwire around the whole class.</para>
/// </remarks>
public sealed class RetrySalvageTests : IClassFixture<HostRepoCleanlinessGuard>, IDisposable
{
    private static readonly bool Windows = OperatingSystem.IsWindows();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gr-salvage-" + Guid.NewGuid().ToString("N"));
    private readonly string _repoPath;
    private readonly string _planDir;
    private readonly string _counterPath;

    private const string RefAttempt1 = "refs/guardrails/01-implement/attempt-1";

    /// <summary>
    /// Name of the fixture-private proof-of-origin variable the plan injects into the TASK ACTION's
    /// environment (SSOT §3 <c>action.env</c>). Deliberately OUTSIDE the <c>GUARDRAILS_</c> namespace —
    /// it is fixture plumbing, not part of the §5.1 contract, and it must be a name no real harness
    /// (inner or outer) ever sets.
    /// </summary>
    private const string ActionTokenVar = "GR_SALVAGE_FIXTURE_ACTION";

    /// <summary>
    /// A per-instance value for <see cref="ActionTokenVar"/>. Unguessable and unique per test instance,
    /// so the ambient environment of an enclosing <c>guardrails run</c> can never match it (issue #253).
    /// </summary>
    private readonly string _actionToken = Guid.NewGuid().ToString("N");

    private enum FakeMode
    {
        /// <summary>Attempt 1 hits max-turns (writing an in-scope file first); attempt 2+ succeeds.</summary>
        MaxTurnsThenSucceed,

        /// <summary>Every attempt hits max-turns (never succeeds) — for the run-end pruning test.</summary>
        MaxTurnsForever,

        /// <summary>Every attempt succeeds cleanly but the GUARDRAIL is what fails (script always exit 1).</summary>
        AlwaysSucceedActionOnly,

        /// <summary>
        /// Like <see cref="AlwaysSucceedActionOnly"/>, but the failing guardrail is named
        /// <c>01-test-files-pristine</c> — a PROTECTED-ARTIFACT (tests-untouched-class) check whose name the
        /// old bare-<c>"untouched"</c> substring MISSED. #306 WEAK-1: this attempt must NOT be stashed
        /// (gamed-artifact work is unrecoverable via salvage, not merely un-advertised).
        /// </summary>
        ProtectedArtifactGuardrailFails,

        /// <summary>
        /// Attempt 1 hits max-turns; attempt 2+ succeeds but ALSO writes an out-of-scope file, modeling
        /// a bad salvage adoption that must still be caught by the (unchanged) write-scope check.
        /// </summary>
        MaxTurnsThenSucceedWithBadScope
    }

    public RetrySalvageTests()
    {
        _repoPath = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repoPath);
        InitRepo(_repoPath);

        _planDir = Path.Combine(_repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(_planDir, "state"));
        Directory.CreateDirectory(Path.Combine(_planDir, "tasks"));

        // OUTSIDE the repo entirely: the fake CLI's invocation counter must survive the segment's
        // F2 `git reset --hard` + `clean -fd` between attempt 1 (max-turns) and attempt 2 (succeeds).
        _counterPath = Path.Combine(_root, "invocations.count");
    }

    public void Dispose() => SafeDeleteTree(_root);

    private async Task<RunReport> RunAsync(FakeMode mode, bool preserveAttemptsForSalvage = true)
    {
        WritePlan(mode, preserveAttemptsForSalvage);
        PlanLoadResult load = new PlanLoader().Load(_planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));

        Scheduler scheduler = SchedulerFactory.Create(
            load.Plan!, new ProcessRunner(), new PathExecutableProbe(), IRunObserver.Null);
        return await scheduler.RunAsync(load.Plan!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MaxTurnsRollback_PreservesRef_AndNextAttemptFeedbackNamesIt_WithDiffStat()
    {
        RunReport report = await RunAsync(FakeMode.MaxTurnsThenSucceed);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.Succeeded, task.Outcome);

        // The task went on to SUCCEED, so its salvage ref is pruned on settle (proven separately by
        // SalvageRefs_PrunedOnTaskSettleSucceeded) — the ref's mid-run existence + content is instead
        // proven by MaxTurnsRollback_RefContainsThePreservedAttempt (a task that never succeeds, so the
        // ref survives to be inspected). Here we assert what a human/agent would actually SEE: attempt
        // 2's feedback.md (attempt 1's failure feedback, read by attempt 2) names the ref and carries a
        // diff-stat summary of what attempt 1 changed.
        string feedbackPath = Path.Combine(AttemptDir(1), "feedback.md");
        Assert.True(File.Exists(feedbackPath), "expected attempt-1 feedback.md");
        string feedback = File.ReadAllText(feedbackPath);

        Assert.Contains("## Prior attempt work is salvageable", feedback);
        Assert.Contains(RefAttempt1, feedback);
        Assert.Contains($"git show \"{RefAttempt1}:<path>\"", feedback);
        Assert.Contains("output.txt", feedback); // the diff-stat summary names the changed file
    }

    [Fact]
    public async Task MaxTurnsRollback_RefContainsThePreservedAttempt()
    {
        // A task that NEVER succeeds (every attempt hits max-turns) keeps every salvage ref past run
        // end (the settle-prune only fires on Succeeded — proven by SalvageRefs_PrunedOnFreshReset),
        // so its attempt-1 ref can be inspected here for CONTENT: it must contain the in-scope file
        // that (rolled-back) attempt actually wrote, proving the preserve captured the real tree.
        await RunAsync(FakeMode.MaxTurnsForever);

        Assert.True(RefExists(_repoPath, RefAttempt1), $"expected salvage ref {RefAttempt1} to exist");
        string blobAtRef = RunGit(_repoPath, "show", $"{RefAttempt1}:src/output.txt").Trim();
        Assert.Equal("attempt-1-output", blobAtRef);
    }

    [Fact]
    public async Task PreserveAttemptsForSalvage_False_DisablesPreservation()
    {
        RunReport report = await RunAsync(FakeMode.MaxTurnsThenSucceed, preserveAttemptsForSalvage: false);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.Succeeded, task.Outcome);

        Assert.False(RefExists(_repoPath, RefAttempt1),
            "salvage ref must NOT be created when preserveAttemptsForSalvage is false");

        string feedback = File.ReadAllText(Path.Combine(AttemptDir(1), "feedback.md"));
        Assert.DoesNotContain("## Prior attempt work is salvageable", feedback);
    }

    [Fact]
    public async Task GuardrailFailedRollback_IsStashedAndExposed_WithVerdicts_Issue306()
    {
        // #306 RED-BAR (supersedes #195's scope guard): a task whose GUARDRAIL fails on a non-final
        // attempt is now STASHED — under the OLD behavior this ref did NOT exist (the whole point of the
        // fix). The stashed ref must contain the attempt's real in-scope file, and attempt-1's feedback
        // (read by attempt 2) must expose an APPLYABLE PATCH FILE + the ref + per-guardrail verdicts —
        // not just a summary. The action always succeeds and writes src/output.txt; the guardrail (exit 1)
        // is what fails.
        RunReport report = await RunAsync(FakeMode.AlwaysSucceedActionOnly);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.GuardrailFailed, task.Outcome); // needs-human after budget exhaustion

        // The stash ref exists AND holds the rolled-back attempt's real work (was gone before #306).
        Assert.True(RefExists(_repoPath, RefAttempt1),
            "a guardrail-failed non-final rollback must now be stashed to a salvage ref (issue #306)");
        string blobAtRef = RunGit(_repoPath, "show", $"{RefAttempt1}:src/output.txt").Trim();
        Assert.Equal("attempt-1-output", blobAtRef);

        // The retry input exposes the stash DIRECTLY: an applyable patch file + the ref, plus verdicts.
        string attempt1Dir = AttemptDir(1);
        Assert.True(File.Exists(Path.Combine(attempt1Dir, "prior-attempt.patch")),
            "expected a directly-applyable prior-attempt.patch in the stashed attempt's log dir");
        string feedback = File.ReadAllText(Path.Combine(attempt1Dir, "feedback.md"));
        Assert.Contains("## Prior attempt work is salvageable", feedback);
        Assert.Contains(RefAttempt1, feedback);
        Assert.Contains("prior-attempt.patch", feedback);              // git apply target named
        Assert.Contains("## Prior attempt: guardrail verdicts", feedback);
        Assert.Contains("- ❌ 01-ok", feedback);                        // the failing guardrail's verdict
    }

    [Fact]
    public async Task GuardrailFailedRollback_NotStashed_WhenSalvageDisabled()
    {
        // The clean-slate reset remains the default and the stash is opt-out: with the config off, a
        // guardrail-failed rollback creates no ref and offers no salvage section.
        RunReport report = await RunAsync(FakeMode.AlwaysSucceedActionOnly, preserveAttemptsForSalvage: false);

        Assert.Equal(TaskOutcome.GuardrailFailed, Assert.Single(report.Tasks).Outcome);
        Assert.False(RefExists(_repoPath, RefAttempt1),
            "salvage must not preserve a guardrail-failed rollback when preserveAttemptsForSalvage is false");

        string feedback = File.ReadAllText(Path.Combine(AttemptDir(1), "feedback.md"));
        Assert.DoesNotContain("## Prior attempt work is salvageable", feedback);
    }

    [Fact]
    public async Task ProtectedArtifactGuardrailFail_IsNotStashed_NorAdvertised_Issue306_WEAK1()
    {
        // #306 review WEAK-1 RED-BAR: a protected-artifact (tests-untouched-class) guardrail failure —
        // named "01-test-files-pristine", a synonym the OLD bare-"untouched" substring MISSED — means the
        // agent gamed the check by editing a protected file. Its work must be genuinely UNRECOVERABLE via
        // salvage: NO ref, NO patch (suppressed AT CREATION, not merely un-advertised). Under the old code
        // the ref + patch WERE written AND the feedback actively instructed `git apply` on the gamed patch.
        RunReport report = await RunAsync(FakeMode.ProtectedArtifactGuardrailFails);
        Assert.Equal(TaskOutcome.GuardrailFailed, Assert.Single(report.Tasks).Outcome);

        Assert.False(RefExists(_repoPath, RefAttempt1),
            "a protected-artifact (gamed-tests) failure must NOT be stashed to a salvage ref (issue #306 WEAK-1)");
        Assert.False(File.Exists(Path.Combine(AttemptDir(1), "prior-attempt.patch")),
            "no applyable salvage patch may be written for a gamed-artifact attempt");

        string feedback = File.ReadAllText(Path.Combine(AttemptDir(1), "feedback.md"));
        Assert.DoesNotContain("## Prior attempt work is salvageable", feedback);
        Assert.DoesNotContain("git apply", feedback);          // never advertise the gamed patch
        Assert.Contains("Do NOT edit the test file", feedback); // the authoritative guidance survives
    }

    [Fact]
    public void SalvageGitFaults_DegradeGracefully_NeverThrow_Issue306_WEAK2()
    {
        // #306 review WEAK-2: a git-spawn failure during salvage (git off PATH, a bad working dir, ENOMEM)
        // surfaces as Win32Exception — NOT InvalidOperationException — so a catch of only the latter would
        // let it ESCAPE and crash the attempt. Prove (a) the fault a bad worktree throws is a type the
        // widened salvage catch handles, and (b) the diff helpers honour their "never throws" contract.
        string bogus = Path.Combine(_root, "does-not-exist-" + Guid.NewGuid().ToString("N"));

        Exception? ex = Record.Exception(
            () => GitWorktreeProvider.PreserveAttemptToRef(bogus, "refs/guardrails/x/attempt-1"));
        Assert.NotNull(ex);
        Assert.True(
            ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException,
            $"expected a fault the widened salvage catch handles, got {ex!.GetType().FullName}: {ex.Message}");

        // The diff helpers must degrade to "" (their documented "never throws" contract) even on a bogus dir.
        Assert.Equal("", GitWorktreeProvider.DiffAgainstBase(bogus, "HEAD", "refs/guardrails/x/attempt-1"));
        Assert.Equal("", GitWorktreeProvider.DiffStatAgainstBase(bogus, "HEAD", "refs/guardrails/x/attempt-1"));
    }

    [Fact]
    public void PreserveAttemptToRef_ExcludesReconstructableDirs_FromSalvagePatch_Issue306_NIT2()
    {
        // #306 review NIT-2: the salvage snapshot now routes through SegmentStaging's exclusion pathspecs,
        // so node_modules (and the harness's own .guardrails-* scaffolding) never bloat the agent-applyable
        // patch — while a real in-scope file is still captured.
        string taskBase = RunGit(_repoPath, "rev-parse", "HEAD").Trim();
        Directory.CreateDirectory(Path.Combine(_repoPath, "src"));
        File.WriteAllText(Path.Combine(_repoPath, "src", "real.txt"), "kept");
        Directory.CreateDirectory(Path.Combine(_repoPath, "node_modules", "pkg"));
        File.WriteAllText(Path.Combine(_repoPath, "node_modules", "pkg", "index.js"), "reconstructable");

        const string refName = "refs/guardrails/nit2/attempt-1";
        GitWorktreeProvider.PreserveAttemptToRef(_repoPath, refName);

        // The real in-scope file is in the snapshot; node_modules is excluded.
        Assert.Equal("kept", RunGit(_repoPath, "show", $"{refName}:src/real.txt").Trim());
        var (_, exit) = TryRunGit(_repoPath, "cat-file", "-e", $"{refName}:node_modules/pkg/index.js");
        Assert.NotEqual(0, exit); // node_modules path is absent from the salvage tree

        string patch = GitWorktreeProvider.DiffAgainstBase(_repoPath, taskBase, refName);
        Assert.Contains("src/real.txt", patch);
        Assert.DoesNotContain("node_modules", patch);
    }

    [Fact]
    public async Task SalvagedOutOfScopeFile_StillCaughtByWriteScopeCheck()
    {
        // Deliverable 5: salvaged files remain subject to writeScope. The task declares writeScope
        // ["src/"], and attempt 2 (simulating an adopted salvage that included an out-of-scope file)
        // ALSO writes outside src/ — the existing retrospective write-scope check (which runs on the
        // FINAL state regardless of how it got there) must still catch it and fail the attempt.
        RunReport report = await RunAsync(FakeMode.MaxTurnsThenSucceedWithBadScope);

        TaskResult task = Assert.Single(report.Tasks);
        Assert.Equal(TaskOutcome.GuardrailFailed, task.Outcome);
        Assert.Contains("write-scope violation", task.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("outside.txt", task.Summary);
    }

    [Fact]
    public async Task FakeCli_WritesNothingOutsideThisFixturesAction_Issue253()
    {
        // #253 RED-BAR (this is the leak itself, not a proxy for it). The class's own tests all pass
        // whether or not the fake CLI is contained, because standalone there is no ambient
        // GUARDRAILS_WORKSPACE for it to escape into — which is exactly why the leak survived a
        // "could not reproduce" investigation and why HostRepoCleanlinessGuard never tripped.
        //
        // This reproduces the ONE invocation that escaped: NeedsHumanTriage fires when a task settles
        // needs-human (as SalvagedOutOfScopeFile_StillCaughtByWriteScopeCheck provokes) and builds
        // `Environment = new Dictionary<string, string>()` on purpose, so the fake CLI ran with cwd and
        // GUARDRAILS_* INHERITED from whatever launched `dotnet test`. Inside an outer `guardrails run`
        // that is the run's own _integration worktree.
        //
        // Handing the outer values in `Environment` is byte-identical from the child's point of view to
        // inheriting them (ProcessRunner overlays this dictionary onto the inherited block), and it keeps
        // the test deterministic and parallel-safe — no process-global env mutation, no sleeps.
        string cli = WriteFakeCli(FakeMode.MaxTurnsThenSucceedWithBadScope);

        string outerWorktree = Path.Combine(_root, "outer-integration-worktree");
        Directory.CreateDirectory(outerWorktree);
        string outerFragment = Path.Combine(_root, "outer-state-fragment.json");

        PromptResult result = await InvokeFakeCliAsync(
            cli,
            cwd: outerWorktree,
            env: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // No ActionTokenVar — this is NOT this fixture's action.
                ["GUARDRAILS_WORKSPACE"] = outerWorktree,
                ["GUARDRAILS_STATE_OUT"] = outerFragment,
                ["GUARDRAILS_TASK_ID"] = "outer-run-task"
            });

        // The fake must still behave like a runner (triage is advisory but must not look broken).
        Assert.True(result.Completed, $"the fake CLI must still return a terminal result: {result.Summary}");

        // The contract: not one byte outside what THIS fixture's harness handed it. cwd is the outer
        // worktree too, so this single probe covers both escape vectors — an inherited
        // $GUARDRAILS_WORKSPACE and a path left relative to an inherited cwd.
        string[] strays = Directory.GetFileSystemEntries(outerWorktree, "*", SearchOption.AllDirectories);
        Assert.True(strays.Length == 0,
            "the fake CLI leaked into a workspace it was never given by this fixture (issue #253): " +
            string.Join(" | ", strays));

        // An inherited GUARDRAILS_STATE_OUT points at the OUTER run's state fragment — writing it would
        // corrupt that run's state, a strictly worse failure than the stray files.
        Assert.False(File.Exists(outerFragment),
            "the fake CLI wrote a state fragment to an inherited GUARDRAILS_STATE_OUT (issue #253)");

        // Nothing at all happened: a non-action invocation must not even be counted as an attempt.
        Assert.False(File.Exists(_counterPath),
            "a non-action invocation must not bump the attempt counter (it is what made the leaked file " +
            "read 'attempt-3-output' for a task that only ever ran 2 attempts)");
    }

    [Fact]
    public async Task FakeCli_StillWritesForThisFixturesAction_Issue253()
    {
        // The positive twin of the red-bar above: the #253 gate must not be satisfiable by neutering the
        // fake. For a genuine action invocation the fake still produces exactly the artifacts every other
        // test in this class depends on — the in-scope file per attempt, the out-of-scope file on the
        // succeeding attempt, and the state fragment.
        string cli = WriteFakeCli(FakeMode.MaxTurnsThenSucceedWithBadScope);

        string workspace = Path.Combine(_root, "segment-worktree");
        Directory.CreateDirectory(workspace);
        string fragment = Path.Combine(_root, "fragment.json");

        var actionEnv = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ActionTokenVar] = _actionToken,
            ["GUARDRAILS_WORKSPACE"] = workspace,
            ["GUARDRAILS_STATE_OUT"] = fragment,
            ["GUARDRAILS_TASK_ID"] = "01-implement"
        };

        // Invocation 1 = the max-turns attempt: in-scope file only, no fragment.
        await InvokeFakeCliAsync(cli, workspace, actionEnv);
        Assert.Equal("attempt-1-output", File.ReadAllText(Path.Combine(workspace, "src", "output.txt")));
        Assert.False(File.Exists(Path.Combine(workspace, "outside.txt")));

        // Invocation 2 = the succeeding attempt: adds the out-of-scope file + the state fragment.
        await InvokeFakeCliAsync(cli, workspace, actionEnv);
        Assert.Equal("attempt-2-output", File.ReadAllText(Path.Combine(workspace, "src", "output.txt")));
        Assert.Equal("out of scope", File.ReadAllText(Path.Combine(workspace, "outside.txt")));
        Assert.Contains("\"done\": true", File.ReadAllText(fragment));
    }

    /// <summary>
    /// Invokes the generated fake CLI through the REAL <see cref="ClaudePromptRunner"/> +
    /// <see cref="ProcessRunner"/> the harness uses — no bespoke process-launch code under test, so the
    /// #253 gate is proven against the same spawn path production takes.
    /// </summary>
    private async Task<PromptResult> InvokeFakeCliAsync(
        string cli, string cwd, IReadOnlyDictionary<string, string> env) =>
        await new ClaudePromptRunner("claude", cli, new ProcessRunner()).RunAsync(
            new PromptInvocation
            {
                ComposedPrompt = "do the thing",
                Role = PromptRole.Action,
                WorkingDirectory = cwd,
                PlanDirectory = _planDir,
                Environment = env,
                Settings = new PromptRunnerSettings { MaxTurns = 5 },
                Timeout = TimeSpan.FromMinutes(2),
                StreamLogPath = Path.Combine(_root, $"stream-{Guid.NewGuid():N}.jsonl")
            },
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task SalvageRefs_PrunedOnTaskSettleSucceeded()
    {
        await RunAsync(FakeMode.MaxTurnsThenSucceed);

        // Sanity already proven by the first test (the ref exists mid-flow); by the time RunAsync
        // returns, the task settled Succeeded, so the Scheduler's OnSettledAsync prune must have run.
        Assert.False(RefExists(_repoPath, RefAttempt1),
            "salvage refs for a task that went on to succeed must be pruned on settle");
    }

    [Fact]
    public async Task SalvageRefs_PrunedOnFreshReset()
    {
        // A task that NEVER succeeds (every attempt hits max-turns) keeps its salvage ref past run end
        // — the settle-prune only fires on Succeeded. --fresh (RunReset.Fresh) must sweep it instead.
        RunReport report = await RunAsync(FakeMode.MaxTurnsForever);
        Assert.NotEqual(TaskOutcome.Succeeded, Assert.Single(report.Tasks).Outcome);

        Assert.True(RefExists(_repoPath, RefAttempt1),
            "sanity: a task that never succeeds must keep its salvage ref after the run (no settle-prune)");

        RunReset.Fresh(_planDir);

        Assert.False(RefExists(_repoPath, RefAttempt1),
            "--fresh must prune every salvage ref via PruneAllSalvageRefs");
    }

    // --- fixture plumbing --------------------------------------------------------------------

    private string AttemptDir(int attempt)
    {
        string logsRoot = Path.Combine(_planDir, "logs");
        string runDir = Directory.GetDirectories(logsRoot).Single();
        return Path.Combine(runDir, "01-implement", $"attempt-{attempt}");
    }

    private void WritePlan(FakeMode mode, bool preserveAttemptsForSalvage)
    {
        string fakeCliPath = WriteFakeCli(mode);
        string commandJson = fakeCliPath.Replace("\\", "\\\\");

        File.WriteAllText(Path.Combine(_planDir, "guardrails.json"),
            $$"""
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 1,
              "maxParallelism": 2,
              "preserveAttemptsForSalvage": {{(preserveAttemptsForSalvage ? "true" : "false")}},
              "promptRunners": {
                "default": "claude",
                "claude": {
                  "command": "{{commandJson}}",
                  "permissionMode": "acceptEdits",
                  "maxTurns": 5
                }
              }
            }
            """);

        string taskDir = Path.Combine(_planDir, "tasks", "01-implement");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        // action.env carries the #253 proof-of-origin token. TaskExecutor.BuildEnvironment folds
        // task.Action.Env into the ACTION process env, so the token reaches the fake CLI for a real
        // attempt — and ONLY for a real attempt (advisory invocations like NeedsHumanTriage build their
        // own empty env and never see it).
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""
            {
              "description": "fake prompt task exercising retry salvage",
              "dependsOn": [],
              "writeScope": ["src/"],
              "action": {
                "path": "action.prompt.md",
                "env": { "{{ActionTokenVar}}": "{{_actionToken}}" }
              }
            }
            """);
        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "Implement the thing.\n");

        bool guardrailFails = mode is FakeMode.AlwaysSucceedActionOnly or FakeMode.ProtectedArtifactGuardrailFails;
        string guardrailBody = guardrailFails
            ? (Windows ? "exit 1\n" : "#!/usr/bin/env bash\nexit 1\n")
            : (Windows ? "exit 0\n" : "#!/usr/bin/env bash\nexit 0\n");
        // #306 WEAK-1: the protected-artifact mode names the guardrail with a synonym ("pristine") the old
        // bare-"untouched" substring would have missed, so the test exercises the de-fragilized matcher.
        string guardrailBase = mode == FakeMode.ProtectedArtifactGuardrailFails ? "01-test-files-pristine" : "01-ok";
        WriteScript(Path.Combine(taskDir, "guardrails", Windows ? $"{guardrailBase}.ps1" : $"{guardrailBase}.sh"), guardrailBody);
    }

    /// <summary>
    /// The fake Claude CLI. Every ACTION invocation (see the containment gate below) increments a counter
    /// file kept OUTSIDE the repo (so it survives the segment's F2 reset between attempts) and always
    /// writes an IN-SCOPE <c>src/output.txt</c> so the action has SOME observable effect. Behavior then
    /// branches on <paramref name="mode"/>:
    /// <list type="bullet">
    /// <item><see cref="FakeMode.MaxTurnsThenSucceed"/>: invocation 1 emits <c>error_max_turns</c> (no
    /// fragment); invocation 2+ writes the fragment and succeeds.</item>
    /// <item><see cref="FakeMode.MaxTurnsForever"/>: EVERY invocation emits <c>error_max_turns</c>.</item>
    /// <item><see cref="FakeMode.AlwaysSucceedActionOnly"/>: EVERY invocation succeeds with a fragment —
    /// the guardrail script (not the action) is what fails in this mode.</item>
    /// <item><see cref="FakeMode.MaxTurnsThenSucceedWithBadScope"/>: like
    /// <see cref="FakeMode.MaxTurnsThenSucceed"/>, but invocation 2+ ALSO writes an out-of-scope
    /// <c>outside.txt</c> (simulating an adopted salvage that pulled in a bad file).</item>
    /// </list>
    /// <para>
    /// <b>Issue #253 containment gate (the first thing every flavor does).</b> This script writes
    /// nothing at all unless <see cref="ActionTokenVar"/> holds this instance's <see cref="_actionToken"/>.
    /// Rooting the fixture under <see cref="Path.GetTempPath"/> does not contain it, because every write
    /// target here is derived from the <b>child's</b> environment (<c>$GUARDRAILS_WORKSPACE</c>,
    /// <c>$GUARDRAILS_STATE_OUT</c>) or its cwd — and a child INHERITS both from the enclosing process
    /// for any invocation whose env the harness does not populate. <c>NeedsHumanTriage</c> is exactly
    /// such an invocation: it fires when a task settles <c>needs-human</c> (which
    /// <see cref="SalvagedOutOfScopeFile_StillCaughtByWriteScopeCheck"/> deliberately provokes) and
    /// builds <c>Environment = new Dictionary&lt;string, string&gt;()</c> on purpose. Running this suite
    /// standalone that invocation saw no <c>GUARDRAILS_WORKSPACE</c> and quietly wrote nothing — which is
    /// why the leak went unreproduced for so long. Running it INSIDE a <c>guardrails run</c> (a plan
    /// preflight doing <c>dotnet test</c>) it inherited the OUTER run's <c>GUARDRAILS_WORKSPACE</c> and
    /// wrote <c>outside.txt</c> + <c>src/output.txt</c> straight into that run's <c>_integration</c>
    /// worktree, where the write-scope <c>git add -A</c> blamed an innocent agent.
    /// </para>
    /// <para>
    /// The gate is a POSITIVE identification, not a path denylist: the segment worktree the fake must
    /// legitimately write to lives under the harness's own <c>gr-wt</c> root, NOT under
    /// <see cref="_root"/>, so "is the target under my temp root?" cannot be the test. Proving the
    /// INVOCATION is this fixture's action proves the whole §5.1 env set came from the inner harness,
    /// which makes every path derived from it fixture-owned by construction.
    /// </para>
    /// </summary>
    private string WriteFakeCli(FakeMode mode)
    {
        string path = Path.Combine(_root, Windows ? "fake-claude.cmd" : "fake-claude.sh");
        string counter = _counterPath.Replace("\\", "\\\\");
        bool maxTurnsForever = mode == FakeMode.MaxTurnsForever;
        bool maxTurnsFirstOnly = mode is FakeMode.MaxTurnsThenSucceed or FakeMode.MaxTurnsThenSucceedWithBadScope;
        bool badScope = mode == FakeMode.MaxTurnsThenSucceedWithBadScope;

        if (OperatingSystem.IsWindows())
        {
            string ps1 = Path.ChangeExtension(path, ".ps1");
            File.WriteAllText(ps1,
                $$"""
                $null = [Console]::In.ReadToEnd()

                # Issue #253 containment gate. Every path below is derived from the child environment,
                # which is INHERITED (not harness-set) for any non-action invocation — so unless this is
                # this fixture's own task action, touch nothing anywhere and return a benign result.
                if ($env:{{ActionTokenVar}} -cne '{{_actionToken}}') {
                    Write-Output '{"type":"result","is_error":false,"result":"fake claude: not this fixture task action - no files written","total_cost_usd":0,"num_turns":1}'
                    exit 0
                }
                if ([string]::IsNullOrWhiteSpace($env:GUARDRAILS_WORKSPACE)) {
                    # Fail LOUD rather than resolving a relative path against an inherited cwd.
                    [Console]::Error.WriteLine('fake claude: GUARDRAILS_WORKSPACE unset for a task action')
                    exit 9
                }

                $count = 0
                if (Test-Path "{{counter}}") { $count = [int](Get-Content "{{counter}}" -Raw).Trim() }
                $count++
                Set-Content -NoNewline -Path "{{counter}}" -Value "$count"

                $srcDir = Join-Path $env:GUARDRAILS_WORKSPACE 'src'
                New-Item -ItemType Directory -Force -Path $srcDir | Out-Null
                Set-Content -NoNewline -Path (Join-Path $srcDir 'output.txt') -Value "attempt-$count-output"

                $hitMaxTurns = ({{(maxTurnsForever ? "$true" : "$false")}}) -or (({{(maxTurnsFirstOnly ? "$true" : "$false")}}) -and ($count -eq 1))

                if ($hitMaxTurns) {
                    Write-Output '{"type":"result","subtype":"error_max_turns","is_error":true,"result":"Reached maximum number of turns (5)","num_turns":5}'
                } else {
                    if ({{(badScope ? "$true" : "$false")}}) {
                        Set-Content -NoNewline -Path (Join-Path $env:GUARDRAILS_WORKSPACE 'outside.txt') -Value 'out of scope'
                    }
                    if ($env:GUARDRAILS_STATE_OUT) {
                        $frag = '{"' + $env:GUARDRAILS_TASK_ID + '": {"done": true}' + '}'
                        Set-Content -NoNewline -Path $env:GUARDRAILS_STATE_OUT -Value $frag
                    }
                    Write-Output '{"type":"result","is_error":false,"result":"fake done","total_cost_usd":0.01,"num_turns":3}'
                }
                """);
            File.WriteAllText(path, $"@echo off\r\npwsh -NoProfile -ExecutionPolicy Bypass -File \"{ps1}\" %*\r\n");
        }
        else
        {
            string body =
                "#!/usr/bin/env bash\n" +
                "cat > /dev/null\n" +
                // Issue #253 containment gate — see WriteFakeCli's remarks. Twin of the .ps1 branch.
                $"if [ \"${ActionTokenVar}\" != \"{_actionToken}\" ]; then\n" +
                "  printf '{\"type\":\"result\",\"is_error\":false,\"result\":\"fake claude: not this fixture task action - no files written\",\"total_cost_usd\":0,\"num_turns\":1}\\n'\n" +
                "  exit 0\n" +
                "fi\n" +
                "if [ -z \"$GUARDRAILS_WORKSPACE\" ]; then\n" +
                "  echo 'fake claude: GUARDRAILS_WORKSPACE unset for a task action' >&2\n" +
                "  exit 9\n" +
                "fi\n" +
                "count=0\n" +
                $"if [ -f \"{counter}\" ]; then count=$(cat \"{counter}\" | tr -d '[:space:]'); fi\n" +
                "count=$((count + 1))\n" +
                $"printf '%s' \"$count\" > \"{counter}\"\n" +
                "mkdir -p \"$GUARDRAILS_WORKSPACE/src\"\n" +
                "printf 'attempt-%s-output' \"$count\" > \"$GUARDRAILS_WORKSPACE/src/output.txt\"\n" +
                (maxTurnsForever
                    ? "if true; then\n"
                    : maxTurnsFirstOnly
                        ? "if [ \"$count\" -eq 1 ]; then\n"
                        : "if false; then\n") +
                "  printf '{\"type\":\"result\",\"subtype\":\"error_max_turns\",\"is_error\":true,\"result\":\"Reached maximum number of turns (5)\",\"num_turns\":5}\\n'\n" +
                "else\n" +
                (badScope ? "  printf 'out of scope' > \"$GUARDRAILS_WORKSPACE/outside.txt\"\n" : "") +
                "  if [ -n \"$GUARDRAILS_STATE_OUT\" ]; then\n" +
                "    printf '{\"%s\": {\"done\": true}}' \"$GUARDRAILS_TASK_ID\" > \"$GUARDRAILS_STATE_OUT\"\n" +
                "  fi\n" +
                "  printf '{\"type\":\"result\",\"is_error\":false,\"result\":\"fake done\",\"total_cost_usd\":0.01,\"num_turns\":3}\\n'\n" +
                "fi\n";
            File.WriteAllText(path, body);
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        return path;
    }

    private static void WriteScript(string path, string content)
    {
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    private static void InitRepo(string repoPath)
    {
        RunGit(repoPath, "init");
        RunGit(repoPath, "config", "user.email", "test@guardrails.local");
        RunGit(repoPath, "config", "user.name", "Guardrails Test");
        RunGit(repoPath, "config", "commit.gpgsign", "false");
        RunGit(repoPath, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(repoPath, "README.md"), "# retry-salvage-test");
        RunGit(repoPath, "add", ".");
        RunGit(repoPath, "commit", "-m", "Initial commit");
    }

    private static bool RefExists(string repoPath, string refName)
    {
        var (_, exitCode) = TryRunGit(repoPath, "rev-parse", "--verify", "--quiet", refName);
        return exitCode == 0;
    }

    private static string RunGit(string workingDir, params string[] args)
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
            throw new InvalidOperationException($"git {string.Join(" ", args)} (in {workingDir}) exited {proc.ExitCode}: {stderr.Trim()}");
        }
        return stdout;
    }

    private static (string stdout, int exitCode) TryRunGit(string workingDir, params string[] args)
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
        proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (stdout, proc.ExitCode);
    }

    /// <summary>Windows-safe recursive delete (strips the read-only bit git leaves on loose objects).</summary>
    private static void SafeDeleteTree(string root)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }

            Directory.Delete(root, recursive: true);
        }
        catch (IOException) { /* best-effort teardown */ }
        catch (UnauthorizedAccessException) { /* best-effort teardown */ }
    }
}

/// <summary>
/// Issue #253 tripwire, shared as an <see cref="IClassFixture{T}"/> so it wraps every test in
/// <see cref="RetrySalvageTests"/> (constructed once before the class's first test, disposed once
/// after its last): snapshots <c>git status --porcelain</c> of the REAL repo checkout hosting this
/// test run, then asserts on teardown that no NEW path appeared. A dogfood run once saw a live task's
/// write-scope check (<c>git add -A</c> in its segment worktree) attribute two files —
/// <c>outside.txt</c> and <c>src/output.txt</c> — to the agent with zero trace in its own transcript.
/// Those are the exact literal fixture names <see cref="RetrySalvageTests.WriteFakeCli"/> uses — and
/// that suspicion is now CONFIRMED (see <see cref="RetrySalvageTests"/>'s remarks for the mechanism).
/// <para>
/// The guard has two layers, because the <c>git status</c> layer alone had to stand down (#433) in
/// precisely the environment where the leak was observed — a LINKED worktree, where the harness itself
/// dirties the tree concurrently:
/// </para>
/// <list type="number">
/// <item><b>Literal probe (always on, worktree included).</b> The two fixture filenames appear nowhere
/// in a Guardrails checkout, so one of them APPEARING at the host root during this class is
/// unambiguous evidence of the #253 leak with none of the ambient-noise problem that forced the
/// carve-out. This is the layer that would have caught the live incident.</item>
/// <item><b>Full <c>git status</c> diff (normal checkouts only).</b> Broad — catches a leak under any
/// name — but only sound when the enclosing repo is quiescent, hence the #433 carve-out below.</item>
/// </list>
/// <para>
/// Both layers are belt-and-braces around the deterministic proof, which lives in
/// <see cref="RetrySalvageTests.FakeCli_WritesNothingOutsideThisFixturesAction_Issue253"/>: this guard
/// can only fire when the ambient environment happens to aim the leak at the host repo root, whereas
/// that test provokes the escape directly.
/// </para>
/// </summary>
public sealed class HostRepoCleanlinessGuard : IDisposable
{
    /// <summary>
    /// The literals <see cref="RetrySalvageTests.WriteFakeCli"/> writes, relative to a repo root. A
    /// Guardrails checkout contains neither, so either one appearing is a leak — never a false positive
    /// from ordinary build/harness activity.
    /// </summary>
    private static readonly string[] FixtureLiterals =
    [
        "outside.txt",
        Path.Combine("src", "output.txt")
    ];

    private readonly string? _hostRepoRoot;
    private readonly HashSet<string> _before;
    private readonly HashSet<string> _literalsBefore;

    public HostRepoCleanlinessGuard()
    {
        _hostRepoRoot = FindEnclosingGitRepo(AppContext.BaseDirectory);
        _before = _hostRepoRoot is null ? [] : StatusLines(_hostRepoRoot);

        // Pre-existing copies (e.g. left by an earlier poisoned run) are not THIS class's doing — only
        // an APPEARANCE during the class is.
        _literalsBefore = _hostRepoRoot is null ? [] : PresentLiterals(_hostRepoRoot);
    }

    public void Dispose()
    {
        // Not running from within a git checkout (e.g. some future packaging context) — nothing to
        // guard; this is a best-effort tripwire, not a hard requirement of the test environment.
        if (_hostRepoRoot is null) return;

        // Layer 1 — runs even inside a linked worktree, i.e. in the exact environment where the live
        // #253 incident happened and where layer 2 must stand down. A plain file-existence check needs
        // no `git status`, so the harness's concurrent churn cannot make it lie.
        List<string> leaked = PresentLiterals(_hostRepoRoot).Except(_literalsBefore).ToList();
        Assert.True(leaked.Count == 0,
            "RetrySalvageTests leaked its fixture file(s) into the REAL repo/worktree hosting the test " +
            "run (issue #253) -- appeared during this class: " + string.Join(" | ", leaked));

        // Issue #433: the tripwire's premise is that the enclosing repo is QUIESCENT for the duration of
        // this class — only these tests could dirty it. That premise is FALSE when the suite runs inside
        // a Guardrails-managed worktree (a plan whose guardrails run `dotnet test`): the harness is
        // concurrently merging task branches, running `git add -A` write-scope checks, and dropping build
        // output into that same tree, so `git status` legitimately gains lines that have nothing to do
        // with these tests. Asserting there produced a CLASS-CLEANUP failure — surfacing as
        // Xunit.Sdk.TestPipelineException with a NON-ZERO exit code and `Failed: 0` — which halted a real
        // run at its baseline preflight while reporting that healthy tests were red.
        //
        // Discriminator: a LINKED git worktree has `.git` as a FILE ("gitdir: …"); a normal checkout has
        // it as a DIRECTORY. Skip the tripwire in the linked-worktree case only, so it keeps full teeth
        // for ordinary dev and CI runs (where #253's regression protection actually applies).
        //
        // NOTE: this carve-out is scoped to the `git status` layer ONLY. Layer 1 above deliberately runs
        // first and unconditionally — the linked-worktree case is where the leak actually bit, so the
        // carve-out must not be allowed to swallow it.
        if (File.Exists(Path.Combine(_hostRepoRoot, ".git")))
        {
            return;
        }

        HashSet<string> after = StatusLines(_hostRepoRoot);
        List<string> newEntries = after.Except(_before).ToList();

        Assert.True(newEntries.Count == 0,
            "RetrySalvageTests must not leave any new untracked/modified path in the REAL repo " +
            "hosting the test run (issue #253) -- new git-status line(s): " + string.Join(" | ", newEntries));
    }

    /// <summary>Which of <see cref="FixtureLiterals"/> currently exist at <paramref name="repoRoot"/>.</summary>
    private static HashSet<string> PresentLiterals(string repoRoot) =>
        FixtureLiterals
            .Where(rel => File.Exists(Path.Combine(repoRoot, rel)))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Walks up from <paramref name="startDir"/> looking for a `.git` dir/file (a worktree's
    /// `.git` is a file, not a dir). Returns null if none is found (not running inside a checkout).</summary>
    private static string? FindEnclosingGitRepo(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static HashSet<string> StatusLines(string repoRoot)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("status");
        psi.ArgumentList.Add("--porcelain");
        using Process proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        // A failure here (e.g. git not on PATH in some exotic environment) must not itself fail the
        // guard — return an empty snapshot so before/after compare equal and this stays a no-op.
        if (proc.ExitCode != 0) return [];

        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToHashSet(StringComparer.Ordinal);
    }
}
