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

    private readonly IPromptRunner? _diagnoseRunner;
    private readonly NeedsHumanTriage? _terminalTriage;
    private readonly AutonomyPolicy _policy;
    private readonly IOverwatchInteraction _interaction;

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
    public Overwatch(
        IPromptRunner? diagnoseRunner,
        NeedsHumanTriage? terminalTriage,
        AutonomyPolicy policy,
        IOverwatchInteraction? interaction = null)
    {
        _diagnoseRunner = diagnoseRunner;
        _terminalTriage = terminalTriage;
        _policy = policy;
        _interaction = interaction ?? IOverwatchInteraction.NonInteractive;
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

        OverwatchProposal? proposal = await RunDiagnoseAsync(trigger, task, plan, attempt, taskLogDir, ct)
            .ConfigureAwait(false);

        // Advisory-never-gates: a malformed/absent/errored proposal = no action; verdict from files.
        if (proposal is null)
        {
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
                feedbackPath = await _terminalTriage
                    .RunAsync(task, taskLogDir, planDirectory, workspace, ct, autoFile)
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

        // prompt (and, in v1, auto — which degrades to prompt): propose the allowlist change; apply on an
        // interactive approve, else honest halt. Non-interactive ⇒ halt (never blocks, never spends unbidden).
        string sanctionedSummary = DescribeSanctionedChange(guidance, budget);
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
    /// Run the diagnose prompt and parse it. Best-effort: a thrown runner, an error/incomplete result, or an
    /// unparseable body all return null (advisory no-action). The stream is teed per attempt so a re-fire
    /// does not clobber a prior one.
    /// </summary>
    private async Task<OverwatchProposal?> RunDiagnoseAsync(
        OverwatchTrigger trigger,
        TaskNode task,
        PlanDefinition plan,
        int attempt,
        string taskLogDir,
        CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(taskLogDir);
            string prompt = BuildDiagnosePrompt(trigger, task, attempt);
            string streamLogPath = Path.Combine(taskLogDir, $"overwatch-stream-attempt-{attempt}.jsonl");

            var invocation = new PromptInvocation
            {
                ComposedPrompt = prompt,
                WorkingDirectory = plan.Workspace,
                PlanDirectory = plan.PlanDirectory,
                Environment = new Dictionary<string, string>(StringComparer.Ordinal),
                Settings = new PromptRunnerSettings { MaxTurns = 10 },
                Timeout = TimeSpan.FromMinutes(5),
                StreamLogPath = streamLogPath
            };

            PromptResult result = await _diagnoseRunner!.RunAsync(invocation, ct).ConfigureAwait(false);
            if (!result.Completed || result.IsError || result.ResultText is null)
            {
                return null;
            }

            return OverwatchProposal.TryParse(result.ResultText);
        }
        catch
        {
            // Advisory: a diagnose failure never aborts the run or changes the verdict.
            return null;
        }
    }

    private static string BuildDiagnosePrompt(OverwatchTrigger trigger, TaskNode task, int attempt) =>
        $"# Overwatch diagnose: task '{task.Id}' (attempt {attempt}, trigger: {OverwatchTriggers.Token(trigger)})\n\n" +
        $"Task: {task.Description}\n\n" +
        "This task is struggling. Read its attempt logs and feedback under the plan's " +
        "`logs/<runId>/" + task.Id + "/` directory to see what was tried and why it failed.\n\n" +
        "Decide whether more attempts can plausibly converge (retryable) or the task is structurally " +
        "doomed, and propose ONLY action-layer fixes: ephemeral guidance for the next attempt, or a runtime " +
        "budget bump (maxTurns / retries / timeoutSeconds). Do NOT propose editing any guardrail/preflight " +
        "body or a task.json verdict field (writeScope / scope / dependsOn / integrationGate) — those are " +
        "the verdict surface and require human review; proposing one is fine but it will be routed to a human.\n\n" +
        "Return ONLY this JSON object:\n" +
        """{"classification":"retryable|doomed","diagnosis":"<precise one-paragraph diagnosis>","fixes":[{"kind":"guidance","guidance":"<failure-specific guidance>"}]}""" + "\n\n" +
        "Fix op shapes: " +
        """{"kind":"guidance","guidance":"..."} | {"kind":"budget","field":"maxTurns|retries|timeoutSeconds","value":<int>} | {"kind":"file-edit","path":"..."} | {"kind":"task-field","field":"..."}""";

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
