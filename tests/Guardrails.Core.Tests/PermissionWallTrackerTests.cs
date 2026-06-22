using Guardrails.Core.Execution;

namespace Guardrails.Core.Tests;

/// <summary>
/// Pins the runner-agnostic decision logic for the permission-wall early halt (issues #86 / #104):
/// a <c>.claude/</c> wall is STRUCTURAL and halts on the first hit (#104); any other path refused
/// across <see cref="PermissionWallTracker.RepeatThreshold"/> attempts halts on the repeat (#86);
/// a one-off non-structural refusal does NOT halt (it may be a transient slip the retry clears).
/// </summary>
public sealed class PermissionWallTrackerTests
{
    [Fact]
    public void NoBlocks_DoesNotHalt()
    {
        var tracker = new PermissionWallTracker();
        tracker.Observe(null);
        tracker.Observe([]);
        Assert.False(tracker.ShouldHalt().Halt);
    }

    [Fact]
    public void ClaudeDirPath_HaltsOnFirstHit_AsStructural()
    {
        // #104: a single .claude/ refusal is enough — the runtime blocks .claude/ writes even under
        // acceptEdits, so no retry can clear it. Halt immediately, zero retries wasted.
        var tracker = new PermissionWallTracker();
        tracker.Observe([".claude/skills/certify-knowledge/SKILL.md"]);

        PermissionWallDecision decision = tracker.ShouldHalt();
        Assert.True(decision.Halt);
        Assert.True(decision.HasStructural);
        Assert.Equal(new[] { ".claude/skills/certify-knowledge/SKILL.md" }, decision.StructuralPaths);
        Assert.Empty(decision.RepeatedPaths);
    }

    [Theory]
    [InlineData(".claude/skills/a/SKILL.md")]
    [InlineData("C:\\repo\\.claude\\agents\\x.md")]
    [InlineData("/home/u/proj/.claude/commands/y.md")]
    [InlineData(".claude")]
    [InlineData("repo/.claude")]
    public void IsClaudeDir_RecognizesClaudeTree_AcrossPathShapes(string path) =>
        Assert.True(PermissionWallTracker.IsClaudeDir(path));

    [Theory]
    [InlineData("src/Foo.cs")]
    [InlineData("claude/notdot.md")]            // no leading dot — not the .claude tree
    [InlineData("docs/.claudette/x")]           // a different dir that merely starts with .claude
    public void IsClaudeDir_RejectsNonClaudeTree(string path) =>
        Assert.False(PermissionWallTracker.IsClaudeDir(path));

    [Fact]
    public void NonStructuralPath_RefusedOnce_DoesNotHalt()
    {
        // #86: a SINGLE non-.claude refusal is not (yet) a wall — give the retry a chance to clear it.
        var tracker = new PermissionWallTracker();
        tracker.Observe(["src/protected/Secret.cs"]);
        Assert.False(tracker.ShouldHalt().Halt);
    }

    [Fact]
    public void NonStructuralPath_RefusedOnTwoAttempts_HaltsAsRepeated()
    {
        // #86: the SAME path refused on two consecutive attempts is a structural blocker the agent
        // cannot fix by retrying. Halt on the repeat rather than burning the remaining budget.
        var tracker = new PermissionWallTracker();
        tracker.Observe(["src/protected/Secret.cs"]);   // attempt 1
        Assert.False(tracker.ShouldHalt().Halt);

        tracker.Observe(["src/protected/Secret.cs"]);   // attempt 2 — same wall
        PermissionWallDecision decision = tracker.ShouldHalt();
        Assert.True(decision.Halt);
        Assert.False(decision.HasStructural);
        Assert.Equal(new[] { "src/protected/Secret.cs" }, decision.RepeatedPaths);
        Assert.Empty(decision.StructuralPaths);
    }

    [Fact]
    public void DifferentPathsEachAttempt_DoNotCountAsARepeat()
    {
        // Two DIFFERENT paths, one each attempt, is not the "same wall repeated" pattern — each has
        // been refused only once, so neither reaches the repeat threshold and the run keeps retrying.
        var tracker = new PermissionWallTracker();
        tracker.Observe(["src/a/One.cs"]);
        tracker.Observe(["src/b/Two.cs"]);
        Assert.False(tracker.ShouldHalt().Halt);
    }

    [Fact]
    public void SamePathRefusedManyTimesInOneAttempt_CountsAsOneAttempt_NotARepeat()
    {
        // Observe is called once per attempt with that attempt's DISTINCT refused paths; the scanner
        // already de-dups within an attempt. A single attempt cannot itself trip the cross-attempt
        // repeat rule for a non-structural path.
        var tracker = new PermissionWallTracker();
        tracker.Observe(["src/a/One.cs"]);   // a single attempt
        Assert.False(tracker.ShouldHalt().Halt);
    }

    [Fact]
    public void PathsAreNormalized_SoQuotedAndUnquotedFormsRepeatTogether()
    {
        var tracker = new PermissionWallTracker();
        tracker.Observe(["\"src/a/One.cs\""]);   // quoted (as some messages embed it)
        tracker.Observe(["src/a/One.cs"]);        // bare
        Assert.True(tracker.ShouldHalt().Halt);
    }

    [Fact]
    public void AllPaths_ListsStructuralFirst_ThenRepeated_Deduplicated()
    {
        var tracker = new PermissionWallTracker();
        tracker.Observe(["src/a/One.cs", ".claude/x.md"]);   // attempt 1
        tracker.Observe(["src/a/One.cs"]);                    // attempt 2 → One.cs repeats

        PermissionWallDecision decision = tracker.ShouldHalt();
        Assert.True(decision.Halt);
        Assert.Equal(new[] { ".claude/x.md", "src/a/One.cs" }, decision.AllPaths);
    }
}
