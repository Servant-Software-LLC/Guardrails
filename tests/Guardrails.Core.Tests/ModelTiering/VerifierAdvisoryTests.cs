using System.Reflection;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Tests.ModelTiering;

/// <summary>
/// Stage 2, wave 3 — DoR <c>docs/plans/17-model-tiering.md</c> §6.5, the <b>verifier advisory</b>
/// (#229). These are the TDD-red tests for <see cref="VerifierAdvisory"/>; they compile against the
/// stub this task wrote and fail against it, and <c>10-implement-verifier-advisory</c> makes them pass.
///
/// <para><b>These are DECISION tests, not surface tests.</b> The two surface-named behaviours below
/// (<c>Preflight_…</c> and <c>Jit_…</c>) do not touch the <c>Scheduler</c> or the
/// <c>GuardrailRunner</c> — they assert what <see cref="VerifierAdvisory"/> DECIDES, because tasks 12
/// and 13 wire those decisions to the real surfaces and are both forbidden from re-deriving the rule.
/// If this class could not ask the questions without recomputing anything, those two tasks could not
/// comply, so the API is designed for those callers: a method that returns the finding
/// (<see cref="VerifierAdvisory.Evaluate"/>, and its per-plan form
/// <see cref="VerifierAdvisory.Preflight"/>) and a method that answers the should-log question
/// (<see cref="VerifierAdvisory.ShouldLog"/>).</para>
///
/// <para><b>Three readings this file settles, because an implementer must pick one:</b></para>
/// <list type="number">
///   <item><b>"Weak" is the SHARED predicate, never a threshold invented here.</b> §6.5 rule 4 +
///     §4.1's D21a: <c>strength</c> when declared, the provider-kind fallback when not
///     (<c>kind != "claude"</c> ⇒ weak-UNLESS-declared). It is written once, as
///     <see cref="TierResolver.IsWeakVerifier"/>, and the fixtures below derive
///     <see cref="JudgeResolution.Weak"/> from it rather than hand-setting it — a fixture that could
///     disagree with the resolver would let the advisory pass against a reality it never sees.</item>
///   <item><b>Equal-and-STRONG is NOT a condition.</b> Opus judging Opus is a real check. An advisory
///     that fires on the healthiest configuration in the registry is one an operator learns to
///     ignore, and then the run it should have caught goes past unread.</item>
///   <item><b>Recording and LOGGING are different questions.</b> The JIT boundary records the advisory
///     into provenance ALWAYS and logs only on a difference from the run-start prediction. A test that
///     merely checks "an advisory was produced" cannot tell the quiet path from the noisy one — and
///     the noisy one is what trains an operator to stop reading advisories at all.</item>
/// </list>
///
/// <para><b>Why the fixtures build resolutions directly instead of driving <c>TierResolver</c>.</b>
/// The resolver has its own suite (<c>JudgeResolutionTests</c>). These tests are about the decision
/// made ON a resolution, so the input is stated outright — which is also the only way to state the
/// case §6.5's second boundary exists for: an observed pair that DIFFERS from what a preflight
/// predicted is, by definition, a pair no correct resolver would produce.</para>
/// </summary>
[Trait("Category", "TierResolution")]
public sealed class VerifierAdvisoryTests
{
    // ── the condition ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// §6.5: a judge WEAKER than its actor is an advisory condition. Both spellings of "weaker" are
    /// asserted, because the design has two and an implementation that handles only the first passes a
    /// single-case test while missing the failure the route exists to prevent:
    ///
    /// <list type="bullet">
    ///   <item><b>By declared <c>strength</c></b> — a rank-2 judge grading a rank-9 actor.</item>
    ///   <item><b>By the provider-kind fallback (§4.1, verifier-only)</b> — an unranked
    ///     <c>openai-compat</c> judge grading a ranked Claude actor. Nobody declared a number here, and
    ///     the fallback is the whole reason the advisory can say anything at all in the registry a
    ///     first-time local-model user actually writes.</item>
    /// </list>
    ///
    /// <para>The message is asserted to NAME both blocks: it is the string that lands in
    /// <c>judge.advisory</c> provenance and the string the run-start line prints, and "a judge is weak"
    /// without the two names is a finding nobody can act on.</para>
    /// </summary>
    [Fact]
    public void WeakerJudge_IsAdvisoryCondition()
    {
        PromptRunnerConfig opus = Block("opus", "claude-opus", strength: 9);
        PromptRunnerConfig haiku = Block("haiku", "claude-haiku", strength: 2);

        VerifierAdvisoryFinding byStrength = Assert.IsType<VerifierAdvisoryFinding>(
            VerifierAdvisory.Evaluate(Actor(opus), JudgeOn(haiku), "01-task"));

        Assert.Equal(VerifierAdvisoryCondition.WeakerThanActor, byStrength.Condition);
        Assert.Equal("opus", byStrength.ActorRunner);
        Assert.Equal("haiku", byStrength.JudgeRunner);
        Assert.Equal("01-task", byStrength.TaskId);
        Assert.False(string.IsNullOrWhiteSpace(byStrength.Message), "the finding IS its message — it is what provenance records and the run-start line prints");
        Assert.Contains("haiku", byStrength.Message, StringComparison.Ordinal);
        Assert.Contains("opus", byStrength.Message, StringComparison.Ordinal);

        // The kind fallback: nobody ranked the local block, and `kind != claude` ⇒ weak-unless-declared.
        PromptRunnerConfig local = Block("local", "qwen-32b", kind: PromptRunnerKind.OpenAiCompat);
        Assert.True(TierResolver.IsWeakVerifier(local), "fixture check — the shared predicate is the oracle, not a number invented in this file");
        Assert.False(TierResolver.IsWeakVerifier(opus));

        VerifierAdvisoryFinding byKind = Assert.IsType<VerifierAdvisoryFinding>(
            VerifierAdvisory.Evaluate(Actor(opus), JudgeOn(local), "02-task"));

        Assert.Equal(VerifierAdvisoryCondition.WeakerThanActor, byKind.Condition);
        Assert.Equal("local", byKind.JudgeRunner);
    }

    /// <summary>
    /// §6.5 rule 4's second half, and the one an implementation drops: <b>equal-and-WEAK is an advisory
    /// condition too.</b> "Weaker than the actor" alone would miss the exact failure this route exists
    /// to prevent — a task on a local 32B whose judge guardrail runs on the SAME local 32B, where a
    /// plausible-but-wrong implementation and a plausible-but-wrong "looks good to me" agree and the
    /// run goes green over broken work. One blind spot talking to itself.
    ///
    /// <para>Three shapes, all equal-and-weak: the same block on both sides; two DIFFERENT unranked
    /// non-Claude blocks (neither out-ranks the other — weakness is decided by <c>strength</c> and
    /// <c>kind</c>, never by block identity, so two blind spots are not better than one); and the §6.5
    /// rule-5 DEGRADE, where a bump was wanted, the only stronger block is <c>costly: true</c>, and the
    /// judge stayed on the actor's own route. The third carries
    /// <see cref="VerifierAdvisoryFinding.Degraded"/> through from
    /// <see cref="JudgeResolution.Degraded"/> so the advisory can say the judge is no stronger than the
    /// work BECAUSE the stronger block is reserved — a fact knowable only inside the resolver, and one
    /// an operator would otherwise be left to guess at.</para>
    /// </summary>
    [Fact]
    public void EqualAndWeak_IsAdvisoryCondition()
    {
        PromptRunnerConfig local = Block("local", "qwen-32b", kind: PromptRunnerKind.OpenAiCompat);

        VerifierAdvisoryFinding itself = Assert.IsType<VerifierAdvisoryFinding>(
            VerifierAdvisory.Evaluate(Actor(local), JudgeOn(local), "01-task"));

        Assert.Equal(VerifierAdvisoryCondition.EqualAndWeak, itself.Condition);
        Assert.Equal("local", itself.ActorRunner);
        Assert.Equal("local", itself.JudgeRunner);
        Assert.False(itself.Degraded, "nothing was refused here — the judge simply had nowhere stronger to be");

        PromptRunnerConfig otherLocal = Block("local-b", "llama-70b", kind: PromptRunnerKind.OpenAiCompat);

        VerifierAdvisoryFinding sibling = Assert.IsType<VerifierAdvisoryFinding>(
            VerifierAdvisory.Evaluate(Actor(local), JudgeOn(otherLocal), "02-task"));

        Assert.Equal(VerifierAdvisoryCondition.EqualAndWeak, sibling.Condition);

        // §6.5 rule 5: the bump was WANTED and refused for cost, so the judge stayed at the actor's route.
        VerifierAdvisoryFinding degraded = Assert.IsType<VerifierAdvisoryFinding>(
            VerifierAdvisory.Evaluate(Actor(local), JudgeOn(local, degraded: true), "03-task"));

        Assert.Equal(VerifierAdvisoryCondition.EqualAndWeak, degraded.Condition);
        Assert.True(degraded.Degraded, "the costly refusal is the resolver's own datum and rides the finding — the advisory reports it, it does not re-derive it");
    }

    /// <summary>
    /// The EXCLUSION, and it matters as much as the condition: <b>equal-and-STRONG is not flagged.</b>
    /// Opus judging Opus is a real check (§6.5 rule 4), and an advisory that fired on it would fire on
    /// the healthiest configuration in the registry — which trains an operator to ignore advisories
    /// wholesale, and then the weak-judge run it exists to catch goes past unread.
    ///
    /// <para>Four non-conditions: the same ranked block on both sides; two DIFFERENT ranked blocks of
    /// equal strength; a STRONGER judge (the happy path — a successful rule-3 bump lands here); and an
    /// unranked Claude block judging itself, because §4.1's fallback reads <c>kind != "claude"</c> ⇒
    /// weak, so an unranked Claude block is NOT weak and "unranked" is not a synonym for "suspect".</para>
    ///
    /// <para>The preflight is asserted alongside, because "no finding" has to mean "no LINE" — a
    /// preflight that emitted a silent finding object would satisfy a null check and still put a line
    /// on the operator's screen.</para>
    /// </summary>
    [Fact]
    public void EqualAndStrong_NoAdvisory()
    {
        PromptRunnerConfig opus = Block("opus", "claude-opus", strength: 9);
        PromptRunnerConfig opusPeer = Block("opus-peer", "claude-opus", strength: 9);
        PromptRunnerConfig sonnet = Block("sonnet", "claude-sonnet", strength: 5);
        PromptRunnerConfig unranked = Block("claude-unranked", "claude-sonnet");

        Assert.Null(VerifierAdvisory.Evaluate(Actor(opus), JudgeOn(opus), "01-task"));
        Assert.Null(VerifierAdvisory.Evaluate(Actor(opus), JudgeOn(opusPeer), "02-task"));
        Assert.Null(VerifierAdvisory.Evaluate(Actor(sonnet), JudgeOn(opus), "03-task"));

        Assert.False(TierResolver.IsWeakVerifier(unranked), "an unranked CLAUDE block is not weak — §4.1's fallback is kind != claude, and 'unranked' is not a synonym for 'suspect'");
        Assert.Null(VerifierAdvisory.Evaluate(Actor(unranked), JudgeOn(unranked), "04-task"));

        IReadOnlyList<VerifierAdvisoryFinding> lines = VerifierAdvisory.Preflight(
        [
            new VerifierAdvisoryPair("01-task", Actor(opus), JudgeOn(opus)),
            new VerifierAdvisoryPair("03-task", Actor(sonnet), JudgeOn(opus))
        ]);

        Assert.Empty(lines);
    }

    // ── it never halts ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Advisory means advisory.</b> No error, no load-time refusal, no halt — in attended OR
    /// unattended mode (§6.5, charter Decision 10). The harness does not block on a model-quality
    /// opinion, and §12.6 states that no verifier condition may ever fail a build.
    ///
    /// <para>This is the property a later reader is most likely to "improve" into a gate, so it is
    /// asserted three ways rather than described:</para>
    /// <list type="number">
    ///   <item><b>Total, on every input.</b> Every advisory condition, the degraded case, a null actor
    ///     route (the re-verification path), a judge that resolved no block at all, and an empty
    ///     preflight — none throws. A throw AT the JIT boundary IS the halt: it would abort the attempt
    ///     that a real judge was about to grade, which is strictly worse than no advisory.</item>
    ///   <item><b>Mode cannot change it, because mode is not an input.</b> Attended vs unattended is
    ///     spelled <see cref="AutonomyPolicy"/> everywhere in this harness. No entry point here takes
    ///     one, so "the same in attended or unattended mode" is a property of the TYPE and not of a
    ///     fixture that happens to pass both values.</item>
    ///   <item><b>There is no halt outcome to return.</b> <see cref="TierResolution"/> has
    ///     <see cref="TierResolution.NoRoute"/> — the ACTOR halts when nothing serves its rung.
    ///     <see cref="JudgeResolution"/> deliberately has no counterpart, and the finding carries no
    ///     severity, code or outcome. Degrade what is advisory; halt what is load-bearing.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void Advisory_NeverHalts_RunProceeds()
    {
        PromptRunnerConfig opus = Block("opus", "claude-opus", strength: 9);
        PromptRunnerConfig haiku = Block("haiku", "claude-haiku", strength: 2);
        PromptRunnerConfig local = Block("local", "qwen-32b", kind: PromptRunnerKind.OpenAiCompat);

        (TierResolution? Actor, JudgeResolution Judge)[] worstCases =
        [
            (Actor(opus), JudgeOn(haiku)),                    // weaker than its actor
            (Actor(local), JudgeOn(local)),                   // equal-and-weak
            (Actor(local), JudgeOn(local, degraded: true)),   // the rule-5 costly refusal
            (Actor(opus), JudgeOn(opus)),                     // no condition at all
            (null, JudgeOn(local)),                           // the re-verification path — no actor route
            (Actor(opus), Unresolved())                       // a judge that resolved no block whatsoever
        ];

        foreach ((TierResolution? actor, JudgeResolution judge) in worstCases)
        {
            VerifierAdvisoryFinding? finding = null;
            Exception? failure = Record.Exception(() => finding = VerifierAdvisory.Evaluate(actor, judge, "01-task"));
            Assert.Null(failure);

            object? logDecision = null;
            Exception? logFailure = Record.Exception(() => logDecision = VerifierAdvisory.ShouldLog(finding, finding));
            Assert.Null(logFailure);
            Assert.NotNull(logDecision);

            object? lines = null;
            Exception? preflightFailure = Record.Exception(
                () => lines = VerifierAdvisory.Preflight([new VerifierAdvisoryPair("01-task", actor, judge)]));
            Assert.Null(preflightFailure);
            Assert.NotNull(lines);
        }

        Assert.Empty(VerifierAdvisory.Preflight([]));

        // 2. Mode is not an input, so unattended cannot differ from attended.
        MethodInfo[] api = typeof(VerifierAdvisory)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.NotEmpty(api);
        Assert.DoesNotContain(api.SelectMany(m => m.GetParameters()), p => p.ParameterType == typeof(AutonomyPolicy));

        // 3. There is no halt outcome for a verifier condition to return — unlike the actor's route.
        Assert.NotNull(typeof(TierResolution).GetProperty(nameof(TierResolution.NoRoute)));
        Assert.Null(typeof(JudgeResolution).GetProperty("NoRoute"));
    }

    // ── the de-duplication rule — the reason three surfaces are tolerable ───────────────────────

    /// <summary>
    /// §6.5's de-duplication ruling, part one: the <b>preflight</b> emits ONE pre-run summary line per
    /// AFFECTED task — and nothing for the tasks whose judge is strong enough. Task 13 wires this to
    /// the run-start <c>IRunObserver</c> event; this pins the DECISION, so that task raises one event
    /// per finding rather than deciding for itself what "affected" means.
    ///
    /// <para>Both halves are asserted, because each fails differently: emitting for the unaffected task
    /// is the noise that trains an operator to stop reading the advisory, and emitting twice for one
    /// affected task is the same noise wearing the other hat. Input order is preserved so the pre-run
    /// summary reads in plan order.</para>
    /// </summary>
    [Fact]
    public void Preflight_EmitsOneLinePerAffectedTask()
    {
        PromptRunnerConfig opus = Block("opus", "claude-opus", strength: 9);
        PromptRunnerConfig haiku = Block("haiku", "claude-haiku", strength: 2);
        PromptRunnerConfig local = Block("local", "qwen-32b", kind: PromptRunnerKind.OpenAiCompat);

        IReadOnlyList<VerifierAdvisoryFinding> lines = VerifierAdvisory.Preflight(
        [
            new VerifierAdvisoryPair("01-weaker", Actor(opus), JudgeOn(haiku)),      // affected
            new VerifierAdvisoryPair("02-healthy", Actor(opus), JudgeOn(opus)),      // NOT affected
            new VerifierAdvisoryPair("03-equal-weak", Actor(local), JudgeOn(local)), // affected
            new VerifierAdvisoryPair("04-bumped", Actor(local), JudgeOn(opus))       // NOT affected — the bump worked
        ]);

        Assert.Equal(2, lines.Count);
        Assert.Equal("01-weaker", lines[0].TaskId);
        Assert.Equal("03-equal-weak", lines[1].TaskId);
        Assert.DoesNotContain(lines, f => f.TaskId == "02-healthy");
        Assert.DoesNotContain(lines, f => f.TaskId == "04-bumped");
    }

    /// <summary>
    /// §6.5's de-duplication ruling, part two: the JIT re-check records <c>judge.advisory</c> into that
    /// attempt's provenance <b>ALWAYS</b> — on every judge resolution, and independently of whether
    /// anything should be LOGGED. Task 12 wires this to the judge provenance object; this pins the
    /// decision, so "recorded always" and "logged sometimes" cannot collapse into one answer.
    ///
    /// <para>The two halves are asserted against the SAME finding: the quiet path (the preflight
    /// predicted exactly this, so nothing is logged) still yields the finding to record, and so does
    /// the case where the preflight predicted nothing at all. The run summary aggregates from
    /// provenance, which is what makes the quieter log lossless — and an implementation that returned
    /// the finding only when it was worth logging would silently empty that aggregate.</para>
    /// </summary>
    [Fact]
    public void Jit_RecordsAdvisoryIntoProvenance_Always()
    {
        PromptRunnerConfig opus = Block("opus", "claude-opus", strength: 9);
        PromptRunnerConfig haiku = Block("haiku", "claude-haiku", strength: 2);

        VerifierAdvisoryFinding predicted = Assert.IsType<VerifierAdvisoryFinding>(
            VerifierAdvisory.Evaluate(Actor(opus), JudgeOn(haiku)));

        VerifierAdvisoryFinding observed = Assert.IsType<VerifierAdvisoryFinding>(
            VerifierAdvisory.Evaluate(Actor(opus), JudgeOn(haiku)));

        Assert.False(VerifierAdvisory.ShouldLog(predicted, observed), "the preflight already said this — the quiet path");
        Assert.False(string.IsNullOrWhiteSpace(observed.Message), "recorded ALWAYS means the text is there to record, log line or not");

        // ... and equally when the preflight predicted nothing: recording never depends on logging.
        Assert.True(VerifierAdvisory.ShouldLog(null, observed));
        Assert.NotNull(VerifierAdvisory.Evaluate(Actor(opus), JudgeOn(haiku)));
    }

    /// <summary>
    /// §6.5's de-duplication ruling, part three — <b>the rule itself</b>: the JIT re-check emits a log
    /// line ONLY when the observed pair differs from what the preflight predicted. That is the
    /// interesting case, and the only one the preflight did not already say.
    ///
    /// <para>Five cases, because "differs" has five answers and a naive implementation gets the
    /// null pairs wrong: agreement is quiet; a different observed pair speaks; a finding appearing
    /// where none was predicted speaks (a mid-run <c>guardrails.json</c> edit, an overwatcher-applied
    /// action change, a hand-edit between waves — none of them existed when the preflight ran); a
    /// finding VANISHING speaks for the same reason; and two nulls are not a difference.</para>
    ///
    /// <para>The "different pair, same sentence" case is deliberate: the comparison is over the
    /// observed PAIR, not over the message text. A judge that resolved somewhere else than predicted is
    /// a difference even when the sentence reads identically, and an implementation comparing only
    /// <see cref="VerifierAdvisoryFinding.Message"/> would go quiet on exactly the resolver bug the
    /// second boundary exists to catch.</para>
    /// </summary>
    [Fact]
    public void Jit_LogsOnlyWhenObservedDiffersFromPreflight()
    {
        PromptRunnerConfig opus = Block("opus", "claude-opus", strength: 9);
        PromptRunnerConfig haiku = Block("haiku", "claude-haiku", strength: 2);
        PromptRunnerConfig local = Block("local", "qwen-32b", kind: PromptRunnerKind.OpenAiCompat);

        VerifierAdvisoryFinding predicted = Assert.IsType<VerifierAdvisoryFinding>(
            VerifierAdvisory.Evaluate(Actor(opus), JudgeOn(haiku), "01-task"));
        VerifierAdvisoryFinding same = Assert.IsType<VerifierAdvisoryFinding>(
            VerifierAdvisory.Evaluate(Actor(opus), JudgeOn(haiku), "01-task"));
        VerifierAdvisoryFinding elsewhere = Assert.IsType<VerifierAdvisoryFinding>(
            VerifierAdvisory.Evaluate(Actor(opus), JudgeOn(local), "01-task"));

        Assert.False(VerifierAdvisory.ShouldLog(predicted, same), "agreement is the NORMAL case and the quiet path — saying it twice is what trains an operator to stop reading advisories");
        Assert.True(VerifierAdvisory.ShouldLog(predicted, elsewhere), "the judge resolved somewhere the preflight did not predict — a resolver bug or a mid-run mutation, and the only thing the preflight could not say");
        Assert.True(VerifierAdvisory.ShouldLog(null, predicted), "a finding appearing where none was predicted is a difference");
        Assert.True(VerifierAdvisory.ShouldLog(predicted, null), "and a predicted finding that did not materialise is one too");
        Assert.False(VerifierAdvisory.ShouldLog(null, null), "no condition either side — there is nothing to say");

        // The comparison is over the PAIR, not the sentence: same text, different judge block ⇒ log.
        VerifierAdvisoryFinding twin = predicted with { JudgeRunner = "somewhere-else" };
        Assert.Equal(predicted.Message, twin.Message);
        Assert.True(VerifierAdvisory.ShouldLog(predicted, twin), "same sentence, different judge block — the observed PAIR differs, which is what §6.5 compares");
    }

    /// <summary>
    /// <b>Agreement is the normal case</b>, and this is why BOTH boundaries exist rather than one. The
    /// preflight is a MODEL of the resolver; the JIT check IS the resolver. In a correct static
    /// implementation they agree — and that agreement is the point: the two are a mirror-check, and any
    /// disagreement is by definition a resolver bug that no amount of preflight sophistication could
    /// catch. (This repo has already paid for the general version of that lesson — #382, where a static
    /// check mirroring the real path certified green while the real path was broken.)
    ///
    /// <para>So the normal case must be QUIET and still RECORDED: one pre-run line from the preflight,
    /// provenance from the JIT boundary, and no second log line saying the same thing.</para>
    /// </summary>
    [Fact]
    public void PreflightAndJit_Agree_IsTheQuietPath()
    {
        PromptRunnerConfig opus = Block("opus", "claude-opus", strength: 9);
        PromptRunnerConfig haiku = Block("haiku", "claude-haiku", strength: 2);

        IReadOnlyList<VerifierAdvisoryFinding> preflight = VerifierAdvisory.Preflight(
            [new VerifierAdvisoryPair("01-task", Actor(opus), JudgeOn(haiku))]);

        VerifierAdvisoryFinding predicted = Assert.Single(preflight);

        // The JIT boundary re-resolves the SAME task and observes what the resolver actually returned.
        VerifierAdvisoryFinding observed = Assert.IsType<VerifierAdvisoryFinding>(
            VerifierAdvisory.Evaluate(Actor(opus), JudgeOn(haiku), "01-task"));

        Assert.Equal(predicted, observed);
        Assert.False(VerifierAdvisory.ShouldLog(predicted, observed), "the preflight and the resolver agree — the mirror-check passed, so there is nothing new to say");
        Assert.False(string.IsNullOrWhiteSpace(observed.Message), "quiet is not the same as unrecorded — the JIT boundary still has a finding to put in provenance");
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One <c>promptRunners</c> block. <paramref name="strength"/> left null is the UNRANKED case §4.1's
    /// provider-kind fallback exists for, and <paramref name="kind"/> is the other half of that
    /// fallback — the two axes <see cref="TierResolver.IsWeakVerifier"/> reads, and the only two this
    /// file varies.
    /// </summary>
    private static PromptRunnerConfig Block(
        string name,
        string? model,
        int? strength = null,
        PromptRunnerKind kind = PromptRunnerKind.Claude) => new()
        {
            Name = name,
            Command = "claude",
            Settings = new PromptRunnerSettings { Model = model },
            Kind = kind,
            Strength = strength
        };

    /// <summary>
    /// An ACTOR resolution on <paramref name="block"/>, in the shape <see cref="TierResolver.Resolve"/>
    /// returns: the block, its name and its model, at a rung.
    /// </summary>
    private static TierResolution Actor(PromptRunnerConfig block, string tier = ActionTiers.Medium) => new()
    {
        Runner = block,
        RunnerName = block.Name,
        Model = block.Settings.Model,
        RequestedTier = tier,
        Tier = tier
    };

    /// <summary>
    /// A JUDGE resolution on <paramref name="block"/>.
    ///
    /// <para><see cref="JudgeResolution.Weak"/> is derived from
    /// <see cref="TierResolver.IsWeakVerifier"/> rather than hand-set, exactly as
    /// <see cref="TierResolver.ResolveJudge"/> derives it. A fixture free to disagree with the shared
    /// predicate would let the advisory pass against a reality it never sees — which is the D22a
    /// divergence reproduced inside the test suite instead of inside the product.</para>
    /// </summary>
    private static JudgeResolution JudgeOn(
        PromptRunnerConfig block,
        string tier = ActionTiers.Medium,
        bool degraded = false,
        bool bumped = false) => new()
        {
            Runner = block,
            RunnerName = block.Name,
            Kind = block.Kind,
            Model = block.Settings.Model,
            Tier = tier,
            Strength = block.Strength,
            Weak = TierResolver.IsWeakVerifier(block),
            Bumped = bumped,
            Degraded = degraded
        };

    /// <summary>
    /// A judge resolution that selected NOTHING — no block, no name, no rung. The shared predicate
    /// counts a null block WEAK ("an unknown verifier is not a vouched-for one"), and the point of the
    /// fixture is that this degenerate input must still be answered rather than thrown on.
    /// </summary>
    private static JudgeResolution Unresolved() => new()
    {
        Weak = TierResolver.IsWeakVerifier(null)
    };
}
