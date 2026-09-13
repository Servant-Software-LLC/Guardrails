using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.Supply;

/// <summary>
/// Design 40 §3 — the overwatcher's auto-resolve for a needs-human halt caused by a missing supplied
/// resource: PROPOSE the supply/reset/run sequence at every dial below <c>critical</c>; at
/// <c>dial:critical</c> the overwatcher may run it itself, because the operator has already accepted
/// machine judgement at that tier. The caution the design records survives the decision: applying the
/// fix is mechanical — the SAME <see cref="SuppliedDrain"/> + <see cref="RunJournal.RecordSupplied"/>
/// machinery a human-run <c>guardrails supply</c> uses — but deciding that the staged file really is the
/// one the task needed is a judgement, so this is gated on the SAME dial composition doc 12 §3.2 uses
/// everywhere else (<see cref="AutonomyPolicy.Auto"/> + an <c>autonomy</c> block present +
/// <see cref="EscalationThreshold.Critical"/>), never on the fix classifier's guidance/budget allowlist.
/// <para>
/// TDD red: <see cref="OverwatchSupplyAutoResolve.Resolve"/> currently throws
/// <see cref="NotImplementedException"/> unconditionally, so every test below is expected to FAIL against
/// this tree. Do not add <c>Assert.Throws&lt;NotImplementedException&gt;</c> wrappers — that would make
/// these pass against the stub, which defeats the point of pinning them red.
/// <see cref="AutoResolve_DoesNotCertifyAnythingUnverified"/> in particular is censused (review finding
/// 10) and is NOT exempt from that redness the way a never-weaker row elsewhere in this plan would be.
/// </para>
/// <para>
/// Mirrors <see cref="SuppliedDrainTests"/>'s fixture idiom: <see cref="SuppliedDrain"/>'s whole job is
/// committing onto a real base, so an auto-resolve at <c>dial:critical</c> is exercised against an ACTUAL
/// git repository rather than a fake of one.
/// </para>
/// </summary>
[Trait("Category", "Supply")]
public sealed class OverwatchSupplyAutoResolveTests : IDisposable
{
    private const string ResourcePath = "vendor/mermaid.min.js";
    private const string ResourceContent = "export const ok = true;";

    private readonly TempGitRepo _repo = new();
    private readonly string _planDirectory =
        Path.Combine(Path.GetTempPath(), "gr-overwatch-autoresolve-plan-" + Guid.NewGuid().ToString("N"));
    private readonly TaskNode _task;
    private readonly PlanDefinition _plan;
    private readonly RunJournal _journal;

    public OverwatchSupplyAutoResolveTests()
    {
        // A real on-disk plan + fresh journal (the same reduced harness OverwatchAutoTierTests uses),
        // but with a REAL git repo as the workspace — the mechanical half of an auto-resolve commits onto
        // it, and a faked git would prove nothing about whether that half ran.
        Directory.CreateDirectory(_planDirectory);
        File.WriteAllText(Path.Combine(_planDirectory, "guardrails.json"), """{ "version": 1 }""");

        string taskDir = Path.Combine(_planDirectory, "tasks", "02-vendor-mermaid-runtime");
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"), """{ "description": "t", "dependsOn": [] }""");

        _task = new TaskNode
        {
            Id = "02-vendor-mermaid-runtime",
            Directory = taskDir,
            Description = "t",
            Action = new ActionDefinition { Path = Path.Combine(taskDir, "action.prompt.md"), Kind = ActionKind.Prompt },
            Guardrails =
            [
                new GuardrailDefinition
                {
                    Name = "01-check",
                    Path = Path.Combine(taskDir, "guardrails", "01-check.ps1"),
                    Kind = ActionKind.Script
                }
            ]
        };

        _plan = new PlanDefinition
        {
            PlanDirectory = _planDirectory,
            Workspace = _repo.RepoPath,
            Config = new RunConfig { Version = 1 },
            Tasks = [_task]
        };

        _journal = RunJournal.LoadOrCreate(_plan);

        // Write the file directly at the location `guardrails supply` would have staged it (mirrors
        // SuppliedDrainTests.Stage) — the operator has already run `guardrails supply` before the
        // overwatcher fires for the halted task.
        string staged = Path.Combine(_planDirectory, "logs", _journal.RunId, "supplied",
            ResourcePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.WriteAllText(staged, ResourceContent);
    }

    private string StagedRoot() => Path.Combine(_planDirectory, "logs", _journal.RunId, "supplied");

    private string StagedFilePath() => Path.Combine(
        _planDirectory, "logs", _journal.RunId, "supplied", ResourcePath.Replace('/', Path.DirectorySeparatorChar));

    private string LandedFilePath() => Path.Combine(_repo.RepoPath, "vendor", "mermaid.min.js");

    private static OverwatchFixOp ResourceSupplyFix() =>
        new() { Kind = OverwatchFixKind.ResourceSupply, TargetPath = ResourcePath };

    // ── every dial below critical: propose the sequence, never run it ──────────────────────────────

    [Theory]
    [InlineData(AutonomyPolicy.Halt, false, null)]
    [InlineData(AutonomyPolicy.Prompt, false, null)]
    [InlineData(AutonomyPolicy.Auto, false, null)]
    [InlineData(AutonomyPolicy.Auto, true, EscalationThreshold.Low)]
    [InlineData(AutonomyPolicy.Auto, true, EscalationThreshold.Moderate)]
    [InlineData(AutonomyPolicy.Auto, true, EscalationThreshold.High)]
    public void BelowCritical_ProposesTheCommandSequence_AndDoesNotRunIt(
        AutonomyPolicy policy, bool autonomyBlockPresent, EscalationThreshold? escalationThreshold)
    {
        OverwatchDecision decision = OverwatchSupplyAutoResolve.Resolve(
            policy, autonomyBlockPresent, escalationThreshold, _task, _plan, _journal, ResourceSupplyFix());

        Assert.NotEqual(OverwatchDecisionKind.AutoResolve, decision.Kind);

        // The halt is enriched with the exact, copy-pasteable three-command sequence design 40 §3(b)
        // decided — the halt itself should name the fix that works, not leave the operator to rediscover
        // the order of `supply` / `reset` / `run`.
        Assert.NotNull(decision.RichHaltSummary);
        Assert.Contains("guardrails supply", decision.RichHaltSummary!);
        Assert.Contains(ResourcePath, decision.RichHaltSummary!);
        Assert.Contains("guardrails reset", decision.RichHaltSummary!);
        Assert.Contains(_task.Id, decision.RichHaltSummary!);
        Assert.Contains("guardrails run", decision.RichHaltSummary!);

        // "AndDoesNotRunIt": the mechanical half never fired — the staged file is untouched, nothing
        // landed on the run's base, and no provenance was recorded.
        Assert.True(File.Exists(StagedFilePath()));
        Assert.False(File.Exists(LandedFilePath()));
        Assert.Null(_journal.Document.Supplied);
    }

    // ── at dial:critical: the decided behaviour ─────────────────────────────────────────────────────

    [Fact]
    public void AtCritical_AutoResolves()
    {
        OverwatchDecision decision = OverwatchSupplyAutoResolve.Resolve(
            AutonomyPolicy.Auto, autonomyBlockPresent: true, EscalationThreshold.Critical,
            _task, _plan, _journal, ResourceSupplyFix());

        Assert.Equal(OverwatchDecisionKind.AutoResolve, decision.Kind);
        Assert.NotNull(decision.AutoResolvedPaths);
        Assert.Contains(ResourcePath, decision.AutoResolvedPaths!);

        // The mechanical fix actually ran — the SAME SuppliedDrain machinery a human-run
        // `guardrails supply` boundary-drain uses: the file lands on the workspace and the staging tree
        // (which the harness owns and no guardrail reads) is left drained, exactly as a normal drain does.
        Assert.Equal(ResourceContent, File.ReadAllText(LandedFilePath()));
        Assert.False(Directory.Exists(StagedRoot()));
    }

    [Fact]
    public void AtCritical_WritesTheProvenanceRecordNamingTheOverwatcherAsSupplier()
    {
        OverwatchSupplyAutoResolve.Resolve(
            AutonomyPolicy.Auto, autonomyBlockPresent: true, EscalationThreshold.Critical,
            _task, _plan, _journal, ResourceSupplyFix());

        // §4's provenance record is the condition on the decision, not a nicety: without it, a supplied
        // file is indistinguishable from the task's own work, which is exactly what the #453 triage would
        // need to tell apart. "By" must name the overwatcher, never "operator" — a human did not run this.
        Assert.NotNull(_journal.Document.Supplied);
        SuppliedRecord record = Assert.Single(_journal.Document.Supplied!);
        Assert.Equal("overwatcher", record.By);
        Assert.Contains(ResourcePath, record.Paths);
        Assert.True(record.Bytes > 0);
        Assert.False(string.IsNullOrWhiteSpace(record.Commit));
    }

    [Fact]
    public void AutoResolve_DoesNotCertifyAnythingUnverified()
    {
        OverwatchDecision decision = OverwatchSupplyAutoResolve.Resolve(
            AutonomyPolicy.Auto, autonomyBlockPresent: true, EscalationThreshold.Critical,
            _task, _plan, _journal, ResourceSupplyFix());

        Assert.Equal(OverwatchDecisionKind.AutoResolve, decision.Kind);

        // An auto-resolve supplies a file and re-arms the task; it must never itself certify the task's
        // work. The ONLY thing that ever marks a task's work good is a real AttemptRecord produced by
        // actually running its action and its guardrails (TaskExecutor) — a resolve has no access to that
        // machinery and must not fabricate one. Unbound, the cheapest passing implementation auto-resolves
        // at critical and short-circuits the re-armed task's gates, fully green — exactly what the
        // maintainer's condition (nothing certified that was not verified) forbids.
        Assert.Empty(_journal.AttemptsFor(_task.Id));
    }

    public void Dispose()
    {
        _repo.Dispose();
        SafeDelete.DeleteDirectory(_planDirectory);
    }

    // ── a real git repository, not a fake of one (mirrors SuppliedDrainTests.TempGitRepo) ───────────

    private sealed class TempGitRepo : IDisposable
    {
        internal string RepoPath { get; } =
            Path.Combine(Path.GetTempPath(), "gr-overwatch-autoresolve-repo-" + Guid.NewGuid().ToString("N"));

        internal TempGitRepo()
        {
            Directory.CreateDirectory(RepoPath);

            Git("init");
            string hooks = Path.Combine(RepoPath, ".git", "no-hooks");
            Directory.CreateDirectory(hooks);
            Git("config", "core.hooksPath", hooks);
            Git("config", "core.autocrlf", "false");
            Git("config", "commit.gpgsign", "false");
            Git("config", "user.email", "test@guardrails.local");
            Git("config", "user.name", "Guardrails Test");

            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# fixture repo");
            Git("add", "README.md");
            Git("commit", "-m", "Initial commit");
        }

        private string Git(params string[] arguments)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = RepoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string argument in arguments)
            {
                psi.ArgumentList.Add(argument);
            }

            using var process = Process.Start(psi)!;
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git {string.Join(' ', arguments)} (in {RepoPath}) exited {process.ExitCode}: {stderr.Trim()}");
            }

            return stdout;
        }

        public void Dispose() => SafeDelete.DeleteDirectory(RepoPath);
    }
}
