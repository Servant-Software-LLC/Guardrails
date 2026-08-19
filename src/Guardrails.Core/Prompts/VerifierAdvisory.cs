namespace Guardrails.Core.Prompts;

/// <summary>
/// WHICH §6.5 condition a <see cref="VerifierAdvisoryFinding"/> reports. There are exactly two, and
/// the second is the one an implementer drops: "weaker than the actor" alone would miss the case the
/// whole verifier route exists for — a local 32B graded by the SAME local 32B, where a
/// plausible-but-wrong implementation and a plausible-but-wrong "looks good to me" agree.
///
/// <para><b>There is deliberately no third value for equal-and-STRONG.</b> Opus judging Opus is a real
/// check, and a design that flagged it would emit an advisory on the healthiest configuration in the
/// registry — which trains an operator to ignore all of them. "No condition" is spelled as a null
/// finding, not as an enum member.</para>
/// </summary>
public enum VerifierAdvisoryCondition
{
    /// <summary>
    /// The judge is STRICTLY less capable than the actor it grades — by declared <c>strength</c> when
    /// both sides declare one, and otherwise by §4.1's verifier-only provider-kind fallback (a weak
    /// judge grading a non-weak actor).
    /// </summary>
    WeakerThanActor,

    /// <summary>
    /// The judge is no weaker than its actor, but BOTH are weak (§6.5 rule 4's "one blind spot talking
    /// to itself"). This is also the shape §6.5 rule 5's costly DEGRADE produces: a bump was wanted,
    /// the only stronger block is <c>costly: true</c>, so the judge stayed on the actor's own route.
    /// </summary>
    EqualAndWeak
}

/// <summary>
/// ONE §6.5 advisory finding: "this judge is not strong enough to vouch for this actor". It is the
/// unit BOTH reporting surfaces consume — the run-start preflight line (one per affected task) and the
/// per-attempt JIT record in <c>judge.advisory</c> — so neither of them re-derives the rule.
///
/// <para><b>It carries no severity, no code and no outcome, and every one of those omissions is the
/// design.</b> §12.6 states that no verifier condition may ever fail a build, and §6.5.1 refuses this
/// condition a GR code for exactly that reason ("a GR code is a thing that can fail a build"). A
/// finding is a REPORT: the caller records it, prints it, and proceeds. There is nothing here for a
/// scheduler to branch on, which is what makes "advisory" a property of the type rather than a
/// convention a later reader can quietly drop.</para>
/// </summary>
public sealed record VerifierAdvisoryFinding
{
    /// <summary>
    /// The task this finding is about — the identity the run-start surface prints one line per. Null
    /// on the JIT path, where the finding is recorded into an attempt that already knows its own task
    /// and the id would be a second copy of it.
    /// </summary>
    public string? TaskId { get; init; }

    /// <summary>Which of the two §6.5 conditions fired.</summary>
    public required VerifierAdvisoryCondition Condition { get; init; }

    /// <summary>
    /// The ACTOR's resolved block name — half of "the observed pair" the de-duplication rule compares.
    /// Null when the actor resolved no block.
    /// </summary>
    public string? ActorRunner { get; init; }

    /// <summary>
    /// The JUDGE's resolved block name — the other half of the pair. Null when the judge resolved no
    /// block, which <see cref="TierResolver.IsWeakVerifier"/> counts as weak (an unknown verifier is
    /// not a vouched-for one).
    /// </summary>
    public string? JudgeRunner { get; init; }

    /// <summary>
    /// Mirrors <see cref="JudgeResolution.Degraded"/>: the §6.5 rule-5 costly refusal fired, so the
    /// judge is no stronger than its actor BECAUSE the stronger block is reserved rather than because
    /// no stronger block exists. Copied through, never re-derived — the resolver is the only place
    /// that knows it, and asking again downstream would need a second copy of the candidacy predicate
    /// (the D22a duplication one level down).
    /// </summary>
    public bool Degraded { get; init; }

    /// <summary>
    /// The human-readable line. This is the string that lands in <c>judge.advisory</c> provenance and
    /// the string the run-start observer prints, so it must stand alone: name the judge's block and the
    /// actor's block, because "a judge is weak" without the two names is a finding nobody can act on.
    /// </summary>
    public required string Message { get; init; }
}

/// <summary>
/// One task's §6.5 <b>(actor, judge) pair</b>, tagged with the task it belongs to — the input the
/// run-start preflight walks. The caller omits tasks that resolve no judge at all (a script action,
/// or a plan where tiering is unconfigured): a pair that cannot produce a finding is not a pair worth
/// carrying, and the preflight's contract is one line per AFFECTED task.
/// </summary>
/// <param name="TaskId">The task's id — what the run-start line is keyed by.</param>
/// <param name="Actor">The actor's resolved route, or null on a path that has no action attempt.</param>
/// <param name="Judge">The judge's resolved route for that task.</param>
public readonly record struct VerifierAdvisoryPair(string TaskId, TierResolution? Actor, JudgeResolution Judge);

/// <summary>
/// The DoR <c>docs/plans/17-model-tiering.md</c> §6.5 <b>verifier advisory</b> (#229), as a pure
/// decision: given an actor route and the judge route that grades it, is this a condition to report —
/// and, at the JIT boundary, is it a condition to LOG or one the run-start line already said?
///
/// <para><b>Three questions, three answers, and NO gate.</b> Nothing here halts, throws, refuses, or
/// takes an <c>AutonomyPolicy</c>: the harness does not block on a model-quality opinion, in
/// attended or unattended mode (§6.5, charter Decision 10). The asymmetry is deliberate and stated —
/// an unsatisfiable ACTOR route halts (<see cref="TierResolution.NoRoute"/>, GR2048); an
/// unsatisfiable VERIFIER preference degrades and the run proceeds. Degrade what is advisory; halt
/// what is load-bearing.</para>
///
/// <para><b>Why this is a unit and not two call sites.</b> §6.5's de-duplication ruling is what makes
/// three reporting surfaces tolerable rather than noise, and it only works if all of them ask the SAME
/// object the SAME questions: the preflight emits one line per affected task, the JIT re-check records
/// into provenance ALWAYS and logs ONLY when what it observed differs from what the preflight
/// predicted. A second implementation of "is this judge weak" is the exact divergence D22a forbids for
/// candidacy — so weakness is decided ONCE, by <see cref="TierResolver.IsWeakVerifier"/>, and read
/// from <see cref="JudgeResolution.Weak"/> rather than re-tested here.</para>
/// </summary>
public static class VerifierAdvisory
{
    /// <summary>
    /// THE condition (§6.5 rules 4 + 5). Returns the finding when the judge is weaker than its actor
    /// or equal-and-weak, and <c>null</c> when it is not — equal-and-STRONG is a real check and is not
    /// a finding.
    ///
    /// <para>Called at BOTH boundaries: the run-start preflight asks it per task (via
    /// <see cref="Preflight"/>), and the JIT re-check asks it on every judge resolution so the answer
    /// can be recorded into that attempt's <c>judge.advisory</c> provenance ALWAYS — independently of
    /// whether anything should be logged. Recording and logging are different questions; this method
    /// answers the first and <see cref="ShouldLog"/> answers the second.</para>
    ///
    /// <para>A null <paramref name="actor"/> is a first-class input, not an error: the re-verification
    /// path (a human's in-place fix) has no action attempt and therefore no actor route. With no actor
    /// there is no "weaker than" relation to find, so there is no finding — do not invent a
    /// comparison.</para>
    /// </summary>
    /// <param name="actor">The actor's resolved route for this attempt, or null when there is none.</param>
    /// <param name="judge">The judge's resolved route, whose <see cref="JudgeResolution.Weak"/> is the
    /// shared §6.5-rule-4 verdict for the block that was actually selected.</param>
    /// <param name="taskId">The task the finding is about, for the run-start surface. Null on the JIT
    /// path, where the attempt already knows its own task.</param>
    /// <returns>The finding, or null when there is no advisory condition.</returns>
    public static VerifierAdvisoryFinding? Evaluate(TierResolution? actor, JudgeResolution judge, string? taskId = null) =>
        throw new NotImplementedException(
            "§6.5 verifier advisory: weaker-than-actor / equal-and-weak is a finding, equal-and-strong is not. Implemented by 10-implement-verifier-advisory.");

    /// <summary>
    /// The RUN-START surface's decision: exactly ONE finding per AFFECTED task, and nothing at all for
    /// the tasks whose judge is strong enough. Input order is preserved so the pre-run summary reads in
    /// plan order.
    ///
    /// <para>This is <see cref="Evaluate"/> over a set, and it is exposed as its own entry point so the
    /// caller that walks the plan does not decide for itself what "affected" means — the one-line-per-
    /// affected-task half of the de-duplication ruling lives HERE, next to the condition it depends
    /// on.</para>
    /// </summary>
    /// <param name="pairs">Each task's (actor, judge) pair. A task with no judge is simply absent.</param>
    /// <returns>One finding per affected task, in input order; empty when nothing is affected.</returns>
    public static IReadOnlyList<VerifierAdvisoryFinding> Preflight(IEnumerable<VerifierAdvisoryPair> pairs) =>
        throw new NotImplementedException(
            "§6.5 preflight: one finding per AFFECTED task, none for the rest. Implemented by 10-implement-verifier-advisory.");

    /// <summary>
    /// The DE-DUPLICATION decision (§6.5): should the JIT re-check emit a log line, given what the
    /// run-start preflight PREDICTED and what this attempt actually OBSERVED? <b>Only when they
    /// differ.</b>
    ///
    /// <para>Agreement is the normal case and the quiet path — in a correct static implementation the
    /// preflight (a MODEL of the resolver) and the JIT check (the resolver ITSELF) agree, and that
    /// agreement is the point: the two are a mirror-check, and a disagreement is by definition a
    /// resolver bug no preflight could catch, or a mid-run mutation that did not exist when the
    /// preflight ran. Saying it twice on the quiet path is what trains an operator to stop reading
    /// advisories, so the interesting case is the ONLY case that speaks.</para>
    ///
    /// <para>"Differ" is over the observed PAIR, not merely over the message text: a run whose judge
    /// resolved somewhere else than predicted is a difference even when the sentence reads the same.
    /// A finding appearing where none was predicted (and vice versa) is a difference too; two nulls
    /// are not.</para>
    /// </summary>
    /// <param name="predicted">What the run-start preflight predicted for this task, or null if it
    /// predicted no finding.</param>
    /// <param name="observed">What the JIT re-check actually observed, or null for no finding.</param>
    /// <returns>True only when the observation differs from the prediction.</returns>
    public static bool ShouldLog(VerifierAdvisoryFinding? predicted, VerifierAdvisoryFinding? observed) =>
        throw new NotImplementedException(
            "§6.5 de-duplication: log ONLY when the observed pair differs from the preflight's prediction. Implemented by 10-implement-verifier-advisory.");
}
