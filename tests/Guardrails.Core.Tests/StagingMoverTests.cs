using Guardrails.Core.Execution;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// Unit tests for <see cref="StagingMover"/> (SSOT §3.5, issue #130): the pure filesystem move that
/// relocates an action's staged <c>.claude/</c>-destined deliverable into its real path, then deletes
/// the staging tree. No git, no executor — just temp dirs.
/// </summary>
public sealed class StagingMoverTests : IDisposable
{
    private readonly string _root;
    private readonly string _staging;
    private readonly string _workspace;

    public StagingMoverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-stagemover-" + Guid.NewGuid().ToString("N"));
        // The staging root is conventionally <workspace>/.guardrails-staging/<task-id>/, but the
        // mover only needs the two absolute roots — keep them siblings for clarity.
        _workspace = Path.Combine(_root, "workspace");
        _staging = Path.Combine(_workspace, ".guardrails-staging", "05-skill");
        Directory.CreateDirectory(_staging);
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void StageFile(string relative, string content)
    {
        string path = Path.Combine(_staging, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private bool WorkspaceHas(string relative) =>
        File.Exists(Path.Combine(_workspace, relative.Replace('/', Path.DirectorySeparatorChar)));

    private string WorkspaceRead(string relative) =>
        File.ReadAllText(Path.Combine(_workspace, relative.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void GlobSubtree_LandsUnderTo_PreservingRelativeStructure_AndDeletesStaging()
    {
        // skill/** moves the subtree below the fixed prefix "skill/" directly under the to dir.
        StageFile("skill/SKILL.md", "# skill");
        StageFile("skill/references/extra.md", "ref");

        StagingMoveResult result = StagingMover.Move(
            _staging, _workspace,
            [new StagingOutput { From = "skill/**", To = ".claude/skills/certify/" }]);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal("# skill", WorkspaceRead(".claude/skills/certify/SKILL.md"));
        Assert.Equal("ref", WorkspaceRead(".claude/skills/certify/references/extra.md"));
        // The staging tree is deleted (no scaffolding survives to be committed).
        Assert.False(Directory.Exists(_staging));
    }

    [Fact]
    public void BareFile_ToDirectory_LandsUnderItKeepingBasename()
    {
        StageFile("agent.md", "persona");

        StagingMoveResult result = StagingMover.Move(
            _staging, _workspace,
            [new StagingOutput { From = "agent.md", To = ".claude/agents/" }]);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal("persona", WorkspaceRead(".claude/agents/agent.md"));
    }

    [Fact]
    public void BareFile_ToExactFilePath_MovesToThatPath()
    {
        StageFile("out.md", "command");

        StagingMoveResult result = StagingMover.Move(
            _staging, _workspace,
            [new StagingOutput { From = "out.md", To = ".claude/commands/do.md" }]);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal("command", WorkspaceRead(".claude/commands/do.md"));
        Assert.False(WorkspaceHas(".claude/commands/out.md"));
    }

    [Fact]
    public void BareDirectory_MovesWholeSubtreeUnderTo()
    {
        StageFile("skill/SKILL.md", "a");
        StageFile("skill/nested/b.md", "b");

        StagingMoveResult result = StagingMover.Move(
            _staging, _workspace,
            [new StagingOutput { From = "skill", To = ".claude/skills/certify/" }]);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal("a", WorkspaceRead(".claude/skills/certify/SKILL.md"));
        Assert.Equal("b", WorkspaceRead(".claude/skills/certify/nested/b.md"));
    }

    [Fact]
    public void ExistingDestination_IsOverwritten_LastWriteWins()
    {
        // Simulate a prior task's artifact already present at the destination.
        string dest = Path.Combine(_workspace, ".claude", "skills", "certify", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllText(dest, "OLD");
        StageFile("skill/SKILL.md", "NEW");

        StagingMoveResult result = StagingMover.Move(
            _staging, _workspace,
            [new StagingOutput { From = "skill/**", To = ".claude/skills/certify/" }]);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal("NEW", WorkspaceRead(".claude/skills/certify/SKILL.md"));
    }

    [Fact]
    public void GlobMatchesNothing_FailsEmptySource_AndStillDeletesStaging()
    {
        // The action declared skill/** but produced nothing — a deliverable-not-produced condition.
        StagingMoveResult result = StagingMover.Move(
            _staging, _workspace,
            [new StagingOutput { From = "skill/**", To = ".claude/skills/certify/" }]);

        Assert.False(result.Succeeded);
        Assert.Equal("skill/**", result.EmptySourceFrom);
        Assert.Contains("matched no files", result.FailureReason);
        Assert.False(WorkspaceHas(".claude/skills/certify/SKILL.md"));
        Assert.False(Directory.Exists(_staging));
    }

    [Fact]
    public void BareFileMissing_FailsEmptySource()
    {
        StagingMoveResult result = StagingMover.Move(
            _staging, _workspace,
            [new StagingOutput { From = "missing.md", To = ".claude/commands/do.md" }]);

        Assert.False(result.Succeeded);
        Assert.Equal("missing.md", result.EmptySourceFrom);
    }

    [Fact]
    public void MovedPaths_AreWorkspaceRelative_ForThePostMoveSurface()
    {
        StageFile("skill/SKILL.md", "x");

        StagingMoveResult result = StagingMover.Move(
            _staging, _workspace,
            [new StagingOutput { From = "skill/**", To = ".claude/skills/certify/" }]);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Contains(".claude/skills/certify/SKILL.md", result.MovedPaths);
    }
}
