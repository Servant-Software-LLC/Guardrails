using System.CommandLine;
using Guardrails.Cli;
using Guardrails.Cli.Commands;
using Guardrails.Core.Loading;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Check-set provenance through the REAL <c>validate</c> command (issue #564, SSOT §16). The Core
/// tests pin the comparison; these pin the two things only the CLI can prove — that the check set is
/// printed on <b>every</b> run, and that GR2072 <b>never</b> moves an exit code.
///
/// <para>Each assertion is two-sided: the warning fires when the tree declares a check this binary
/// lacks, and is <b>absent</b> when the two agree. Both directions matter — a warning that always
/// fires is the muting failure (#229), and one that never fires is the original defect.</para>
///
/// <para>The stale-binary case is constructed rather than mocked: a temp directory shaped like a
/// Guardrails checkout, whose <c>DiagnosticCodes.cs</c> declares every code this assembly carries
/// <i>plus</i> one it cannot. That is the measured #564 scenario (an installed <c>1.12.0</c> against
/// a tree that had merged GR2068/GR2069) with the roles played by fixtures.</para>
/// </summary>
public sealed class CheckSetCliTests
{
    private static async Task<(int ExitCode, string Output)> ValidateAsync(string folder)
    {
        var io = new StringConsoleIo();
        var root = new RootCommand("test root");
        root.Add(ValidateCommand.Create(io));
        int exit = await root.Parse(["validate", folder]).InvokeAsync();
        return (exit, io.OutText);
    }

    /// <summary>
    /// The always-correct half of the fix: before #564 a <c>validate</c> run said nothing at all
    /// about which checks produced its verdict, so a green could not be told apart from a
    /// green-because-blind.
    /// <para>Note what this test can NOT reach. The probe falls back to the working directory, and a
    /// test process always runs inside this repository, so the <c>NotCompared</c> branch — the one
    /// every ordinary user of the released tool sees — is unreachable here without mutating the
    /// process-global working directory, which other tests assert on. Its wording is pinned in
    /// <c>CheckSetProbeTests</c> instead.</para>
    /// </summary>
    [Fact]
    public async Task Validate_AlwaysPrintsTheCheckSetLine_EvenOnAnOrdinaryCleanPlan()
    {
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (int exit, string output) = await ValidateAsync(plan.PlanDir);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("OK: plan is valid.", output, StringComparison.Ordinal);
        Assert.Contains("Check set: guardrails ", output, StringComparison.Ordinal);
        Assert.Contains("diagnostic codes", output, StringComparison.Ordinal);
        Assert.Contains($"highest {CheckSetProbe.ImplementedCodes[^1]}", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_WhenTheTreeDeclaresACheckThisBinaryLacks_WarnsGR2072_AndStillExitsZero()
    {
        using var checkout = new FakeCheckout(includeAllImplemented: true, extraCodes: ["GR9998", "GR9999"]);
        using var plan = new ScriptPlanBuilder(checkout.Root).AddTask("01-first");

        (int exit, string output) = await ValidateAsync(plan.PlanDir);

        // WARN, NEVER BLOCK. Running an older tool against a newer tree is legitimate — a release
        // build, a CI pinned to a version, a contributor who has not updated. Refusing to validate
        // would break all three and would be a worse cure than the disease. The point is that a green
        // can be TRUSTED OR DISCOUNTED, not that it becomes an error.
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("OK: plan is valid.", output, StringComparison.Ordinal);

        Assert.Contains(
            $"WARNING {DiagnosticCodes.CheckSetPredatesSourceTree}", output, StringComparison.Ordinal); // GR2072
        Assert.Contains("GR9998", output, StringComparison.Ordinal);
        Assert.Contains("GR9999", output, StringComparison.Ordinal);
        Assert.Contains("did NOT run", output, StringComparison.Ordinal);
        Assert.Contains(checkout.Root, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_WhenBinaryAndTreeAgree_SaysSoAndDoesNotWarn()
    {
        using var checkout = new FakeCheckout(includeAllImplemented: true);
        using var plan = new ScriptPlanBuilder(checkout.Root).AddTask("01-first");

        (int exit, string output) = await ValidateAsync(plan.PlanDir);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("declares the same set", output, StringComparison.Ordinal);
        Assert.DoesNotContain("did NOT run", output, StringComparison.Ordinal);

        // Asserted on the RENDERED diagnostic, not the bare code: the code string also appears in the
        // summary line's "highest GR2072" — which is the point of that clause and must not be what
        // makes this negative control pass.
        Assert.DoesNotContain(
            $"WARNING {DiagnosticCodes.CheckSetPredatesSourceTree}", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_StaleBinary_DoesNotRescueAFailingPlan_NorInflateItsErrorCount()
    {
        using var checkout = new FakeCheckout(includeAllImplemented: true, extraCodes: ["GR9999"]);
        string missing = Path.Combine(checkout.Root, "no-such-plan");

        (int exit, string output) = await ValidateAsync(missing);

        // GR2072 is a WARNING, so it is invisible to the exit code and to the error tally in both
        // directions: a broken plan still fails, and it still fails for exactly its own reasons.
        Assert.Equal(ExitCodes.HarnessError, exit);
        Assert.Contains(DiagnosticCodes.MissingFile, output, StringComparison.Ordinal); // GR1001
        Assert.Contains(
            $"WARNING {DiagnosticCodes.CheckSetPredatesSourceTree}", output, StringComparison.Ordinal);
        Assert.Contains("FAILED: 1 error(s).", output, StringComparison.Ordinal);

        // The scope of a FAILING verdict is reported too — a run that stops early still says which
        // checks it had.
        Assert.Contains("Check set: guardrails ", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_PrintsTheCheckSetAboveTheVerdict_SoTheVerdictStaysTheLastLine()
    {
        // Callers tail this output. The scope of a result belongs with the result, but ahead of it —
        // moving the verdict off the last line would be a silent break for anyone parsing it.
        using var plan = new ScriptPlanBuilder().AddTask("01-first");

        (_, string output) = await ValidateAsync(plan.PlanDir);

        string[] lines = [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r'))];
        Assert.Equal("OK: plan is valid.", lines[^1]);
        Assert.StartsWith("Check set: guardrails ", lines[^2], StringComparison.Ordinal);
    }

    /// <summary>
    /// A throwaway directory that <see cref="CheckSetProbe"/> will recognise as a Guardrails
    /// checkout: it carries a <c>src/Guardrails.Core/Loading/DiagnosticCodes.cs</c> declaring a
    /// chosen set of codes. The plan folder is created INSIDE it, so the probe's upward walk finds
    /// this fixture rather than the real repository.
    /// </summary>
    private sealed class FakeCheckout : IDisposable
    {
        public FakeCheckout(bool includeAllImplemented, params string[] extraCodes)
        {
            Root = Path.Combine(Path.GetTempPath(), "guardrails-fake-checkout-" + Guid.NewGuid().ToString("N"));

            IEnumerable<string> codes = includeAllImplemented
                ? [.. CheckSetProbe.ImplementedCodes, .. extraCodes]
                : extraCodes;

            string codesPath = CheckSetProbe.CodesPath(Root);
            Directory.CreateDirectory(Path.GetDirectoryName(codesPath)!);
            File.WriteAllLines(
                codesPath,
                ["public static class DiagnosticCodes", "{",
                 .. codes.Select(c => $"    public const string Name{c} = \"{c}\";"),
                 "}"]);
        }

        public string Root { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort — a leaked temp dir must never fail a test.
            }
        }
    }
}
