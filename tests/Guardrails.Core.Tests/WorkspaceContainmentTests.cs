using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// The shared containment boundary (FIX B, issue #51) used by both the GR2013 validator check and the
/// CapturedFileStore snapshot/restore guard. A single definition of "stays inside the workspace?" so
/// the two can never drift. Workspace is an absolute temp dir so resolution is realistic.
/// </summary>
public sealed class WorkspaceContainmentTests
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "gr-wc-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("tests/Foo.cs")]
    [InlineData("a/b/c/Deep.cs")]
    [InlineData("file with space.cs")]
    public void ContainedPath_DoesNotEscape(string entry) =>
        Assert.False(WorkspaceContainment.Escapes(_workspace, entry));

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("../../etc/passwd")]
    [InlineData("sub/../../escape.cs")]
    public void EscapingRelativePath_Escapes(string entry) =>
        Assert.True(WorkspaceContainment.Escapes(_workspace, entry));

    [Fact]
    public void RootedPath_Escapes()
    {
        string rooted = OperatingSystem.IsWindows() ? @"C:\Windows\System32\hosts" : "/etc/passwd";
        Assert.True(WorkspaceContainment.Escapes(_workspace, rooted));
    }

    [Fact]
    public void WorkspaceRootItself_DoesNotEscape()
    {
        // "." resolves to the workspace root — contained, not an escape.
        Assert.False(WorkspaceContainment.Escapes(_workspace, "."));
    }

    [Fact]
    public void SiblingWithSharedPrefix_Escapes()
    {
        // A sibling dir whose name starts with the workspace name must NOT count as inside it
        // (the directory-boundary check, not a bare string prefix).
        string parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(_workspace)))!;
        string workspaceName = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(_workspace)));
        // entry climbs out then into "<workspaceName>-evil/secret"
        string entry = Path.Combine("..", workspaceName + "-evil", "secret.cs");
        Assert.True(WorkspaceContainment.Escapes(_workspace, entry));
        _ = parent; // parent computed for clarity; the relative entry drives the assertion.
    }
}
