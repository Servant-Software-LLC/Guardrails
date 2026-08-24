using Guardrails.Core.Model;

namespace Guardrails.Core.Tests.ModelTiering;

/// <summary>
/// The tier-CLASSIFICATION half of the model-tiering review net, implemented as a folder-observable
/// audit so it can be asserted by a test instead of only by a reviewer's eye: <i>which prompt task and
/// which surviving prompt judge did a tiering-configured plan leave unclassified?</i>
///
/// <para><b>Why this lives in tests/ and not in the validator.</b> The settled ruling for this wave is
/// that NO <c>guardrails validate</c> code and NO GR code are allocated for it — the harness does not
/// block on a model-quality opinion, and an author who deliberately leaves a task on the plan-wide
/// default has done nothing invalid. So the review pass is the only gate, and this type is the review
/// pass's own reference implementation of the rule. It is deliberately NOT production code; the two
/// committed fixtures under <c>TestData/tier-tags/</c> are what keep it honest. This is the same posture
/// <see cref="SeamProofPlacement"/> takes for #382's T\* rule, for the same reason.</para>
///
/// <para><b>Why the finding is computable at all — read
/// <see cref="ActionDefinition.TierOrigin"/> before changing anything here.</b> The loader RESOLVES a
/// tier: precedence <c>task.json action.tier</c> &gt; <c>tiering.defaultTier</c> &gt; null, so a plan
/// that sets a plan-wide default arrives with EVERY prompt task carrying a non-null
/// <see cref="ActionDefinition.Tier"/>. Read that field alone and this audit would report every such
/// plan as fully classified and find nothing, forever. <see cref="ActionDefinition.TierOrigin"/> is the
/// provenance that <c>?? defaultTier</c> collapse would otherwise have destroyed, and it is the whole
/// reason "the author never classified this task" is a question a folder can still answer.</para>
///
/// <para><b>The judge side is a different rule, not the same rule applied twice (SSOT §4.2 / §9.6).</b>
/// An absent frontmatter <c>tier</c> on a prompt judge does not mean "undefined" — it means <i>the
/// judge's rung follows the actor it guards</i>. So an untagged judge is only a finding when there is no
/// classified actor for it to follow. Flagging every untagged judge would fire on almost every
/// configured plan, which is how a check gets muted.</para>
/// </summary>
public static class TierClassificationAudit
{
    /// <summary>
    /// Will report whether <paramref name="plan"/> has OPTED IN to tiering — the gate every other member
    /// of this audit stands behind (DoR Invariant 7). The predicate is the two-part one already codified
    /// as <c>NoRoutingGolden.IsUnconfiguredForTiering</c> in the integration suite, restated here over the
    /// loaded <see cref="PlanDefinition"/> because that helper is <c>internal</c> to a different test
    /// assembly and cannot be called: <b>a plan is tiering-configured when some <c>promptRunners</c> block
    /// declares a <c>routing</c> block, or the config declares a top-level <c>tiering</c> block.</b> Do not
    /// invent a second spelling of it.
    ///
    /// <para>False means a plan generated before tiering shipped, and the audit must then produce nothing
    /// at all — not a softer finding, not an advisory. A single-model user who never asked for any of this
    /// must never be told their plan is under-classified.</para>
    /// </summary>
    public static bool IsTieringConfigured(PlanDefinition plan) =>
        plan.Config.PromptRunners.Values.Any(runner => runner.Routing is not null)
        || plan.Config.Tiering is not null;

    /// <summary>
    /// Will report every unclassified subject in <paramref name="plan"/>, in a deterministic order.
    /// EMPTY means every subject this audit could see carries a classification of its own — or that
    /// <see cref="IsTieringConfigured"/> is false, in which case the audit reports nothing whatsoever.
    ///
    /// <para>A subject is DISCHARGED — and so is never a finding — by any of:</para>
    /// <list type="bullet">
    ///   <item>a prompt task whose <see cref="ActionDefinition.TierOrigin"/> is
    ///     <see cref="TierOrigin.Task"/>: the author classified it at its own site;</item>
    ///   <item>a prompt task carrying a pin — <see cref="ActionDefinition.Runner"/>,
    ///     <see cref="ActionDefinition.Model"/> or <see cref="ActionDefinition.Effort"/>: the author made a
    ///     deliberate task-specific routing statement at the action site, so no rung is owed. (Only
    ///     <c>runner</c>/<c>model</c> BYPASS resolution — <c>effort</c> alone still routes by tier and only
    ///     overrides that route's effort — but all three are the author having decided, which is what this
    ///     audit is asking about;)</item>
    ///   <item>a prompt judge whose <see cref="GuardrailDefinition.Tier"/> is present (frontmatter, and
    ///     frontmatter is its only source), or whose guarded task is itself classified — SSOT §4.2's
    ///     "the judge's rung follows the actor's".</item>
    /// </list>
    ///
    /// <para>A tier supplied by the plan-wide <c>tiering.defaultTier</c>
    /// (<see cref="TierOrigin.PlanDefault"/>) does NOT discharge anything: it is the fallback the author
    /// never made a decision about, and it is precisely the case this audit exists to surface. A script
    /// task is not a subject at all — it runs no model, so it is not a subject that passed.</para>
    ///
    /// <para>Every finding's <see cref="TierClassificationFinding.Detail"/> must NAME THE REMEDY at the
    /// site the fix is made — an <c>action.tier</c> for a task, a frontmatter <c>tier</c> for a judge. A
    /// finding that says "this is wrong" without saying where it belongs sends the author hunting, which
    /// is how a rule stops being applied.</para>
    /// </summary>
    public static IReadOnlyList<TierClassificationFinding> Audit(PlanDefinition plan)
    {
        // THE GATE, and it is first for a reason: everything below this line assumes the author asked for
        // tiering. A pre-tiering plan is not "fully classified" and it is not "under-classified" either —
        // the question does not apply to it, so nothing is reported.
        if (!IsTieringConfigured(plan))
        {
            return [];
        }

        List<TierClassificationFinding> findings = [];

        foreach (TaskNode task in plan.Tasks.Where(t => t.Action.Kind == ActionKind.Prompt))
        {
            if (IsClassified(task.Action))
            {
                continue;
            }

            findings.Add(new TierClassificationFinding(
                task.Id,
                TierClassificationSubject.PromptTask,
                task.Action.Tier,
                task.Action.TierOrigin,
                $"'{task.Id}' runs a prompt, but nobody classified it: {DescribeRung(task.Action)}. " +
                "Add an \"action.tier\" (easy|medium|hard) to this task's task.json — or, if its route was " +
                "a deliberate task-specific decision rather than a rung, pin it there with \"model\", " +
                "\"runner\" or \"effort\"."));
        }

        foreach (JudgeSubject judge in JudgeSubjects(plan))
        {
            if (IsClassified(judge))
            {
                continue;
            }

            findings.Add(new TierClassificationFinding(
                judge.SubjectId,
                TierClassificationSubject.PromptJudge,
                judge.Guardrail.Tier,
                TierOrigin.None,
                $"The prompt judge '{judge.Guardrail.Name}' declares no rung of its own and " +
                $"{judge.WhyNothingToFollow}, so SSOT §4.2's \"the judge's rung follows the actor it " +
                "guards\" has nothing to resolve against. Add a `tier:` key to the frontmatter of " +
                $"{judge.Guardrail.Name}.prompt.md — frontmatter is a judge's only site, because no " +
                "plan-wide default stands behind one."));
        }

        // Subject id, ordinal — the same key ClassifiableSubjects orders by, so a reader can line the two
        // lists up. LINQ's sort is stable, and the enumerations above are themselves deterministic, so two
        // subjects sharing an id (a task's preflight and guardrail of the same name) still order stably.
        return [.. findings.OrderBy(f => f.SubjectId, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Will name every subject this audit RECOGNISES in <paramref name="plan"/>, classified or not, in a
    /// deterministic order — so a test can assert the audit was not vacuous before asserting it found
    /// nothing. An audit reporting no findings because it recognised no subjects is green for the wrong
    /// reason, which is the passing-but-blind shape this whole review net exists to remove.
    ///
    /// <para>The population is exactly: every prompt-action task, plus every surviving prompt judge.
    /// Script tasks and script guardrails are absent — they run no model, so they can carry no rung.
    /// Each entry uses the same identity <see cref="TierClassificationFinding.SubjectId"/> does.</para>
    /// </summary>
    public static IReadOnlyList<string> ClassifiableSubjects(PlanDefinition plan) =>
    [
        .. plan.Tasks.Where(t => t.Action.Kind == ActionKind.Prompt).Select(t => t.Id)
             .Concat(JudgeSubjects(plan).Select(j => j.SubjectId))
             .OrderBy(id => id, StringComparer.Ordinal)
    ];

    /// <summary>The pseudo task-id a judge in <c>&lt;plan&gt;/guardrails|preflights/</c> is named under.</summary>
    public const string PlanRootOwner = "<plan>";

    // ---- the two predicates ------------------------------------------------------------------------

    /// <summary>
    /// Whether a prompt task's author CLASSIFIED it — the crux of the whole audit, and the reason
    /// <see cref="ActionDefinition.Tier"/> is never read here. Only <see cref="TierOrigin.Task"/> means the
    /// rung came from this task's own <c>task.json</c>; <see cref="TierOrigin.PlanDefault"/> is a non-null
    /// tier nobody chose, which is precisely the finding. A pin is the other way an author states a
    /// decision at the action site, so it discharges too.
    /// </summary>
    private static bool IsClassified(ActionDefinition action) =>
        action.TierOrigin == TierOrigin.Task
        || action.Runner is not null
        || action.Model is not null
        || action.Effort is not null;

    /// <summary>
    /// Whether a prompt judge is answered for — its own frontmatter <c>tier</c>, or a classified actor for
    /// SSOT §4.2's inheritance to follow. Flagging an untagged judge that DOES have a classified actor
    /// would fire on almost every configured plan, and a check that fires on almost every plan gets muted.
    ///
    /// <para>A judge guarding a SCRIPT task is not discharged: §4.2 has the judge follow <i>the actor's
    /// rung</i>, and a script actor runs no model, so it has none to lend. That is the same "no actor to
    /// follow" as a root gate, reached by a different route.</para>
    /// </summary>
    private static bool IsClassified(JudgeSubject judge) =>
        judge.Guardrail.Tier is not null
        || (judge.Actor is { } actor
            && actor.Action.Kind == ActionKind.Prompt
            && IsClassified(actor.Action));

    // ---- the population ----------------------------------------------------------------------------

    /// <summary>
    /// Every surviving prompt judge in the plan, paired with the actor it guards (null when it guards no
    /// task at all), in a deterministic order. Sweeps every folder a judge can live in: the plan root, each
    /// wave's entry and exit gates, and each task's own preflights and guardrails — a judge missed here is
    /// silently absent from BOTH the findings and the census, which is the blind-but-green shape the census
    /// exists to expose.
    /// </summary>
    private static IEnumerable<JudgeSubject> JudgeSubjects(PlanDefinition plan)
    {
        foreach (JudgeSubject judge in RootJudges(plan.PlanPreflights, $"{PlanRootOwner}/preflights")
                     .Concat(RootJudges(plan.PlanGuardrails, $"{PlanRootOwner}/guardrails")))
        {
            yield return judge;
        }

        foreach (WaveNode wave in plan.Waves)
        {
            foreach (JudgeSubject judge in RootJudges(wave.Preflights, $"{wave.Dir}/preflights")
                         .Concat(RootJudges(wave.Guardrails, $"{wave.Dir}/guardrails")))
            {
                yield return judge;
            }
        }

        foreach (TaskNode task in plan.Tasks)
        {
            foreach (GuardrailDefinition guardrail in task.Preflights.Concat(task.Guardrails)
                         .Where(g => g.Kind == ActionKind.Prompt))
            {
                yield return new JudgeSubject($"{task.Id}/{guardrail.Name}", guardrail, task);
            }
        }
    }

    /// <summary>
    /// The prompt judges of one ROOT gate folder — a plan-root or wave-root gate, which guards the run or
    /// the wave rather than any one task. There is no actor to inherit from at all here, which is why these
    /// are a finding at every classification of everything else.
    /// </summary>
    private static IEnumerable<JudgeSubject> RootJudges(
        IReadOnlyList<GuardrailDefinition> gate, string owner) =>
        gate.Where(g => g.Kind == ActionKind.Prompt)
            .Select(g => new JudgeSubject($"{owner}/{g.Name}", g, Actor: null));

    /// <summary>One prompt judge and the actor (if any) whose rung SSOT §4.2 would have it follow.</summary>
    private sealed record JudgeSubject(string SubjectId, GuardrailDefinition Guardrail, TaskNode? Actor)
    {
        /// <summary>Why this judge has no rung to inherit — the middle clause of its finding.</summary>
        public string WhyNothingToFollow => Actor switch
        {
            null => "guards no task at all (it is a plan-root or wave-root gate, where no actor exists)",
            { Action.Kind: ActionKind.Script } actor =>
                $"the task it guards ('{actor.Id}') runs a script, so that actor has no rung to lend",
            { } actor => $"the task it guards ('{actor.Id}') is itself unclassified"
        };
    }

    // ---- rendering ---------------------------------------------------------------------------------

    /// <summary>
    /// What the subject's rung actually IS, so the finding can say "resolves to medium, but from the
    /// plan-wide default" rather than merely "missing". The pairing of a resolved tier with a
    /// <see cref="TierOrigin.PlanDefault"/> origin is the evidence that a resolved tier is not a
    /// classification.
    /// </summary>
    private static string DescribeRung(ActionDefinition action) => action.TierOrigin switch
    {
        TierOrigin.PlanDefault =>
            $"it resolves to '{action.Tier}', but only from the plan-wide \"tiering.defaultTier\" — a " +
            "fallback, not a decision",
        _ => "no rung resolved for it at all"
    };
}

/// <summary>Which kind of model-running subject a finding is about.</summary>
public enum TierClassificationSubject
{
    /// <summary>A task whose action is a prompt — the <c>action.tier</c> site.</summary>
    PromptTask,

    /// <summary>A surviving prompt guardrail — the frontmatter <c>tier</c> site (SSOT §4.2).</summary>
    PromptJudge
}

/// <summary>
/// One subject a tiering-configured plan left unclassified, with the remedy spelled out.
/// </summary>
/// <param name="SubjectId">
/// The task's <c>Id</c> for a prompt task; <c>&lt;taskId&gt;/&lt;guardrailName&gt;</c> for a task's judge;
/// <c>&lt;plan&gt;/guardrails/&lt;name&gt;</c> for a plan-root judge and
/// <c>&lt;waveDir&gt;/guardrails/&lt;name&gt;</c> for a wave-root one — the two judges that guard no task at
/// all. A root gate's ENTRY half is named the same way under <c>preflights/</c>: the gate folder is part of
/// a root judge's id precisely because a wave's entry and exit gates may each hold a <c>01-</c> file, and a
/// bare <c>&lt;waveDir&gt;/&lt;name&gt;</c> could not tell the two apart (nor tell either from a task id,
/// which is itself <c>&lt;waveDir&gt;/&lt;taskFolder&gt;</c>).
/// </param>
/// <param name="Kind">Which site the missing classification belongs at.</param>
/// <param name="ResolvedTier">
/// The tier the subject actually carries after load, or null when it carries none. For a task left on the
/// plan-wide default this is NON-NULL and the subject is STILL flagged: that pairing, with
/// <paramref name="Origin"/>, is the whole evidence that a resolved tier is not a classification.
/// </param>
/// <param name="Origin">
/// Where <paramref name="ResolvedTier"/> came from. <see cref="TierOrigin.PlanDefault"/> for a task the
/// plan-wide default filled in; <see cref="TierOrigin.None"/> for an untagged judge, because
/// <see cref="GuardrailDefinition.Tier"/> is bound from frontmatter and from nothing else — there is no
/// plan-wide default standing behind a judge.
/// </param>
/// <param name="Detail">An actionable sentence naming the site the classification belongs at.</param>
public sealed record TierClassificationFinding(
    string SubjectId,
    TierClassificationSubject Kind,
    string? ResolvedTier,
    TierOrigin Origin,
    string Detail)
{
    /// <inheritdoc />
    public override string ToString() => $"{Kind} [{SubjectId}]: {Detail}";
}
