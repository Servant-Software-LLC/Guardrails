using System.Diagnostics;
using System.Reflection;
using Guardrails.Core.Execution;
using Guardrails.Core.Loading;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Composition-root wiring tests for the #120-class pattern: the production
/// <see cref="SchedulerFactory.Create"/> must wire an <see cref="IReVerifier"/> into every
/// <see cref="Scheduler"/> it builds — <b>unconditionally</b>, in serial mode as well as worktree
/// mode. Both tests drive the REAL factory against a committed fixture plan and assert (by
/// reflection on the Scheduler's private <c>IReVerifier? _reVerifier</c> field) that it is non-null.
///
/// <para>
/// Today the factory constructs the re-verifier ONLY inside its
/// <c>maxParallelism &gt; 1 &amp;&amp; IsGitRepository(...)</c> guard (SchedulerFactory.cs), so a
/// serial (<c>maxParallelism = 1</c>) run leaves <c>_reVerifier</c> null. The serial-mode
/// <see cref="Factory_WiresReVerifier_InSerialMode"/> therefore FAILS on current code — that RED bar
/// is intentional and is what makes the test meaningful. The worktree-mode
/// <see cref="Factory_WiresReVerifier_InWorktreeMode"/> already passes and stands as the
/// no-regression case.
/// </para>
///
/// <para>
/// These are tagged <c>[Trait("Category", "Preflights")]</c> — a NEW trait convention for this plan;
/// the baseline root run excludes it via <c>--filter "Category!=Preflights"</c>.
/// </para>
///
/// <para>
/// Both tests go through <see cref="SchedulerFactory.Create"/> so they observe what PRODUCTION wires.
/// Constructing the Scheduler by hand with an injected re-verifier is FORBIDDEN: it would pass even
/// against an unwired factory and defeat the test's purpose. Tests only — no production wiring is
/// implemented here.
/// </para>
/// </summary>
public sealed class ReVerifierWiringTests
{
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // TempGitRepo — Windows-safe temp repo (strips read-only bits before delete). Copied from the
    // ProductionWiringTests / WiringDefectRegressionTests git-worktree fixture pattern to keep this
    // test file self-contained (the scope boundary limits edits to this one file).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;

        public string RepoPath { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-rvw-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(_root, "repo");
            Directory.CreateDirectory(RepoPath);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# reverifier-wiring-test");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
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
            try
            {
                if (Directory.Exists(_root))
                {
                    foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                        File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch { /* best-effort teardown */ }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Reflection accessor for the Scheduler's private IReVerifier field (the exact GetField the
    // task specifies).
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static FieldInfo ReVerifierField()
    {
        FieldInfo? field = typeof(Scheduler).GetField(
            "_reVerifier", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!;
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Fixture helpers — a minimal, committed, single-task script-only plan. workspace ".." points at
    // the repo root, so the factory's IsGitRepository(workspace) probe sees a real git working tree.
    // The plan is only inspected for wiring — it is never run — so the action/guardrail bodies are
    // trivial exit-0 scripts.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    private static string CreateFixturePlan(string repoPath, int maxParallelism, string folder)
    {
        string planDir = Path.Combine(repoPath, folder);
        Directory.CreateDirectory(planDir);
        Directory.CreateDirectory(Path.Combine(planDir, "state"));
        Directory.CreateDirectory(Path.Combine(planDir, "tasks"));

        File.WriteAllText(Path.Combine(planDir, "guardrails.json"),
            $$"""
            {
              "version": 1,
              "guardrailMode": "failFast",
              "workspace": "..",
              "defaultRetries": 0,
              "maxParallelism": {{maxParallelism}}
            }
            """);

        WriteTask(planDir, "01-task-a");

        // Commit the plan definition so the fixture is a real, committed plan inside the repo.
        TempGitRepo.Git(repoPath, "add", ".");
        TempGitRepo.Git(repoPath, "commit", "-m", $"Add {folder} fixture");

        return planDir;
    }

    private static void WriteTask(string planDir, string taskId)
    {
        string taskDir = Path.Combine(planDir, "tasks", taskId);
        Directory.CreateDirectory(taskDir);
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            $$"""
            {
              "description": "reverifier wiring fixture {{taskId}}",
              "dependsOn": []
            }
            """);

        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(taskDir, "action.ps1"), "exit 0\n");
            File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-check.ps1"), "exit 0\n");
        }
        else
        {
            string actionPath = Path.Combine(taskDir, "action.sh");
            File.WriteAllText(actionPath, "#!/usr/bin/env bash\nexit 0\n");
            File.SetUnixFileMode(actionPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            string guardrailPath = Path.Combine(taskDir, "guardrails", "01-check.sh");
            File.WriteAllText(guardrailPath, "#!/usr/bin/env bash\nexit 0\n");
            File.SetUnixFileMode(guardrailPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Behavior 1 (RED on current code): serial mode (maxParallelism = 1) must still wire IReVerifier.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drives the REAL <see cref="SchedulerFactory.Create"/> against a committed fixture plan at
    /// <c>maxParallelism = 1</c> (serial mode) and asserts, via reflection on the Scheduler's private
    /// <c>IReVerifier? _reVerifier</c> field, that it is non-null.
    ///
    /// <para>
    /// This FAILS on current code: the factory constructs the re-verifier only inside its
    /// <c>maxParallelism &gt; 1 &amp;&amp; IsGitRepository(...)</c> guard, so at
    /// <c>maxParallelism = 1</c> the field is null. The RED bar is intentional — it proves the
    /// re-verifier is NOT yet wired unconditionally (the #120 composition-root gap this plan closes).
    /// The wiring change is NOT implemented here.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Preflights")]
    public void Factory_WiresReVerifier_InSerialMode()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateFixturePlan(repo.RepoPath, maxParallelism: 1, folder: "serial-plan");

        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors,
            "Serial-mode fixture plan must load cleanly: " + string.Join("\n", load.Diagnostics));

        // Drive the REAL production factory — no manual provider/re-verifier injection. Injecting one
        // would pass even against an unwired factory and is FORBIDDEN; this must observe what
        // PRODUCTION wires.
        Scheduler scheduler = SchedulerFactory.Create(
            load.Plan!,
            new ProcessRunner(),
            new PathExecutableProbe(),
            IRunObserver.Null);

        object? reVerifier = ReVerifierField().GetValue(scheduler);
        Assert.NotNull(reVerifier);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Behavior 2 (no-regression, GREEN on current code): worktree mode (maxParallelism > 1, git repo)
    // must wire IReVerifier too.
    // ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drives the REAL <see cref="SchedulerFactory.Create"/> against a committed fixture plan at
    /// <c>maxParallelism = 2</c> inside a git repository (worktree mode) and asserts, via reflection
    /// on the Scheduler's private <c>IReVerifier? _reVerifier</c> field, that it is non-null.
    ///
    /// <para>
    /// This passes on current code (the factory already wires a re-verifier in worktree mode) and is
    /// the no-regression guard: unconditional wiring must not drop the re-verifier from the path that
    /// already had it.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Preflights")]
    public void Factory_WiresReVerifier_InWorktreeMode()
    {
        using var repo = new TempGitRepo();
        string planDir = CreateFixturePlan(repo.RepoPath, maxParallelism: 2, folder: "worktree-plan");

        PlanLoadResult load = new PlanLoader().Load(planDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors,
            "Worktree-mode fixture plan must load cleanly: " + string.Join("\n", load.Diagnostics));

        Scheduler scheduler = SchedulerFactory.Create(
            load.Plan!,
            new ProcessRunner(),
            new PathExecutableProbe(),
            IRunObserver.Null);

        object? reVerifier = ReVerifierField().GetValue(scheduler);
        Assert.NotNull(reVerifier);
    }
}
