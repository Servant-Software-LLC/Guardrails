using Guardrails.Core.Execution;
using Guardrails.Core.Loading;

namespace Guardrails.Core.Tests;

/// <summary>
/// Issue #501 — the allow-list of errors a knowingly-INCOMPLETE wave prefix is excused from.
///
/// <para><b>What this pins, and what it does NOT.</b> It pins the POLICY — which codes are excused —
/// because that is the part most at risk of quiet widening later: the temptation when a future
/// truncation trips some other completeness lint will be to add it here, and one careless addition turns
/// the post-breakdown gate into a rubber stamp. It does NOT prove the end-to-end salvage, and no test in
/// this repo currently does.</para>
///
/// <para><b>The honest gap.</b> GR2028 fires only in WORKTREE mode (<c>maxParallelism &gt; 1</c>), and
/// every waved fixture in this suite is built serial precisely so validation does not require a
/// git-backed workspace. So the bug was found by a REAL run (2026-08-22, `model-tiering-stage-3` wave 2)
/// and its behavioural reproduction still needs a git-backed waved fixture that does not exist yet.
/// <see cref="SchedulerBreakdownDurabilityTests"/> covers the other half behaviourally — a prefix with a
/// malformed task is still reverted wholesale.</para>
/// </summary>
public sealed class BreakdownSalvageAllowListTests
{
    private static Diagnostic Error(string code) => new()
    {
        Code = code,
        Severity = DiagnosticSeverity.Error,
        Path = "plan",
        Message = code
    };

    [Fact]
    public void Gr2028_IsExcused_BecauseABreakdownAuthorsTheWaveExitGateLast()
    {
        // A breakdown authors task folders first and the wave gates last, so any truncation leaves a
        // parallel-topology wave with no exit gate. The error is a restatement of "the wave is unfinished",
        // which the manifest already told the harness — it must not also veto the prefix.
        Assert.True(Scheduler.UnsatisfiableWhileIncomplete(
            Error(DiagnosticCodes.PlanGuardrailsMissingIntegrationReRun)));
    }

    [Theory]
    [InlineData(DiagnosticCodes.MissingWriteScope)]      // the authored CONTENT is wrong
    [InlineData(DiagnosticCodes.InvalidTierValue)]       // ditto
    [InlineData(DiagnosticCodes.DuplicateStableId)]      // ditto
    [InlineData(DiagnosticCodes.CrossWaveDependency)]    // a real topology error, not an unfinished one
    public void ContentErrors_AreNOTExcused_SoAMalformedPrefixIsStillReverted(string code)
    {
        // The line this allow-list has to hold: "unsatisfiable because the wave is UNFINISHED" is excused;
        // "the part that WAS written is wrong" is not. Resuming onto a malformed prefix is worse than
        // re-authoring, because the next segment builds on top of it.
        Assert.False(Scheduler.UnsatisfiableWhileIncomplete(Error(code)));
    }

    [Fact]
    public void TheAllowListIsExactlyOneCode_SoWideningItIsADeliberateActWithAFailingTest()
    {
        // Every GR code the validator can emit, checked against the list. If someone adds a second code to
        // UnsatisfiableWhileIncomplete without revisiting this test, this fails and they have to argue for
        // it — which is the entire point of an allow-list over a category.
        string[] excused = typeof(DiagnosticCodes)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Where(c => Scheduler.UnsatisfiableWhileIncomplete(Error(c)))
            .ToArray();

        Assert.Equal([DiagnosticCodes.PlanGuardrailsMissingIntegrationReRun], excused);
    }
}
