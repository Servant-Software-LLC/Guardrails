// Sample: a CORRECT shape for 03-no-new-api-named.ps1 -> the guardrail must exit 0.
//
// To run the pair, stage BOTH sample files into a scratch tree at the two paths the guardrail scans
// and point GUARDRAILS_WORKSPACE at it:
//   tests/Guardrails.Core.Tests/Execution/EscalationSalvageTests.cs
//   tests/Guardrails.Integration.Tests/EscalationSalvageTests.cs
//
// What makes this sample VALID is not that it avoids the words - it is that it avoids the USES. The
// four banned identifiers appear below in comments and in string literals on purpose: the guardrail
// strips comments and string literals before scanning, so a test that EXPLAINS why it does not name
// SalvageFraming, and a test that asserts on a composed string mentioning SalvagePatchPath, must both
// pass. A ban that red-flagged those would be anchored on a mention rather than a use (#470/#76), and
// would red a correct implementation for documenting itself.
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
        string composed = Compose(Path.Combine(Path.GetTempPath(), "gr-sample", "attempt-1"));

        // The literal below names the derived ref shape. It is a STRING, not a member access - the
        // guardrail strips literals, so this is not a use of any banned identifier.
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
}
