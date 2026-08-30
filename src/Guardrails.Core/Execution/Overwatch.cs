using System.Text;
using Guardrails.Core.Journal;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Execution;

/// <summary>
/// The active AI run supervisor (SSOT §9.2, design-of-record <c>docs/plans/11-overwatcher.md</c>, issue
/// #269). At a struggle boundary (<see cref="OverwatchTrigger"/>) it reasons "will more attempts help, or
/// is this structurally doomed?" and produces a diagnosis + a decision — but it is ALWAYS advisory: it can
/// grant an adjusted attempt (coupled to a SANCTIONED change) or halt honestly, and it can NEVER mark a
/// task succeeded, merge a fragment, or soften a deterministic guardrail's verdict.
///
/// <para>It SUBSUMES the shipped one-shot needs-human triage (§9.2.1) — that becomes the
/// <see cref="OverwatchTrigger.TerminalExhaustion"/> case, delegating to the composed
/// <see cref="NeedsHumanTriage"/> so its <c>feedback.md</c>/<c>triage.json</c> + advisory-never-gates
/// invariants are preserved verbatim. The new eager/short-circuit triggers run a diagnose prompt, classify
/// each proposed fix via the MECHANICAL ASYMMETRY (<see cref="OverwatchFixClassifier"/>), and decide per the
/// shared <see cref="AutonomyPolicy"/> (no new policy field).</para>
///
/// <para><b>v1 = diagnose + propose.</b> The diagnosis core is always on; <c>halt</c> always halts; <c>prompt</c>
/// (default) proposes the allowlist action-layer levers (guidance / budget) and applies on an interactive
/// approve, else honest-halts; <c>auto</c> DEGRADES to <c>prompt</c> behavior in v1 (silent auto-application
/// of the overwatcher's own fixes + persistent authoring-defect fixes are the v2 bet). The deterministic
/// short-circuits (#174/#264/#94) remain the FLOOR: "no sanctioned change ⇒ no grant ⇒ honest halt."</para>
/// </summary>
public sealed class Overwatch
{
    /// <summary>The hard ceiling on extra attempts a single grant may add (bounded-grant invariant, doc 11 §5).</summary>
    private const int MaxExtraRetriesPerGrant = 2;

    /// <summary>
    /// The diagnose tool profile (SSOT §9.2, issue #452): READ BROADLY, WRITE NOTHING.
    ///
    /// <para>The overwatcher is a different class of actor from a task runner and neither of the two
    /// shipped confinement models fits it. <c>writeScope</c> is not applied to it (correctly — it authors
    /// no segment), and it does not inherit the plan's <c>promptRunners</c> allowlist either. Before this
    /// constant existed it fell through the gap between them and inherited
    /// <see cref="PromptRunnerSettings.AllowedTools"/>'s record default — an EMPTY list — so in a
    /// non-interactive subprocess with nobody to approve a prompt, every tool call it made was refused.
    /// It was asked to read attempt logs with permission to read nothing, and burned its whole turn
    /// budget re-trying blocked calls.</para>
    ///
    /// <para><b>Read/Glob/Grep and nothing else — deliberately not Bash.</b> Widening a Bash allowlist
    /// would leave it one unusual shell form away from the same silent death (the two refused calls in the
    /// #452 evidence were a <c>python</c> heredoc and a <c>for</c> loop, both reaching for file contents
    /// these three tools return directly). Granting no write tool at all also makes the "diagnose and
    /// propose, never edit" guarantee STRUCTURAL rather than merely advisory: today it is enforced after
    /// the fact by <see cref="OverwatchFixClassifier"/> on what the judge may PROPOSE; with this profile
    /// the judge has no mechanism to write in the first place. (The runner still appends its own read-only
    /// <c>Bash(git show*)</c> salvage grant to every invocation — read-only, so the write-none property
    /// holds.)</para>
    /// </summary>
    private static readonly IReadOnlyList<string> DiagnoseTools = ["Read", "Glob", "Grep"];

    /// <summary>
    /// The diagnose turn ceiling. Raised from 10 (issue #452): reading two or three attempt folders —
    /// a <c>feedback.md</c> and the tail of an action log each — is already 6–8 tool calls before a word
    /// of reasoning, and the observed failure terminated at exactly the old ceiling. <c>--max-turns</c> is
    /// a CEILING, not a target: a diagnose stops the moment it returns its JSON verdict, so the headroom
    /// costs a converging run nothing. The PATHOLOGICAL run is no longer bounded by this number at all —
    /// <see cref="DenialAbortThreshold"/> now cuts it off far earlier than the turn cap ever did.
    /// </summary>
    private const int DiagnoseMaxTurns = 20;

    /// <summary>
    /// Abort the diagnose after this many CONSECUTIVE permission-denied tool calls (issue #452).
    ///
    /// <para><b>Why 3.</b> One denial is recoverable and must not be punished — the intended reaction to a
    /// refused call is to reach for a granted tool instead, and cutting the run at the first refusal would
    /// forbid exactly that self-correction. Two shows the agent is not adapting. Three is conclusive: with
    /// a three-tool read-only profile there is no fourth route to try. The streak RESETS on any tool call
    /// that runs (<see cref="Prompts.ClaudePermissionScanner"/>), so a diagnose making real progress is
    /// never cut short. It bounds the pathological case at roughly 3 turns instead of the 11 turns / $0.66
    /// the shipped overwatcher spent producing nothing.</para>
    /// </summary>
    private const int DenialAbortThreshold = 3;

    private readonly IPromptRunner? _diagnoseRunner;
    private readonly NeedsHumanTriage? _terminalTriage;
    private readonly AutonomyPolicy _policy;
    private readonly IOverwatchInteraction _interaction;
    private readonly bool _autonomyBlockPresent;

    /// <param name="diagnoseRunner">
    /// The runner for the eager/short-circuit diagnose prompt (the reserved <c>overwatch</c> profile,
    /// resolved with fallback). Null disables the non-terminal diagnose (no runner ⇒ advisory no-action).
    /// </param>
    /// <param name="terminalTriage">
    /// The composed one-shot triage for the terminal-exhaustion case (§9.2.1). Null disables it (a
    /// script-only plan gets no overwatcher at all — the factory leaves the whole component null then).
    /// </param>
    /// <param name="policy">The shared <see cref="AutonomyPolicy"/> in force for this run.</param>
    /// <param name="interaction">The <c>prompt</c>-tier confirmation seam; defaults to non-interactive (honest halt).</param>
    /// <param name="autonomyBlockPresent">
    /// Whether the run's config carries an explicit <c>autonomy</c> block (doc 12 §9 Phase 4, doc 11 §9.6). The
    /// <c>auto</c>-tier gate keys on the PRESENCE of this block — NOT on <c>autonomyPolicy: auto</c> alone (the
    /// anti-Option-(c) guard): only a block-present <c>auto</c> tier silently auto-applies a sanctioned ALLOWLIST
    /// lever; a bare <c>auto</c> with no block still degrades to <c>prompt</c>, byte-identical to today.
    /// It is stored in <see cref="_autonomyBlockPresent"/> and read by the <see cref="Decide"/> gate.
    /// </param>
    public Overwatch(
        IPromptRunner? diagnoseRunner,
        NeedsHumanTriage? terminalTriage,
        AutonomyPolicy policy,
        IOverwatchInteraction? interaction = null,
        bool autonomyBlockPresent = false)
    {
        _diagnoseRunner = diagnoseRunner;
        _terminalTriage = terminalTriage;
        _policy = policy;
        _interaction = interaction ?? IOverwatchInteraction.NonInteractive;
        _autonomyBlockPresent = autonomyBlockPresent;
    }

    /// <summary>
    /// Evaluate a NON-terminal struggle boundary (eager <c>attempt ≥ 2</c>, a no-op/deterministic-script
    /// short-circuit about to fire, or a permission wall). Returns the control-flow decision the loop
    /// consults. ADVISORY: any absence/error (no runner, cost cap hit, malformed/errored diagnose) returns
    /// <see cref="OverwatchDecision.NoAction"/> and the deterministic policy stands. Never throws — the loop
    /// need not guard it.
    /// </summary>
    internal async Task<OverwatchDecision> EvaluateAsync(
        OverwatchTrigger trigger,
        TaskNode task,
        PlanDefinition plan,
        int attempt,
        string taskLogDir,
        RunJournal journal,
        IRunObserver observer,
        CancellationToken ct)
    {
        // Cost bound (Decision C: the cost mitigation for eager). Once the task's cumulative journaled cost
        // has reached maxCostUsd, do not spend more on a diagnose — stay out (deterministic policy stands).
        if (plan.Config.MaxCostUsd is { } cap && journal.CurrentCostUsd() >= cap)
        {
            return OverwatchDecision.NoAction;
        }

        // No diagnose runner ⇒ advisory no-action (a plan with no prompt runner at all).
        if (_diagnoseRunner is null)
        {
            return OverwatchDecision.NoAction;
        }

        DiagnoseOutcome diagnose = await RunDiagnoseAsync(trigger, task, plan, attempt, taskLogDir, journal, ct)
            .ConfigureAwait(false);

        // Advisory-never-gates: a malformed/absent/errored proposal = no action; verdict from files.
        if (diagnose.Proposal is not { } proposal)
        {
            // #452: BUT it is no longer SILENT. A diagnose that ran and came back with nothing spent real
            // money and left the operator unsupervised, and a supervisor that reports its own failure by
            // saying nothing is indistinguishable from one with nothing to report. Record it — a visible
            // line plus a durable decisions[] entry — then stand down. Still advisory: no verdict changes,
            // no exit code changes, the deterministic policy stands exactly as before.
            RecordNoVerdict(trigger, task, attempt, diagnose.NoVerdictReason, taskLogDir, journal, observer);
            return OverwatchDecision.NoAction;
        }

        // Classify every proposed fix op via the mechanical asymmetry (harness decides, not the judge).
        var classified = proposal.Fixes
            .Select(f => (Fix: f, Class: OverwatchFixClassifier.Classify(f, task, plan)))
            .ToList();

        // The allowlist action-layer levers the overwatcher MAY sanction in v1.
        OverwatchFixOp? guidance = classified
            .FirstOrDefault(c => c.Class == OverwatchAuthorityClass.Allowlist && c.Fix.Kind == OverwatchFixKind.GuidanceInjection).Fix;
        OverwatchFixOp? budget = classified
            .FirstOrDefault(c => c.Class == OverwatchAuthorityClass.Allowlist && c.Fix.Kind == OverwatchFixKind.BudgetOverride).Fix;

        (OverwatchDecision decision, string decisionToken, string headline) =
            Decide(trigger, task, attempt, proposal, guidance, budget);

        Record(trigger, task, attempt, proposal, classified, decision, decisionToken, headline, taskLogDir, journal, observer);
        return decision;
    }

    /// <summary>True for a DETERMINISTIC HALT boundary (a short-circuit / permission wall / exhaustion) where a
    /// non-grant decision HALTS the task; false for the eager <c>attempt ≥ 2</c> trigger, a NON-floor boundary
    /// where a non-grant decision is purely advisory (the loop keeps retrying per the deterministic policy — the
    /// eager diagnose never GATES a task the floor would let continue).</summary>
    private static bool IsFloorBoundary(OverwatchTrigger trigger) => trigger != OverwatchTrigger.EagerAttempt;

    /// <summary>
    /// The terminal-exhaustion case (§9.2.1): the task exhausted its retry budget and is settling
    /// <c>needs-human</c>. Delegates to the composed <see cref="NeedsHumanTriage"/> (unchanged
    /// <c>feedback.md</c>/<c>triage.json</c>), records a <c>task</c>-boundary <c>decisions[]</c> entry + an
    /// <c>overwatch.jsonl</c> record for the halt, and returns the triage <c>feedback.md</c> path (or null).
    /// Advisory: a thrown/errored triage yields a null feedback path and a bare halt record — never a partial
    /// artifact, never a changed verdict.
    /// </summary>
    internal async Task<string?> EvaluateTerminalAsync(
        TaskNode task,
        PlanDefinition plan,
        string taskLogDir,
        string planDirectory,
        string workspace,
        RunJournal journal,
        IRunObserver observer,
        bool autoFile,
        CancellationToken ct)
    {
        string? feedbackPath = null;
        if (_terminalTriage is not null)
        {
            try
            {
                // The triage's own prompt spend is charged to the run's cumulative cost via the shared
                // overhead sink INSIDE RunAsync (SSOT §9.2/§7, #314) — the journal is threaded in so that
                // charge happens BEFORE any parse of the triage result, exactly as the diagnose charge does.
                feedbackPath = await _terminalTriage
                    .RunAsync(task, taskLogDir, planDirectory, workspace, journal, ct, autoFile)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Triage is advisory — a thrown runner never changes the verdict or aborts the run.
                feedbackPath = null;
            }
        }

        TriageSummary? summary = TriageSummaryReader.TryRead(taskLogDir);
        string? diagnosis = summary?.OneLine ?? summary?.Diagnosis;
        string headline = diagnosis is { Length: > 0 }
            ? $"Overwatch halted '{task.Id}' needs-human (terminal exhaustion): {diagnosis}"
            : $"Overwatch halted '{task.Id}' needs-human (terminal exhaustion)";

        var detail = new OverwatchDetailRecord
        {
            At = DateTimeOffset.UtcNow.ToString("O"),
            Trigger = OverwatchTriggers.Token(OverwatchTrigger.TerminalExhaustion),
            Attempt = journal.NextAttemptNumber(task.Id) - 1,
            Policy = AutonomyPolicies.Token(_policy),
            Decision = "halted",
            Classification = summary is null ? null : "doomed",
            Diagnosis = diagnosis,
            Headline = headline
        };
        OverwatchDetailWriter.Append(taskLogDir, detail);

        var entry = new DecisionEntry
        {
            Boundary = "task",
            Policy = AutonomyPolicies.Token(_policy),
            Decision = "halted",
            Subject = task.Id,
            Headline = headline,
            Detail = diagnosis ?? ""
        };
        journal.RecordDecision(entry);
        observer.DecisionRecorded(entry);

        return feedbackPath;
    }

    // --- decision logic --------------------------------------------------------------------

    /// <summary>
    /// Map the parsed proposal onto the shared <see cref="AutonomyPolicy"/> (doc 11 §6) to produce the
    /// control-flow decision + the reporting tokens. The heart of "no sanctioned change ⇒ no grant ⇒ honest
    /// halt": a grant is returned ONLY when the tier + interaction sanction it AND a sanctioned allowlist
    /// change (guidance / budget) exists.
    /// </summary>
    private (OverwatchDecision Decision, string DecisionToken, string Headline) Decide(
        OverwatchTrigger trigger,
        TaskNode task,
        int attempt,
        OverwatchProposal proposal,
        OverwatchFixOp? guidance,
        OverwatchFixOp? budget)
    {
        string triggerToken = OverwatchTriggers.Token(trigger);
        bool floor = IsFloorBoundary(trigger);

        // halt tier: always halt; propose nothing, apply nothing. Most conservative.
        if (_policy == AutonomyPolicy.Halt)
        {
            return NonGrant(task, attempt, triggerToken, floor, proposal.Diagnosis, "policy=halt");
        }

        // A permission wall is structurally unfixable by an ephemeral guidance/budget lever (it needs a
        // config/permission change — a human action), so it is diagnose-only: never grant, always the floor.
        if (trigger == OverwatchTrigger.PermissionWall)
        {
            return NonGrant(task, attempt, triggerToken, floor, proposal.Diagnosis, "permission-wall");
        }

        // A doomed diagnosis halts regardless of tier — never grant more attempts to a structurally doomed task.
        if (proposal.Classification == OverwatchClassification.Doomed)
        {
            return NonGrant(task, attempt, triggerToken, floor, proposal.Diagnosis, "doomed");
        }

        // No sanctioned allowlist change available (only denylist/default ops, or none) ⇒ no grant.
        // At a FLOOR boundary this is the exact reconciliation with #174/#264: the overwatcher cannot grant
        // "keep trying, unchanged" — that is the deterministic short-circuit's domain, and it always halts.
        bool hasSanctionedChange = guidance is not null || budget is not null;
        if (!hasSanctionedChange)
        {
            return NonGrant(task, attempt, triggerToken, floor, proposal.Diagnosis, "no-sanctioned-change");
        }

        // A sanctioned allowlist change exists. Describe it once — both the auto-tier gate and the prompt
        // confirmation reference the same summary.
        string sanctionedSummary = DescribeSanctionedChange(guidance, budget);

        // auto-tier gate (issue #361 Phase 4, doc 12 §9 Phase 4, doc 11 §6/§9.6): a BLOCK-PRESENT `auto` tier
        // SILENTLY auto-applies the sanctioned allowlist lever — it grants the retry WITHOUT consulting
        // `_interaction.ConfirmApply` (no prompt), realizing the action/budget half of overwatcher v2 bet #6.
        // The gate keys on the PRESENCE of the `autonomy` block (`_autonomyBlockPresent`), NOT on
        // `autonomyPolicy: auto` alone — the anti-Option-(c) guard. It sits BELOW every floor (halt /
        // permission-wall / doomed / no-sanctioned-change), so a DENYLIST verdict-surface op — which the
        // classifier never routes onto the guidance/budget levers — can never reach it (a denylist-only
        // proposal already exited above at no-sanctioned-change). Recorded with the shipped `auto-applied`
        // decision token.
        if (_policy == AutonomyPolicy.Auto && _autonomyBlockPresent)
        {
            var autoGrant = new OverwatchDecision
            {
                Kind = OverwatchDecisionKind.Grant,
                GuidanceInjection = guidance?.Guidance,
                ExtraRetries = ExtraRetriesFor(budget)
            };
            string autoHeadline =
                $"Overwatch auto-applied a sanctioned change for '{task.Id}' (attempt {attempt}, {triggerToken}): " +
                sanctionedSummary;
            return (autoGrant, DecisionTokens.AutoApplied, autoHeadline);
        }

        // prompt (and a bare `auto` with no block — which degrades to prompt, byte-identical to today): propose
        // the allowlist change; apply on an interactive approve, else honest halt. Non-interactive ⇒ halt
        // (never blocks, never spends unbidden). This is the load-bearing anti-Option-(c) back-compat path.
        OverwatchInteractionResult response = _interaction.ConfirmApply(proposal, task, trigger, sanctionedSummary);

        switch (response)
        {
            case OverwatchInteractionResult.Apply:
                int extraRetries = ExtraRetriesFor(budget);
                var grant = new OverwatchDecision
                {
                    Kind = OverwatchDecisionKind.Grant,
                    GuidanceInjection = guidance?.Guidance,
                    ExtraRetries = extraRetries
                };
                string grantHeadline =
                    $"Overwatch granted '{task.Id}' one more attempt (attempt {attempt}, {triggerToken}) " +
                    $"with a sanctioned change: {sanctionedSummary}";
                return (grant, "prompted-approved", grantHeadline);

            case OverwatchInteractionResult.Declined:
                return NonGrant(task, attempt, triggerToken, floor, proposal.Diagnosis, "prompted-declined", "prompted-declined");

            default: // NonInteractive
                return NonGrant(task, attempt, triggerToken, floor, proposal.Diagnosis, "non-interactive");
        }
    }

    /// <summary>
    /// Build the non-grant decision. At a FLOOR boundary it is a Halt with the rich diagnosis (the floor
    /// stands, made earlier + richer); at the NON-floor eager boundary it is ADVISORY — the loop keeps
    /// retrying per the deterministic policy, so the decision must never carry <see cref="OverwatchDecisionKind.Halt"/>
    /// (that would gate a task the floor would let continue) and its reporting token is <c>advisory</c>.
    /// </summary>
    private static (OverwatchDecision, string, string) NonGrant(
        TaskNode task, int attempt, string triggerToken, bool floor, string diagnosis, string why,
        string floorToken = "halted")
    {
        if (!floor)
        {
            var advisory = new OverwatchDecision { Kind = OverwatchDecisionKind.NoAction };
            string advisoryHeadline =
                $"Overwatch advisory on '{task.Id}' (attempt {attempt}, {triggerToken}; {why}): {diagnosis}";
            return (advisory, "advisory", advisoryHeadline);
        }

        var decision = new OverwatchDecision
        {
            Kind = OverwatchDecisionKind.Halt,
            RichHaltSummary = $"overwatch: {diagnosis}"
        };
        string headline =
            $"Overwatch halted '{task.Id}' (attempt {attempt}, {triggerToken}; {why}): {diagnosis}";
        return (decision, floorToken, headline);
    }

    /// <summary>Clamp a granted budget lever to the hard cap; a non-retries budget field still grants ≥1 extra attempt (the loop's #94/#119 auto-escalation raises turns/timeout on it).</summary>
    private static int ExtraRetriesFor(OverwatchFixOp? budget)
    {
        if (budget is null)
        {
            return 0;
        }

        if (string.Equals(budget.BudgetField, "retries", StringComparison.OrdinalIgnoreCase) && budget.BudgetValue is { } v && v > 0)
        {
            return Math.Min(v, MaxExtraRetriesPerGrant);
        }

        // maxTurns / timeoutSeconds: grant one more attempt (the loop auto-raises the turn/timeout budget on it).
        return 1;
    }

    private static string DescribeSanctionedChange(OverwatchFixOp? guidance, OverwatchFixOp? budget)
    {
        var parts = new List<string>();
        if (guidance is not null)
        {
            parts.Add("inject failure-specific guidance");
        }

        if (budget is not null)
        {
            parts.Add(budget.BudgetValue is { } v
                ? $"raise {budget.BudgetField} to {v}"
                : $"raise {budget.BudgetField}");
        }

        return string.Join("; ", parts);
    }

    // --- diagnose prompt -------------------------------------------------------------------

    /// <summary>
    /// The result of one diagnose: a parsed <see cref="OverwatchProposal"/>, or a NO-VERDICT with the
    /// one-line reason (issue #452). The reason is what makes the failure reportable — before it, every
    /// non-proposal collapsed to a bare null and the caller had nothing to say.
    /// </summary>
    private readonly record struct DiagnoseOutcome(OverwatchProposal? Proposal, string NoVerdictReason)
    {
        internal static DiagnoseOutcome Verdict(OverwatchProposal proposal) => new(proposal, "");

        internal static DiagnoseOutcome NoVerdict(string reason) => new(null, reason);
    }

    /// <summary>
    /// Run the diagnose prompt and parse it. Best-effort: a thrown runner, an error/incomplete result, or an
    /// unparseable body all yield a NO-VERDICT outcome (advisory no-action — but a REPORTED one, #452). The
    /// stream is teed per attempt so a re-fire does not clobber a prior one.
    /// </summary>
    private async Task<DiagnoseOutcome> RunDiagnoseAsync(
        OverwatchTrigger trigger,
        TaskNode task,
        PlanDefinition plan,
        int attempt,
        string taskLogDir,
        RunJournal journal,
        CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(taskLogDir);
            string prompt = BuildDiagnosePrompt(trigger, task, attempt, taskLogDir, journal);
            string streamLogPath = Path.Combine(taskLogDir, $"overwatch-stream-attempt-{attempt}.jsonl");

            var invocation = new PromptInvocation
            {
                ComposedPrompt = prompt,
                WorkingDirectory = plan.Workspace,
                PlanDirectory = plan.PlanDirectory,
                Environment = new Dictionary<string, string>(StringComparer.Ordinal),

                // #452: the diagnose profile is set EXPLICITLY. Every field left to its record default is
                // a field nobody chose — and AllowedTools defaulting to empty is the whole defect.
                Settings = new PromptRunnerSettings
                {
                    AllowedTools = DiagnoseTools,
                    MaxTurns = DiagnoseMaxTurns
                },
                Timeout = TimeSpan.FromMinutes(5),
                StreamLogPath = streamLogPath,
                AbortAfterConsecutiveToolDenials = DenialAbortThreshold
            };

            PromptResult result = await _diagnoseRunner!.RunAsync(invocation, ct).ConfigureAwait(false);

            // Charge the diagnose spend to the run's cumulative cost via the shared overhead sink (SSOT
            // §9.2, WEAK-1): the spend is REAL regardless of whether the body parses, so it is charged here —
            // BEFORE the parse — so it BOTH counts toward the maxCostUsd gate (bounding subsequent eager
            // fires) AND appears in the reported total. A null CostUsd is a no-op.
            journal.AddOverheadCost(result.CostUsd);

            if (!result.Completed || result.IsError || result.ResultText is null)
            {
                // The runner's own summary IS the reason (the runner owns the vendor wording — a
                // max-turns exhaustion, a denial abort, a timeout — and the harness never re-derives it).
                return DiagnoseOutcome.NoVerdict(
                    result.Summary is { Length: > 0 } s ? s : "the diagnose runner produced no result");
            }

            OverwatchProposal? parsed = OverwatchProposal.TryParse(result.ResultText);
            if (parsed is not null)
            {
                return DiagnoseOutcome.Verdict(parsed);
            }

            // The body was PAID FOR (charged above, before the parse) and is the only evidence of why the
            // judge produced nothing usable — so persist it instead of discarding it. Plan 28 hit this twice
            // and the run recorded the same eleven-word reason both times; the actual bodies were two
            // complete, correct verdicts wrapped in a ```json fence, and nothing on disk said so. Diagnosing
            // it needed the raw stream JSONL and a hand-written parser.
            //
            // The FILE carries the body; the REASON carries only the path and a length. That keeps the
            // existing rule at the catch below — this string is rendered to the console and journaled, so it
            // must not carry model-authored content — while making the next occurrence a one-command answer
            // rather than an investigation.
            //
            // DISPOSITION — PERMANENT, deliberately. This is NOT scaffolding for #551 and must not be
            // removed when #551 closes. #551's fence-stripping fixes ONE cause of an unparseable body; it
            // cannot fix truncation, a judge answering in prose, a future schema change, or — most
            // pointedly — a NON-CLAUDE runner with different formatting habits, which is exactly what #223
            // is about to introduce. The forensics get MORE valuable after #551, not less. The cost is one
            // small file, written only on a path that has already failed and already spent money.
            // If you disagree, argue it on #551 rather than deleting it in passing.
            string bodyPath = Path.Combine(taskLogDir, $"overwatch-noverdict-attempt-{attempt}.txt");
            string reason = "the diagnose returned a body that is not a parseable verdict";
            try
            {
                File.WriteAllText(bodyPath, result.ResultText);
                reason += $" ({result.ResultText.Length} chars, saved to {Path.GetFileName(bodyPath)})";
            }
            catch (IOException)
            {
                // Best-effort forensics must never be the thing that breaks an advisory path.
            }
            catch (UnauthorizedAccessException)
            {
            }

            return DiagnoseOutcome.NoVerdict(reason);
        }
        catch (Exception ex)
        {
            // Advisory: a diagnose failure never aborts the run or changes the verdict — but it is
            // REPORTED now rather than swallowed. The type, not the message: a message can carry a path
            // or a prompt fragment, and this string is rendered to the console and journaled.
            return DiagnoseOutcome.NoVerdict($"the diagnose threw {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Compose the diagnose brief. Two things it deliberately does NOT do: send the judge hunting for its
    /// own evidence, and ask it for work its tools cannot do (issue #452).
    ///
    /// <para>The old brief named the log directory as the literal template <c>logs/&lt;runId&gt;/&lt;taskId&gt;/</c>
    /// — the run id was never substituted — relative to a workspace that is not even where the plan folder
    /// lives. So the judge's first job was to GUESS where its input was, which is what the refused shell
    /// calls were for. It is handed the resolved absolute path instead.</para>
    ///
    /// <para>And the per-attempt outcomes are DETERMINISTIC FACTS the harness already holds in the journal,
    /// so they are stated rather than left to be reconstructed by reading logs. This is what makes the #94
    /// "a bigger budget would finish" shape visible: the discriminator between "the work was wrong" and
    /// "the work was cut off" is <c>failedGuardrails</c> being empty on a <c>max-turns</c>/<c>timeout</c>
    /// attempt, and that is a fact, not a judgement.</para>
    /// </summary>
    private static string BuildDiagnosePrompt(
        OverwatchTrigger trigger, TaskNode task, int attempt, string taskLogDir, RunJournal journal) =>
        $"# Overwatch diagnose: task '{task.Id}' (attempt {attempt}, trigger: {OverwatchTriggers.Token(trigger)})\n\n" +
        $"Task: {task.Description}\n\n" +
        "You are a read-only supervisor. Your ONLY tools are Read, Glob and Grep — you have no Bash and no " +
        "write tools, so do not attempt shell commands, and do not try to fix anything yourself.\n\n" +
        "## Attempt history (recorded by the harness — authoritative)\n\n" +
        RenderAttemptHistory(task, journal) + "\n" +
        "Discriminator: an attempt with NO failed guardrails did not fail a check — it was CUT OFF " +
        "(turns, clock, or an error). An attempt with named failed guardrails failed a check. Attempts " +
        "that are repeatedly cut off mid-progress point at a BUDGET lever; failed checks point at " +
        "guidance, or at a structurally doomed task.\n\n" +
        "## Evidence\n\n" +
        $"The attempt logs, feedback and transcripts for this task are under:\n\n    {taskLogDir}\n\n" +
        "Each `attempt-N/` subfolder holds that attempt's action output, guardrail output, `feedback.md` " +
        "and `transcript.md`. Read the most recent attempts first; Grep is cheaper than Read on a large " +
        "transcript. Do not read more than you need — a verdict grounded in two attempts beats no verdict.\n\n" +
        "## Your verdict\n\n" +
        "Decide whether more attempts can plausibly converge (retryable) or the task is structurally " +
        "doomed, and propose ONLY action-layer fixes: ephemeral guidance for the next attempt, or a runtime " +
        "budget bump (maxTurns / retries / timeoutSeconds). Do NOT propose editing any guardrail/preflight " +
        "body or a task.json verdict field (writeScope / scope / dependsOn / integrationGate) — those are " +
        "the verdict surface and require human review; proposing one is fine but it will be routed to a human.\n\n" +
        "Return ONLY this JSON object:\n" +
        """{"classification":"retryable|doomed","diagnosis":"<precise one-paragraph diagnosis>","fixes":[{"kind":"guidance","guidance":"<failure-specific guidance>"}]}""" + "\n\n" +
        "Fix op shapes: " +
        """{"kind":"guidance","guidance":"..."} | {"kind":"budget","field":"maxTurns|retries|timeoutSeconds","value":<int>} | {"kind":"file-edit","path":"..."} | {"kind":"task-field","field":"..."}""";

    /// <summary>
    /// The journal's per-attempt outcome table for the brief — outcome token plus the failed-guardrail
    /// names (or an explicit "none", which is the load-bearing half of the #94 discriminator). Renders a
    /// plain sentence when the journal has no attempts yet (a permission wall may fire on attempt 1).
    /// </summary>
    private static string RenderAttemptHistory(TaskNode task, RunJournal journal)
    {
        IReadOnlyList<AttemptRecord> attempts = journal.AttemptsFor(task.Id);
        if (attempts.Count == 0)
        {
            return "No attempts recorded yet for this task.\n";
        }

        var sb = new StringBuilder();
        sb.Append("| attempt | outcome | failed guardrails |\n|---|---|---|\n");
        foreach (AttemptRecord record in attempts)
        {
            string failed = record.FailedGuardrails.Count == 0
                ? "(none)"
                : string.Join(", ", record.FailedGuardrails.Select(g => g.Name));
            sb.Append($"| {record.Attempt} | {JournalJson.OutcomeToken(record.Outcome)} | {failed} |\n");
        }

        return sb.ToString();
    }

    // --- reporting -------------------------------------------------------------------------

    private void Record(
        OverwatchTrigger trigger,
        TaskNode task,
        int attempt,
        OverwatchProposal proposal,
        IReadOnlyList<(OverwatchFixOp Fix, OverwatchAuthorityClass Class)> classified,
        OverwatchDecision decision,
        string decisionToken,
        string headline,
        string taskLogDir,
        RunJournal journal,
        IRunObserver observer)
    {
        var detailFixes = classified
            .Select(c => new OverwatchDetailFix
            {
                Kind = FixKindToken(c.Fix.Kind),
                Authority = AuthorityToken(c.Class),
                Target = c.Fix.TargetPath ?? c.Fix.TaskField ?? c.Fix.BudgetField
            })
            .ToList();

        var record = new OverwatchDetailRecord
        {
            At = DateTimeOffset.UtcNow.ToString("O"),
            Trigger = OverwatchTriggers.Token(trigger),
            Attempt = attempt,
            Policy = AutonomyPolicies.Token(_policy),
            Decision = decisionToken,
            Classification = proposal.Classification == OverwatchClassification.Doomed ? "doomed" : "retryable",
            Diagnosis = proposal.Diagnosis,
            Fixes = detailFixes,
            Applied = decision.Kind == OverwatchDecisionKind.Grant
                ? new OverwatchDetailApplied
                {
                    Guidance = !string.IsNullOrEmpty(decision.GuidanceInjection),
                    ExtraRetries = decision.ExtraRetries
                }
                : null,
            Headline = headline
        };
        OverwatchDetailWriter.Append(taskLogDir, record);

        var entry = new DecisionEntry
        {
            Boundary = "task",
            Policy = AutonomyPolicies.Token(_policy),
            Decision = decisionToken,
            Subject = task.Id,
            Headline = headline,
            Detail = proposal.Diagnosis
        };
        journal.RecordDecision(entry);
        observer.DecisionRecorded(entry);
    }

    /// <summary>
    /// Record a consulted-but-no-verdict fire (issue #452) across all three surfaces the overwatcher
    /// already owns — the <c>overwatch.jsonl</c> detail stream, the durable <c>decisions[]</c> audit, and
    /// the live operator surface — so the one outcome that used to be invisible now reports like every
    /// other. The visible line is raised through <see cref="IRunObserver.OverwatchNoVerdict"/> rather than
    /// <see cref="IRunObserver.DecisionRecorded"/>: the decision channel renders as a settled green
    /// decision, and "your supervisor failed" is an advisory warning, not a decision that went well. One
    /// event, one line.
    /// </summary>
    private void RecordNoVerdict(
        OverwatchTrigger trigger,
        TaskNode task,
        int attempt,
        string reason,
        string taskLogDir,
        RunJournal journal,
        IRunObserver observer)
    {
        string why = string.IsNullOrWhiteSpace(reason) ? "the diagnose produced no verdict" : reason.Trim();
        string triggerToken = OverwatchTriggers.Token(trigger);
        string headline = $"overwatch: no verdict — {why} (task '{task.Id}', attempt {attempt}, {triggerToken})";

        OverwatchDetailWriter.Append(taskLogDir, new OverwatchDetailRecord
        {
            At = DateTimeOffset.UtcNow.ToString("O"),
            Trigger = triggerToken,
            Attempt = attempt,
            Policy = AutonomyPolicies.Token(_policy),
            Decision = DecisionTokens.NoVerdict,
            Diagnosis = why,
            Headline = headline
        });

        journal.RecordDecision(new DecisionEntry
        {
            Boundary = "task",
            Policy = AutonomyPolicies.Token(_policy),
            Decision = DecisionTokens.NoVerdict,
            Subject = task.Id,
            Headline = headline,
            Detail = why
        });

        observer.OverwatchNoVerdict(task.Id, why);
    }

    private static string FixKindToken(OverwatchFixKind kind) => kind switch
    {
        OverwatchFixKind.GuidanceInjection => "guidance",
        OverwatchFixKind.BudgetOverride => "budget",
        OverwatchFixKind.FileEdit => "file-edit",
        OverwatchFixKind.TaskFieldEdit => "task-field",
        _ => "unknown"
    };

    private static string AuthorityToken(OverwatchAuthorityClass authority) => authority switch
    {
        OverwatchAuthorityClass.Allowlist => "allowlist",
        OverwatchAuthorityClass.Denylist => "denylist",
        _ => "default"
    };
}
