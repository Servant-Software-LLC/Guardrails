using Guardrails.Core.Execution;
using static Guardrails.Core.Tests.PlanFixtures;

namespace Guardrails.Core.Tests;

public sealed class RetryPolicyTests
{
    [Fact]
    public void ActionFailure_IncludesExitCodeAndStderrTail()
    {
        var action = new ProcessResult
        {
            ExitCode = 3,
            StandardOutput = "building...",
            StandardError = "error CS1002: ; expected",
            TimedOut = false,
            Duration = TimeSpan.FromSeconds(1)
        };

        string feedback = RetryPolicy.ForActionFailure(Task("01-t"), attempt: 1, action);

        Assert.Contains("exited with code 3", feedback);
        Assert.Contains("error CS1002", feedback);
        Assert.Contains("Do NOT start over", feedback);
        Assert.Contains("Attempt 1", feedback);
    }

    [Fact]
    public void GuardrailFailures_NameEachFailureWithReason_AndListPassed()
    {
        var results = new List<GuardrailResult>
        {
            new() { Name = "01-build", Passed = true },
            new() { Name = "02-tests", Passed = false, Reason = "3 of 14 tests failed" },
            new() { Name = "03-lint", Passed = false, Reason = null }
        };

        string feedback = RetryPolicy.ForGuardrailFailures(Task("01-t"), attempt: 2, results);

        Assert.Contains("### 02-tests", feedback);
        Assert.Contains("3 of 14 tests failed", feedback);
        Assert.Contains("### 03-lint", feedback);
        Assert.Contains("no reason printed", feedback);
        Assert.Contains("PASSED (do not break these): 01-build", feedback);
    }

    [Fact]
    public void GuardrailFailures_ScriptAction_UsesDeterministicScriptWording_NotAgentWording()
    {
        // Issue #264 part 2: a `script` action has NO agent to read this and "fix what failed" — the
        // agent-oriented "keep what already works / Do NOT start over" header is nonsensical for it.
        // The feedback must instead say the script (or its guardrail) has to be EDITED to converge.
        var results = new List<GuardrailResult>
        {
            new() { Name = "02-vendored", Passed = false, Reason = "vendored dep is stale" }
        };

        string feedback = RetryPolicy.ForGuardrailFailures(Task("02-vendor"), attempt: 2, results);

        Assert.Contains("deterministic `script` action", feedback);
        Assert.Contains("no agent to self-correct", feedback);
        Assert.Contains("must be", feedback);                              // "the script or its guardrail must be edited"
        Assert.DoesNotContain("keep what", feedback);                     // agent-oriented header is gone
        Assert.DoesNotContain("Do NOT start over", feedback);
        // The concrete failure detail still reaches the reader (a human, here).
        Assert.Contains("### 02-vendored", feedback);
        Assert.Contains("vendored dep is stale", feedback);
    }

    [Fact]
    public void GuardrailFailures_PromptAction_KeepsAgentOrientedWording()
    {
        // Regression guard (#264 must NOT touch prompt-action feedback): a PROMPT action DOES have an
        // agent that can self-correct, so it keeps the "fix what failed, keep what works" header and
        // must NOT get the deterministic-script wording.
        var results = new List<GuardrailResult>
        {
            new() { Name = "02-tests", Passed = false, Reason = "3 of 14 tests failed" }
        };

        string feedback = RetryPolicy.ForGuardrailFailures(PromptTask("07-impl"), attempt: 2, results);

        Assert.Contains("Do NOT start over", feedback);
        Assert.Contains("keep what", feedback);
        Assert.DoesNotContain("deterministic `script` action", feedback);
        Assert.DoesNotContain("no agent to self-correct", feedback);
    }

    [Fact]
    public void WriteScopeViolation_ScriptAction_UsesDeterministicScriptWording()
    {
        // The observed 10-gitignore case is a `script` write-scope violation — its feedback header must
        // also drop the agent-oriented wording (issue #264).
        var offenses = new List<WriteScopeOffense> { new() { Path = "outside.txt", Status = 'A' } };

        string feedback = RetryPolicy.ForWriteScopeViolation(Task("10-gitignore"), attempt: 2, offenses);

        Assert.Contains("deterministic `script` action", feedback);
        Assert.DoesNotContain("Do NOT start over", feedback);
        Assert.Contains("outside.txt", feedback);                          // the concrete offense survives
    }

    [Fact]
    public void TestsUntouchedFailure_TellsAgentNotToEditTests_AndDropsDoNotBreakLine()
    {
        // issue #51: when tests-untouched fails, the harness has restored the test file to baseline;
        // the feedback must steer the agent to fix the implementation (or escalate), and must NOT
        // tell it to preserve the tests-pass guardrail it gamed by editing the tests.
        var results = new List<GuardrailResult>
        {
            new() { Name = "01-builds", Passed = true },
            new() { Name = "02-tests-pass", Passed = true },
            new() { Name = "03-tests-untouched", Passed = false, Reason = "WizardNavigationTests.cs was modified" }
        };

        string feedback = RetryPolicy.ForGuardrailFailures(Task("07-impl"), attempt: 2, results);

        Assert.Contains("Do NOT edit the test file", feedback);
        Assert.Contains("restored", feedback);            // tells the agent the file is pristine again
        Assert.Contains("needsHuman", feedback);           // the escape hatch when tests are wrong
        Assert.DoesNotContain("do not break these", feedback); // the misleading line is suppressed
    }

    [Fact]
    public void GuardrailFailure_IncludesFullOutput_NotJustFirstLine()
    {
        // Regression for issue #26 Gap 1: a build guardrail with 9 errors must surface ALL of
        // them in feedback, not only the first line (the one-line Reason).
        string nineErrors = string.Join('\n', new[]
        {
            "error CS5001: no Main method",
            "MainWindow.xaml.cs: error CS0103: 'InitializeComponent' missing",
            "PlaceholderStep.xaml.cs: error CS0103: 'InitializeComponent' missing",
            "ConnectionStep.xaml.cs: error CS0103: 'InitializeComponent' missing"
        });

        var results = new List<GuardrailResult>
        {
            new()
            {
                Name = "04-builds",
                Passed = false,
                Reason = "error CS5001: no Main method",
                Output = nineErrors
            }
        };

        string feedback = RetryPolicy.ForGuardrailFailures(Task("01-t"), attempt: 1, results);

        Assert.Contains("Reason: error CS5001: no Main method", feedback);
        Assert.Contains("Full output (tail)", feedback);
        Assert.Contains("InitializeComponent", feedback);     // the hidden errors are now visible
        Assert.Contains("ConnectionStep.xaml.cs", feedback);
    }

    [Fact]
    public void GuardrailFailure_OutputEqualToReason_DoesNotDuplicate()
    {
        var results = new List<GuardrailResult>
        {
            new() { Name = "02-tests", Passed = false, Reason = "1 test failed", Output = "1 test failed" }
        };

        string feedback = RetryPolicy.ForGuardrailFailures(Task("01-t"), attempt: 1, results);

        Assert.Contains("Reason: 1 test failed", feedback);
        Assert.DoesNotContain("Full output (tail)", feedback);
    }

    [Fact]
    public void InvalidFragment_ExplainsTheContractWithExample()
    {
        string feedback = RetryPolicy.ForInvalidFragment(Task("01-t"), attempt: 1, "fragment root is an array");

        Assert.Contains("fragment root is an array", feedback);
        Assert.Contains("GUARDRAILS_STATE_OUT", feedback);
        Assert.Contains("\"01-t\"", feedback); // example is namespaced under the task id
    }

    [Fact]
    public void ForeignKey_NamesEachOffendingKey_AndPointsToOwnNamespace()
    {
        // SSOT §6.2 single-writer-per-key (issue #48): the feedback must name the exact stray
        // top-level key(s) and tell the agent to nest under its own id, so a confused (non-malicious)
        // agent can drop the foreign key on retry.
        string feedback = RetryPolicy.ForForeignKey(Task("02-x"), attempt: 1, ["01-producer", "config"]);

        Assert.Contains("01-producer", feedback);
        Assert.Contains("config", feedback);
        Assert.Contains("\"02-x\"", feedback);          // example is namespaced under the task's own id
        Assert.Contains("Do NOT start over", feedback); // shared retry header
    }

    [Fact]
    public void ForeignKey_WhenFileWritesRolledBack_DisclosesTheRollback()
    {
        // issue #162: in worktree mode a state-rejected non-final attempt has its segment reset to
        // taskBase before the next attempt, so the attempt's FILE writes are reverted too. The
        // feedback must disclose this so the agent re-authors its files instead of fixing only the key
        // and then failing a file-exists guardrail against files it believes still exist.
        string feedback = RetryPolicy.ForForeignKey(
            Task("04-author-tests"), attempt: 1, ["j9hf6y"], fileWritesRolledBack: true);

        Assert.Contains("## File writes were also rolled back", feedback);
        Assert.Contains("re-author ALL files", feedback);
        Assert.Contains("do not assume", feedback);
        Assert.Contains("j9hf6y", feedback);            // the original key error is still present
    }

    [Fact]
    public void ForeignKey_WhenNoRollback_DoesNotClaimAFileRollback()
    {
        // Serial mode (file writes persist across attempts) and the final attempt (never reset) pass
        // fileWritesRolledBack:false — the feedback must NOT claim a rollback that did not happen.
        string feedback = RetryPolicy.ForForeignKey(Task("04-author-tests"), attempt: 1, ["j9hf6y"]);

        Assert.DoesNotContain("File writes were also rolled back", feedback);
        Assert.DoesNotContain("re-author ALL files", feedback);
        Assert.Contains("j9hf6y", feedback);            // the key error itself is unchanged
    }

    [Fact]
    public void InvalidFragment_WhenFileWritesRolledBack_DisclosesTheRollback()
    {
        // issue #162: the rollback disclosure attaches to EVERY state-rejection class, not only the
        // foreign-key one — an unparseable / non-object fragment is reset the same way in worktree mode.
        string feedback = RetryPolicy.ForInvalidFragment(
            Task("04-author-tests"), attempt: 2, "fragment is not valid JSON", fileWritesRolledBack: true);

        Assert.Contains("fragment is not valid JSON", feedback);     // the original reason is preserved
        Assert.Contains("## File writes were also rolled back", feedback);
        Assert.Contains("re-author ALL files", feedback);
    }

    [Fact]
    public void InvalidFragment_WhenNoRollback_DoesNotClaimAFileRollback()
    {
        string feedback = RetryPolicy.ForInvalidFragment(
            Task("04-author-tests"), attempt: 2, "fragment root is an array");

        Assert.DoesNotContain("File writes were also rolled back", feedback);
        Assert.Contains("fragment root is an array", feedback);
    }

    [Fact]
    public void LongOutput_IsTailTruncated()
    {
        string longError = string.Join('\n', Enumerable.Range(1, 500).Select(i => $"line {i}"));
        var action = new ProcessResult
        {
            ExitCode = 1,
            StandardOutput = "",
            StandardError = longError,
            TimedOut = false,
            Duration = TimeSpan.Zero
        };

        string feedback = RetryPolicy.ForActionFailure(Task("01-t"), 1, action);

        Assert.DoesNotContain("line 1\n", feedback);   // head dropped
        Assert.Contains("line 500", feedback);          // tail kept
    }

    [Fact]
    public void OutputCap_Feedback_IsActionable_AndTellsAgentToWriteIncrementally()
    {
        // #114: the retry must CHANGE behavior, not re-hit the same wall — so the feedback names the
        // cap and prescribes incremental edits / splitting, plus the needsHuman escape if too large.
        string feedback = RetryPolicy.ForOutputCapExceeded(Task("12-implement"), attempt: 2);

        Assert.Contains("output-token cap", feedback);
        Assert.Contains("INCREMENTAL", feedback);
        Assert.Contains("split", feedback);
        Assert.Contains("needsHuman", feedback);          // the escape when inherently too large
        Assert.Contains("Attempt 2", feedback);
    }

    [Fact]
    public void Timeout_Feedback_TellsAgentToContinueFromPartialWork_NotReExplore()
    {
        // #119 (serial mode, the default — file writes persist across attempts): the retry must
        // continue from the preserved partial work and prioritise compile/green, not re-read the whole
        // codebase (the wasteful "15 reads, 0 edits" retry the issue documents).
        string feedback = RetryPolicy.ForTimeout(Task("18-merge-engine"), attempt: 2);

        Assert.Contains("timed out", feedback);
        Assert.Contains("preserved", feedback);
        Assert.Contains("CONTINUE", feedback);
        Assert.Contains("do NOT start over", feedback, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("needsHuman", feedback);          // split-suggestion escape
        Assert.DoesNotContain("File writes were also rolled back", feedback); // no false rollback claim
    }

    [Fact]
    public void Timeout_Feedback_WhenFileWritesRolledBack_DisclosesReset_AndDropsPreservedClaim()
    {
        // #167: in worktree mode a non-final timed-out attempt has its segment reset to taskBase +
        // cleaned before the next attempt, so the partial work on disk is GONE. The feedback must NOT
        // claim it is "preserved on disk"; it discloses the reset and instructs re-authoring.
        string feedback = RetryPolicy.ForTimeout(Task("18-merge-engine"), attempt: 2, fileWritesRolledBack: true);

        Assert.Contains("timed out", feedback);
        Assert.Contains("## File writes were also rolled back", feedback);
        Assert.Contains("re-author ALL files", feedback);
        Assert.DoesNotContain("preserved in your workspace", feedback);  // the false claim is gone
        Assert.DoesNotContain("CONTINUE from the partial work already on disk", feedback);
        // The still-valid timeout advice survives: a larger clock, work efficiently, don't re-explore.
        Assert.Contains("re-read", feedback, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("needsHuman", feedback);
    }

    [Fact]
    public void MaxTurns_Feedback_TellsAgentToContinueFromPartialWork_NotReExplore()
    {
        // #129 / #94 (serial mode, the default): a max-turns termination is a budget exhaustion mid-
        // progress, not a logic error — the retry continues from the preserved partial work with a
        // raised turn budget, spending its turns on the deliverable, not re-exploration.
        string feedback = RetryPolicy.ForMaxTurnsExceeded(Task("12-implement"), attempt: 2);

        Assert.Contains("ran out of turns", feedback);
        Assert.Contains("CONTINUE from the partial work already on disk", feedback);
        Assert.Contains("do NOT start over", feedback, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("needsHuman", feedback);          // under-budget escape
        Assert.DoesNotContain("File writes were also rolled back", feedback); // no false rollback claim
    }

    [Fact]
    public void MaxTurns_Feedback_WhenFileWritesRolledBack_DisclosesReset_AndDropsPreservedClaim()
    {
        // #167: in worktree mode a non-final max-turns attempt has its segment reset to taskBase +
        // cleaned before the next attempt, so the partial work on disk is GONE. The feedback must NOT
        // tell the agent to continue from on-disk files; it discloses the reset and instructs
        // re-authoring — while keeping the still-valid "you have a larger turn budget; work directly"
        // advice (the turn-budget raise applies whether or not the files were rolled back).
        string feedback = RetryPolicy.ForMaxTurnsExceeded(Task("12-implement"), attempt: 2, fileWritesRolledBack: true);

        Assert.Contains("ran out of turns", feedback);
        Assert.Contains("## File writes were also rolled back", feedback);
        Assert.Contains("re-author ALL files", feedback);
        Assert.DoesNotContain("CONTINUE from the partial work already on disk", feedback); // false claim gone
        Assert.Contains("RAISED the turn budget", feedback);   // the still-valid budget advice survives
        Assert.Contains("needsHuman", feedback);
    }

    [Fact]
    public void TimeoutAndMaxTurns_FinalAttempt_DoNotClaimAFileRollback()
    {
        // Consistent with #162: the final attempt is never reset, so the executor passes
        // fileWritesRolledBack:false — the feedback must not claim a rollback that will not happen.
        string timeout = RetryPolicy.ForTimeout(Task("18-merge"), attempt: 3, fileWritesRolledBack: false);
        string maxTurns = RetryPolicy.ForMaxTurnsExceeded(Task("18-merge"), attempt: 3, fileWritesRolledBack: false);

        Assert.DoesNotContain("File writes were also rolled back", timeout);
        Assert.Contains("preserved", timeout);                 // serial/final keeps the existing guidance
        Assert.DoesNotContain("File writes were also rolled back", maxTurns);
        Assert.Contains("CONTINUE from the partial work already on disk", maxTurns);
    }

    // ── #195 retry salvage ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void MaxTurns_Feedback_WithSalvageRef_NamesRefAndDiffStat_AndSoftensPreservedClaim()
    {
        var salvage = new SalvageRef(
            "refs/guardrails/12-implement/attempt-2", " src/Foo.cs | 40 ++++++++++\n 1 file changed", Attempt: 2);

        string feedback = RetryPolicy.ForMaxTurnsExceeded(
            Task("12-implement"), attempt: 3, fileWritesRolledBack: true, salvageRef: salvage);

        Assert.Contains("## Prior attempt work is salvageable", feedback);
        Assert.Contains("refs/guardrails/12-implement/attempt-2", feedback);
        Assert.Contains("git checkout refs/guardrails/12-implement/attempt-2 -- <path>", feedback);
        Assert.Contains("src/Foo.cs | 40", feedback);
        Assert.Contains("writeScope", feedback);
        // The rollback disclosure still fires (files WERE rolled back from the working tree) but the
        // "do NOT continue from on-disk files" advice is softened to point at the salvage ref instead
        // of a flat "re-explore/re-derive from scratch" instruction.
        Assert.Contains("## File writes were also rolled back", feedback);
        Assert.Contains("NOT discarded", feedback);
    }

    [Fact]
    public void OutputCap_Feedback_WithSalvageRef_NamesRefAndDiffStat()
    {
        var salvage = new SalvageRef("refs/guardrails/07-write/attempt-1", " a.txt | 2 ++", Attempt: 1);

        string feedback = RetryPolicy.ForOutputCapExceeded(Task("07-write"), attempt: 2, salvageRef: salvage);

        Assert.Contains("## Prior attempt work is salvageable", feedback);
        Assert.Contains("refs/guardrails/07-write/attempt-1", feedback);
        Assert.Contains("a.txt | 2", feedback);
    }

    [Fact]
    public void MaxTurns_Feedback_WithoutSalvageRef_OmitsSalvageSection()
    {
        string feedback = RetryPolicy.ForMaxTurnsExceeded(Task("12-implement"), attempt: 2, fileWritesRolledBack: true);

        Assert.DoesNotContain("## Prior attempt work is salvageable", feedback);
        Assert.DoesNotContain("git checkout", feedback);
    }

    [Fact]
    public void MaxTurns_Feedback_WithSalvageRef_ButEmptyDiffStat_OmitsDiffBlock_KeepsAdoptionSection()
    {
        var salvage = new SalvageRef("refs/guardrails/12-implement/attempt-2", "", Attempt: 2);

        string feedback = RetryPolicy.ForMaxTurnsExceeded(
            Task("12-implement"), attempt: 3, fileWritesRolledBack: true, salvageRef: salvage);

        Assert.Contains("## Prior attempt work is salvageable", feedback);
        Assert.DoesNotContain("```", feedback); // no diff-stat code block when the stat is empty
    }

    // ── #253 write-scope diagnostic: status letter + forensic preview ─────────────────────────────

    [Fact]
    public void WriteScopeViolation_NamesEachPath_WithItsGitStatusLetter()
    {
        var offenses = new List<WriteScopeOffense>
        {
            new() { Path = "outside.txt", Status = 'A' },
            new() { Path = "config/settings.json", Status = 'M' },
            new() { Path = "old/Gone.cs", Status = 'D' }
        };

        string feedback = RetryPolicy.ForWriteScopeViolation(Task("04-implement"), attempt: 2, offenses);

        Assert.Contains("`outside.txt` (A: new/untracked", feedback);
        Assert.Contains("`config/settings.json` (M: modified", feedback);
        Assert.Contains("`old/Gone.cs` (D: deleted", feedback);
    }

    [Fact]
    public void WriteScopeViolation_NewFileWithPreview_IncludesSizeAndContentSnippet()
    {
        // Issue #253: a brand-new/untracked out-of-scope file is the suspicious case (no history at
        // taskBase) — its captured preview must reach the feedback since the file itself is already
        // gone (ScopedRevert deleted it) by the time anyone reads this text.
        var offense = new WriteScopeOffense
        {
            Path = "outside.txt",
            Status = 'A',
            Preview = new WriteScopeOffensePreview { SizeBytes = 42, TextPreview = "out of scope cruft" }
        };

        string feedback = RetryPolicy.ForWriteScopeViolation(Task("04-implement"), attempt: 1, [offense]);

        Assert.Contains("42 byte(s) before revert", feedback);
        Assert.Contains("out of scope cruft", feedback);
    }

    [Fact]
    public void WriteScopeViolation_ModifiedFile_NoPreviewSection()
    {
        // M/D offenses never carry a Preview (the taskBase blob is separately recoverable) — the
        // feedback must not fabricate a "byte(s) before revert" section for one.
        var offense = new WriteScopeOffense { Path = "config/settings.json", Status = 'M' };

        string feedback = RetryPolicy.ForWriteScopeViolation(Task("04-implement"), attempt: 1, [offense]);

        Assert.DoesNotContain("byte(s) before revert", feedback);
    }
}
