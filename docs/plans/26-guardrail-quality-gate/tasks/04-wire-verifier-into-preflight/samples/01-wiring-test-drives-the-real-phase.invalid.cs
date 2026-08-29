using Guardrails.Cli;
using Guardrails.Core.Execution;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Samples;

namespace Guardrails.Integration.Tests.Samples;

/// <summary>
/// THE ONE DEFECT THIS SAMPLE CARRIES: the test injects the seam it claims to verify. It never CALLS
/// <see cref="PlanPreflightPhase.EvaluateAsync"/> — the only reference is inside a <c>nameof(...)</c>,
/// which is valid C#, is not a string literal, and therefore survives a comment/string strip — and it
/// runs <see cref="SampleVerifier"/> itself, asserting on ITS findings. Every assertion below passes
/// against a <see cref="PlanPreflightPhase"/> that was never changed at all, so a reversed sample pair
/// still costs a full run's tokens while the suite reports success (#120).
///
/// Two visible symptoms, one failure: the test does not drive the production path. Both are what
/// issue #521 measured on 2026-08-28 — a composition-root clause that stopped at the dotted NAME was
/// satisfied by a hollow test with two dead nameof references and ZERO invocations, exit 0.
/// </summary>
[Trait("Category", "BacklogSlate")]
public sealed class SampleVerifierWiringTests : IDisposable
{
    private readonly string _root;

    public SampleVerifierWiringTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr510-wiring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task EvaluateAsync_ReturnsFalse_WhenACommittedSamplePairIsReversed()
    {
        string planDir = CreatePlan("reversed", soundPair: false);
        PlanDefinition plan = LoadPlan(planDir);

        // The #521 operator, in the open: a bare nameof is NOT a string literal, so it survives a
        // comment/string strip and satisfies any clause that stops at the dotted NAME — while the
        // method is never invoked. Measured 2026-08-28: exit 0, zero invocations.
        string entryPoint = nameof(PlanPreflightPhase.EvaluateAsync);
        Assert.NotEmpty(entryPoint);

        // The seam, injected: this file runs the verifier, so nothing here observes whether
        // PlanPreflightPhase.EvaluateAsync was taught to run it.
        SampleVerifyResult result = await SampleVerifier.VerifyAsync(
            plan, new ProcessRunner(), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task EvaluateAsync_HaltsOnABadSamplePair_EvenWhenThePlanDeclaresNoPreflightsFolder()
    {
        string planDir = CreatePlan("no-preflights", soundPair: false);
        PlanDefinition plan = LoadPlan(planDir);
        Assert.Empty(plan.PlanPreflights);

        // Named for the phase; bound to the verifier. The placement trap this test exists to pin —
        // whether the step sits before or after the `PlanPreflights.Count == 0` early return — is
        // invisible from here, because the phase is never invoked.
        SampleVerifyResult result = await SampleVerifier.VerifyAsync(
            plan, new ProcessRunner(), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(result.Passed, $"expected {nameof(PlanPreflightPhase.EvaluateAsync)} to halt this plan");
    }

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task EvaluateAsync_ReturnsTrue_WhenEverySamplePairIsSound()
    {
        string planDir = CreatePlan("sound", soundPair: true);
        PlanDefinition plan = LoadPlan(planDir);

        SampleVerifyResult result = await SampleVerifier.VerifyAsync(
            plan, new ProcessRunner(), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.True(result.Passed);
    }

    private static PlanDefinition LoadPlan(string planDir)
    {
        PlanLoadResult loaded = new PlanLoader().Load(planDir);
        Assert.NotNull(loaded.Plan);
        return loaded.Plan!;
    }

    private string CreatePlan(string name, bool soundPair)
    {
        string planDir = Path.Combine(_root, name);
        string taskDir = Path.Combine(planDir, "tasks", "01-only");
        Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
        Directory.CreateDirectory(Path.Combine(taskDir, "samples"));

        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), "{ \"version\": 1 }");
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            "{ \"description\": \"only\", \"dependsOn\": [] }");
        File.WriteAllText(Path.Combine(taskDir, "action.prompt.md"), "do nothing");

        bool ps = OperatingSystem.IsWindows();
        string ext = ps ? ".ps1" : ".sh";
        string body = ps
            ? "# catches: a subject carrying the BAD marker\n"
              + "param([string]$SubjectPath = 'nope')\n"
              + "if (-not (Test-Path $SubjectPath)) { exit 1 }\n"
              + "if ((Get-Content $SubjectPath -Raw) -match 'BAD') { Write-Output 'defect present'; exit 1 }\n"
              + "exit 0\n"
            : "# catches: a subject carrying the BAD marker\n"
              + "set -eu\n"
              + "[ -f \"$1\" ] || exit 1\n"
              + "grep -q BAD \"$1\" && { echo 'defect present'; exit 1; }\n"
              + "exit 0\n";
        File.WriteAllText(Path.Combine(taskDir, "guardrails", "01-subject-check" + ext), body);

        string samples = Path.Combine(taskDir, "samples");
        File.WriteAllText(Path.Combine(samples, "01-subject-check.valid.txt"), soundPair ? "clean" : "BAD");
        File.WriteAllText(Path.Combine(samples, "01-subject-check.invalid.txt"), soundPair ? "BAD" : "clean");

        return planDir;
    }
}
