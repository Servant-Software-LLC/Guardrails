using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// M4 detect-only enforcement: WorkspaceScopeEnforcer snapshots the workspace (SHA-256,
/// content-based) before an action and, after the action, detects any write outside the task's
/// declared writeScope. No revert in M4 — HasOutOfScopeWrites is the signal that fails the attempt.
/// </summary>
public sealed class WorkspaceScopeEnforcerTests : IDisposable
{
    private readonly string _workspace;

    public WorkspaceScopeEnforcerTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "gr-enforcer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workspace))
                Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
            // best-effort
        }
    }

    // ── Snapshot — ignored paths excluded ────────────────────────────────────────

    [Fact]
    public void Snapshot_WritesUnderIgnoredPaths_AreNotTracked()
    {
        // A write that happens after the snapshot but under an ignored path must never surface
        // as a violation — the ignored path was excluded from the tracked tree.
        var scope = WriteScope.Parse(["src/**"]);
        var snapshot = WorkspaceScopeEnforcer.Snapshot(_workspace, scope, ["state/"]);

        WriteFile("state/fragment.json", "{}");

        var result = WorkspaceScopeEnforcer.DetectOutOfScopeWrites(_workspace, scope, snapshot);

        Assert.False(result.HasOutOfScopeWrites);
        Assert.Empty(result.ViolatingPaths);
    }

    [Fact]
    public void Snapshot_NonIgnoredPaths_AreTracked()
    {
        // A write to a non-ignored, out-of-scope path IS detected — it was part of the pre-image.
        WriteFile("tests/Thing.cs", "original");
        var scope = WriteScope.Parse(["src/**"]);
        var snapshot = WorkspaceScopeEnforcer.Snapshot(_workspace, scope, ["state/"]);

        WriteFile("tests/Thing.cs", "modified out-of-scope");

        var result = WorkspaceScopeEnforcer.DetectOutOfScopeWrites(_workspace, scope, snapshot);

        Assert.True(result.HasOutOfScopeWrites);
    }

    // ── No violation ─────────────────────────────────────────────────────────────

    [Fact]
    public void Detect_NoChanges_IsClean()
    {
        WriteFile("src/Feature/Thing.cs", "content");
        WriteFile("tests/FeatureTests.cs", "test");
        var scope = WriteScope.Parse(["src/**"]);
        var snapshot = WorkspaceScopeEnforcer.Snapshot(_workspace, scope, DefaultIgnore());

        var result = WorkspaceScopeEnforcer.DetectOutOfScopeWrites(_workspace, scope, snapshot);

        Assert.False(result.HasOutOfScopeWrites);
        Assert.Empty(result.ViolatingPaths);
    }

    [Fact]
    public void Detect_InScopeEdit_IsNotViolation()
    {
        WriteFile("src/Feature/Thing.cs", "original");
        var scope = WriteScope.Parse(["src/**"]);
        var snapshot = WorkspaceScopeEnforcer.Snapshot(_workspace, scope, DefaultIgnore());

        WriteFile("src/Feature/Thing.cs", "modified within scope");

        var result = WorkspaceScopeEnforcer.DetectOutOfScopeWrites(_workspace, scope, snapshot);

        Assert.False(result.HasOutOfScopeWrites);
    }

    [Fact]
    public void Detect_InScopeNewFile_IsNotViolation()
    {
        var scope = WriteScope.Parse(["src/**"]);
        var snapshot = WorkspaceScopeEnforcer.Snapshot(_workspace, scope, DefaultIgnore());

        WriteFile("src/Feature/NewThing.cs", "new file in scope");

        var result = WorkspaceScopeEnforcer.DetectOutOfScopeWrites(_workspace, scope, snapshot);

        Assert.False(result.HasOutOfScopeWrites);
    }

    [Fact]
    public void Detect_NoOpTouch_SameBytes_IsNotViolation()
    {
        // Content-based detection: writing the same bytes to an out-of-scope file is NOT a
        // violation — mtime may change but the content hash is unchanged.
        WriteFile("tests/FeatureTests.cs", "same bytes");
        var scope = WriteScope.Parse(["src/**"]);
        var snapshot = WorkspaceScopeEnforcer.Snapshot(_workspace, scope, DefaultIgnore());

        WriteFile("tests/FeatureTests.cs", "same bytes");

        var result = WorkspaceScopeEnforcer.DetectOutOfScopeWrites(_workspace, scope, snapshot);

        Assert.False(result.HasOutOfScopeWrites);
    }

    // ── Violations ───────────────────────────────────────────────────────────────

    [Fact]
    public void Detect_OutOfScopeModification_ReportsViolation()
    {
        WriteFile("tests/FeatureTests.cs", "original");
        var scope = WriteScope.Parse(["src/**"]);
        var snapshot = WorkspaceScopeEnforcer.Snapshot(_workspace, scope, DefaultIgnore());

        WriteFile("tests/FeatureTests.cs", "cheated");

        var result = WorkspaceScopeEnforcer.DetectOutOfScopeWrites(_workspace, scope, snapshot);

        Assert.True(result.HasOutOfScopeWrites);
        Assert.Contains("tests/FeatureTests.cs", result.ViolatingPaths);
    }

    [Fact]
    public void Detect_OutOfScopeNewFile_ReportsViolation()
    {
        var scope = WriteScope.Parse(["src/**"]);
        var snapshot = WorkspaceScopeEnforcer.Snapshot(_workspace, scope, DefaultIgnore());

        WriteFile("tests/NewTests.cs", "new out-of-scope file");

        var result = WorkspaceScopeEnforcer.DetectOutOfScopeWrites(_workspace, scope, snapshot);

        Assert.True(result.HasOutOfScopeWrites);
        Assert.Contains("tests/NewTests.cs", result.ViolatingPaths);
    }

    [Fact]
    public void Detect_OutOfScopeFileDeletion_ReportsViolation()
    {
        // A deletion of an out-of-scope file is a violation — the pre-image recorded it; it is now gone.
        WriteFile("tests/FeatureTests.cs", "must stay");
        var scope = WriteScope.Parse(["src/**"]);
        var snapshot = WorkspaceScopeEnforcer.Snapshot(_workspace, scope, DefaultIgnore());

        File.Delete(Path.Combine(_workspace, "tests", "FeatureTests.cs"));

        var result = WorkspaceScopeEnforcer.DetectOutOfScopeWrites(_workspace, scope, snapshot);

        Assert.True(result.HasOutOfScopeWrites);
        Assert.Contains("tests/FeatureTests.cs", result.ViolatingPaths);
    }

    [Fact]
    public void Detect_MultipleOutOfScopeWrites_AllReported()
    {
        WriteFile("tests/A.cs", "a");
        WriteFile("tests/B.cs", "b");
        WriteFile("src/Impl.cs", "impl");
        var scope = WriteScope.Parse(["src/**"]);
        var snapshot = WorkspaceScopeEnforcer.Snapshot(_workspace, scope, DefaultIgnore());

        WriteFile("tests/A.cs", "a-dirty");
        WriteFile("tests/B.cs", "b-dirty");
        WriteFile("src/Impl.cs", "impl-dirty");  // in-scope — not a violation

        var result = WorkspaceScopeEnforcer.DetectOutOfScopeWrites(_workspace, scope, snapshot);

        Assert.True(result.HasOutOfScopeWrites);
        var sorted = result.ViolatingPaths.OrderBy(p => p, StringComparer.Ordinal).ToList();
        Assert.Equal(["tests/A.cs", "tests/B.cs"], sorted);
    }

    // ── enforcementIgnore default patterns ───────────────────────────────────────

    [Fact]
    public void Detect_WriteUnderStatePath_IsNotViolation()
    {
        var scope = WriteScope.Parse(["src/**"]);
        var snapshot = WorkspaceScopeEnforcer.Snapshot(_workspace, scope, DefaultIgnore());

        WriteFile("state/logs/output.json", "{}");

        var result = WorkspaceScopeEnforcer.DetectOutOfScopeWrites(_workspace, scope, snapshot);

        Assert.False(result.HasOutOfScopeWrites);
    }

    [Fact]
    public void Detect_WriteUnderGitDir_IsNotViolation()
    {
        var scope = WriteScope.Parse(["src/**"]);
        var snapshot = WorkspaceScopeEnforcer.Snapshot(_workspace, scope, DefaultIgnore());

        WriteFile(".git/COMMIT_EDITMSG", "commit msg");

        var result = WorkspaceScopeEnforcer.DetectOutOfScopeWrites(_workspace, scope, snapshot);

        Assert.False(result.HasOutOfScopeWrites);
    }

    [Fact]
    public void Detect_WriteUnderBinObjNodeModules_IsNotViolation()
    {
        // Build artifacts under **/bin/**, **/obj/**, **/node_modules/** are excluded —
        // a build guardrail legitimately writes bin/.
        var scope = WriteScope.Parse(["src/**"]);
        var snapshot = WorkspaceScopeEnforcer.Snapshot(_workspace, scope, DefaultIgnore());

        WriteFile("src/bin/Release/net8.0/Feature.dll", "binary");
        WriteFile("src/obj/Debug/net8.0/Feature.pdb", "debug");
        WriteFile("tests/node_modules/pkg/index.js", "js");

        var result = WorkspaceScopeEnforcer.DetectOutOfScopeWrites(_workspace, scope, snapshot);

        Assert.False(result.HasOutOfScopeWrites);
    }

    // ── Empty scope (writes nothing) ─────────────────────────────────────────────

    [Fact]
    public void Detect_EmptyScope_NewFileIsViolation()
    {
        // Empty scope = "writes nothing" — any workspace write is out-of-scope.
        var scope = WriteScope.Parse([]);
        var snapshot = WorkspaceScopeEnforcer.Snapshot(_workspace, scope, DefaultIgnore());

        WriteFile("src/Anything.cs", "gate tasks should write nothing");

        var result = WorkspaceScopeEnforcer.DetectOutOfScopeWrites(_workspace, scope, snapshot);

        Assert.True(result.HasOutOfScopeWrites);
        Assert.Contains("src/Anything.cs", result.ViolatingPaths);
    }

    [Fact]
    public void Detect_EmptyScope_ExistingFileModified_IsViolation()
    {
        WriteFile("src/Impl.cs", "original");
        var scope = WriteScope.Parse([]);
        var snapshot = WorkspaceScopeEnforcer.Snapshot(_workspace, scope, DefaultIgnore());

        WriteFile("src/Impl.cs", "modified — empty-scope task has no permission");

        var result = WorkspaceScopeEnforcer.DetectOutOfScopeWrites(_workspace, scope, snapshot);

        Assert.True(result.HasOutOfScopeWrites);
        Assert.Contains("src/Impl.cs", result.ViolatingPaths);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private void WriteFile(string relative, string content)
    {
        string full = Path.Combine(_workspace, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static IReadOnlyList<string> DefaultIgnore() =>
        ["state/", ".git/", "**/bin/**", "**/obj/**", "**/node_modules/**"];
}
