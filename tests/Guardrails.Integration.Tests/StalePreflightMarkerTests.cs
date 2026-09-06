using Guardrails.Cli;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.TestSupport;

namespace Guardrails.Integration.Tests;

/// <summary>
/// Issue #623 — a plan-preflight FAILURE verdict outlives the run that produced it.
///
/// <para>
/// <b>The reported symptom, and why its stated cause is wrong.</b> The filing concludes the phase
/// "skips" because the marker guard ignores <c>status</c>. It does not: that guard has required
/// <c>Status == Passed</c> since <c>382ed999</c> (2026-07-03), two months before the report. The real
/// mechanism is one level earlier and simpler — <c>SamplePairsPassAsync</c> writes a
/// <c>plan-preflight-failed</c> section when a committed sample pair fails, and on success writes
/// <b>nothing at all</b>, by design ("no journal section, no console line, no marker touched"). So
/// fixing the guardrail that failed the gate leaves the failure verdict in place forever.
/// </para>
///
/// <para>
/// It compounds on exactly the plans the reporter had: one declaring <b>no <c>preflights/</c> folder</b>.
/// There the phase returns at the <c>PlanPreflights.Count == 0</c> short-circuit having written nothing,
/// so no later write corrects the record either. <c>run.json</c> then reports a pre-DAG failure for a
/// run that passed the pre-DAG phase, and every surface reading that section — <c>status</c>, the log
/// site's halt banner — reports it too. This is Tier 1's thesis in its purest form: a check reporting a
/// verdict it did not reach.
/// </para>
///
/// <para>
/// Both tests drive the REAL <see cref="PlanPreflightPhase.EvaluateAsync"/> over a real plan folder with
/// real OS-picked scripts, twice against one journal — the second call standing in for the resume. A
/// faked verifier would satisfy the assertion while the production path stayed broken.
/// </para>
/// </summary>
[Trait("Category", "ScanSoundness")]
public sealed class StalePreflightMarkerTests
{
    private static readonly bool UsePowerShell = OperatingSystem.IsWindows();
    private static readonly string Ext = UsePowerShell ? ".ps1" : ".sh";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The headline case. A broken pair records <c>plan-preflight-failed</c>; the pair is then fixed and
    /// the phase re-run — the verdict on record must describe THIS evaluation, not the previous one.
    /// </summary>
    [Fact]
    public async Task APassingSamplePairClearsThePriorFailureVerdict()
    {
        using var plan = new SamplePlan();
        plan.WriteGuardrail(rejectsItsOwnValidSample: true);

        Assert.False(await EvaluateAsync(plan), "a rejected valid half must fail the pre-DAG phase");
        Assert.Equal(PlanPhaseStatus.PlanPreflightFailed, ReadMarker(plan)!.Status);

        // The operator fixes the guardrail and re-runs — the #623 scenario exactly.
        plan.WriteGuardrail(rejectsItsOwnValidSample: false);

        Assert.True(await EvaluateAsync(plan), "the repaired pair must pass the pre-DAG phase");

        PlanPreflightsSection? after = ReadMarker(plan);
        Assert.False(
            after?.Status == PlanPhaseStatus.PlanPreflightFailed,
            "run.json still reports plan-preflight-failed after an evaluation that PASSED — the verdict "
            + "describes a previous run, and `status` plus the log site's halt banner both report it (#623).");
    }

    /// <summary>
    /// The polarity control, so the fix cannot be "stop writing failure markers". A pair that is STILL
    /// broken on the second evaluation must keep recording the failure.
    /// </summary>
    [Fact]
    public async Task AStillBrokenSamplePairKeepsRecordingTheFailure()
    {
        using var plan = new SamplePlan();
        plan.WriteGuardrail(rejectsItsOwnValidSample: true);

        Assert.False(await EvaluateAsync(plan));
        Assert.False(await EvaluateAsync(plan));

        Assert.Equal(PlanPhaseStatus.PlanPreflightFailed, ReadMarker(plan)!.Status);
    }

    private static async Task<bool> EvaluateAsync(SamplePlan plan)
    {
        PlanDefinition definition = LoadPlan(plan);
        RunJournal journal = RunJournal.LoadOrCreate(definition);

        return await PlanPreflightPhase.EvaluateAsync(
            definition, journal, new ProcessRunner(), heartbeatOut: null, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static PlanDefinition LoadPlan(SamplePlan plan)
    {
        PlanLoadResult load = new PlanLoader().Load(plan.PlanDir);
        Assert.NotNull(load.Plan);
        Assert.False(load.HasErrors, string.Join("\n", load.Diagnostics));
        return load.Plan!;
    }

    private static PlanPreflightsSection? ReadMarker(SamplePlan plan) =>
        RunJournal.LoadOrCreate(LoadPlan(plan)).Document.PlanPreflights;

    /// <summary>
    /// #530 — the SOUND arrangement of this fixture's pair actually discriminates its halves.
    ///
    /// <para>
    /// The tests above turn on the difference between a sound guardrail and one broken in a named
    /// direction. That difference is only real if the sound body reads the halves differently: a body
    /// that returned one code for both would make "sound" and "rejects its own valid sample" the same
    /// arrangement, and the stale-marker tests would pass on a fixture proving nothing. The broken
    /// variant is deliberately undiscriminating in one direction and is not proved here — that is what it
    /// is for.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheSoundFixturePair_DiscriminatesItsTwoHalves()
    {
        using var plan = new SamplePlan();
        plan.WriteGuardrail(rejectsItsOwnValidSample: false);

        await SynthesisedPairProof.AssertHalvesAreDiscriminatedAsync(
            plan.GuardrailPath,
            plan.HalfPath("valid"),
            plan.HalfPath("invalid"),
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A minimal real plan carrying ONE task with ONE committed sample pair and NO
    /// <c>&lt;plan&gt;/preflights/</c> folder — the reporter's shape, and the one where nothing
    /// downstream can correct the record.
    /// </summary>
    private sealed class SamplePlan : IDisposable
    {
        public string PlanDir { get; }
        private readonly string _taskDir;

        public SamplePlan()
        {
            PlanDir = Path.Combine(Path.GetTempPath(), "gr623-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(PlanDir);
            File.WriteAllText(Path.Combine(PlanDir, "guardrails.json"),
                """
                {
                  "version": 1,
                  "workspace": "."
                }
                """);

            _taskDir = Path.Combine(PlanDir, "tasks", "01-only");
            Directory.CreateDirectory(Path.Combine(_taskDir, "guardrails"));
            Directory.CreateDirectory(Path.Combine(_taskDir, "samples"));
            File.WriteAllText(Path.Combine(_taskDir, "task.json"),
                """
                {
                  "description": "the only task",
                  "dependsOn": [],
                  "writeScope": []
                }
                """);
            WriteScript(Path.Combine(_taskDir, "action" + Ext), UsePowerShell ? "exit 0\n" : "#!/usr/bin/env bash\nexit 0\n");

            // The pair the guardrail below is judged against: the VALID half must exit 0, the INVALID
            // half non-zero. Only the valid half's marker text differs between them.
            File.WriteAllText(Path.Combine(_taskDir, "samples", "01-check.valid.cs"), "// GOOD-MARKER\n");
            File.WriteAllText(Path.Combine(_taskDir, "samples", "01-check.invalid.cs"), "// nothing here\n");
        }

        /// <summary>
        /// Writes the guardrail either sound (finds GOOD-MARKER in the valid half → exit 0) or broken in
        /// the one direction the gate exists to catch: rejecting its own valid sample.
        /// </summary>
        /// <summary>The guardrail on disk, and its two halves — the trio the #530 proof executes.</summary>
        public string GuardrailPath => Path.Combine(_taskDir, "guardrails", "01-check" + Ext);

        public string HalfPath(string half) => Path.Combine(_taskDir, "samples", "01-check." + half + ".cs");

        public void WriteGuardrail(bool rejectsItsOwnValidSample)
        {
            string wanted = rejectsItsOwnValidSample ? "MARKER-THAT-IS-IN-NEITHER-HALF" : "GOOD-MARKER";
            string body = UsePowerShell
                ? "# catches: a subject missing the marker\n"
                  + "$subject = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { $args[0] }\n"
                  + $"if ((Get-Content -Raw -LiteralPath $subject) -match '{wanted}') {{ exit 0 }}\n"
                  + "exit 1\n"
                : "#!/usr/bin/env bash\n# catches: a subject missing the marker\n"
                  + "SUBJECT=\"${GR_SUBJECT:-$1}\"\n"
                  + $"if grep -q '{wanted}' \"$SUBJECT\"; then exit 0; fi\n"
                  + "exit 1\n";

            WriteScript(Path.Combine(_taskDir, "guardrails", "01-check" + Ext), body);
        }

        private static void WriteScript(string path, string content)
        {
            File.WriteAllText(path, content);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(PlanDir, recursive: true); }
            catch (IOException) { }
        }
    }
}
