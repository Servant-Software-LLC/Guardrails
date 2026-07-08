using System.Diagnostics;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests;

/// <summary>
/// End-to-end proof (issue #258) that the plan-root <c>.gitignore</c> the harness scaffolds at
/// run-init (<see cref="StateManager.Initialize"/>) makes a REAL <c>git</c> ignore exactly the
/// transient runtime set while keeping every committed artifact tracked. The pure-logic side
/// (content, non-clobbering, idempotency) is covered by <c>PlanGitignoreTests</c> in the Core suite;
/// here we shell out to <c>git check-ignore</c> / <c>git status --porcelain</c> so the assertion is
/// against git's actual matching, not our reading of the rules.
/// </summary>
public sealed class PlanGitignoreScaffoldTests : IDisposable
{
    private readonly string _root;
    private readonly string _repo;
    private readonly string _planDir;

    public PlanGitignoreScaffoldTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-gi-" + Guid.NewGuid().ToString("N"));
        _repo = Path.Combine(_root, "repo");
        _planDir = Path.Combine(_repo, ".claude", "plans", "myplan");
        Directory.CreateDirectory(_planDir);

        Git("init");
        Git("config", "user.email", "test@guardrails.local");
        Git("config", "user.name", "Guardrails Test");
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

    // ── git plumbing ─────────────────────────────────────────────────────────────────────────────

    private (int ExitCode, string StdOut, string StdErr) RunGit(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using Process proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, stdout, stderr);
    }

    private string Git(params string[] args)
    {
        (int code, string stdout, string stderr) = RunGit(args);
        if (code != 0)
            throw new InvalidOperationException($"git {string.Join(" ", args)} exited {code}: {stderr.Trim()}");
        return stdout;
    }

    /// <summary>True when git reports <paramref name="repoRelativePath"/> as ignored (check-ignore exit 0).</summary>
    private bool IsIgnored(string repoRelativePath) => RunGit("check-ignore", repoRelativePath).ExitCode == 0;

    private void Write(string repoRelativePath, string content)
    {
        string full = Path.Combine(_repo, repoRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    // ── the run-init that scaffolds the .gitignore ────────────────────────────────────────────────

    /// <summary>Drive the real harness run-init that scaffolds the file, plus a full transient + committed layout.</summary>
    private void SeedPlanAndInitialize()
    {
        // Committed artifacts, authored before the run.
        Write(".claude/plans/myplan/guardrails.json", "{ \"version\": 1 }");
        Write(".claude/plans/myplan/tasks/01-task/task.json", "{ \"description\": \"x\" }");
        Write(".claude/plans/myplan/state/seed.json", "{ \"k\": \"v\" }");
        Write(".claude/plans/myplan/state/guardrails-review.json", "{ \"version\": 1 }");

        // THE call under test: run-init scaffolds the plan-root .gitignore and seeds state.json.
        new StateManager(_planDir).Initialize();

        // Transient runtime state a run would produce (state.json already written by Initialize).
        Write(".claude/plans/myplan/state/run.json", "{ \"runId\": \"r1\" }");
        Write(".claude/plans/myplan/state/merge-conflicts.log", "seq\ttask\n");
        Write(".claude/plans/myplan/state/captured/01-task/Foo.cs", "ORIGINAL");
        Write(".claude/plans/myplan/logs/r1/01-task/attempt-1/action-stdout.log", "output");
    }

    private const string Base = ".claude/plans/myplan";

    private static readonly string[] TransientPaths =
    [
        $"{Base}/logs/r1/01-task/attempt-1/action-stdout.log",
        $"{Base}/state/run.json",
        $"{Base}/state/state.json",
        $"{Base}/state/merge-conflicts.log",
        $"{Base}/state/captured/01-task/Foo.cs",
    ];

    private static readonly string[] CommittedPaths =
    [
        $"{Base}/guardrails.json",
        $"{Base}/tasks/01-task/task.json",
        $"{Base}/state/seed.json",
        $"{Base}/state/guardrails-review.json",
        $"{Base}/.gitignore",
    ];

    // ── tests ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Initialize_ScaffoldsFile_AtPlanRoot()
    {
        SeedPlanAndInitialize();

        Assert.True(File.Exists(Path.Combine(_planDir, ".gitignore")),
            "run-init must scaffold a plan-root .gitignore");
    }

    [Fact]
    public void GitIgnoresEveryTransientPath()
    {
        SeedPlanAndInitialize();

        foreach (string p in TransientPaths)
            Assert.True(IsIgnored(p), $"transient runtime path must be git-ignored: {p}");
    }

    [Fact]
    public void GitDoesNotIgnoreAnyCommittedArtifact()
    {
        SeedPlanAndInitialize();

        foreach (string p in CommittedPaths)
            Assert.False(IsIgnored(p), $"committed artifact must NOT be git-ignored: {p}");
    }

    [Fact]
    public void GitAdd_StagesCommittedArtifacts_ButNoTransientState()
    {
        // The exact foot-gun from the issue: `git add <plan-folder>/` must NOT sweep in runtime state.
        SeedPlanAndInitialize();

        Git("add", "-A");
        string porcelain = Git("status", "--porcelain");
        // Porcelain emits forward-slash, repo-relative paths on every OS.
        List<string> staged = porcelain
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Length > 3 ? line[3..].Trim() : line.Trim())
            .ToList();

        foreach (string p in CommittedPaths)
            Assert.Contains(p, staged);

        foreach (string p in TransientPaths)
            Assert.DoesNotContain(p, staged);
    }

    [Fact]
    public void Initialize_DoesNotClobber_AHandAuthoredGitignore()
    {
        // A user (the issue reporter) may hand-author their own ignore file; run-init must leave it be.
        const string handAuthored = "# hand authored\n/logs/\n!keep\n";
        Directory.CreateDirectory(_planDir);
        File.WriteAllText(Path.Combine(_planDir, ".gitignore"), handAuthored);

        new StateManager(_planDir).Initialize();

        Assert.Equal(handAuthored, File.ReadAllText(Path.Combine(_planDir, ".gitignore")));
    }
}
