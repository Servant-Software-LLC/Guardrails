using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Core.Tests;

/// <summary>
/// GR2058 (issue #487) — the GR2037 banned-pattern scan must DEGRADE when a registry entry's matcher hits
/// its bounded match timeout, not crash.
///
/// <para><c>BannedPattern.Matcher</c> carries a 2-second timeout whose doc comment promised "a pathological
/// registry regex cannot hang the scan". Unhandled at the call site, that promise was half kept: the timeout
/// converted a hang into an unhandled <see cref="System.Text.RegularExpressions.RegexMatchTimeoutException"/>
/// that propagated out of <c>Validate</c> and took down every unrelated check with it, surfacing as a stack
/// trace rather than a diagnostic. <c>validate</c> is read-only, fast, and run in CI; a timeout says the scan
/// could not reach a verdict, not that the plan is invalid.</para>
///
/// <para>Not reachable by any realistic guardrail — the registry's costliest entry is strictly linear and
/// would need thousands of candidate sites in one script to reach the ceiling — so these tests build a
/// deliberately catastrophic synthetic entry. This is a robustness fix and must never be cited to justify
/// weakening a shipped registry entry.</para>
/// </summary>
public sealed class BannedPatternScanTimeoutTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("gr2058-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort temp cleanup */ }
    }

    /// <summary>
    /// A catastrophically backtracking entry times out against one guardrail: validation RETURNS, the pair is
    /// reported as skipped (WARNING, not error), and the NEXT registry entry is still scanned — so one
    /// pathological entry costs exactly one (guardrail, entry) verdict and nothing else.
    /// </summary>
    [Fact]
    public void MatcherTimeout_DegradesToAWarning_AndTheRestOfTheScanContinues()
    {
        BannedPatternRegistry registry = new(
        [
            new BannedPattern
            {
                Id = "#synthetic-catastrophic",
                // (a+)+$ over a long run of 'a' that cannot reach the anchor: exponential backtracking.
                BadPattern = @"(a+)+\$",
                Reason = "Synthetic entry for the timeout path.",
                GoodPatternHint = "Never ship this.",
            },
            new BannedPattern
            {
                Id = "#synthetic-linear",
                BadPattern = "sentinel-token",
                Reason = "Synthetic linear entry proving the scan continues.",
                GoodPatternHint = "Use something else.",
            },
        ]);

        GuardrailDefinition guardrail = WriteScript("01-pathological",
            new string('a', 44) + "!\nsentinel-token\nexit 0\n");

        IReadOnlyList<Diagnostic> diagnostics =
            new PlanValidator(FakeExecutableProbe.All, registry).Validate(PlanWith(guardrail));

        Diagnostic timedOut = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.BannedPatternScanTimedOut);
        Assert.Equal(DiagnosticSeverity.Warning, timedOut.Severity);
        Assert.Equal(guardrail.Path, timedOut.Path);
        Assert.Contains("#synthetic-catastrophic", timedOut.Message, StringComparison.Ordinal);
        Assert.Contains("01-pathological", timedOut.Message, StringComparison.Ordinal);

        // The timed-out pair is SKIPPED, not condemned — no GR2037 is invented for it — and the sibling
        // entry still reaches its verdict, which is the whole point of degrading rather than throwing.
        Diagnostic banned = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.BannedGuardrailPattern);
        Assert.Contains("#synthetic-linear", banned.Message, StringComparison.Ordinal);
    }

    /// <summary>An entry that completes normally must not emit the degradation warning.</summary>
    [Fact]
    public void NormalScan_EmitsNoTimeoutWarning()
    {
        BannedPatternRegistry registry = new(
        [
            new BannedPattern
            {
                Id = "#synthetic-linear",
                BadPattern = "sentinel-token",
                Reason = "Synthetic linear entry.",
                GoodPatternHint = "Use something else.",
            },
        ]);

        GuardrailDefinition guardrail = WriteScript("01-ordinary", "sentinel-token\nexit 0\n");

        IReadOnlyList<Diagnostic> diagnostics =
            new PlanValidator(FakeExecutableProbe.All, registry).Validate(PlanWith(guardrail));

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.BannedPatternScanTimedOut);
        Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.BannedGuardrailPattern);
    }

    private GuardrailDefinition WriteScript(string name, string body)
    {
        string dir = Path.Combine(_tempRoot, "tasks", "01-a", "guardrails");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name + ".ps1");
        File.WriteAllText(path, body);
        return new GuardrailDefinition { Name = name, Path = path, Kind = ActionKind.Script };
    }

    private PlanDefinition PlanWith(GuardrailDefinition guardrail)
    {
        TaskNode task = new()
        {
            Id = "01-a",
            Directory = Path.Combine(_tempRoot, "tasks", "01-a"),
            Description = "task 01-a",
            Action = new ActionDefinition { Path = Path.Combine(_tempRoot, "tasks", "01-a", "action.ps1"), Kind = ActionKind.Script },
            Guardrails = [guardrail],
            Preflights = [],
        };

        return new PlanDefinition
        {
            PlanDirectory = _tempRoot,
            Workspace = _tempRoot,
            Config = new RunConfig { Version = 1, MaxParallelism = 1 },
            Tasks = [task],
            PlanPreflights = [],
            PlanGuardrails = [],
        };
    }
}
