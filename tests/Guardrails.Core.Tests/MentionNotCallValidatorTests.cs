using Guardrails.Core.Loading;

namespace Guardrails.Core.Tests;

/// <summary>
/// GR2074 (issue #521 gap 1) — a required-present clause anchoring on a dotted MENTION while its own
/// <c>catches:</c> line claims to prove a CALL.
///
/// <para><b>This is not a missing rule.</b> The mention-vs-use doctrine is precise and lives in two places:
/// <c>guardrails-review</c> Probe B operator 9 ("reference the type via <c>nameof</c> in a dead field | dies
/// against a DOTTED CALL") and #76 in <c>stacks/dotnet.md</c>, which spells out the trailing paren. The
/// guardrail that got gamed was authored by a specialist agent <b>with that doctrine loaded</b>, and it wrote
/// <c>InitialBreakdownInvoker\.PrepareInvocation</c> — dotted, no <c>\s*\(</c>.</para>
///
/// <para><b>The word carrying the whole rule is "call"; the word that gets remembered is "dotted."</b> A
/// clause can satisfy the reader's memory of a rule while satisfying none of its teeth — which is why this
/// needed a gate and not a third copy of the prose.</para>
///
/// <para>Measured: a mutant whose only references were inside <c>nameof(...)</c> — zero invocations — exited
/// <b>0</b>, while the committed valid sample still passed. The check was not broadly broken; it was
/// toothless in the one direction it existed for.</para>
/// </summary>
public sealed class MentionNotCallValidatorTests
{
    [Fact]
    public void ADottedMentionUnderACallClaim_Warns()
    {
        // The measured shape, verbatim in structure: catches: says "drives the real seam", the clause
        // requires a dotted reference, and nothing anchors an invocation.
        string planDir = BuildPlan(
            catchesLine: "# catches: a test that names the seam but never CALLS it — the wiring is unproven",
            clause: @"if ($body -notmatch 'InitialBreakdownInvoker\.PrepareInvocation') { exit 1 }");
        try
        {
            Diagnostic warning = Assert.Single(
                Validate(planDir), d => d.Code == DiagnosticCodes.ClauseProvesMentionNotCall);

            Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);

            // The message has to carry the FIX, because the author already believed they had applied the
            // rule — restating "anchor on a use" would land as the thing they thought they did.
            Assert.Contains("nameof", warning.Message, StringComparison.Ordinal);
            Assert.Contains("trailing paren", warning.Message, StringComparison.Ordinal);
        }
        finally { Cleanup(planDir); }
    }

    [Fact]
    public void TheSameClauseWITHTheTrailingParen_IsSilent()
    {
        // The positive control, and the one that decides whether the lint is usable: the doctrine's own
        // prescribed form must not be flagged, or every correctly-written guardrail warns.
        string planDir = BuildPlan(
            catchesLine: "# catches: a test that names the seam but never CALLS it — the wiring is unproven",
            clause: @"if ($body -notmatch 'InitialBreakdownInvoker\.PrepareInvocation\s*\(') { exit 1 }");
        try
        {
            Assert.DoesNotContain(Validate(planDir), d => d.Code == DiagnosticCodes.ClauseProvesMentionNotCall);
        }
        finally { Cleanup(planDir); }
    }

    [Fact]
    public void ADottedMentionWithNoCallCLAIM_IsSilent()
    {
        // The conservatism that makes this shippable. A clause legitimately asserting a DECLARATION or a
        // type reference is correct as written, and there is no way to tell it from a weak call-check
        // except by reading what the guardrail says it is for. So the lint reads that, and stays quiet
        // when intent cannot be established — silence is the right failure direction here.
        string planDir = BuildPlan(
            catchesLine: "# catches: the type is not DECLARED in the expected namespace",
            clause: @"if ($body -notmatch 'Guardrails\.Core') { exit 1 }");
        try
        {
            Assert.DoesNotContain(Validate(planDir), d => d.Code == DiagnosticCodes.ClauseProvesMentionNotCall);
        }
        finally { Cleanup(planDir); }
    }

    [Fact]
    public void AFORBIDDINGClauseIsNeverFlagged_becauseThisRuleIsAboutWhatAClauseREQUIRES()
    {
        // `-match` means the presence FAILS the guardrail: a prohibition. "Anchored on a mention" is not a
        // weakness there — it is a BROADER ban, which is the safe direction. Flagging it would invert the
        // rule's meaning.
        string planDir = BuildPlan(
            catchesLine: "# catches: the test calls the deprecated entry point",
            clause: @"if ($body -match 'LegacyRunner\.Execute') { exit 1 }");
        try
        {
            Assert.DoesNotContain(Validate(planDir), d => d.Code == DiagnosticCodes.ClauseProvesMentionNotCall);
        }
        finally { Cleanup(planDir); }
    }

    [Fact]
    public void AClaimWordInsideALongerWord_DoesNotTripIt()
    {
        // "recall" contains "call" and "wireframe" contains "wire". A lint that fired on a substring would
        // be noise, and noise is how a warning stops being read.
        string planDir = BuildPlan(
            catchesLine: "# catches: the wireframe doc does not recall the earlier decision",
            clause: @"if ($body -notmatch 'Design\.Decision') { exit 1 }");
        try
        {
            Assert.DoesNotContain(Validate(planDir), d => d.Code == DiagnosticCodes.ClauseProvesMentionNotCall);
        }
        finally { Cleanup(planDir); }
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static IReadOnlyList<Diagnostic> Validate(string planDir) =>
        new PlanValidator(FakeExecutableProbe.All).Validate(new PlanLoader().Load(planDir).Plan!);

    private static string BuildPlan(string catchesLine, string clause)
    {
        string planDir = Path.Combine(Path.GetTempPath(), "gr2074-" + Guid.NewGuid().ToString("N"));
        string taskDir = Path.Combine(planDir, "tasks", "01-wire-it");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));

        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), "{\n  \"version\": 1\n}\n");
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            "{\n  \"description\": \"Wire it\",\n  \"dependsOn\": [],\n  \"writeScope\": [\"src/App.cs\"]\n}\n");
        File.WriteAllText(Path.Combine(taskDir, "action.sh"), "#!/usr/bin/env bash\necho ran\nexit 0\n");
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-seam.ps1"),
            catchesLine + "\n$body = Get-Content -Raw 'src/App.cs'\n" + clause + "\nexit 0\n");

        return planDir;
    }

    private static void Cleanup(string planDir)
    {
        try { Directory.Delete(planDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
