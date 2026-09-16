// INVALID sample for 03-tokens-and-fixture.ps1 — expect exit 1.
// IDENTICAL to the .valid sample except for ONE defect: the fixture rolls back with
// git merge --abort, which exits 128 on a dirtied tracked path (logged as W3). The reason tokens
// and the other three Windows-safe constructs are all still present, so a non-zero exit here can
// only come from the negative assertion — which is the clause whose polarity is easiest to get
// wrong, because the banned text lives in a string literal.
using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.Io;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.Supply;

/// <summary>
/// Design 41 §2.2 — the harness-computed facts, against REAL git repositories. Every git call is
/// tri-state; an error is never read as absent.
/// </summary>
[Trait("Category", "OverwatchSupply")]
public sealed class MissingResourceFactsTests : IDisposable
{
    private readonly TempGitRepo _checkout = new();
    private readonly TempGitRepo _integration = new();

    // ── run-level lineage facts ────────────────────────────────────────────────────────────────
    [Fact]
    public void Compute_RefusesEveryPath_WhenTheCheckoutIsNotOnTheRunBranch()
    {
        MissingResourceFactsResult result = Run(originalBranch: "some-other-branch");

        Assert.All(result.Verdicts, v => Assert.Equal("checkout-not-on-run-branch", v.Reason));
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Compute_RefusesEveryPath_WhenTheCheckoutHasLeftTheRunsStartingHistory()
    {
        string orphaned = _checkout.HeadSha();
        _checkout.RollBack("HEAD~1");

        MissingResourceFactsResult result = Run(originalHeadSha: orphaned);

        Assert.All(result.Verdicts, v => Assert.Equal("checkout-diverged", v.Reason));
    }

    // ── per-path facts, in §2.2's order ────────────────────────────────────────────────────────
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
        Assert.Equal("plan-scope-incomplete", ReasonFor("vendor/resource.js", unscopedSibling: true));

    [Fact]
    public void Compute_RefusesAPathAnotherTaskMayProduce() =>
        Assert.Equal("produced-by-another-task", ReasonFor("vendor/resource.js", siblingScope: "vendor/**"));

    [Fact]
    public void Compute_RefusesAPathAlreadyPresentOnTheRunBase()
    {
        _integration.WriteFile("vendor/resource.js", "already here");
        _integration.Commit("base already carries it");

        Assert.Equal("present-on-run-base", ReasonFor("vendor/resource.js"));
    }

    [Fact]
    public void Compute_RefusesAPathWithACaseOnlyTwinOnTheRunBase()
    {
        _integration.WriteFile("vendor/Resource.js", "the twin");
        _integration.Commit("a case-only twin");

        Assert.Equal("case-collision", ReasonFor("vendor/resource.js"));
    }

    [Fact]
    public void Compute_RefusesAPathThisRunDeleted()
    {
        _integration.WriteFile("vendor/resource.js", "about to go");
        _integration.Commit("add");
        _integration.Git("rm", "vendor/resource.js");
        _integration.Commit("the run removed it deliberately");

        Assert.Equal("deleted-on-run-base", ReasonFor("vendor/resource.js"));
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
        // vendor/tree.js resolves to a TREE at HEAD, not a blob.
        _checkout.WriteFile("vendor/tree.js/inner.txt", "a directory wearing a file's name");
        _checkout.Commit("a tree where a blob was asked for");

        Assert.Equal("not-a-blob", ReasonFor("vendor/tree.js"));
    }

    [Fact]
    public void Compute_RefusesAPathModifiedInTheCheckoutsWorkingTree()
    {
        _checkout.WriteFile("vendor/resource.js", "edited after the commit");

        Assert.Equal("modified-in-checkout", ReasonFor("vendor/resource.js"));
    }

    // ── the tri-state stop, and the positive ───────────────────────────────────────────────────
    [Fact]
    public void Compute_StopsWithFactsUnavailable_WhenAGitCallErrors_NeverReadingItAsAbsent()
    {
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
        Assert.Equal("vendor/resource.js", candidate.Path);
        Assert.Equal(_checkout.HeadSha(), candidate.SourceCommit);
    }

    private string? ReasonFor(string path, bool unscopedSibling = false, string? siblingScope = null) =>
        Run(path: path, unscopedSibling: unscopedSibling, siblingScope: siblingScope).Verdicts[0].Reason;

    private MissingResourceFactsResult Run(
        string path = "vendor/resource.js",
        string? originalBranch = null,
        string? originalHeadSha = null,
        string? integrationWorktreePath = null,
        bool unscopedSibling = false,
        string? siblingScope = null)
    {
        var halted = new TaskNode { Id = "02-needs-resource", Directory = _checkout.RepoPath, Description = "t" };
        var sibling = new TaskNode
        {
            Id = "04-owns-vendor",
            Directory = _checkout.RepoPath,
            Description = "t",
            WriteScope = unscopedSibling ? null : [siblingScope ?? "unrelated/**"]
        };

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
            [path],
            integrationWorktreePath ?? _integration.RepoPath,
            originalBranch ?? _checkout.CurrentBranch(),
            originalHeadSha ?? _integration.HeadSha());
    }

    public void Dispose()
    {
        _checkout.Dispose();
        _integration.Dispose();
    }

    private sealed class TempGitRepo : IDisposable
    {
        internal string RepoPath { get; } =
            Path.Combine(Path.GetTempPath(), "gr-missing-resource-facts-" + Guid.NewGuid().ToString("N"));

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

            WriteFile("README.md", "# fixture repo");
            Commit("Initial commit");
            WriteFile("vendor/resource.js", "export const ok = true;");
            Commit("Commit the vendored resource");
        }

        internal string HeadSha() => Git("rev-parse", "HEAD").Trim();

        internal string CurrentBranch() => Git("rev-parse", "--abbrev-ref", "HEAD").Trim();

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

        // THE ONE DEFECT: this exits 128 on a dirtied tracked path. reset --hard is the rollback.
        internal void RollBack(string commitish) => Git("merge", "--abort");

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
