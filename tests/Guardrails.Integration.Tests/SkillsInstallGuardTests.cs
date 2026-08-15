using System.CommandLine;
using Guardrails.Cli;
using Guardrails.Core.Io;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #461, the destructive third step: <c>skills install --force</c> DELETES each destination skill
/// folder before copying the bundled one in. That is correct for an install — the payload is
/// reconstructable from any tool of the same version — and catastrophic for authored SOURCE, which is
/// exactly what a git-tracked <c>.claude/skills</c> holds. The two operations shared one flag, and the
/// drift warning pointed authors at it precisely when they were most likely mid-edit (differing versions
/// is what mid-edit looks like).
/// <para>
/// These drive the REAL command through the REAL composition root
/// (<see cref="CommandFactory.BuildRootCommand"/>) against a REAL git repository and the REAL bundled
/// skills payload — no fake probe, no injected installer. A guard that exists only under a stubbed git is
/// not a guard.
/// </para>
/// </summary>
public sealed class SkillsInstallGuardTests
{
    private const string SkillName = "plan-breakdown"; // a real bundled skill, so an install would overwrite it

    /// <summary>The tool's own bundled payload, next to the entry assembly — what an install copies.</summary>
    private static string BundledSkillsDir => Path.Combine(AppContext.BaseDirectory, "skills");

    private static async Task<(int ExitCode, string Out, string Err)> InstallAsync(params string[] args)
    {
        // The bundle must really be there: without it the command fails for an unrelated reason and a
        // "refused" assertion would pass for the wrong cause.
        Assert.True(
            Directory.Exists(Path.Combine(BundledSkillsDir, SkillName)),
            $"Expected the bundled skills payload beside the test host at '{BundledSkillsDir}'.");

        var io = new StringConsoleIo();
        RootCommand root = CommandFactory.BuildRootCommand(io);
        int exitCode = await root.Parse(args).InvokeAsync(
            configuration: null, TestContext.Current.CancellationToken);
        return (exitCode, io.OutText, io.ErrorText);
    }

    [Fact]
    public async Task Force_OverTrackedSourceWithUncommittedEdits_IsRefused_AndChangesNothing()
    {
        // THE case: a maintainer mid-edit on the repo's own skills reads "the remedy didn't work" and
        // reaches for the flag that targets that root. Before #461 their uncommitted work was gone.
        using var repo = new TempSkillsRepo();
        repo.CommitSkillsRoot(SkillName);
        const string InProgress = "---\nname: plan-breakdown\n---\n# half-written edit nobody has committed\n";
        repo.EditWithoutCommitting(SkillName, InProgress);

        (int exitCode, _, string err) = await InstallAsync(
            "skills", "install", "--target", repo.SkillsRoot, "--force");

        Assert.Equal(ExitCodes.HarnessError, exitCode);
        Assert.Contains("Refusing to overwrite git-tracked skill source", err, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(repo.SkillsRoot, SkillName), err, StringComparison.Ordinal);
        Assert.Contains("--overwrite-tracked", err, StringComparison.Ordinal);

        // The work survives, byte for byte …
        Assert.Equal(InProgress, repo.ReadSkillMd(SkillName));
        // … and the refusal is total: no OTHER bundled skill was installed alongside it either.
        Assert.False(Directory.Exists(Path.Combine(repo.SkillsRoot, "guardrails-review")));
    }

    [Fact]
    public async Task Force_OverTrackedSourceWithAnUntrackedFile_IsRefused()
    {
        // A never-committed file inside a tracked skill folder is the least recoverable content of all —
        // git has no copy — and --force deletes the whole folder. `git status --porcelain` reports it, so
        // the guard must treat untracked-inside-tracked as dirty.
        using var repo = new TempSkillsRepo();
        repo.CommitSkillsRoot(SkillName);
        repo.AddUntrackedFile(SkillName, "notes.md", "# scratch notes, never committed");

        (int exitCode, _, string err) = await InstallAsync(
            "skills", "install", "--target", repo.SkillsRoot, "--force");

        Assert.Equal(ExitCodes.HarnessError, exitCode);
        Assert.Contains("Refusing to overwrite git-tracked skill source", err, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(repo.SkillsRoot, SkillName, "notes.md")));
    }

    [Fact]
    public async Task Force_OverTrackedSourceWithUncommittedEdits_ProceedsWithTheExplicitOverride()
    {
        // The refusal is a guard, not a wall: a second, explicit flag still allows it. Overwriting
        // authored source is a real (if rare) intent — it just may not share a flag with "install".
        using var repo = new TempSkillsRepo();
        repo.CommitSkillsRoot(SkillName);
        repo.EditWithoutCommitting(SkillName, "---\nname: plan-breakdown\n---\n# uncommitted\n");

        (int exitCode, string outText, _) = await InstallAsync(
            "skills", "install", "--target", repo.SkillsRoot, "--force", "--overwrite-tracked");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("installed", outText, StringComparison.Ordinal);
        Assert.DoesNotContain("uncommitted", repo.ReadSkillMd(SkillName), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Force_OverTrackedButCleanSource_Proceeds_WithANote()
    {
        // Tracked-but-clean is allowed: git can restore it, and refusing would break the legitimate
        // "refresh this repo's project skills" flow. It is still worth saying out loud, because the result
        // is a working-tree diff the user did not write.
        using var repo = new TempSkillsRepo();
        repo.CommitSkillsRoot(SkillName);

        (int exitCode, _, string err) = await InstallAsync(
            "skills", "install", "--target", repo.SkillsRoot, "--force");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("Note: overwriting git-tracked skill source", err, StringComparison.Ordinal);
        Assert.DoesNotContain("Refusing", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Force_OverAnUntrackedDestination_ProceedsSilently()
    {
        // The pre-#461 path, unchanged: an ordinary install directory git knows nothing about is overwritten
        // by --force with no guard and no commentary. The new protection keys on TRACKED, so it must be
        // invisible to everyone whose destination is a real install.
        string target = Path.Combine(Path.GetTempPath(), "gr-skills-plain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(target, SkillName));
        File.WriteAllText(Path.Combine(target, SkillName, "SKILL.md"), "---\nname: plan-breakdown\n---\n# old install\n");

        try
        {
            (int exitCode, _, string err) = await InstallAsync(
                "skills", "install", "--target", target, "--force");

            Assert.Equal(ExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, err);
            Assert.DoesNotContain("old install", File.ReadAllText(Path.Combine(target, SkillName, "SKILL.md")),
                StringComparison.Ordinal);
        }
        finally
        {
            SafeDelete.DeleteDirectory(target);
        }
    }

    [Fact]
    public async Task WithoutForce_TrackedDirtySourceIsSkipped_NotRefused()
    {
        // Without --force an existing folder is SKIPPED, so nothing is ever destroyed and there is nothing
        // to guard. The refusal must not leak onto that path and turn a harmless no-op into an error.
        using var repo = new TempSkillsRepo();
        repo.CommitSkillsRoot(SkillName);
        const string InProgress = "---\nname: plan-breakdown\n---\n# uncommitted work\n";
        repo.EditWithoutCommitting(SkillName, InProgress);

        (int exitCode, string outText, string err) = await InstallAsync(
            "skills", "install", "--target", repo.SkillsRoot);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("skipped", outText, StringComparison.Ordinal);
        Assert.DoesNotContain("Refusing", err, StringComparison.Ordinal);
        Assert.Equal(InProgress, repo.ReadSkillMd(SkillName));
    }
}
