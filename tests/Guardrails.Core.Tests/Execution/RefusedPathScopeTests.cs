using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests.Execution;

/// <summary>
/// Issue #708: which refused PATH is a write WALL, decided against the scope the attempt is actually enforced
/// against. A path the write-scope check would reject cannot be this task's deliverable, so refusing it costs the
/// task nothing a retry cannot route around — it is auxiliary, exactly like a refused command, and it retries.
/// Only a path INSIDE the enforced scope halts, because nothing grants that write between attempts.
///
/// <para>The relativization is the part that needs pinning: the scanner mines the target out of the runtime's own
/// denial message, which names it however the refused tool call did — absolutely as often as not — while a
/// <c>writeScope</c> glob is always workspace-relative.</para>
/// </summary>
public sealed class RefusedPathScopeTests
{
    private static readonly string Workspace = OperatingSystem.IsWindows() ? @"C:\repo\segment" : "/repo/segment";

    /// <summary>A task's declared scope plus an implicit staging destination, as the executor folds them.</summary>
    private static readonly string[] Scope = ["src/**", "docs/ssot.md", ".claude/skills/demo/**"];

    [Theory]
    [InlineData("src/Locked.cs")]
    [InlineData("src/nested/deep/Locked.cs")]
    [InlineData("docs/ssot.md")]
    [InlineData(".claude/skills/demo/SKILL.md")]   // a staging destination is in scope without being declared
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
    public void WithNoEnforcedScope_EveryPathStaysAWall()
    {
        // Serial mode runs no write-scope check, so there is no scope to ask and nothing is auxiliary by it.
        // This keeps the #86 repeat rule exactly as it was wherever the harness cannot say what is deliverable.
        Assert.True(TaskExecutor.RefusedPathIsInScope("tests/Other.cs", scope: null, Workspace));
        Assert.True(TaskExecutor.RefusedPathIsInScope("src/Locked.cs", scope: null, Workspace));
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
}
