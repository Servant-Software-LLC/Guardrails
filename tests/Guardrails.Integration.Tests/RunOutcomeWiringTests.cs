using System.CommandLine;
using System.Diagnostics;
using Guardrails.Cli.Commands;
using Guardrails.Core.Review;

namespace Guardrails.Integration.Tests;

/// <summary>
/// TDD-RED (issue #361 Phase 4, doc 12 §1 delivery-gating + §5.2 Option P + §7.1 exit code): the observable
/// RUN OUTCOME of a <c>proceed-unreviewed</c> run, driven END-TO-END through the REAL <see cref="RunCommand"/>
/// (the #120 composition-root discipline — never a hand-injected resolver). A waved fixture plan under
/// <c>autonomyPolicy: auto</c> + <c>autonomy.gateThresholds.review-gate: "proceed-unreviewed"</c> reaches the
/// between-wave JIT checkpoint, a FAKE <c>breakdown</c>-profile prompt runner authors + self-validates a valid
/// wave-02 (NO real Claude), the run PROCEEDS UNREVIEWED, runs the fresh wave, and finalizes — then the four
/// run-outcome facts are asserted at the CLI surface.
///
/// <para>Reaching the gate mirrors the shipped <c>SchedulerReviewGateTests</c> (wave-01 authored + wave-02 an
/// empty JIT stub carrying an opt-in <c>brief.md</c> + a fake breakdown runner that authors a canned valid
/// wave), lifted to the CLI-integration level of the shipped
/// <c>SchedulerEscalationWiringTests.Cli_RunWithUnresolvedEscalation</c> (in-process <c>run</c> command, real
/// git via a Windows-safe temp-git fixture — issue #116).</para>
///
/// <para>The four facts (doc 12): (1) DELIVERY IS SUPPRESSED — a recorded <c>proceeded-unreviewed</c> decision
/// defaults <c>mergeOnSuccess</c> OFF, so <c>RunReport.WhollyGreenButUndelivered</c> is set (surfaced by the
/// CLI's loud "*** WORK NOT DELIVERED ***" warning) and the user's branch is NOT fast-forwarded; (2) a DISTINCT
/// non-zero EXIT CODE (numeric <c>5</c>, distinct from 0/2/4); (3) the run is FLAGGED "ran with N unreviewed
/// waves" in the captured stdout; (4) NO forged review marker — <c>ReviewMarker.PathFor</c>
/// (<c>state/guardrails-review.json</c>) never exists (§5 floor 3).</para>
///
/// <para>Facts (2) and (3) are wired by the sink task (10 — the distinct <c>ExitCodes.ProceededUnreviewed</c>
/// value + the "ran with N unreviewed waves" CLI rendering); against CURRENT code the run exits <c>0</c> and
/// prints no such flag, so this test FAILS now (the <c>tests-fail-on-current-code</c> gate) and goes green only
/// once task 10 lands — WITHOUT editing this file. Fact (2) asserts the LITERAL <c>5</c> (not
/// <c>ExitCodes.ProceededUnreviewed</c>, which task 10 adds) so the test compiles against current symbols, and
/// fact (3) asserts a stdout substring rather than a not-yet-added <c>RunReport</c> field — exactly how the
/// shipped <c>Cli_RunWithUnresolvedEscalation</c> asserts its numeric code.</para>
/// </summary>
public sealed class RunOutcomeWiringTests
{
    private static readonly bool Windows = OperatingSystem.IsWindows();

    [Fact]
    public async Task ProceedUnreviewed_Run_SuppressesDelivery_ExitsDistinct_FlagsUnreviewed_NoForgedMarker()
    {
        using var repo = new TempGitRepo();
        string initialHead = repo.HeadSha();
        string originalBranch = repo.CurrentBranch();

        string planDir = CreateProceedUnreviewedPlan(repo.RepoPath);

        // Drive the REAL `run` command in-process (the CLI-in-process pattern) — worktree mode (maxParallelism
        // 2 + a real git repo) so SchedulerFactory wires the WaveBreakdownInvoker over the fake `breakdown`
        // runner; auto policy so the JIT checkpoint auto-invokes it.
        (int exit, string output) = await RunViaCliAsync("run", planDir, "--no-ui", "--no-log-server");

        // ── Fact 1: DELIVERY IS SUPPRESSED (doc 12 §1 hard rule, #340) ────────────────────────────────
        // The recorded proceeded-unreviewed decision defaults mergeOnSuccess OFF (RunOutcomePolicy
        // .SuppressesDelivery), so the Scheduler's Finalize sets RunReport.WhollyGreenButUndelivered — the CLI
        // renders it as the loud "*** WORK NOT DELIVERED ***" warning — and the verified work stays on the plan
        // branch guardrails/<plan>, so the user's original branch was NOT fast-forwarded. (Both hold now: the
        // finalize flip is task 09, already wired; this proves the fixture actually reached proceed-unreviewed.)
        Assert.Contains("WORK NOT DELIVERED", output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(initialHead, repo.HeadSha());          // delivery suppressed — user branch un-advanced
        Assert.Equal(originalBranch, repo.CurrentBranch()); // still on the user's own branch (not detached)

        // ── Fact 4: NO FORGED REVIEW MARKER (§5 floor 3 — the harness never self-attests) ─────────────
        // ReviewMarker.PathFor(planDir) == <planDir>/state/guardrails-review.json must NOT exist after a
        // proceed-unreviewed run: the wave ran UNREVIEWED, and the harness never marks a wave reviewed on a
        // human's behalf at any dial setting. (An invariant that holds now and must keep holding at task 10.)
        Assert.False(File.Exists(ReviewMarker.PathFor(planDir)),
            "proceed-unreviewed ran the wave — but the harness must NOT forge state/guardrails-review.json (§5 floor 3).");

        // ── Fact 2: DISTINCT NON-ZERO EXIT CODE (doc 12 §7.1) ─────────────────────────────────────────
        // The run exits the distinct ExitCodes.ProceededUnreviewed (= 5, added by task 10), asserted as the
        // LITERAL 5 so this compiles against current symbols. It must be DISTINCT from 0 (green), 2 (a plain
        // needs-human), and 4 (EscalationsPending). Against CURRENT code the wholly-green run exits 0, so this
        // FAILS now — the load-bearing red bar for the exit-code wiring (mirrors the shipped
        // Cli_RunWithUnresolvedEscalation, which asserts its numeric code).
        Assert.Equal(5, exit);
        Assert.NotEqual(0, exit); // not green
        Assert.NotEqual(2, exit); // not a plain needs-human
        Assert.NotEqual(4, exit); // not EscalationsPending

        // ── Fact 3: FLAGGED "ran with N unreviewed waves" (§5.2 Option P / §7.1) ──────────────────────
        // The distinct-exit sink (task 10) renders the run's UnreviewedWaveCount as a "ran with N unreviewed
        // waves" flag so an automated firstmate consumer can never read it as clean green. Assert the stdout
        // "unreviewed wave" flag — absent from CURRENT output (the mid-run decision headline says only "ran
        // UNREVIEWED (N task(s))"), so this FAILS now and goes green when task 10 wires the rendering.
        Assert.Contains("unreviewed wave", output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Issue #597, the POSITIVE half of the same interlock, driven through the REAL <c>run</c> command: with
    /// <c>--merge-on-success</c> the operator override LIFTS the #361 delivery suppression and the work is
    /// actually delivered to the user's branch.
    ///
    /// <para><b>The defect this pins.</b> SSOT §5.3 has always said the default-OFF is "overridable ONLY by
    /// an explicit <c>--merge-on-success</c>", but the Scheduler gated on
    /// <c>plan.Config.MergeOnSuccessExplicit</c> — the RAW MANIFEST KEY — while the flag resolved only into
    /// <c>Config.MergeOnSuccess</c>. So the flag could not lift the suppression at all, and the
    /// "*** WORK NOT DELIVERED ***" banner recommended a command that provably could not work: the measured
    /// re-run re-ran a full terminal gate (~9 minutes) and reprinted the byte-identical banner.</para>
    ///
    /// <para>This is the exact fixture the sibling test above runs unflagged — where delivery IS suppressed
    /// and the user's branch does NOT advance — so the pair is two-sided over one scenario: same plan, one
    /// flag, opposite delivery outcomes. Against pre-#597 code THIS test fails (HEAD unchanged, the
    /// undelivered banner present); the sibling keeps passing, which is what proves the flag is the only
    /// difference.</para>
    /// </summary>
    [Fact]
    public async Task ProceedUnreviewed_Run_WithMergeOnSuccessFlag_DeliversPastTheInterlock_AndSaysSo()
    {
        using var repo = new TempGitRepo();
        string initialHead = repo.HeadSha();
        string originalBranch = repo.CurrentBranch();

        string planDir = CreateProceedUnreviewedPlan(repo.RepoPath);

        (int exit, string output) = await RunViaCliAsync(
            "run", planDir, "--no-ui", "--no-log-server", "--merge-on-success");

        // ── The override FIRED: the verified work reached the user's branch ───────────────────────────
        Assert.NotEqual(initialHead, repo.HeadSha());       // user branch advanced — delivery happened
        Assert.Equal(originalBranch, repo.CurrentBranch()); // still on the user's own branch (not detached)

        // ── And the run SAID so rather than presenting a quiet green ──────────────────────────────────
        // Forcing delivery past a machine decision is an operator override of a safety interlock; it is
        // legitimate, and it is announced.
        Assert.Contains("DELIVERY FORCED PAST A MACHINE DECISION", output, StringComparison.Ordinal);
        Assert.Contains("proceeded-unreviewed", output, StringComparison.Ordinal);

        // ── The undelivered banner must NOT print: nothing is stranded ────────────────────────────────
        Assert.DoesNotContain("WORK NOT DELIVERED", output, StringComparison.OrdinalIgnoreCase);

        // ── The unreviewed flag is INDELIBLE — delivering does not launder it ─────────────────────────
        // Exit 5 (ExitCodes.ProceededUnreviewed) and the "ran with N unreviewed waves" flag stand, because
        // the run really did proceed through a wave no human reviewed. The override changes where the work
        // LIVES, never what the run IS.
        Assert.Equal(5, exit);
        Assert.Contains("unreviewed wave", output, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(ReviewMarker.PathFor(planDir)),
            "delivering under --merge-on-success must still NOT forge state/guardrails-review.json (§5 floor 3).");
    }

    // ── The #120 driver: the REAL `run` command in-process (never `new Scheduler(...)`) ───────────────

    private static async Task<(int ExitCode, string Output)> RunViaCliAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("run-outcome-wiring test root");
        root.Add(RunCommand.Create(io));
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    // ── The fixture: a real on-disk waved plan whose `breakdown` runner is a FAKE, deterministic CLI ───
    //  Everything lives under the temp git repo (never this task's worktree), so nothing leaks into the
    //  write-scope git diff (#253). Mirrors SchedulerReviewGateTests (wave-01 authored + wave-02 JIT stub
    //  + fake breakdown that authors a canned valid wave) and SchedulerEscalationWiringTests (fake CLI).

    /// <summary>
    /// Build the proceed-unreviewed plan at <c>&lt;repoPath&gt;/plan</c>: <c>workspace: ".."</c> +
    /// <c>maxParallelism: 2</c> (worktree mode), <c>autonomyPolicy: auto</c>, and the Option-P opt-in
    /// <c>autonomy.gateThresholds.review-gate: "proceed-unreviewed"</c>. wave-01 is an authored green script
    /// task; wave-02 is an EMPTY JIT stub carrying a human-authored <c>brief.md</c>. A FAKE <c>breakdown</c>
    /// prompt runner (authors a canned valid wave-02, then returns success) is registered in
    /// <c>promptRunners</c> so the real <c>SchedulerFactory</c> builds the breakdown invoker over it — NO real
    /// Claude process.
    /// </summary>
    private static string CreateProceedUnreviewedPlan(string repoPath)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));

        // The fake breakdown CLI lives OUTSIDE the plan dir (repo root) so it never interacts with plan
        // loading; it authors wave-02 into its process working directory (= plan.PlanDirectory, the breakdown
        // invocation's WorkingDirectory).
        string breakdownCli = WriteFakeBreakdownCli(repoPath);
        string breakdownCmd = breakdownCli.Replace("\\", "\\\\"); // JSON-escape backslashes in the command path

        // mergeOnSuccess is OMITTED (defaults ON, #340) — so delivery is suppressed PURELY by the
        // proceeded-unreviewed decision (RunOutcomePolicy.SuppressesDelivery), the faithful §1 delivery-gating
        // test. review-gate proceed-unreviewed at the DEFAULT dial is NOT a GR2040 compound (that needs
        // critical). autonomyPolicy auto so the JIT checkpoint auto-invokes the breakdown.
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            $$"""
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 0,
              "maxParallelism": 2,
              "autonomyPolicy": "auto",
              "autonomy": {
                "gateThresholds": { "review-gate": "proceed-unreviewed" }
              },
              "promptRunners": {
                "default": "breakdown",
                "breakdown": {
                  "command": "{{breakdownCmd}}",
                  "permissionMode": "acceptEdits",
                  "allowedTools": ["Read", "Write", "Edit", "Bash"],
                  "maxTurns": 5
                }
              }
            }
            """);

        // wave-01: authored, a real green script task (runs on the plan branch before the checkpoint).
        WriteGreenScriptTask(Path.Combine(planDir, "wave-01-scaffold", "tasks", "01-config"));

        // wave-02: an EMPTY JIT stub (tasks/ present but empty) + the opt-in human-authored brief.md.
        Directory.CreateDirectory(Path.Combine(planDir, "wave-02-build", "tasks"));
        File.WriteAllText(Path.Combine(planDir, "wave-02-build", "brief.md"),
            "# wave-02-build\nBuild the compiled artifact from wave-01's config.\n");

        return planDir;
    }

    /// <summary>Write a trivially-green script task (exit-0 action + exit-0 guardrail) — OS-picked flavour.</summary>
    private static void WriteGreenScriptTask(string taskDir)
    {
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            """{ "description": "green script task", "writeScope": [], "dependsOn": [] }""");

        if (Windows)
        {
            File.WriteAllText(Path.Combine(taskDir, "action.ps1"), "exit 0\r\n");
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-check.ps1"), "exit 0\r\n");
        }
        else
        {
            WriteExecutable(Path.Combine(taskDir, "action.sh"), "#!/usr/bin/env bash\nexit 0\n");
            WriteExecutable(Path.Combine(taskDir, "guardrails", "01-check.sh"), "#!/usr/bin/env bash\nexit 0\n");
        }
    }

    /// <summary>
    /// Write the FAKE <c>breakdown</c> CLI (a <c>.cmd</c>+<c>.ps1</c> on Windows, a <c>.sh</c> elsewhere).
    /// It drains stdin, AUTHORS a canned VALID single-task wave-02 into its working directory (=
    /// plan.PlanDirectory), and emits a terminal stream-json <c>result</c> line the <c>ClaudePromptRunner</c>
    /// parses as success — NO real Claude process. Mirrors <c>SchedulerEscalationWiringTests</c>'s fake CLI +
    /// <c>SchedulerReviewGateTests.AuthorValidWave</c>. Returns the command path.
    /// </summary>
    private static string WriteFakeBreakdownCli(string dir)
    {
        if (Windows)
        {
            string cmdPath = Path.Combine(dir, "breakdown.cmd");
            string ps1Path = Path.Combine(dir, "breakdown.ps1");
            File.WriteAllText(ps1Path, BreakdownPsBody);
            File.WriteAllText(cmdPath,
                $"@echo off\r\npwsh -NoProfile -ExecutionPolicy Bypass -File \"{ps1Path}\"\r\n");
            return cmdPath;
        }

        string shPath = Path.Combine(dir, "breakdown.sh");
        WriteExecutable(shPath, BreakdownBashBody);
        return shPath;
    }

    // Authors <cwd>/wave-02-build/tasks/01-compile/{task.json, action.ps1, guardrails/01-check.ps1}. The cwd is
    // the breakdown invocation's WorkingDirectory (= plan.PlanDirectory), so relative authoring lands in the
    // plan folder. A single-leaf wave needs no wave-level exit gate (GR2028 applies only to parallel waves).
    private const string BreakdownPsBody =
        """
        $null = [Console]::In.ReadToEnd()
        $planDir = (Get-Location).Path
        $taskDir = Join-Path $planDir 'wave-02-build\tasks\01-compile'
        $guardDir = Join-Path $taskDir 'guardrails'
        New-Item -ItemType Directory -Force -Path $guardDir | Out-Null
        Set-Content -NoNewline -Path (Join-Path $taskDir 'task.json') -Value '{ "description": "compile", "writeScope": [], "dependsOn": [] }'
        Set-Content -Path (Join-Path $taskDir 'action.ps1') -Value 'exit 0'
        Set-Content -Path (Join-Path $guardDir '01-check.ps1') -Value 'exit 0'
        Write-Output '{"type":"result","is_error":false,"result":"authored wave-02","num_turns":1}'
        """;

    private const string BreakdownBashBody =
        """
        #!/usr/bin/env bash
        cat > /dev/null
        planDir="$(pwd)"
        taskDir="$planDir/wave-02-build/tasks/01-compile"
        mkdir -p "$taskDir/guardrails"
        printf '%s' '{ "description": "compile", "writeScope": [], "dependsOn": [] }' > "$taskDir/task.json"
        printf '%s\n' '#!/usr/bin/env bash' 'exit 0' > "$taskDir/action.sh"
        chmod +x "$taskDir/action.sh"
        printf '%s\n' '#!/usr/bin/env bash' 'exit 0' > "$taskDir/guardrails/01-check.sh"
        chmod +x "$taskDir/guardrails/01-check.sh"
        printf '%s\n' '{"type":"result","is_error":false,"result":"authored wave-02","num_turns":1}'
        """;

    private static void WriteExecutable(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    // ── Windows-safe temp git repo (issue #116) — strips read-only bits before delete (.git/objects are
    //  read-only on Windows), Windows-portable teardown. Mirrors the proven shipped TempGitRepo pattern.

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;

        public string RepoPath { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-rowt-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            Directory.CreateDirectory(RepoPath);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# run-outcome-wiring test");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        public string CurrentBranch() => Git(RepoPath, "rev-parse", "--abbrev-ref", "HEAD").Trim();

        public string HeadSha() => Git(RepoPath, "rev-parse", "HEAD").Trim();

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
            // Windows-safe: git marks loose objects under .git/objects read-only. Strip all bits before delete.
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
