using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests.Execution;

/// <summary>
/// Issue #708: which refused PATH is a write WALL, decided against the scope the attempt is actually enforced
/// against. A path the write-scope check would reject cannot be this task's deliverable, so refusing it costs the
/// task nothing a retry cannot route around — it is auxiliary, exactly like a refused command, and it retries.
/// Only a path INSIDE the enforced scope halts, because nothing grants that write between attempts.
///
/// <para><b>The relativization is the part that needs pinning</b>, and it has to reconcile SPELLINGS, not just
/// separators. The scanner mines its target out of the runtime's own denial message, which names the path however
/// the refused tool call did, while a <c>writeScope</c> glob is always workspace-relative. Worse, the two sides
/// routinely spell the same directory differently: under #383 the workspace is a short junction (<c>C:\.a\…</c>)
/// while a canonicalizing tool reports the real <c>…\worktrees\…</c> path, and on macOS the same shape appears as
/// <c>/var</c> versus <c>/private/var</c> (#452). A lexical comparison answers "different directory" to both, which
/// fails in the direction that BURNS THE BUDGET: a real in-scope wall reads as auxiliary and nothing halts. So the
/// comparison goes through <see cref="Guardrails.Core.Io.RealPath"/>, which resolves reparse points and degrades to
/// the lexical answer when it cannot.</para>
/// </summary>
public sealed class RefusedPathScopeTests
{
    private static readonly string Workspace = OperatingSystem.IsWindows() ? @"C:\repo\segment" : "/repo/segment";

    /// <summary>A task's declared scope. Every entry is workspace-relative, as a writeScope always is.</summary>
    private static readonly string[] Scope = ["src/**", "docs/ssot.md"];

    [Theory]
    [InlineData("src/Locked.cs")]
    [InlineData("src/nested/deep/Locked.cs")]
    [InlineData("docs/ssot.md")]
    [InlineData("\"src/Locked.cs\"")]              // some denial messages embed the path quoted
    public void APathInsideTheEnforcedScope_IsAWall(string refused) =>
        Assert.True(TaskExecutor.RefusedPathIsInScope(refused, Scope, Workspace));

    [Theory]
    [InlineData("tests/Other.cs")]
    [InlineData("docs/notes.md")]                  // the scope names one docs FILE, not the directory
    [InlineData(".claude/settings.json")]
    public void APathOutsideTheEnforcedScope_IsAuxiliary(string refused) =>
        Assert.False(TaskExecutor.RefusedPathIsInScope(refused, Scope, Workspace));

    [Fact]
    public void AnAbsolutePathUnderTheWorkspace_IsRelativizedBeforeTheScopeIsAsked()
    {
        // Plan 40's own refusals named absolute paths. Comparing one to a relative glob without relativizing
        // matches nothing, which would read every absolute refusal as out of scope and never halt.
        Assert.True(TaskExecutor.RefusedPathIsInScope(Path.Combine(Workspace, "src", "Locked.cs"), Scope, Workspace));
        Assert.False(TaskExecutor.RefusedPathIsInScope(Path.Combine(Workspace, "tests", "Other.cs"), Scope, Workspace));
    }

    [Fact]
    public void APathOutsideTheWorkspaceEntirely_IsNotTheDeliverable()
    {
        string elsewhere = OperatingSystem.IsWindows()
            ? @"C:\elsewhere\src\Locked.cs"
            : "/elsewhere/src/Locked.cs";

        Assert.False(TaskExecutor.RefusedPathIsInScope(elsewhere, Scope, Workspace));
        Assert.False(TaskExecutor.RefusedPathIsInScope("../sibling/src/Locked.cs", Scope, Workspace));
    }

    [Fact]
    public void WithNoEnforcedScope_EveryPathINSIDeTheWorkspaceStaysAWall()
    {
        // Serial mode runs no write-scope check, so there is no scope to ask and nothing is auxiliary by it.
        // This keeps the #86 repeat rule exactly as it was wherever the harness cannot say what is deliverable.
        Assert.True(TaskExecutor.RefusedPathIsInScope("tests/Other.cs", scope: null, Workspace));
        Assert.True(TaskExecutor.RefusedPathIsInScope("src/Locked.cs", scope: null, Workspace));
    }

    [Fact]
    public void WithNoEnforcedScope_APathOutsideTheWorkspaceIsStillNotAWall()
    {
        // NIT-10. "No scope" means the harness cannot say WHICH file is the deliverable — not that a path in
        // someone else's tree became one. Containment is asked on every path, scope or no scope.
        string elsewhere = OperatingSystem.IsWindows()
            ? @"C:\elsewhere\src\Locked.cs"
            : "/elsewhere/src/Locked.cs";

        Assert.False(TaskExecutor.RefusedPathIsInScope(elsewhere, scope: null, Workspace));
        Assert.False(TaskExecutor.RefusedPathIsInScope("../sibling/Thing.cs", scope: null, Workspace));
    }

    [Fact]
    public void ATargetThatNamesNoFile_IsNotAWall()
    {
        Assert.False(TaskExecutor.RefusedPathIsInScope("   ", Scope, Workspace));
        Assert.False(TaskExecutor.RefusedPathIsInScope(Workspace, Scope, Workspace));
    }

    [Fact]
    public void AnEmptyEnforcedScope_ClaimsNothing_SoEveryPathIsAuxiliary()
    {
        // #389 fail-closed: an empty scope means the attempt may write nothing, so no refused path can be the
        // deliverable. The attempt retries and the refusal rides along as secondary context.
        Assert.False(TaskExecutor.RefusedPathIsInScope("src/Locked.cs", [], Workspace));
    }

    /// <summary>
    /// The BLOCKER case, on the OS where it is reproducible. Under #383 the run's workspace is the SHORT JUNCTION
    /// (<c>C:\.a\…</c>) while a canonicalizing tool reports the real target path — two spellings of one directory.
    /// A lexical comparison calls the refusal out-of-scope, so a real in-scope wall is classified auxiliary,
    /// nothing halts, and the budget burns against a wall no summary names: #708's own defect, restored.
    /// </summary>
    [Fact]
    public void ARefusalSpelledThroughTheJunctionsTarget_IsStillInsideTheJunctionedWorkspace()
    {
        if (!OperatingSystem.IsWindows())
        {
            // The junction primitive is Windows-only. The same two-spellings shape on macOS (/var vs
            // /private/var, #452) is resolved by the identical RealPath call this exercises.
            return;
        }

        string root = Path.Combine(Path.GetTempPath(), "gr-708-junction-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(root, "real-workspace");
        string link = Path.Combine(root, "link");
        Directory.CreateDirectory(target);

        try
        {
            Assert.True(
                WorktreeJunction.TryCreateJunction(link, target),
                "the junction must be created for this test to mean anything");

            // The workspace as the RUN sees it (the alias); the refusal as a canonicalizing tool spells it.
            string refused = Path.Combine(target, "src", "Locked.cs");

            Assert.True(
                TaskExecutor.RefusedPathIsInScope(refused, Scope, link),
                "a refusal spelled through the junction's TARGET is inside the junction-spelled workspace");

            // And the reverse spelling, which is what a refusal reported by the agent's own cwd looks like.
            Assert.True(TaskExecutor.RefusedPathIsInScope(Path.Combine(link, "src", "Locked.cs"), Scope, target));

            // The control: resolution must not turn an unrelated tree into a match.
            Assert.False(TaskExecutor.RefusedPathIsInScope(Path.Combine(root, "elsewhere", "src", "X.cs"), Scope, link));
        }
        finally
        {
            try { WorktreeJunction.RemoveJunctionLink(link); } catch (IOException) { }
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// NIT-8: the path comparison must be case-INSENSITIVE on Windows. Without this an
    /// <c>OrdinalIgnoreCase</c>-to-<c>Ordinal</c> mutation leaves every other row in this file green, because
    /// they all happen to agree on case.
    /// </summary>
    [Fact]
    public void OnWindows_TheWorkspaceSpellingIsCaseInsensitive()
    {
        if (!OperatingSystem.IsWindows())
        {
            // POSIX filesystems are case-SENSITIVE, so src/ and SRC/ are genuinely different directories there.
            return;
        }

        Assert.True(TaskExecutor.RefusedPathIsInScope(@"C:\REPO\SEGMENT\src\Locked.cs", Scope, @"C:\repo\segment"));
    }

    /// <summary>
    /// A <c>.claude/</c> path never reaches this predicate: <see cref="PermissionWallTracker"/> classifies one as
    /// STRUCTURAL and it lands in <c>StructuralPaths</c>, never in <c>RepeatedPaths</c>, which is the only list
    /// the scope filter is applied to. Asserting a <c>.claude/</c> row here would read as proof that staged
    /// <c>.claude/</c> deliverables are covered by the scope rule when production cannot reach the case at all —
    /// the structural wall is what covers them, consulted separately at every halt site.
    /// </summary>
    [Fact]
    public void AClaudePath_IsClassifiedStructural_SoTheScopeFilterNeverSeesIt()
    {
        var tracker = new PermissionWallTracker();
        tracker.Observe(1, [".claude/skills/demo/SKILL.md"]);
        tracker.Observe(2, [".claude/skills/demo/SKILL.md"]);

        PermissionWallDecision decision = tracker.ShouldHalt();
        Assert.Equal([".claude/skills/demo/SKILL.md"], decision.StructuralPaths);
        Assert.Empty(decision.RepeatedPaths);
    }
}
