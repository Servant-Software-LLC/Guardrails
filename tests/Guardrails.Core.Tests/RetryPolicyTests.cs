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
    public void GuardrailFailures_NameEachFailureWithReason_AndPerGuardrailVerdictLedger()
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
        // #306 per-guardrail verdict ledger: every guardrail marked pass/fail so the agent knows how
        // much already passes and can make a targeted fix, not a re-derive.
        Assert.Contains("## Prior attempt: guardrail verdicts", feedback);
        Assert.Contains("- ✅ 01-build", feedback);
        Assert.Contains("- ❌ 02-tests — 3 of 14 tests failed", feedback);
        Assert.Contains("- ❌ 03-lint — guardrail failed (no reason printed)", feedback);
        Assert.Contains("do not break them", feedback);
    }

    // ── #306 incremental retry: stash the failed guardrail attempt + per-guardrail verdicts ──────────

    [Fact]
    public void GuardrailFailures_WhenRolledBackWithSalvage_HeaderSaysSaved_AndOffersStash()
    {
        // #306: a guardrail-failed non-final worktree attempt is now STASHED (superseding #195's
        // exclusion of the guardrail path). The header must promise recovery (not a false "keep what
        // already works" while the tree was discarded), and the salvage section must expose the patch +
        // ref so the agent can pull all/some/none.
        var results = new List<GuardrailResult>
        {
            new() { Name = "01-imports-clean", Passed = true },
            new() { Name = "02-tests-fail-on-stubs", Passed = true },
            new() { Name = "03-covers-key-behaviors", Passed = false, Reason = "add a test referencing `ingress`" }
        };
        var salvage = new SalvageRef(
            "refs/guardrails/10-author-tests/attempt-1", " tests/foo.test.ts | 30 +++",
            Attempt: 1, PatchPath: "/plan/logs/run/10-author-tests/attempt-1/prior-attempt.patch");

        string feedback = RetryPolicy.ForGuardrailFailures(
            PromptTask("10-author-tests"), attempt: 2, results, fileWritesRolledBack: true, salvageRef: salvage);

        // Header is truthful: work was SAVED, recover it — NOT the bare "keep what already works" claim.
        Assert.Contains("was SAVED, not lost", feedback);
        Assert.DoesNotContain("keep what", feedback);
        // The stash is exposed directly as a readable patch AND a per-file ref (all/some/none).
        // #382 re-baseline: the patch is now handed over as something to READ (it needs no git at all —
        // the harness emits `--add-dir <planDirectory>` unconditionally) rather than as a `git apply`
        // invocation of a verb --allowedTools does not carry. What this assertion has always been for —
        // the patch FILE reaching the agent — is unchanged.
        Assert.Contains("## Prior attempt work is salvageable", feedback);
        Assert.Contains("`/plan/logs/run/10-author-tests/attempt-1/prior-attempt.patch`", feedback);
        Assert.Contains("git show \"refs/guardrails/10-author-tests/attempt-1:<path>\"", feedback);
        // The per-guardrail verdicts tell it exactly what already passes.
        Assert.Contains("- ✅ 01-imports-clean", feedback);
        Assert.Contains("- ✅ 02-tests-fail-on-stubs", feedback);
        Assert.Contains("- ❌ 03-covers-key-behaviors — add a test referencing `ingress`", feedback);
    }

    [Fact]
    public void SalvageSection_NeverRecommendsAStateMutatingGitCommandAsThePerFileRoute()
    {
        // Issue #374 (regression). The salvage section used to tell the agent to run
        // `git checkout "<ref>" -- <path>`, but the #252 allowedTools default grants READ-ONLY git
        // (log/diff/show/status) and DELIBERATELY withholds state-mutating git — plan-breakdown's own
        // guidance lists `restore`/`reset`/`checkout`/`push`/`commit`/`stash` as ungranted. So the harness
        // was recommending a command its own permission layer refuses; observed live, the agent gave up on
        // it and re-applied the changes by hand, burning turns.
        //
        // The per-file route must therefore stay inside the read-only set. This pins the DIRECTION (no
        // ungranted verb is offered as the way to recover a file), not merely today's wording — a future
        // edit that reintroduces `git checkout`/`git restore` as the recommended per-file command fails
        // here even if it phrases it differently. Widening the #252 allow-list is a maintainer policy call;
        // until it is made, the feedback must not presuppose it.
        //
        // Wave 4 (#382) rewrote the section around this test and left every assertion below intact, on
        // purpose: the new text warns about the write-side verbs by naming them BARE ("checkout, restore,
        // reset"), never as a `git <verb> …` invocation, so the direction survives the rewrite unweakened.
        // RetryPolicySalvageAdviceTests re-pins the same rule at INVOCATION granularity (a backticked span
        // carrying arguments), which is what makes the prose warning safe to keep.
        var salvage = new SalvageRef(
            "refs/guardrails/07-impl/attempt-1", " src/A.cs | 2 +-",
            Attempt: 1, PatchPath: "/plan/logs/run/07-impl/attempt-1/prior-attempt.patch");

        string feedback = RetryPolicy.ForActionFailure(
            PromptTask("07-impl"), attempt: 2,
            new ProcessResult
            {
                ExitCode = 1,
                StandardOutput = string.Empty,
                StandardError = "boom",
                TimedOut = false,
                Duration = TimeSpan.FromSeconds(1)
            },
            fileWritesRolledBack: true, salvageRef: salvage);

        Assert.Contains("## Prior attempt work is salvageable", feedback);

        // The recovery route actually offered is the allow-listed read-only one.
        Assert.Contains("git show \"refs/guardrails/07-impl/attempt-1:<path>\"", feedback);

        // No state-mutating git verb is handed over as a RUNNABLE command. The prose may still name the
        // write-side verbs to warn they are ungranted (that warning is the point — it stops the agent
        // burning turns discovering the refusal); what it must never contain is a copy-pasteable
        // `git <verb> …` invocation, which is exactly the shape an agent acts on.
        foreach (string ungranted in new[] { "git checkout", "git restore", "git reset", "git stash" })
        {
            Assert.DoesNotContain(ungranted, feedback);
        }

        // `git apply` may still be MENTIONED, but only hedged on the permission actually being granted —
        // it must never read as an unconditional instruction the way it did before #374.
        Assert.Contains("allowedTools", feedback);
        Assert.Contains("only if", feedback);
    }

    [Fact]
    public void GuardrailFailures_WhenRolledBackWithoutSalvage_HeaderIsHonestlyNotRecoverable()
    {
        // The #167 gap: on the guardrail-fail path, a worktree rollback with NO stash (salvage disabled)
        // must NOT claim "keep what already works" — it must honestly say the writes are gone.
        var results = new List<GuardrailResult>
        {
            new() { Name = "01-build", Passed = true },
            new() { Name = "02-tests", Passed = false, Reason = "1 failed" }
        };

        string feedback = RetryPolicy.ForGuardrailFailures(
            PromptTask("07-impl"), attempt: 2, results, fileWritesRolledBack: true, salvageRef: null);

        Assert.Contains("rolled back to a clean base and are NOT", feedback);
        Assert.Contains("Re-author from scratch", feedback);
        Assert.DoesNotContain("keep what", feedback);            // the false claim is gone
        Assert.DoesNotContain("## Prior attempt work is salvageable", feedback);
        // Verdicts still reach the agent even without a stash.
        Assert.Contains("- ✅ 01-build", feedback);
        Assert.Contains("- ❌ 02-tests — 1 failed", feedback);
    }

    [Theory]
    [InlineData("03-tests-untouched")]     // the doctrine archetype name
    [InlineData("03-test-files-pristine")] // WEAK-1 fragility: the bare "untouched" substring MISSED this
    public void GuardrailFailures_ProtectedArtifactCheck_NeverAdvertisesSalvage_EvenWhenPassedAStaleRef(string guardrailName)
    {
        // A protected-artifact (tests-untouched-class) failure means the agent gamed the check by editing
        // a protected file; salvaging would re-introduce the gamed edits. The feedback must never advertise
        // the stash even if a caller passes a stale ref (defense-in-depth; TaskExecutor also suppresses it
        // at creation). Both the doctrine name AND a synonym the old substring missed are covered.
        var results = new List<GuardrailResult>
        {
            new() { Name = "02-tests-pass", Passed = true },
            new() { Name = guardrailName, Passed = false, Reason = "Foo.test.ts modified" }
        };
        var salvage = new SalvageRef("refs/guardrails/07-impl/attempt-1", " x | 1 +", Attempt: 1, PatchPath: "/p.patch");

        string feedback = RetryPolicy.ForGuardrailFailures(
            PromptTask("07-impl"), attempt: 2, results, fileWritesRolledBack: true, salvageRef: salvage);

        Assert.Contains("Do NOT edit the test file", feedback);
        Assert.DoesNotContain("## Prior attempt work is salvageable", feedback);
        Assert.DoesNotContain("git apply", feedback);
        Assert.DoesNotContain("was SAVED, not lost", feedback);   // header not switched to salvage wording
        // WEAK-3: in worktree mode the reset discarded the WHOLE tree, so the header is honestly
        // rolled-back-and-lost (re-author) — NOT the false persisted "keep what already works".
        Assert.Contains("rolled back to a clean base and are NOT", feedback);
        Assert.DoesNotContain("keep what", feedback);
    }

    [Fact]
    public void ActionFailure_WhenRolledBackWithSalvage_OffersStash_AndHeaderSaysSaved()
    {
        var action = new ProcessResult
        {
            ExitCode = 1, StandardOutput = "", StandardError = "boom", TimedOut = false, Duration = TimeSpan.Zero
        };
        var salvage = new SalvageRef("refs/guardrails/05-x/attempt-1", " a.cs | 3 +", Attempt: 1, PatchPath: "/p.patch");

        string feedback = RetryPolicy.ForActionFailure(
            Task("05-x"), attempt: 2, action, fileWritesRolledBack: true, salvageRef: salvage);

        Assert.Contains("exited with code 1", feedback);
        Assert.Contains("was SAVED, not lost", feedback);
        Assert.Contains("## Prior attempt work is salvageable", feedback);
        Assert.Contains("`/p.patch`", feedback);                   // #382: the patch is READ, not applied
        Assert.DoesNotContain("Do NOT start over", feedback);      // the #167-gap wording is gone here too
    }

    [Fact]
    public void Timeout_WhenRolledBackWithSalvage_OffersStash_AndSoftensReAuthor()
    {
        // #306 extends salvage to the timeout path (which #195 left out): the reverted work is now
        // recoverable, so the feedback points at the stash instead of a flat "re-author the files".
        var salvage = new SalvageRef("refs/guardrails/18-merge/attempt-1", " m.cs | 9 +", Attempt: 1, PatchPath: "/p.patch");

        string feedback = RetryPolicy.ForTimeout(
            Task("18-merge"), attempt: 2, fileWritesRolledBack: true, salvageRef: salvage);

        Assert.Contains("timed out", feedback);
        Assert.Contains("NOT discarded", feedback);
        Assert.Contains("## Prior attempt work is salvageable", feedback);
        Assert.Contains("`/p.patch`", feedback);                        // #382: the patch is READ, not applied
        Assert.DoesNotContain("preserved in your workspace", feedback); // no false "on disk" claim
    }

    [Fact]
    public void SalvageSection_WithPatchPath_LeadsWithThePatchFile_ThenThePerFileBlobRoute()
    {
        // Wave 4 / #382 re-baseline of SalvageSection_WithPatchPath_OffersGitApply_ForPullAll.
        // RE-POINTED, NOT WEAKENED: both things it always proved still hold — the patch file reaches the
        // agent, and the per-file `git show` route is offered — but the patch is now the LEADING route
        // and is handed over as something to READ (it needs no git at all: the harness emits
        // `--add-dir <planDirectory>` unconditionally), never as a `git apply "<patch>"` invocation of a
        // verb --allowedTools does not carry. The ordering and no-invocation assertions added below make
        // this strictly stronger than the pair it replaces.
        var salvage = new SalvageRef(
            "refs/guardrails/12-implement/attempt-2", " src/Foo.cs | 40 ++++",
            Attempt: 2, PatchPath: "/plan/logs/run/12-implement/attempt-2/prior-attempt.patch");

        string feedback = RetryPolicy.ForMaxTurnsExceeded(
            Task("12-implement"), attempt: 3, fileWritesRolledBack: true, salvageRef: salvage);

        Assert.Contains("`/plan/logs/run/12-implement/attempt-2/prior-attempt.patch`", feedback);
        Assert.Contains("git show \"refs/guardrails/12-implement/attempt-2:<path>\"", feedback);
        Assert.True(
            feedback.IndexOf("prior-attempt.patch", StringComparison.Ordinal)
                < feedback.IndexOf("git show", StringComparison.Ordinal),
            "the patch route must be presented BEFORE the `git show` blob route — it is the cheaper of " +
            "the two and needs no git at all");
        Assert.DoesNotContain("git apply \"", feedback);   // never a copy-pasteable ungranted invocation
    }

    [Fact]
    public void WriteScopeViolation_WorktreeRollback_DropsFalsePreservedClaim_AndOffersStash()
    {
        // The in-scope changes are NOT preserved in worktree mode — the whole attempt resets — so the
        // #167-class "Your in-scope changes are preserved" claim must be replaced by the stash offer.
        var offenses = new List<WriteScopeOffense> { new() { Path = "outside.txt", Status = 'A' } };
        var salvage = new SalvageRef("refs/guardrails/04-impl/attempt-1", " src/a.cs | 2 +", Attempt: 1, PatchPath: "/p.patch");

        string feedback = RetryPolicy.ForWriteScopeViolation(
            PromptTask("04-impl"), attempt: 2, Violation(offenses, "src/**") with { InScopePaths = ["src/a.cs"] },
            fileWritesRolledBack: true, salvageRef: salvage);

        Assert.Contains("outside.txt", feedback);
        Assert.DoesNotContain("in-scope changes are preserved", feedback); // the false claim is gone
        Assert.Contains("reset to a", feedback);
        Assert.Contains("## Prior attempt work is salvageable", feedback);
    }

    [Fact]
    public void WriteScopeViolation_SerialMode_KeepsInScopePreservedClaim()
    {
        // Serial mode (no reset): only the out-of-scope paths were reverted; the in-scope work genuinely
        // persists, so the original accurate claim stays.
        var offenses = new List<WriteScopeOffense> { new() { Path = "outside.txt", Status = 'A' } };

        string feedback = RetryPolicy.ForWriteScopeViolation(
            PromptTask("04-impl"), attempt: 2, Violation(offenses, "src/**") with { InScopePaths = ["src/a.cs"] });

        Assert.Contains("in-scope changes are preserved", feedback);
        Assert.DoesNotContain("## Prior attempt work is salvageable", feedback);
    }

    [Fact]
    public void ForeignKey_WhenRolledBack_HeaderNoLongerContradictsTheReAuthorDisclosure()
    {
        // #167/#306 reconciliation: the fragment-rejection header used to say "keep what already works"
        // while its body said "re-author ALL files" — a self-contradiction. Rolled back ⇒ the header now
        // agrees (re-author), no false preserved-work claim.
        string feedback = RetryPolicy.ForForeignKey(
            Task("04-author-tests"), attempt: 1, ["j9hf6y"], fileWritesRolledBack: true);

        Assert.Contains("## File writes were also rolled back", feedback);
        Assert.Contains("re-author ALL files", feedback);
        Assert.DoesNotContain("keep what", feedback);           // header no longer contradicts the body
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

        string feedback = RetryPolicy.ForWriteScopeViolation(Task("10-gitignore"), attempt: 2, Violation(offenses, ".gitignore"));

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
        Assert.Contains("git show \"refs/guardrails/12-implement/attempt-2:<path>\"", feedback);
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

        string feedback = RetryPolicy.ForWriteScopeViolation(Task("04-implement"), attempt: 2, Violation(offenses, "src/**"));

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

        string feedback = RetryPolicy.ForWriteScopeViolation(Task("04-implement"), attempt: 1, Violation([offense], "src/**"));

        Assert.Contains("42 byte(s) before revert", feedback);
        Assert.Contains("out of scope cruft", feedback);
    }

    [Fact]
    public void WriteScopeViolation_ModifiedFile_NoPreviewSection()
    {
        // M/D offenses never carry a Preview (the taskBase blob is separately recoverable) — the
        // feedback must not fabricate a "byte(s) before revert" section for one.
        var offense = new WriteScopeOffense { Path = "config/settings.json", Status = 'M' };

        string feedback = RetryPolicy.ForWriteScopeViolation(Task("04-implement"), attempt: 1, Violation([offense], "src/**"));

        Assert.DoesNotContain("byte(s) before revert", feedback);
    }

    // ── #706 the violation feedback shows what the task MAY write, not only what it wrote ─────────

    [Fact]
    public void WriteScopeViolation_ListsTheEnforcedAllowedPaths_AfterTheOffendingOnes()
    {
        // #706: plan 40's task 20 was told "ensure you only write to paths covered by this task's writeScope"
        // on every retry and was never once shown those paths. The feedback must carry the ENFORCED list —
        // every entry, none invented — after the offending path, so a stray write reads differently from a
        // scope the plan got wrong.
        var offenses = new List<WriteScopeOffense>
        {
            new() { Path = "src/Guardrails.Core/Execution/OverwatchDecision.cs", Status = 'M' }
        };
        string[] scope = ["src/Guardrails.Core/Execution/Overwatch.cs", "tests/Guardrails.Core.Tests/Supply/**"];

        string feedback = RetryPolicy.ForWriteScopeViolation(
            PromptTask("20-implement"), attempt: 1, Violation(offenses, scope));

        int offending = feedback.IndexOf("`src/Guardrails.Core/Execution/OverwatchDecision.cs`", StringComparison.Ordinal);
        int allowedBlock = feedback.IndexOf(AllowedPathsLead, StringComparison.Ordinal);
        Assert.True(offending >= 0, "the offending path must still be named:\n" + feedback);
        Assert.True(allowedBlock > offending, "the allowed paths must be listed AFTER the offending path:\n" + feedback);
        Assert.Equal(scope, BulletPathsAfter(feedback, AllowedPathsLead));
    }

    [Fact]
    public void WriteScopeViolation_EmptyScope_SaysNothingIsAllowed_NotAnEmptyList()
    {
        // Control against a silently empty rendering: `writeScope: []` is a deliberate "writes nothing"
        // declaration (#389). A lead-in followed by zero bullets reads as a rendering bug, so the feedback
        // must SAY the scope is empty instead.
        var offenses = new List<WriteScopeOffense> { new() { Path = "outside.txt", Status = 'A' } };

        string feedback = RetryPolicy.ForWriteScopeViolation(PromptTask("04-impl"), attempt: 1, Violation(offenses));

        Assert.DoesNotContain(AllowedPathsLead, feedback);
        Assert.Contains("writeScope is EMPTY", feedback);
    }

    // ── #707 a scope gap the PLAN caused halts with the one-line fix ──────────────────────────────

    private const string DecisionPath = "src/Guardrails.Core/Execution/OverwatchDecision.cs";

    [Fact]
    public void WriteScopeGapHalt_RepeatedPath_NamesThePath_TheTaskJson_AndTheOneLineFix()
    {
        // #707 item 1: the same path written out of scope on a second attempt. The halt must hand the human
        // everything needed to decide in one read: the path, WHERE the fix goes, and the exact entry to add.
        var offenses = new List<WriteScopeOffense> { new() { Path = DecisionPath, Status = 'M' } };
        var gap = new WriteScopeGap(RepeatedPaths: [DecisionPath], UpstreamAuthorByPath: new Dictionary<string, string>());

        string feedback = RetryPolicy.ForWriteScopeGapHalt(
            PromptTask("20-implement"), attempt: 2, Violation(offenses, "src/Guardrails.Core/Execution/Overwatch.cs"), gap);

        Assert.Contains("## Write-scope violation", feedback);          // telemetry's marker survives on the halt
        Assert.Contains($"`{DecisionPath}`", feedback);
        Assert.Contains("/fake/tasks/20-implement/task.json", feedback);  // where the one-line fix goes
        Assert.Contains($"\"{DecisionPath}\"", feedback);                // the writeScope entry to add, as JSON
        Assert.Contains("earlier attempt", feedback);                    // why no retry can help
        Assert.Equal(["src/Guardrails.Core/Execution/Overwatch.cs"], BulletPathsAfter(feedback, AllowedPathsLead));
    }

    [Fact]
    public void WriteScopeGapHalt_UpstreamAuthoredPath_NamesTheUpstreamTask_AndClaimsNoRepeat()
    {
        // #707 item 2: halted on the FIRST attempt because an upstream task last committed the path. The text
        // must name that task, and must not describe a repeat that never happened.
        var offenses = new List<WriteScopeOffense> { new() { Path = DecisionPath, Status = 'M' } };
        var gap = new WriteScopeGap(
            RepeatedPaths: [],
            UpstreamAuthorByPath: new Dictionary<string, string> { [DecisionPath] = "19-author-tests-overwatcher-autoresolve" });

        string feedback = RetryPolicy.ForWriteScopeGapHalt(
            PromptTask("20-implement"), attempt: 1, Violation(offenses, "src/Guardrails.Core/Execution/Overwatch.cs"), gap);

        Assert.Contains("`19-author-tests-overwatcher-autoresolve`", feedback);
        Assert.Contains($"\"{DecisionPath}\"", feedback);
        Assert.DoesNotContain("earlier attempt", feedback);
    }

    [Fact]
    public void WriteScopeGapSummary_KeepsTheNeedsHumanPrefix_AndNamesThePathAndTheFix()
    {
        // The summary is what a live table, run.json and the escalation record show. It keeps the stable
        // `needs human: ` prefix every harness needs-human summary carries, and is complete on its own.
        var repeated = new WriteScopeGap(["src/Stub.cs"], new Dictionary<string, string>());
        var upstream = new WriteScopeGap([], new Dictionary<string, string> { ["src/Stub.cs"] = "01-author" });

        foreach (WriteScopeGap gap in new[] { repeated, upstream })
        {
            string summary = RetryPolicy.WriteScopeGapSummary(PromptTask("02-implement"), gap);
            Assert.StartsWith("needs human: ", summary);
            Assert.Contains("\"src/Stub.cs\"", summary);
            Assert.Contains("/fake/tasks/02-implement/task.json", summary);
        }

        Assert.Contains("01-author", RetryPolicy.WriteScopeGapSummary(PromptTask("02-implement"), upstream));
    }

    // ── #708 the permission-wall halt names a refused command as a command ───────────────────────

    [Fact]
    public void PermissionWall_RepeatedCommand_IsNamedACommand_NotAPath()
    {
        // Plan 40, task 20: the halt listed `echo "EXIT:$?` under "Repeatedly-refused path(s)", and plan 28's refused
        // read-only `grep` was summarized as "write repeatedly refused". Neither was a write, and neither was a path.
        const string selfCheck = "echo \"EXIT:$?\"";
        var wall = new PermissionWallDecision(true, [], [], [selfCheck]);

        string feedback = RetryPolicy.ForPermissionWall(PromptTask("20-implement"), wall);

        Assert.Contains("## Repeatedly-refused command(s)", feedback);
        Assert.Contains($"- `{selfCheck}`", feedback);
        Assert.Contains("allowedTools", feedback);
        Assert.DoesNotContain("path(s)", feedback);
        Assert.Equal(
            $"needs human: command repeatedly refused (permission wall) — {selfCheck}",
            RetryPolicy.PermissionWallSummary(wall));
    }

    [Fact]
    public void PermissionWall_RepeatedPathWithASpace_IsStillNamedAPath()
    {
        // #534 told a command from a path by the key's SHAPE: whitespace meant a command. This repository once lived at
        // `C:\Dev AI\Guardrails`, where that guess calls its own source files commands. The decision now says which is which.
        const string path = @"C:\Dev AI\Guardrails\src\Locked.cs";
        var wall = new PermissionWallDecision(true, [], [path], []);

        string feedback = RetryPolicy.ForPermissionWall(PromptTask("04-impl"), wall);

        Assert.Contains("REFUSED to write one or more paths", feedback);
        Assert.Contains("## Repeatedly-refused path(s)", feedback);
        Assert.DoesNotContain("tool calls", feedback);
        Assert.DoesNotContain("refused COMMAND", feedback);
        Assert.Equal($"needs human: write repeatedly refused (permission wall) — {path}", RetryPolicy.PermissionWallSummary(wall));
    }

    [Fact]
    public void RepeatedRefusalContext_NamesEachKind_AndIsEmptyWithoutARepeat()
    {
        // #708: a repeat no longer settles an attempt whose action succeeded. On a guardrail failure it rides along as
        // secondary context, so the next attempt stops reaching for a call that will be refused again.
        var wall = new PermissionWallDecision(true, [], ["src/locked/Protected.cs"], ["echo \"EXIT:$?\""]);

        string context = RetryPolicy.ForRepeatedRefusalContext(wall);

        Assert.Contains("## Secondary context", context);
        Assert.Contains("- path: `src/locked/Protected.cs`", context);
        Assert.Contains("- command: `echo \"EXIT:$?\"`", context);
        Assert.Contains("needsHuman", context);
        Assert.Empty(RetryPolicy.ForRepeatedRefusalContext(new PermissionWallDecision(true, [".claude/x.md"], [], [])));
    }

    [Fact]
    public void RepeatedPathWallHalt_LeadsWithTheCauseThatFired_ThenNamesThePath()
    {
        // #708 / #329: when a repeated in-scope write wall halts an attempt, the cause that genuinely fired leads,
        // and the wall follows in #86's own path wording.
        var wall = new PermissionWallDecision(true, [], ["src/locked/Protected.cs"], []);

        string feedback = RetryPolicy.ForRepeatedPathWallHalt(
            PromptTask("04-impl"), "A guardrail failed", "- **01-fail** — exit 1", wall, budgetRemained: true);

        int failure = feedback.IndexOf("## A guardrail failed", StringComparison.Ordinal);
        int wallSection = feedback.IndexOf("## Repeatedly-refused path(s)", StringComparison.Ordinal);
        Assert.True(failure >= 0 && wallSection > failure, "the cause that fired must lead, and the wall follow it");
        Assert.Contains("- **01-fail** — exit 1", feedback);
        Assert.Contains("- `src/locked/Protected.cs`", feedback);
        Assert.Contains("cover this path", feedback);
        Assert.Contains("the remaining retry budget was not burned", feedback);
    }

    [Fact]
    public void RepeatedPathWallHalt_OnAFinalAttempt_DoesNotClaimABudgetItNeverSaved()
    {
        // #708: this halt also fires at the four pre-guardrail rejection sites, where it can land on the LAST
        // budgeted attempt — and there was no budget left to save. Claiming otherwise credits the harness with
        // ending the task early when the budget ended it, which is the opposite of the diagnosis a human needs.
        var wall = new PermissionWallDecision(true, [], ["src/locked/Protected.cs"], []);

        string feedback = RetryPolicy.ForRepeatedPathWallHalt(
            PromptTask("04-impl"), "A write-scope violation", "- `docs/stray.md`", wall, budgetRemained: false);

        Assert.Contains("## A write-scope violation", feedback);
        Assert.Contains("This was the last budgeted attempt.", feedback);
        Assert.DoesNotContain("the remaining retry budget was not burned", feedback);
    }

    [Fact]
    public void RepeatedRefusalContext_OmitsWhatTheHaltAlreadyNamed()
    {
        // #708 review: at a halt the wall's own paths are already listed under `## Repeatedly-refused path(s)` and
        // its commands under `## Repeatedly-refused command(s)`. Listing either again under secondary context reads
        // as a second, different finding. The filter is at the RENDER — the caller still hands over the FULL
        // decision, so nothing is lost; only what has demonstrably been named already is dropped.
        var wall = new PermissionWallDecision(
            true, [], ["docs/blocked.md", "src/Sneaky.cs"], ["echo \"EXIT:$?\""]);

        string context = RetryPolicy.ForRepeatedRefusalContext(wall, ["docs/blocked.md", "echo \"EXIT:$?\""]);

        Assert.Contains("- path: `src/Sneaky.cs`", context);
        Assert.DoesNotContain("docs/blocked.md", context);
        Assert.DoesNotContain("EXIT:$?", context);
    }

    [Fact]
    public void RepeatedRefusalContext_IsOmittedEntirely_WhenTheHaltNamedEverything()
    {
        // An empty section is worse than no section: a heading with no bullets reads as a finding with its
        // evidence missing.
        var wall = new PermissionWallDecision(true, [], ["docs/blocked.md"], []);

        Assert.Empty(RetryPolicy.ForRepeatedRefusalContext(wall, ["docs/blocked.md"]));
    }

    [Fact]
    public void RepeatedPathWallHalt_WhenNothingWasPreserved_SaysSo_RatherThanStayingSilent()
    {
        // #708: the nested-control-key site stays unsalvaged (the documented fragment-rejection boundary), and a
        // halt performs no reset — so the tree is orphaned with nothing offered. Silence there reads as "there was
        // nothing to keep"; the agent and the human both need to be told the work was not preserved.
        var wall = new PermissionWallDecision(true, [], ["src/locked/Protected.cs"], []);

        string feedback = RetryPolicy.ForRepeatedPathWallHalt(
            PromptTask("04-impl"), "The state fragment was rejected", "- nested", wall,
            budgetRemained: true, salvageRef: null, workNotPreserved: true);

        Assert.Contains("was NOT preserved", feedback);
        Assert.DoesNotContain("## Prior attempt work is salvageable", feedback);
    }

    [Fact]
    public void RepeatedPathWallHalt_AtAWriteScopeViolation_KeepsThe705Disclosures()
    {
        // #705: the revert destroys the out-of-scope bytes unless a copy is kept, and the copy is useless if
        // nothing points a human at it. The halt must disclose both, exactly as ForWriteScopeViolation does on
        // the retry path it replaces.
        var wall = new PermissionWallDecision(true, [], ["src/locked/Protected.cs"], []);

        string feedback = RetryPolicy.ForRepeatedPathWallHalt(
            PromptTask("04-impl"), "A write-scope violation", "- `docs/stray.md`", wall,
            budgetRemained: true, salvageRef: null, outOfScopePatchPath: "/logs/out-of-scope.patch");

        Assert.Contains("reverted", feedback);
        Assert.Contains("/logs/out-of-scope.patch", feedback);
    }

    [Fact]
    public void RepeatedPathWallHalt_OffersPreservedWork_AsOrphaned_NotAsRolledBack()
    {
        // #554 / #708: a halt performs NO reset — the loop returns before it — so the tree the attempt wrote in is
        // ORPHANED, not rolled back. The retry path's wording here would tell a human something false about the
        // state of that tree, which is the whole reason SalvageFraming exists.
        var wall = new PermissionWallDecision(true, [], ["src/locked/Protected.cs"], []);
        var salvage = new SalvageRef(
            "refs/guardrails/04-impl/attempt-2", " src/Impl.cs | 12 ++", Attempt: 2, PatchPath: "/p.patch");

        string feedback = RetryPolicy.ForRepeatedPathWallHalt(
            PromptTask("04-impl"), "A guardrail failed", "- **01-fail** — exit 1", wall, budgetRemained: true, salvage);

        Assert.Contains("## Prior attempt work is salvageable", feedback);
        Assert.Contains("ORPHANED", feedback);
        Assert.Contains("refs/guardrails/04-impl/attempt-2", feedback);
        Assert.DoesNotContain("rolled back to a clean base", feedback);
    }

    // ── #705 salvage says only what is true, and out-of-scope work is kept for a human ───────────

    [Fact]
    public void WriteScopeViolation_NoInScopeWork_NeverClaimsSaved_EvenWhenHandedASnapshot()
    {
        // #705: plan 40's task 20 put ALL of its work in a file outside its writeScope. The revert took that work,
        // the snapshot taken afterwards held only lock-file churn, and the feedback still opened with "that work
        // was SAVED, not lost" over a patch with nothing of the agent's in it. With no in-scope change there is
        // nothing in scope to save, whatever snapshot a caller hands over.
        var offenses = new List<WriteScopeOffense> { new() { Path = DecisionPath, Status = 'M' } };
        var churnOnly = new SalvageRef(
            "refs/guardrails/20-implement/attempt-1", " src/Guardrails.Cli/packages.lock.json | 58 +--",
            Attempt: 1, PatchPath: "/p.patch");

        string feedback = RetryPolicy.ForWriteScopeViolation(
            PromptTask("20-implement"), attempt: 1, Violation(offenses, "src/Guardrails.Core/Execution/Overwatch.cs"),
            fileWritesRolledBack: true, salvageRef: churnOnly);

        Assert.DoesNotContain("SAVED, not lost", feedback);
        Assert.DoesNotContain("## Prior attempt work is salvageable", feedback);
        Assert.Contains("no in-scope work", feedback);
    }

    [Fact]
    public void WriteScopeViolation_InScopeWorkBesideAStrayWrite_StillSaysSaved_AndOffersIt()
    {
        // CONTROL: the honest header must not become a blanket one. An attempt that changed files inside its scope
        // AND strayed outside it left in-scope work in the snapshot, and the retry is still pointed at it.
        var offenses = new List<WriteScopeOffense> { new() { Path = "docs/notes.md", Status = 'A' } };
        var salvage = new SalvageRef("refs/guardrails/04-impl/attempt-1", " src/a.cs | 2 +", Attempt: 1, PatchPath: "/p.patch");

        string feedback = RetryPolicy.ForWriteScopeViolation(
            PromptTask("04-impl"), attempt: 1, Violation(offenses, "src/**") with { InScopePaths = ["src/a.cs"] },
            fileWritesRolledBack: true, salvageRef: salvage);

        Assert.Contains("SAVED, not lost", feedback);
        Assert.Contains("## Prior attempt work is salvageable", feedback);
    }

    [Fact]
    public void WriteScopeViolation_NoInScopeWork_OnTheFinalAttempt_ClaimsNothingPreserved()
    {
        // The same fact under the other disposition: no reset follows a final attempt, but with nothing changed in
        // scope there is nothing preserved to keep.
        var offenses = new List<WriteScopeOffense> { new() { Path = "docs/notes.md", Status = 'A' } };

        string feedback = RetryPolicy.ForWriteScopeViolation(PromptTask("04-impl"), attempt: 3, Violation(offenses, "src/**"));

        Assert.DoesNotContain("in-scope changes are preserved", feedback);
        Assert.DoesNotContain("Do NOT start over", feedback);
    }

    [Fact]
    public void WriteScopeViolation_NamesTheOutOfScopeCopy_AsAHumansCopy_NotTheAgents()
    {
        // #705 item 1: the out-of-scope bytes are copied before the revert. The retry must never apply them —
        // re-applying fails the same check — so the feedback names the copy and says whose it is.
        var offenses = new List<WriteScopeOffense> { new() { Path = DecisionPath, Status = 'M' } };

        string feedback = RetryPolicy.ForWriteScopeViolation(
            PromptTask("20-implement"), attempt: 1, Violation(offenses, "src/**"), fileWritesRolledBack: true,
            outOfScopePatchPath: @"C:\logs\20-implement\attempt-1\out-of-scope.patch");

        Assert.Contains("`C:/logs/20-implement/attempt-1/out-of-scope.patch`", feedback);
        Assert.Contains("not for you", feedback);
    }

    [Fact]
    public void WriteScopeViolation_WithoutAnOutOfScopeCopy_NamesNone()
    {
        // CONTROL: the capture is best-effort, and a copy that was never written must not be announced.
        var offenses = new List<WriteScopeOffense> { new() { Path = DecisionPath, Status = 'M' } };

        string feedback = RetryPolicy.ForWriteScopeViolation(
            PromptTask("20-implement"), attempt: 1, Violation(offenses, "src/**"), fileWritesRolledBack: true);

        Assert.DoesNotContain("out-of-scope.patch", feedback);
    }

    [Fact]
    public void WriteScopeGapHalt_NamesTheKeptOutOfScopeCopy()
    {
        // The halt is where a human decides whether to widen the scope — the moment the kept work matters most.
        var offenses = new List<WriteScopeOffense> { new() { Path = DecisionPath, Status = 'M' } };
        var gap = new WriteScopeGap([], new Dictionary<string, string> { [DecisionPath] = "19-author" });

        string feedback = RetryPolicy.ForWriteScopeGapHalt(
            PromptTask("20-implement"), attempt: 1, Violation(offenses, "src/**"), gap,
            outOfScopePatchPath: "/logs/20-implement/attempt-1/out-of-scope.patch");

        Assert.Contains("`/logs/20-implement/attempt-1/out-of-scope.patch`", feedback);
    }

    /// <summary>The lead-in line the allowed-path list follows (#706).</summary>
    private const string AllowedPathsLead = "This task's writeScope allows changes ONLY to:";

    /// <summary>The backticked paths of the bullet run that immediately follows <paramref name="lead"/>.</summary>
    private static List<string> BulletPathsAfter(string text, string lead)
    {
        string after = text[(text.IndexOf(lead, StringComparison.Ordinal) + lead.Length)..];
        return after.Replace("\r\n", "\n").Split('\n')
            .SkipWhile(line => line.Length == 0)
            .TakeWhile(line => line.StartsWith("- `", StringComparison.Ordinal))
            .Select(line => line[3..line.IndexOf('`', 3)])
            .ToList();
    }

    /// <summary>A failed <see cref="WriteScopeCheckResult"/> for <paramref name="offenses"/> under <paramref name="scope"/>.</summary>
    private static WriteScopeCheckResult Violation(IReadOnlyList<WriteScopeOffense> offenses, params string[] scope) =>
        new() { Passed = false, Scope = scope, OffendingPaths = offenses, InScopePaths = [] };
}
