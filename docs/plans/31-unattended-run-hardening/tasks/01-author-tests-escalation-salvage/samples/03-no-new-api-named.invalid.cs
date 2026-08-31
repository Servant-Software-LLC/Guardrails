// Sample: the ONE defect 03-no-new-api-named.ps1 exists to catch -> the guardrail must exit non-zero.
//
// It is the .valid.cs sample with a single change: the third test now CONSTRUCTS a PriorAttemptRef
// carrying SalvagePatchPath and SalvageRefName, and switches on SalvageFraming.PriorAttempt. Those are
// stage 3's and stage 2's deliverables, so this file cannot compile today - and, more to the point, a
// pin written this way stops being a pin on an observable artifact and becomes a pin on an API that
// does not exist yet, which is what plan 31 section 7 forbids and what makes stages 2 and 3 test-free.
//
// Note the comments and string literals from the valid sample are still here unchanged. That is
// deliberate: it proves the guardrail fires on the USES added below and not on the mentions that were
// already passing.
using System;
using System.IO;
using Xunit;

namespace Guardrails.Core.Tests.Execution;

public sealed class EscalationSalvageTests
{
    // Deliberately does NOT construct a PriorAttemptRef carrying SalvagePatchPath or SalvageRefName -
    // those are stage 3's deliverables and would not compile today. We lay down the log directory and
    // let DependencyContextBuilder.BuildPriorAttempts fill them, exactly as production does.
    [Fact]
    public void PriorAttemptWithPatch_ComposedPromptCarriesSizeRoutedRecoveryChoice()
    {
        string logDir = Path.Combine(Path.GetTempPath(), "gr-sample", "attempt-1");
        Directory.CreateDirectory(logDir);
        File.WriteAllText(Path.Combine(logDir, "prior-attempt.patch"), "diff --git a/x b/x\n");

        string composed = Compose(logDir);

        Assert.Contains("prior-attempt.patch", composed);
        Assert.Contains("git show", composed);
    }

    [Fact]
    public void PriorAttemptWithPatch_ComposedPromptNamesTheDerivedSalvageRef()
    {
        // THE DEFECT: three banned identifiers used as code rather than named in prose.
        var prior = new PriorAttemptRef
        {
            Attempt = 1,
            Outcome = "needs-human",
            LogDir = "/plan/state/logs/01/attempt-1",
            SalvagePatchPath = "/plan/state/logs/01/attempt-1/prior-attempt.patch",
            SalvageRefName = "refs/guardrails/01/attempt-1"
        };

        string composed = Render(prior, SalvageFraming.PriorAttempt);

        Assert.Contains("refs/guardrails/", composed);
    }

    [Fact]
    public void PriorAttemptWithoutPatch_ComposedPromptCarriesNoRecoveryBlock()
    {
        string logDir = Path.Combine(Path.GetTempPath(), "gr-sample", "attempt-2");
        Directory.CreateDirectory(logDir);

        string composed = Compose(logDir);

        Assert.DoesNotContain("Prior attempt work is salvageable", composed);
    }

    private static string Compose(string logDir) => throw new NotSupportedException("sample only");

    private static string Render(PriorAttemptRef prior, SalvageFraming framing) =>
        throw new NotSupportedException("sample only");
}
