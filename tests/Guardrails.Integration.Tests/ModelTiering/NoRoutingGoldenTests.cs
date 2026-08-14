using System.Text;
using System.Text.Json;
using Guardrails.Core.Loading;

namespace Guardrails.Integration.Tests.ModelTiering;

/// <summary>
/// DoR Invariant 7, mechanism ONE of two: the committed no-<c>routing</c> GOLDEN.
///
/// <para><b>The invariant.</b> Breaking a plan down against a config that carries no <c>routing</c>
/// block produces a folder <b>byte-identical to today</b> — the same bytes <c>/plan-breakdown</c>
/// emitted before difficulty tiering (#225) existed. SKILL.md Step 4c.1 is blunt about why this is
/// written down at length: it is the acceptance criterion most likely to be asserted and least likely
/// to be genuinely tested, because "I added the feature and gated it" reads as done long before anyone
/// diffs a real no-<c>routing</c> folder.</para>
///
/// <para><b>Why a golden AND negative assertions.</b> The resolved <c>invariant-7-proof</c> question
/// settled on BOTH, and they are not redundant. This golden catches drift <i>nobody enumerated</i> — a
/// new key, a reordered field, a file that appeared — which is precisely the failure an enumerated
/// assertion cannot anticipate. It states what must never appear only implicitly, which is the other
/// half's job (<see cref="NoRoutingNegativeAssertionTests"/>). Either alone leaves the other's failure
/// mode uncovered.</para>
///
/// <para><b>What is deterministic here, and what is not.</b> A breakdown is performed by a MODEL, so a
/// re-run cannot be byte-reproduced on a CI runner without a token spend, and an LLM re-run would
/// legitimately vary in prose and naming even if the gate held perfectly. This class therefore splits
/// the same way <c>GoldenRoundTripTests</c> already splits the M6 golden round-trip:</para>
/// <list type="bullet">
///   <item><b>Always-on (CI):</b> the golden's bytes are SEALED by <c>manifest.sha256</c> and the
///     seal is recomputed here, the golden is proven to have been generated against a genuinely
///     un-configured config, it is proven to contain every population a tier could leak into, and it
///     is loaded through the REAL <see cref="PlanLoader"/> / <see cref="PlanValidator"/> to prove the
///     RESOLVED model carries no tier either — text can only show a key is absent, the loader shows no
///     default was fabricated in its place.</item>
///   <item><b>Opt-in (never in CI):</b> point <c>GUARDRAILS_FRESH_BREAKDOWN_DIR</c> at the output of a
///     real <c>/plan-breakdown</c> over <c>input/</c> and the fresh folder is diffed against the golden
///     byte-for-byte. A difference there is a REVIEW SIGNAL for a human — an LLM re-run may vary in
///     wording — which is exactly why it is opt-in and never gates CI.</item>
/// </list>
///
/// <para>Do not "fix" a failure here by editing the golden. See the fixture README: a legitimate
/// regeneration replaces <c>expected/</c> wholesale, and the negative assertions then run over the NEW
/// bytes, so a regeneration that leaked a tier is still caught after the seal is re-recorded.</para>
/// </summary>
[Trait("Category", "ModelTieringStage1")]
public sealed class NoRoutingGoldenTests
{
    /// <summary>
    /// The whole proof is vacuous if the golden was generated against a config that had opted IN — a
    /// configured breakdown is ALLOWED to emit tiers. So the input is checked first: no <c>routing</c>
    /// block on any prompt runner, no top-level <c>tiering</c> block, in the config the breakdown ran
    /// against AND in the config it emitted.
    /// </summary>
    [Fact]
    public void Golden_WasGeneratedAgainstAConfigWithNoRoutingAndNoTieringBlock()
    {
        foreach (string configPath in new[]
                 {
                     NoRoutingGolden.InputConfigPath,
                     Path.Combine(NoRoutingGolden.GoldenPlanDir, "guardrails.json")
                 })
        {
            Assert.True(File.Exists(configPath), $"{configPath} is missing");

            using JsonDocument config = NoRoutingGolden.ParseJson(configPath);

            IReadOnlyList<string> routing = NoRoutingGolden.FindPropertyPaths(config.RootElement, "routing");
            Assert.True(routing.Count == 0,
                $"{configPath} declares a 'routing' block at {string.Join(", ", routing)} — that config " +
                "OPTS IN to tiering (SKILL.md Step 0.9), so it cannot stand as the no-routing golden's input.");

            IReadOnlyList<string> tiering = NoRoutingGolden.FindPropertyPaths(config.RootElement, "tiering");
            Assert.True(tiering.Count == 0,
                $"{configPath} declares a 'tiering' block at {string.Join(", ", tiering)} — same problem.");

            // A config with no prompt runners at all would also pass the two checks above while proving
            // nothing: there would be no runner a routing block could hang off.
            Assert.True(config.RootElement.TryGetProperty("promptRunners", out JsonElement runners) &&
                        runners.EnumerateObject().Any(),
                $"{configPath} declares no promptRunners — a config with no runner cannot demonstrate " +
                "the absence of a routing block on one.");
        }
    }

    /// <summary>
    /// The seal. Recomputes <c>manifest.sha256</c> over every file in <c>expected/</c> and compares the
    /// whole document — so a changed byte, an ADDED file and a REMOVED file all fail, which a per-file
    /// hash lookup would not. This is the half that catches drift no assertion anticipated.
    /// </summary>
    [Fact]
    public void Golden_IsByteForByteWhatWasCommitted()
    {
        Assert.True(File.Exists(NoRoutingGolden.ManifestPath),
            $"{NoRoutingGolden.ManifestPath} is missing — an unsealed golden asserts nothing.");

        string recorded = NoRoutingGolden.ReadNormalized(NoRoutingGolden.ManifestPath);
        string recomputed = NoRoutingGolden.RenderManifest(NoRoutingGolden.ExpectedDir);

        Assert.True(recorded == recomputed,
            "The no-routing golden no longer matches its seal — the breakdown output drifted." +
            Environment.NewLine + Environment.NewLine +
            DescribeManifestDrift(recorded, recomputed) +
            Environment.NewLine +
            "If the drift is a LEGITIMATE regeneration (see the fixture README), replace expected/ " +
            "wholesale and paste this as tests/Guardrails.Integration.Tests/Fixtures/no-routing-golden/" +
            "manifest.sha256:" + Environment.NewLine + Environment.NewLine + recomputed);
    }

    /// <summary>
    /// Anti-vacuity. A golden containing no prompt task, or no <c>action</c> block, would satisfy every
    /// negative assertion by being empty. This pins that the golden actually contains all three
    /// populations Step 4c could have leaked into, INCLUDING a task whose <c>action</c> block exists —
    /// so "no <c>tier</c> in an action block" is a statement about a block that is really there.
    /// </summary>
    [Fact]
    public void Golden_ContainsEveryPopulationATierCouldLeakInto()
    {
        string plan = NoRoutingGolden.GoldenPlanDir;

        Assert.True(NoRoutingGolden.PromptActions(plan).Count >= 2,
            "the golden needs at least two PROMPT tasks — the population that would carry action.tier");
        Assert.True(NoRoutingGolden.ScriptActions(plan).Count >= 1,
            "the golden needs at least one SCRIPT task — the population that stays untagged on both " +
            "sides of the gate, so tagging everything is distinguishable from tagging prompts");
        Assert.True(NoRoutingGolden.PromptJudges(plan).Count >= 1,
            "the golden needs at least one surviving prompt-judge guardrail — Step 4c.2 classifies and " +
            "REPORTS that population, so without one the 'no report line' assertion is vacuous");
        Assert.True(File.Exists(NoRoutingGolden.ReportPath),
            "the golden needs the captured breakdown report — one of the three forbidden emissions is a " +
            "report LINE, and a report nobody captured cannot be asserted against");

        int withActionBlock = NoRoutingGolden.TaskManifests(plan).Count(manifest =>
        {
            using JsonDocument task = NoRoutingGolden.ParseJson(manifest);
            return task.RootElement.TryGetProperty("action", out _);
        });
        Assert.True(withActionBlock >= 1,
            "no task.json in the golden carries an 'action' block, so 'the action block carries no tier' " +
            "would be true of a block that does not exist");
    }

    /// <summary>
    /// The RESOLVED half. Bytes can only show that a key is absent; the loader shows that nothing was
    /// substituted in its place. Loads the golden through the real <see cref="PlanLoader"/>, validates
    /// it with <see cref="PlanValidator"/>, and asserts the plan is clean, <c>Config.Tiering</c> is
    /// null (an absent block fabricates NO default) and every task's resolved
    /// <c>Action.Tier</c> is null — which is the load-time precedence chain
    /// <c>action.tier</c> &gt; <c>tiering.defaultTier</c> &gt; null bottoming out at null.
    /// </summary>
    [Fact]
    public void Golden_LoadsCleanAndResolvesNoTierAnywhere()
    {
        PlanLoadResult load = new PlanLoader().Load(NoRoutingGolden.GoldenPlanDir);
        Assert.False(load.HasErrors, Dump(load.Diagnostics));
        Assert.NotNull(load.Plan);

        IReadOnlyList<Diagnostic> validation = new PlanValidator(new EveryCommandResolves()).Validate(load.Plan!);
        Assert.DoesNotContain(validation, d => d.Severity == DiagnosticSeverity.Error);

        Assert.Null(load.Plan!.Config.Tiering);

        Assert.Equal(3, load.Plan.Tasks.Count);
        foreach (TaskNode task in load.Plan.Tasks)
        {
            Assert.True(task.Action.Tier is null,
                $"task '{task.Id}' resolved to tier '{task.Action.Tier}' from a plan that declares none — " +
                "an absent tiering block must never be substituted by a fallback.");
        }
    }

    /// <summary>
    /// The live half — skipped unless <c>GUARDRAILS_FRESH_BREAKDOWN_DIR</c> names the output of a real
    /// <c>/plan-breakdown</c> over <c>input/</c>. Diffs that folder against the golden byte-for-byte and
    /// names every file that moved.
    ///
    /// <para>A model does not reproduce prose verbatim, so a difference here is a signal for a human to
    /// read, not automatically a broken gate — which is why this never runs in CI and why the always-on
    /// proof of the gate is <see cref="NoRoutingNegativeAssertionTests"/> applied to the same fresh
    /// bytes.</para>
    /// </summary>
    [Fact]
    public void FreshBreakdown_ReproducesTheGoldenByteForByte()
    {
        string? fresh = NoRoutingGolden.FreshBreakdownDir;
        Assert.SkipUnless(fresh is not null,
            $"Set {NoRoutingGolden.FreshBreakdownDirVariable} to a folder produced by a real " +
            "/plan-breakdown over the fixture's input/ to diff it against the golden.");
        Assert.True(Directory.Exists(fresh),
            $"{NoRoutingGolden.FreshBreakdownDirVariable} points at '{fresh}', which does not exist.");

        string recomputed = NoRoutingGolden.RenderManifest(fresh!);
        string golden = NoRoutingGolden.RenderManifest(NoRoutingGolden.ExpectedDir);

        Assert.True(golden == recomputed,
            "A fresh breakdown against the no-routing config did NOT reproduce the golden byte-for-byte." +
            Environment.NewLine + Environment.NewLine + DescribeManifestDrift(golden, recomputed));
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>
    /// Turns two manifests into a per-file account — added, removed, changed — because "these two long
    /// strings differ" is not an actionable failure.
    /// </summary>
    private static string DescribeManifestDrift(string expected, string actual)
    {
        Dictionary<string, string> before = ParseManifest(expected);
        Dictionary<string, string> after = ParseManifest(actual);

        StringBuilder report = new();
        foreach (string path in after.Keys.Except(before.Keys).OrderBy(p => p, StringComparer.Ordinal))
        {
            report.AppendLine($"  ADDED    {path}");
        }

        foreach (string path in before.Keys.Except(after.Keys).OrderBy(p => p, StringComparer.Ordinal))
        {
            report.AppendLine($"  REMOVED  {path}");
        }

        foreach (string path in before.Keys.Intersect(after.Keys).OrderBy(p => p, StringComparer.Ordinal))
        {
            if (!string.Equals(before[path], after[path], StringComparison.Ordinal))
            {
                report.AppendLine($"  CHANGED  {path}");
            }
        }

        return report.Length == 0 ? "  (no per-file drift — the manifest's own formatting differs)" : report.ToString();
    }

    private static Dictionary<string, string> ParseManifest(string manifest)
    {
        Dictionary<string, string> entries = new(StringComparer.Ordinal);
        foreach (string line in manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int split = line.IndexOf("  ", StringComparison.Ordinal);
            if (split > 0)
            {
                entries[line[(split + 2)..]] = line[..split];
            }
        }

        return entries;
    }

    private static string Dump(IReadOnlyList<Diagnostic> diagnostics) =>
        string.Join(Environment.NewLine, diagnostics.Select(d => d.ToString()));
}
