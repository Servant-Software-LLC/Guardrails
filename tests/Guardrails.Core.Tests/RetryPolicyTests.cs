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
        // #119: the retry must continue from the preserved partial work and prioritise compile/green,
        // not re-read the whole codebase (the wasteful "15 reads, 0 edits" retry the issue documents).
        string feedback = RetryPolicy.ForTimeout(Task("18-merge-engine"), attempt: 2);

        Assert.Contains("timed out", feedback);
        Assert.Contains("preserved", feedback);
        Assert.Contains("CONTINUE", feedback);
        Assert.Contains("do NOT start over", feedback, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("needsHuman", feedback);          // split-suggestion escape
    }
}
