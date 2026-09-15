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

    /// <summary>
    /// Issue #534 — an attempt that refused NOTHING is not standing at a wall, whatever its predecessors
    /// hit.
    ///
    /// <para>
    /// Measured on run <c>2026-08-29T16-37-39Z-fc5d</c>, task
    /// <c>08-record-visibility-surfaces-in-ssot</c>:
    /// </para>
    /// <code>
    /// attempt 1  15 Bash calls,  0 refused   guardrail-failed (anchor mismatch)
    /// attempt 2  12 Bash calls,  5 refused   guardrail-failed (anchor mismatch)
    /// attempt 3   0 Bash calls,  0 refused   PERMISSION-DENIED
    /// </code>
    /// <para>
    /// Attempt 3 used <c>Read</c> ×6, <c>Grep</c> ×2, <c>Write</c> ×1 — it had stopped reaching for the
    /// refused command, re-read both targets, and emitted a well-formed root-level
    /// <c>needsHarnessWrite</c> with corrected anchors (its own transcript: <i>"anchor texts are now copied
    /// exactly from the files, matching character-for-character"</i>). The wall fired on attempt 2's
    /// HISTORY, settled the task <c>needs-human</c> with "no further retries", and that fragment was never
    /// applied. The mechanism designed to stop an agent burning attempts on an unclearable wall threw away
    /// the attempt that had cleared it. Cost: $1.27, and a plan stalled at task 8 of 8.
    /// </para>
    ///
    /// <para>
    /// The premise in the tracker's own doc is what fails: <i>"a path retrying cannot clear"</i>. Retrying
    /// DID clear it — by not making the call. A refused auxiliary command is one route among several to the
    /// same end, and the agent found another.
    /// </para>
    /// </summary>
    [Fact]
    public void AnAttemptThatRefusedNothing_DoesNotInheritThePriorAttemptsWall()
    {
        var tracker = new PermissionWallTracker();
        tracker.Observe(["python3 -m json.tool out.json"]);   // attempt 1 — refused
        tracker.Observe(["python3 -m json.tool out.json"]);   // attempt 2 — refused again: a repeat

        // Left there, this is a halt — and correctly so, while the agent is still hitting it.
        Assert.True(tracker.ShouldHalt().Halt);

        tracker.Observe([]);                                  // attempt 3 — refused NOTHING

        Assert.False(tracker.ShouldHalt().Halt,
            "an attempt that made no refused calls has cleared the wall; killing it on the previous "
            + "attempt's history discards the recovery AND its deliverable");
    }

    /// <summary>
    /// The control, and the reason this is a guard rather than a repeal: a <c>.claude/</c> wall still halts
    /// on the attempt that HITS it, and a repeated path still halts on the attempt that RE-hits it. In both
    /// cases the current attempt is, by construction, not clean — so the #534 guard cannot weaken either
    /// rule, and a fix that did would reopen #86 and #104.
    /// </summary>
    [Fact]
    public void AnAttemptThatDidRefuse_StillHalts_OnBothRules()
    {
        var structural = new PermissionWallTracker();
        structural.Observe([".claude/skills/x/SKILL.md"]);
        Assert.True(structural.ShouldHalt().Halt);

        var repeated = new PermissionWallTracker();
        repeated.Observe(["src/a/One.cs"]);
        Assert.False(repeated.ShouldHalt().Halt);
        repeated.Observe(["src/a/One.cs"]);
        Assert.True(repeated.ShouldHalt().Halt);
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

    // ── #708: a refused COMMAND is a wall key, never a write path ─────────────────────────────────────

    /// <summary>
    /// Issue #708. The runner's refused-target list carries refused Bash COMMANDS as well as write paths, and the
    /// tracker read every key as a path. So a command that merely names a <c>.claude/</c> path — a <c>grep</c> the
    /// task was not granted — became the STRUCTURAL <c>.claude/</c> write wall, which settles on its first refusal.
    /// The runner reports which keys are commands, so the tracker tells them apart without guessing from shape.
    /// </summary>
    [Fact]
    public void ARefusedCommandThatNamesAClaudePath_IsNotTheStructuralWriteWall()
    {
        const string grep = "grep -rn needsHarnessWrite /repo/.claude/skills";
        var tracker = new PermissionWallTracker();

        tracker.Observe([grep], refusedCommands: [grep]);   // attempt 1

        PermissionWallDecision decision = tracker.ShouldHalt();
        Assert.Empty(decision.StructuralPaths);
        Assert.False(decision.Halt, "a command refused on one attempt is not a wall yet, whatever path it mentions");
    }

    [Fact]
    public void ACommandRefusedOnTwoAttempts_IsARepeatedCommand_Verbatim_NotARepeatedPath()
    {
        const string selfCheck = "echo \"EXIT:$?\"";
        var tracker = new PermissionWallTracker();

        tracker.Observe([selfCheck], refusedCommands: [selfCheck]);   // attempt 1
        tracker.Observe([selfCheck], refusedCommands: [selfCheck]);   // attempt 2

        PermissionWallDecision decision = tracker.ShouldHalt();
        Assert.True(decision.HasRepeated);
        Assert.Empty(decision.RepeatedPaths);
        // Closing quote included: trimming quotes off a command the way a path is normalized is what reported plan
        // 40's `echo "EXIT:$?"` as `echo "EXIT:$?`.
        Assert.Equal(new[] { selfCheck }, decision.RepeatedCommands);
    }

    /// <summary>
    /// The control: naming the commands must not turn the paths beside them into commands, or the structural and
    /// repeated-path rules would silently stop seeing real write walls.
    /// </summary>
    [Fact]
    public void PathsRefusedBesideACommand_AreStillPaths()
    {
        const string command = "pwsh -NoProfile -File guardrails/01-check.ps1";
        var tracker = new PermissionWallTracker();

        tracker.Observe(["src/locked/Protected.cs", ".claude/agents/x.md", command], refusedCommands: [command]);
        tracker.Observe(["src/locked/Protected.cs", command], refusedCommands: [command]);

        PermissionWallDecision decision = tracker.ShouldHalt();
        Assert.Equal(new[] { ".claude/agents/x.md" }, decision.StructuralPaths);
        Assert.Equal(new[] { "src/locked/Protected.cs" }, decision.RepeatedPaths);
        Assert.Equal(new[] { command }, decision.RepeatedCommands);
    }
}
