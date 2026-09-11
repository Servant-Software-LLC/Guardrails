using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests.Supply;

/// <summary>
/// Design 40 §1: the harness-owned staging tree <c>guardrails supply</c> writes into
/// (<c>&lt;planDirectory&gt;/logs/&lt;runId&gt;/supplied/</c>). <see cref="SuppliedStagingTree"/> is pure
/// path logic — no process spawning, no git — so these are plain Core unit tests against real temp
/// directories standing in for the workspace and the plan directory.
/// <para>
/// TDD red: <see cref="SuppliedStagingTree"/>'s members currently throw <see cref="NotImplementedException"/>,
/// so every test below is expected to FAIL against the stub. Do not add
/// <c>Assert.Throws&lt;NotImplementedException&gt;</c> wrappers — that would make these pass against the
/// stub, which defeats the point of pinning them red.
/// </para>
/// </summary>
[Trait("Category", "Supply")]
public sealed class SuppliedStagingTreeTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "gr-supply-ws-" + Guid.NewGuid().ToString("N"));
    private readonly string _planDirectory = Path.Combine(Path.GetTempPath(), "gr-supply-plan-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch (IOException) { }
        try { Directory.Delete(_planDirectory, recursive: true); } catch (IOException) { }
    }

    // ── StagedPathFor ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StagedPathFor_PutsTheFileUnderLogsRunIdSupplied()
    {
        StagedPathResult result = SuppliedStagingTree.StagedPathFor(_workspace, "R", "vendor/x.js");

        Assert.False(result.Refused);
        Assert.Equal("logs/R/supplied/vendor/x.js", result.StagedPath);
    }

    [Fact]
    public void StagedPathFor_PreservesTheWorkspaceRelativeLayout()
    {
        // The staged layout IS the destination layout (design 40 §1) — there is no separate
        // destination argument, so nested directories must survive intact.
        StagedPathResult result = SuppliedStagingTree.StagedPathFor(_workspace, "R", "vendor/nested/deep/file.txt");

        Assert.False(result.Refused);
        Assert.Equal("logs/R/supplied/vendor/nested/deep/file.txt", result.StagedPath);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("sub/../../escape.cs")]
    public void StagedPathFor_RefusesAPathEscapingTheWorkspace(string escapingPath)
    {
        // GR2019's writeScope traversal rule (plan 08 §2/§3.4), applied to a guardrails supply CLI
        // argument: a relative path whose ".." segments resolve outside the workspace is refused.
        StagedPathResult result = SuppliedStagingTree.StagedPathFor(_workspace, "R", escapingPath);

        Assert.True(result.Refused);
        Assert.Null(result.StagedPath);
    }

    [Fact]
    public void StagedPathFor_RefusesAnAbsolutePathOutsideTheWorkspace()
    {
        // Distinct from the ".." case: an absolute/rooted path is refused outright, regardless of
        // where it resolves to, the same early rejection WorkspaceContainment.Escapes applies.
        string rooted = OperatingSystem.IsWindows() ? @"C:\Windows\System32\hosts" : "/etc/passwd";

        StagedPathResult result = SuppliedStagingTree.StagedPathFor(_workspace, "R", rooted);

        Assert.True(result.Refused);
        Assert.Null(result.StagedPath);
    }

    // ── DrainableFiles ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DrainableFiles_OnAnEmptyTree_IsEmpty()
    {
        // §2 step 1, the never-weaker requirement: a run that never called `supply` has no
        // logs/<runId>/supplied/ directory at all, and the feature must be completely inert for it.
        Directory.CreateDirectory(_planDirectory);

        IReadOnlyList<SuppliedFile> files = SuppliedStagingTree.DrainableFiles(_planDirectory, "R");

        Assert.Empty(files);
    }

    [Fact]
    public void DrainableFiles_EnumeratesEveryStagedFileWithItsDestination()
    {
        string suppliedRoot = Path.Combine(_planDirectory, "logs", "R", "supplied");
        string stagedA = Path.Combine(suppliedRoot, "vendor", "x.js");
        string stagedB = Path.Combine(suppliedRoot, "docs", "notes.md");
        Directory.CreateDirectory(Path.GetDirectoryName(stagedA)!);
        Directory.CreateDirectory(Path.GetDirectoryName(stagedB)!);
        File.WriteAllText(stagedA, "content-a");
        File.WriteAllText(stagedB, "content-b");

        IReadOnlyList<SuppliedFile> files = SuppliedStagingTree.DrainableFiles(_planDirectory, "R");

        Assert.Equal(2, files.Count);
        Dictionary<string, string> byDestination = files.ToDictionary(f => f.DestinationPath, f => f.AbsoluteStagedPath);
        Assert.Equal(stagedA, byDestination["vendor/x.js"]);
        Assert.Equal(stagedB, byDestination["docs/notes.md"]);
    }
}
