using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.Supply;

/// <summary>
/// Design 41 §2.2 — the harness-computed facts, against REAL git repositories. Every git call is
/// tri-state: present, absent, or error — and an error is never read as absent. A faked git would prove
/// nothing about the one thing this component is: a reader of git.
/// <para>
/// TDD red: <see cref="MissingResourceFacts.Compute"/> currently throws <see cref="NotImplementedException"/>
/// unconditionally, so every test below is expected to FAIL against the stub. Do not add
/// <c>Assert.Throws&lt;NotImplementedException&gt;</c> wrappers — that would make these pass against the
/// stub, which defeats the point of pinning them red.
/// </para>
/// </summary>
[Trait("Category", "OverwatchSupply")]
public sealed class MissingResourceFactsTests : IDisposable
{
    private const string ResourcePath = "vendor/resource.js";

    // The operator's checkout carries the shared resource path from construction — rows that read its
    // committed and working-tree state depend on that. The integration repo is the run's own BASE and
    // must start WITHOUT it: a fixture that seeded both roles identically would make check 5
    // (present-on-run-base) fire first for every row that needs the path missing from the base,
    // shadowing checks 6/8/9/10 and making them unsatisfiable under any implementation.
    private readonly TempGitRepo _checkout = new(seedResource: true);
    private readonly TempGitRepo _integration = new(seedResource: false);

    // ── run-level lineage facts — refuse EVERY named path, not just one ─────────────────────────────

    [Fact]
    public void Compute_RefusesEveryPath_WhenTheCheckoutIsNotOnTheRunBranch()
    {
        MissingResourceFactsResult result = Run(
            paths: ["vendor/a.js", "vendor/b.js"],
            originalBranch: "some-other-branch");

        Assert.Equal(2, result.Verdicts.Count);
        Assert.All(result.Verdicts, v => Assert.Equal("checkout-not-on-run-branch", v.Reason));
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Compute_RefusesEveryPath_WhenTheCheckoutHasLeftTheRunsStartingHistory()
    {
        string orphaned = _checkout.HeadSha();
        _checkout.ResetHard("HEAD~1");

        MissingResourceFactsResult result = Run(
            paths: ["vendor/a.js", "vendor/b.js"],
            originalHeadSha: orphaned);

        Assert.Equal(2, result.Verdicts.Count);
        Assert.All(result.Verdicts, v => Assert.Equal("checkout-diverged", v.Reason));
        Assert.Empty(result.Candidates);
    }

    // ── per-path facts, in §2.2's order ─────────────────────────────────────────────────────────────

    [Fact]
    public void Compute_RefusesAPathThatEscapesTheWorkspace() =>
        Assert.Equal("escapes-workspace", ReasonFor("../outside/x.js"));

    [Fact]
    public void Compute_RefusesAProtectedPath() =>
        Assert.Equal("protected-path", ReasonFor(".claude/settings.json"));

    [Fact]
    public void Compute_RefusesAPathUnderThePlanFolder() =>
        Assert.Equal("under-plan-folder", ReasonFor("docs/plans/41-x/tasks/01/action.prompt.md"));

    [Fact]
    public void Compute_FailsClosed_WhenAnotherTaskDeclaresNoWriteScope() =>
        Assert.Equal("plan-scope-incomplete", ReasonFor(ResourcePath, unscopedSibling: true));

    [Fact]
    public void Compute_RefusesAPathAnotherTaskMayProduce() =>
        Assert.Equal("produced-by-another-task", ReasonFor(ResourcePath, siblingScope: "vendor/**"));

    [Fact]
    public void Compute_ReturnsACandidate_WhenTheHaltedTasksOwnWriteScopeCoversThePath()
    {
        // Review decision d41-candidate-scope: check 4 asks whether every OTHER task's writeScope covers
        // the path — never the halted task's own. A task that embeds a vendored bundle declares the HTML
        // it writes, not the bundle, so the halted task's OWN scope covering the missing file must not
        // refuse it. The sibling here does NOT cover the path, so this row only passes if the halted
        // task's own scope was excluded from the ownership scan (an `Any` over ALL tasks, including the
        // halted one, would wrongly refuse this as "produced-by-another-task" / self).
        MissingResourceFactsResult result = Run(haltedWriteScope: [ResourcePath]);

        MissingResourceCandidate candidate = Assert.Single(result.Candidates);
        Assert.Equal(ResourcePath, candidate.Path);
    }

    [Fact]
    public void Compute_RefusesAPathAlreadyPresentOnTheRunBase()
    {
        _integration.WriteFile(ResourcePath, "already here");
        _integration.Commit("base already carries it");

        Assert.Equal("present-on-run-base", ReasonFor(ResourcePath));
    }

    [Fact]
    public void Compute_RefusesAPathWithACaseOnlyTwinOnTheRunBase()
    {
        _integration.WriteFile("vendor/Resource.js", "the twin");
        _integration.Commit("a case-only twin");

        Assert.Equal("case-collision", ReasonFor(ResourcePath));
    }

    [Fact]
    public void Compute_RefusesAPathThisRunDeleted()
    {
        _integration.WriteFile(ResourcePath, "about to go");
        _integration.Commit("add");
        _integration.Git("rm", ResourcePath);
        _integration.Commit("the run removed it deliberately");

        Assert.Equal("deleted-on-run-base", ReasonFor(ResourcePath));
    }

    [Fact]
    public void Compute_RefusesAPathNotCommittedInTheCheckout()
    {
        _checkout.WriteFile("vendor/uncommitted.js", "never committed");

        Assert.Equal("not-committed-in-checkout", ReasonFor("vendor/uncommitted.js"));
    }

    [Fact]
    public void Compute_RefusesAPathThatIsNotABlob()
    {
        // vendor/tree.js resolves to a TREE at the checkout's HEAD, not a blob.
        _checkout.WriteFile("vendor/tree.js/inner.txt", "a directory wearing a file's name");
        _checkout.Commit("a tree where a blob was asked for");

        Assert.Equal("not-a-blob", ReasonFor("vendor/tree.js"));
    }

    [Fact]
    public void Compute_RefusesAPathModifiedInTheCheckoutsWorkingTree()
    {
        _checkout.WriteFile(ResourcePath, "edited after the commit");

        Assert.Equal("modified-in-checkout", ReasonFor(ResourcePath));
    }

    // ── the tri-state stop, and the positive ────────────────────────────────────────────────────────

    [Fact]
    public void Compute_StopsWithFactsUnavailable_WhenAGitCallErrors_NeverReadingItAsAbsent()
    {
        // Not a git repository at all: a git call against it (e.g. `git rev-parse`) exits 128 on every
        // platform — a real, deterministic ERROR, never "absent". Reading it as absent would supply a
        // file on evidence the harness never actually had.
        string notARepo = Path.Combine(Path.GetTempPath(), "gr-not-a-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(notARepo);

        MissingResourceFactsResult result = Run(integrationWorktreePath: notARepo);

        Assert.False(result.Available);
        Assert.Equal("facts-unavailable", result.UnavailableReason);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Compute_ReturnsACandidateCarryingItsCheckoutSha_ForACommittedUnownedPathMissingFromTheRunBase()
    {
        MissingResourceFactsResult result = Run();

        MissingResourceCandidate candidate = Assert.Single(result.Candidates);
        Assert.Equal(ResourcePath, candidate.Path);
        Assert.Equal(_checkout.HeadSha(), candidate.SourceCommit);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────

    private string? ReasonFor(string path, bool unscopedSibling = false, string? siblingScope = null) =>
        Run(path: path, unscopedSibling: unscopedSibling, siblingScope: siblingScope).Verdicts[0].Reason;

    private MissingResourceFactsResult Run(
        string path = ResourcePath,
        IReadOnlyList<string>? paths = null,
        string? originalBranch = null,
        string? originalHeadSha = null,
        string? integrationWorktreePath = null,
        bool unscopedSibling = false,
        string? siblingScope = null,
        IReadOnlyList<string>? haltedWriteScope = null)
    {
        TaskNode halted = MakeTask("02-needs-resource", haltedWriteScope);
        TaskNode sibling = MakeTask(
            "04-owns-vendor",
            unscopedSibling ? null : [siblingScope ?? "unrelated/**"]);

        var plan = new PlanDefinition
        {
            PlanDirectory = Path.Combine(_checkout.RepoPath, "docs", "plans", "41-x"),
            Workspace = _checkout.RepoPath,
            Config = new RunConfig { Version = 1 },
            Tasks = [halted, sibling]
        };

        return MissingResourceFacts.Compute(
            plan,
            halted,
            paths ?? [path],
            integrationWorktreePath ?? _integration.RepoPath,
            originalBranch ?? _checkout.CurrentBranch(),
            originalHeadSha ?? _checkout.HeadSha());
    }

    private TaskNode MakeTask(string id, IReadOnlyList<string>? writeScope) => new()
    {
        Id = id,
        Directory = _checkout.RepoPath,
        Description = "t",
        Action = new ActionDefinition
        {
            Path = Path.Combine(_checkout.RepoPath, "action.prompt.md"),
            Kind = ActionKind.Prompt
        },
        Guardrails =
        [
            new GuardrailDefinition
            {
                Name = "01-check",
                Path = Path.Combine(_checkout.RepoPath, "guardrails", "01-check.ps1"),
                Kind = ActionKind.Script
            }
        ],
        WriteScope = writeScope
    };

    public void Dispose()
    {
        _checkout.Dispose();
        _integration.Dispose();
    }

    // ── a real git repository, not a fake of one ────────────────────────────────────────────────────

    /// <summary>
    /// A throwaway single-use git repository in a temp directory (mirrors the private nested fixture
    /// already duplicated in <see cref="SuppliedDrainTests"/> and <see cref="OverwatchSupplyAutoResolveTests"/> —
    /// this project has no shared temp-repo helper). <paramref name="seedResource"/> controls whether the
    /// fixture commits <c>vendor/resource.js</c> at construction: the checkout needs it from the start,
    /// but the run's own base must start WITHOUT it.
    /// <para>
    /// Four Windows behaviours this repo has been bitten by before, all handled here: git marks loose
    /// objects under <c>.git/objects</c> READ-ONLY, so disposal goes through <see cref="SafeDelete"/>
    /// rather than a plain recursive delete; <c>core.autocrlf</c> is forced off so a host's line-ending
    /// translation can never turn a clean file into a "modified" one; <c>core.hooksPath</c> points at an
    /// empty directory inside <c>.git</c> so a machine-global hook can never gate this throwaway repo; and
    /// <see cref="WriteFile"/> recreates a pruned parent directory before writing, since Git-for-Windows
    /// prunes the emptied parent on <c>git rm</c>.
    /// </para>
    /// </summary>
    private sealed class TempGitRepo : IDisposable
    {
        internal string RepoPath { get; } =
            Path.Combine(Path.GetTempPath(), "gr-missing-resource-facts-" + Guid.NewGuid().ToString("N"));

        internal TempGitRepo(bool seedResource)
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

            WriteFile("README.md", "# fixture repo");
            Commit("Initial commit");

            if (seedResource)
            {
                WriteFile(ResourcePath, "export const ok = true;");
                Commit("Commit the vendored resource");
            }
        }

        internal string HeadSha() => Git("rev-parse", "HEAD").Trim();

        internal string CurrentBranch() => Git("rev-parse", "--abbrev-ref", "HEAD").Trim();

        /// <summary>Write a file, recreating its parent if git pruned it (the empty-dir prune on git rm).</summary>
        internal void WriteFile(string relativePath, string content)
        {
            string full = Path.Combine(RepoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        internal void Commit(string message)
        {
            Git("add", "-A");
            Git("commit", "-m", message);
        }

        /// <summary>Roll back. reset --hard is the reliable rollback; the merge-abort form exits 128 on a dirtied tracked path.</summary>
        internal void ResetHard(string commitish) => Git("reset", "--hard", commitish);

        internal string Git(params string[] arguments)
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
