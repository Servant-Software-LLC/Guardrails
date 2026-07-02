using System.Diagnostics;
using Guardrails.Cli;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using JournalTaskStatus = Guardrails.Core.Journal.TaskStatus;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Red-bar integration tests for the TASK-LEVEL preflight slot (design-of-record
/// 09-preflight-first-class, deliverable 5). A task's <c>tasks/&lt;id&gt;/preflights/</c> folder is the
/// JIT dependency-delivery gate: it is evaluated BEFORE the attempt loop to verify a producer actually
/// delivered in the bytes this consumer inherited. A RED preflight must short-circuit the consumer to
/// <c>needs-human</c> (settle outcome <see cref="AttemptOutcome.TaskPreflightFailed"/>, serialized
/// <c>task-preflight-failed</c>) WITHOUT burning a single attempt, block only its transitive cone (exit
/// 2), and leave independent branches free to complete; a GREEN preflight must let the attempt loop
/// proceed unchanged.
///
/// <para>
/// The four-folder loader (deliverable 6) already parses <c>tasks/&lt;id&gt;/preflights/</c> into
/// <see cref="Model.TaskNode.Preflights"/>, and the <c>task-preflight-failed</c> attempt outcome
/// (deliverable 4) already exists — so these tests COMPILE against the current surface. But NOTHING
/// evaluates the slot yet: a red <c>tasks/&lt;id&gt;/preflights/</c> is inert today, so the consumer runs
/// its action and SUCCEEDS. Every no-burn / cone-isolation / exit-2 assertion below therefore FAILS on
/// current code. That RED bar is intentional and is the whole point — the slot is NOT implemented here.
/// </para>
///
/// <para>
/// Each behaviour is a <c>[Theory]</c> exercised in BOTH serial (<c>MaxParallelism = 1</c>) and worktree
/// (<c>MaxParallelism &gt; 1</c>) mode, because the no-burn property is STRUCTURAL — it must hold in both.
/// All tests drive the REAL production wiring via <see cref="SchedulerFactory.Create"/> (serial when
/// <c>maxParallelism == 1</c>; a real <see cref="GitWorktreeProvider"/> + the unconditional re-verifier
/// seam when <c>&gt; 1</c> in a git repo) and assert on the run journal. Tagged
/// <c>[Trait("Category", "Preflights")]</c> so the baseline root run excludes them via
/// <c>--filter "Category!=Preflights"</c> until the slot lands.
/// </para>
/// </summary>
public sealed class TaskPreflightSlotTests
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // Fixture — a real, runnable plan inside a real git repo. workspace ".." points at the repo root,
    // so the factory's IsGitRepository(workspace) probe sees a real git working tree and picks worktree
    // mode when maxParallelism > 1 (and serial when == 1). The git repo is harmless in serial mode. The
    // colour of each task's tasks/<id>/preflights/ slot (none/red/green) is chosen per task.
    // Windows-safe teardown strips read-only bits (loose .git objects) before delete.
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    private enum Preflight { None, Red, Green }

    private sealed class PreflightPlan : IDisposable
    {
        private readonly string _root;
        private readonly int _maxParallelism;
        private readonly int _defaultRetries;
        private PlanDefinition? _plan;

        public string RepoPath { get; }
        public string PlanDir { get; }

        public PreflightPlan(int maxParallelism, int defaultRetries)
        {
            _maxParallelism = maxParallelism;
            _defaultRetries = defaultRetries;
            _root = Path.Combine(Path.GetTempPath(), "gr-taskpreflight-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            PlanDir = Path.Combine(RepoPath, "plan");
            Directory.CreateDirectory(RepoPath);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# task-preflight-slot-test");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");

            Directory.CreateDirectory(PlanDir);
            Directory.CreateDirectory(Path.Combine(PlanDir, "state"));
            Directory.CreateDirectory(Path.Combine(PlanDir, "tasks"));

            // maxParallelism == 1 → serial mode; > 1 (in this git repo) → worktree mode.
            File.WriteAllText(Path.Combine(PlanDir, "guardrails.json"),
                $$"""
                {
                  "version": 1,
                  "guardrailMode": "failFast",
                  "workspace": "..",
                  "defaultRetries": {{defaultRetries}},
                  "maxParallelism": {{maxParallelism}}
                }
                """);
        }

        /// <summary>
        /// Add a task with a green action (writes a unique file so worktree segments have a real diff to
        /// merge) and a green guardrail. <paramref name="preflight"/> controls the NEW
        /// <c>tasks/&lt;id&gt;/preflights/</c> slot: absent, RED (exit 1), or GREEN (exit 0).
        /// </summary>
        public PreflightPlan AddTask(string id, Preflight preflight = Preflight.None, params string[] dependsOn)
        {
            string taskDir = Path.Combine(PlanDir, "tasks", id);
            Directory.CreateDirectory(taskDir);
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

            string dependsJson = dependsOn.Length == 0
                ? "[]"
                : "[" + string.Join(", ", dependsOn.Select(d => $"\"{d}\"")) + "]";

            File.WriteAllText(Path.Combine(taskDir, "task.json"),
                $$"""
                {
                  "description": "task-preflight fixture {{id}}",
                  "dependsOn": {{dependsJson}}
                }
                """);

            WriteScript(Path.Combine(taskDir, ActionFileName), ActionBody(id));
            WriteScript(Path.Combine(taskDir, "guardrails", GuardrailFileName), "exit 0");

            if (preflight != Preflight.None)
            {
                Directory.CreateDirectory(Path.Combine(taskDir, "preflights"));
                WriteScript(
                    Path.Combine(taskDir, "preflights", PreflightFileName),
                    preflight == Preflight.Red ? RedPreflightBody() : GreenPreflightBody());
            }

            return this;
        }

        /// <summary>
        /// Commit the plan (so worktree mode branches segments from a clean, committed workspace), load
        /// it, and drive the REAL production factory. No manual provider/re-verifier injection — this
        /// must observe exactly what PRODUCTION wires, because the task-preflight slot reuses the
        /// re-verifier seam the factory wires unconditionally in both modes.
        /// </summary>
        public async Task<RunReport> RunAsync()
        {
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Add plan");

            PlanLoadResult load = new PlanLoader().Load(PlanDir);
            Assert.NotNull(load.Plan);
            Assert.False(load.HasErrors,
                "Fixture plan must load cleanly (preflights/ carries a catches: declaration): " +
                string.Join("\n", load.Diagnostics));
            _plan = load.Plan!;

            Scheduler scheduler = SchedulerFactory.Create(
                _plan, new ProcessRunner(), new PathExecutableProbe(), IRunObserver.Null);
            return await scheduler.RunAsync(_plan, TestContext.Current.CancellationToken);
        }

        public JournalDocument Journal() => JournalReader.Read(RunJournal.PathFor(PlanDir));

        // ---- OS-appropriate script emission (PowerShell on Windows, bash elsewhere) ----

        private static string ActionFileName => OperatingSystem.IsWindows() ? "action.ps1" : "action.sh";
        private static string GuardrailFileName => OperatingSystem.IsWindows() ? "01-check.ps1" : "01-check.sh";
        private static string PreflightFileName => OperatingSystem.IsWindows() ? "01-producer-delivered.ps1" : "01-producer-delivered.sh";

        private static string ActionBody(string id) => OperatingSystem.IsWindows()
            ? $"Set-Content -NoNewline -Path '{id}.txt' -Value 'done'; exit 0"
            : $"printf 'done' > '{id}.txt'; exit 0";

        // A RED task-preflight: exits non-zero. The preflights/ folder enforces a `catches:` declaration
        // (GR2027), so the body OPENS with a catches: comment.
        private static string RedPreflightBody() => OperatingSystem.IsWindows()
            ? "# catches: the producer's contribution is absent in this consumer's inherited bytes\n" +
              "Write-Output 'producer contribution absent'\nexit 1"
            : "# catches: the producer's contribution is absent in this consumer's inherited bytes\n" +
              "echo 'producer contribution absent'\nexit 1";

        // A GREEN task-preflight: the producer's contribution is present, so the gate passes (exit 0).
        private static string GreenPreflightBody() => OperatingSystem.IsWindows()
            ? "# catches: the producer's contribution is absent in this consumer's inherited bytes\nexit 0"
            : "# catches: the producer's contribution is absent in this consumer's inherited bytes\nexit 0";

        private static void WriteScript(string path, string body)
        {
            string content = OperatingSystem.IsWindows() ? body + "\n" : "#!/usr/bin/env bash\n" + body + "\n";
            File.WriteAllText(path, content);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
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
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                throw new InvalidOperationException(
                    $"git {string.Join(" ", args)} (in {workingDir}) exited {proc.ExitCode}: {stderr.Trim()}");
            return stdout;
        }

        public void Dispose()
        {
            ForceDelete(_root);
            // The factory places segment worktrees under a global temp root keyed by the plan dir path;
            // remove it too so a worktree-mode run leaves nothing behind.
            if (_plan is not null)
            {
                ForceDelete(SchedulerFactory.WorktreeRootFor(_plan));
            }
        }

        private static void ForceDelete(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                        File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch { /* best-effort teardown */ }
        }
    }

    /// <summary>
    /// The SSOT §7 exit code a real <c>run</c> derives from this report — the exact terminal mapping in
    /// <c>RunCommand</c>: an infrastructure abort is 1, a Ctrl+C cancel is 3, an all-green run is 0, and
    /// anything else (a needs-human / blocked task) is 2.
    /// </summary>
    private static int ExitCodeFor(RunReport report) =>
        report.Aborted ? ExitCodes.HarnessError
        : report.Cancelled ? ExitCodes.Cancelled
        : report.AllSucceeded ? ExitCodes.Success
        : ExitCodes.TaskFailed;

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // Behaviour 1 — NO-BURN short-circuit. A RED tasks/<id>/preflights/ settles the consumer to
    // needs-human BEFORE the attempt loop is ever entered, so NO attempt is burned. This zero-attempt
    // property is the whole point of the feature and holds in BOTH serial and worktree mode.
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(1)] // serial     — MaxParallelism = 1
    [InlineData(2)] // worktree   — MaxParallelism > 1
    [Trait("Category", "Preflights")]
    public async Task RedTaskPreflight_SettlesNeedsHuman_WithoutBurningAnAttempt(int maxParallelism)
    {
        // producer delivers; consumer's RED preflight reports the producer's contribution absent.
        using var plan = new PreflightPlan(maxParallelism, defaultRetries: 2)
            .AddTask("01-producer")
            .AddTask("02-consumer", Preflight.Red, dependsOn: "01-producer");

        RunReport report = await plan.RunAsync();

        // The dependency ran; the preflight fires AFTER it, in the consumer's own segment/workspace.
        Assert.Equal(TaskOutcome.Succeeded, report.Tasks.Single(t => t.TaskId == "01-producer").Outcome);

        TaskResult consumer = report.Tasks.Single(t => t.TaskId == "02-consumer");
        Assert.Equal(TaskOutcome.NeedsHuman, consumer.Outcome);

        JournalDocument journal = plan.Journal();
        Assert.True(journal.Tasks.ContainsKey("02-consumer"), "the consumer must be journaled");
        TaskJournalEntry consumerEntry = journal.Tasks["02-consumer"];
        Assert.Equal(JournalTaskStatus.NeedsHuman, consumerEntry.Status);

        // ── THE NO-BURN PROPERTY (the whole feature) ──────────────────────────────────────────────
        // A red task-preflight short-circuits to needs-human WITHOUT entering the attempt loop, so the
        // consumer's Attempts list is EMPTY (attempt count == 0). The settle outcome is deliverable 4's
        // AttemptOutcome.TaskPreflightFailed (serialized "task-preflight-failed").
        Assert.Empty(consumerEntry.Attempts);
        Assert.True(consumerEntry.Attempts.Count == 0,
            $"NO-BURN violated: a red task-preflight must settle the consumer to needs-human with the " +
            $"{nameof(AttemptOutcome.TaskPreflightFailed)} outcome ('task-preflight-failed') WITHOUT " +
            $"entering the attempt loop, but {consumerEntry.Attempts.Count} attempt(s) were burned.");
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // Behaviours 2 + 3 — CONE ISOLATION and EXIT 2. A RED task-preflight blocks ONLY the consumer's
    // transitive cone; an INDEPENDENT branch runs to completion; and the run as a whole exits 2.
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(1)] // serial     — MaxParallelism = 1
    [InlineData(2)] // worktree   — MaxParallelism > 1
    [Trait("Category", "Preflights")]
    public async Task RedTaskPreflight_BlocksConeButIndependentBranchCompletes_AndRunExitsTwo(int maxParallelism)
    {
        //   01-producer ─▶ 02-consumer(RED preflight) ─▶ 03-dependent   (the blocked cone)
        //   04-independent                                              (a separate leaf — must complete)
        using var plan = new PreflightPlan(maxParallelism, defaultRetries: 2)
            .AddTask("01-producer")
            .AddTask("02-consumer", Preflight.Red, dependsOn: "01-producer")
            .AddTask("03-dependent", dependsOn: "02-consumer")
            .AddTask("04-independent");

        RunReport report = await plan.RunAsync();
        JournalDocument journal = plan.Journal();

        // The consumer short-circuits to needs-human (with no attempt burned).
        Assert.Equal(TaskOutcome.NeedsHuman, report.Tasks.Single(t => t.TaskId == "02-consumer").Outcome);
        Assert.Equal(JournalTaskStatus.NeedsHuman, journal.Tasks["02-consumer"].Status);
        Assert.Empty(journal.Tasks["02-consumer"].Attempts);

        // Cone isolation: the consumer's transitive dependent is BLOCKED and never runs.
        Assert.Equal(TaskOutcome.Blocked, report.Tasks.Single(t => t.TaskId == "03-dependent").Outcome);
        Assert.Equal(JournalTaskStatus.Blocked, journal.Tasks["03-dependent"].Status);

        // ...but the independent branch is OUTSIDE the cone, so it runs to completion.
        Assert.Equal(TaskOutcome.Succeeded, report.Tasks.Single(t => t.TaskId == "04-independent").Outcome);
        Assert.Equal(JournalTaskStatus.Succeeded, journal.Tasks["04-independent"].Status);

        // Exit 2: a task-preflight that blocks a cone takes the whole run off-green.
        Assert.False(report.AllSucceeded);
        Assert.Equal(ExitCodes.TaskFailed, ExitCodeFor(report));
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // Behaviour 4 — GREEN task-preflight. A passing tasks/<id>/preflights/ lets the attempt loop
    // proceed exactly as today (no behaviour change): the consumer runs its action and succeeds.
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(1)] // serial     — MaxParallelism = 1
    [InlineData(2)] // worktree   — MaxParallelism > 1
    [Trait("Category", "Preflights")]
    public async Task GreenTaskPreflight_LetsAttemptLoopProceed(int maxParallelism)
    {
        using var plan = new PreflightPlan(maxParallelism, defaultRetries: 2)
            .AddTask("01-producer")
            .AddTask("02-consumer", Preflight.Green, dependsOn: "01-producer");

        RunReport report = await plan.RunAsync();

        // The green preflight does not block: the consumer proceeds into the attempt loop and SUCCEEDS.
        // The success itself is the proof the loop was entered — a red preflight would instead
        // short-circuit the consumer to needs-human with zero attempts. (Attempt COUNT is not asserted
        // here: a worktree-mode success settles via the deferred B1 path, which journals no attempt
        // record, so a succeeded task's Attempts list is empty in worktree mode but not in serial mode.)
        TaskResult consumer = report.Tasks.Single(t => t.TaskId == "02-consumer");
        Assert.Equal(TaskOutcome.Succeeded, consumer.Outcome);

        JournalDocument journal = plan.Journal();
        Assert.Equal(JournalTaskStatus.Succeeded, journal.Tasks["02-consumer"].Status);

        Assert.True(report.AllSucceeded);
        Assert.Equal(ExitCodes.Success, ExitCodeFor(report));
    }
}
