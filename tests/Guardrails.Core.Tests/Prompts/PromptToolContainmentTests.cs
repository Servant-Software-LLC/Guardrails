using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests.Prompts;

/// <summary>
/// Pins the plan 28 §5 contract for <see cref="PromptToolContainment.IsReadable"/> — the primitive
/// that replaces <see cref="Guardrails.Core.Execution.WorkspaceContainment.Escapes"/> for the prompt
/// read tools, because that function rejects every ROOTED path outright and every path the harness
/// hands a prompt is absolute. All of these must FAIL against the throwing stub.
/// </summary>
public sealed class PromptToolContainmentTests
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "gr-ptc-ws-" + Guid.NewGuid().ToString("N"));

    private readonly string _planDir =
        Path.Combine(Path.GetTempPath(), "gr-ptc-plan-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void AbsolutePathInsideRoot_IsReadable()
    {
        // The case WorkspaceContainment.Escapes gets wrong: this candidate is absolute, so that
        // function would reject it outright regardless of the workspace.
        string candidate = Path.Combine(_workspace, "src", "Foo.cs");

        Assert.True(PromptToolContainment.IsReadable([_workspace], candidate));
    }

    [Fact]
    public void AbsolutePathOutsideEveryRoot_IsRefused()
    {
        string outside = Path.Combine(
            Path.GetTempPath(), "gr-ptc-other-" + Guid.NewGuid().ToString("N"), "secret.cs");

        Assert.False(PromptToolContainment.IsReadable([_workspace], outside));
    }

    [Fact]
    public void TraversalEscapingAfterNormalisation_IsRefused()
    {
        string escaping = Path.Combine(_workspace, "..", "outside", "secret.cs");

        Assert.False(PromptToolContainment.IsReadable([_workspace], escaping));
    }

    [Fact]
    public void DirectoryBoundary_SiblingWithSharedPrefix_IsRefused()
    {
        // root ".../<name>" must NOT admit ".../<name>evil/x.cs" -- a string-prefix match is not a
        // directory-boundary match (the plan's "/repo/src" vs "/repo/srcevil" example).
        string siblingWithSharedPrefix = Path.Combine(
            Path.GetDirectoryName(_workspace)!,
            Path.GetFileName(_workspace) + "evil",
            "x.cs");

        Assert.False(PromptToolContainment.IsReadable([_workspace], siblingWithSharedPrefix));
    }

    [Fact]
    public void BothRootsAreHonoured()
    {
        string[] roots = [_workspace, _planDir];
        string underWorkspace = Path.Combine(_workspace, "a.cs");
        string underPlanDir = Path.Combine(_planDir, "state.json");

        Assert.True(PromptToolContainment.IsReadable(roots, underWorkspace));
        Assert.True(PromptToolContainment.IsReadable(roots, underPlanDir));
    }

    [Fact]
    public void EmptyRootEntries_AreDroppedBeforeMatching()
    {
        // Path.GetFullPath("") throws, and a real caller (the criticality assessment) supplies empty
        // entries alongside real roots -- they must be filtered out before normalisation runs, not
        // passed through or allowed to crash the call.
        string[] roots = [string.Empty, _workspace, string.Empty];
        string underWorkspace = Path.Combine(_workspace, "a.cs");
        string outside = Path.Combine(
            Path.GetTempPath(), "gr-ptc-other-" + Guid.NewGuid().ToString("N"), "secret.cs");

        Assert.True(PromptToolContainment.IsReadable(roots, underWorkspace));
        Assert.False(PromptToolContainment.IsReadable(roots, outside));
    }

    [Fact]
    public void EmptyRootSet_DeniesEverything()
    {
        // The exact shape the criticality assessment supplies (plan §5): WorkingDirectory and
        // PlanDirectory both empty, because that caller needs no tools at all. After dropping the
        // empties there are zero roots, so even an otherwise-ordinary absolute path is denied --
        // deliberately: wrong-direction here is a loud refused tool call, not a silent read of the
        // whole filesystem.
        string[] roots = [string.Empty, string.Empty];
        string anyAbsolutePath = Path.Combine(_workspace, "a.cs");

        Assert.False(PromptToolContainment.IsReadable(roots, anyAbsolutePath));
    }
}
