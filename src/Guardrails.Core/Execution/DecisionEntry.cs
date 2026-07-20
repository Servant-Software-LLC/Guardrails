using Guardrails.Core.Model;

namespace Guardrails.Core.Execution;

/// <summary>
/// One entry in the unified, append-only <c>decisions[]</c> reporting surface (SSOT §2.1/§7): the durable,
/// <see cref="Boundary"/>-discriminated record of every autonomy-policy decision point. It is surfaced live
/// under the task table (via <see cref="IRunObserver.DecisionRecorded"/>), threaded onto
/// <see cref="RunReport.Decision"/> for the end-of-run summary, and appended to the top-level
/// <c>decisions[]</c> array in <c>run.json</c> — the canonical durable store. In M1 only the <c>drift</c>
/// boundary is emitted (the resume definition-drift gate, §7.2); the <c>wave</c> (#254) and <c>task</c>
/// (#269) boundaries land in M2/M3 and simply append to the SAME array. It is NOT a terminal bucket: an
/// auto-resolved decision flows into the normal outcome and returns the NORMAL exit code (0 green /
/// 2 needs-human).
/// </summary>
public sealed record DecisionEntry
{
    /// <summary>The decision-class discriminator (extensible): <c>drift</c> (M1) · <c>wave</c> (M2) · <c>task</c> (M3).</summary>
    public required string Boundary { get; init; }

    /// <summary>The <see cref="AutonomyPolicy"/> token (<c>prompt</c>/<c>halt</c>/<c>auto</c>) in force at this boundary.</summary>
    public required string Policy { get; init; }

    /// <summary>How the boundary resolved: <c>halted</c> · <c>prompted-approved</c> · <c>prompted-declined</c> · <c>auto-applied</c>.</summary>
    public required string Decision { get; init; }

    /// <summary>UTC time the decision was made (ISO-8601).</summary>
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The unit the decision concerned — a task id / wave dir / the drifted unit(s).</summary>
    public required string Subject { get; init; }

    /// <summary>A one-line human summary (what an operator sees at the decision point / in the live UI).</summary>
    public required string Headline { get; init; }

    /// <summary>Fuller free-text detail (e.g. the per-task old→new hash breakdown for a drift resolution); may be empty.</summary>
    public string Detail { get; init; } = "";
}

/// <summary>One task rebuilt by a Part C drift resolution: its id and its old→new definition hash. A helper
/// carried into <see cref="DriftDecisions"/> to render a <c>drift</c>-boundary <see cref="DecisionEntry"/>;
/// not itself serialized into <c>run.json</c> (the flat <see cref="DecisionEntry"/> is).</summary>
public sealed record DriftResolvedTask
{
    /// <summary>The rebuilt task's id.</summary>
    public required string TaskId { get; init; }

    /// <summary>The <c>definitionHash</c> recorded at the task's last successful settle (or a sentinel when a descendant had none recorded).</summary>
    public required string OldHash { get; init; }

    /// <summary>The current on-disk <c>definitionHash</c> the task will be rebuilt against.</summary>
    public required string NewHash { get; init; }
}

/// <summary>
/// Builds the <c>drift</c>-boundary <see cref="DecisionEntry"/> for a Part C safe-drift resolution (SSOT
/// §2.1/§7.2): the plan branch was rewound past a provably-safe drifted suffix (or a journal-only reset was
/// applied) and its tasks re-run. The single place the drift specifics (rewind target + per-task old→new
/// hashes) are rendered into the unified log's flat shape, so the run-time auto-resolve gate
/// (<see cref="Scheduler"/>) and the manual scoped reset (<c>RunReset.ScopedReset</c>) produce identical
/// entries.
/// </summary>
public static class DriftDecisions
{
    /// <summary>
    /// The run-time pre-DAG auto-resolve (<see cref="Scheduler"/>). <paramref name="policy"/> is the policy in
    /// force: <see cref="AutonomyPolicy.Auto"/> ⇒ <c>auto-applied</c>; <see cref="AutonomyPolicy.Prompt"/> ⇒
    /// <c>prompted-approved</c> (the CLI captured an operator <c>y</c>). <see cref="AutonomyPolicy.Halt"/>
    /// never reaches here.
    /// </summary>
    public static DecisionEntry AutoResolved(
        AutonomyPolicy policy, string? rewindTarget, IReadOnlyList<DriftResolvedTask> tasks)
    {
        string decision = policy == AutonomyPolicy.Prompt ? "prompted-approved" : "auto-applied";
        return Build(AutonomyPolicies.Token(policy), decision, rewindTarget, tasks, manualReset: false);
    }

    /// <summary>
    /// The manual scoped reset (<c>guardrails reset &lt;folder&gt; &lt;taskId&gt;...</c>) — an operator
    /// explicitly applied the rewind + reset. Recorded as <c>auto-applied</c> (applied with no prompt);
    /// <paramref name="configuredPolicy"/> is the plan's configured <c>autonomyPolicy</c>.
    /// </summary>
    public static DecisionEntry ManualReset(
        AutonomyPolicy configuredPolicy, string? rewindTarget, IReadOnlyList<DriftResolvedTask> tasks) =>
        Build(AutonomyPolicies.Token(configuredPolicy), "auto-applied", rewindTarget, tasks, manualReset: true);

    /// <summary>
    /// Wave-level drift auto-resolve (SSOT §14.6, #254 M2b): a COMPLETED wave's <c>WaveDefinitionHash</c>
    /// drifted on resume; the plan branch was rewound past this wave (+ its downstream waves — a wave-scoped
    /// rewind is ALWAYS a safe trailing suffix, §14.8) and they were reset to re-run. A <c>wave</c>-boundary
    /// entry. <paramref name="policy"/> maps <see cref="AutonomyPolicy.Auto"/> ⇒ <c>auto-applied</c>,
    /// <see cref="AutonomyPolicy.Prompt"/> ⇒ <c>prompted-approved</c>.
    /// </summary>
    public static DecisionEntry WaveDriftResolved(
        AutonomyPolicy policy, string waveDir, string? rewindTarget,
        string oldHash, string newHash, IReadOnlyList<string> affectedWaves)
    {
        string decision = policy == AutonomyPolicy.Prompt ? "prompted-approved" : "auto-applied";
        return BuildWave(AutonomyPolicies.Token(policy), decision, waveDir, rewindTarget,
            oldHash, newHash, affectedWaves, manualReset: false);
    }

    /// <summary>
    /// The manual wave-scoped reset (<c>guardrails reset &lt;folder&gt; &lt;wave&gt;</c>, SSOT §14.8): an
    /// operator explicitly rewound the plan branch past a wave + its downstream waves. Recorded as
    /// <c>auto-applied</c> (applied with no prompt) at the <c>wave</c> boundary.
    /// </summary>
    public static DecisionEntry WaveReset(
        AutonomyPolicy configuredPolicy, string waveDir, string? rewindTarget, IReadOnlyList<string> affectedWaves) =>
        BuildWave(AutonomyPolicies.Token(configuredPolicy), "auto-applied", waveDir, rewindTarget,
            oldHash: null, newHash: null, affectedWaves, manualReset: true);

    /// <summary>
    /// The between-wave JIT checkpoint HALT (SSOT §14.4/§14.10, #360): a waved run reached a wave whose
    /// <c>tasks/</c> is empty (unauthored) and honest-halted for human JIT breakdown. A <c>wave</c>-boundary
    /// entry recorded as <c>halted</c> (or <c>prompted-declined</c>) — the auto-breakdown INVOCATION did not
    /// fire (an absent <c>brief.md</c>, no breakdown runner / serial mode / hit cost cap, or the
    /// <c>autoBreakdown:false</c> <c>autonomyPolicy</c>-gated path declined it). <paramref name="briefPresent"/>
    /// notes whether an OPTIONAL <c>brief.md</c> is authored in the wave folder (the opt-in signal, which under
    /// the default <c>autoBreakdown</c> auto-fires the breakdown). This closes the gap where the JIT checkpoint
    /// was the one wave boundary that emitted no <c>decisions[]</c> entry (design-360, §14.4).
    /// </summary>
    public static DecisionEntry WaveCheckpointHalt(
        AutonomyPolicy configuredPolicy, string waveDir, bool briefPresent, string decision = "halted")
    {
        string brief = $"{waveDir}/{WaveNode.BriefFileName}";
        string detail = briefPresent
            ? $"'{brief}' is present — auto-breakdown against it is on by default (autoBreakdown, decoupled from "
              + "autonomyPolicy); this run honest-halts because it could not run (no breakdown runner, serial "
              + "mode, or cost cap) or autoBreakdown is false — do a manual JIT breakdown + review."
            : $"No '{brief}' — create one to enable auto-breakdown at this checkpoint, "
              + "or author the wave manually against the integration worktree.";
        return new DecisionEntry
        {
            Boundary = "wave",
            Policy = AutonomyPolicies.Token(configuredPolicy),
            Decision = decision,
            Subject = waveDir,
            Headline = decision == "prompted-declined"
                ? $"Wave '{waveDir}' unauthored — breakdown DECLINED, halted for manual JIT breakdown"
                : $"Wave '{waveDir}' unauthored — halted for JIT breakdown",
            Detail = detail
        };
    }

    /// <summary>
    /// The between-wave breakdown INVOCATION succeeded and its output passed <c>guardrails validate</c>
    /// (#360 Phase 1, SSOT §14.4/doc 11 §9): the JIT wave was authored and the run HALTS for the human review
    /// gate (<c>/guardrails-review</c>) — the review gate is NEVER auto-satisfied at any policy (doc 11 §9.6).
    /// A <c>wave</c>-boundary entry whose <paramref name="invocationDecision"/> records HOW the invocation was
    /// authorized: <c>auto-applied</c> (autonomyPolicy <c>auto</c>) or <c>prompted-approved</c> (an interactive
    /// <c>prompt</c> approval).
    /// </summary>
    public static DecisionEntry WaveBreakdownComplete(
        AutonomyPolicy policy, string waveDir, string invocationDecision, int taskCount) =>
        new()
        {
            Boundary = "wave",
            Policy = AutonomyPolicies.Token(policy),
            Decision = invocationDecision,
            Subject = waveDir,
            Headline = $"Wave '{waveDir}' broken down ({taskCount} task(s)) — halting for /guardrails-review",
            Detail = "The breakdown output is a DRAFT: inspect the wave, run /guardrails-review, then re-run "
                     + "'guardrails run'. The harness never marks a wave reviewed on a human's behalf."
        };

    /// <summary>
    /// The between-wave breakdown INVOCATION ran but its output FAILED <c>guardrails validate</c> (#360 Phase 1):
    /// the partial invalid <c>tasks/</c> is quarantined so the plan stays loadable and the JIT checkpoint
    /// cleanly re-fires on resume. A <c>wave</c>-boundary entry; <paramref name="invocationDecision"/> records
    /// how the invocation was authorized (<c>auto-applied</c> / <c>prompted-approved</c>).
    /// </summary>
    public static DecisionEntry WaveBreakdownFailed(
        AutonomyPolicy policy, string waveDir, string invocationDecision, string errorSummary) =>
        new()
        {
            Boundary = "wave",
            Policy = AutonomyPolicies.Token(policy),
            Decision = invocationDecision,
            Subject = waveDir,
            Headline = $"Wave '{waveDir}' breakdown FAILED validation — partial output quarantined",
            Detail = errorSummary
        };

    private static DecisionEntry BuildWave(
        string policyToken, string decision, string waveDir, string? rewindTarget,
        string? oldHash, string? newHash, IReadOnlyList<string> affectedWaves, bool manualReset)
    {
        string how = rewindTarget is { Length: > 0 } target
            ? $"rewound the plan branch to {ShortSha(target)}"
            : "reset the wave (no plan-branch rewind needed)";
        string lead = manualReset
            ? "Manual wave-scoped reset"
            : $"Wave drift auto-resolved ({policyToken})";
        string hashPart = oldHash is not null && newHash is not null
            ? $" ({ShortHash(oldHash)} -> {ShortHash(newHash)})"
            : "";
        string detail = $"waves reset (this + downstream): {string.Join(", ", affectedWaves)}";

        return new DecisionEntry
        {
            Boundary = "wave",
            Policy = policyToken,
            Decision = decision,
            Subject = waveDir,
            Headline = $"{lead}: {how} and re-running wave '{waveDir}'{hashPart} + {affectedWaves.Count - 1} downstream wave(s)",
            Detail = detail
        };
    }

    private static DecisionEntry Build(
        string policyToken, string decision, string? rewindTarget,
        IReadOnlyList<DriftResolvedTask> tasks, bool manualReset)
    {
        string how = rewindTarget is { Length: > 0 } target
            ? $"rewound the plan branch to {ShortSha(target)}"
            : "reset the drifted task(s) (no plan-branch rewind needed)";
        string lead = manualReset ? "Manual scoped reset" : $"Definition drift auto-resolved ({policyToken})";
        string subject = string.Join(", ", tasks.Select(t => t.TaskId));
        string detail = string.Join(
            "\n", tasks.Select(t => $"{t.TaskId}: {ShortHash(t.OldHash)} -> {ShortHash(t.NewHash)}"));

        return new DecisionEntry
        {
            Boundary = "drift",
            Policy = policyToken,
            Decision = decision,
            Subject = subject,
            Headline = $"{lead}: {how} and re-running {tasks.Count} task(s)",
            Detail = detail
        };
    }

    private static string ShortSha(string sha) => sha.Length <= 8 ? sha : sha[..8];

    private static string ShortHash(string hash)
    {
        const string prefix = "sha256:";
        string body = hash.StartsWith(prefix, StringComparison.Ordinal) ? hash[prefix.Length..] : hash;
        return body.Length <= 8 ? body : body[..8];
    }
}
