using Guardrails.Core.Execution;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Samples;

namespace Guardrails.Core.Tests.Samples;

/// <summary>
/// RED tests for plan-of-record 26 (guardrail-quality-gate) — <see cref="SampleVerifier"/>, the
/// deterministic executor for the <c>tasks/&lt;id&gt;/samples/</c> pair convention (plan §2/§7). Every
/// test builds a REAL plan folder (<c>guardrails.json</c> + <c>task.json</c> + a real guardrail script
/// + real sample files), loads it with the production <see cref="PlanLoader"/> — the
/// <c>FourFolderLoaderTests</c> house idiom — and drives the real <see cref="SampleVerifier.VerifyAsync"/>
/// entry point. This type's entire subject is process exit codes, so a fixture that fakes the process
/// would prove nothing.
///
/// <para>
/// Every test here fails against the <see cref="NotImplementedException"/> stub (task 01, this file's
/// sibling <c>SampleVerifier.cs</c>); task 02 implements the type so these turn green without this file
/// changing.
/// </para>
/// </summary>
[Trait("Category", "BacklogSlate")]
public sealed class SampleVerifierTests : IDisposable
{
    private static readonly bool Ps = OperatingSystem.IsWindows();
    private static readonly string Ext = Ps ? ".ps1" : ".sh";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly string _root;

    public SampleVerifierTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gr-sampleverifier-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        // A .git marker so the workspace (a plan's parent = _root) counts as a git repo, matching the
        // FourFolderLoaderTests house idiom. No git process is ever run.
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    // =========================================================================================
    // The happy pair.
    // =========================================================================================

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task Verify_ReportsNothing_WhenTheValidHalfExitsZeroAndTheInvalidHalfExitsNonZero()
    {
        string planDir = NewPlan("happy-pair");
        string taskDir = WriteTask(planDir, "01-only");
        WriteGuardrail(taskDir, "01-check", MarkerGuardrailBody());
        WriteSample(taskDir, "01-check", "valid", "a clean artifact, no defect here");
        WriteSample(taskDir, "01-check", "invalid", "this one carries the DEFECT marker");

        PlanDefinition plan = LoadPlan(planDir);

        SampleVerifyResult result = await SampleVerifier.VerifyAsync(
            plan, new ProcessRunner(), DefaultTimeout, CancellationToken.None);

        Assert.Empty(result.Findings);
        Assert.Equal(1, result.PairsVerified);
    }

    // =========================================================================================
    // The can-never-fail detector.
    // =========================================================================================

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task Verify_ReportsInvalidHalfPassed_WhenTheInvalidSampleExitsZero()
    {
        string planDir = NewPlan("invalid-half-passed");
        string taskDir = WriteTask(planDir, "01-only");
        WriteGuardrail(taskDir, "01-check", AlwaysExitBody(0));
        WriteSample(taskDir, "01-check", "valid", "a clean artifact, no defect here");
        WriteSample(taskDir, "01-check", "invalid", "this one carries the DEFECT marker");

        PlanDefinition plan = LoadPlan(planDir);

        SampleVerifyResult result = await SampleVerifier.VerifyAsync(
            plan, new ProcessRunner(), DefaultTimeout, CancellationToken.None);

        SampleFinding finding = Assert.Single(result.Findings);
        Assert.Equal(SampleFindingKind.InvalidHalfPassed, finding.Kind);
    }

    // =========================================================================================
    // The false-red.
    // =========================================================================================

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task Verify_ReportsValidHalfFailed_WhenTheValidSampleExitsNonZero()
    {
        string planDir = NewPlan("valid-half-failed");
        string taskDir = WriteTask(planDir, "01-only");
        WriteGuardrail(taskDir, "01-check", AlwaysExitBody(1));
        WriteSample(taskDir, "01-check", "valid", "a clean artifact, no defect here");
        WriteSample(taskDir, "01-check", "invalid", "this one carries the DEFECT marker");

        PlanDefinition plan = LoadPlan(planDir);

        SampleVerifyResult result = await SampleVerifier.VerifyAsync(
            plan, new ProcessRunner(), DefaultTimeout, CancellationToken.None);

        SampleFinding finding = Assert.Single(result.Findings);
        Assert.Equal(SampleFindingKind.ValidHalfFailed, finding.Kind);
    }

    // =========================================================================================
    // Reversed polarity — one finding, not two.
    // =========================================================================================

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task Verify_ReportsReversedPolarity_AsASingleFinding_WhenBothHalvesAreInverted()
    {
        string planDir = NewPlan("reversed-polarity");
        string taskDir = WriteTask(planDir, "01-only");
        WriteGuardrail(taskDir, "01-check", InvertedMarkerGuardrailBody());
        WriteSample(taskDir, "01-check", "valid", "a clean artifact, no defect here");
        WriteSample(taskDir, "01-check", "invalid", "this one carries the DEFECT marker");

        PlanDefinition plan = LoadPlan(planDir);

        SampleVerifyResult result = await SampleVerifier.VerifyAsync(
            plan, new ProcessRunner(), DefaultTimeout, CancellationToken.None);

        SampleFinding finding = Assert.Single(result.Findings);
        Assert.Equal(SampleFindingKind.ReversedPolarity, finding.Kind);
    }

    // =========================================================================================
    // A one-sided pair certifies nothing.
    // =========================================================================================

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task Verify_ReportsMissingHalf_WhenOnlyOneSideOfThePairIsCommitted()
    {
        string planDir = NewPlan("missing-half");
        string taskDir = WriteTask(planDir, "01-only");
        WriteGuardrail(taskDir, "01-check", MarkerGuardrailBody());
        WriteGuardrail(taskDir, "02-other", MarkerGuardrailBody());
        // "01-check" commits only its .valid half; "02-other" commits only its .invalid half — the
        // mirror image of the same defect, both in one fixture.
        string checkValidPath = WriteSample(taskDir, "01-check", "valid", "a clean artifact, no defect here");
        string otherInvalidPath = WriteSample(taskDir, "02-other", "invalid", "this one carries the DEFECT marker");

        PlanDefinition plan = LoadPlan(planDir);

        SampleVerifyResult result = await SampleVerifier.VerifyAsync(
            plan, new ProcessRunner(), DefaultTimeout, CancellationToken.None);

        Assert.Equal(2, result.Findings.Count);
        Assert.All(result.Findings, f => Assert.Equal(SampleFindingKind.MissingHalf, f.Kind));
        Assert.Contains(result.Findings, f => f.SamplePath == checkValidPath);
        Assert.Contains(result.Findings, f => f.SamplePath == otherInvalidPath);
    }

    // =========================================================================================
    // The stale pair — no guardrail matches.
    // =========================================================================================

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task Verify_ReportsOrphanSample_WhenNoGuardrailMatchesTheSampleBaseName()
    {
        string planDir = NewPlan("orphan-sample");
        string taskDir = WriteTask(planDir, "01-only");
        WriteGuardrail(taskDir, "01-active", MarkerGuardrailBody());
        // "07-renamed" matches no guardrail in this task's guardrails/ — the script was renamed or
        // deleted and this sample was left behind.
        string orphanSamplePath = WriteSample(taskDir, "07-renamed", "valid", "a clean artifact, no defect here");

        PlanDefinition plan = LoadPlan(planDir);

        SampleVerifyResult result = await SampleVerifier.VerifyAsync(
            plan, new ProcessRunner(), DefaultTimeout, CancellationToken.None);

        SampleFinding finding = Assert.Single(result.Findings);
        Assert.Equal(SampleFindingKind.OrphanSample, finding.Kind);
        Assert.Null(finding.GuardrailPath);
        Assert.Equal(orphanSamplePath, finding.SamplePath);
    }

    // =========================================================================================
    // The binding problem, both directions — a verifier that supplies only one convention fails
    // exactly one of these two tests.
    // =========================================================================================

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task Verify_BindsTheSample_AsTheGuardrailsFirstPositionalArgument()
    {
        string planDir = NewPlan("binds-positional");
        string taskDir = WriteTask(planDir, "01-only");
        WriteGuardrail(taskDir, "01-check", PositionalArgOnlyGuardrailBody());
        WriteSample(taskDir, "01-check", "valid", "a clean artifact, no defect here");
        WriteSample(taskDir, "01-check", "invalid", "this one carries the DEFECT marker");

        PlanDefinition plan = LoadPlan(planDir);

        SampleVerifyResult result = await SampleVerifier.VerifyAsync(
            plan, new ProcessRunner(), DefaultTimeout, CancellationToken.None);

        Assert.Empty(result.Findings);
    }

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task Verify_BindsTheSample_AsTheGrSubjectEnvironmentVariable()
    {
        string planDir = NewPlan("binds-env-var");
        string taskDir = WriteTask(planDir, "01-only");
        WriteGuardrail(taskDir, "01-check", GrSubjectOnlyGuardrailBody());
        WriteSample(taskDir, "01-check", "valid", "a clean artifact, no defect here");
        WriteSample(taskDir, "01-check", "invalid", "this one carries the DEFECT marker");

        PlanDefinition plan = LoadPlan(planDir);

        SampleVerifyResult result = await SampleVerifier.VerifyAsync(
            plan, new ProcessRunner(), DefaultTimeout, CancellationToken.None);

        Assert.Empty(result.Findings);
    }

    // =========================================================================================
    // An actionable finding names the guardrail, the sample, and the exit code.
    // =========================================================================================

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task Verify_EveryFinding_NamesTheGuardrailPath_TheSamplePath_AndTheObservedExitCode()
    {
        string planDir = NewPlan("actionable-finding");
        string taskDir = WriteTask(planDir, "01-only");
        string guardrailPath = WriteGuardrail(taskDir, "01-check", AlwaysExitBody(0));
        WriteSample(taskDir, "01-check", "valid", "a clean artifact, no defect here");
        string invalidSamplePath = WriteSample(taskDir, "01-check", "invalid", "this one carries the DEFECT marker");

        PlanDefinition plan = LoadPlan(planDir);

        SampleVerifyResult result = await SampleVerifier.VerifyAsync(
            plan, new ProcessRunner(), DefaultTimeout, CancellationToken.None);

        SampleFinding finding = Assert.Single(result.Findings);
        Assert.Equal(guardrailPath, finding.GuardrailPath);
        Assert.Equal(invalidSamplePath, finding.SamplePath);
        Assert.Equal(0, finding.ObservedExitCode);
        Assert.Contains(guardrailPath, finding.Message, StringComparison.Ordinal);
        Assert.Contains(invalidSamplePath, finding.Message, StringComparison.Ordinal);
    }

    // =========================================================================================
    // Non-pair files in samples/ are ignored.
    // =========================================================================================

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task Verify_IgnoresSamplesFolderFilesThatAreNotAValidOrInvalidHalf()
    {
        string planDir = NewPlan("ignores-non-pair-files");
        string taskDir = WriteTask(planDir, "01-only");
        WriteGuardrail(taskDir, "05-lint", AlwaysExitBody(0));
        string samplesDir = Path.Combine(taskDir, "samples");
        Directory.CreateDirectory(samplesDir);
        File.WriteAllText(Path.Combine(samplesDir, "README.md"), "This folder holds committed sample pairs.\n");
        File.WriteAllText(Path.Combine(samplesDir, "01-thing.probe.ps1"), "# a probe script, not a sample half\n");

        PlanDefinition plan = LoadPlan(planDir);

        SampleVerifyResult result = await SampleVerifier.VerifyAsync(
            plan, new ProcessRunner(), DefaultTimeout, CancellationToken.None);

        Assert.Empty(result.Findings);
        Assert.Equal(0, result.PairsVerified);
    }

    // =========================================================================================
    // A pair we cannot execute is reported, never silently skipped.
    // =========================================================================================

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task Verify_ReportsUnverifiablePair_WhenTheMatchedGuardrailIsAPromptJudge()
    {
        string planDir = NewPlan("unverifiable-prompt");
        string taskDir = WriteTask(planDir, "01-only");
        string guardrailPath = WritePromptGuardrail(taskDir, "03-judge", "Judge whether the change is correct.\n");
        WriteSample(taskDir, "03-judge", "valid", "a correct change");
        WriteSample(taskDir, "03-judge", "invalid", "an incorrect change");

        PlanDefinition plan = LoadPlan(planDir);
        TaskNode task = Assert.Single(plan.Tasks);
        GuardrailDefinition guardrail = Assert.Single(task.Guardrails);
        Assert.Equal(ActionKind.Prompt, guardrail.Kind); // fixture sanity: this pair's guardrail really is a prompt judge.

        SampleVerifyResult result = await SampleVerifier.VerifyAsync(
            plan, new ProcessRunner(), DefaultTimeout, CancellationToken.None);

        SampleFinding finding = Assert.Single(result.Findings);
        Assert.Equal(SampleFindingKind.Unverifiable, finding.Kind);
        Assert.Equal(guardrailPath, finding.GuardrailPath);
    }

    // =========================================================================================
    // The permanent-tax condition (plan of record 26 §7): zero pairs, zero process launches.
    // =========================================================================================

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task Verify_RunsNoGuardrail_WhenNoTaskCarriesASamplePair()
    {
        string planDir = NewPlan("zero-pairs");
        string taskDir = WriteTask(planDir, "01-only");
        string markerPath = Path.Combine(_root, "guardrail-executed.marker");
        WriteGuardrail(taskDir, "01-check", SentinelGuardrailBody(markerPath));

        // Assert BY CONSTRUCTION: this fixture carries no 'samples' directory anywhere, so the
        // absence asserted below is not vacuous.
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(planDir, "*", SearchOption.AllDirectories),
            d => string.Equals(Path.GetFileName(d), "samples", StringComparison.OrdinalIgnoreCase));

        PlanDefinition plan = LoadPlan(planDir);

        SampleVerifyResult result = await SampleVerifier.VerifyAsync(
            plan, new ProcessRunner(), DefaultTimeout, CancellationToken.None);

        Assert.False(File.Exists(markerPath),
            "SampleVerifier launched a guardrail even though the plan carries no committed sample pair — " +
            "this is a permanent tax on every future run of every plan in this repo (plan of record 26 §7).");
        Assert.Equal(0, result.PairsVerified);
    }

    // ── fixture builders ─────────────────────────────────────────────────────────────────────
    // Real folders, real scripts, real processes: this type's entire subject is process exit
    // codes, so every fixture guardrail's exit code is a function of the SAMPLE it is handed,
    // never a hard-coded line — a pair's polarity is a property of the samples, exactly as the
    // committed corpus works.

    private string NewPlan(string name)
    {
        string planDir = Path.Combine(_root, name);
        Directory.CreateDirectory(planDir);
        File.WriteAllText(Path.Combine(planDir, "guardrails.json"), "{ \"version\": 1 }");
        return planDir;
    }

    private static PlanDefinition LoadPlan(string planDir)
    {
        PlanLoadResult loaded = new PlanLoader().Load(planDir);
        Assert.NotNull(loaded.Plan);
        return loaded.Plan!;
    }

    private static string WriteTask(string planDir, string id)
    {
        string taskDir = Path.Combine(planDir, "tasks", id);
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, "task.json"),
            "{ \"description\": \"" + id + "\", \"dependsOn\": [] }");
        WriteScript(Path.Combine(taskDir, "action" + Ext), Ps ? "exit 0\n" : "#!/usr/bin/env bash\nexit 0\n");
        return taskDir;
    }

    private static string WriteGuardrail(string taskDir, string name, string body)
    {
        string guardrailsDir = Path.Combine(taskDir, "guardrails");
        Directory.CreateDirectory(guardrailsDir);
        string path = Path.Combine(guardrailsDir, name + Ext);
        WriteScript(path, body);
        return path;
    }

    private static string WritePromptGuardrail(string taskDir, string name, string content)
    {
        string guardrailsDir = Path.Combine(taskDir, "guardrails");
        Directory.CreateDirectory(guardrailsDir);
        string path = Path.Combine(guardrailsDir, name + ".prompt.md");
        File.WriteAllText(path, content);
        return path;
    }

    private static string WriteSample(string taskDir, string baseName, string half, string content)
    {
        string samplesDir = Path.Combine(taskDir, "samples");
        Directory.CreateDirectory(samplesDir);
        string path = Path.Combine(samplesDir, $"{baseName}.{half}.cs");
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Write an OS-appropriate script (no shebang on Windows; a bash shebang elsewhere),
    /// mirroring the <c>PlanPreflightPhaseTests</c> house idiom.</summary>
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

    /// <summary>
    /// Reads the sample BOTH ways the verifier must supply it — <c>GR_SUBJECT</c> if set, else the
    /// first positional argument — and exits non-zero iff the subject's text carries "DEFECT". Used
    /// for every fixture where the point under test is the sample's CONTENT, not the binding
    /// convention.
    /// </summary>
    private static string MarkerGuardrailBody() => Ps
        ? "# catches: a subject carrying the DEFECT marker\n"
          + "param([string]$SubjectPath = 'gr-sample-verifier-unbound-subject')\n"
          + "if ($env:GR_SUBJECT) { $SubjectPath = $env:GR_SUBJECT }\n"
          + "if (-not (Test-Path $SubjectPath)) { exit 9 }\n"
          + "if ((Get-Content $SubjectPath -Raw) -match 'DEFECT') { exit 1 } else { exit 0 }\n"
        : "#!/usr/bin/env bash\n"
          + "set -eu\n"
          + "SUBJECT=\"${GR_SUBJECT:-${1:-gr-sample-verifier-unbound-subject}}\"\n"
          + "[ -f \"$SUBJECT\" ] || exit 9\n"
          + "grep -q DEFECT \"$SUBJECT\" && exit 1\n"
          + "exit 0\n";

    /// <summary>The inverse of <see cref="MarkerGuardrailBody"/> — exits 0 when the marker IS present,
    /// and non-zero when it is not, so a sound pair (clean valid / marked invalid) trips BOTH halves at
    /// once, in opposite directions, yielding the single reversed-polarity finding.</summary>
    private static string InvertedMarkerGuardrailBody() => Ps
        ? "# catches: nothing - deliberately inverted, for the reversed-polarity fixture only\n"
          + "param([string]$SubjectPath = 'gr-sample-verifier-unbound-subject')\n"
          + "if ($env:GR_SUBJECT) { $SubjectPath = $env:GR_SUBJECT }\n"
          + "if (-not (Test-Path $SubjectPath)) { exit 9 }\n"
          + "if ((Get-Content $SubjectPath -Raw) -match 'DEFECT') { exit 0 } else { exit 1 }\n"
        : "#!/usr/bin/env bash\n"
          + "set -eu\n"
          + "SUBJECT=\"${GR_SUBJECT:-${1:-gr-sample-verifier-unbound-subject}}\"\n"
          + "[ -f \"$SUBJECT\" ] || exit 9\n"
          + "grep -q DEFECT \"$SUBJECT\" && exit 0\n"
          + "exit 1\n";

    /// <summary>Reads ONLY the first positional argument — never <c>GR_SUBJECT</c> — so a verifier that
    /// supplies just the environment variable fails this fixture's pair.</summary>
    private static string PositionalArgOnlyGuardrailBody() => Ps
        ? "# catches: a subject carrying the DEFECT marker (positional argument only)\n"
          + "param([string]$SubjectPath = 'gr-sample-verifier-unbound-subject')\n"
          + "if (-not (Test-Path $SubjectPath)) { exit 9 }\n"
          + "if ((Get-Content $SubjectPath -Raw) -match 'DEFECT') { exit 1 } else { exit 0 }\n"
        : "#!/usr/bin/env bash\n"
          + "set -eu\n"
          + "SUBJECT=\"${1:-gr-sample-verifier-unbound-subject}\"\n"
          + "[ -f \"$SUBJECT\" ] || exit 9\n"
          + "grep -q DEFECT \"$SUBJECT\" && exit 1\n"
          + "exit 0\n";

    /// <summary>Reads ONLY <c>GR_SUBJECT</c> — ignores every positional argument — so a verifier that
    /// supplies just the positional argument fails this fixture's pair.</summary>
    private static string GrSubjectOnlyGuardrailBody() => Ps
        ? "# catches: a subject carrying the DEFECT marker (GR_SUBJECT only)\n"
          + "$SubjectPath = $env:GR_SUBJECT\n"
          + "if ([string]::IsNullOrEmpty($SubjectPath) -or -not (Test-Path $SubjectPath)) { exit 9 }\n"
          + "if ((Get-Content $SubjectPath -Raw) -match 'DEFECT') { exit 1 } else { exit 0 }\n"
        : "#!/usr/bin/env bash\n"
          + "set -eu\n"
          + "SUBJECT=\"${GR_SUBJECT:-}\"\n"
          + "[ -n \"$SUBJECT\" ] || exit 9\n"
          + "[ -f \"$SUBJECT\" ] || exit 9\n"
          + "grep -q DEFECT \"$SUBJECT\" && exit 1\n"
          + "exit 0\n";

    /// <summary>Always exits <paramref name="exitCode"/> regardless of the subject — the fixture for a
    /// guardrail that ignores its argument entirely (the can-never-fail / always-red shapes).</summary>
    private static string AlwaysExitBody(int exitCode) => Ps
        ? $"# catches: nothing - always exits {exitCode}, ignoring the subject entirely\nexit {exitCode}\n"
        : $"#!/usr/bin/env bash\nexit {exitCode}\n";

    /// <summary>Writes <paramref name="markerPath"/> if it is EVER executed, then exits 0. Used to prove
    /// a negative — that the verifier launched no process at all for a plan with no committed pairs.</summary>
    private static string SentinelGuardrailBody(string markerPath) => Ps
        ? "# catches: nothing - a sentinel that must never run\n"
          + $"Set-Content -Path '{EscapeForPowerShellSingleQuotedString(markerPath)}' -Value 'executed'\n"
          + "exit 0\n"
        : "#!/usr/bin/env bash\n"
          + $"echo executed > '{EscapeForBashSingleQuotedString(markerPath)}'\n"
          + "exit 0\n";

    private static string EscapeForPowerShellSingleQuotedString(string value) => value.Replace("'", "''");

    private static string EscapeForBashSingleQuotedString(string value) => value.Replace("'", "'\\''");
}
