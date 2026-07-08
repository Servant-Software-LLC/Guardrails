using System.Diagnostics;
using Guardrails.Core.Execution;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Red-bar integration tests encoding plan 08 §2 / §3.4 write-scope CHECK semantics against
/// <see cref="WriteScopeCheck"/> before it exists. The project will NOT compile against current
/// code — that compilation failure IS the expected signal. Do NOT implement the check here;
/// tests only, in this one file.
/// </summary>
public sealed class WriteScopeCheckTests
{
    /// <summary>
    /// Throwaway single-use git repo in a temp directory. Provides a real repo root with an
    /// initial commit; cleaned up on dispose.
    /// </summary>
    private sealed class TempGitRepo : IDisposable
    {
        private readonly string _root;

        public string RepoPath { get; }

        public TempGitRepo()
        {
            _root = Path.Combine(Path.GetTempPath(), "gr-wsc-" + Guid.NewGuid().ToString("N"));
            RepoPath = _root;
            Directory.CreateDirectory(RepoPath);

            Git(RepoPath, "init");
            Git(RepoPath, "config", "user.email", "test@guardrails.local");
            Git(RepoPath, "config", "user.name", "Guardrails Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "# Test repo");
            Git(RepoPath, "add", ".");
            Git(RepoPath, "commit", "-m", "Initial commit");
        }

        /// <summary>Returns the full commit sha of HEAD in the repo.</summary>
        public string HeadSha() => Git(RepoPath, "rev-parse", "HEAD").Trim();

        /// <summary>
        /// Writes <paramref name="content"/> to <paramref name="relativePath"/>
        /// (using '/' separators), stages it, commits it, and returns the new HEAD sha.
        /// </summary>
        public string CommitFile(string relativePath, string content, string message)
        {
            string fullPath = ToFull(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
            Git(RepoPath, "add", relativePath);
            Git(RepoPath, "commit", "-m", message);
            return HeadSha();
        }

        /// <summary>
        /// Stages a deletion of <paramref name="relativePath"/> and commits it.
        /// </summary>
        public void DeleteAndCommit(string relativePath, string message)
        {
            Git(RepoPath, "rm", relativePath);
            Git(RepoPath, "commit", "-m", message);
        }

        /// <summary>
        /// Reads the current working-tree content of <paramref name="relativePath"/>.
        /// </summary>
        public string ReadFile(string relativePath) =>
            File.ReadAllText(ToFull(relativePath));

        private string ToFull(string relativePath) =>
            Path.Combine(RepoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

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
                    $"git {string.Join(" ", args)} (in {workingDir}) exited {proc.ExitCode}: {stderr}");
            return stdout;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                        File.SetAttributes(file, FileAttributes.Normal);
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch { /* best-effort teardown of a temp dir */ }
        }
    }

    // -------------------------------------------------------------------------
    // Out-of-scope add / modify / delete
    // -------------------------------------------------------------------------

    /// <summary>
    /// An out-of-scope add, modify, or delete in the diff causes the check to fail and
    /// names the offending path(s) in the result.
    /// </summary>
    [Theory]
    [InlineData("add")]
    [InlineData("modify")]
    [InlineData("delete")]
    public void OutOfScope_Change_Fails_NamesOffendingPath(string changeKind)
    {
        using var repo = new TempGitRepo();
        // Establish baseline files so modify and delete have something to act on
        repo.CommitFile("src/InScope.cs", "// base", "add in-scope base");
        repo.CommitFile("tests/OutOfScope.cs", "// base test", "add out-of-scope base");
        string taskBase = repo.HeadSha();

        // writeScope: only src/
        IReadOnlyList<string> scope = ["src/**"];

        switch (changeKind)
        {
            case "add":
                repo.CommitFile("tests/NewTest.cs", "// new test", "add out-of-scope file");
                break;
            case "modify":
                repo.CommitFile("tests/OutOfScope.cs", "// modified", "modify out-of-scope file");
                break;
            case "delete":
                repo.DeleteAndCommit("tests/OutOfScope.cs", "delete out-of-scope file");
                break;
        }

        var result = WriteScopeCheck.Check(repo.RepoPath, taskBase, scope);

        Assert.False(result.Passed,
            $"Expected check to FAIL for an out-of-scope {changeKind}");
        Assert.NotEmpty(result.OffendingPaths);
        // Every offending path must reside in the out-of-scope location
        Assert.All(result.OffendingPaths, o =>
            Assert.Contains("tests", o.Path, StringComparison.OrdinalIgnoreCase));

        // The status letter must match the change actually made (issue #253 diagnostic).
        char expectedStatus = changeKind switch
        {
            "add" => 'A',
            "modify" => 'M',
            "delete" => 'D',
            _ => throw new InvalidOperationException($"unexpected changeKind {changeKind}")
        };
        Assert.All(result.OffendingPaths, o => Assert.Equal(expectedStatus, o.Status));
    }

    /// <summary>
    /// Changes that stay entirely within the declared write-scope produce a passing result
    /// with no offending paths.
    /// </summary>
    [Fact]
    public void InScope_Change_Passes()
    {
        using var repo = new TempGitRepo();
        string taskBase = repo.HeadSha();
        IReadOnlyList<string> scope = ["src/**"];

        repo.CommitFile("src/Thing.cs", "// content", "add in-scope file");

        var result = WriteScopeCheck.Check(repo.RepoPath, taskBase, scope);

        Assert.True(result.Passed);
        Assert.Empty(result.OffendingPaths);
    }

    // -------------------------------------------------------------------------
    // Issue #262: a dotfile writeScope must claim its own dotfile edit
    // -------------------------------------------------------------------------

    /// <summary>
    /// The exact #262 repro: a task with <c>writeScope: [".gitignore"]</c> that appends lines to the
    /// workspace-root <c>.gitignore</c> must PASS the check — the single declared scope entry must
    /// claim the identical diff path. Before the fix, <c>.gitignore</c> normalised to the directory
    /// glob <c>.gitignore/**</c>, which never matched the FILE <c>.gitignore</c>, so the legitimate
    /// edit was flagged out-of-scope, reverted, and dead-ended at needs-human after 3 identical
    /// attempts.
    /// </summary>
    [Fact]
    public void DotfileWriteScope_ClaimsItsOwnDotfileEdit_Issue262()
    {
        using var repo = new TempGitRepo();
        repo.CommitFile(".gitignore", "bin/\nobj/\n", "add base .gitignore");
        string taskBase = repo.HeadSha();

        // The task declares exactly the dotfile it intends to edit.
        IReadOnlyList<string> scope = [".gitignore"];

        // The action appends two lines to that same .gitignore — nothing else.
        repo.CommitFile(".gitignore", "bin/\nobj/\n*.dfd.generated.c4\nnode_modules/\n",
            "append generated + node_modules to .gitignore");

        var result = WriteScopeCheck.Check(repo.RepoPath, taskBase, scope);

        Assert.True(result.Passed,
            "A writeScope of ['.gitignore'] must claim an edit to the file '.gitignore' (issue #262)");
        Assert.Empty(result.OffendingPaths);
    }

    /// <summary>
    /// Discrimination guard for the #262 fix: a dotfile writeScope must still FAIL an out-of-scope
    /// edit to a DIFFERENT file — the literal dotfile arm must not turn the matcher permissive.
    /// </summary>
    [Fact]
    public void DotfileWriteScope_StillFailsUnrelatedOutOfScopeEdit_Issue262()
    {
        using var repo = new TempGitRepo();
        repo.CommitFile(".gitignore", "bin/\n", "add base .gitignore");
        repo.CommitFile("src/Existing.cs", "// base", "add base source");
        string taskBase = repo.HeadSha();

        IReadOnlyList<string> scope = [".gitignore"];

        // Edit a file OUTSIDE the declared dotfile scope.
        repo.CommitFile("src/Existing.cs", "// tampered", "edit an out-of-scope source file");

        var result = WriteScopeCheck.Check(repo.RepoPath, taskBase, scope);

        Assert.False(result.Passed,
            "A writeScope of ['.gitignore'] must still reject an edit to src/Existing.cs (issue #262)");
        Assert.Contains(result.OffendingPaths,
            o => o.Path.Replace('\\', '/').EndsWith("src/Existing.cs", StringComparison.OrdinalIgnoreCase)
                 && o.Status == 'M');
    }

    // -------------------------------------------------------------------------
    // Rename: presented as paired D + A (no git -M); both paths must be in scope
    // -------------------------------------------------------------------------

    /// <summary>
    /// A rename where the new (A) path is outside the declared scope fails — the A path
    /// is named in the offending list. The D path is in scope, so it does not appear.
    /// </summary>
    [Fact]
    public void Rename_NewPathOutOfScope_Fails_NamesNewPath()
    {
        using var repo = new TempGitRepo();
        repo.CommitFile("src/OldName.cs", "// content", "add old file");
        string taskBase = repo.HeadSha();
        IReadOnlyList<string> scope = ["src/**"];

        // Delete old (in scope), add new at out-of-scope location — paired D+A in one commit
        TempGitRepo.Git(repo.RepoPath, "rm", "src/OldName.cs");
        Directory.CreateDirectory(Path.Combine(repo.RepoPath, "docs"));
        File.WriteAllText(Path.Combine(repo.RepoPath, "docs", "NewName.cs"), "// moved");
        TempGitRepo.Git(repo.RepoPath, "add", "docs/NewName.cs");
        TempGitRepo.Git(repo.RepoPath, "commit", "-m", "rename src to docs (new path out of scope)");

        var result = WriteScopeCheck.Check(repo.RepoPath, taskBase, scope);

        Assert.False(result.Passed,
            "Rename to an out-of-scope path must fail the check");
        // The A (new) path is out of scope — it must appear in the offending list, tagged 'A'
        // (git presents an untracked rename destination the same way as any other new file).
        Assert.Contains(result.OffendingPaths,
            o => o.Path.Replace('\\', '/').EndsWith("docs/NewName.cs", StringComparison.OrdinalIgnoreCase)
                 && o.Status == 'A');
    }

    /// <summary>
    /// A rename where the old (D) path is outside the declared scope fails — the D path
    /// is named in the offending list.
    /// </summary>
    [Fact]
    public void Rename_OldPathOutOfScope_Fails_NamesOldPath()
    {
        using var repo = new TempGitRepo();
        // Old file lives in tests/ (out of scope)
        repo.CommitFile("tests/OldFile.cs", "// original test content abc123", "add old out-of-scope file");
        string taskBase = repo.HeadSha();
        IReadOnlyList<string> scope = ["src/**"];

        // Delete from out-of-scope location, add at in-scope location — paired D+A
        TempGitRepo.Git(repo.RepoPath, "rm", "tests/OldFile.cs");
        Directory.CreateDirectory(Path.Combine(repo.RepoPath, "src"));
        File.WriteAllText(Path.Combine(repo.RepoPath, "src", "NewFile.cs"), "// relocated xyz789");
        TempGitRepo.Git(repo.RepoPath, "add", "src/NewFile.cs");
        TempGitRepo.Git(repo.RepoPath, "commit", "-m", "rename tests→src (old path out of scope)");

        var result = WriteScopeCheck.Check(repo.RepoPath, taskBase, scope);

        Assert.False(result.Passed,
            "Rename from an out-of-scope path must fail the check");
        // The D (old) path is out of scope — it must appear in the offending list, tagged 'D'.
        Assert.Contains(result.OffendingPaths,
            o => o.Path.Replace('\\', '/').EndsWith("tests/OldFile.cs", StringComparison.OrdinalIgnoreCase)
                 && o.Status == 'D');
    }

    /// <summary>
    /// A rename where both the old (D) and new (A) paths are within the declared scope passes.
    /// </summary>
    [Fact]
    public void Rename_BothPathsInScope_Passes()
    {
        using var repo = new TempGitRepo();
        repo.CommitFile("src/OldName.cs", "// content pqr456", "add old in-scope file");
        string taskBase = repo.HeadSha();
        IReadOnlyList<string> scope = ["src/**"];

        // Both delete and add within src/ — paired D+A, both in scope
        TempGitRepo.Git(repo.RepoPath, "rm", "src/OldName.cs");
        // Git-for-Windows prunes the now-empty src/ after `git rm`, so recreate it before the write
        // (mirrors the sibling rename tests above; portable-git-fixture pattern).
        Directory.CreateDirectory(Path.Combine(repo.RepoPath, "src"));
        File.WriteAllText(Path.Combine(repo.RepoPath, "src", "NewName.cs"), "// renamed content pqr456");
        TempGitRepo.Git(repo.RepoPath, "add", "src/NewName.cs");
        TempGitRepo.Git(repo.RepoPath, "commit", "-m", "rename within src (both in scope)");

        var result = WriteScopeCheck.Check(repo.RepoPath, taskBase, scope);

        Assert.True(result.Passed,
            "A rename where both old and new paths are in scope must pass");
        Assert.Empty(result.OffendingPaths);
    }

    // -------------------------------------------------------------------------
    // TDD test-exclusion (triad replacement)
    // The scenarios-present guardrail greps for this exact method name.
    // -------------------------------------------------------------------------

    /// <summary>
    /// TDD test-exclusion (the triad replacement, plan 08 §2.3 / SSOT §3.4):
    /// an implementation task whose <c>writeScope</c> EXCLUDES the test files editing
    /// a test file causes the check to FAIL.
    /// </summary>
    [Fact]
    public void TestFileExcludedFromScope()
    {
        using var repo = new TempGitRepo();
        // The test-author task already committed the test file
        repo.CommitFile("tests/MyFeatureTests.cs", "// original test suite", "author tests");
        string taskBase = repo.HeadSha();

        // Implementation task scope: only src/ — tests/ is explicitly excluded
        IReadOnlyList<string> implementationScope = ["src/**"];

        // The implementation task modifies the test file (it must not)
        repo.CommitFile("tests/MyFeatureTests.cs",
            "// test suite modified by the implementation task",
            "implementation task edits test file");

        var result = WriteScopeCheck.Check(repo.RepoPath, taskBase, implementationScope);

        Assert.False(result.Passed,
            "An implementation task editing a test file excluded from its writeScope must FAIL the check");
        Assert.Contains(result.OffendingPaths,
            o => o.Path.Replace('\\', '/').EndsWith("tests/MyFeatureTests.cs", StringComparison.OrdinalIgnoreCase)
                 && o.Status == 'M');
    }

    // -------------------------------------------------------------------------
    // Off-switch: absent writeScope skips the check entirely
    // -------------------------------------------------------------------------

    /// <summary>
    /// When <c>writeScope</c> is null (absent in task.json), the check is the off-switch:
    /// even a diff that touches out-of-scope paths produces a passing result.
    /// </summary>
    [Fact]
    public void NullWriteScope_NoCheck_AlwaysPasses()
    {
        using var repo = new TempGitRepo();
        string taskBase = repo.HeadSha();

        // Write changes that would normally fail any declared scope
        repo.CommitFile("anywhere/anything.ts", "export {}; // unchecked", "add unchecked file");

        // null = absent writeScope = off-switch (SSOT §3.4)
        var result = WriteScopeCheck.Check(repo.RepoPath, taskBase, null);

        Assert.True(result.Passed,
            "Absent writeScope (null) must be the off-switch — the check does not run");
        Assert.Empty(result.OffendingPaths);
    }

    // -------------------------------------------------------------------------
    // Read-only verdict path
    // -------------------------------------------------------------------------

    /// <summary>
    /// The check is READ-ONLY in the verdict path: when it passes it does NOT rewrite
    /// any tracked files in the worktree.
    /// </summary>
    [Fact]
    public void ReadOnly_PassedCheck_DoesNotRewriteTrackedFiles()
    {
        using var repo = new TempGitRepo();
        repo.CommitFile("src/Subject.cs", "// original subject", "add subject");
        string taskBase = repo.HeadSha();
        IReadOnlyList<string> scope = ["src/**"];

        // Make an in-scope change so the check has something to inspect
        repo.CommitFile("src/Subject.cs", "// updated subject", "update subject");

        // Snapshot working-tree content before running the check
        string subjectBefore = repo.ReadFile("src/Subject.cs");
        string readmeBefore = repo.ReadFile("README.md");

        var result = WriteScopeCheck.Check(repo.RepoPath, taskBase, scope);

        // The check must pass (all changes in scope)
        Assert.True(result.Passed);

        // No tracked file may have been rewritten by the check
        Assert.Equal(subjectBefore, repo.ReadFile("src/Subject.cs"));
        Assert.Equal(readmeBefore, repo.ReadFile("README.md"));
    }

    // -------------------------------------------------------------------------
    // Scoped revert — the "fix, don't restart" path
    // The scenarios-present guardrail greps for this exact method name.
    // -------------------------------------------------------------------------

    /// <summary>
    /// After a scope-violating attempt that touches one IN-scope file and one OUT-of-scope
    /// file, the scoped revert restores ONLY the out-of-scope file to its <c>taskBase</c>
    /// content while the in-scope file keeps its attempt (WIP) content.
    ///
    /// This pins that an over-reverting implementation (e.g. <c>git checkout taskBase -- .</c>)
    /// is REJECTED: that would wipe the in-scope WIP, which must survive the revert.
    /// </summary>
    [Fact]
    public void ScopedRevert_KeepsInScopeWip()
    {
        using var repo = new TempGitRepo();
        // Establish taskBase with both files present
        repo.CommitFile("src/Feature.cs", "// base feature content", "add base feature");
        repo.CommitFile("config/settings.json", "{\"base\": true}", "add base config");
        string taskBase = repo.HeadSha();

        // Scope: only src/ — config/ is out of scope
        IReadOnlyList<string> scope = ["src/**"];

        // Simulate the task attempt: modify both in-scope and out-of-scope files
        const string inScopeWip = "// feature WIP — must survive the scoped revert";
        const string outOfScopeWip = "{\"modified\": true}";
        repo.CommitFile("src/Feature.cs", inScopeWip, "update feature (in scope)");
        repo.CommitFile("config/settings.json", outOfScopeWip, "update config (out of scope)");

        // Check detects the out-of-scope config change
        var result = WriteScopeCheck.Check(repo.RepoPath, taskBase, scope);
        Assert.False(result.Passed,
            "Expected check to FAIL because config/settings.json is outside writeScope");
        Assert.NotEmpty(result.OffendingPaths);
        Assert.Contains(result.OffendingPaths,
            o => o.Path.Replace('\\', '/').EndsWith("config/settings.json", StringComparison.OrdinalIgnoreCase)
                 && o.Status == 'M'); // config/settings.json existed at taskBase -- a modify, not an add

        // Perform the scoped revert — ONLY the out-of-scope offending paths
        WriteScopeCheck.ScopedRevert(repo.RepoPath, taskBase, result.OffendingPaths);

        // The in-scope file must KEEP its WIP content (the "fix, don't restart" guarantee)
        Assert.Equal(inScopeWip, repo.ReadFile("src/Feature.cs"));

        // The out-of-scope file must be RESTORED to its taskBase content
        Assert.Equal("{\"base\": true}", repo.ReadFile("config/settings.json"));
    }

    // -------------------------------------------------------------------------
    // Issue #253 diagnostic: status letter + forensic preview on a new-file offense
    // -------------------------------------------------------------------------

    /// <summary>
    /// A brand-new out-of-scope file (no history at <c>taskBase</c>) is tagged <c>Status == 'A'</c>
    /// and carries a captured content preview — the forensic trace that lets a human debugging a
    /// later <c>needs-human</c> immediately tell "unattributable new file" apart from "a real edit to
    /// something that already existed" (issue #253's proposed fix #1).
    /// </summary>
    [Fact]
    public void NewOutOfScopeFile_TaggedAdded_AndCarriesContentPreview()
    {
        using var repo = new TempGitRepo();
        repo.CommitFile("src/InScope.cs", "// base", "add in-scope base");
        string taskBase = repo.HeadSha();
        IReadOnlyList<string> scope = ["src/**"];

        const string content = "out of scope cruft — could be a leaked fixture (issue #253)";
        Directory.CreateDirectory(Path.Combine(repo.RepoPath, "docs"));
        File.WriteAllText(Path.Combine(repo.RepoPath, "docs", "outside.txt"), content);
        TempGitRepo.Git(repo.RepoPath, "add", "docs/outside.txt");
        TempGitRepo.Git(repo.RepoPath, "commit", "-m", "add out-of-scope untracked-style file");

        var result = WriteScopeCheck.Check(repo.RepoPath, taskBase, scope);

        Assert.False(result.Passed);
        WriteScopeOffense offense = Assert.Single(result.OffendingPaths);
        Assert.Equal('A', offense.Status);
        Assert.True(offense.IsNewFile);
        Assert.NotNull(offense.Preview);
        // SizeBytes is the on-disk UTF-8 byte count, not the .NET (UTF-16) char count — content
        // contains an em dash, which is multiple bytes in UTF-8 but one char in a C# string.
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(content), offense.Preview!.SizeBytes);
        Assert.Contains("issue #253", offense.Preview.TextPreview);
    }

    /// <summary>
    /// The preview is captured DURING <see cref="WriteScopeCheck.Check"/> — before the caller invokes
    /// <see cref="WriteScopeCheck.ScopedRevert"/> deletes the file — so it survives as the only
    /// remaining forensic trace even after the revert wipes the file from the worktree.
    /// </summary>
    [Fact]
    public void NewOutOfScopeFile_PreviewSurvivesAfterScopedRevertDeletesTheFile()
    {
        using var repo = new TempGitRepo();
        string taskBase = repo.HeadSha();
        IReadOnlyList<string> scope = ["src/**"];

        const string content = "attempt-1-output";
        Directory.CreateDirectory(Path.Combine(repo.RepoPath, "src"));
        File.WriteAllText(Path.Combine(repo.RepoPath, "notsrc.txt"), content);
        TempGitRepo.Git(repo.RepoPath, "add", "notsrc.txt");
        TempGitRepo.Git(repo.RepoPath, "commit", "-m", "add out-of-scope file");

        var result = WriteScopeCheck.Check(repo.RepoPath, taskBase, scope);
        WriteScopeOffense offense = Assert.Single(result.OffendingPaths);
        Assert.NotNull(offense.Preview);

        WriteScopeCheck.ScopedRevert(repo.RepoPath, taskBase, result.OffendingPaths);

        Assert.False(File.Exists(Path.Combine(repo.RepoPath, "notsrc.txt")),
            "sanity: the revert really does delete the new out-of-scope file");
        Assert.Equal(content, offense.Preview!.TextPreview); // the ALREADY-CAPTURED result is unaffected
    }

    // -------------------------------------------------------------------------
    // Issue #280: phase-2 scope-clean (StripOutOfScope) — strips silently, returns what it stripped,
    // and NEVER touches the reconstructable dep set (invisible to Check's staging).
    // -------------------------------------------------------------------------

    /// <summary>
    /// Phase-2 scope-clean (SSOT §3.4, issue #280): <see cref="WriteScopeCheck.StripOutOfScope"/>
    /// reverts an out-of-scope path (a passing guardrail's non-dep side effect), RETURNS what it
    /// stripped for a log/observer note, and leaves in-scope WIP untouched — the same revert as phase 1,
    /// but reported, not failed.
    /// </summary>
    [Fact]
    public void StripOutOfScope_RevertsOutOfScopePath_KeepsInScope_AndReturnsStripped()
    {
        using var repo = new TempGitRepo();
        repo.CommitFile("src/Feature.cs", "// base", "add base feature");
        string taskBase = repo.HeadSha();
        IReadOnlyList<string> scope = ["src/**"];

        const string inScopeWip = "// in-scope WIP must survive phase-2";
        repo.CommitFile("src/Feature.cs", inScopeWip, "in-scope edit (survives)");
        repo.CommitFile("build/out/report.txt", "out-of-scope build artifact", "guardrail side effect");

        IReadOnlyList<WriteScopeOffense> stripped =
            WriteScopeCheck.StripOutOfScope(repo.RepoPath, taskBase, scope);

        // Returned the stripped offense (diagnosability), reverted it, kept the in-scope WIP.
        Assert.Contains(stripped,
            o => o.Path.Replace('\\', '/').EndsWith("build/out/report.txt", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(Path.Combine(repo.RepoPath, "build", "out", "report.txt")),
            "The out-of-scope artifact must be stripped from the worktree.");
        Assert.Equal(inScopeWip, repo.ReadFile("src/Feature.cs"));
    }

    /// <summary>
    /// The (A)/(B) interplay (issue #280): a reconstructable dep dir (<c>node_modules</c>) a passing
    /// guardrail left is INVISIBLE to <see cref="WriteScopeCheck.Check"/>'s staging (§5.3(D)), so phase-2
    /// scope-clean neither reports it NOR deletes it — it stays on disk (warm-cache #255) and is kept out
    /// of the commit by the staging exclusion at the <c>Integrate</c> site instead.
    /// </summary>
    [Fact]
    public void StripOutOfScope_DoesNotStripOrDeleteNodeModules()
    {
        using var repo = new TempGitRepo();
        string taskBase = repo.HeadSha();
        IReadOnlyList<string> scope = ["src/**"];

        // An in-scope action write plus a guardrail-created nested node_modules.
        Directory.CreateDirectory(Path.Combine(repo.RepoPath, "src"));
        File.WriteAllText(Path.Combine(repo.RepoPath, "src", "Impl.cs"), "// in-scope");
        string nested = Path.Combine(repo.RepoPath, "dsl", "node_modules", "ajv", "dist", "ajv.js");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        File.WriteAllText(nested, "module.exports={};");

        IReadOnlyList<WriteScopeOffense> stripped =
            WriteScopeCheck.StripOutOfScope(repo.RepoPath, taskBase, scope);

        // node_modules is never seen as an offense (excluded from staging) → nothing to strip.
        Assert.Empty(stripped);
        // …and it is NOT deleted from disk (stage-exclusion, not worktree deletion — #255).
        Assert.True(File.Exists(nested),
            "node_modules must remain on disk after phase-2 scope-clean (warm-cache #255).");
    }

    /// <summary>
    /// A modified/deleted (M/D) offense never carries a preview — its taskBase blob is always
    /// separately recoverable via <c>git show</c>, so there is nothing extra to capture.
    /// </summary>
    [Fact]
    public void ModifiedOutOfScopeFile_NoPreview()
    {
        using var repo = new TempGitRepo();
        repo.CommitFile("config/settings.json", "{\"base\": true}", "add base config");
        string taskBase = repo.HeadSha();
        IReadOnlyList<string> scope = ["src/**"];

        repo.CommitFile("config/settings.json", "{\"modified\": true}", "modify out-of-scope file");

        var result = WriteScopeCheck.Check(repo.RepoPath, taskBase, scope);

        WriteScopeOffense offense = Assert.Single(result.OffendingPaths);
        Assert.Equal('M', offense.Status);
        Assert.False(offense.IsNewFile);
        Assert.Null(offense.Preview);
    }
}
