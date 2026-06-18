using System.Diagnostics;
using Guardrails.Core.Execution;
using Guardrails.Core.State;

namespace Guardrails.Core.Tests;

/// <summary>
/// M5 enforcement revert: WorkspaceScopeEnforcer.RevertOutOfScope reverts out-of-scope creates,
/// modifies, and deletes to their pre-attempt bytes while keeping in-scope changes intact. Tracked
/// files are restored via <c>git checkout --</c>; untracked files via a lazy byte snapshot saved
/// to <c>state/scope-baseline/</c> (the #51 case — authored test files are often untracked).
/// RunReset.Fresh wipes <c>state/scope-baseline/</c> on <c>--fresh</c>.
///
/// These tests intentionally FAIL TO COMPILE against current code:
/// - WorkspaceScopeEnforcer.RevertOutOfScope does not exist yet.
/// - The four-param WorkspaceScopeEnforcer.Snapshot(workspace, scope, ignore, planDir) overload
///   that saves untracked file bytes to state/scope-baseline/ does not exist yet.
/// Do NOT implement those methods here.
/// </summary>
public sealed class ScopeRevertTests : IDisposable
{
    private readonly string _planDir;
    private readonly string _workspace;

    public ScopeRevertTests()
    {
        _planDir = Path.Combine(Path.GetTempPath(), "gr-revert-" + Guid.NewGuid().ToString("N"));
        _workspace = Path.Combine(_planDir, "workspace");
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_planDir))
                Directory.Delete(_planDir, recursive: true);
        }
        catch (IOException)
        {
            // best-effort
        }
    }

    // ── RevertOutOfScope — created file is deleted ────────────────────────────────

    [Fact]
    public void Revert_OutOfScopeCreatedFile_IsDeleted()
    {
        var scope = WriteScope.Parse(["src/**"]);
        var preImage = WorkspaceScopeEnforcer.Snapshot(_workspace, scope, DefaultIgnore(), _planDir);

        WriteFile("tests/NewFile.cs", "new out-of-scope file");

        var result = WorkspaceScopeEnforcer.RevertOutOfScope(_workspace, scope, preImage, _planDir);

        Assert.True(result.HasOutOfScopeWrites);
        Assert.Contains("tests/NewFile.cs", result.ViolatingPaths);
        Assert.False(File.Exists(Path.Combine(_workspace, "tests", "NewFile.cs")),
            "An out-of-scope created file must be deleted by the revert.");
    }

    // ── RevertOutOfScope — tracked file restoration (git baseline) ───────────────

    [Fact]
    public void Revert_OutOfScopeModifiedTrackedFile_IsRestoredFromGit()
    {
        WriteFile("tests/FeatureTests.cs", "ORIGINAL");
        InitGitRepoWithCommit();

        var scope = WriteScope.Parse(["src/**"]);
        var preImage = WorkspaceScopeEnforcer.Snapshot(_workspace, scope, DefaultIgnore(), _planDir);

        WriteFile("tests/FeatureTests.cs", "MODIFIED OUT-OF-SCOPE");

        var result = WorkspaceScopeEnforcer.RevertOutOfScope(_workspace, scope, preImage, _planDir);

        // A modified tracked out-of-scope file is restored to its git-committed bytes.
        Assert.True(result.HasOutOfScopeWrites);
        Assert.Equal("ORIGINAL", ReadFile("tests/FeatureTests.cs"));
    }

    [Fact]
    public void Revert_OutOfScopeDeletedTrackedFile_IsRestoredFromGit()
    {
        WriteFile("tests/FeatureTests.cs", "ORIGINAL");
        InitGitRepoWithCommit();

        var scope = WriteScope.Parse(["src/**"]);
        var preImage = WorkspaceScopeEnforcer.Snapshot(_workspace, scope, DefaultIgnore(), _planDir);

        File.Delete(Path.Combine(_workspace, "tests", "FeatureTests.cs"));

        var result = WorkspaceScopeEnforcer.RevertOutOfScope(_workspace, scope, preImage, _planDir);

        // A deleted tracked out-of-scope file is recreated from the git baseline.
        Assert.True(result.HasOutOfScopeWrites);
        Assert.True(File.Exists(Path.Combine(_workspace, "tests", "FeatureTests.cs")));
        Assert.Equal("ORIGINAL", ReadFile("tests/FeatureTests.cs"));
    }

    // ── RevertOutOfScope — untracked file restoration (scope-baseline) ───────────

    [Fact]
    public void Revert_OutOfScopeModifiedUntrackedFile_IsRestoredFromBaseline()
    {
        // The #51 case: test files authored in a prior task are untracked (not committed to git).
        // Snapshot(4-param) saves their bytes to state/scope-baseline/; revert restores from there.
        WriteFile("tests/FeatureTests.cs", "ORIGINAL");
        // intentionally no git init — file is untracked

        var scope = WriteScope.Parse(["src/**"]);
        var preImage = WorkspaceScopeEnforcer.Snapshot(_workspace, scope, DefaultIgnore(), _planDir);

        WriteFile("tests/FeatureTests.cs", "MODIFIED BY IMPL TASK");

        var result = WorkspaceScopeEnforcer.RevertOutOfScope(_workspace, scope, preImage, _planDir);

        // An untracked out-of-scope file is restored from state/scope-baseline/.
        Assert.True(result.HasOutOfScopeWrites);
        Assert.Equal("ORIGINAL", ReadFile("tests/FeatureTests.cs"));
    }

    [Fact]
    public void Revert_OutOfScopeDeletedUntrackedFile_IsRestoredFromBaseline()
    {
        WriteFile("tests/FeatureTests.cs", "ORIGINAL");

        var scope = WriteScope.Parse(["src/**"]);
        var preImage = WorkspaceScopeEnforcer.Snapshot(_workspace, scope, DefaultIgnore(), _planDir);

        File.Delete(Path.Combine(_workspace, "tests", "FeatureTests.cs"));

        var result = WorkspaceScopeEnforcer.RevertOutOfScope(_workspace, scope, preImage, _planDir);

        // A deleted untracked out-of-scope file is recreated from state/scope-baseline/.
        Assert.True(result.HasOutOfScopeWrites);
        Assert.True(File.Exists(Path.Combine(_workspace, "tests", "FeatureTests.cs")));
        Assert.Equal("ORIGINAL", ReadFile("tests/FeatureTests.cs"));
    }

    // ── RevertOutOfScope — in-scope changes are kept ─────────────────────────────

    [Fact]
    public void Revert_InScopeChanges_AreKept()
    {
        // In-scope edits survive the revert so the next attempt's "fix, don't restart" feedback
        // still has the implementation work from attempt N.
        WriteFile("src/Feature/Impl.cs", "ORIGINAL IMPL");
        WriteFile("tests/FeatureTests.cs", "ORIGINAL TEST");

        var scope = WriteScope.Parse(["src/**"]);
        var preImage = WorkspaceScopeEnforcer.Snapshot(_workspace, scope, DefaultIgnore(), _planDir);

        WriteFile("src/Feature/Impl.cs", "NEW IMPL");        // in-scope: must be kept
        WriteFile("tests/FeatureTests.cs", "CHEATED");       // out-of-scope: must be reverted

        var result = WorkspaceScopeEnforcer.RevertOutOfScope(_workspace, scope, preImage, _planDir);

        Assert.True(result.HasOutOfScopeWrites);
        Assert.Equal("NEW IMPL", ReadFile("src/Feature/Impl.cs"));       // in-scope preserved
        Assert.Equal("ORIGINAL TEST", ReadFile("tests/FeatureTests.cs")); // out-of-scope reverted
    }

    // ── Snapshot — untracked bytes saved to state/scope-baseline/ ────────────────

    [Fact]
    public void Snapshot_SavesUntrackedFileBytesToScopeBaseline()
    {
        // Verifies the contract: after Snapshot(4-param), state/scope-baseline/<relPath> holds the
        // exact pre-attempt bytes of untracked files — the byte source for revert when git is absent.
        WriteFile("tests/FeatureTests.cs", "ORIGINAL BYTES");

        var scope = WriteScope.Parse(["src/**"]);
        WorkspaceScopeEnforcer.Snapshot(_workspace, scope, DefaultIgnore(), _planDir);

        string baselinePath = Path.Combine(
            _planDir, "state", "scope-baseline", "tests", "FeatureTests.cs");
        Assert.True(File.Exists(baselinePath),
            "Snapshot must persist untracked file bytes to state/scope-baseline/<relPath>.");
        Assert.Equal("ORIGINAL BYTES", File.ReadAllText(baselinePath));
    }

    // ── #51 end-to-end: attempt 1 reverts, attempt 2 starts clean ────────────────

    [Fact]
    public void EndToEnd_OutOfScopeTestEdit_Attempt1Reverts_Attempt2StartsClean()
    {
        // §5.3/§5.4 scenario: an impl task whose writeScope excludes tests/ edits the test file.
        // Attempt 1 enforcement detects the violation, reverts the test, and fails the attempt.
        // Attempt 2 starts from pristine test bytes; correct impl writes only in-scope → success.
        WriteFile("tests/FeatureTests.cs", "ORIGINAL TEST");
        WriteFile("src/Feature/Impl.cs", "ORIGINAL IMPL");

        var scope = WriteScope.Parse(["src/**"]);

        // ── Attempt 1 ────────────────────────────────────────────────────────────
        var preImage1 = WorkspaceScopeEnforcer.Snapshot(_workspace, scope, DefaultIgnore(), _planDir);
        WriteFile("src/Feature/Impl.cs", "ATTEMPT-1 IMPL");
        WriteFile("tests/FeatureTests.cs", "CORRUPTED BY ATTEMPT 1");

        var result1 = WorkspaceScopeEnforcer.RevertOutOfScope(_workspace, scope, preImage1, _planDir);

        Assert.True(result1.HasOutOfScopeWrites);                     // violation detected
        Assert.Equal("ORIGINAL TEST", ReadFile("tests/FeatureTests.cs")); // test restored
        Assert.Equal("ATTEMPT-1 IMPL", ReadFile("src/Feature/Impl.cs")); // in-scope kept

        // ── Attempt 2 ────────────────────────────────────────────────────────────
        // Correct implementation: writes only in-scope, does not touch the test file.
        var preImage2 = WorkspaceScopeEnforcer.Snapshot(_workspace, scope, DefaultIgnore(), _planDir);
        WriteFile("src/Feature/Impl.cs", "CORRECT IMPL");             // in-scope only

        var result2 = WorkspaceScopeEnforcer.RevertOutOfScope(_workspace, scope, preImage2, _planDir);

        Assert.False(result2.HasOutOfScopeWrites);                    // no violation
        Assert.Equal("ORIGINAL TEST", ReadFile("tests/FeatureTests.cs")); // still pristine
    }

    // ── --fresh / reset wipes state/scope-baseline/ ──────────────────────────────

    [Fact]
    public void Fresh_WipesScopeBaselineStore()
    {
        // A stale scope-baseline surviving --fresh would revert a legitimately re-authored file on
        // the next run, before the new attempt re-snapshots its current bytes. Mirrors the
        // state/captured/ precedent in RunResetTests.Fresh_WipesCapturedBaselineStore.
        string baseline = Path.Combine(
            _planDir, "state", "scope-baseline", "tests", "Tests.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(baseline)!);
        File.WriteAllText(baseline, "STALE BYTES");

        RunReset.Fresh(_planDir);

        Assert.False(Directory.Exists(Path.Combine(_planDir, "state", "scope-baseline")),
            "--fresh must delete state/scope-baseline/ to prevent stale bytes reverting a re-authored file.");
        Assert.True(File.Exists(Path.Combine(_planDir, "state", "state.json")));
    }

    [Fact]
    public void Fresh_WithNoScopeBaseline_DoesNotThrow()
    {
        // scope-baseline is absent on a first run; --fresh must be a no-op for it, not a failure.
        RunReset.Fresh(_planDir);
        Assert.False(Directory.Exists(Path.Combine(_planDir, "state", "scope-baseline")));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private void WriteFile(string relative, string content)
    {
        string full = Path.Combine(_workspace, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private string ReadFile(string relative) =>
        File.ReadAllText(Path.Combine(_workspace, relative));

    /// <summary>
    /// Initialises a git repo in <see cref="_workspace"/> and commits all currently written files.
    /// Used for tests that exercise the git-baseline restore path (tracked files).
    /// </summary>
    private void InitGitRepoWithCommit()
    {
        RunGit("init");
        RunGit("config", "user.email", "test@guardrails.local");
        RunGit("config", "user.name", "Guardrails Test");
        RunGit("add", ".");
        RunGit("commit", "-m", "init");
    }

    private void RunGit(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(" ", args)} failed (exit {proc.ExitCode}): {stderr}");
    }

    private static IReadOnlyList<string> DefaultIgnore() =>
        ["state/", ".git/", "**/bin/**", "**/obj/**", "**/node_modules/**"];
}
