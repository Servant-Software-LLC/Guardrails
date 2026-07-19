using System.Text;
using Guardrails.Core.Model;
using Guardrails.Core.Prompts;

namespace Guardrails.Core.Execution;

/// <summary>
/// The between-wave breakdown actor (#360 Phase 1, design of record <c>docs/plans/11-overwatcher.md</c> §9).
/// At the JIT wave checkpoint (an unauthored/empty next wave carrying a human-authored <c>brief.md</c>) the
/// wave loop (<see cref="Scheduler.RunWavedAsync"/>) invokes this actor to AUTHOR the wave's <c>tasks/**</c>
/// by driving the <c>plan-breakdown</c> skill through the shipped <see cref="IPromptRunner"/> seam under the
/// reserved <c>breakdown</c> profile.
///
/// <para>It is a DISTINCT component from the per-task <see cref="Overwatch"/> (they are "one supervisor"
/// conceptually, two components mechanically — doc 11 §9.1): the overwatcher's <c>overwatch</c> profile is
/// READ-ONLY (it only reasons); the <c>breakdown</c> profile has the FULL authoring tool set
/// (Read/Write/Edit/Bash/Grep/Glob) because it writes task files into a <c>pending</c> wave folder
/// (invariant 2 — never merged state). The materialized upstream (the completed prior waves' real outputs)
/// lives on the plan branch in the integration worktree, injected via a second <c>--add-dir</c> so the
/// sub-process can READ it while WRITING the wave into the plan folder.</para>
///
/// <para>This actor only INVOKES; the deterministic gate on its output is the harness re-running
/// <c>guardrails validate</c> in-process (invariant 1) — owned by the caller, never the judge that produced
/// the wave. Its own prompt spend is charged to the shared overhead sink (<c>overheadCostUsd</c>, SSOT
/// §7/§9), folded into <c>maxCostUsd</c> and the reported total, exactly like the diagnose / AI-merge /
/// triage spend (#314).</para>
/// </summary>
public sealed class WaveBreakdownInvoker
{
    /// <summary>The full authoring tool set (doc 11 §9.2) — distinct from the read-only <c>overwatch</c> diagnose profile.</summary>
    private static readonly IReadOnlyList<string> AuthoringTools = ["Read", "Write", "Edit", "Bash", "Grep", "Glob"];

    /// <summary>A generous turn ceiling — breakdown is a full authoring session, not a one-shot diagnose.</summary>
    private const int BreakdownMaxTurns = 120;

    /// <summary>A generous timeout — a whole-wave breakdown + self-validate is a long session.</summary>
    private static readonly TimeSpan BreakdownTimeout = TimeSpan.FromMinutes(30);

    private readonly IPromptRunner _runner;

    /// <param name="runner">The runner for the reserved <c>breakdown</c> profile (resolved with fallback to the default/sole runner).</param>
    public WaveBreakdownInvoker(IPromptRunner runner) => _runner = runner;

    /// <summary>
    /// Invoke <c>plan-breakdown</c> for <paramref name="wave"/> against its <c>brief.md</c> and the
    /// materialized upstream in <paramref name="integrationWorktreePath"/>, teeing the transcript under
    /// <paramref name="breakdownLogDir"/> (SSOT §8 <c>logs/&lt;runId&gt;/&lt;wave-dir&gt;/breakdown/</c>).
    /// Charges the invocation's prompt spend to the shared overhead sink BEFORE returning (regardless of
    /// outcome — the spend is real either way). Never throws: a runner fault degrades to a faulted outcome
    /// so the caller's deterministic validate gate still decides the halt kind.
    /// </summary>
    internal async Task<WaveBreakdownOutcome> InvokeAsync(
        WaveNode wave,
        PlanDefinition plan,
        string integrationWorktreePath,
        string breakdownLogDir,
        ISchedulerJournal journal,
        CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(breakdownLogDir);
            string composedPromptPath = Path.Combine(breakdownLogDir, "composed-prompt.md");
            string streamLogPath = Path.Combine(breakdownLogDir, "claude-stream.jsonl");
            string transcriptLogPath = Path.Combine(breakdownLogDir, "transcript.md");

            string prompt = ComposePrompt(wave, plan, integrationWorktreePath);
            try { File.WriteAllText(composedPromptPath, prompt); } catch { /* best-effort log tee */ }

            var invocation = new PromptInvocation
            {
                ComposedPrompt = prompt,
                // Author into the plan folder (the wave's tasks/ live here); the integration worktree is
                // granted read access via the second --add-dir below (materialized upstream).
                WorkingDirectory = plan.PlanDirectory,
                PlanDirectory = plan.PlanDirectory,
                Environment = new Dictionary<string, string>(StringComparer.Ordinal),
                Settings = new PromptRunnerSettings
                {
                    PermissionMode = "acceptEdits",
                    AllowedTools = AuthoringTools,
                    MaxTurns = BreakdownMaxTurns,
                    // Doc 11 §9.3 step 4: grant the sub-process access to the materialized upstream on the
                    // plan branch (a SECOND --add-dir on top of the plan dir) so the skill reads real prior-wave
                    // outputs from the integration worktree, NOT the read-only user checkout.
                    ExtraArgs = ["--add-dir", integrationWorktreePath]
                },
                Timeout = BreakdownTimeout,
                StreamLogPath = streamLogPath,
                TranscriptLogPath = transcriptLogPath
            };

            PromptResult result = await _runner.RunAsync(invocation, ct).ConfigureAwait(false);

            // Charge the spend to the shared overhead sink BEFORE any gate — the spend is real whether or not
            // the output validates, and it must both count toward maxCostUsd and appear in the reported total
            // (the exact discipline the diagnose / AI-merge / triage spend uses, SSOT §9/#314). Null = no-op.
            journal.AddOverheadCost(result.CostUsd);

            return new WaveBreakdownOutcome
            {
                ProcessCompleted = result.Completed && !result.IsError,
                Summary = result.Summary
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A runner fault (e.g. the runner binary off PATH) must never crash the run — the caller's
            // deterministic validate gate then reports BreakdownFailed with this error carried in the detail.
            return new WaveBreakdownOutcome { ProcessCompleted = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Compose the breakdown-invocation prompt (doc 11 §9.3): inline the <c>plan-breakdown</c> SKILL.md when
    /// it can be located beside the tool (best-effort), name the wave's <c>brief.md</c> as the target, and
    /// point at the materialized upstream in the integration worktree. The exact text is not a wire contract
    /// (a stub runner in tests ignores it); the load-bearing parts are the target, the integration path, and
    /// the "write into &lt;wave&gt;/tasks/, self-validate, present as a draft" instruction.
    /// </summary>
    private static string ComposePrompt(WaveNode wave, PlanDefinition plan, string integrationWorktreePath)
    {
        var sb = new StringBuilder();
        sb.Append("# Between-wave breakdown invocation (Guardrails harness, #360)\n\n");
        sb.Append($"Break down the JIT wave `{wave.Dir}` of the plan at `{plan.PlanDirectory}` into its ")
          .Append("`tasks/` folder — a dependency DAG of tasks, each with an action and deterministic-first ")
          .Append("guardrails — using the `plan-breakdown` skill.\n\n");
        sb.Append("## Target (the reviewed `.md` plan for THIS wave)\n\n");
        sb.Append($"- Wave brief: `{wave.Dir}/{WaveNode.BriefFileName}` (the input plan for this wave)\n");
        sb.Append($"- Write the authored tasks into `{wave.Dir}/tasks/` (currently an empty JIT stub)\n\n");
        sb.Append("## Materialized upstream (read-only)\n\n");
        sb.Append($"The completed prior wave(s)' real outputs are materialized on the plan branch at ")
          .Append($"`{integrationWorktreePath}` (granted via `--add-dir`). Read the actual file paths and ")
          .Append("signatures there — NOT the user's checkout — when authoring this wave's tasks/guardrails.\n\n");
        sb.Append("## Contract\n\n");
        sb.Append("- Lean deterministic (tests / regex / exit codes) over prompt-judges.\n");
        sb.Append("- Every task needs >= 1 guardrail; insert guardrail-enabling tasks the brief omits.\n");
        sb.Append("- `dependsOn` is intra-wave only; state fragments are keyed by the wave-qualified id ")
          .Append($"`{wave.Dir}/<taskFolder>`.\n");
        sb.Append("- Self-validate with `guardrails validate` before finishing; the output is a DRAFT a human reviews.\n\n");

        string? skill = TryLoadPlanBreakdownSkill();
        if (skill is not null)
        {
            sb.Append("## plan-breakdown skill (inlined)\n\n");
            sb.Append(skill);
            sb.Append('\n');
        }
        else
        {
            sb.Append("## plan-breakdown skill\n\n");
            sb.Append("Apply the installed `plan-breakdown` skill's full procedure (Step 9, waved breakdown).\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Best-effort locate + read the bundled <c>plan-breakdown</c> SKILL.md so it can be inlined. The tool
    /// bundles skills beside the entry assembly under <c>skills/</c> (dev-knowledge: the packer sweeps
    /// copy-to-output content into the nupkg next to <see cref="AppContext.BaseDirectory"/>); a test host has
    /// no such folder, so this returns null and the composer falls back to naming the installed skill.
    /// </summary>
    private static string? TryLoadPlanBreakdownSkill()
    {
        try
        {
            string candidate = Path.Combine(
                AppContext.BaseDirectory, "skills", "plan-breakdown", "SKILL.md");
            return File.Exists(candidate) ? File.ReadAllText(candidate) : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>The outcome of one <see cref="WaveBreakdownInvoker.InvokeAsync"/> — advisory only; the deterministic
/// <c>guardrails validate</c> gate (run by the caller) is the actual verdict on the authored wave.</summary>
public sealed record WaveBreakdownOutcome
{
    /// <summary>True when the runner produced a terminal result without error (the authoring session itself completed).</summary>
    public required bool ProcessCompleted { get; init; }

    /// <summary>A short human-readable summary of the runner outcome, for the breakdown log / halt detail.</summary>
    public string? Summary { get; init; }

    /// <summary>Set only when the invocation FAULTED (the runner threw) — carried into a <c>BreakdownFailed</c> halt's detail.</summary>
    public string? Error { get; init; }
}
