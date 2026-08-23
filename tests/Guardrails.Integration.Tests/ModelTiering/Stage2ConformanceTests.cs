using System.Text.Json;
using System.Text.Json.Nodes;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;
using Guardrails.Core.Prompts;
using Guardrails.Core.State;

namespace Guardrails.Integration.Tests.ModelTiering;

/// <summary>
/// The Stage 2 REAL-SEAM conformance suite (issue #201, DoR <c>docs/plans/17-model-tiering.md</c> §6) —
/// the wave's proof that the attempt-launch path actually ROUTES, and the behaviour manifest the plan's
/// terminal gate (<c>&lt;plan&gt;/guardrails/03-dor-section-6-contract-landed.ps1</c>) discovers by
/// name.
///
/// <para><b>Every clause observes the route the way an OPERATOR does.</b> Nothing here asks the
/// resolver what it would have chosen: a suite that did would prove the resolver — which wave 1 already
/// proved, against its own unit tests — and would say nothing about whether anything CALLS it. That
/// distinction is the whole point of the wave (#382: fake-masked unit guardrails certified green while
/// the composition root was broken), so the three observation surfaces are, and stay:</para>
/// <list type="bullet">
///   <item><b><c>run.json</c>'s per-attempt <c>provenance</c></b> — the machine-readable copy of the
///     route, and where every non-log assertion is made.</item>
///   <item><b>The captured <see cref="Guardrails.Core.Prompts.PromptInvocation"/></b> — what the
///     process/CLI boundary actually received. A provenance recording a route the runner never got is
///     exactly the drift this wave removes.</item>
///   <item><b>The attempt's own log dir</b> — <c>attempt-route.log</c>, the human-facing disclosure.</item>
/// </list>
///
/// <para><b>Faking stops at the process boundary.</b> The one substitution is
/// <see cref="Guardrails.Core.Prompts.IPromptRunner"/>, via <see cref="Stage2PlanHarness"/>: no tokens,
/// no <c>claude</c> process. <see cref="PlanLoader"/>, <see cref="Scheduler"/> and
/// <see cref="TaskExecutor"/> — and whatever they call — are the SHIPPED ones.</para>
///
/// <para><b>The <c>TierResolution</c> trait is load-bearing, not decoration.</b> The plan-root
/// Integration baseline preflight excludes <c>Category!=TierResolution</c>; without it this plan's own
/// deliberately-red suite would be swept into a later run's green baseline, and "never build on red"
/// would quietly certify red.</para>
///
/// <para><b>Fixture naming rule.</b> No block name and no model string in this file may contain the
/// substring <c>easy</c>, <c>medium</c> or <c>hard</c>. Two clauses assert on the PRESENCE or ABSENCE of
/// a rung token in a disclosure file, and a runner called <c>hard-worker</c> would satisfy them without
/// a rung ever being named.</para>
/// </summary>
[Trait("Category", "TierResolution")]
public sealed class Stage2ConformanceTests
{
    /// <summary>The <c>promptRunners.default</c> pointer's block — deliberately carries NO <c>routing</c>.</summary>
    private const string Pointer = "pointer";

    /// <summary>
    /// The default pointer's model, named distinctly on purpose: every "it did NOT fall back to legacy"
    /// assertion in this file is a comparison against this exact string, so a fallback is unmistakable
    /// in the failure message rather than a subtle equality between two plausible model names.
    /// </summary>
    private const string PointerModel = "pointer-model";

    /// <summary>The route disclosure file, in the attempt's own log dir (a sibling of <c>attempt-tool-grants.log</c>).</summary>
    private const string RouteLogName = "attempt-route.log";

    /// <summary>The prefix a LOUD disclosure line carries (DoR §6.2 — a climb and a binding ceiling are both loud).</summary>
    private const string WarningPrefix = "WARNING:";

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // 1. Resolution runs per ATTEMPT, and reaches that attempt's provenance (DoR §6 / §9.3)
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolution runs immediately before EVERY attempt launch, retries included, and the route it
    /// produced is the route BOTH recorded and run — per attempt, not once per task.
    ///
    /// <para><b>Why this is asserted structurally rather than by observing a difference.</b> In v1 the
    /// resolver is a pure function of (effective tier + registry), so a once-per-task implementation
    /// would serve the same block on attempt 2 and look identical here TODAY. It is still wrong:
    /// neither input is frozen for the life of a run (a resumed run whose <c>guardrails.json</c> was
    /// edited between sessions moves an input mid-run), and the per-attempt seam is where the v2
    /// dynamic inputs — probes §6.4, the ladder §7, steering §8 — slot in. So the clause asserts the
    /// SHAPE: both attempt records carry the resolved route, and each one agrees with the model that
    /// reached the invocation for THAT SAME attempt.</para>
    ///
    /// <para><b>Which copy of the per-attempt provenance is read, and why it is not the journal's
    /// alone.</b> SSOT §7/§8 records ONE provenance object in TWO places: the <c>run.json</c> attempt
    /// record and the mirror at <c>&lt;attempt&gt;/attempt-provenance.json</c>. Only the SUCCESS settle
    /// paths hand it to the journal today — <c>AttemptJournaler.FailedAttempt</c> takes no provenance
    /// parameter at all — so the FAILED attempt has no journal provenance, and that journaler is outside
    /// the writeScope of the task that must green this clause. The route is therefore asserted on the
    /// mirror, which the attempt launcher writes for EVERY attempt from the same object, plus a
    /// requirement that the journal's copy AGREES wherever it exists. Widening <c>FailedAttempt</c> to
    /// carry provenance is a real gap in the #198 surface; it is recorded for the wave rather than
    /// quietly assumed here.</para>
    /// </summary>
    [Fact]
    public async Task Resolution_RunsPerAttempt_AndReachesAttemptProvenance()
    {
        const string taskId = "01-retried";
        const string Worker = "worker";
        const string WorkerModel = "worker-model";

        using var harness = new Stage2PlanHarness();
        Stage2RunResult run = await harness.RunAsync(new Stage2PlanSpec
        {
            DefaultRunner = Pointer,
            Runners =
            [
                PointerBlock,
                new Stage2RunnerBlock
                {
                    Name = Worker,
                    Model = WorkerModel,
                    Strength = 2,
                    Tiers = [ActionTiers.Easy, ActionTiers.Medium]
                }
            ],
            // Fails its first attempt and succeeds its second: two attempt records, two invocations.
            Tasks = [new Stage2TaskSpec { Id = taskId, Tier = ActionTiers.Easy, FailFirstAttempt = true }]
        });

        IReadOnlyList<Stage2RecordedCall> calls = run.CallsFor(taskId);
        Assert.True(
            calls.Count == 2,
            $"expected 2 action invocations for '{taskId}' (a failed first attempt and a successful " +
            $"re-attempt), saw {calls.Count} — the fixture, not the wiring, is wrong if this is the " +
            "failure.");

        Assert.True(
            run.JournalFor(taskId).Attempts.Count == 2,
            $"expected 2 journaled attempts for '{taskId}', saw {run.JournalFor(taskId).Attempts.Count}.");

        foreach (int attempt in new[] { 1, 2 })
        {
            AttemptProvenance provenance = PerAttemptProvenance(run, taskId, attempt);

            Assert.True(
                provenance.Runner == Worker,
                $"attempt {attempt}: provenance.runner is '{Describe(provenance.Runner)}', expected " +
                $"'{Worker}' — the block the rung '{ActionTiers.Easy}' resolves to. Resolution must run " +
                "before EVERY attempt launch and feed the provenance it builds (DoR §6.1/§9.3).");

            Assert.True(
                provenance.Model == WorkerModel,
                $"attempt {attempt}: provenance.model is '{Describe(provenance.Model)}', expected " +
                $"'{WorkerModel}'.");

            Assert.True(
                provenance.Tier == ActionTiers.Easy,
                $"attempt {attempt}: provenance.tier is '{Describe(provenance.Tier)}', expected " +
                $"'{ActionTiers.Easy}' — the rung that resolved. A tier recorded on attempt 1 but not on " +
                "attempt 2 is resolution hoisted to a per-TASK computation.");

            Assert.True(
                provenance.TierSource == TierSource.Task,
                $"attempt {attempt}: provenance.tierSource is '{Describe(provenance.TierSource?.ToString())}', " +
                $"expected '{TierSource.Task}' — the rung came from the task's own action.tier (D31). Read it " +
                "from ActionDefinition.TierOrigin; do NOT re-derive it by comparing action.tier to " +
                "tiering.defaultTier.");

            // The half that makes the record honest: what was RECORDED is what actually RAN.
            Stage2RecordedCall call = CallForAttempt(calls, attempt);
            Assert.True(
                call.Model == provenance.Model,
                $"attempt {attempt}: provenance records model '{Describe(provenance.Model)}' but the " +
                $"invocation that reached the runner carried '{Describe(call.Model)}'. The resolved route " +
                "must feed BOTH the provenance and the invocation — one resolution per attempt, two " +
                "consumers, never two code paths that agree only by construction.");
        }

        // The journal's copy of the SAME object must not tell a different story. Only the success settle
        // hands provenance to the journaler today, so this requires agreement wherever a copy exists
        // rather than a copy on every record.
        IReadOnlyList<AttemptRecord> journaled =
        [
            .. run.JournalFor(taskId).Attempts.Where(a => a.Provenance is not null)
        ];

        Assert.True(
            journaled.Count > 0,
            "no attempt record in run.json carries provenance at all — the machine-readable route never " +
            "reached the journal, only the per-attempt mirror.");

        foreach (AttemptRecord record in journaled)
        {
            AttemptProvenance recorded = record.Provenance!;
            Assert.True(
                recorded.Runner == Worker && recorded.Model == WorkerModel && recorded.Tier == ActionTiers.Easy,
                $"run.json's attempt {record.Attempt} records runner '{Describe(recorded.Runner)}' / model " +
                $"'{Describe(recorded.Model)}' / tier '{Describe(recorded.Tier)}', which disagrees with the " +
                $"route that ran ('{Worker}' / '{WorkerModel}' / '{ActionTiers.Easy}'). Both copies come " +
                "from ONE resolution per attempt; a divergence means there are two.");
        }
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // 2. Candidacy agrees with the ONE predicate (DoR §6.2 / D22a)
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Whatever the attempt launch routed to is a block that <see cref="PromptRunnerConfig.ServesTier"/>
    /// says serves the recorded rung — and no <c>costly: true</c> block is ever the one the harness
    /// picked.
    ///
    /// <para><b>The expected set is computed with <c>ServesTier</c> over the loaded config</b>, never by
    /// asking the resolver. That is the D22a correctness requirement made observable from OUTSIDE: if
    /// GR2048's validate-time check counted a block as serving a rung and the runtime route did not,
    /// validation would pass and every task at that rung would die at run time. The registry here mixes
    /// all four shapes — a block with no <c>routing</c> at all, one that declares only a weaker rung, the
    /// candidate that wins, and one excluded ONLY because it is <c>costly: true</c>.</para>
    /// </summary>
    [Fact]
    public async Task ResolverCandidacy_AgreesWith_ServesTier_Predicate()
    {
        const string Entry = "entry";
        const string Capable = "capable";
        const string Restrained = "restrained";
        const string Peak = "peak";

        using var harness = new Stage2PlanHarness();
        Stage2RunResult run = await harness.RunAsync(new Stage2PlanSpec
        {
            DefaultRunner = Pointer,
            Runners =
            [
                PointerBlock,                                                       // no routing: never a tier target
                new Stage2RunnerBlock { Name = Entry, Model = "entry-model", Strength = 1, Tiers = [ActionTiers.Easy] },
                new Stage2RunnerBlock { Name = Capable, Model = "capable-model", Strength = 2, Tiers = [ActionTiers.Medium, ActionTiers.Hard] },
                // Declares the rung, and is excluded ONLY for cost — the one exclusion with no override.
                new Stage2RunnerBlock { Name = Restrained, Model = "restrained-model", Strength = 3, Costly = true, Tiers = [ActionTiers.Medium, ActionTiers.Hard] },
                new Stage2RunnerBlock { Name = Peak, Model = "peak-model", Strength = 9, Tiers = [ActionTiers.Hard] }
            ],
            Tasks =
            [
                new Stage2TaskSpec { Id = "01-mid", Tier = ActionTiers.Medium },
                new Stage2TaskSpec { Id = "02-top", Tier = ActionTiers.Hard }
            ]
        });

        IReadOnlyDictionary<string, PromptRunnerConfig> registry = RegistryOf(run);

        // The fixture must be able to FAIL: a registry where everything serves every rung would make the
        // agreement assertion below vacuous.
        Assert.True(
            registry.Values.Any(b => !b.ServesTier(ActionTiers.Medium)),
            "fixture defect: every block serves 'medium', so agreeing with ServesTier proves nothing.");
        Assert.True(
            registry[Restrained].DeclaresTier(ActionTiers.Medium) && !registry[Restrained].ServesTier(ActionTiers.Medium),
            $"fixture defect: '{Restrained}' must DECLARE the rung and be excluded ONLY by the costly floor.");

        foreach (string taskId in new[] { "01-mid", "02-top" })
        {
            AttemptProvenance provenance = ProvenanceOf(run, taskId, 1);

            Assert.True(
                provenance.Tier is not null,
                $"'{taskId}': provenance.tier is absent, so there is no rung to check candidacy against — " +
                "the attempt did not resolve through routing at all.");
            Assert.True(
                provenance.Runner is not null,
                $"'{taskId}': provenance.runner is absent, so the journal never names the block that served " +
                "the attempt.");

            string rung = provenance.Tier!;
            string runner = provenance.Runner!;

            Assert.True(
                registry.ContainsKey(runner),
                $"'{taskId}': provenance names runner '{runner}', which is not a promptRunners key at all " +
                $"(the registry declares [{string.Join(", ", registry.Keys.Order(StringComparer.Ordinal))}]). " +
                "provenance.runner is the registry KEY, so a reader can go straight to the block that served " +
                "the attempt.");

            IReadOnlyList<string> serving =
            [
                .. registry.Values.Where(b => b.ServesTier(rung)).Select(b => b.Name).Order(StringComparer.Ordinal)
            ];

            Assert.True(
                serving.Contains(runner, StringComparer.Ordinal),
                $"'{taskId}': the attempt routed to block '{runner}' at rung '{rung}', but " +
                $"PromptRunnerConfig.ServesTier('{rung}') is satisfied only by [{string.Join(", ", serving)}]. " +
                "Candidacy is that ONE predicate and nothing else (D22a) — the resolver's answer and " +
                "GR2048's must never diverge.");

            Assert.True(
                registry[runner].Costly is not true,
                $"'{taskId}': the harness selected '{runner}', which is costly: true. The costly floor is a " +
                "hard floor on harness AUTONOMY (D22 / charter Decision 3) — no override, no dial. The only " +
                "paths to a costly block are a human's action.runner/action.model pin or the registry " +
                "default pointer.");
        }

        // And nothing reached the process boundary on the restrained block's model either — a route that
        // was recorded correctly but RUN on the costly model would still have spent the money.
        Assert.True(
            run.Calls.All(c => c.Model != "restrained-model"),
            $"an invocation carried '{Restrained}'s model even though the harness may never select a " +
            "costly: true block.");
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // 3. Invariant 7 — activation is PLAN-scoped, not config-scoped
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>routing</c> blocks PRESENT in the registry, the task carrying no <c>action.tier</c>, and no
    /// <c>tiering.defaultTier</c>: the run still takes the LEGACY path, with ZERO tier-resolution
    /// activity.
    ///
    /// <para><b>This is the case implementers get wrong.</b> "Routing is configured, so route" is the
    /// wrong reading — activation is PLAN-scoped (§4): a config that declares routing while no task in
    /// the plan carries a rung has not opted in to anything. The failure mode is not cosmetic: a
    /// single-model user who never asked for tiering gets routed "for uniformity", and Invariant 7 is
    /// exactly the promise that this cannot happen.</para>
    ///
    /// <para><b>Where the "no climb, no ceiling datum" half is observed.</b> The journal schema carries
    /// no climb or ceiling field — those data ride the resolution and surface in the disclosure — so
    /// their absence is asserted where they would appear: the attempt's log dir must contain no route
    /// disclosure naming a rung at all.</para>
    /// </summary>
    [Fact]
    public async Task Invariant7_RoutingEnabledConfig_ZeroTagPlan_UsesLegacyPath_WithNoTierActivity()
    {
        const string taskId = "01-untagged";
        const string Router = "router";

        using var harness = new Stage2PlanHarness();
        Stage2RunResult run = await harness.RunAsync(new Stage2PlanSpec
        {
            DefaultRunner = Pointer,
            Runners =
            [
                PointerBlock,
                // Routing is CONFIGURED, and richly so — every rung is served by this block.
                new Stage2RunnerBlock
                {
                    Name = Router,
                    Model = "router-model",
                    Strength = 5,
                    Tiers = [ActionTiers.Easy, ActionTiers.Medium, ActionTiers.Hard]
                }
            ],
            // A tiering block that is PRESENT but declares no defaultTier — the harder of the two
            // fixtures, because an implementer reaching for "is tiering configured?" finds a block here.
            IncludeTieringBlock = true,
            Tasks = [new Stage2TaskSpec { Id = taskId }]
        });

        AttemptProvenance provenance = ProvenanceOf(run, taskId, 1);

        Assert.True(
            provenance.Model == PointerModel,
            $"provenance.model is '{Describe(provenance.Model)}', expected '{PointerModel}' — " +
            $"promptRunners.{Pointer}.model, the legacy two-level fallback, byte-identical to today. An " +
            "untagged task in a routing-ENABLED config must not be routed.");

        Assert.True(
            run.CallsFor(taskId).All(c => c.Model == PointerModel),
            $"an invocation for '{taskId}' carried a model other than '{PointerModel}' — the untagged task " +
            "was routed through the registry rather than taking the legacy path.");

        Assert.True(
            provenance.Tier is null,
            $"provenance.tier is '{Describe(provenance.Tier)}' — it must be ABSENT: no rung resolved, " +
            "because the plan supplies none (D30: legacy is the no-RUNG path).");

        Assert.True(
            provenance.TierSource is null,
            $"provenance.tierSource is '{Describe(provenance.TierSource?.ToString())}' — a legacy-fallback " +
            "attempt carries NO tierSource at all (§12.4): nothing resolved and nothing was overridden. " +
            "Absent, never a null-ish token.");

        // No climb datum, no ceiling datum, no rung named anywhere in the attempt's disclosure.
        string disclosure = RouteDisclosureOrEmpty(run, taskId, 1);
        Assert.True(
            !ActionTiers.All.Any(rung => disclosure.Contains(rung, StringComparison.OrdinalIgnoreCase)),
            $"the attempt log dir's {RouteLogName} names a rung on a run with ZERO tier activity:\n" +
            $"{disclosure}\nNo rung was requested, none was served, nothing climbed and no costly ceiling " +
            "was ever computed — a sweep that never ran cannot be disclosed.");
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // 4. D30 — legacy is the no-RUNG path and nothing else
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A task WITH an effective tier whose requested rung has no candidate, where a STRONGER rung does:
    /// the resolver CLIMBS, and it never falls back to <c>promptRunners.&lt;default&gt;.model</c>.
    ///
    /// <para><b>D30 severed a disjunction that let two opposite behaviours claim one condition.</b>
    /// Through revision 4 the legacy item read "no effective tier, OR no block serves it" while §6.2/D26
    /// says the second half HALTS — proceed quietly on the runner's model, or stop and ask a human, and
    /// an implementer could satisfy either reading while failing the other's test. Once an effective
    /// tier exists, resolution OWNS the outcome: an empty candidate set climbs, and a genuinely empty
    /// registry at-or-above the rung settles <c>no-route</c>. The runner's model is sitting right
    /// there, which is precisely why this is asserted.</para>
    /// </summary>
    [Fact]
    public async Task D30_TieredPlan_ClimbsToStrongerRung_AndNeverFallsBackToLegacy()
    {
        const string taskId = "01-climber";

        using var harness = new Stage2PlanHarness();
        Stage2RunResult run = await harness.RunAsync(ClimbPlan(taskId));

        AttemptProvenance provenance = ProvenanceOf(run, taskId, 1);

        Assert.True(
            provenance.Model != PointerModel,
            $"provenance.model is '{PointerModel}' — the plan's default pointer. The task asked for " +
            $"'{ActionTiers.Medium}', so an effective tier EXISTS and legacy is unreachable (D30). The " +
            "resolver must climb to the nearest stronger rung with a candidate, never drop back to the " +
            "runner's model.");

        Assert.True(
            provenance.Tier == ActionTiers.Hard,
            $"provenance.tier is '{Describe(provenance.Tier)}', expected '{ActionTiers.Hard}' — the rung " +
            $"actually SERVED after climbing from the requested '{ActionTiers.Medium}'.");

        Assert.True(
            provenance.Tier != ActionTiers.Medium,
            $"provenance.tier still reads '{ActionTiers.Medium}' — the requested rung has no candidate, so " +
            "the served rung cannot be it.");

        Assert.True(
            provenance.Runner == Apex,
            $"provenance.runner is '{Describe(provenance.Runner)}', expected '{Apex}' — the only block " +
            $"serving '{ActionTiers.Hard}'.");

        Assert.True(
            run.CallsFor(taskId).All(c => c.Model == ApexModel),
            $"the invocation carried '{Describe(run.CallsFor(taskId).FirstOrDefault()?.Model)}', expected " +
            $"'{ApexModel}'. Recording the climb while RUNNING the legacy model would be the same silent " +
            "fallback wearing a better journal entry.");
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // 5. D31 — a full pin records tierSource "override"; legacy records no tierSource at all
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// An <c>action.runner</c> pin and an <c>action.model</c> pin each record
    /// <c>tierSource: "override"</c> with <c>provenance.tier</c> ABSENT — contrasted, in the same run,
    /// with a legacy attempt that records NO <c>tierSource</c> at all.
    ///
    /// <para><b>"Bypasses tier resolution entirely" governs what is SELECTED, not what is LOGGED</b>
    /// (§6.1 item 1, D31). §12.4's enum lists <c>override</c> and nothing else in the design emits it, so
    /// a pin is its single producer; <c>tier</c> is absent because no rung resolved. The legacy contrast
    /// is what stops "absent" and "override" from collapsing into one indistinguishable state — they are
    /// different facts about how the attempt got its model, and a reader must be able to tell them
    /// apart.</para>
    /// </summary>
    [Fact]
    public async Task D31_FullPin_RecordsTierSourceOverride_WithProvenanceTierAbsent()
    {
        const string Pinned = "pinned";
        const string PinnedModel = "pinned-model";
        const string RawPinModel = "raw-pin-model";

        using var harness = new Stage2PlanHarness();
        Stage2RunResult run = await harness.RunAsync(new Stage2PlanSpec
        {
            DefaultRunner = Pointer,
            Runners =
            [
                PointerBlock,
                new Stage2RunnerBlock { Name = Pinned, Model = PinnedModel, Strength = 2, Tiers = [ActionTiers.Medium] }
            ],
            // No defaultTier: the third task below is genuinely untagged, so it is the legacy contrast.
            Tasks =
            [
                // A pin that COEXISTS with a tier — validate warns (the tier is dead weight), and the pin
                // still wins. The tier is here precisely so "the pin won" is provable.
                new Stage2TaskSpec { Id = "01-pinned-runner", Runner = Pinned, Tier = ActionTiers.Medium },
                new Stage2TaskSpec { Id = "02-pinned-model", Model = RawPinModel },
                new Stage2TaskSpec { Id = "03-legacy" }
            ]
        });

        foreach ((string taskId, string expectedModel) in new[]
                 {
                     ("01-pinned-runner", PinnedModel),
                     ("02-pinned-model", RawPinModel)
                 })
        {
            AttemptProvenance pinnedProvenance = ProvenanceOf(run, taskId, 1);

            Assert.True(
                pinnedProvenance.TierSource == TierSource.Override,
                $"'{taskId}': provenance.tierSource is " +
                $"'{Describe(pinnedProvenance.TierSource?.ToString())}', expected " +
                $"'{TierSource.Override}'. A full pin BYPASSES selection but is still LOGGED — §12.4 gives " +
                "each v1 value exactly one producer, and a pin is override's.");

            Assert.True(
                pinnedProvenance.Tier is null,
                $"'{taskId}': provenance.tier is '{Describe(pinnedProvenance.Tier)}' — it must be ABSENT on " +
                "a pinned attempt, because no rung resolved (D31). Recording the dead action.tier here " +
                "would claim a resolution that never happened.");

            Assert.True(
                pinnedProvenance.Model == expectedModel,
                $"'{taskId}': provenance.model is '{Describe(pinnedProvenance.Model)}', expected " +
                $"'{expectedModel}' — explicit always wins (§6.1 item 1).");
        }

        // The contrast, in the same run: legacy records NO tierSource at all.
        AttemptProvenance legacy = ProvenanceOf(run, "03-legacy", 1);
        Assert.True(
            legacy.TierSource is null,
            $"'03-legacy': provenance.tierSource is '{Describe(legacy.TierSource?.ToString())}' — a " +
            "legacy-fallback attempt has no source to name (§12.4 / D30) and must carry none. If this " +
            "reads 'override', the pin and the legacy paths have been collapsed into one branch.");
        Assert.True(
            legacy.Tier is null,
            $"'03-legacy': provenance.tier is '{Describe(legacy.Tier)}' — no rung resolved.");
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // 6. The climb is RECORDED (DoR §6.2)
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A climb is legible from the JOURNAL, not only from a log line: <c>provenance.tier</c> is the rung
    /// actually SERVED, which is distinguishable from the rung the task REQUESTED.
    ///
    /// <para><b>How the pair is read.</b> §12.4's provenance carries ONE rung field and it is the served
    /// one; the requested rung is the task's own <c>action.tier</c>, which a reader already has beside
    /// the journal. So "distinguishes requested from served" means exactly this: the served rung is
    /// recorded, it differs from the requested one, and <c>tierSource</c> names the SITE the request came
    /// from. An implementation that recorded the REQUESTED rung would satisfy a naive "tier is present"
    /// check while making a climb invisible everywhere except a log line nobody greps.</para>
    /// </summary>
    [Fact]
    public async Task Climb_ToStrongerRung_IsRecordedInProvenance()
    {
        const string taskId = "01-climber";

        using var harness = new Stage2PlanHarness();
        Stage2RunResult run = await harness.RunAsync(ClimbPlan(taskId));

        AttemptProvenance provenance = ProvenanceOf(run, taskId, 1);

        Assert.True(
            provenance.Tier == ActionTiers.Hard,
            $"provenance.tier is '{Describe(provenance.Tier)}'. It must record the rung that was SERVED " +
            $"('{ActionTiers.Hard}'), not the one that was requested ('{ActionTiers.Medium}') — otherwise " +
            "the journal cannot tell a climb from an ordinary resolution.");

        Assert.True(
            provenance.TierSource == TierSource.Task,
            $"provenance.tierSource is '{Describe(provenance.TierSource?.ToString())}', expected " +
            $"'{TierSource.Task}' — the requested rung came from the task's own action.tier. The " +
            "(served rung, requesting site) pair is what makes the climb reconstructible from the record.");

        Assert.True(
            provenance.Runner == Apex,
            $"provenance.runner is '{Describe(provenance.Runner)}', expected '{Apex}' — naming the block " +
            "that actually served the climbed-to rung.");
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // 7. no-route settles needs-human BEFORE an attempt is launched (DoR §6.2 / §12.4)
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A rung whose only capable block is <c>costly: true</c> — so <c>Candidates</c> is empty at that
    /// rung and at every stronger one — settles <c>no-route</c> / needs-human, and the runner is never
    /// invoked for that task at all.
    ///
    /// <para><b>The half that matters is the negative one.</b> A no-route discovered AFTER an attempt ran
    /// on some fallback is not a no-route; it is a silent fallback wearing the name. §6.2's asymmetry is
    /// deliberate — degrade what is advisory, HALT what is load-bearing — and an actor route is
    /// load-bearing, so the harness neither reaches for the costly block nor drops to a weaker rung. It
    /// stops, and it says something the operator can act on.</para>
    /// </summary>
    [Fact]
    public async Task NoCandidateAtOrAboveRung_SettlesNoRoute_AsNeedsHuman()
    {
        const string taskId = "01-dead-rung";
        const string Restrained = "restrained";
        const string RestrainedModel = "restrained-model";

        using var harness = new Stage2PlanHarness();
        Stage2RunResult run = await harness.RunAsync(new Stage2PlanSpec
        {
            DefaultRunner = Pointer,
            Runners =
            [
                PointerBlock,                               // no routing: not a candidate at any rung
                // Declares the top rung, and is the ONLY block that does — excluded solely by the floor,
                // so Candidates('hard') is empty and there is no stronger rung to climb to.
                new Stage2RunnerBlock
                {
                    Name = Restrained,
                    Model = RestrainedModel,
                    Strength = 9,
                    Costly = true,
                    Tiers = [ActionTiers.Hard]
                }
            ],
            Tasks = [new Stage2TaskSpec { Id = taskId, Tier = ActionTiers.Hard }]
        });

        // (a) The runner was NEVER invoked — the settle happens before an attempt is launched.
        Assert.True(
            run.Calls.Count == 0,
            $"the fake runner was invoked {run.Calls.Count} time(s), but no rung at or above " +
            $"'{ActionTiers.Hard}' has a candidate. A no-route must be settled BEFORE an attempt launches; " +
            "invoking anything here means the result was turned into a fallback launch. Invocations: " +
            $"[{string.Join(", ", run.Calls.Select(c => $"{c.RunnerName}/{Describe(c.Model)}"))}].");

        // (b) The task settles needs-human, with the DISTINCT attempt outcome.
        TaskResult result = ResultFor(run, taskId);
        Assert.True(
            result.Outcome == TaskOutcome.NeedsHuman,
            $"the task settled '{result.Outcome}', expected '{TaskOutcome.NeedsHuman}'. A config gap no " +
            "retry can fix must escalate, not burn the retry budget as a generic action failure.");

        Assert.True(
            run.JournalFor(taskId).Status == Guardrails.Core.Journal.TaskStatus.NeedsHuman,
            $"run.json records status '{run.JournalFor(taskId).Status}', expected " +
            $"'{Guardrails.Core.Journal.TaskStatus.NeedsHuman}'.");

        AttemptRecord attempt = run.AttemptFor(taskId, 1);
        Assert.True(
            attempt.Outcome == AttemptOutcome.NoRoute,
            $"the attempt outcome is '{attempt.Outcome}', expected '{AttemptOutcome.NoRoute}' (the " +
            "'no-route' wire token, §12.4). It has its OWN outcome precisely so a human — and #9 triage — " +
            "sees a routing config gap rather than a generic failure.");

        Assert.True(
            ProvenanceOf(run, taskId, 1).TierSource == TierSource.Task,
            "the no-route attempt records no tierSource of 'task', so the record does not say WHERE the " +
            "unservable rung was asked for. Record provenance as usual on this path.");

        // (c) The costly block was never selected, on any surface.
        Assert.True(
            ProvenanceOf(run, taskId, 1).Runner != Restrained,
            $"provenance names '{Restrained}' as the resolved runner — the costly floor has no override " +
            "and no dial, and a no-route is not one.");

        // (d) The reason is ACTIONABLE: it names the rung and says what to change.
        string summary = result.Summary;
        Assert.True(
            summary.Contains(ActionTiers.Hard, StringComparison.OrdinalIgnoreCase),
            $"the operator-facing reason does not name the rung that could not be served " +
            $"('{ActionTiers.Hard}'):\n{summary}");
        Assert.True(
            summary.Contains("serving tier", StringComparison.OrdinalIgnoreCase),
            $"the operator-facing reason does not carry the §12.4 'register a provider serving tier >= " +
            $"<rung>' remedy:\n{summary}\n'no route' alone tells an operator nothing about what to change.");
        Assert.True(
            summary.Contains("provider", StringComparison.OrdinalIgnoreCase),
            $"the operator-facing reason never mentions a provider to register:\n{summary}");
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // 8. D28 — a BINDING costly ceiling is LOUD on re-attempt
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A stronger block DECLARES the rung but is excluded ONLY because it is <c>costly: true</c>, and the
    /// task fails its first attempt: the SECOND attempt's route disclosure carries a
    /// <c>WARNING:</c> line NAMING that block.
    ///
    /// <para><b>Why the name, and why on re-attempt.</b> Without it, a failure caused by the weaker model
    /// running out of reasoning is indistinguishable from an ordinary failure — the operator tunes
    /// prompts and budgets against a constraint they cannot see, and "some stronger block was excluded"
    /// is not actionable. The first attempt has not failed yet, so warning there would be noise on every
    /// single tiered run; the ceiling becomes news exactly when the cheaper route has already lost
    /// once.</para>
    ///
    /// <para><b>This changes what is LOGGED, never what is SELECTED.</b> Both attempts still route to the
    /// non-costly candidate — asserted here, so a "fix" that satisfies the warning by widening the floor
    /// fails this clause instead of passing it.</para>
    /// </summary>
    [Fact]
    public async Task Reattempt_BoundByCostlyCeiling_WarnsNamingTheExcludedOnlyForCostBlock()
    {
        const string taskId = "01-ceilinged";
        const string Budget = "budget";
        const string BudgetModel = "budget-model";
        const string Flagship = "flagship";

        using var harness = new Stage2PlanHarness();
        Stage2RunResult run = await harness.RunAsync(new Stage2PlanSpec
        {
            DefaultRunner = Pointer,
            Runners =
            [
                PointerBlock,
                new Stage2RunnerBlock { Name = Budget, Model = BudgetModel, Strength = 1, Tiers = [ActionTiers.Medium] },
                // Stronger, declares the SAME rung, excluded only for cost: the binding ceiling.
                new Stage2RunnerBlock
                {
                    Name = Flagship,
                    Model = "flagship-model",
                    Strength = 8,
                    Costly = true,
                    Tiers = [ActionTiers.Medium, ActionTiers.Hard]
                }
            ],
            Tasks = [new Stage2TaskSpec { Id = taskId, Tier = ActionTiers.Medium, FailFirstAttempt = true }]
        });

        Assert.True(
            run.JournalFor(taskId).Attempts.Count == 2,
            $"expected a failed first attempt and a re-attempt, saw " +
            $"{run.JournalFor(taskId).Attempts.Count} attempt(s).");

        // The floor is untouched: the weaker candidate served BOTH attempts. Read from the per-attempt
        // provenance MIRROR for the same reason clause 1 does — the failed attempt has no journal copy.
        foreach (int attempt in new[] { 1, 2 })
        {
            Assert.True(
                PerAttemptProvenance(run, taskId, attempt).Runner == Budget,
                $"attempt {attempt} routed to " +
                $"'{Describe(PerAttemptProvenance(run, taskId, attempt).Runner)}', expected '{Budget}'. D28 " +
                "changes what is LOGGED, never what is SELECTED — a warning is not a new path to a costly " +
                "model.");
        }

        // The disclosure on the RE-attempt names the block the harness was not permitted to pick.
        IReadOnlyList<string> warnings = WarningLines(RouteDisclosure(run, taskId, 2));
        Assert.True(
            warnings.Any(line => line.Contains(Flagship, StringComparison.Ordinal)),
            $"attempt 2's {RouteLogName} carries no '{WarningPrefix}' line naming '{Flagship}' — the " +
            "stronger block excluded ONLY by the costly floor (D28). Read the datum off the resolution " +
            "(CostlyCeilingBound / CostlyCeilingBlocks); re-deriving it would duplicate the candidacy " +
            $"predicate D22a forbids duplicating. Lines seen: [{string.Join(" | ", warnings)}].");

        // ...and NOT on the first attempt, which has not failed yet.
        Assert.True(
            !WarningLines(RouteDisclosureOrEmpty(run, taskId, 1)).Any(line => line.Contains(Flagship, StringComparison.Ordinal)),
            $"attempt 1's {RouteLogName} already warns about '{Flagship}'. The ceiling becomes news only " +
            "when the cheaper route has failed — a warning on the first attempt fires on every tiered run " +
            "and trains the operator to ignore it.");
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // 9. The climb is LOUD too (DoR §6.2)
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The climb's disclosure line names BOTH rungs — the one asked for and the one served.
    ///
    /// <para>§6.2 says a climb is recorded AND logged. A climb absorbed silently is a route change the
    /// operator never sees: a cost and latency change they will attribute to the prompt. Naming only one
    /// rung is not enough — "served at hard" reads as an ordinary hard task unless the request it
    /// replaced is beside it.</para>
    ///
    /// <para>The assertion is on the presence of the rung TOKENS in a <c>WARNING:</c> line, never on
    /// exact prose — a golden nobody owns is a file that gets edited to match whatever was written.
    /// (Fixture note: no block name or model in this plan contains a rung substring, so the tokens can
    /// only have come from the rungs themselves.)</para>
    /// </summary>
    [Fact]
    public async Task Climb_ToStrongerRung_EmitsLoudWarningLine()
    {
        const string taskId = "01-climber";

        using var harness = new Stage2PlanHarness();
        Stage2RunResult run = await harness.RunAsync(ClimbPlan(taskId));

        IReadOnlyList<string> warnings = WarningLines(RouteDisclosure(run, taskId, 1));

        Assert.True(
            warnings.Any(line =>
                line.Contains(ActionTiers.Medium, StringComparison.OrdinalIgnoreCase)
                && line.Contains(ActionTiers.Hard, StringComparison.OrdinalIgnoreCase)),
            $"{RouteLogName} carries no '{WarningPrefix}' line naming BOTH the requested rung " +
            $"('{ActionTiers.Medium}') and the served one ('{ActionTiers.Hard}'). §6.2: the climb is " +
            "recorded AND logged — an operator who cannot see the route change will attribute its cost to " +
            $"the prompt. Lines seen: [{string.Join(" | ", warnings)}].");
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // 10. The judge resolves through the SAME resolver, at the ACTOR's rung (DoR §6.5 rules 2–3)
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A judge guardrail that pins nothing resolves at its ACTOR's rung, through the same resolution the
    /// actor went through — and it TRACKS that rung across tasks rather than being a plan-wide constant.
    ///
    /// <para><b>Why two tasks at two rungs.</b> One task cannot tell "the judge resolved at the actor's
    /// rung" from "the judge resolved at the only rung anything serves". Two tasks at different rungs, in
    /// ONE run against ONE registry, make the tracking observable: the judges land on different blocks,
    /// and each lands on its own actor's. A judge resolved once per plan, or resolved against
    /// <c>tiering.defaultTier</c> instead of the actor's rung, gives both tasks the same answer.</para>
    ///
    /// <para><b>What it is contrasted against is today's behaviour, not a strawman.</b>
    /// <c>GuardrailRunner</c> picks a judge's block from frontmatter-or-default with no tier awareness at
    /// all, so an unwired harness sends both judges to <see cref="Pointer"/> — a block carrying no
    /// <c>routing</c>, and therefore never a tier target. That is why every clause below also asserts the
    /// judge did NOT run on <see cref="PointerModel"/>: it is the answer the wire replaces.</para>
    /// </summary>
    [Fact]
    public async Task Judge_ResolvesThroughSameResolver_AtActorsRung()
    {
        const string Entry = "entry";
        const string EntryModel = "entry-model";
        const string Mid = "mid";
        const string MidModel = "mid-model";

        using var harness = new Stage2PlanHarness();
        Stage2RunResult run = await harness.RunAsync(new Stage2PlanSpec
        {
            DefaultRunner = Pointer,
            Runners =
            [
                PointerBlock,
                new Stage2RunnerBlock { Name = Entry, Model = EntryModel, Strength = 2, Tiers = [ActionTiers.Easy] },
                new Stage2RunnerBlock { Name = Mid, Model = MidModel, Strength = 4, Tiers = [ActionTiers.Medium] }
            ],
            Tasks =
            [
                new Stage2TaskSpec { Id = "01-light", Tier = ActionTiers.Easy, JudgeGuardrail = Verdict() },
                new Stage2TaskSpec { Id = "02-weighty", Tier = ActionTiers.Medium, JudgeGuardrail = Verdict() }
            ]
        });

        foreach ((string taskId, string expected, string rung) in new[]
                 {
                     ("01-light", EntryModel, ActionTiers.Easy),
                     ("02-weighty", MidModel, ActionTiers.Medium)
                 })
        {
            // Fixture floor first, so a broken plan reads as a broken plan rather than as a routing bug.
            Stage2RecordedCall actor = ActionCall(run, taskId, 1);
            Assert.True(
                actor.Model == expected,
                $"'{taskId}': the ACTOR ran on '{Describe(actor.Model)}', expected '{expected}' — the block " +
                $"serving rung '{rung}'. The fixture, not the verifier route, is wrong if this is the failure.");

            Stage2RecordedCall judge = run.JudgeCallFor(taskId, 1);
            Assert.True(
                judge.GuardrailName == JudgeGuardrailName,
                $"'{taskId}': the captured judge call names guardrail '{Describe(judge.GuardrailName)}', " +
                $"expected '{JudgeGuardrailName}' — the prompt guardrail the harness emitted. Also a fixture " +
                "check, not a routing one.");

            Assert.True(
                judge.Model != PointerModel,
                $"'{taskId}': the judge ran on '{PointerModel}' — the promptRunners.default pointer, which " +
                "carries no routing at all and is therefore never a tier target. That is the " +
                "FRONTMATTER-OR-DEFAULT answer GuardrailRunner gives with no tier awareness; §6.5 rule 2 " +
                "says a judge resolves through the same resolver as its actor.");

            Assert.True(
                judge.Model == expected,
                $"'{taskId}': the judge ran on '{Describe(judge.Model)}', expected '{expected}' — the block " +
                $"serving the ACTOR's rung '{rung}' (§6.5 rule 2: the judge's rung IS the actor's rung, and " +
                "no bump is owed here because the actor's block declares a strength and is therefore not weak).");

            Assert.True(
                judge.RunnerName == actor.RunnerName,
                $"'{taskId}': the judge dispatched to block '{judge.RunnerName}' while the actor dispatched " +
                $"to '{actor.RunnerName}'. With no bump owed, one rung resolves to one block — a difference " +
                "here means the two sides resolved through different code.");
        }

        // The half a single-task fixture cannot state: the judge FOLLOWS the actor's rung.
        Assert.True(
            run.JudgeCallFor("01-light", 1).Model != run.JudgeCallFor("02-weighty", 1).Model,
            $"both judges ran on '{Describe(run.JudgeCallFor("01-light", 1).Model)}' even though their actors " +
            $"resolved at different rungs ('{ActionTiers.Easy}' and '{ActionTiers.Medium}'). A judge route " +
            "constant across a plan is not resolving at the actor's rung — it is reading something plan-wide " +
            "(the default pointer, or tiering.defaultTier) and calling it the actor's.");
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // 11. D24a — the weak-actor bump moves STRENGTH, at a FIXED rung
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A WEAK actor gets a strictly stronger judge — chosen from the actor's OWN rung. The rung does not
    /// move.
    ///
    /// <para><b>D24a settled a real ambiguity, and this clause is the difference between the two
    /// readings.</b> The charter says "bumped one tier ABOVE" in one place and "one strength rank ABOVE"
    /// in another; only the second is coherent, because bumping the TIER means "pretend the work is
    /// harder" — a category error that drags the judge into a rung nobody declared for this work. The
    /// registry therefore holds a stronger block at the actor's rung AND a stronger one at the rung
    /// above: a strength bump lands on the first, a tier bump on the second, and they are different model
    /// strings.</para>
    ///
    /// <para><b>Why the actor is PINNED.</b> "Weak" is <c>strength</c> when declared and the
    /// provider-kind fallback when not, so a weak block is an UNRANKED non-Claude one — and §6.2 sorts
    /// unranked LAST, so the actor path would never select one while a ranked block serves the same rung.
    /// The one configuration that puts a weak actor beside a stronger candidate is a human pinning it,
    /// which is also the story the verifier route exists for: a task pinned to a small local model,
    /// graded by something that can actually see the mistake. A pin resolves no rung of its own, so the
    /// judge's rung comes from <c>tiering.defaultTier</c> — the rung this task would have resolved at.</para>
    /// </summary>
    [Fact]
    public async Task Judge_WeakActor_StrengthBump_NotTierBump()
    {
        const string taskId = "01-weakly-actored";
        const string Unranked = "unranked";
        const string UnrankedModel = "unranked-model";
        const string Checker = "checker";
        const string CheckerModel = "checker-model";
        const string Summit = "summit";
        const string SummitModel = "summit-model";

        using var harness = new Stage2PlanHarness();
        Stage2RunResult run = await harness.RunAsync(new Stage2PlanSpec
        {
            DefaultRunner = Pointer,
            // A pin resolves no rung, so the judge's rung is the plan-wide one — the rung this task WOULD
            // have resolved at, which is what "the actor's effective rung" means for a pinned actor.
            DefaultTier = ActionTiers.Medium,
            Runners =
            [
                PointerBlock,
                // WEAK by §6.5 rule 4 + §4.1's verifier-only kind fallback: nobody ranked it, and it is not
                // a Claude block. This is the actor.
                new Stage2RunnerBlock
                {
                    Name = Unranked,
                    Model = UnrankedModel,
                    Kind = WeakProviderKind,
                    Tiers = [ActionTiers.Medium]
                },
                // Stronger, at the SAME rung — where a STRENGTH bump lands.
                new Stage2RunnerBlock { Name = Checker, Model = CheckerModel, Strength = 3, Tiers = [ActionTiers.Medium] },
                // Stronger still, one rung ABOVE — where a TIER bump would land, and must not.
                new Stage2RunnerBlock { Name = Summit, Model = SummitModel, Strength = 9, Tiers = [ActionTiers.Hard] }
            ],
            Tasks = [new Stage2TaskSpec { Id = taskId, Runner = Unranked, JudgeGuardrail = Verdict() }]
        });

        Stage2RecordedCall actor = ActionCall(run, taskId, 1);
        Assert.True(
            actor.Model == UnrankedModel,
            $"the ACTOR ran on '{Describe(actor.Model)}', expected '{UnrankedModel}' — the pinned weak " +
            "block. The fixture, not the verifier route, is wrong if this is the failure.");

        Stage2RecordedCall judge = run.JudgeCallFor(taskId, 1);

        Assert.True(
            judge.Model != UnrankedModel,
            $"the judge ran on '{UnrankedModel}' — the same weak block as its actor. Equal-and-weak is one " +
            "blind spot talking to itself, which is the exact failure §6.5 exists to prevent: a " +
            "plausible-but-wrong implementation and a plausible-but-wrong 'looks good to me' agreeing, and " +
            "the run going green over broken work.");

        Assert.True(
            judge.Model != SummitModel,
            $"the judge ran on '{SummitModel}' — the block serving '{ActionTiers.Hard}', one rung ABOVE the " +
            $"actor's '{ActionTiers.Medium}'. That is a TIER bump, which D24a forbids: bumping the rung says " +
            "'pretend the work is harder', contradicting the difficulty-is-not-strength split and dragging " +
            "the judge into a rung nobody declared for this work. A tier bump satisfies a naive 'the judge " +
            "is stronger' check, which is why it is asserted against by name.");

        Assert.True(
            judge.Model != PointerModel,
            $"the judge ran on '{PointerModel}' — the promptRunners.default pointer, i.e. the " +
            "frontmatter-or-default block an unwired GuardrailRunner picks. No bump happened at all.");

        Assert.True(
            judge.Model == CheckerModel,
            $"the judge ran on '{Describe(judge.Model)}', expected '{CheckerModel}' — the WEAKEST candidate " +
            $"at the ACTOR's OWN rung ('{ActionTiers.Medium}') whose strength strictly exceeds the actor's " +
            "(§6.5 rule 3 / D24a). The bump moves along strength; the rung is fixed.");

        // The bump moves the JUDGE. It is not a licence to re-route the work itself.
        Assert.True(
            run.ActionCallsFor(taskId).All(c => c.Model == UnrankedModel),
            $"an ACTION invocation carried a model other than the pinned '{UnrankedModel}'. §6.5 changes who " +
            "VOUCHES for the work, never who does it — explicit still wins on the actor side (§6.1 item 1).");
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // 12. §6.5 rule 5 — the judge DEGRADES and the run PROCEEDS
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A weak actor wants a stronger judge and every block that would satisfy it is <c>costly: true</c>:
    /// the judge STAYS on the actor's route, and <b>the run proceeds</b>.
    ///
    /// <para><b>The run-proceeds half is the whole clause.</b> The ACTOR in this exact situation HALTS —
    /// wave 2's no-route clause asserts that, on the same input shape — and §6.2's asymmetry is
    /// deliberate: degrade what is advisory, halt what is load-bearing. An implementation that reused the
    /// actor's no-route settle for the verifier would satisfy "it did not reach the costly block" while
    /// turning a model-quality opinion into something that can stop a run, which §12.6 forbids outright.</para>
    ///
    /// <para><b>And it must not reach the costly block.</b> The costly floor is a hard floor on harness
    /// AUTONOMY with no override and no dial; "the judge would be better" is not one. Both halves are
    /// asserted, because either alone has a cheap wrong implementation that passes it.</para>
    /// </summary>
    [Fact]
    public async Task Judge_OnlyStrongerBlockIsCostly_DegradesAndProceeds()
    {
        const string taskId = "01-degraded";
        const string Unranked = "unranked";
        const string UnrankedModel = "unranked-model";
        const string Flagship = "flagship";
        const string FlagshipModel = "flagship-model";

        using var harness = new Stage2PlanHarness();
        Stage2RunResult run = await harness.RunAsync(new Stage2PlanSpec
        {
            DefaultRunner = Pointer,
            DefaultTier = ActionTiers.Medium,
            Runners =
            [
                PointerBlock,
                // The weak, pinned actor.
                new Stage2RunnerBlock
                {
                    Name = Unranked,
                    Model = UnrankedModel,
                    Kind = WeakProviderKind,
                    Tiers = [ActionTiers.Medium]
                },
                // The ONLY block that could satisfy the bump — and it is reserved.
                new Stage2RunnerBlock
                {
                    Name = Flagship,
                    Model = FlagshipModel,
                    Strength = 8,
                    Costly = true,
                    Tiers = [ActionTiers.Medium]
                }
            ],
            Tasks = [new Stage2TaskSpec { Id = taskId, Runner = Unranked, JudgeGuardrail = Verdict() }]
        });

        Stage2RecordedCall judge = run.JudgeCallFor(taskId, 1);

        Assert.True(
            judge.Model != FlagshipModel,
            $"the judge ran on '{FlagshipModel}', a costly: true block. The costly floor is a hard floor on " +
            "harness AUTONOMY (D22 / charter Decision 3) — no override, no dial — and wanting a stronger " +
            "judge is not an exception to it. The only sanctioned routes to a costly block are a human's " +
            "pin and the registry default pointer.");

        Assert.True(
            judge.Model != PointerModel,
            $"the judge ran on '{PointerModel}' — the frontmatter-or-default block. §6.5 rule 5 says a " +
            "refused bump leaves the judge at the ACTOR's route, not at the plan's default pointer.");

        Assert.True(
            judge.Model == UnrankedModel,
            $"the judge ran on '{Describe(judge.Model)}', expected '{UnrankedModel}' — the ACTOR's own " +
            "route. §6.5 rule 5: the bump obeys the costly floor, so when the only stronger block is " +
            "reserved the judge stays exactly where the actor is and the advisory carries the finding.");

        // The half that separates the verifier rule from the actor rule: NOTHING HALTED.
        TaskResult result = ResultFor(run, taskId);
        Assert.True(
            result.Outcome == TaskOutcome.Succeeded,
            $"the task settled '{result.Outcome}', expected '{TaskOutcome.Succeeded}'. A verifier preference " +
            "that cannot be satisfied DEGRADES and the run proceeds (§6.5 rule 5); the ACTOR halts on the " +
            $"same input (§6.2, invariant 5), and that asymmetry is the design. Summary: {result.Summary}");

        Assert.True(
            run.JournalFor(taskId).Status == Guardrails.Core.Journal.TaskStatus.Succeeded,
            $"run.json records status '{run.JournalFor(taskId).Status}', expected " +
            $"'{Guardrails.Core.Journal.TaskStatus.Succeeded}' — §12.6: no verifier condition may ever fail " +
            "a build, so an unbumpable judge can never reach needs-human.");

        Assert.True(
            run.AttemptFor(taskId, 1).Outcome != AttemptOutcome.NoRoute,
            $"the attempt recorded '{AttemptOutcome.NoRoute}'. There is deliberately no no-route outcome on " +
            "the verifier side: a judge that could not be improved is a warning, never an outcome the " +
            "scheduler can halt on.");
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // 13. D29 — a pinned COSTLY actor licenses a costly judge bump; the default pointer does not
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// When the ACTOR is pinned to a <c>costly</c> block a human has already authorized costly spend for
    /// that task, so the judge MAY bump into a costly block. When the pin names a NON-costly block the
    /// same registry — whose <c>default</c> pointer is itself costly — buys no such licence.
    ///
    /// <para><b>Both halves in one run, against one registry, on purpose.</b> D29 is narrow enough that
    /// stating only the permissive half invites the over-broad implementation — "the actor's block is
    /// costly ⇒ licensed", dropping the pin — which reads the plan-wide <c>default</c> pointer as
    /// sanction and silently licenses costly judges across an entire plan. The registry is built so
    /// exactly that mistake is visible: the <c>default</c> pointer IS a costly block, so an
    /// implementation treating "a costly block is in play" as authorization bumps the second task's judge
    /// into the costly one too, and fails here.</para>
    ///
    /// <para><b>The licence widens candidacy; it does not lower the floor.</b> The floor constrains the
    /// harness CHOOSING, never the human ASSIGNING — and here the human assigned. The shape it produces
    /// is the one the verifier route exists for: pin a frontier actor and get a judge strong enough to
    /// vouch for it, instead of a weaker judge rubber-stamping the strongest actor in the run.</para>
    /// </summary>
    [Fact]
    public async Task Judge_PinnedCostlyActor_MayBumpIntoCostly_D29()
    {
        const string Sanctioned = "sanctioned";
        const string SanctionedModel = "sanctioned-model";
        const string Frontier = "frontier";
        const string FrontierModel = "frontier-model";
        const string Unranked = "unranked";
        const string UnrankedModel = "unranked-model";

        using var harness = new Stage2PlanHarness();
        Stage2RunResult run = await harness.RunAsync(new Stage2PlanSpec
        {
            // The plan-wide fallback NAMES A COSTLY BLOCK. Legal, and the trap: it is not a decision about
            // any particular task, so it can never be the sanction D29 turns on.
            DefaultRunner = Sanctioned,
            DefaultTier = ActionTiers.Medium,
            Runners =
            [
                new Stage2RunnerBlock
                {
                    Name = Sanctioned,
                    Model = SanctionedModel,
                    Kind = WeakProviderKind,
                    Costly = true,
                    Tiers = [ActionTiers.Medium]
                },
                // The only block strong enough to satisfy a bump — and it is costly.
                new Stage2RunnerBlock
                {
                    Name = Frontier,
                    Model = FrontierModel,
                    Strength = 9,
                    Costly = true,
                    Tiers = [ActionTiers.Medium]
                },
                // Weak, and NOT costly: pinning it authorizes nothing.
                new Stage2RunnerBlock
                {
                    Name = Unranked,
                    Model = UnrankedModel,
                    Kind = WeakProviderKind,
                    Tiers = [ActionTiers.Medium]
                }
            ],
            Tasks =
            [
                new Stage2TaskSpec { Id = "01-costly-pin", Runner = Sanctioned, JudgeGuardrail = Verdict() },
                new Stage2TaskSpec { Id = "02-ordinary-pin", Runner = Unranked, JudgeGuardrail = Verdict() }
            ]
        });

        // Fixture floor: each actor ran where it was pinned.
        Assert.True(
            ActionCall(run, "01-costly-pin", 1).Model == SanctionedModel
            && ActionCall(run, "02-ordinary-pin", 1).Model == UnrankedModel,
            "an ACTOR did not run on its pinned block — the fixture, not D29, is the failure here " +
            $"('01-costly-pin' ran on '{Describe(ActionCall(run, "01-costly-pin", 1).Model)}', " +
            $"'02-ordinary-pin' on '{Describe(ActionCall(run, "02-ordinary-pin", 1).Model)}').");

        // (a) The carve-out fires: a human already paid for costly on THIS task.
        Stage2RecordedCall sanctionedJudge = run.JudgeCallFor("01-costly-pin", 1);
        Assert.True(
            sanctionedJudge.Model == FrontierModel,
            $"'01-costly-pin': the judge ran on '{Describe(sanctionedJudge.Model)}', expected " +
            $"'{FrontierModel}'. This actor is pinned to a costly block, so costly spend for this task is " +
            "already authorized and the judge MAY bump into a costly: true block (D29) — no halt, no " +
            "further prompt. Without the carve-out this degrades, and a weak judge rubber-stamps the most " +
            "expensive actor in the run, which is the shape §6.5 exists to prevent.");

        // (b) The carve-out is NARROW: a costly default pointer is not sanction.
        Stage2RecordedCall ordinaryJudge = run.JudgeCallFor("02-ordinary-pin", 1);
        Assert.True(
            ordinaryJudge.Model != FrontierModel,
            $"'02-ordinary-pin': the judge bumped into the costly '{FrontierModel}' even though this task's " +
            $"actor is pinned to the NON-costly '{Unranked}'. The only costly thing in play here is the " +
            "plan-wide promptRunners.default pointer, and D29 says that does NOT trigger it: a plan-wide " +
            "fallback is not a decision about this task, and reading it as sanction silently licenses " +
            "costly judges across an entire plan. The licence is 'the ACTOR was PINNED and that block is " +
            "costly' — both conjuncts, not either.");

        Assert.True(
            ordinaryJudge.Model == UnrankedModel,
            $"'02-ordinary-pin': the judge ran on '{Describe(ordinaryJudge.Model)}', expected " +
            $"'{UnrankedModel}' — the actor's own route. With no licence the bump is refused by the costly " +
            "floor, so §6.5 rule 5 degrades: the judge stays where the actor is and the advisory carries " +
            "the finding.");
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // 14. §6.5.1 — the verifier floor RAISES, and only raises
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>tiering.verifier.minTier</c> lifts a judge that resolved BELOW it, and leaves one that resolved
    /// at or above it completely alone.
    ///
    /// <para><b>The two halves are the difference between a floor and a default</b>, which is the
    /// distinction that settled this knob's design. A DEFAULT replaces the rule: a plan-wide
    /// <c>medium</c> would drag a <c>hard</c> judge DOWN, and a single-task fixture asserting only "the
    /// judge ended up at minTier" cannot tell the two designs apart — it passes against both. So the run
    /// carries a task BELOW the floor (which must rise) and one ABOVE it (which must not move), against
    /// one registry; the second is the half that actually discriminates.</para>
    ///
    /// <para><b>The floor governs the JUDGE, never the actor</b> — asserted too, because raising both
    /// satisfies the judge-side assertion while re-routing real work onto a rung nobody asked for.</para>
    /// </summary>
    [Fact]
    public async Task Judge_VerifierMinTier_RaisesNeverLowers()
    {
        using var harness = new Stage2PlanHarness();
        Stage2RunResult run = await harness.RunAsync(VerifierFloorPlan());

        // ── The floor RAISES: the easy task's judge is lifted to the floor's rung ──────────────
        Stage2RecordedCall lightActor = ActionCall(run, FloorRaisedTask, 1);
        Assert.True(
            lightActor.Model == FloorEntryModel,
            $"'{FloorRaisedTask}': the ACTOR ran on '{Describe(lightActor.Model)}', expected " +
            $"'{FloorEntryModel}'. Fixture check — the floor is about the judge, and this is the unraised " +
            "baseline it is measured against.");

        Stage2RecordedCall lightJudge = run.JudgeCallFor(FloorRaisedTask, 1);
        Assert.True(
            lightJudge.Model != PointerModel,
            $"'{FloorRaisedTask}': the judge ran on '{PointerModel}' — the frontmatter-or-default block. The " +
            "floor never entered the picture, because nothing resolved.");

        Assert.True(
            lightJudge.Model == FloorMidModel,
            $"'{FloorRaisedTask}': the judge ran on '{Describe(lightJudge.Model)}', expected " +
            $"'{FloorMidModel}'. Steps 2–3 resolve this judge at the actor's rung ('{ActionTiers.Easy}'), " +
            $"which is BELOW tiering.verifier.minTier ('{ActionTiers.Medium}') — so §6.5.1 raises the rung " +
            "to the floor and RE-SELECTS from that rung's candidates. 'Never verify anything with less than " +
            "a medium judge, however trivial the task looked' is the whole policy, and it is reachable in a " +
            "purely static run because the judge's tier varies ACROSS TASKS even where it cannot vary " +
            "across attempts.");

        Assert.True(
            lightJudge.Model != lightActor.Model,
            $"'{FloorRaisedTask}': the judge ran on the actor's own model " +
            $"('{Describe(lightActor.Model)}') — the floor moved nothing.");

        // ── The floor NEVER LOWERS: the hard task's judge is untouched ─────────────────────────
        Stage2RecordedCall heavyJudge = run.JudgeCallFor(FloorUntouchedTask, 1);
        Assert.True(
            heavyJudge.Model != FloorMidModel,
            $"'{FloorUntouchedTask}': the judge ran on '{FloorMidModel}' — the floor's own rung — even " +
            $"though it resolved at '{ActionTiers.Hard}', ABOVE the floor. That is a DEFAULT, not a floor: " +
            "it replaced the rule instead of refusing a result that came out too low. A plan-wide knob that " +
            "can drag a hard judge down is strictly worse than no knob at all, and a single-task fixture " +
            "cannot tell the two designs apart — which is why this half exists.");

        Assert.True(
            heavyJudge.Model == FloorTopModel,
            $"'{FloorUntouchedTask}': the judge ran on '{Describe(heavyJudge.Model)}', expected " +
            $"'{FloorTopModel}' — the block serving the actor's own rung. A result at or above minTier is " +
            "UNTOUCHED; the floor only ever raises.");

        // The floor is a constraint on VERIFICATION. It must not re-route the work.
        Assert.True(
            run.ActionCallsFor(FloorRaisedTask).All(c => c.Model == FloorEntryModel)
            && run.ActionCallsFor(FloorUntouchedTask).All(c => c.Model == FloorTopModel),
            "an ACTION invocation moved when tiering.verifier.minTier was applied. The verifier floor bounds " +
            "how weak the JUDGE may be; the costly floor bounds what the harness may choose for the ACTOR. " +
            "They constrain different axes and never contend — raising the actor here spends more on every " +
            "task in the plan to satisfy a verification policy.");
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // 15. §12.4 — the judge object SURVIVES to run.json, on BOTH record paths
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// After a real run the resolved verifier route is READ BACK OUT of <c>run.json</c> as §12.4's
    /// <c>provenance.judge</c> object — on the SERIAL record path and on the WORKTREE-SETTLE one.
    ///
    /// <para><b>Why this is asserted on the journal file rather than on the returned result.</b> A judge
    /// object computed, assigned to something in memory, and never serialized would satisfy any assertion
    /// made against the harness's own return value. That is not hypothetical: this repo shipped
    /// <c>AttemptRecord.Usage</c> declared, READ by the per-tier spend aggregation, and assigned by none
    /// of the record-construction sites — structurally dead, with every guardrail green (#475). The
    /// journal is parsed back off disk here for exactly that reason.</para>
    ///
    /// <para><b>And why BOTH paths.</b> A succeeded attempt's record is built in two places: the serial
    /// <c>AttemptJournaler.CompleteSucceededOrInvalidFragment</c>, and the Scheduler's deferred B1 settle
    /// (<c>RecordSucceededSettle</c>) — which is the mode a real worktree run takes. A field threaded
    /// through only the first is not half-delivered, it is INVISIBLE to nearly every user. D32 is the
    /// answer (the judge hangs off <c>provenance</c>, the one member already riding both paths), and this
    /// clause is what makes D32 checkable rather than merely stated.</para>
    ///
    /// <para><b>The fixture is the floor-raised one on purpose</b>: the judge's block, model and rung all
    /// differ from the actor's, so a <c>judge</c> object that merely echoed the actor's route — the
    /// cheapest wrong implementation producing a non-null object — fails here.</para>
    /// </summary>
    [Fact]
    public async Task Judge_ProvenanceReachesRunJson_BothPaths()
    {
        using var harness = new Stage2PlanHarness();
        Stage2RunResult serial = await harness.RunAsync(VerifierFloorPlan());
        using Stage2DeferredSettleRun deferred =
            await Stage2DeferredSettleRun.RunAsync(harness.PlanRoot, VerifierFloorPlan());

        // BOTH receipts first, before a single content assertion: an assertion that fails on the serial
        // run would otherwise mask a second host that never took the path it claims, and "both paths"
        // would quietly be one path asserted twice.
        AssertRecordPath(serial, FloorRaisedTask, deferredSettle: false);
        AssertRecordPath(deferred.Result, FloorRaisedTask, deferredSettle: true);
        Assert.True(
            deferred.Integrations > 0,
            $"the deferred-settle run integrated {deferred.Integrations} segment(s) — only the B1 settle " +
            "integrates, so a zero here means the run took the serial record path after all.");

        foreach ((Stage2RunResult run, bool deferredSettle) in new[] { (serial, false), (deferred.Result, true) })
        {
            string path = RecordPathLabel(deferredSettle);

            // Fixture floor: the wiring under test must actually have run the judge somewhere distinct.
            Stage2RecordedCall judgeCall = run.JudgeCallFor(FloorRaisedTask, 1);
            Assert.True(
                judgeCall.Model == FloorMidModel,
                $"[{path}] the judge RAN on '{Describe(judgeCall.Model)}', expected '{FloorMidModel}'. The " +
                "route is not wired yet, so there is no resolved judge for the journal to carry — fix the " +
                "resolution before the record.");

            AttemptJudge judge = JudgeRecordOf(run, FloorRaisedTask, 1, path);

            Assert.True(
                judge.Model == FloorMidModel,
                $"[{path}] run.json records judge.model '{Describe(judge.Model)}', but the invocation that " +
                $"reached the runner carried '{Describe(judgeCall.Model)}'. One resolution, two consumers — " +
                "a record that disagrees with what ran is the drift this wave removes.");

            Assert.True(
                judge.Runner == FloorMid,
                $"[{path}] run.json records judge.runner '{Describe(judge.Runner)}', expected '{FloorMid}' " +
                "— the promptRunners KEY, exactly as provenance.runner records it for the actor, so a reader " +
                "can go straight to the block that graded the work.");

            Assert.True(
                judge.Tier == ActionTiers.Medium,
                $"[{path}] run.json records judge.tier '{Describe(judge.Tier)}', expected " +
                $"'{ActionTiers.Medium}' — the rung the JUDGE resolved at once the floor raised it, which is " +
                $"a different question from the rung the ACTOR ran at ('{ActionTiers.Easy}').");

            Assert.False(
                judge.Bumped,
                $"[{path}] run.json records judge.bumped 'True'. The actor's block declares a strength and " +
                "is therefore not weak, so no rule-3 bump was owed — and recording that as a real false is " +
                "the point: 'a judge resolved and no bump was needed' is a measurement, where an absent key " +
                "is indistinguishable from 'no judge resolved at all'. The spend report aggregates this " +
                "datum, and a denominator that silently drops its zeroes is not an answer.");

            // The actor's own route sits beside it, UNCHANGED — the judge is a second route recorded, never
            // a rewrite of the first.
            AttemptProvenance provenance = ProvenanceOf(run, FloorRaisedTask, 1);
            Assert.True(
                provenance.Model == FloorEntryModel && provenance.Tier == ActionTiers.Easy,
                $"[{path}] the ACTOR's provenance reads model '{Describe(provenance.Model)}' / tier " +
                $"'{Describe(provenance.Tier)}', expected '{FloorEntryModel}' / '{ActionTiers.Easy}'. The " +
                "judge hangs OFF the provenance (D32); it does not overwrite it.");
        }
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // 16. §6.5 — a WEAK judge records an advisory; an equal-and-strong one records none
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The per-attempt JIT re-check records <c>judge.advisory</c> in provenance when the resolved judge is
    /// weak, and records NOTHING when it is equal-and-strong.
    ///
    /// <para><b>Both polarities, because either alone has a trivially wrong implementation.</b> Asserting
    /// only that an advisory appeared passes against a rule that flags every judge in every run — worse
    /// than silence, since three surfaces already report this one condition and the whole de-duplication
    /// ruling exists to stop it becoming noise people learn to ignore. Asserting only that a strong judge
    /// is silent passes against a rule that never fires at all. The two tasks run together, against one
    /// registry.</para>
    ///
    /// <para><b>Weakness is §6.5 rule 4's one predicate:</b> <c>strength</c> where the operator declared
    /// it, and the verifier-only provider-kind fallback where they did not (<c>kind != "claude"</c> ⇒
    /// weak-unless-declared). So the weak side is an unranked non-Claude block and the strong side a
    /// ranked one — the operator ranked it, so nothing is guessed. Equal-and-strong needs no advisory:
    /// Opus judging Opus is a real check.</para>
    ///
    /// <para><b>It is an ADVISORY, never a halt and never a diagnostic code</b> — a code is a thing that
    /// can fail a build, and the harness does not block on a model-quality opinion. Both tasks are
    /// therefore asserted green.</para>
    /// </summary>
    [Fact]
    public async Task Judge_WeakVerifier_AdvisoryRecorded_EqualAndStrongNot()
    {
        const string weakTask = "01-unranked-judge";
        const string strongTask = "02-ranked-judge";
        const string Unranked = "unranked";
        const string UnrankedModel = "unranked-model";
        const string Ranked = "ranked";
        const string RankedModel = "ranked-model";

        using var harness = new Stage2PlanHarness();
        Stage2RunResult run = await harness.RunAsync(new Stage2PlanSpec
        {
            DefaultRunner = Pointer,
            Runners =
            [
                PointerBlock,
                // Nobody ranked it and it is not a Claude block ⇒ WEAK by the rule-4 fallback. It is the
                // only block serving its rung, so the actor and its judge both land here.
                new Stage2RunnerBlock
                {
                    Name = Unranked,
                    Model = UnrankedModel,
                    Kind = WeakProviderKind,
                    Tiers = [ActionTiers.Easy]
                },
                // Ranked, so weakness is decided by the number and never guessed: not weak.
                new Stage2RunnerBlock { Name = Ranked, Model = RankedModel, Strength = 4, Tiers = [ActionTiers.Medium] }
            ],
            Tasks =
            [
                new Stage2TaskSpec { Id = weakTask, Tier = ActionTiers.Easy, JudgeGuardrail = Verdict() },
                new Stage2TaskSpec { Id = strongTask, Tier = ActionTiers.Medium, JudgeGuardrail = Verdict() }
            ]
        });

        // Fixture floor: each judge resolved where this clause needs it, so an advisory failure is about
        // the advisory rather than about the route.
        Assert.True(
            run.JudgeCallFor(weakTask, 1).Model == UnrankedModel
            && run.JudgeCallFor(strongTask, 1).Model == RankedModel,
            $"the judges ran on '{Describe(run.JudgeCallFor(weakTask, 1).Model)}' and " +
            $"'{Describe(run.JudgeCallFor(strongTask, 1).Model)}', expected '{UnrankedModel}' and " +
            $"'{RankedModel}'. The verifier route is not resolving yet, so there is no weakness verdict to " +
            "record — wire §6.5 before the advisory.");

        AttemptJudge weakJudge = JudgeRecordOf(run, weakTask, 1, "weak");
        Assert.True(
            !string.IsNullOrWhiteSpace(weakJudge.Advisory),
            $"'{weakTask}': run.json records judge.advisory '{Describe(weakJudge.Advisory)}' for a judge that " +
            $"resolved to '{Unranked}' — a block nobody ranked, on a non-Claude provider, which §6.5 rule 4 " +
            "reads as WEAK. The JIT re-check records the finding in provenance on EVERY such attempt (the " +
            "quieter LOG line is the separate surface, and the run summary aggregates from here) — so an " +
            "advisory computed and never landed in run.json is a finding nobody ever reads.");

        AttemptJudge strongJudge = JudgeRecordOf(run, strongTask, 1, "equal-and-strong");
        Assert.True(
            strongJudge.Advisory is null,
            $"'{strongTask}': run.json records judge.advisory '{Describe(strongJudge.Advisory)}' for a judge " +
            $"on '{Ranked}', a block the operator RANKED which grades work at its own rung. Equal-and-strong " +
            "needs no bump and no finding — flagging it fires the advisory on every correctly-configured " +
            "run, which trains people to ignore the one case that matters. Absent, never an empty string " +
            "and never a 'none' token.");

        // An advisory is an advisory: three surfaces report it and none may fail a build (§12.6).
        foreach (string taskId in new[] { weakTask, strongTask })
        {
            Assert.True(
                ResultFor(run, taskId).Outcome == TaskOutcome.Succeeded,
                $"'{taskId}' settled '{ResultFor(run, taskId).Outcome}', expected '{TaskOutcome.Succeeded}'. " +
                "A weak judge is a #229 review finding, a preflight line and a per-attempt re-check — never " +
                "a hard error, never a load-time refusal and never a halt, in attended or unattended mode.");
        }
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // 17. #475 — attempt USAGE reaches run.json with real numbers, on BOTH record paths
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A prompt attempt's token volume arrives in <c>run.json</c> as <c>attempt.usage</c>, with STRICTLY
    /// POSITIVE input and output counts — on the serial record path and on the worktree-settle one.
    ///
    /// <para><b>This clause exists because the field already shipped dead.</b> <c>AttemptRecord.Usage</c>
    /// is declared, is READ by the per-tier spend aggregation, and is assigned by NONE of the record
    /// construction sites: the runner parses the counts, they reach <c>PromptResult.Usage</c>, and they
    /// stop (#475). Every guardrail in the wave that shipped it was green, because a structural check can
    /// see a member and cannot see that nothing ever fills it. The fake runner here REPORTS the counts —
    /// <see cref="ReportedUsage"/> on a real <see cref="PromptResult"/> — so everything between the
    /// process boundary and the journal is the shipped code.</para>
    ///
    /// <para><b>Strictly positive, never merely non-null — and that is not pedantry.</b> The spend
    /// aggregation's own <c>anyUsage</c> flag stays FALSE for an all-zero total, so a report built on
    /// zeroed usage is indistinguishable from one built on absent usage. An assertion satisfied by
    /// <c>{ 0, 0 }</c> would therefore pass against the exact bug it exists to close.</para>
    ///
    /// <para><b>The tokens axis is not a duplicate of the cost axis.</b> On a costless provider — a local
    /// endpoint, a flat-rate subscription — <c>0</c> is the honest cost, and volume is then the only
    /// evidence of what an attempt actually did.</para>
    /// </summary>
    [Fact]
    public async Task Attempt_UsageTokensReachRunJson_BothPaths()
    {
        using var harness = new Stage2PlanHarness();
        Stage2RunResult serial = await harness.RunAsync(MeteredPlan());
        using Stage2DeferredSettleRun deferred =
            await Stage2DeferredSettleRun.RunAsync(harness.PlanRoot, MeteredPlan());

        // BOTH receipts first — see the sibling clause: a serial failure must not mask a second host that
        // never took the deferred path, or "both paths" is one path asserted twice.
        AssertRecordPath(serial, MeteredTask, deferredSettle: false);
        AssertRecordPath(deferred.Result, MeteredTask, deferredSettle: true);
        Assert.True(
            deferred.Integrations > 0,
            $"the deferred-settle run integrated {deferred.Integrations} segment(s) — only the B1 settle " +
            "integrates, so a zero here means the run took the serial record path after all.");

        foreach ((Stage2RunResult run, bool deferredSettle) in new[] { (serial, false), (deferred.Result, true) })
        {
            string path = RecordPathLabel(deferredSettle);

            Assert.True(
                ResultFor(run, MeteredTask).Outcome == TaskOutcome.Succeeded,
                $"[{path}] '{MeteredTask}' settled '{ResultFor(run, MeteredTask).Outcome}' — the fixture must " +
                "reach a SUCCEEDED attempt record for there to be a usage field to read at all. Summary: " +
                $"{ResultFor(run, MeteredTask).Summary}");

            AttemptRecord attempt = run.AttemptFor(MeteredTask, 1);

            Assert.True(
                attempt.Usage is not null,
                $"[{path}] run.json's attempt record carries NO usage at all, though the runner reported " +
                $"{ReportedUsage.InputTokens} input / {ReportedUsage.OutputTokens} output tokens. The counts " +
                "reach PromptResult; the carry from there to the attempt record is what is missing (#475). " +
                "The same record does carry costUsd " +
                $"('{Describe(attempt.CostUsd?.ToString(System.Globalization.CultureInfo.InvariantCulture))}'), " +
                "so this is not an attempt that reported nothing.");

            AttemptUsage usage = attempt.Usage!;

            Assert.True(
                usage.InputTokens > 0,
                $"[{path}] run.json records usage.inputTokens '{usage.InputTokens}'. It must be STRICTLY " +
                $"POSITIVE — the runner reported {ReportedUsage.InputTokens}. A zero here is not a smaller " +
                "number, it is the same signal as an absent one: the per-tier spend line's anyUsage flag " +
                "stays false for an all-zero total, so a zeroed record reports exactly what a dead field " +
                "reports.");

            Assert.True(
                usage.OutputTokens > 0,
                $"[{path}] run.json records usage.outputTokens '{usage.OutputTokens}'. It must be STRICTLY " +
                $"POSITIVE — the runner reported {ReportedUsage.OutputTokens}. Output tokens are the half a " +
                "cache-inflated input count cannot stand in for.");
        }
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // 18. #349 — the OBSERVED model is the provenance model, and a mismatch names the request
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A run whose runner reports a model DIFFERENT from the one the route asked for records the OBSERVED
    /// value as <c>provenance.model</c> — on the serial record path and on the worktree-settle one.
    ///
    /// <para><b>The settled shape is best-known-actual:</b> <c>observed ?? route ?? sentinel</c>. Every
    /// existing reader of <c>provenance.model</c> — the spend report, the log viewer, a human opening
    /// <c>run.json</c> — improves with no change on its side, because the field goes on answering the same
    /// question ("what did this attempt run on") with a better answer wherever one exists.</para>
    ///
    /// <para><b>Why the observed value is the stronger fact.</b> The route is a REQUEST, and a request is
    /// not a receipt: an alias resolves to a dated snapshot, a provider substitutes, and the zero-setup
    /// user passes no <c>--model</c> at all — for whom the harness records the <c>"(cli default)"</c>
    /// sentinel, a string that names no model whatsoever. What the runner echoes back is the answer.</para>
    ///
    /// <para><b>Read back off DISK, never off the harness's own return value.</b> A datum computed,
    /// assigned to something in memory and never serialized satisfies any assertion made against the
    /// in-memory result. That is not a hypothetical here: <c>AttemptRecord.Usage</c> shipped declared, READ
    /// by the per-tier spend aggregation and assigned by none of the record-construction sites (#475), with
    /// every guardrail in its wave green. The sibling <c>Judge_ProvenanceReachesRunJson_BothPaths</c> reads
    /// the journal for exactly that reason, and so does this.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "ObservedModelProvenance")]
    public async Task ObservedModel_BecomesProvenanceModel_OnBothRecordPaths()
    {
        using var harness = new Stage2PlanHarness();
        Stage2RunResult serial = await harness.RunAsync(ObservedModelPlan());
        using Stage2DeferredSettleRun deferred =
            await Stage2DeferredSettleRun.RunAsync(harness.PlanRoot, ObservedModelPlan());

        AssertBothRecordPaths(serial, deferred, ObservedMismatchTask);

        foreach ((Stage2RunResult run, bool deferredSettle) in new[] { (serial, false), (deferred.Result, true) })
        {
            string path = RecordPathLabel(deferredSettle);
            AssertSettledGreen(run, ObservedMismatchTask, path);

            // Fixture floor, both halves: the route really asked for one model, and the runner really
            // answered with a different one. Without it, "the observed value wins" would be asserted over a
            // run where the two never disagreed — which every implementation passes, including no
            // implementation at all.
            Stage2RecordedCall call = ActionCall(run, ObservedMismatchTask, 1);
            Assert.True(
                call.Model == ObservedRouteModel,
                $"[{path}] the action RAN on '{Describe(call.Model)}', expected the route's " +
                $"'{ObservedRouteModel}'. The request half of the disagreement is not in place, so there is " +
                "nothing here for an observed model to differ FROM.");
            Assert.True(
                call.Result.ObservedModel == ObservedActualModel,
                $"[{path}] the fake runner returned observedModel " +
                $"'{Describe(call.Result.ObservedModel)}', expected '{ObservedActualModel}'. The scripted " +
                "PromptResult is the whole setup for this clause — a runner that reported nothing leaves " +
                "the carry with nothing to carry.");

            AttemptProvenance provenance = ProvenanceOf(run, ObservedMismatchTask, 1);

            Assert.True(
                provenance.Model == ObservedActualModel,
                $"[{path}] run.json records provenance.model '{Describe(provenance.Model)}', but the runner " +
                $"itself reported running on '{ObservedActualModel}' while the route merely ASKED for " +
                $"'{ObservedRouteModel}'. provenance.model is best-known-actual — observed, else the " +
                "resolved route, else the sentinel — so the moment an attempt learns what actually served " +
                "it, that is the fact the field carries. Recording the request here leaves every reader of " +
                "the journal believing a model ran that may never have.");
        }
    }

    /// <summary>
    /// That same disagreeing run also records <c>provenance.requestedModel</c> — the ROUTE's model, which
    /// is the one fact <c>provenance.model</c> no longer carries once it became best-known-actual.
    ///
    /// <para><b>Nothing is lost by the swap, and that is the point.</b> The requested model is not
    /// disposable: it is what the operator's <c>promptRunners</c> block and <c>tiering</c> config actually
    /// selected, so it is the only evidence that answers "did my routing do what I configured it to do"
    /// when the provider served something else. Moving <c>model</c> to the observed value without this
    /// field would buy one fact by destroying another.</para>
    ///
    /// <para><b>Two keys, two distinct facts.</b> DoR §9.3 asked for a third — a <c>resolvedModel</c>
    /// alongside — and the shipped Stage 2 contract refused it in <c>JournalModel.cs</c>: two fields
    /// claiming the same fact is how they drift. A second field earns its place only by carrying the
    /// DISAGREEMENT, which is what this one does.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "ObservedModelProvenance")]
    public async Task RequestedModel_IsWritten_WhenTheObservedDiffersFromTheRoute()
    {
        using var harness = new Stage2PlanHarness();
        Stage2RunResult serial = await harness.RunAsync(ObservedModelPlan());
        using Stage2DeferredSettleRun deferred =
            await Stage2DeferredSettleRun.RunAsync(harness.PlanRoot, ObservedModelPlan());

        AssertBothRecordPaths(serial, deferred, ObservedMismatchTask);

        foreach ((Stage2RunResult run, bool deferredSettle) in new[] { (serial, false), (deferred.Result, true) })
        {
            string path = RecordPathLabel(deferredSettle);
            AssertSettledGreen(run, ObservedMismatchTask, path);

            // Fixture floor: the route this clause expects to see recorded is the one that actually ran.
            Stage2RecordedCall call = ActionCall(run, ObservedMismatchTask, 1);
            Assert.True(
                call.Model == ObservedRouteModel,
                $"[{path}] the action RAN on '{Describe(call.Model)}', expected the route's " +
                $"'{ObservedRouteModel}' — requestedModel records the ROUTE, so a fixture whose route is " +
                "somewhere else would pin the wrong string.");

            AttemptProvenance provenance = ProvenanceOf(run, ObservedMismatchTask, 1);

            Assert.True(
                provenance.RequestedModel == ObservedRouteModel,
                $"[{path}] run.json records provenance.requestedModel " +
                $"'{Describe(provenance.RequestedModel)}', expected the route's '{ObservedRouteModel}' — " +
                $"the runner answered '{ObservedActualModel}', so the request and the reality disagree and " +
                "the request needs its own key. Without it the journal cannot answer whether the operator's " +
                "routing selected what they configured: 'the provider substituted' and 'my tier config is " +
                "wrong' look identical from a single model field.");
        }
    }

    /// <summary>
    /// A run whose runner echoes back EXACTLY the model the route asked for records NO
    /// <c>requestedModel</c> key at all. Its PRESENCE is the mismatch signal, so an always-written key
    /// destroys the signal — there is no separate flag to fall back on.
    ///
    /// <para><b>The floor is the whole clause.</b> "The key is absent" is trivially satisfied by an
    /// implementation that never writes it anywhere — which is precisely the state of the journal before
    /// this wave. So the sibling task in the SAME run, on the same plan and the same block, must carry the
    /// key first: only then does absence over here mean "the two agreed" rather than "the mechanism does
    /// not exist".</para>
    ///
    /// <para><b>Absent, never <c>null</c>.</b> <c>JournalJson</c> sets
    /// <c>DefaultIgnoreCondition = Never</c>, so a member without a <c>WhenWritingNull</c> ignore grows a
    /// <c>"requestedModel": null</c> key on EVERY attempt — including the script attempts of runs whose
    /// author opted into none of this. The two shapes deserialize identically and the cost is paid entirely
    /// by the humans and tooling reading <c>run.json</c>, which is why this is asserted on the RAW JSON
    /// keys rather than on a deserialized null.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "ObservedModelProvenance")]
    public async Task RequestedModel_IsAbsent_WhenTheObservedMatchesTheRequest()
    {
        using var harness = new Stage2PlanHarness();
        Stage2RunResult serial = await harness.RunAsync(ObservedModelPlan());
        using Stage2DeferredSettleRun deferred =
            await Stage2DeferredSettleRun.RunAsync(harness.PlanRoot, ObservedModelPlan());

        AssertBothRecordPaths(serial, deferred, ObservedMatchTask);

        foreach ((Stage2RunResult run, bool deferredSettle) in new[] { (serial, false), (deferred.Result, true) })
        {
            string path = RecordPathLabel(deferredSettle);
            AssertSettledGreen(run, ObservedMatchTask, path);
            AssertSettledGreen(run, ObservedMismatchTask, path);

            // Fixture floor: the DISAGREEING sibling carries the key, so absence below is a decision this
            // run made rather than a mechanism that was never built.
            bool siblingCarriesRequest = ProvenanceJson(run, ObservedMismatchTask, 1).ContainsKey(RequestedModelKey);
            Assert.True(
                siblingCarriesRequest,
                $"[{path}] '{ObservedMismatchTask}' — whose runner answered '{ObservedActualModel}' to a " +
                $"route asking for '{ObservedRouteModel}' — records no '{RequestedModelKey}' key either, so " +
                "this run writes it NOWHERE. An absence that is absent everywhere proves nothing about the " +
                "matching case; wire the mismatch first.");

            Stage2RecordedCall call = ActionCall(run, ObservedMatchTask, 1);
            Assert.True(
                call.Model == ObservedRouteModel && call.Result.ObservedModel == ObservedRouteModel,
                $"[{path}] the action RAN on '{Describe(call.Model)}' and the runner answered " +
                $"'{Describe(call.Result.ObservedModel)}' — this clause needs both to be " +
                $"'{ObservedRouteModel}', because it is about the case where request and reality AGREE.");

            AttemptProvenance provenance = ProvenanceOf(run, ObservedMatchTask, 1);
            Assert.True(
                provenance.Model == ObservedRouteModel,
                $"[{path}] run.json records provenance.model '{Describe(provenance.Model)}' for an attempt " +
                $"whose route and runner both say '{ObservedRouteModel}'. best-known-actual and the request " +
                "are the same string here; there is no third answer.");

            bool carriesRequestedModel = ProvenanceJson(run, ObservedMatchTask, 1).ContainsKey(RequestedModelKey);
            Assert.False(
                carriesRequestedModel,
                $"[{path}] '{ObservedMatchTask}' records a '{RequestedModelKey}' key on an attempt where the " +
                "runner echoed exactly what the route asked for. The key is written ONLY on a disagreement " +
                "— its presence IS the mismatch signal, and there is no separate flag beside it. Writing it " +
                "on every attempt makes the signal indistinguishable from the ordinary case, which is the " +
                "one thing it exists to distinguish.");
        }
    }

    /// <summary>
    /// A runner that reports NO model leaves <c>provenance.model</c> exactly as it is today — the resolved
    /// route, or the <c>"(cli default)"</c> sentinel when nothing named a model at all — and writes no
    /// <c>requestedModel</c> key.
    ///
    /// <para><b>Deliberately GREEN from the first commit.</b> This is the regression half: the new fact is
    /// bought at no cost to the old one. <c>observed ?? route ?? sentinel</c> is a fallback CHAIN, not a
    /// replacement, and the cheapest wrong implementation of the clauses above — assign the observed value
    /// unconditionally — turns every silent runner's provenance into a null or an empty string and takes
    /// the sentinel with it. A field that regressed to nothing is worse than the field that shipped,
    /// because the reader cannot tell "the runner said nothing" from "no model was ever resolved".</para>
    ///
    /// <para><b>Both tails of the chain, because they fail differently.</b> The routed task loses a real
    /// model string; the model-less block's task loses the sentinel — the display stand-in that exists
    /// precisely so per-attempt provenance is not a silent gap for the zero-setup user who configured no
    /// model anywhere.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "ObservedModelProvenance")]
    public async Task ProvenanceModel_StaysTheResolvedRoute_WhenTheRunnerReportedNoModel()
    {
        using var harness = new Stage2PlanHarness();
        Stage2RunResult serial = await harness.RunAsync(ObservedModelPlan());
        using Stage2DeferredSettleRun deferred =
            await Stage2DeferredSettleRun.RunAsync(harness.PlanRoot, ObservedModelPlan());

        AssertBothRecordPaths(serial, deferred, ObservedSilentTask);

        foreach ((Stage2RunResult run, bool deferredSettle) in new[] { (serial, false), (deferred.Result, true) })
        {
            string path = RecordPathLabel(deferredSettle);

            foreach (string taskId in new[] { ObservedSilentTask, ObservedUnnamedModelTask })
            {
                AssertSettledGreen(run, taskId, path);

                // Fixture floor: these two runners really did answer with nothing. A scripted result that
                // quietly carried a model would make the clause green for the wrong reason.
                Assert.True(
                    ActionCall(run, taskId, 1).Result.ObservedModel is null,
                    $"[{path}] '{taskId}': the fake runner reported observedModel " +
                    $"'{Describe(ActionCall(run, taskId, 1).Result.ObservedModel)}' — this clause is about " +
                    "the runner that reports NOTHING, so it must report nothing.");

                bool carriesRequestedModel = ProvenanceJson(run, taskId, 1).ContainsKey(RequestedModelKey);
                Assert.False(
                    carriesRequestedModel,
                    $"[{path}] '{taskId}' records a '{RequestedModelKey}' key though its runner named no " +
                    "model at all. Silence is not a disagreement: there is no second model here to hold " +
                    "apart from the first, and a key written anyway turns the mismatch signal into noise.");
            }

            Assert.True(
                ProvenanceOf(run, ObservedSilentTask, 1).Model == ObservedRouteModel,
                $"[{path}] '{ObservedSilentTask}' records provenance.model " +
                $"'{Describe(ProvenanceOf(run, ObservedSilentTask, 1).Model)}', expected the resolved " +
                $"route's '{ObservedRouteModel}'. The runner volunteered nothing, so the chain falls " +
                "through to the route — exactly today's behaviour, unchanged. An implementation that " +
                "assigns the observed value unconditionally erases a real model string here.");

            Assert.True(
                ProvenanceOf(run, ObservedUnnamedModelTask, 1).Model
                    == PromptExecutionSupport.CliDefaultModelDisplay,
                $"[{path}] '{ObservedUnnamedModelTask}' records provenance.model " +
                $"'{Describe(ProvenanceOf(run, ObservedUnnamedModelTask, 1).Model)}', expected the sentinel " +
                $"'{PromptExecutionSupport.CliDefaultModelDisplay}'. Its block names no model and its runner " +
                "reported none, so nothing in the chain has an answer — and the sentinel is what says so out " +
                "loud. Losing it makes per-attempt provenance a silent gap for the user who configured no " +
                "model anywhere, which is the gap #200 closed.");
        }
    }

    /// <summary>
    /// No attempt's provenance, anywhere in <c>run.json</c>, carries a <c>resolvedModel</c> key — on either
    /// record path.
    ///
    /// <para><b>Pinning a refusal, not an omission.</b> DoR §9.3 asked for a <c>resolvedModel</c> beside
    /// <c>model</c>; the shipped Stage 2 contract declined it in <c>JournalModel.cs</c> — <i>two fields
    /// claiming the same fact is how they drift</i> — and #349 keeps that ruling while adding the one field
    /// that carries a DIFFERENT fact, the disagreement. Nothing in the harness stops a later well-meaning
    /// edit from adding the duplicate back "for clarity", and a duplicate is silent when it is right and
    /// invisible when it is wrong, so the contract is pinned here rather than merely written down.</para>
    ///
    /// <para><b>GREEN from the first commit, like the sibling regression clause</b> — and it stays green
    /// only for as long as the settled shape holds.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "ObservedModelProvenance")]
    public async Task NoResolvedModelKeyIsEverWritten()
    {
        using var harness = new Stage2PlanHarness();
        Stage2RunResult serial = await harness.RunAsync(ObservedModelPlan());
        using Stage2DeferredSettleRun deferred =
            await Stage2DeferredSettleRun.RunAsync(harness.PlanRoot, ObservedModelPlan());

        AssertBothRecordPaths(serial, deferred, ObservedMismatchTask);

        int taskCount = ObservedModelPlan().Tasks.Count;

        foreach ((Stage2RunResult run, bool deferredSettle) in new[] { (serial, false), (deferred.Result, true) })
        {
            string path = RecordPathLabel(deferredSettle);
            IReadOnlyList<(string TaskId, int Attempt, JsonObject Provenance)> all = AllProvenanceJson(run);

            // Floor: this clause proves a key is ABSENT, and a sweep that found no provenance objects at
            // all would report exactly the same green for entirely the wrong reason.
            Assert.True(
                all.Count >= taskCount,
                $"[{path}] run.json carries {all.Count} attempt provenance object(s), expected at least one " +
                $"per task ({taskCount}). An absence asserted over an empty sweep is not evidence.");

            foreach ((string taskId, int attempt, JsonObject provenance) in all)
            {
                bool carriesResolvedModel = provenance.ContainsKey(ResolvedModelKey);
                Assert.False(
                    carriesResolvedModel,
                    $"[{path}] '{taskId}' attempt {attempt} records a '{ResolvedModelKey}' key " +
                    $"('{Describe(provenance[ResolvedModelKey]?.ToString())}'). There is no such field in " +
                    "the contract: provenance.model IS the resolved fact, best-known-actual, and " +
                    $"'{RequestedModelKey}' is the one field allowed beside it — written only on a " +
                    "disagreement, because a second field earns its place by carrying what the first cannot.");
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // Fixtures and observation helpers
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The only block serving the top rung in <see cref="ClimbPlan"/>.</summary>
    private const string Apex = "apex";

    /// <summary>That block's model — distinct from <see cref="PointerModel"/> so a fallback is obvious.</summary>
    private const string ApexModel = "apex-model";

    /// <summary>
    /// The <c>promptRunners.default</c> pointer's block. It carries NO <c>routing</c>, so it is never a
    /// tier target — reachable only by name or as the legacy fallback, which is exactly what makes
    /// "provenance.model == PointerModel" a sound test for "it fell back".
    /// </summary>
    private static Stage2RunnerBlock PointerBlock => new() { Name = Pointer, Model = PointerModel };

    /// <summary>
    /// The CLIMB fixture, shared by the three clauses that need it (D30, the recorded climb, the logged
    /// climb): a task asking for <c>medium</c>, where the only routable block serves <c>hard</c>. The
    /// requested rung's candidate set is empty and a stronger rung's is not, so the resolver must climb —
    /// and the default pointer's distinctly-named model is sitting right there as the wrong answer.
    /// </summary>
    private static Stage2PlanSpec ClimbPlan(string taskId) => new()
    {
        DefaultRunner = Pointer,
        Runners =
        [
            PointerBlock,
            new Stage2RunnerBlock { Name = Apex, Model = ApexModel, Strength = 4, Tiers = [ActionTiers.Hard] }
        ],
        Tasks = [new Stage2TaskSpec { Id = taskId, Tier = ActionTiers.Medium }]
    };

    /// <summary>
    /// The registry the run actually executed against, read back through the REAL loader from the plan
    /// the harness wrote. Clause 2 computes its expected candidate set from these blocks with
    /// <see cref="PromptRunnerConfig.ServesTier"/> — the one predicate — rather than from a
    /// hand-maintained copy of the fixture.
    /// </summary>
    private static IReadOnlyDictionary<string, PromptRunnerConfig> RegistryOf(Stage2RunResult run)
    {
        PlanLoadResult load = new PlanLoader().Load(run.PlanRoot);
        Assert.True(
            load.Plan is not null,
            $"the plan at {run.PlanRoot} no longer loads:\n{string.Join("\n", load.Diagnostics)}");
        return load.Plan!.Config.PromptRunners;
    }

    /// <summary>
    /// The SSOT §8 mirror of the attempt's provenance — <c>attempt-provenance.json</c> in the attempt's
    /// own log dir, written by the attempt launcher from the same object the journal receives.
    ///
    /// <para>It is read (rather than only the journal's copy) by the two clauses that inspect a FAILED
    /// attempt: <c>AttemptJournaler.FailedAttempt</c> takes no provenance parameter, so a failed attempt
    /// has no journal copy, and that file is outside the writeScope of the tasks that must green those
    /// clauses. Deserialized with the same camelCase policy <c>AttemptArtifacts</c> serializes with, so
    /// the assertion reads whatever the harness actually wrote.</para>
    /// </summary>
    private static AttemptProvenance PerAttemptProvenance(Stage2RunResult run, string taskId, int attempt)
    {
        string path = Path.Combine(run.AttemptLogDir(taskId, attempt), "attempt-provenance.json");
        Assert.True(
            File.Exists(path),
            $"'{taskId}' attempt {attempt} wrote no attempt-provenance.json. The attempt's log dir holds " +
            $"[{string.Join(", ", run.AttemptLogFiles(taskId, attempt))}].");

        AttemptProvenance? mirrored = JsonSerializer.Deserialize<AttemptProvenance>(File.ReadAllText(path), MirrorJson);
        Assert.True(
            mirrored is not null,
            $"'{taskId}' attempt {attempt}: attempt-provenance.json did not deserialize to a provenance object.");
        return mirrored!;
    }

    /// <summary>Matches <c>AttemptArtifacts</c>' serializer policy, so the mirror round-trips exactly.</summary>
    private static readonly JsonSerializerOptions MirrorJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The JOURNAL's per-attempt provenance, asserted present so every clause reads a real record.</summary>
    private static AttemptProvenance ProvenanceOf(Stage2RunResult run, string taskId, int attempt)
    {
        AttemptRecord record = run.AttemptFor(taskId, attempt);
        Assert.True(
            record.Provenance is not null,
            $"'{taskId}' attempt {attempt} recorded NO provenance — it is the machine-readable copy of the " +
            "route, and every clause in this suite reads the route from it.");
        return record.Provenance!;
    }

    /// <summary>The run report's entry for <paramref name="taskId"/>.</summary>
    private static TaskResult ResultFor(Stage2RunResult run, string taskId)
    {
        TaskResult? result = run.Report.Tasks.FirstOrDefault(t => string.Equals(t.TaskId, taskId, StringComparison.Ordinal));
        Assert.True(
            result is not null,
            $"the run report has no entry for '{taskId}' (it reported: " +
            $"{string.Join(", ", run.Report.Tasks.Select(t => t.TaskId))}).");
        return result!;
    }

    /// <summary>
    /// The attempt's route disclosure, asserted PRESENT — the surface tasks 07/09 implement to, written
    /// best-effort beside <c>attempt-tool-grants.log</c> in the attempt's own log dir.
    /// </summary>
    private static string RouteDisclosure(Stage2RunResult run, string taskId, int attempt)
    {
        string path = Path.Combine(run.AttemptLogDir(taskId, attempt), RouteLogName);
        Assert.True(
            File.Exists(path),
            $"'{taskId}' attempt {attempt} wrote no {RouteLogName}. The attempt's log dir holds " +
            $"[{string.Join(", ", run.AttemptLogFiles(taskId, attempt))}]. The disclosure carries the " +
            "resolved runner, model, effort, the requested and served rungs and the tierSource — the " +
            "human-readable twin of the provenance object.");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// The same file, but ABSENCE is a legitimate answer — used by the two NEGATIVE halves (Invariant 7's
    /// "no rung is named anywhere" and D28's "not on the first attempt"), where writing nothing is one
    /// correct implementation and writing something without the warning is another.
    /// </summary>
    private static string RouteDisclosureOrEmpty(Stage2RunResult run, string taskId, int attempt)
    {
        string path = Path.Combine(run.AttemptLogDir(taskId, attempt), RouteLogName);
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    /// <summary>The LOUD lines of a disclosure — the <c>WARNING:</c>-prefixed ones §6.2 requires.</summary>
    private static IReadOnlyList<string> WarningLines(string disclosure) =>
    [
        .. disclosure
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Contains(WarningPrefix, StringComparison.Ordinal))
    ];

    /// <summary>The one invocation made for <paramref name="attempt"/>, so a per-attempt clause cannot silently read the wrong call.</summary>
    private static Stage2RecordedCall CallForAttempt(IReadOnlyList<Stage2RecordedCall> calls, int attempt)
    {
        IReadOnlyList<Stage2RecordedCall> matching = [.. calls.Where(c => c.Attempt == attempt)];
        Assert.True(
            matching.Count == 1,
            $"expected exactly 1 invocation for attempt {attempt}, saw {matching.Count} (the run made " +
            $"{calls.Count} for this task).");
        return matching[0];
    }

    /// <summary>Renders an absent value as a readable token, so a failure message never reads "expected x, got ''".</summary>
    private static string Describe(string? value) => value ?? "(absent)";

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // Verifier-route fixtures (clauses 10–17)
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The provider kind every WEAK fixture block declares. §6.5 rule 4 decides weakness by
    /// <c>strength</c> when the operator declared one and by the verifier-only provider-kind fallback
    /// when they did not — <c>kind != "claude"</c> ⇒ weak-unless-declared — so a weak block is an
    /// UNRANKED non-Claude one, and both halves of that are spelled at every use site.
    /// </summary>
    private const string WeakProviderKind = "openai-compat";

    /// <summary>The loaded name of the judge guardrail <see cref="Verdict"/> emits.</summary>
    private const string JudgeGuardrailName = "01-verdict";

    /// <summary>
    /// A prompt-JUDGE guardrail that pins NOTHING — no frontmatter <c>runner</c>, no frontmatter
    /// <c>tier</c> — so §6.5 rule 1 does not fire and rules 2–3 decide the route. That is the shape every
    /// clause here needs: a pinned judge would prove only that a pin is honoured, which §6.1 already
    /// covers on the actor side.
    /// </summary>
    private static Stage2GuardrailSpec Verdict() => new() { Name = "verdict" };

    /// <summary>The task whose judge resolves BELOW <c>verifier.minTier</c> and must be RAISED to it.</summary>
    private const string FloorRaisedTask = "01-light";

    /// <summary>The task whose judge resolves ABOVE the floor and must be left exactly where it is.</summary>
    private const string FloorUntouchedTask = "02-heavyweight";

    /// <summary>The block serving the bottom rung — the raised judge's ACTOR, and never the judge itself.</summary>
    private const string FloorEntryModel = "entry-model";

    /// <summary>The block at the floor's own rung — where a RAISED judge lands.</summary>
    private const string FloorMid = "mid";

    /// <summary>That block's model.</summary>
    private const string FloorMidModel = "mid-model";

    /// <summary>The block serving the top rung — where a judge ABOVE the floor stays.</summary>
    private const string FloorTopModel = "top-model";

    /// <summary>
    /// The VERIFIER-FLOOR fixture: <c>tiering.verifier.minTier</c> set one rung up from the bottom, a
    /// task BELOW it and a task ABOVE it, and exactly one block per rung so each judge's landing site is
    /// an unambiguous model string.
    ///
    /// <para>Shared by the floor clause (which reads both tasks) and the judge-provenance clause (which
    /// reads the raised one). The raised task is what makes the second clause sharp: its judge's block,
    /// model AND rung all differ from its actor's, so a recorded <c>judge</c> object that merely echoed
    /// the actor cannot pass — and that is the cheapest wrong implementation which still produces a
    /// non-null object.</para>
    /// </summary>
    private static Stage2PlanSpec VerifierFloorPlan() => new()
    {
        DefaultRunner = Pointer,
        // A FLOOR, not a default: it never selects, it only refuses a result that came out too low.
        VerifierMinTier = ActionTiers.Medium,
        Runners =
        [
            PointerBlock,
            new Stage2RunnerBlock { Name = "entry", Model = FloorEntryModel, Strength = 1, Tiers = [ActionTiers.Easy] },
            new Stage2RunnerBlock { Name = FloorMid, Model = FloorMidModel, Strength = 4, Tiers = [ActionTiers.Medium] },
            new Stage2RunnerBlock { Name = "top", Model = FloorTopModel, Strength = 7, Tiers = [ActionTiers.Hard] }
        ],
        Tasks =
        [
            new Stage2TaskSpec { Id = FloorRaisedTask, Tier = ActionTiers.Easy, JudgeGuardrail = Verdict() },
            new Stage2TaskSpec { Id = FloorUntouchedTask, Tier = ActionTiers.Hard, JudgeGuardrail = Verdict() }
        ]
    };

    /// <summary>The single task of the #475 usage fixture.</summary>
    private const string MeteredTask = "01-metered";

    /// <summary>
    /// The token volume the fake runner REPORTS for the metered attempt — the value the shipped carry has
    /// to move from <see cref="PromptResult.Usage"/> to <c>run.json</c>'s <c>attempt.usage</c>. Both
    /// counts are non-zero and unequal so a record that transposed or defaulted them is visible.
    /// </summary>
    private static readonly PromptUsage ReportedUsage = new() { InputTokens = 4321, OutputTokens = 876 };

    /// <summary>
    /// The #475 fixture: one ordinary tiered task whose scripted runner result CARRIES usage. The counts
    /// are put on a real <see cref="PromptResult"/> — the object the process boundary hands back — so
    /// every step between the runner and the journal is the shipped code, which is exactly where the
    /// field currently dies.
    /// </summary>
    private static Stage2PlanSpec MeteredPlan() => new()
    {
        DefaultRunner = Pointer,
        Runners =
        [
            PointerBlock,
            new Stage2RunnerBlock { Name = "worker", Model = "worker-model", Strength = 2, Tiers = [ActionTiers.Easy] }
        ],
        Tasks =
        [
            new Stage2TaskSpec
            {
                Id = MeteredTask,
                Tier = ActionTiers.Easy,
                Results = [Stage2PlanHarness.Success() with { Usage = ReportedUsage }]
            }
        ]
    };

    /// <summary>The record path a "both paths" case is exercising, for its failure messages.</summary>
    private static string RecordPathLabel(bool deferredSettle) => deferredSettle ? "worktree-settle" : "serial";

    /// <summary>
    /// Assert the run really took the record path its case claims. A succeeded attempt's record is built
    /// in TWO places — <c>AttemptJournaler.CompleteSucceededOrInvalidFragment</c> in serial mode, and the
    /// Scheduler's deferred B1 settle (<c>RecordSucceededSettle</c>) in worktree mode — and
    /// <see cref="TaskResult.DeferredSettle"/> is the flag that decides which. Without this check "both
    /// paths" degrades into the same serial assertion made twice, which is precisely the shape a two-path
    /// proof exists to rule out.
    /// </summary>
    private static void AssertRecordPath(Stage2RunResult run, string taskId, bool deferredSettle)
    {
        TaskResult result = ResultFor(run, taskId);
        Assert.True(
            result.DeferredSettle == deferredSettle,
            $"[{RecordPathLabel(deferredSettle)}] '{taskId}' settled with DeferredSettle=" +
            $"{result.DeferredSettle}, expected {deferredSettle} — this case did not exercise the record " +
            "path it claims to, so whatever it asserts below says nothing about that path. The two paths " +
            "are AttemptJournaler (serial) and the Scheduler's deferred B1 settle (worktree mode, which is " +
            "what a real run takes); a datum threaded through only one is invisible to nearly every user.");
    }

    /// <summary>
    /// The §12.4 <c>judge {...}</c> object on this attempt's journal provenance, asserted PRESENT. Read
    /// back off <c>run.json</c> (the parsed journal) rather than off the harness's in-memory result: a
    /// datum that is computed and never serialized is exactly the failure this wave is closing.
    /// </summary>
    private static AttemptJudge JudgeRecordOf(Stage2RunResult run, string taskId, int attempt, string path)
    {
        AttemptProvenance provenance = ProvenanceOf(run, taskId, attempt);
        Assert.True(
            provenance.Judge is not null,
            $"[{path}] '{taskId}' attempt {attempt}: run.json's provenance carries NO judge object, though " +
            "this attempt was graded by a prompt guardrail that resolved through routing. §12.4 records the " +
            "verifier route beside the actor's so the run is MEASURABLE rather than merely asserted — " +
            "\"who vouched for this work, and were they strong enough to\" has no answer without it.");
        return provenance.Judge!;
    }

    /// <summary>
    /// The one ACTION invocation of <paramref name="taskId"/>'s <paramref name="attempt"/> — so a clause
    /// comparing a judge's route against its actor's cannot silently read a different attempt's, which is
    /// what makes "the judge resolved at the actor's rung" look green when it resolved somewhere else.
    /// </summary>
    private static Stage2RecordedCall ActionCall(Stage2RunResult run, string taskId, int attempt)
    {
        IReadOnlyList<Stage2RecordedCall> matching =
        [
            .. run.ActionCallsFor(taskId).Where(c => c.Attempt == attempt)
        ];

        Assert.True(
            matching.Count == 1,
            $"expected exactly 1 ACTION invocation for '{taskId}' attempt {attempt}, saw {matching.Count} " +
            $"(the run made {run.ActionCallsFor(taskId).Count} action and " +
            $"{run.JudgeCallsFor(taskId).Count} judge call(s) for this task).");
        return matching[0];
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // Observed-model fixtures and RAW-JSON readers (clause 18, #349)
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The wire key that records the ROUTE's model when the runner reports a different one. Spelled once,
    /// because three clauses assert on its PRESENCE or ABSENCE and a typo in either direction is a green
    /// test that measures nothing.
    /// </summary>
    private const string RequestedModelKey = "requestedModel";

    /// <summary>
    /// The key DoR §9.3 asked for and the shipped contract refused. It has no producer — it is spelled here
    /// only so a clause can assert nobody ever grew one.
    /// </summary>
    private const string ResolvedModelKey = "resolvedModel";

    /// <summary>The one routable block of the observed-model fixture, serving the bottom rung.</summary>
    private const string ObservedRunner = "routed";

    /// <summary>
    /// The model that block declares — what the ROUTE asks for, and the only model fact the harness knows
    /// at attempt launch.
    /// </summary>
    private const string ObservedRouteModel = "routed-model";

    /// <summary>
    /// What the fake runner ECHOES back on the mismatch task. Named with no substring in common with
    /// <see cref="ObservedRouteModel"/>, on the same reasoning as <see cref="PointerModel"/>: a failure
    /// message must make the wrong answer unmistakable rather than a subtle difference between two
    /// plausible model names.
    /// </summary>
    private const string ObservedActualModel = "observed-model";

    /// <summary>The block that names NO model — where the <c>"(cli default)"</c> sentinel comes from.</summary>
    private const string ObservedUnnamedRunner = "unnamed";

    /// <summary>The task whose runner answers with a model DIFFERENT from the route's.</summary>
    private const string ObservedMismatchTask = "01-observed-differs";

    /// <summary>The task whose runner echoes back EXACTLY the model the route asked for.</summary>
    private const string ObservedMatchTask = "02-observed-echoes";

    /// <summary>The task whose runner answers with nothing, on a route that DOES name a model.</summary>
    private const string ObservedSilentTask = "03-runner-silent";

    /// <summary>The task whose runner answers with nothing on a route that names no model either — the sentinel case.</summary>
    private const string ObservedUnnamedModelTask = "04-silent-and-unnamed";

    /// <summary>
    /// The #349 fixture: one registry, four tasks, and the four combinations of (does the route name a
    /// model) × (what did the runner answer) that the <c>observed ?? route ?? sentinel</c> chain has to get
    /// right. Every task is scripted EXPLICITLY, including the two that report nothing, so "the runner
    /// stayed silent" is a stated fixture rather than a default nobody looked at.
    ///
    /// <para>All four run in ONE plan on purpose. The absence clause needs a task that DOES carry
    /// <c>requestedModel</c> sitting in the same <c>run.json</c> as the task that must not — otherwise
    /// "the key is absent here" is satisfied by a journal that writes it nowhere, which is the state of
    /// every journal written before this wave.</para>
    ///
    /// <para>The counts are the model strings the shipped carry has to move from
    /// <see cref="PromptResult.ObservedModel"/> — the object the process boundary hands back — into
    /// <c>run.json</c>, so every step in between is the shipped code.</para>
    /// </summary>
    private static Stage2PlanSpec ObservedModelPlan() => new()
    {
        DefaultRunner = Pointer,
        Runners =
        [
            PointerBlock,
            new Stage2RunnerBlock
            {
                Name = ObservedRunner,
                Model = ObservedRouteModel,
                Strength = 2,
                Tiers = [ActionTiers.Easy]
            },
            // No Model at all: the route resolves, names nothing, and provenance falls through to the
            // display sentinel — the zero-setup operator's shape.
            new Stage2RunnerBlock { Name = ObservedUnnamedRunner, Strength = 3, Tiers = [ActionTiers.Medium] }
        ],
        Tasks =
        [
            new Stage2TaskSpec
            {
                Id = ObservedMismatchTask,
                Tier = ActionTiers.Easy,
                Results = [Stage2PlanHarness.Success() with { ObservedModel = ObservedActualModel }]
            },
            new Stage2TaskSpec
            {
                Id = ObservedMatchTask,
                Tier = ActionTiers.Easy,
                Results = [Stage2PlanHarness.Success() with { ObservedModel = ObservedRouteModel }]
            },
            new Stage2TaskSpec
            {
                Id = ObservedSilentTask,
                Tier = ActionTiers.Easy,
                // Stated, not defaulted: this runner reports NO model, which is the whole fixture.
                Results = [Stage2PlanHarness.Success()]
            },
            new Stage2TaskSpec
            {
                Id = ObservedUnnamedModelTask,
                Tier = ActionTiers.Medium,
                Results = [Stage2PlanHarness.Success()]
            }
        ]
    };

    /// <summary>
    /// The two receipts every #349 clause takes BEFORE its first content assertion: the serial run really
    /// settled serially, the deferred run really settled through the Scheduler's B1 settle, and that second
    /// host really integrated a segment.
    ///
    /// <para>Taken up front for the reason the sibling clauses spell out at their own use sites — a failure
    /// on the serial run would otherwise mask a second host that never took the path it claims, and "both
    /// paths" would quietly be one path asserted twice. A field threaded through only
    /// <c>AttemptJournaler</c> and not the Scheduler's deferred settle is not half-delivered: worktree mode
    /// is what a real run takes, so it is invisible to nearly every user.</para>
    /// </summary>
    private static void AssertBothRecordPaths(Stage2RunResult serial, Stage2DeferredSettleRun deferred, string taskId)
    {
        AssertRecordPath(serial, taskId, deferredSettle: false);
        AssertRecordPath(deferred.Result, taskId, deferredSettle: true);
        Assert.True(
            deferred.Integrations > 0,
            $"the deferred-settle run integrated {deferred.Integrations} segment(s) — only the B1 settle " +
            "integrates, so a zero here means the run took the serial record path after all.");
    }

    /// <summary>
    /// The task settled SUCCEEDED — the floor under every provenance assertion, since only the success
    /// settle paths hand a provenance to the journal. Without it a broken fixture reports "no provenance
    /// recorded", which names the wrong defect.
    /// </summary>
    private static void AssertSettledGreen(Stage2RunResult run, string taskId, string path)
    {
        TaskResult result = ResultFor(run, taskId);
        Assert.True(
            result.Outcome == TaskOutcome.Succeeded,
            $"[{path}] '{taskId}' settled '{result.Outcome}', expected '{TaskOutcome.Succeeded}' — a task " +
            "must reach a SUCCEEDED attempt record for there to be a provenance to read at all. Summary: " +
            $"{result.Summary}");
    }

    /// <summary>
    /// The run's <c>state/run.json</c> as RAW JSON, parsed straight off disk.
    ///
    /// <para>Distinct from <see cref="Stage2RunResult.Journal"/>, and the difference is the point: a
    /// deserialized <c>AttemptProvenance</c> cannot tell an ABSENT key from a <c>"key": null</c> one, and
    /// <c>JournalJson</c> sets <c>DefaultIgnoreCondition = Never</c> — so a member declared without a
    /// <c>WhenWritingNull</c> ignore grows a null key on every attempt in the file. The clauses that assert
    /// on presence and absence read the keys themselves, which is what a human or a downstream tool sees.</para>
    /// </summary>
    private static JsonNode RunJsonOf(Stage2RunResult run)
    {
        string path = RunJournal.PathFor(run.PlanRoot);
        Assert.True(File.Exists(path), $"the run wrote no journal at {path}.");

        JsonNode? parsed = JsonNode.Parse(File.ReadAllText(path));
        Assert.True(parsed is not null, $"{path} did not parse as JSON.");
        return parsed!;
    }

    /// <summary>
    /// EVERY attempt provenance object in this run's <c>run.json</c>, as raw JSON, with the task id and
    /// attempt number carried along so a failure names the record it found.
    /// </summary>
    private static IReadOnlyList<(string TaskId, int Attempt, JsonObject Provenance)> AllProvenanceJson(Stage2RunResult run)
    {
        JsonObject? tasks = RunJsonOf(run)["tasks"]?.AsObject();
        Assert.True(tasks is not null, "run.json carries no 'tasks' object at all.");

        List<(string, int, JsonObject)> found = [];
        foreach (KeyValuePair<string, JsonNode?> task in tasks!)
        {
            foreach (JsonNode? attempt in task.Value?["attempts"]?.AsArray() ?? [])
            {
                if (attempt?["provenance"] is JsonObject provenance)
                {
                    found.Add((task.Key, attempt["attempt"]?.GetValue<int>() ?? 0, provenance));
                }
            }
        }

        return found;
    }

    /// <summary>The one raw provenance object of <paramref name="taskId"/>'s <paramref name="attempt"/>.</summary>
    private static JsonObject ProvenanceJson(Stage2RunResult run, string taskId, int attempt)
    {
        IReadOnlyList<(string TaskId, int Attempt, JsonObject Provenance)> all = AllProvenanceJson(run);
        IReadOnlyList<(string TaskId, int Attempt, JsonObject Provenance)> matching =
        [
            .. all.Where(p => string.Equals(p.TaskId, taskId, StringComparison.Ordinal) && p.Attempt == attempt)
        ];

        Assert.True(
            matching.Count == 1,
            $"run.json carries {matching.Count} provenance object(s) for '{taskId}' attempt {attempt}, " +
            $"expected exactly 1 (it recorded: {string.Join(", ", all.Select(p => p.TaskId + "#" + p.Attempt))}).");
        return matching[0].Provenance;
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════
    // The WORKTREE-SETTLE host — the second record path, driven the same real way
    // ═════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Re-runs a plan the <see cref="Stage2PlanHarness"/> already emitted, but through a Scheduler that
    /// OWNS an <see cref="IWorktreeProvider"/> — so a succeeded attempt takes
    /// <c>AttemptJournaler.ValidateFragmentForSettle</c> → the Scheduler's deferred B1 settle instead of
    /// the serial <c>CompleteSucceededOrInvalidFragment</c>. That is the second of the two places a
    /// succeeded attempt's record is BUILT, and the one a real worktree-mode run takes.
    ///
    /// <para><b>Why it lives in the suite rather than in the harness.</b> This is the honest place for it
    /// only because this task's declared writeScope is this file alone —
    /// <c>Stage2PlanHarness.cs</c> is task 05's deliverable and is out of scope here. A
    /// <c>Stage2PlanSpec.WorktreeSettle</c> flag on the harness would be the better home; recording that
    /// as the follow-up is more useful than silently dropping the second path, which would leave the
    /// #475-shaped defect (a datum threaded through one record site and dead on the other) exactly as
    /// invisible as it is today.</para>
    ///
    /// <para><b>Everything in-process is still the SHIPPED code.</b> The plan is the byte-identical one
    /// the harness wrote (copied, minus <c>state/</c> and <c>logs/</c>, so the run starts from a pristine
    /// journal rather than resuming a settled one); the loader, executor, scheduler and journal are the
    /// real ones; the ONE faked seam is <see cref="IPromptRunner"/>, exactly as in the harness. The
    /// worktree provider is a stand-in for GIT, not for the settle: it hands out real, existing segment
    /// directories — which is the whole trigger for the deferred path — with the all-zeros
    /// <c>TaskBase</c> sentinel that keeps <c>TaskExecutor.IsRealGitSegment</c> false, so no git command
    /// is ever reached. The same shape <c>MergeLockAndSettleTests</c> uses to pin the B1 ordering.</para>
    /// </summary>
    private sealed class Stage2DeferredSettleRun : IDisposable
    {
        /// <summary>The sentinel base sha that marks a NON-git segment (see <c>IsRealGitSegment</c>).</summary>
        private const string ZeroSha = "0000000000000000000000000000000000000000";

        private readonly string _root;

        private Stage2DeferredSettleRun(string root, Stage2RunResult result, int integrations)
        {
            _root = root;
            Result = result;
            Integrations = integrations;
        }

        /// <summary>The run's observation, in the same shape a serial harness run returns.</summary>
        public Stage2RunResult Result { get; }

        /// <summary>How many segments were integrated — non-zero only on the deferred settle path.</summary>
        public int Integrations { get; }

        /// <summary>
        /// Copy the plan at <paramref name="planTemplateRoot"/> to a fresh root and run it end to end with
        /// a worktree provider in play. <paramref name="spec"/> is the SAME spec that plan was emitted
        /// from — it supplies the per-task runner scripts and judge verdicts, nothing about the plan
        /// itself.
        /// </summary>
        public static async Task<Stage2DeferredSettleRun> RunAsync(string planTemplateRoot, Stage2PlanSpec spec)
        {
            string root = Path.Combine(Path.GetTempPath(), "gr-stage2-settle-" + Guid.NewGuid().ToString("N"));
            string planRoot = Path.Combine(root, "plan");
            string segmentRoot = Path.Combine(root, "segments");
            CopyPlanTemplate(planTemplateRoot, planRoot);

            PlanLoadResult load = new PlanLoader().Load(planRoot);
            Assert.False(
                load.HasErrors,
                "the copied plan no longer loads:\n" + string.Join("\n", load.Diagnostics));

            PlanDefinition plan = load.Plan!;

            var stateManager = new StateManager(plan.PlanDirectory);
            stateManager.Initialize();
            RunJournal journal = RunJournal.LoadOrCreate(plan);

            var ledger = new Ledger(
                spec.Tasks.ToDictionary(t => t.Id, t => t.EffectiveResults(), StringComparer.Ordinal),
                spec.Tasks
                    .Where(t => t.JudgeGuardrail is not null)
                    .ToDictionary(t => t.Id, t => t.JudgeGuardrail!, StringComparer.Ordinal));

            PromptRunnerRegistry registry =
                PromptRunnerRegistry.Build(plan.Config, block => new LedgerRunner(block, ledger));

            var executor = new TaskExecutor(
                plan, new ProcessRunner(), new InterpreterMap(new PathExecutableProbe(), plan.Config.Interpreters),
                stateManager, journal, IRunObserver.Null, registry,
                overwatch: null,
                transientDelay: (_, _) => Task.CompletedTask);

            var provider = new SegmentProvider(segmentRoot);
            var scheduler = new Scheduler(plan, executor, journal, provider, observer: IRunObserver.Null);
            RunReport report = await scheduler.RunAsync(plan, TestContext.Current.CancellationToken);

            return new Stage2DeferredSettleRun(
                root,
                new Stage2RunResult
                {
                    Report = report,
                    Journal = JournalReader.Read(RunJournal.PathFor(planRoot)),
                    Calls = ledger.Calls,
                    PlanRoot = planRoot
                },
                provider.Integrations);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort: a Windows file handle can outlive the run by a beat.
            }
            catch (UnauthorizedAccessException)
            {
                // Same — teardown must never fail a green test.
            }
        }

        /// <summary>
        /// Copy the emitted plan, EXCLUDING <c>state/</c> and <c>logs/</c>: the template's run.json already
        /// records its tasks succeeded, and a resume would skip every one of them rather than execute the
        /// path under test.
        /// </summary>
        private static void CopyPlanTemplate(string source, string destination)
        {
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(source, file);
                string top = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (top is "state" or "logs")
                {
                    continue;
                }

                string target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);

                // File.Copy does not carry the mode across, and a deterministic guardrail that is not
                // executable fails the task for a reason that has nothing to do with routing.
                if (!OperatingSystem.IsWindows() && target.EndsWith(".sh", StringComparison.Ordinal))
                {
                    File.SetUnixFileMode(
                        target,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                }
            }
        }

        /// <summary>
        /// A no-git <see cref="IWorktreeProvider"/> whose segments are REAL directories — the one fact
        /// <c>TaskExecutor</c> keys the deferred settle on. <c>TaskBase</c> is the all-zeros sentinel, so
        /// <c>IsRealGitSegment</c> stays false and neither the write-scope check nor the F2 reset nor any
        /// other git-backed step is reached. <c>Integrate</c> reports a free fast-forward, which is the
        /// settle's no-re-verify path.
        /// </summary>
        private sealed class SegmentProvider(string segmentRoot) : IWorktreeProvider
        {
            /// <summary>How many segments were handed to <see cref="Integrate"/> — the settle's receipt.</summary>
            public int Integrations { get; private set; }

            public IntegrationHandle CreateIntegration(string planName, string runId, CancellationToken ct) =>
                new()
                {
                    IntegrationWorktreePath = Materialize(Path.Combine(segmentRoot, "_integration")),
                    PlanBranchName = "stage2/" + planName,
                    OriginalBranch = "main",
                    OriginalHeadSha = ZeroSha,
                    RunId = runId
                };

            public WorktreeHandle CreateSegment(string taskId, int attempt, IntegrationHandle integ, CancellationToken ct) =>
                Segment(Path.Combine(segmentRoot, taskId, "attempt-" + attempt), taskId, $"stage2/{taskId}/attempt-{attempt}");

            public WorktreeHandle ReuseSegment(WorktreeHandle upstreamSegment, string taskId, int attempt) =>
                Segment(upstreamSegment.WorktreePath, taskId, $"stage2/reused/{taskId}/attempt-{attempt}");

            public WorktreeHandle ForkFromTip(string producerRecordedSha, string taskId, int attempt) =>
                Segment(Path.Combine(segmentRoot, "fork", taskId, "attempt-" + attempt), taskId, $"stage2/fork/{taskId}");

            public IntegrationResult Integrate(WorktreeHandle segment, IntegrationHandle integ, CancellationToken ct)
            {
                Integrations++;
                return IntegrationResult.FastForward;
            }

            public void Discard(WorktreeHandle handle)
            {
                // The whole tree is removed on Dispose; discarding early would race the end-of-run sweep
                // for no benefit.
            }

            public void PruneOrphans(IReadOnlyCollection<string> liveTaskIds, IntegrationHandle integ) { }

            public MergeOnSuccessResult MergePlanBranchIntoUserBranch(IntegrationHandle integ, CancellationToken ct) =>
                MergeOnSuccessResult.FastForwarded;

            private static WorktreeHandle Segment(string path, string taskId, string branch) =>
                new()
                {
                    WorktreePath = Materialize(path),
                    SegmentBranchName = branch,
                    TaskBase = ZeroSha,
                    RecordedCommitSha = ZeroSha,
                    PlanBranchHead = ZeroSha,
                    TaskId = taskId
                };

            /// <summary>The path, guaranteed to EXIST — a segment that is only a string stays serial.</summary>
            private static string Materialize(string path)
            {
                Directory.CreateDirectory(path);
                return path;
            }
        }

        /// <summary>One registry block's stand-in CLI — the same seam, and the only one, the harness fakes.</summary>
        private sealed class LedgerRunner(PromptRunnerConfig block, Ledger ledger) : IPromptRunner
        {
            public string Name => block.Name;

            public Task<PromptResult> RunAsync(PromptInvocation invocation, CancellationToken cancellationToken) =>
                Task.FromResult(ledger.Record(block, invocation));
        }

        /// <summary>
        /// The ordered ledger of every prompt invocation — ACTION and JUDGE alike — serving the spec's own
        /// per-task scripts, so a run here and a run through <see cref="Stage2PlanHarness"/> see the same
        /// scripted results. A JUDGE call never consumes an action's scripted step: a guardrail's outcome
        /// is its VERDICT FILE (SSOT §4.2/§9), which is what this writes.
        /// </summary>
        private sealed class Ledger(
            IReadOnlyDictionary<string, IReadOnlyList<PromptResult>> scripts,
            IReadOnlyDictionary<string, Stage2GuardrailSpec> judges)
        {
            private readonly List<Stage2RecordedCall> _calls = [];
            private readonly Dictionary<string, int> _consumed = new(StringComparer.Ordinal);
            private readonly object _gate = new();

            public IReadOnlyList<Stage2RecordedCall> Calls => _calls;

            public PromptResult Record(PromptRunnerConfig block, PromptInvocation invocation)
            {
                lock (_gate)
                {
                    string taskId = Env(invocation, "GUARDRAILS_TASK_ID") ?? string.Empty;

                    // The §5.1 contract adds the action-output pointers to a GUARDRAIL's env only, so the
                    // role is OBSERVED rather than passed in — exactly as the harness derives it.
                    bool isGuardrail = invocation.Environment.ContainsKey("GUARDRAILS_ACTION_RESULT");

                    PromptResult result = isGuardrail
                        ? ServeJudge(taskId, invocation)
                        : NextActionResult(taskId);

                    _calls.Add(new Stage2RecordedCall
                    {
                        Index = _calls.Count,
                        RunnerName = block.Name,
                        Effort = block.Effort,
                        TaskId = taskId,
                        Attempt = int.TryParse(Env(invocation, "GUARDRAILS_ATTEMPT"), out int attempt) ? attempt : 0,
                        IsGuardrail = isGuardrail,
                        Invocation = invocation,
                        Result = result
                    });

                    return result;
                }
            }

            private PromptResult NextActionResult(string taskId)
            {
                IReadOnlyList<PromptResult> script =
                    scripts.TryGetValue(taskId, out IReadOnlyList<PromptResult>? scripted) && scripted.Count > 0
                        ? scripted
                        : [Stage2PlanHarness.Success()];

                _consumed.TryGetValue(taskId, out int consumed);
                _consumed[taskId] = consumed + 1;
                return script[Math.Min(consumed, script.Count - 1)];
            }

            private PromptResult ServeJudge(string taskId, PromptInvocation invocation)
            {
                if (Env(invocation, "GUARDRAILS_VERDICT_OUT") is not { } verdictOut)
                {
                    throw new InvalidOperationException(
                        $"a guardrail invocation for '{taskId}' carried no GUARDRAILS_VERDICT_OUT, so the " +
                        "fake judge has nowhere to write its verdict. A prompt guardrail passes or fails " +
                        "SOLELY by that file (SSOT §4.2/§9).");
                }

                Stage2GuardrailSpec? judge = judges.TryGetValue(taskId, out Stage2GuardrailSpec? spec) ? spec : null;

                var verdict = new JsonObject
                {
                    ["pass"] = judge?.Pass ?? true,
                    ["reason"] = judge?.EffectiveReason() ?? "stage-2 fake judge: passed"
                };

                File.WriteAllText(verdictOut, verdict.ToJsonString());
                return Stage2PlanHarness.Success();
            }

            private static string? Env(PromptInvocation invocation, string key) =>
                invocation.Environment.TryGetValue(key, out string? value) ? value : null;
        }
    }
}
