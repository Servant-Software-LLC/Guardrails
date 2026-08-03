using System.CommandLine;
using System.Diagnostics;
using Guardrails.Cli.Commands;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;
using Guardrails.Core.Loading;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #419 — the PROCESS-SCOPED junction, proven END-TO-END through the REAL <c>run</c> command
/// (#120 composition-root discipline). A worktree-mode run that ends in ANY state (green-delivered /
/// needs-human) leaves NO junction behind AND never journals one — the decouple + the release-on-exit
/// lifetime.
/// <para>
/// The junction is created only when the real root is long enough (#407 C lazy predicate) — usually NOT on a
/// short CI temp — so the "no junction survives" assertion is Windows-gated and, when no junction was created,
/// holds vacuously; the CROSS-OS proof is that <c>run.json</c> never carries <c>worktreeJunctionRoot</c>. When
/// a junction IS created (a long temp), the run RELEASES it on exit — so scanning the drive-root <c>.a</c>..
/// <c>.z</c> for a link to THIS run's real root and asserting none is the faithful end-to-end release proof,
/// and it can never pollute the drive because the release is the feature under test.
/// </para>
/// </summary>
public sealed class WorktreeJunctionRunTests
{
    [Fact]
    public async Task GreenRun_LeavesNoJunction_AndNeverJournalsOne()
    {
        using var repo = new TempGitRepo();
        string planDir = CreatePlan(repo.RepoPath, guardrailFails: false);

        (int exit, _) = await RunViaCliAsync("run", planDir, "--no-ui", "--no-log-server", "--no-merge-on-success");

        Assert.Equal(0, exit);
        AssertNoJournaledJunction(planDir);
        AssertNoSurvivingJunctionForPlan(planDir);
    }

    [Fact]
    public async Task NeedsHumanRun_LeavesNoJunction_AndNeverJournalsOne()
    {
        using var repo = new TempGitRepo();
        string planDir = CreatePlan(repo.RepoPath, guardrailFails: true);

        // defaultRetries 0 + a guardrail that always fails → needs-human (exit 2). The link is RELEASED even
        // for this RESUMABLE outcome (#419), while the worktree ROOT is KEPT for the resume.
        (int exit, _) = await RunViaCliAsync("run", planDir, "--no-ui", "--no-log-server", "--no-merge-on-success");

        Assert.Equal(2, exit);
        AssertNoJournaledJunction(planDir);
        AssertNoSurvivingJunctionForPlan(planDir);
    }

    [Fact]
    public async Task LongTaskId_ForcesAJunction_ThatIsCreatedThenReleasedOnExit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // junctions are Windows-only
        }

        // A deliberately LONG task id makes the #407 C lazy predicate FORCE a real junction, so this
        // exercises the FULL RunCommand → PrepareWorktreeJunction → WorktreeJunctionLifetime → release-on-exit
        // wiring on any Windows machine (a shallow-task run usually skips the junction on a short temp). The
        // captured log proves a junction was BOTH allocated AND released, and nothing is left behind.
        using var repo = new TempGitRepo();
        string planDir = CreatePlanWithLongTaskId(repo.RepoPath);

        (int exit, string output) =
            await RunViaCliAsync("run", planDir, "--no-ui", "--no-log-server", "--no-merge-on-success");

        Assert.Equal(0, exit);
        Assert.Contains("worktree junction:", output, StringComparison.Ordinal);    // a junction WAS allocated
        Assert.Contains("released on exit", output, StringComparison.Ordinal);      // ...and released (#419)
        AssertNoJournaledJunction(planDir);
        AssertNoSurvivingJunctionForPlan(planDir);
    }

    [Fact]
    public async Task Resume_WithOldJournalCarryingWorktreeJunctionRoot_ResumesClean()
    {
        using var repo = new TempGitRepo();
        string planDir = CreatePlan(repo.RepoPath, guardrailFails: false);

        (int firstExit, _) = await RunViaCliAsync("run", planDir, "--no-ui", "--no-log-server", "--no-merge-on-success");
        Assert.Equal(0, firstExit);

        // Simulate a run.json written by a PRE-#419 binary: inject the retired worktreeJunctionRoot key.
        string journalPath = Path.Combine(planDir, "state", "run.json");
        string journal = await File.ReadAllTextAsync(journalPath, TestContext.Current.CancellationToken);
        journal = journal.TrimEnd().TrimEnd('}') + $",\n  \"worktreeJunctionRoot\": \"{JunctionLiteral()}\"\n}}";
        await File.WriteAllTextAsync(journalPath, journal, TestContext.Current.CancellationToken);

        // The unknown member is skipped on load (no JsonUnmappedMemberHandling.Disallow) → the resume drains
        // clean (idempotent green), never crashing on the retired field.
        (int resumeExit, _) = await RunViaCliAsync("run", planDir, "--no-ui", "--no-log-server", "--no-merge-on-success");
        Assert.Equal(0, resumeExit);
        AssertNoJournaledJunction(planDir);
    }

    // ── assertions ───────────────────────────────────────────────────────────────────────────

    private static void AssertNoJournaledJunction(string planDir)
    {
        string journalPath = Path.Combine(planDir, "state", "run.json");
        Assert.True(File.Exists(journalPath), "run.json should exist after a run");
        Assert.DoesNotContain(
            "worktreeJunctionRoot", File.ReadAllText(journalPath), StringComparison.Ordinal);
    }

    private static void AssertNoSurvivingJunctionForPlan(string planDir)
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // junctions are Windows-only
        }

        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        string realRoot = SchedulerFactory.WorktreeRootFor(load.Plan!);
        string? drive = Path.GetPathRoot(realRoot);
        if (string.IsNullOrEmpty(drive))
        {
            return;
        }

        foreach (string leaf in WorktreeJunction.CandidateLeaves)
        {
            string link = Path.Combine(drive, leaf);
            Assert.False(
                WorktreeJunction.IsJunctionTo(link, realRoot),
                $"a worktree-mode run must leave NO junction to its real root; '{link}' still points at '{realRoot}' (#419).");
        }
    }

    private static string JunctionLiteral() =>
        OperatingSystem.IsWindows() ? @"C:\\.a" : "/tmp/.a";

    // ── the #120 driver + fixture ──────────────────────────────────────────────────────────────

    private static async Task<(int ExitCode, string Output)> RunViaCliAsync(params string[] args)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("worktree-junction-run test root");
        root.Add(RunCommand.Create(io));
        int exit = await root.Parse(args).InvokeAsync();
        return (exit, io.OutText);
    }

    /// <summary>A worktree-mode plan (maxParallelism 2 + a real git repo) with two green script tasks; the guardrail of one optionally always fails (→ needs-human).</summary>
    private static string CreatePlan(string repoPath, bool guardrailFails)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 0,
              "maxParallelism": 2
            }
            """);

        WriteGreenScriptTask(Path.Combine(planDir, "tasks", "01-first"), guardrailFails: false, dependsOn: []);
        WriteGreenScriptTask(Path.Combine(planDir, "tasks", "02-second"), guardrailFails, dependsOn: ["01-first"]);
        return planDir;
    }

    /// <summary>A worktree-mode plan whose single task has a deliberately LONG folder name, so the #407 C lazy predicate FORCES a Windows short junction.</summary>
    private static string CreatePlanWithLongTaskId(string repoPath)
    {
        string planDir = Path.Combine(repoPath, "plan");
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            """
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 0,
              "maxParallelism": 2
            }
            """);

        WriteGreenScriptTask(
            Path.Combine(planDir, "tasks", "01-force-a-windows-short-junction-allocation-for-max-path-headroom"),
            guardrailFails: false, dependsOn: []);
        return planDir;
    }

    private static void WriteGreenScriptTask(string taskDir, bool guardrailFails, string[] dependsOn)
    {
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        string depends = dependsOn.Length == 0 ? "[]" : "[" + string.Join(", ", dependsOn.Select(d => $"\"{d}\"")) + "]";
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""{ "description": "green script task", "writeScope": [], "dependsOn": {{depends}} }""");

        string guardBody = guardrailFails ? "exit 1" : "exit 0";
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(taskDir, "action.ps1"), "exit 0\r\n");
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-check.ps1"), guardBody + "\r\n");
        }
        else
        {
            WriteExecutable(Path.Combine(taskDir, "action.sh"), "#!/usr/bin/env bash\nexit 0\n");
            WriteExecutable(Path.Combine(taskDir, "guardrails", "01-check.sh"), "#!/usr/bin/env bash\n" + guardBody + "\n");
        }
    }

    private static void WriteExecutable(string path, string body)
    {
        File.WriteAllText(path, body);
        if (!OperatingSystem.IsWindows())
        {
            var psi = new ProcessStartInfo("chmod") { UseShellExecute = false };
            psi.ArgumentList.Add("+x");
            psi.ArgumentList.Add(path);
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
        }
    }

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _rootDir;
        public string RepoPath { get; }

        public TempGitRepo()
        {
            _rootDir = Path.Combine(Path.GetTempPath(), "gr-junc-run-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_rootDir, "repo");
            Directory.CreateDirectory(RepoPath);

            Git("init");
            Git("config", "user.email", "test@guardrails.local");
            Git("config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# junc-run-test");
            Git("add", ".");
            Git("commit", "-m", "Initial commit");
        }

        private void Git(params string[] args)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = RepoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);
            using var proc = Process.Start(psi)!;
            proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"git {string.Join(" ", args)} exited {proc.ExitCode}: {stderr}");
        }

        public void Dispose()
        {
            try { SafeDelete.DeleteDirectory(_rootDir); } catch { /* best-effort temp cleanup */ }
        }
    }
}
