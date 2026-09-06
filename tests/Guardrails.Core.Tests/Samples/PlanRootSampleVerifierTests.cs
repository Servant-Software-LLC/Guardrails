using Guardrails.Core.Execution;
using Guardrails.Core.Loading;
using Guardrails.Core.Model;
using Guardrails.Core.Samples;

namespace Guardrails.Core.Tests.Samples;

/// <summary>
/// Issue #624 — <see cref="SampleVerifier"/> walked <c>plan.Tasks</c> only, so a pair belonging to a
/// PLAN-ROOT check (<c>&lt;plan&gt;/guardrails/</c>, <c>&lt;plan&gt;/preflights/</c>) was verified by
/// nothing: not the <c>samples verify</c> verb, not the pre-DAG phase. Measured on
/// <c>docs/plans/228-escalation-ladder</c>, which commits a real pair for its plan-root union guardrail:
/// the verb printed <c>OK: 0 sample pair(s) verified, 0 findings</c>. <b>OK, over a pair sitting on
/// disk</b> — the verb could not tell "there are no pairs" from "there are pairs and I did not look",
/// and reported the reassuring one.
///
/// <para>
/// It is the worst possible place to skip. A plan-root <c>scope: "integration"</c> guardrail is
/// re-evaluated at EVERY union point, so an authoring error there red-halts the whole run at a merge,
/// where the same mistake in a task guardrail costs one attempt.
/// </para>
///
/// <para>
/// <b>Why the halves are directories.</b> A task guardrail inspects one artifact, so its half is one
/// file passed as <c>GR_SUBJECT</c>/<c>argv[0]</c>. A plan-root guardrail inspects the merged
/// <i>workspace</i> — the committed example resolves <c>$ws = $env:GUARDRAILS_WORKSPACE</c> and falls
/// back to the working directory — so its half must BE a workspace root. These tests pin that contract
/// by driving the real verifier over real scripts that read their subject each way.
/// </para>
/// </summary>
[Trait("Category", "ScanSoundness")]
public sealed class PlanRootSampleVerifierTests
{
    private static readonly bool Ps = OperatingSystem.IsWindows();
    private static readonly string Ext = Ps ? ".ps1" : ".sh";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>The headline: a sound plan-root pair is COUNTED, where it used to be invisible.</summary>
    [Fact]
    public async Task ASoundPlanRootPairIsVerified_NotSilentlySkipped()
    {
        using var plan = new PlanRootPlan();
        plan.WriteCheck(findsTheMarker: true);

        SampleVerifyResult result = await VerifyAsync(plan);

        Assert.Empty(result.Findings);
        Assert.Equal(1, result.PairsVerified);
    }

    /// <summary>
    /// The teeth. A plan-root guardrail that rejects its own valid workspace must be REPORTED — this is
    /// the false-red that would red-halt a whole run at a merge.
    /// </summary>
    [Fact]
    public async Task APlanRootGuardrailRejectingItsValidHalf_IsReported()
    {
        using var plan = new PlanRootPlan();
        plan.WriteCheck(findsTheMarker: false); // demands a marker neither half carries

        SampleVerifyResult result = await VerifyAsync(plan);

        SampleFinding finding = Assert.Single(result.Findings);
        Assert.Equal(SampleFindingKind.ValidHalfFailed, finding.Kind);
        Assert.Equal(1, finding.ObservedExitCode);
    }

    /// <summary>
    /// The opposite polarity, which is the more dangerous one: a check that can never fail certifies
    /// every union, including a broken one.
    /// </summary>
    [Fact]
    public async Task APlanRootGuardrailThatCanNeverFail_IsReported()
    {
        using var plan = new PlanRootPlan();
        plan.WriteAlwaysPassingCheck();

        SampleVerifyResult result = await VerifyAsync(plan);

        SampleFinding finding = Assert.Single(result.Findings);
        Assert.Equal(SampleFindingKind.InvalidHalfPassed, finding.Kind);
        Assert.Equal(0, finding.ObservedExitCode);
    }

    /// <summary>A pair naming no plan-root check is a stale leftover, not a silent pass.</summary>
    [Fact]
    public async Task APairNamingNoPlanRootCheck_IsReportedAsOrphaned()
    {
        using var plan = new PlanRootPlan();
        plan.WriteCheck(findsTheMarker: true);
        plan.AddPairDirectory("99-no-such-check");

        SampleVerifyResult result = await VerifyAsync(plan);

        Assert.Contains(result.Findings, f => f.Kind == SampleFindingKind.OrphanSample);
    }

    /// <summary>A one-sided pair certifies nothing, and says so rather than counting as verified.</summary>
    [Fact]
    public async Task AOneSidedPlanRootPair_IsReportedAsMissingAHalf()
    {
        using var plan = new PlanRootPlan();
        plan.WriteCheck(findsTheMarker: true);
        plan.DeleteInvalidHalf();

        SampleVerifyResult result = await VerifyAsync(plan);

        SampleFinding finding = Assert.Single(result.Findings);
        Assert.Equal(SampleFindingKind.MissingHalf, finding.Kind);
        Assert.Equal(0, result.PairsVerified);
    }

    private static async Task<SampleVerifyResult> VerifyAsync(PlanRootPlan plan)
    {
        PlanLoadResult load = new PlanLoader().Load(plan.PlanDir);
        Assert.NotNull(load.Plan);
        return await SampleVerifier
            .VerifyAsync(load.Plan!, new ProcessRunner(), Timeout, CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A minimal plan whose ONLY check is at the plan root, with its pair at
    /// <c>&lt;plan&gt;/samples/&lt;check&gt;/{valid,invalid}/</c>. Each half is a workspace tree holding
    /// one repo-relative file, so a guardrail resolving <c>GUARDRAILS_WORKSPACE</c> (or its working
    /// directory) reads a different file per half — which is the whole subject contract under test.
    /// </summary>
    private sealed class PlanRootPlan : IDisposable
    {
        private const string CheckName = "01-union-verified";
        public string PlanDir { get; }

        public PlanRootPlan()
        {
            PlanDir = Path.Combine(Path.GetTempPath(), "gr624-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(PlanDir, "guardrails"));
            Directory.CreateDirectory(Path.Combine(PlanDir, "tasks", "01-only"));

            File.WriteAllText(Path.Combine(PlanDir, "guardrails.json"),
                """
                {
                  "version": 1,
                  "workspace": "."
                }
                """);
            File.WriteAllText(Path.Combine(PlanDir, "tasks", "01-only", "task.json"),
                """
                {
                  "description": "the only task",
                  "dependsOn": [],
                  "writeScope": []
                }
                """);
            WriteScript(Path.Combine(PlanDir, "tasks", "01-only", "action" + Ext),
                Ps ? "exit 0\n" : "#!/usr/bin/env bash\nexit 0\n");

            // The two workspace halves. Same relative path, different content: the valid one carries the
            // marker a correct union has, the invalid one carries a conflict marker instead.
            WriteHalf("valid", "OK-MARKER\n");
            WriteHalf("invalid", "<<<<<<< HEAD\n");
        }

        private void WriteHalf(string half, string content)
        {
            string dir = Path.Combine(PlanDir, "samples", CheckName, half, "out");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "greeting.txt"), content);
        }

        public void DeleteInvalidHalf() =>
            Directory.Delete(Path.Combine(PlanDir, "samples", CheckName, "invalid"), recursive: true);

        public void AddPairDirectory(string name)
        {
            Directory.CreateDirectory(Path.Combine(PlanDir, "samples", name, "valid"));
            Directory.CreateDirectory(Path.Combine(PlanDir, "samples", name, "invalid"));
        }

        /// <summary>
        /// The plan-root check, reading its subject as a WORKSPACE — <c>GUARDRAILS_WORKSPACE</c> with a
        /// working-directory fallback, exactly as the committed example does.
        /// </summary>
        public void WriteCheck(bool findsTheMarker)
        {
            string wanted = findsTheMarker ? "OK-MARKER" : "MARKER-IN-NEITHER-HALF";
            string body = Ps
                ? "# catches: a union missing the marker\n"
                  + "$ws = $env:GUARDRAILS_WORKSPACE\n"
                  + "if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }\n"
                  + "$f = Join-Path $ws 'out/greeting.txt'\n"
                  + "if (-not (Test-Path -LiteralPath $f)) { exit 1 }\n"
                  + $"if ((Get-Content -Raw -LiteralPath $f) -match '{wanted}') {{ exit 0 }}\n"
                  + "exit 1\n"
                : "#!/usr/bin/env bash\n# catches: a union missing the marker\n"
                  + "WS=\"${GUARDRAILS_WORKSPACE:-$(pwd)}\"\n"
                  + "F=\"$WS/out/greeting.txt\"\n"
                  + "[ -f \"$F\" ] || exit 1\n"
                  + $"if grep -q '{wanted}' \"$F\"; then exit 0; fi\n"
                  + "exit 1\n";
            WriteScript(Path.Combine(PlanDir, "guardrails", CheckName + Ext), body);
        }

        /// <summary>A check with no teeth: it passes whatever the workspace holds.</summary>
        public void WriteAlwaysPassingCheck() =>
            WriteScript(Path.Combine(PlanDir, "guardrails", CheckName + Ext),
                Ps ? "# catches: nothing - it can never fail\nexit 0\n"
                   : "#!/usr/bin/env bash\n# catches: nothing - it can never fail\nexit 0\n");

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
