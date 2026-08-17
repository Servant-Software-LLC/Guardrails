using System.Text.Json;
using Guardrails.Core.Execution;
using Guardrails.Core.Journal;
using Guardrails.Core.Loading;

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
}
