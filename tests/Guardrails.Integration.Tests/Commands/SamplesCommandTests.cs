using Guardrails.Cli;
using Guardrails.Core.Execution;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Samples;

namespace Guardrails.Integration.Tests.Commands;

/// <summary>
/// The AGREEMENT test for <c>guardrails samples verify [folder]</c> (plan of record 26, task 03). A
/// source grep proving <c>SamplesCommand</c> calls <see cref="SampleVerifier.VerifyAsync"/> is
/// defeatable by a dead field or an unused private method that survives
/// <c>TreatWarningsAsErrors=true</c> (#468) — either lets the verb re-implement pair discovery and
/// polarity classification inline while the grep still passes. So the binding this file proves is a
/// PROPERTY, not a spelling: for a corpus of sample pairs, the findings the verb REPORTS must equal the
/// findings <see cref="SampleVerifier.VerifyAsync"/> returns for that same corpus, computed at RUN TIME
/// — never hard-coded, or the test would be a second copy of the policy and drift exactly like the
/// implementation it exists to forbid.
///
/// <para>
/// Drives the verb through the REAL composition root, <see cref="CommandFactory.BuildRootCommand"/>
/// (the <c>LockCliTests</c> idiom): a <c>samples verify</c> that works only via a hand-built root but is
/// missing from the factory would ship broken. Output is captured with <see cref="StringConsoleIo"/> —
/// no process-global console, parallel-safe.
/// </para>
/// </summary>
[Trait("Category", "BacklogSlate")]
public sealed class SamplesCommandTests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private static async Task<(int Exit, string Output)> InvokeVerbAsync(string planDir)
    {
        var io = new StringConsoleIo();
        var root = CommandFactory.BuildRootCommand(io);
        int exit = await root.Parse(["samples", "verify", planDir]).InvokeAsync();
        return (exit, io.OutText);
    }

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task Verify_TheVerbsReport_AgreesWithSampleVerifier_OnACorpusThatProducesFindings()
    {
        using var corpus = new SampleCorpusBuilder();
        BuildCorpusWithFindings(corpus);

        PlanDefinition plan = LoadPlan(corpus.PlanDir);
        SampleVerifyResult reference = await SampleVerifier.VerifyAsync(
            plan, new ProcessRunner(), DefaultTimeout, CancellationToken.None);

        // Fixture sanity: the load-bearing corpus must actually produce findings of at least two
        // different kinds, or this test is much weaker than the property it is meant to check.
        Assert.True(reference.Findings.Count >= 2);
        Assert.True(reference.Findings.Select(f => f.Kind).Distinct().Count() >= 2);

        (_, string output) = await InvokeVerbAsync(corpus.PlanDir);

        List<string> findingLines = ExtractFindingLines(output);

        // No more than the reference findings: a verb that reports extras is not "agreeing", it is
        // over-reporting.
        Assert.Equal(reference.Findings.Count, findingLines.Count);

        // Every reference finding is accounted for, by guardrail path + sample path + observed exit code.
        foreach (SampleFinding finding in reference.Findings)
        {
            string expectedExit = finding.ObservedExitCode?.ToString() ?? "(none)";
            Assert.Contains(findingLines, line =>
                line.StartsWith(finding.Kind + ":", StringComparison.Ordinal) &&
                line.Contains(finding.SamplePath, StringComparison.Ordinal) &&
                (finding.GuardrailPath is null || line.Contains(finding.GuardrailPath, StringComparison.Ordinal)) &&
                line.Contains(expectedExit, StringComparison.Ordinal));
        }
    }

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task Verify_TheVerbsReport_AgreesWithSampleVerifier_OnACleanCorpus()
    {
        using var corpus = new SampleCorpusBuilder();
        BuildCleanCorpus(corpus);

        PlanDefinition plan = LoadPlan(corpus.PlanDir);
        SampleVerifyResult reference = await SampleVerifier.VerifyAsync(
            plan, new ProcessRunner(), DefaultTimeout, CancellationToken.None);

        // Fixture sanity: this corpus must be genuinely two-sided-sound, or "agrees" would be satisfied
        // by a verb that reports everything as broken.
        Assert.Empty(reference.Findings);
        Assert.True(reference.PairsVerified > 0);

        (int exit, string output) = await InvokeVerbAsync(corpus.PlanDir);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Empty(ExtractFindingLines(output));
    }

    [Fact]
    [Trait("Category", "BacklogSlate")]
    public async Task Verify_TheVerbsExitCode_FollowsSampleVerifiersVerdict_NotItsOwnJudgement()
    {
        using var findingsCorpus = new SampleCorpusBuilder();
        BuildCorpusWithFindings(findingsCorpus);
        using var cleanCorpus = new SampleCorpusBuilder();
        BuildCleanCorpus(cleanCorpus);

        SampleVerifyResult findingsReference = await SampleVerifier.VerifyAsync(
            LoadPlan(findingsCorpus.PlanDir), new ProcessRunner(), DefaultTimeout, CancellationToken.None);
        SampleVerifyResult cleanReference = await SampleVerifier.VerifyAsync(
            LoadPlan(cleanCorpus.PlanDir), new ProcessRunner(), DefaultTimeout, CancellationToken.None);

        // Fixture sanity: the two corpora must actually disagree on the verdict, or this test cannot
        // distinguish "follows the verifier" from "always returns the same code".
        Assert.False(findingsReference.Passed);
        Assert.True(cleanReference.Passed);

        (int findingsExit, _) = await InvokeVerbAsync(findingsCorpus.PlanDir);
        (int cleanExit, _) = await InvokeVerbAsync(cleanCorpus.PlanDir);

        Assert.Equal(ExitCodes.HarnessError, findingsExit);
        Assert.Equal(ExitCodes.Success, cleanExit);
    }

    // ── fixture corpora ──────────────────────────────────────────────────────────────────────
    // Every guardrail here is a REAL script, executed for real by both the reference call and the
    // verb-under-test — this is the only way "agreement" is a fact about the verb's behaviour rather
    // than about a shared assumption baked into the test.

    /// <summary>
    /// A corpus that produces findings of THREE different kinds in one task: a guardrail that rejects
    /// its own valid sample (<see cref="SampleFindingKind.ValidHalfFailed"/>), one that can never fail
    /// (<see cref="SampleFindingKind.InvalidHalfPassed"/>), and a one-sided pair
    /// (<see cref="SampleFindingKind.MissingHalf"/>).
    /// </summary>
    private static void BuildCorpusWithFindings(SampleCorpusBuilder corpus)
    {
        string taskDir = corpus.AddTask("01-only");

        corpus.AddGuardrail(taskDir, "01-always-red", AlwaysExitBody(1));
        corpus.AddSample(taskDir, "01-always-red", "valid", "irrelevant content");
        corpus.AddSample(taskDir, "01-always-red", "invalid", "irrelevant content");

        corpus.AddGuardrail(taskDir, "02-always-green", AlwaysExitBody(0));
        corpus.AddSample(taskDir, "02-always-green", "valid", "irrelevant content");
        corpus.AddSample(taskDir, "02-always-green", "invalid", "irrelevant content");

        corpus.AddGuardrail(taskDir, "03-one-sided", MarkerGuardrailBody());
        corpus.AddSample(taskDir, "03-one-sided", "valid", "a clean artifact, no defect here");
        // Deliberately no ".invalid" half for "03-one-sided" — a MissingHalf finding.
    }

    /// <summary>A corpus whose pairs are all genuinely two-sided sound — zero findings.</summary>
    private static void BuildCleanCorpus(SampleCorpusBuilder corpus)
    {
        string taskDir = corpus.AddTask("01-only");

        corpus.AddGuardrail(taskDir, "01-check", MarkerGuardrailBody());
        corpus.AddSample(taskDir, "01-check", "valid", "a clean artifact, no defect here");
        corpus.AddSample(taskDir, "01-check", "invalid", "this one carries the DEFECT marker");

        corpus.AddGuardrail(taskDir, "02-check", MarkerGuardrailBody());
        corpus.AddSample(taskDir, "02-check", "valid", "another clean artifact");
        corpus.AddSample(taskDir, "02-check", "invalid", "and another DEFECT carrier");
    }

    /// <summary>Lines the verb prints one per finding — see <c>SamplesCommand.RunAsync</c>.</summary>
    private static List<string> ExtractFindingLines(string output)
    {
        string[] kindNames = Enum.GetNames<SampleFindingKind>();
        return output
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => kindNames.Any(kind => line.StartsWith(kind + ":", StringComparison.Ordinal)))
            .ToList();
    }

    private static PlanDefinition LoadPlan(string planDir)
    {
        PlanLoadResult loaded = new PlanLoader().Load(planDir);
        Assert.NotNull(loaded.Plan);
        return loaded.Plan!;
    }

    /// <summary>Always exits <paramref name="exitCode"/> regardless of the subject.</summary>
    private static string AlwaysExitBody(int exitCode) => SampleCorpusBuilder.UsePowerShell
        ? $"exit {exitCode}\n"
        : $"#!/usr/bin/env bash\nexit {exitCode}\n";

    /// <summary>
    /// Reads the sample both ways <see cref="SampleVerifier"/> supplies it — <c>GR_SUBJECT</c> if set,
    /// else the first positional argument — and exits non-zero iff the subject's text carries "DEFECT".
    /// </summary>
    private static string MarkerGuardrailBody() => SampleCorpusBuilder.UsePowerShell
        ? "param([string]$SubjectPath = 'gr-samplescmd-unbound-subject')\n"
          + "if ($env:GR_SUBJECT) { $SubjectPath = $env:GR_SUBJECT }\n"
          + "if (-not (Test-Path $SubjectPath)) { exit 9 }\n"
          + "if ((Get-Content $SubjectPath -Raw) -cmatch 'DEFECT') { exit 1 } else { exit 0 }\n"
        : "#!/usr/bin/env bash\n"
          + "set -eu\n"
          + "SUBJECT=\"${GR_SUBJECT:-${1:-gr-samplescmd-unbound-subject}}\"\n"
          + "[ -f \"$SUBJECT\" ] || exit 9\n"
          + "grep -q DEFECT \"$SUBJECT\" && exit 1\n"
          + "exit 0\n";

    /// <summary>
    /// Builds a real, runnable plan folder in a temp directory carrying <c>tasks/&lt;id&gt;/samples/</c>
    /// pairs — <see cref="ScriptPlanBuilder"/> does not support samples, so this fixture is scoped to
    /// this test file. Deleted on <see cref="Dispose"/>; never written into the repository tree.
    /// </summary>
    private sealed class SampleCorpusBuilder : IDisposable
    {
        public static readonly bool UsePowerShell = OperatingSystem.IsWindows();
        private static readonly string Ext = UsePowerShell ? ".ps1" : ".sh";

        public string PlanDir { get; }

        public SampleCorpusBuilder()
        {
            PlanDir = Path.Combine(Path.GetTempPath(), "gr-samplescmd-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(PlanDir);
            File.WriteAllText(Path.Combine(PlanDir, "guardrails.json"),
                """
                {
                  "version": 1,
                  "workspace": "."
                }
                """);
            Directory.CreateDirectory(Path.Combine(PlanDir, "tasks"));
        }

        public string AddTask(string id)
        {
            string taskDir = Path.Combine(PlanDir, "tasks", id);
            Directory.CreateDirectory(taskDir);
            Directory.CreateDirectory(Path.Combine(taskDir, "guardrails"));
            File.WriteAllText(Path.Combine(taskDir, "task.json"),
                $$"""
                {
                  "description": "task {{id}}",
                  "dependsOn": []
                }
                """);
            WriteScript(Path.Combine(taskDir, "action" + Ext),
                UsePowerShell ? "exit 0\n" : "#!/usr/bin/env bash\nexit 0\n");
            return taskDir;
        }

        public void AddGuardrail(string taskDir, string name, string body) =>
            WriteScript(Path.Combine(taskDir, "guardrails", name + Ext), body);

        public void AddSample(string taskDir, string baseName, string half, string content)
        {
            string samplesDir = Path.Combine(taskDir, "samples");
            Directory.CreateDirectory(samplesDir);
            File.WriteAllText(Path.Combine(samplesDir, $"{baseName}.{half}.cs"), content);
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
