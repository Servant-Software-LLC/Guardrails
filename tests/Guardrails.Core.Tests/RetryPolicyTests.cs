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
}
