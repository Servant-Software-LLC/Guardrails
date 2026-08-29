using Guardrails.Cli;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;

namespace Guardrails.Integration.Tests.Samples;

/// <summary>
/// Composition-root tests for the sample-pair step of the pre-DAG plan-preflight phase (#510). Every
/// test drives the REAL <see cref="PlanPreflightPhase"/> over a temp plan folder and asserts on what
/// the PHASE returned and journaled — never on a verifier this file ran itself, which would be green
/// against a phase that was never wired at all (#120).
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
        string planDir = CreatePlan("reversed", declarePreflightsFolder: true, soundPair: false);
        PlanDefinition plan = LoadPlan(planDir);
        RunJournal journal = RunJournal.LoadOrCreate(planDir, plan);

        // Drive the REAL pre-DAG phase — never construct the verifier by hand here.
        bool proceed = await PlanPreflightPhase.EvaluateAsync(
            plan, journal, new ProcessRunner(), heartbeatOut: null, CancellationToken.None);

        Assert.False(proceed);
    }

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task EvaluateAsync_HaltsOnABadSamplePair_EvenWhenThePlanDeclaresNoPreflightsFolder()
    {
        // The placement trap: EvaluateAsync returns TRUE immediately when PlanPreflights is empty, so a
        // sample step added after that early return protects only the plans that already opted into
        // Full Flight Checks — which is most plans left unprotected, and every one of this test's
        // siblings still green.
        string planDir = CreatePlan("no-preflights", declarePreflightsFolder: false, soundPair: false);
        PlanDefinition plan = LoadPlan(planDir);
        Assert.Empty(plan.PlanPreflights);
        RunJournal journal = RunJournal.LoadOrCreate(planDir, plan);

        bool proceed = await PlanPreflightPhase.EvaluateAsync(
            plan, journal, new ProcessRunner(), heartbeatOut: null, CancellationToken.None);

        Assert.False(proceed);
    }

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task EvaluateAsync_ReturnsTrue_WhenEverySamplePairIsSound()
    {
        // Without this the phase could return false unconditionally and every other test here would
        // still pass — the mirror of the can-never-fail guardrail this whole feature exists to detect.
        string planDir = CreatePlan("sound", declarePreflightsFolder: false, soundPair: true);
        PlanDefinition plan = LoadPlan(planDir);
        RunJournal journal = RunJournal.LoadOrCreate(planDir, plan);

        bool proceed = await PlanPreflightPhase.EvaluateAsync(
            plan, journal, new ProcessRunner(), heartbeatOut: null, CancellationToken.None);

        Assert.True(proceed);
    }

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task EvaluateAsync_JournalsTheFailingPair_SoAPostMortemReaderCanSeeWhichPairHalted()
    {
        string planDir = CreatePlan("journalled", declarePreflightsFolder: false, soundPair: false);
        PlanDefinition plan = LoadPlan(planDir);
        RunJournal journal = RunJournal.LoadOrCreate(planDir, plan);

        await PlanPreflightPhase.EvaluateAsync(
            plan, journal, new ProcessRunner(), heartbeatOut: null, CancellationToken.None);

        // A halt whose only trace is the operator's scrollback is the #432 failure repeating.
        string recorded = File.ReadAllText(RunJournal.PathFor(planDir));
        Assert.Contains("01-subject-check", recorded, StringComparison.Ordinal);
    }

    // ── fixture ─────────────────────────────────────────────────────────────────────────────────────
    // The guardrail's exit code is a function of the SUBJECT it is handed, so a pair's polarity is a
    // property of the two sample files rather than a hard-coded exit line.

    private static PlanDefinition LoadPlan(string planDir)
    {
        PlanLoadResult loaded = new PlanLoader().Load(planDir);
        Assert.NotNull(loaded.Plan);
        return loaded.Plan!;
    }

    private string CreatePlan(string name, bool declarePreflightsFolder, bool soundPair)
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
        // soundPair: .valid is clean (exit 0) and .invalid carries the marker (exit 1).
        // Otherwise the two halves are SWAPPED — reversed polarity, the pair proves nothing.
        File.WriteAllText(Path.Combine(samples, "01-subject-check.valid.txt"), soundPair ? "clean" : "BAD");
        File.WriteAllText(Path.Combine(samples, "01-subject-check.invalid.txt"), soundPair ? "BAD" : "clean");

        if (declarePreflightsFolder)
        {
            Directory.CreateDirectory(Path.Combine(planDir, "preflights"));
            File.WriteAllText(Path.Combine(planDir, "preflights", "01-green" + ext),
                ps ? "# catches: nothing — always green\nexit 0\n" : "# catches: nothing — always green\nexit 0\n");
        }

        return planDir;
    }
}
